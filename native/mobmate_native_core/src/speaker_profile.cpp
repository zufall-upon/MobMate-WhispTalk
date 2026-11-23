#include "mobmate_native_core/speaker_profile.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>

#ifndef M_PI
#define M_PI 3.14159265358979323846f
#endif

namespace {

static constexpr int MEL_BANDS = 26;
static constexpr int NFFT = 512;
static constexpr int NUM_MFCC = 12;
static constexpr int FRAME_SIZE = 512;
static constexpr int HOP_SIZE = 160;
static constexpr int HALF_NFFT = 256;
static constexpr float PRE_EMPHASIS = 0.97f;
static constexpr float MIN_FRAME_RMS = 10.0f;
static constexpr int MIN_VALID_FRAMES = 5;

struct MelFilterbank {
    std::array<std::array<float, HALF_NFFT>, MEL_BANDS> bank{};
    bool init = false;
    void initialize() {
        if (init) return;
        constexpr float fmin = 300.0f;
        constexpr float fmax = 8000.0f;
        constexpr float sr = 16000.0f;
        auto mel = [](float f) { return 1127.0f * std::log(1.0f + f / 700.0f); };
        auto inv_mel = [](float m) { return 700.0f * (std::exp(m / 1127.0f) - 1.0f); };
        float m_low = mel(fmin);
        float m_high = mel(fmax);
        std::vector<float> m_pts(MEL_BANDS + 2);
        for (int i = 0; i < MEL_BANDS + 2; i++) {
            m_pts[i] = m_low + (m_high - m_low) * i / (MEL_BANDS + 1);
        }
        std::vector<float> f_pts(MEL_BANDS + 2);
        for (int i = 0; i < MEL_BANDS + 2; i++) {
            f_pts[i] = inv_mel(m_pts[i]);
        }
        std::vector<int> idx(MEL_BANDS + 2);
        for (int i = 0; i < MEL_BANDS + 2; i++) {
            idx[i] = static_cast<int>(std::round(f_pts[i] * HALF_NFFT / sr));
        }
        for (int m = 0; m < MEL_BANDS; m++) {
            for (int k = 0; k < HALF_NFFT; k++) {
                if (k >= idx[m] && k <= idx[m + 1]) {
                    bank[m][k] = static_cast<float>(k - idx[m]) / static_cast<float>(idx[m + 1] - idx[m]);
                } else if (k > idx[m + 1] && k < idx[m + 2]) {
                    bank[m][k] = static_cast<float>(idx[m + 2] - k) / static_cast<float>(idx[m + 2] - idx[m + 1]);
                } else {
                    bank[m][k] = 0.0f;
                }
            }
        }
        init = true;
    }
};

static MelFilterbank g_mel_filterbank;

void fft_512(std::array<float, NFFT>& re, std::array<float, NFFT>& im) {
    int n = NFFT;
    int j = 0;
    for (int i = 0; i < n - 1; i++) {
        int k = n >> 1;
        while (j >= k) {
            j -= k;
            k >>= 1;
        }
        j += k;
        if (i < j) {
            std::swap(re[i], re[j]);
            std::swap(im[i], im[j]);
        }
    }
    for (int len = 2; len <= n; len <<= 1) {
        float ang = -static_cast<float>(M_PI) / len;
        float wr = std::cos(ang);
        float wi = std::sin(ang);
        for (int i = 0; i < n; i += len) {
            float ar = 1.0f;
            float ai = 0.0f;
            for (int k = 0; k < len / 2; k++) {
                float tr = ar * re[i + k + len / 2] - ai * im[i + k + len / 2];
                float ti = ar * im[i + k + len / 2] + ai * re[i + k + len / 2];
                re[i + k + len / 2] = re[i + k] - tr;
                im[i + k + len / 2] = im[i + k] - ti;
                re[i + k] += tr;
                im[i + k] += ti;
                float nr = ar * wr - ai * wi;
                float ni = ar * wi + ai * wr;
                ar = nr;
                ai = ni;
            }
        }
    }
}

void dct_ii(const std::array<float, MEL_BANDS>& x, std::array<float, NUM_MFCC>& c) {
    for (int k = 0; k < NUM_MFCC; k++) {
        float s = 0.0f;
        for (int n = 0; n < MEL_BANDS; n++) {
            s += x[n] * std::cos(static_cast<float>(M_PI) / MEL_BANDS * (n + 0.5f) * k);
        }
        c[k] = s * std::sqrt(2.0f / MEL_BANDS);
    }
}

void hamming_window(std::array<float, FRAME_SIZE>& win) {
    for (int i = 0; i < FRAME_SIZE; i++) {
        win[i] = 0.54f - 0.46f * std::cos(2.0f * static_cast<float>(M_PI) * i / (FRAME_SIZE - 1));
    }
}

std::vector<float> extract_mfcc(const int16_t* pcm, int n, int sr) {
    if (n < FRAME_SIZE) return {};
    (void)sr;

    std::vector<float> sig(n);
    for (int i = 0; i < n; i++) sig[i] = static_cast<float>(pcm[i]);

    for (int i = n - 1; i > 0; i--) {
        sig[i] -= PRE_EMPHASIS * sig[i - 1];
    }
    sig[0] *= (1.0f - PRE_EMPHASIS);

    std::array<float, FRAME_SIZE> win;
    hamming_window(win);

    int nframes = (n - FRAME_SIZE) / HOP_SIZE + 1;
    std::vector<std::array<float, NUM_MFCC>> mfcc_frames;
    mfcc_frames.reserve(nframes);

    g_mel_filterbank.initialize();

    for (int f = 0; f < nframes; f++) {
        int offset = f * HOP_SIZE;
        float rms = 0.0f;
        for (int i = 0; i < FRAME_SIZE; i++) {
            float v = sig[offset + i] * win[i];
            rms += v * v;
        }
        rms = std::sqrt(rms / FRAME_SIZE);
        if (rms < MIN_FRAME_RMS) continue;

        std::array<float, NFFT> re{};
        std::array<float, NFFT> im{};
        for (int i = 0; i < FRAME_SIZE; i++) {
            re[i] = sig[offset + i] * win[i];
        }

        fft_512(re, im);

        std::array<float, HALF_NFFT> spec{};
        for (int i = 0; i < HALF_NFFT; i++) {
            spec[i] = std::sqrt(re[i] * re[i] + im[i] * im[i]);
        }

        std::array<float, MEL_BANDS> mel{};
        for (int m = 0; m < MEL_BANDS; m++) {
            float s = 1e-10f;
            for (int k = 0; k < HALF_NFFT; k++) {
                s += spec[k] * spec[k] * g_mel_filterbank.bank[m][k];
            }
            mel[m] = std::log(std::max(s, 1e-10f));
        }

        std::array<float, NUM_MFCC> c{};
        dct_ii(mel, c);
        mfcc_frames.push_back(c);
    }

    if (static_cast<int>(mfcc_frames.size()) < MIN_VALID_FRAMES) return {};

    int nf = static_cast<int>(mfcc_frames.size());
    std::vector<std::array<float, NUM_MFCC * 2>> all_frames(nf);

    for (int f = 0; f < nf; f++) {
        for (int d = 0; d < NUM_MFCC; d++) {
            all_frames[f][d] = mfcc_frames[f][d];
            float num = 0.0f;
            for (int delta = -2; delta <= 2; delta++) {
                int idx = f + delta;
                if (idx >= 0 && idx < nf) {
                    num += delta * mfcc_frames[idx][d];
                }
            }
            all_frames[f][NUM_MFCC + d] = num / 10.0f;
        }
    }

    std::array<float, SPEAKER_FEAT_DIM> out{};
    for (int d = 0; d < NUM_MFCC * 2; d++) {
        float mean = 0.0f;
        for (int f = 0; f < nf; f++) mean += all_frames[f][d];
        mean /= nf;
        out[d] = mean;
    }
    for (int d = 0; d < NUM_MFCC * 2; d++) {
        float var = 0.0f;
        for (int f = 0; f < nf; f++) {
            float diff = all_frames[f][d] - out[d];
            var += diff * diff;
        }
        out[NUM_MFCC * 2 + d] = std::sqrt(var / nf);
    }

    return std::vector<float>(out.begin(), out.end());
}

float compute_rms(const int16_t* pcm, int n) {
    if (n == 0) return 0.0f;
    double s = 0.0;
    for (int i = 0; i < n; i++) s += static_cast<double>(pcm[i]) * pcm[i];
    return static_cast<float>(std::sqrt(s / n));
}

float l2_distance(const float* a, const float* b, int dim) {
    float s = 0.0f;
    for (int i = 0; i < dim; i++) {
        float d = a[i] - b[i];
        s += d * d;
    }
    return std::sqrt(s);
}

void update_ema(std::array<float, SPEAKER_FEAT_DIM>& ema, const std::vector<float>& feat, int n) {
    float alpha = 0.15f / (1.0f + n * 0.05f);
    if (alpha < 0.02f) alpha = 0.02f;
    for (int i = 0; i < SPEAKER_FEAT_DIM; i++) {
        ema[i] = (1.0f - alpha) * ema[i] + alpha * feat[i];
    }
}

float gaussian_score(float d, float sigma) {
    if (sigma < 0.01f) sigma = 0.01f;
    return std::exp(-d * d / (2.0f * sigma * sigma));
}

} // anonymous namespace

