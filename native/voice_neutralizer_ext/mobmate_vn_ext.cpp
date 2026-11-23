#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

extern "C" {
#include "rnnoise.h"
}

#include "world/dio.h"
#include "world/cheaptrick.h"
#include "world/d4c.h"
#include "world/synthesis.h"

#include "api/scoped_refptr.h"
#include "webrtc/api/audio/audio_processing.h"
#include "webrtc/modules/audio_processing/include/audio_processing.h"

namespace {

static inline float clampf(float x, float lo, float hi) {
    return std::max(lo, std::min(hi, x));
}

static void resample_linear_i16_to_float(const int16_t* in, int in_count, std::vector<float>& out, int out_count) {
    out.assign(std::max(0, out_count), 0.0f);
    if (!in || in_count <= 0 || out_count <= 0) return;
    if (in_count == 1 || out_count == 1) {
        std::fill(out.begin(), out.end(), static_cast<float>(in[0]));
        return;
    }
    const double scale = static_cast<double>(in_count - 1) / static_cast<double>(out_count - 1);
    for (int i = 0; i < out_count; ++i) {
        double src = static_cast<double>(i) * scale;
        int i0 = static_cast<int>(src);
        int i1 = std::min(i0 + 1, in_count - 1);
        float t = static_cast<float>(src - static_cast<double>(i0));
        out[i] = static_cast<float>(in[i0]) * (1.0f - t) + static_cast<float>(in[i1]) * t;
    }
}

static float sample_linear_float(const std::vector<float>& in, double pos) {
    if (in.empty()) return 0.0f;
    if (in.size() == 1) return in[0];
    pos = std::max(0.0, std::min(pos, static_cast<double>(in.size() - 1)));
    int i0 = static_cast<int>(pos);
    int i1 = std::min(i0 + 1, static_cast<int>(in.size() - 1));
    float t = static_cast<float>(pos - static_cast<double>(i0));
    return in[i0] * (1.0f - t) + in[i1] * t;
}

struct VnExtState {
    int sample_rate = 48000;
    int channels = 1;
    float rnnoise_wet = 0.0f;
    float webrtc_wet = 0.0f;
    float world_wet = 0.0f;
    DenoiseState* rnnoise = nullptr;
    rtc::scoped_refptr<webrtc::AudioProcessing> apm;
    std::vector<int16_t> rnnoise_pending;
    std::vector<int16_t> world_pending;
    std::string status = "created";
};

static void process_rnnoise(VnExtState* st, int16_t* samples, int count) {
    if (!st || !st->rnnoise || st->channels != 1 || st->rnnoise_wet <= 0.001f) return;
    if (st->sample_rate != 16000 && st->sample_rate != 24000 && st->sample_rate != 48000) return;
    constexpr int kFrame = 480;
    const int up_count = st->sample_rate == 48000 ? count : (count * 48000) / st->sample_rate;
    if (up_count < kFrame) return;

    std::vector<float> up;
    if (st->sample_rate == 48000) {
        up.resize(count);
        for (int i = 0; i < count; ++i) up[i] = static_cast<float>(samples[i]);
    } else {
        resample_linear_i16_to_float(samples, count, up, up_count);
    }

    float in[kFrame];
    float out[kFrame];
    for (int pos = 0; pos + kFrame <= static_cast<int>(up.size()); pos += kFrame) {
        for (int i = 0; i < kFrame; ++i) in[i] = up[pos + i];
        rnnoise_process_frame(st->rnnoise, out, in);
        for (int i = 0; i < kFrame; ++i) up[pos + i] = out[i];
    }

    if (st->sample_rate == 48000) {
        for (int i = 0; i < count; ++i) {
            float dry = static_cast<float>(samples[i]);
            float mixed = dry * (1.0f - st->rnnoise_wet) + up[i] * st->rnnoise_wet;
            samples[i] = static_cast<int16_t>(clampf(mixed, -32768.0f, 32767.0f));
        }
    } else {
        const double scale = static_cast<double>(std::max(1, up_count - 1)) / static_cast<double>(std::max(1, count - 1));
        for (int i = 0; i < count; ++i) {
            float dry = static_cast<float>(samples[i]);
            float wet = sample_linear_float(up, static_cast<double>(i) * scale);
            float mixed = dry * (1.0f - st->rnnoise_wet) + wet * st->rnnoise_wet;
            samples[i] = static_cast<int16_t>(clampf(mixed, -32768.0f, 32767.0f));
        }
    }
}

static void process_webrtc(VnExtState* st, int16_t* samples, int count) {
    if (!st || !st->apm || st->webrtc_wet <= 0.001f || st->channels != 1) return;
    int frame = st->sample_rate / 100;
    if (frame <= 0) return;
    webrtc::StreamConfig cfg(st->sample_rate, 1);
    std::vector<int16_t> dry(frame);
    std::vector<int16_t> proc(frame);
    for (int pos = 0; pos + frame <= count; pos += frame) {
        std::memcpy(dry.data(), samples + pos, frame * sizeof(int16_t));
        std::memcpy(proc.data(), samples + pos, frame * sizeof(int16_t));
        st->apm->ProcessStream(proc.data(), cfg, cfg, proc.data());
        for (int i = 0; i < frame; ++i) {
            float mixed = dry[i] * (1.0f - st->webrtc_wet) + proc[i] * st->webrtc_wet;
            samples[pos + i] = static_cast<int16_t>(clampf(mixed, -32768.0f, 32767.0f));
        }
    }
}

static void process_world(VnExtState* st, int16_t* samples, int count) {
    if (!st || st->world_wet <= 0.001f || st->channels != 1 || count < st->sample_rate / 5) return;
    const int fs = st->sample_rate;
    const double frame_period = 5.0;
    std::vector<double> x(count);
    for (int i = 0; i < count; ++i) x[i] = static_cast<double>(samples[i]) / 32768.0;

    DioOption dio_opt;
    InitializeDioOption(&dio_opt);
    dio_opt.frame_period = frame_period;
    int f0_length = GetSamplesForDIO(fs, count, frame_period);
    if (f0_length <= 0) return;
    std::vector<double> temporal(f0_length), f0(f0_length);
    Dio(x.data(), count, fs, &dio_opt, temporal.data(), f0.data());

    double sum = 0.0;
    int voiced = 0;
    for (double v : f0) if (v > 40.0) { sum += v; ++voiced; }
    double median_like = voiced > 0 ? sum / voiced : 0.0;
    for (double& v : f0) if (v > 40.0 && median_like > 40.0) v = v * 0.75 + median_like * 0.25;

    CheapTrickOption ct_opt;
    InitializeCheapTrickOption(fs, &ct_opt);
    int fft_size = GetFFTSizeForCheapTrick(fs, &ct_opt);
    std::vector<std::vector<double>> sp_store(f0_length, std::vector<double>(fft_size / 2 + 1));
    std::vector<double*> sp(f0_length);
    for (int i = 0; i < f0_length; ++i) sp[i] = sp_store[i].data();
    CheapTrick(x.data(), count, fs, temporal.data(), f0.data(), f0_length, &ct_opt, sp.data());

    const int bins = fft_size / 2 + 1;
    std::vector<double> frame_energy(f0_length, 0.0);
    double mean_energy = 0.0;
    int voiced_energy = 0;
    for (int i = 0; i < f0_length; ++i) {
        double e = 0.0;
        for (int j = 0; j < bins; ++j) e += std::max(0.0, sp[i][j]);
        frame_energy[i] = e / static_cast<double>(bins);
        if (f0[i] > 40.0 && frame_energy[i] > 1.0e-10) {
            mean_energy += frame_energy[i];
            ++voiced_energy;
        }
    }
    if (voiced_energy > 0) mean_energy /= static_cast<double>(voiced_energy);

    std::vector<double> smooth_bins(bins, 0.0);
    std::vector<double> warped_bins(bins, 0.0);
    const double warp_amount = static_cast<double>(clampf(st->world_wet, 0.0f, 3.0f));
    const double formant_warp = 1.0 + 0.18 * warp_amount;
    const double formant_blend = std::min(0.95, 0.20 + 0.35 * warp_amount);
    for (int i = 0; i < f0_length; ++i) {
        if (mean_energy > 1.0e-10 && frame_energy[i] > 1.0e-10 && f0[i] > 40.0) {
            double energy_gain = std::pow(mean_energy / frame_energy[i], 0.20);
            energy_gain = std::max(0.75, std::min(1.35, energy_gain));
            for (int j = 0; j < bins; ++j) sp[i][j] *= energy_gain;
        }

        for (int j = 0; j < bins; ++j) {
            double acc = 0.0;
            double wsum = 0.0;
            for (int k = -2; k <= 2; ++k) {
                int idx = std::max(0, std::min(bins - 1, j + k));
                double w = (k == 0) ? 0.40 : (std::abs(k) == 1 ? 0.24 : 0.06);
                acc += std::log(std::max(sp[i][idx], 1.0e-12)) * w;
                wsum += w;
            }
            smooth_bins[j] = std::exp(acc / std::max(wsum, 1.0e-9));
        }
        for (int j = 0; j < bins; ++j) sp[i][j] = sp[i][j] * 0.75 + smooth_bins[j] * 0.25;

        // Mild spectral-envelope frequency warp. This intentionally shifts the
        // formant structure a little, reducing speaker identity more than simple
        // smoothing while keeping WORLD as a wet/dry layer rather than full VC.
        for (int j = 0; j < bins; ++j) {
            double src = static_cast<double>(j) / formant_warp;
            int j0 = static_cast<int>(std::floor(src));
            int j1 = std::min(j0 + 1, bins - 1);
            double t = src - static_cast<double>(j0);
            j0 = std::max(0, std::min(j0, bins - 1));
            warped_bins[j] = sp[i][j0] * (1.0 - t) + sp[i][j1] * t;
        }
        for (int j = 0; j < bins; ++j) sp[i][j] = sp[i][j] * (1.0 - formant_blend) + warped_bins[j] * formant_blend;
    }

    D4COption d4c_opt;
    InitializeD4COption(&d4c_opt);
    std::vector<std::vector<double>> ap_store(f0_length, std::vector<double>(fft_size / 2 + 1));
    std::vector<double*> ap(f0_length);
    for (int i = 0; i < f0_length; ++i) ap[i] = ap_store[i].data();
    D4C(x.data(), count, fs, temporal.data(), f0.data(), f0_length, fft_size, &d4c_opt, ap.data());

    std::vector<double> y(count, 0.0);
    Synthesis(f0.data(), f0_length, sp.data(), ap.data(), fft_size, frame_period, fs, count, y.data());
    for (int i = 0; i < count; ++i) {
        float dry = static_cast<float>(samples[i]);
        float wet = static_cast<float>(clampf(static_cast<float>(y[i] * 32768.0), -32768.0f, 32767.0f));
        samples[i] = static_cast<int16_t>(clampf(dry * (1.0f - st->world_wet) + wet * st->world_wet, -32768.0f, 32767.0f));
    }
}

} // namespace

