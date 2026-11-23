#define _USE_MATH_DEFINES
#include "mobmate_native_core/audio_prefilter.h"
#include <cmath>
#include <cstring>
#include <algorithm>
#include <vector>

namespace mobmate {

/* ------------------------------------------------------------------ */
/*  STRONG profile constants (matching Java AudioPrefilter)            */
/* ------------------------------------------------------------------ */

static constexpr int STRONG_SPECTRAL_FRAME_SIZE = 128;
static constexpr int STRONG_SPECTRAL_HOP_SIZE = 64;
static constexpr double STRONG_SPECTRAL_SUBTRACT = 0.92;
static constexpr double STRONG_SPECTRAL_FLOOR = 0.24;
static constexpr double STRONG_SPECTRAL_NOISE_DOWN = 0.14;
static constexpr double STRONG_SPECTRAL_NOISE_UP = 0.028;
static constexpr double STRONG_DEREVERB_FACTOR = 0.045;
static constexpr double STRONG_DEREVERB_DECAY = 0.72;

// STRONG profile filter parameters
static constexpr double STRONG_HP_CUTOFF_HZ = 75.0;
static constexpr double STRONG_LP_CUTOFF_HZ = 6000.0;
static constexpr double STRONG_PRE_EMPHASIS_ALPHA = 0.42;
static constexpr double STRONG_LIMIT_DRIVE = 0.86;
static constexpr double STRONG_STREAMING_AGC_MAX_GAIN = 3.4;

/* ------------------------------------------------------------------ */
/*  Helper: read/write PCM16 LE                                        */
/* ------------------------------------------------------------------ */

static inline int16_t readPcm16le(const uint8_t* buf, int offset) {
    int lo = buf[offset] & 0xFF;
    int hi = buf[offset + 1];
    return static_cast<int16_t>(lo | (hi << 8));
}

static inline void writePcm16le(uint8_t* buf, int offset, int16_t value) {
    buf[offset] = static_cast<uint8_t>(value & 0xFF);
    buf[offset + 1] = static_cast<uint8_t>((value >> 8) & 0xFF);
}

static inline double dbfsToLinear(double dbfs) {
    return std::pow(10.0, dbfs / 20.0);
}

/* ------------------------------------------------------------------ */
/*  Hann window (pre-computed)                                         */
/* ------------------------------------------------------------------ */

static std::vector<double> buildHannWindow(int n) {
    std::vector<double> window(n);
    for (int i = 0; i < n; i++) {
        window[i] = 0.5 * (1.0 - std::cos(2.0 * M_PI * i / (n - 1)));
    }
    return window;
}

static const std::vector<double> STRONG_WINDOW = []() {
    return buildHannWindow(STRONG_SPECTRAL_FRAME_SIZE);
}();

/* ------------------------------------------------------------------ */
/*  Pre-computed trig tables (matching Java buildTrigTable)            */
/* ------------------------------------------------------------------ */

static inline void buildTrigTables(int n,
                                    std::vector<std::vector<double>>& cosTable,
                                    std::vector<std::vector<double>>& sinTable) {
    cosTable.resize(n, std::vector<double>(n));
    sinTable.resize(n, std::vector<double>(n));
    for (int k = 0; k < n; k++) {
        for (int t = 0; t < n; t++) {
            double angle = 2.0 * M_PI * k * t / n;
            cosTable[k][t] = std::cos(angle);
            sinTable[k][t] = std::sin(angle);
        }
    }
}

static std::vector<std::vector<double>> STRONG_COS;
static std::vector<std::vector<double>> STRONG_SIN;

static void ensureTrigTables() {
    if (STRONG_COS.empty()) {
        buildTrigTables(STRONG_SPECTRAL_FRAME_SIZE, STRONG_COS, STRONG_SIN);
    }
}

/* ------------------------------------------------------------------ */
/*  DFT / IDFT (O(N^2), N=128, using pre-computed trig tables)        */
/* ------------------------------------------------------------------ */

static void dft(const std::vector<double>& input,
                std::vector<double>& re,
                std::vector<double>& im) {
    int n = STRONG_SPECTRAL_FRAME_SIZE;
    ensureTrigTables();
    for (int k = 0; k < n; k++) {
        double sumRe = 0.0;
        double sumIm = 0.0;
        const auto& cosRow = STRONG_COS[k];
        const auto& sinRow = STRONG_SIN[k];
        for (int t = 0; t < n; t++) {
            double sample = input[t];
            sumRe += sample * cosRow[t];
            sumIm -= sample * sinRow[t];
        }
        re[k] = sumRe;
        im[k] = sumIm;
    }
}

static void idft(const std::vector<double>& re,
                 const std::vector<double>& im,
                 std::vector<double>& out) {
    int n = STRONG_SPECTRAL_FRAME_SIZE;
    ensureTrigTables();
    for (int t = 0; t < n; t++) {
        double sum = 0.0;
        for (int k = 0; k < n; k++) {
            sum += (re[k] * STRONG_COS[k][t]) - (im[k] * STRONG_SIN[k][t]);
        }
        out[t] = sum / n;
    }
}

/* ------------------------------------------------------------------ */
/*  Mirror spectrum (conjugate symmetry for real output)               */
/* ------------------------------------------------------------------ */

static void mirrorSpectrum(std::vector<double>& re,
                           std::vector<double>& im,
                           int bins) {
    int n = STRONG_SPECTRAL_FRAME_SIZE;
    for (int k = bins; k < n; k++) {
        re[k] = re[n - k];
        im[k] = -im[n - k];
    }
}

/* ------------------------------------------------------------------ */
/*  Spectral enhancement (subtraction + dereverb)                      */
/* ------------------------------------------------------------------ */

static std::vector<double> applyStrongSpectralEnhance(
    const std::vector<double>& input,
    int sampleRateHz,
    AudioPrefilterState* state) {
    int n = STRONG_SPECTRAL_FRAME_SIZE;
    int hop = STRONG_SPECTRAL_HOP_SIZE;
    int bins = (n / 2) + 1;
    int len = static_cast<int>(input.size());

    if (len < 16) return input;

    // Initialize spectral state if needed
    if (state->spectralSampleRateHz != sampleRateHz ||
        state->spectralNoiseFloor.size() != static_cast<size_t>(bins)) {
        state->spectralSampleRateHz = sampleRateHz;
        state->spectralNoiseFloor.assign(bins, 0.0);
        state->spectralLateReverb.assign(bins, 0.0);
    }

    std::vector<double> out(len, 0.0);
    std::vector<double> norm(len, 0.0);
    std::vector<double> frame(n, 0.0);
    std::vector<double> re(n, 0.0);
    std::vector<double> im(n, 0.0);
    std::vector<double> time(n, 0.0);

    // Count frames to determine warmup period
    int totalFrames = (len + hop - 1) / hop;
    constexpr int WARMUP_FRAMES = 3; // Skip spectral subtraction for first 3 frames

    for (int start = 0; start < len; start += hop) {
        int frameIndex = start / hop;
        bool isWarmup = (frameIndex < WARMUP_FRAMES);

        std::fill(frame.begin(), frame.end(), 0.0);
        std::fill(re.begin(), re.end(), 0.0);
        std::fill(im.begin(), im.end(), 0.0);

        // Windowed frame
        for (int i = 0; i < n; i++) {
            int src = start + i;
            double sample = (src < len) ? input[src] : 0.0;
            frame[i] = sample * STRONG_WINDOW[i];
        }

        // Forward DFT
        dft(frame, re, im);

        // Spectral subtraction + dereverb per bin
        for (int k = 0; k < bins; k++) {
            double mag = std::hypot(re[k], im[k]);
            double prevNoise = state->spectralNoiseFloor[k];

            // Noise floor tracking
            if (prevNoise <= 0.0) {
                state->spectralNoiseFloor[k] = mag;
            } else if (mag < prevNoise) {
                state->spectralNoiseFloor[k] =
                    (prevNoise * (1.0 - STRONG_SPECTRAL_NOISE_DOWN)) +
                    (mag * STRONG_SPECTRAL_NOISE_DOWN);
            } else {
                state->spectralNoiseFloor[k] =
                    (prevNoise * (1.0 - STRONG_SPECTRAL_NOISE_UP)) +
                    (mag * STRONG_SPECTRAL_NOISE_UP);
            }

            // During warmup, only learn noise floor, skip spectral subtraction
            if (isWarmup) {
                // Still track late reverb during warmup
                state->spectralLateReverb[k] =
                    (state->spectralLateReverb[k] * STRONG_DEREVERB_DECAY) +
                    (mag * (1.0 - STRONG_DEREVERB_DECAY));
                continue;
            }

            // Spectral subtraction + dereverb
            double suppressed = mag
                - (STRONG_SPECTRAL_SUBTRACT * state->spectralNoiseFloor[k])
                - (STRONG_DEREVERB_FACTOR * state->spectralLateReverb[k]);
            double cleanMag = std::max(suppressed, STRONG_SPECTRAL_FLOOR * mag);
            double scale = cleanMag / std::max(1e-9, mag);

            re[k] *= scale;
            im[k] *= scale;

            // Late reverb tracking
            state->spectralLateReverb[k] =
                (state->spectralLateReverb[k] * STRONG_DEREVERB_DECAY) +
                (cleanMag * (1.0 - STRONG_DEREVERB_DECAY));
        }

        // Mirror spectrum for real IDFT
        mirrorSpectrum(re, im, bins);

        // Inverse DFT
        idft(re, im, time);

        // Overlap-add with window
        for (int i = 0; i < n; i++) {
            int dst = start + i;
            if (dst >= len) break;
            double win = STRONG_WINDOW[i];
            out[dst] += time[i] * win;
            norm[dst] += win * win;
        }
    }

    // Normalize by overlap-add weights
    std::vector<double> filtered(len);
    for (int i = 0; i < len; i++) {
        filtered[i] = (norm[i] > 1e-9) ? (out[i] / norm[i]) : input[i];
    }
    return filtered;
}

/* ------------------------------------------------------------------ */
/*  Soft limit (tanh-based)                                            */
/* ------------------------------------------------------------------ */

static double softLimit(double sample, double drive) {
    double normalized = sample / static_cast<double>(INT16_MAX);
    double driven = normalized / std::max(0.1, drive);
    double limited = std::tanh(driven) * drive;
    return limited * static_cast<double>(INT16_MAX);
}

/* ------------------------------------------------------------------ */
/*  Streaming AGC (STRONG profile)                                     */
/* ------------------------------------------------------------------ */

static constexpr double STREAMING_AGC_TARGET_RMS_DBFS = -22.5;
static constexpr double STREAMING_AGC_MIN_RMS = 140.0;
static constexpr double STREAMING_AGC_GAIN_UP_ALPHA = 0.10;
static constexpr double STREAMING_AGC_GAIN_DOWN_ALPHA = 0.28;
static constexpr double STREAMING_AGC_IDLE_ALPHA = 0.04;
static constexpr double STREAMING_AGC_MIN_GAIN = 0.72;
static constexpr double TALK_STREAMING_AGC_MIN_GAIN = 0.90;
static constexpr double PEAK_CEILING_DBFS = -3.0;

static void applyStreamingAgcStrong(std::vector<double>& samples,
                                     AudioPrefilterState* state) {
    if (samples.empty()) return;

    double sumSq = 0.0;
    double peak = 0.0;
    for (double sample : samples) {
        double abs = std::abs(sample);
        if (abs > peak) peak = abs;
        sumSq += sample * sample;
    }
    double rms = std::sqrt(sumSq / samples.size());

    double maxGain = STRONG_STREAMING_AGC_MAX_GAIN;
    double minGain = TALK_STREAMING_AGC_MIN_GAIN; // STRONG is used in TALK mode
    double targetLinear = dbfsToLinear(STREAMING_AGC_TARGET_RMS_DBFS) * static_cast<double>(INT16_MAX);
    double targetGain = 1.0;

    if (rms >= STREAMING_AGC_MIN_RMS) {
        targetGain = targetLinear / std::max(1.0, rms);
        targetGain = std::max(minGain, std::min(maxGain, targetGain));
    }

    double currentGain = (state->agcGain > 0.0) ? state->agcGain : 1.0;
    double alpha;
    if (rms < STREAMING_AGC_MIN_RMS) {
        alpha = STREAMING_AGC_IDLE_ALPHA;
        targetGain = 1.0;
    } else {
        alpha = (targetGain > currentGain)
                ? STREAMING_AGC_GAIN_UP_ALPHA
                : STREAMING_AGC_GAIN_DOWN_ALPHA;
    }
    currentGain = currentGain + ((targetGain - currentGain) * alpha);

    double peakCeiling = dbfsToLinear(PEAK_CEILING_DBFS) * static_cast<double>(INT16_MAX);
    if (peak > 1.0) {
        currentGain = std::min(currentGain, peakCeiling / peak);
    }
    currentGain = std::max(minGain, std::min(maxGain, currentGain));
    state->agcGain = currentGain;

    for (double& s : samples) {
        s *= currentGain;
    }
}

/* ------------------------------------------------------------------ */
/*  Hum notch filters (shared with NORMAL profile)                     */
/* ------------------------------------------------------------------ */

static constexpr double HUM_NOTCH_Q = 18.0;
static constexpr int NOTCH_FREQS_HZ[] = {50, 60, 100, 120, 150, 180};
static constexpr int NOTCH_COUNT = 6;

static double applyHumNotches(double x, int sampleRateHz, AudioPrefilterState* state) {
    double y = x;
    for (int i = 0; i < NOTCH_COUNT; i++) {
        int hz = NOTCH_FREQS_HZ[i];
        if (hz >= sampleRateHz / 2) continue;

        double w0 = 2.0 * M_PI * hz / sampleRateHz;
        double alpha = std::sin(w0) / (2.0 * HUM_NOTCH_Q);
        double b0 = 1.0;
        double b1 = -2.0 * std::cos(w0);
        double b2 = 1.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * std::cos(w0);
        double a2 = 1.0 - alpha;

        double out = (b0 / a0) * y
                   + (b1 / a0) * state->notchX1[i]
                   + (b2 / a0) * state->notchX2[i]
                   - (a1 / a0) * state->notchY1[i]
                   - (a2 / a0) * state->notchY2[i];

        state->notchX2[i] = state->notchX1[i];
        state->notchX1[i] = y;
        state->notchY2[i] = state->notchY1[i];
        state->notchY1[i] = out;
        y = out;
    }
    return y;
}

/* ------------------------------------------------------------------ */
/*  Main STRONG pipeline                                               */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> prefilter_process_strong(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    AudioPrefilterState* state)
{
    if (!pcm16le || bytes <= 0 || !state) {
        return {};
    }
    if (bytes < 4) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    std::vector<uint8_t> out(static_cast<size_t>(bytes));
    std::vector<double> working(samples);

    // Initialize notch state if sample rate changed
    if (state->notchSampleRateHz != sampleRateHz) {
        state->notchSampleRateHz = sampleRateHz;
        std::memset(state->notchX1, 0, sizeof(state->notchX1));
        std::memset(state->notchX2, 0, sizeof(state->notchX2));
        std::memset(state->notchY1, 0, sizeof(state->notchY1));
        std::memset(state->notchY2, 0, sizeof(state->notchY2));
    }

    // Stage 1: Hum notch filtering
    for (int i = 0; i < samples; i++) {
        double x = static_cast<double>(readPcm16le(pcm16le, i * 2));
        working[i] = applyHumNotches(x, sampleRateHz, state);
    }

    // Stage 2: Spectral subtraction + dereverb (STRONG specific)
    working = applyStrongSpectralEnhance(working, sampleRateHz, state);

    // Stage 3-6: HP -> pre-emphasis -> LP -> AGC -> soft limit
    double dt = 1.0 / sampleRateHz;

    // High-pass RC (STRONG cutoff)
    double hpRc = 1.0 / (2.0 * M_PI * STRONG_HP_CUTOFF_HZ);
    double hpAlpha = hpRc / (hpRc + dt);

    // Low-pass RC (STRONG cutoff)
    double lpRc = 1.0 / (2.0 * M_PI * STRONG_LP_CUTOFF_HZ);
    double lpAlpha = dt / (lpRc + dt);

    double hpPrevX = state->hpPrevX;
    double hpPrevY = state->hpPrevY;
    double prePrevX = state->prePrevX;
    double lpPrevY = state->lpPrevY;

    std::vector<double> shaped(samples);
    for (int i = 0; i < samples; i++) {
        double x = working[i];

        // High-pass (DC blocker)
        double hp = hpAlpha * (hpPrevY + x - hpPrevX);
        hpPrevX = x;
        hpPrevY = hp;

        // Pre-emphasis (STRONG alpha)
        double voice = hp;
        if (STRONG_PRE_EMPHASIS_ALPHA > 0.0) {
            double preInput = voice;
            voice = preInput - (STRONG_PRE_EMPHASIS_ALPHA * prePrevX);
            prePrevX = preInput;
        }

        // Low-pass
        double y = lpPrevY + lpAlpha * (voice - lpPrevY);
        lpPrevY = y;

        shaped[i] = y;
    }

    // Stage 7: AGC (STRONG profile)
    applyStreamingAgcStrong(shaped, state);

    // Stage 8: Soft limit + write output
    for (int i = 0; i < samples; i++) {
        int16_t v = static_cast<int16_t>(std::round(softLimit(shaped[i], STRONG_LIMIT_DRIVE)));
        v = std::max(static_cast<int16_t>(INT16_MIN), std::min(static_cast<int16_t>(INT16_MAX), v));
        writePcm16le(out.data(), i * 2, v);
    }

    // Save state
    state->hpPrevX = hpPrevX;
    state->hpPrevY = hpPrevY;
    state->prePrevX = prePrevX;
    state->lpPrevY = lpPrevY;

    return out;
}

} // namespace mobmate