std::vector<float> speaker_extract_features(const int16_t* pcm, int n, int sr) {
    return extract_mfcc(pcm, n, sr);
}

bool speaker_enroll(SpeakerProfile& p, const int16_t* pcm, int n, int sr) {
    if (!pcm || n < FRAME_SIZE) return false;
    std::vector<float> feat = extract_mfcc(pcm, n, sr);
    if (feat.empty()) return false;
    float rms = compute_rms(pcm, n);
    if (rms < 50.0f) return false;

    SpeakerSample s;
    for (int i = 0; i < SPEAKER_FEAT_DIM; i++) s.feat[i] = feat[i];
    s.rms_energy = rms;

    if (static_cast<int>(p.samples.size()) >= SPEAKER_MAX_CENTROIDS) {
        // FI/FO: only replace lowest-energy sample if new sample has higher energy
        float worst_rms = 1e9f;
        int worst_idx = 0;
        for (int i = 0; i < static_cast<int>(p.samples.size()); i++) {
            if (p.samples[i].rms_energy < worst_rms) {
                worst_rms = p.samples[i].rms_energy;
                worst_idx = i;
            }
        }
        if (rms > worst_rms) {
            p.samples[worst_idx] = s;
        }
        // else: new sample has lower energy than worst → discard
    } else {
        p.samples.push_back(s);
    }

    p.total_enrolled++;
    update_ema(p.ema_centroid, feat, p.total_enrolled);

    if (p.total_enrolled >= 3) {
        float spread = 0.0f;
        for (int i = 0; i < static_cast<int>(p.samples.size()); i++) {
            float d = l2_distance(p.samples[i].feat.data(), p.ema_centroid.data(), SPEAKER_FEAT_DIM);
            spread += d;
        }
        spread /= static_cast<float>(p.samples.size());
        p.ema_spread = spread;
        p.ema_ready = true;
    }

    return true;
}