extern "C" {

#ifdef _WIN32
#define MMVN_EXPORT __declspec(dllexport)
#else
#define MMVN_EXPORT __attribute__((visibility("default")))
#endif

MMVN_EXPORT void* mmvn_create(int sample_rate, int channels, float rnnoise_wet, float webrtc_wet, float world_wet) {
    auto* st = new VnExtState();
    st->sample_rate = sample_rate;
    st->channels = channels;
    st->rnnoise_wet = clampf(rnnoise_wet, 0.0f, 1.0f);
    st->webrtc_wet = clampf(webrtc_wet, 0.0f, 1.0f);
    st->world_wet = clampf(world_wet, 0.0f, 3.0f);
    if ((sample_rate == 16000 || sample_rate == 24000 || sample_rate == 48000) && channels == 1 && st->rnnoise_wet > 0.001f) st->rnnoise = rnnoise_create(nullptr);
    if (channels == 1 && st->webrtc_wet > 0.001f) {
        st->apm = webrtc::AudioProcessingBuilder().Create();
        webrtc::AudioProcessing::Config cfg;
        cfg.high_pass_filter.enabled = true;
        cfg.noise_suppression.enabled = true;
        cfg.noise_suppression.level = webrtc::AudioProcessing::Config::NoiseSuppression::kModerate;
        cfg.gain_controller2.enabled = false;
        st->apm->ApplyConfig(cfg);
    }
    st->status = "rnnoise=" + std::string(st->rnnoise ? "on" : "off") + ",webrtc=" + std::string(st->apm ? "on" : "off") + ",world=" + (st->world_wet > 0.001f ? "on" : "off");
    return st;
}

MMVN_EXPORT void mmvn_destroy(void* handle) {
    auto* st = static_cast<VnExtState*>(handle);
    if (!st) return;
    if (st->rnnoise) rnnoise_destroy(st->rnnoise);
    delete st;
}

MMVN_EXPORT int mmvn_process_i16(void* handle, int16_t* samples, int count) {
    auto* st = static_cast<VnExtState*>(handle);
    if (!st || !samples || count <= 0) return -1;
    process_webrtc(st, samples, count);
    process_rnnoise(st, samples, count);
    process_world(st, samples, count);
    return count;
}

MMVN_EXPORT const char* mmvn_status(void* handle) {
    auto* st = static_cast<VnExtState*>(handle);
    return st ? st->status.c_str() : "null";
}

} // extern "C"