float speaker_match(SpeakerProfile& p, const int16_t* pcm, int n, int sr, bool* pass) {
    if (static_cast<int>(p.samples.size()) < SPEAKER_MIN_SAMPLES) {
        *pass = true;
        p.consecutive_reject = 0;
        return 1.0f;
    }

    std::vector<float> feat = extract_mfcc(pcm, n, sr);
    if (feat.empty()) {
        // 無音 or too short → pass (spec: silence passes)
        *pass = true;
        return 1.0f;
    }

    float ema_dist = 1e9f;
    float max_sim = 0.0f;
    float avg_sim = 0.0f;
    int n_samples = static_cast<int>(p.samples.size());

    if (p.ema_ready) {
        ema_dist = l2_distance(feat.data(), p.ema_centroid.data(), SPEAKER_FEAT_DIM);
    }

    for (int i = 0; i < n_samples; i++) {
        float d = l2_distance(feat.data(), p.samples[i].feat.data(), SPEAKER_FEAT_DIM);
        float sc = gaussian_score(d, std::max(p.ema_spread, 0.5f));
        if (sc > max_sim) max_sim = sc;
        avg_sim += sc;
    }
    avg_sim /= n_samples;

    float score = 0.4f * gaussian_score(ema_dist, std::max(p.ema_spread, 0.5f));
    score += 0.4f * max_sim;
    score += 0.2f * avg_sim;

    if (score >= 0.55f) {
        *pass = true;
        p.consecutive_reject = 0;
        return score;
    }

    p.consecutive_reject++;
    if (p.consecutive_reject >= 15 && score >= 0.35f) {
        *pass = true;
        return score;
    }

    *pass = false;
    return score;
}

static void fmt_json_float(std::ostream& os, float v) {
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%.6f", v);
    os << buf;
}

bool speaker_save(const SpeakerProfile& p, const std::string& path) {
    std::ofstream f(path);
    if (!f.is_open()) return false;

    f << "{" << std::endl;
    f << "  " << "\"version\": 1," << std::endl;
    f << "  " << "\"total_enrolled\": " << p.total_enrolled << "," << std::endl;
    f << "  " << "\"ema_ready\": " << (p.ema_ready ? "true" : "false") << "," << std::endl;

    f << "  " << "\"ema_spread\": ";
    fmt_json_float(f, p.ema_spread);
    f << "," << std::endl;

    f << "  " << "\"ema_centroid\": [" << std::endl;
    for (int i = 0; i < SPEAKER_FEAT_DIM; i++) {
        f << "    ";
        fmt_json_float(f, p.ema_centroid[i]);
        f << (i < SPEAKER_FEAT_DIM - 1 ? "," : "") << std::endl;
    }
    f << "  ]," << std::endl;

    f << "  " << "\"samples\": [" << std::endl;
    for (int i = 0; i < static_cast<int>(p.samples.size()); i++) {
        f << "    {";
        f << "\"rms_energy\": ";
        fmt_json_float(f, p.samples[i].rms_energy);
        f << ", \"feat\": [";
        for (int j = 0; j < SPEAKER_FEAT_DIM; j++) {
            fmt_json_float(f, p.samples[i].feat[j]);
            f << (j < SPEAKER_FEAT_DIM - 1 ? ", " : "");
        }
        f << "]}" << (i < static_cast<int>(p.samples.size()) - 1 ? "," : "") << std::endl;
    }
    f << "  ]" << std::endl;
    f << "}" << std::endl;

    f.close();
    return true;
}

bool speaker_load(SpeakerProfile& p, const std::string& path) {
    std::ifstream f(path);
    if (!f.is_open()) return false;
    std::stringstream ss;
    ss << f.rdbuf();
    std::string data = ss.str();
    f.close();

    auto find_key = [&](const std::string& key) -> std::string::size_type {
        std::string search = "\"" + key + "\"";
        return data.find(search);
    };

    // Skip from key position (pointing at opening '"') past "key": to the value start
    auto skip_to_value = [&](std::string::size_type pos) -> std::string::size_type {
        while (pos < data.size() && data[pos] != ':') pos++;  // find colon
        if (pos < data.size()) pos++;                          // skip ':'
        while (pos < data.size() && (data[pos] == ' ' || data[pos] == '\t')) pos++;
        return pos;
    };

    auto parse_int = [&](std::string::size_type pos) -> std::pair<int, bool> {
        pos = skip_to_value(pos);
        char* end = nullptr;
        int val = std::strtol(data.c_str() + pos, &end, 10);
        return {val, end > data.c_str() + pos};
    };

    auto parse_float = [&](std::string::size_type pos) -> std::pair<float, bool> {
        pos = skip_to_value(pos);
        char* end = nullptr;
        float val = std::strtof(data.c_str() + pos, &end);
        return {val, end > data.c_str() + pos};
    };

    auto parse_bool = [&](std::string::size_type pos) -> std::pair<bool, bool> {
        pos = skip_to_value(pos);
        if (data.compare(pos, 4, "true") == 0) return {true, true};
        if (data.compare(pos, 5, "false") == 0) return {false, true};
        return {false, false};
    };

    auto find_arr_start = [&](std::string::size_type pos) -> std::string::size_type {
        while (pos < data.size() && data[pos] != '[') pos++;
        return pos;
    };

    auto parse_float_array = [&](std::string::size_type pos, std::vector<float>& out) -> bool {
        pos = find_arr_start(pos);
        if (pos == std::string::npos) return false;
        pos++;
        while (pos < data.size() && data[pos] != ']') {
            while (pos < data.size() && (data[pos] == ' ' || data[pos] == ',' || data[pos] == '\n' || data[pos] == '\r' || data[pos] == '\t')) pos++;
            if (pos >= data.size() || data[pos] == ']') break;
            char* end = nullptr;
            float val = std::strtof(data.c_str() + pos, &end);
            if (end <= data.c_str() + pos) return false;
            out.push_back(val);
            pos = end - data.c_str();
        }
        return true;
    };

    std::string::size_type pos = find_key("version");
    if (pos == std::string::npos) return false;
    auto vr = parse_int(pos);
    if (!vr.second || vr.first != 1) return false;

    pos = find_key("total_enrolled");
    if (pos == std::string::npos) return false;
    auto tr = parse_int(pos);
    if (!tr.second) return false;
    p.total_enrolled = tr.first;

    pos = find_key("ema_ready");
    if (pos == std::string::npos) return false;
    auto er = parse_bool(pos);
    if (!er.second) return false;
    p.ema_ready = er.first;

    pos = find_key("ema_spread");
    if (pos == std::string::npos) return false;
    auto spr = parse_float(pos);
    if (!spr.second) return false;
    p.ema_spread = spr.first;

    pos = find_key("ema_centroid");
    if (pos == std::string::npos) return false;
    std::vector<float> ema_vals;
    if (!parse_float_array(pos, ema_vals)) return false;
    if (static_cast<int>(ema_vals.size()) != SPEAKER_FEAT_DIM) return false;
    for (int i = 0; i < SPEAKER_FEAT_DIM; i++) p.ema_centroid[i] = ema_vals[i];

    pos = find_key("samples");
    if (pos == std::string::npos) return false;
    pos = find_arr_start(pos);
    if (pos == std::string::npos) return false;
    pos++;

    while (pos < data.size() && data[pos] != ']') {
        while (pos < data.size() && (data[pos] == ' ' || data[pos] == ',' || data[pos] == '\n' || data[pos] == '\r' || data[pos] == '\t')) pos++;
        if (pos >= data.size() || data[pos] == ']') break;
        if (data[pos] != '{') return false;
        pos++;

        SpeakerSample samp{};

        pos = data.find("\"rms_energy\"", pos);
        if (pos == std::string::npos) return false;
        auto rms_r = parse_float(pos);
        if (!rms_r.second) return false;
        samp.rms_energy = rms_r.first;

        pos = data.find("\"feat\"", pos);
        if (pos == std::string::npos) return false;
        std::vector<float> feat_vals;
        if (!parse_float_array(pos, feat_vals)) return false;
        if (static_cast<int>(feat_vals.size()) != SPEAKER_FEAT_DIM) return false;
        for (int i = 0; i < SPEAKER_FEAT_DIM; i++) samp.feat[i] = feat_vals[i];

        p.samples.push_back(samp);
    }

    p.consecutive_reject = 0;
    return true;
}

void speaker_reset(SpeakerProfile& p) {
    p.samples.clear();
    p.total_enrolled = 0;
    p.ema_centroid.fill(0.0f);
    p.ema_spread = 1.0f;
    p.ema_ready = false;
    p.consecutive_reject = 0;
}
