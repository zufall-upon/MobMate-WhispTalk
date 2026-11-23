#include "mobmate_native_core/native_api.h"
#include "mobmate_native_core/pcm_playback_owner.h"
#include "mobmate_native_core/addon_language_policy.h"
#include "mobmate_native_core/audio_prefilter.h"
#include "mobmate_native_core/manifest.h"
#include "mobmate_native_core/metrics.h"
#include "mobmate_native_core/version.h"
#include "mobmate_native_core/wave.h"
#include "mobmate_native_core/moonshine_bridge.h"
#include "mobmate_native_core/whisper_bridge.h"
#include "mobmate_native_core/silero_vad.h"
#include "mobmate_native_core/speaker_profile.h"
#ifdef _WIN32
#include "mobmate_native_core/audio_capture.h"
#else
#include "mobmate_native_core/audio_capture_linux.h"
// Include miniaudio declarations (implementation already compiled in audio_capture_linux.cpp)
#include "../../vendor/miniaudio.h"
#endif

#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <chrono>
#include <fstream>
#include <filesystem>
#include <memory>
#include <limits>
#include <sstream>
#include <algorithm>
#include <cctype>
#include <cwctype>
#include <cmath>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <thread>
#include <atomic>
#include <map>
#include <set>

#ifdef _WIN32
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <codecvt>
#ifndef AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
#define AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM 0x80000000
#endif
#ifndef AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
#define AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY 0x08000000
#endif
#endif

// sherpa-onnx C API: SenseVoice offline ASR
// DllImport 経由で sherpa-onnx-c-api.dll を使用する
#include "c-api.h"

static std::atomic<mmv2_addon_transcriber> g_addon_transcriber{nullptr};
static std::atomic<mmv2_addon_transcriber> g_addon_joint_transcriber{nullptr};
static std::atomic<mmv2_addon_transcriber_v2> g_addon_transcriber_v2{nullptr};
static std::atomic<mmv2_addon_transcriber_v2> g_addon_joint_transcriber_v2{nullptr};

static bool is_addon_engine(const std::string& engine) {
    return engine == "haas_ja" || engine == "parakeet_ja" || engine == "qwen3_asr_ja" || engine == "nemotron_3_5";
}
static bool addon_language_supported(const std::string& engine, const std::string& language) {
    return mobmate::addon_language_supported(engine, language);
}
// Native dispatch capability only; managed Ready still requires the verified resident/probe.
static bool addon_dispatch_available(const std::string& engine, const std::string& language, const std::string& mode) {
    return is_addon_engine(engine) && addon_language_supported(engine, language) &&
        (mode == "requested" || (mode == "auto" && engine == "qwen3_asr_ja")) &&
        (g_addon_transcriber_v2.load() || (engine == "haas_ja" && g_addon_transcriber.load()));
}
static bool addon_joint_language_supported(const std::string& engine, const std::string& language, const std::string& consent_language) {
    return mobmate::addon_joint_language_supported(engine, language, g_addon_joint_transcriber_v2.load() != nullptr, consent_language);
}
MMV2_API int mmv2_set_addon_transcriber_v2(mmv2_addon_transcriber_v2 callback) {
    g_addon_transcriber_v2.store(callback); return 0;
}
MMV2_API int mmv2_set_addon_joint_transcriber_v2(mmv2_addon_transcriber_v2 callback) {
    g_addon_joint_transcriber_v2.store(callback); return 0;
}

MMV2_API int mmv2_set_addon_transcriber(mmv2_addon_transcriber callback) {
    if (!callback) return -1;
    g_addon_transcriber.store(callback);
    return 0;
}

MMV2_API int mmv2_set_addon_joint_transcriber(mmv2_addon_transcriber callback) {
    if (!callback) return -1;
    g_addon_joint_transcriber.store(callback);
    return 0;
}

/* Minimal JSON escaping for short strings (no external dependency). */
static std::string json_escape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:   out += c; break;
       }
    }
    return out;
}

static size_t selector_text_len(const std::string& s) {
    size_t count = 0;
    for (size_t i = 0; i < s.size();) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        uint32_t cp = c;
        size_t n = 1;
        if ((c & 0xE0) == 0xC0 && i + 1 < s.size()) {
            cp = ((c & 0x1F) << 6) | (static_cast<unsigned char>(s[i + 1]) & 0x3F);
            n = 2;
        } else if ((c & 0xF0) == 0xE0 && i + 2 < s.size()) {
            cp = ((c & 0x0F) << 12) |
                 ((static_cast<unsigned char>(s[i + 1]) & 0x3F) << 6) |
                 (static_cast<unsigned char>(s[i + 2]) & 0x3F);
            n = 3;
        } else if ((c & 0xF8) == 0xF0 && i + 3 < s.size()) {
            cp = ((c & 0x07) << 18) |
                 ((static_cast<unsigned char>(s[i + 1]) & 0x3F) << 12) |
                 ((static_cast<unsigned char>(s[i + 2]) & 0x3F) << 6) |
                 (static_cast<unsigned char>(s[i + 3]) & 0x3F);
            n = 4;
        }
        const bool skip = cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r' || cp == 0x3000 ||
            cp == 0x3002 || cp == 0x3001 || cp == 0xFF0C || cp == ',' || cp == '.' ||
            cp == '!' || cp == '?' || cp == 0xFF01 || cp == 0xFF1F || cp == ':' ||
            cp == 0xFF1A || cp == ';' || cp == 0xFF1B || cp == '"' || cp == '\'' ||
            cp == '`' || cp == 0x00B4 || cp == 0x2018 || cp == 0x2019 ||
            cp == 0x201C || cp == 0x201D || cp == '(' || cp == ')' || cp == 0xFF08 ||
            cp == 0xFF09 || cp == '[' || cp == ']' || cp == '{' || cp == '}' ||
            cp == 0x300C || cp == 0x300D || cp == 0x300E || cp == 0x300F ||
            cp == 0x2026 || cp == 0x30FB || cp == 0xFF65;
        if (!skip) ++count;
        i += n;
    }
    return count;
}

static std::string make_error_json(const std::string& message, const std::string& stage) {
    return "{\"ok\":false,\"error\":\"" + json_escape(message) +
           "\",\"stage\":\"" + json_escape(stage) +
           "\",\"selected_text\":\"\",\"selected_engine\":\"none\",\"decision_reason\":\"error\"}";
}

static std::string make_success_json(const std::string& transcript, int latency_ms,
                                     const std::string& engine,
                                     const std::string& language) {
    const char* confidence_reason = engine == "moonshine"
        ? "moonshine_word_confidence_unavailable"
        : "whisper_confidence_unavailable";
    return "{\"ok\":true,\"transcript\":\"" + json_escape(transcript) +
           "\",\"latencyMs\":" + std::to_string(latency_ms) +
           ",\"selected_text\":\"" + json_escape(transcript) +
           "\",\"selected_engine\":\"" + json_escape(engine) +
           "\",\"decision_reason\":\"single_engine\"" +
           ",\"language\":\"" + json_escape(language) +
           "\",\"confidence_available\":false" +
           ",\"confidence_score\":null" +
           ",\"confidence_unavailable_reason\":\"" + confidence_reason +
           "\",\"candidates\":[{\"engine\":\"" + json_escape(engine) +
           "\",\"text\":\"" + json_escape(transcript) +
           "\",\"confidence_available\":false,\"confidence_score\":null}]}";
}

static std::string make_no_speech_json(const std::string& language) {
    return "{\"ok\":true,\"transcript\":\"\",\"latencyMs\":0"
           ",\"selected_text\":\"\",\"selected_engine\":\"none\""
           ",\"decision_reason\":\"no_speech\""
           ",\"language\":\"" + json_escape(language) + "\""
           ",\"confidence_available\":false,\"confidence_score\":null"
           ",\"confidence_unavailable_reason\":\"no_speech\""
           ",\"candidates\":[]}";
}

static bool is_near_silence_pcm16(const std::vector<int16_t>& samples) {
    if (samples.empty()) return true;
    double sum_sq = 0.0;
    int peak = 0;
    for (int16_t s : samples) {
        int v = std::abs(static_cast<int>(s));
        peak = (std::max)(peak, v);
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
    }
    double rms = std::sqrt(sum_sq / static_cast<double>(samples.size()));
    return peak < 80 && rms < 20.0;
}

/* ---- One-shot Whisper GPU cache ----
 * Hearing uses mmv2_transcribe_pcm16 repeatedly. Loading/freeing a Vulkan
 * whisper context for every short chunk is unstable on some AMD/Vulkan stacks
 * (observed process crash in vulkan-1.dll, 0xc0000409). Keep the GPU engine
 * alive for identical model/runtime options and serialize access.
 */
struct WhisperGpuOneShotCache {
    std::mutex mutex;
    std::string key;
    std::unique_ptr<mobmate::WhisperEngine> engine;
};

static WhisperGpuOneShotCache g_whisper_gpu_oneshot;

static mobmate::WhisperEngine* get_cached_whisper_gpu_engine_locked(
    const std::string& whisper_dll_dir,
    const std::string& model_path,
    std::string& err_msg)
{
    const std::string key = whisper_dll_dir + "\n" + model_path + "\nGPU";
    if (!g_whisper_gpu_oneshot.engine || g_whisper_gpu_oneshot.key != key) {
        auto engine = mobmate::WhisperEngine::load(whisper_dll_dir, model_path, err_msg, true);
        if (!engine) return nullptr;
        g_whisper_gpu_oneshot.engine = std::move(engine);
        g_whisper_gpu_oneshot.key = key;
    }
    return g_whisper_gpu_oneshot.engine.get();
}

// Phase 6D-2: Final acceptance gate helpers
// V1: MobMateWhisp.java:8046-8049 (MIN_AUDIO_DATA_LENGTH, FINAL_COOLDOWN_MS, FORCE_FINAL_MS)
struct PcmStats {
    double rms;
    int peak;
    double duration_ms;
    double active_duration_ms;
    int64_t sample_count;
};

static PcmStats compute_pcm_stats(const std::vector<int16_t>& samples) {
    PcmStats stats{0.0, 0, 0.0, 0.0, 0};
    if (samples.empty()) return stats;
    stats.sample_count = static_cast<int64_t>(samples.size());
    double sum_sq = 0.0;
    for (int16_t s : samples) {
        int v = std::abs(static_cast<int>(s));
        stats.peak = (std::max)(stats.peak, v);
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
    }
    stats.rms = std::sqrt(sum_sq / static_cast<double>(samples.size()));
    stats.duration_ms = static_cast<double>(samples.size()) * 1000.0 / 16000.0;
    const size_t frame_size = 512;
    const double active_rms_threshold = (std::max)(120.0, stats.rms * 0.20);
    const int active_peak_threshold = 500;
    size_t first_active = samples.size();
    size_t last_active = 0;
    for (size_t frame_start = 0; frame_start < samples.size(); frame_start += frame_size) {
        const size_t frame_end = (std::min)(samples.size(), frame_start + frame_size);
        double frame_sum_sq = 0.0;
        int frame_peak = 0;
        for (size_t i = frame_start; i < frame_end; ++i) {
            int v = std::abs(static_cast<int>(samples[i]));
            frame_peak = (std::max)(frame_peak, v);
            frame_sum_sq += static_cast<double>(samples[i]) * static_cast<double>(samples[i]);
        }
        const double frame_rms = std::sqrt(frame_sum_sq / static_cast<double>(frame_end - frame_start));
        if (frame_rms >= active_rms_threshold || frame_peak >= active_peak_threshold) {
            if (first_active == samples.size()) first_active = frame_start;
            last_active = frame_end;
        }
    }
    if (first_active < last_active) {
        stats.active_duration_ms = static_cast<double>(last_active - first_active) * 1000.0 / 16000.0;
    }
    return stats;
}

// Phase 6F-0: Per-frame statistics for instrumentation
struct FrameStats {
    double rms;
    int peak;
    double zcr;           // zero-crossing rate (0..1)
    double voiceBandRatio; // energy above ~300Hz / total energy (0..1)
};

static FrameStats compute_frame_stats(const int16_t* samples, size_t n) {
    FrameStats fs{0.0, 0, 0.0, 0.0};
    if (n < 2) return fs;
    double sum_sq = 0.0;
    int zero_crossings = 0;
    double diff_energy = 0.0; // first-difference energy (high-pass proxy)
    for (size_t i = 0; i < n; i++) {
        int v = std::abs(static_cast<int>(samples[i]));
        fs.peak = (std::max)(fs.peak, v);
        sum_sq += static_cast<double>(samples[i]) * static_cast<double>(samples[i]);
        if (i > 0 && ((samples[i] >= 0) != (samples[i - 1] >= 0))) {
            zero_crossings++;
        }
        if (i > 0) {
            double d = static_cast<double>(samples[i]) - static_cast<double>(samples[i - 1]);
            diff_energy += d * d;
        }
    }
    fs.rms = std::sqrt(sum_sq / static_cast<double>(n));
    fs.zcr = static_cast<double>(zero_crossings) / static_cast<double>(n - 1);
    fs.voiceBandRatio = (sum_sq > 0.0) ? (diff_energy / (2.0 * sum_sq)) : 0.0;
    // diff_energy / (2*total_energy) approximates high-frequency fraction
    return fs;
}

/* ---- P0 2026-07-14: hallucination gates ported from Mobfy ----
 * Origin: voice_conversion.cpp vc_calc_zcr_i16 / vc_calc_periodicity_i16 /
 * vc_analyze_asr_gate_audio / vc_asr_gate_rejects_text_rate /
 * vc_asr_gate_accepts_nonliteral_text (Task A/B 2026-07-12).
 * Duplicated (not shared) to avoid touching the Mobfy TU; adaptation:
 * realtime segments include ~1s pre-roll + ~3s silence tail, so the analysis
 * is restricted to the active region instead of the whole buffer. */
static float rt_calc_zcr_i16(const int16_t* samples, size_t count) {
    if (!samples || count < 2) return 0.0f;
    size_t crossings = 0;
    for (size_t i = 1; i < count; ++i) {
        if ((samples[i] >= 0) != (samples[i - 1] >= 0)) ++crossings;
    }
    return static_cast<float>(crossings) / static_cast<float>(count - 1);
}

static float rt_calc_periodicity_i16(const int16_t* samples, size_t count) {
    if (!samples || count < 160) return 0.0f;
    double best = 0.0;
    const size_t max_lag = (count / 2 < static_cast<size_t>(200)) ? count / 2 : static_cast<size_t>(200);
    for (size_t lag = 40; lag <= max_lag; ++lag) {
        double corr = 0.0, e0 = 0.0, e1 = 0.0;
        for (size_t i = 0; i + lag < count; ++i) {
            const double a = static_cast<double>(samples[i]);
            const double b = static_cast<double>(samples[i + lag]);
            corr += a * b;
            e0 += a * a;
            e1 += b * b;
        }
        best = (std::max)(best, corr / (std::sqrt(e0 * e1) + 1e-9));
    }
    return static_cast<float>(best);
}

/* Mobfy noise-adaptive VAD start threshold (voice_conversion.cpp:6511-6513). */
static double realtime_effective_start_threshold(double base, bool noise_ready, double silero_p90) {
    if (!noise_ready) return base;
    double adaptive = silero_p90 + 0.22;
    if (adaptive > 0.72) adaptive = 0.72;
    return adaptive > base ? adaptive : base;
}

struct RtAsrGateAnalysis {
    int duration_ms = 0;      // active-region span (not whole segment)
    float rms = 0.0f;         // active-region rms
    float peak = 0.0f;
    float zcr = 1.0f;
    float periodicity = 0.0f;
    float start_prob = 0.0f;
    float active_ratio = 0.0f;
    float active_span_ratio = 0.0f; // active frames / active-region frames, excluding outer silence
    float voiced_active_ratio = 0.0f;
    float air_active_ratio = 0.0f;
};

static RtAsrGateAnalysis rt_analyze_asr_gate_audio(const int16_t* samples, size_t num_samples,
                                                   int sr, float start_prob) {
    RtAsrGateAnalysis a{};
    a.start_prob = start_prob;
    if (!samples || num_samples == 0 || sr <= 0) return a;
    const size_t frame_n = static_cast<size_t>((std::max)(1, sr / 50)); // 20ms
    /* STT adaptation: Mobfy used a fixed 650 rms active-frame threshold, which
     * assumes normal mic level. Quiet speakers (talk_low_voice fixtures) fall
     * below it, shrinking the active span and firing text_rate/weak_long on
     * genuine speech. Scale the threshold from the loudest frame instead. */
    float max_frame_rms = 0.0f;
    for (size_t pos = 0; pos + frame_n <= num_samples; pos += frame_n) {
        double frame_sum_sq = 0.0;
        for (size_t i = 0; i < frame_n; ++i) {
            const double s = static_cast<double>(samples[pos + i]);
            frame_sum_sq += s * s;
        }
        const float fr = static_cast<float>(std::sqrt(frame_sum_sq / static_cast<double>(frame_n)));
        if (fr > max_frame_rms) max_frame_rms = fr;
    }
    float active_thr = 0.22f * max_frame_rms;
    if (active_thr > 650.0f) active_thr = 650.0f;
    if (active_thr < 180.0f) active_thr = 180.0f;
    size_t frame_count = 0;
    size_t active_count = 0;
    size_t voiced_count = 0;
    size_t air_count = 0;
    size_t first_active = num_samples;
    size_t last_active = 0;
    for (size_t pos = 0; pos + frame_n <= num_samples; pos += frame_n) {
        double frame_sum_sq = 0.0;
        for (size_t i = 0; i < frame_n; ++i) {
            const double s = static_cast<double>(samples[pos + i]);
            frame_sum_sq += s * s;
        }
        ++frame_count;
        const float frame_rms = static_cast<float>(std::sqrt(frame_sum_sq / static_cast<double>(frame_n)));
        if (frame_rms < active_thr) continue;
        ++active_count;
        if (first_active == num_samples) first_active = pos;
        last_active = pos + frame_n;
        const float frame_zcr = rt_calc_zcr_i16(samples + pos, frame_n);
        const float frame_periodicity = rt_calc_periodicity_i16(samples + pos, frame_n);
        if (frame_periodicity >= 0.18f && frame_zcr < 0.12f) ++voiced_count;
        if (frame_zcr >= 0.075f && frame_periodicity < 0.20f) ++air_count;
    }
    if (frame_count > 0)
        a.active_ratio = static_cast<float>(active_count) / static_cast<float>(frame_count);
    if (active_count > 0) {
        a.voiced_active_ratio = static_cast<float>(voiced_count) / static_cast<float>(active_count);
        a.air_active_ratio = static_cast<float>(air_count) / static_cast<float>(active_count);
    }
    if (first_active >= last_active) return a; // no active audio at all
    const size_t active_len = last_active - first_active;
    a.active_span_ratio = static_cast<float>(active_count * frame_n) / static_cast<float>(active_len);
    a.duration_ms = static_cast<int>((active_len * 1000) / static_cast<size_t>((std::max)(1, sr)));
    double sum_sq = 0.0;
    int peak_i = 0;
    for (size_t i = first_active; i < last_active; ++i) {
        const double s = static_cast<double>(samples[i]);
        sum_sq += s * s;
        peak_i = (std::max)(peak_i, std::abs(static_cast<int>(samples[i])));
    }
    a.rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(active_len)));
    a.peak = static_cast<float>(peak_i);
    const size_t analyze_n = (std::min)(active_len, static_cast<size_t>((std::max)(1, sr)));
    a.zcr = rt_calc_zcr_i16(samples + first_active, analyze_n);
    a.periodicity = rt_calc_periodicity_i16(samples + first_active, analyze_n);
    return a;
}

static size_t rt_utf8_codepoint_count(const std::string& s) {
    size_t count = 0;
    for (size_t i = 0; i < s.size();) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        size_t len = 1;
        if ((c & 0xF8) == 0xF0) len = 4;
        else if ((c & 0xF0) == 0xE0) len = 3;
        else if ((c & 0xE0) == 0xC0) len = 2;
        if (c != ' ' && c != '\t') ++count;
        i += len;
    }
    return count;
}

/* Text/audio length consistency: hallucinated long sentences show an
 * unrealistically high char rate for the active speech duration.
 * Non-literal: codepoint count vs duration only, no text content comparison. */
static bool rt_asr_gate_rejects_text_rate(const RtAsrGateAnalysis& a, const std::string& text) {
    if (a.duration_ms <= 0 || text.empty()) return false;
    const double dur_sec = static_cast<double>(a.duration_ms) / 1000.0;
    const double chars = static_cast<double>(rt_utf8_codepoint_count(text));
    /* Latin scripts spell ~2x more codepoints per second than CJK
     * (letters vs. syllabic chars). Pick the limit from the script mix,
     * not from text content. ("Open the map now." = 15.6 chars/s genuine.) */
    size_t ascii_letters = 0, total = 0;
    for (size_t i = 0; i < text.size();) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        size_t len = 1;
        if ((c & 0xF8) == 0xF0) len = 4;
        else if ((c & 0xF0) == 0xE0) len = 3;
        else if ((c & 0xE0) == 0xC0) len = 2;
        if (c != ' ' && c != '\t') {
            ++total;
            if (c < 0x80) ++ascii_letters;
        }
        i += len;
    }
    const bool latin_dominant = total > 0 &&
        (static_cast<double>(ascii_letters) / static_cast<double>(total)) >= 0.6;
    const double rate_limit = latin_dominant ? 26.0 : 14.0;
    if (chars / dur_sec > rate_limit) return true;         // hallucinated long text
    if (chars <= 2.0 && a.duration_ms > 1500) return true; // long audio, near-empty text
    return false;
}

/* Acoustic hallucination gate (Mobfy Task A port): rejects segments whose
 * active audio is dominated by diffuse air / monotone hum / weak signal that
 * cannot plausibly produce the recognized text. */
static bool rt_asr_gate_accepts_nonliteral_text(const RtAsrGateAnalysis& a) {
    const bool very_short = a.duration_ms > 0 && a.duration_ms <= 520;
    const bool voiced = a.periodicity >= 0.18f || a.start_prob >= 0.62f;
    const bool audible = a.rms >= 650.0f || a.peak >= 2200.0f;
    const bool air_like = a.zcr >= 0.075f && a.periodicity < 0.20f && a.start_prob < 0.72f;
    /* STT adaptation: quiet-but-articulated speech (talk_low_voice) also has
     * rms < 520; require the active frames to lack voiced structure too. */
    const bool weak_long_hallucination = a.duration_ms >= 900 && a.duration_ms <= 2600 &&
        a.rms < 520.0f && a.peak < 5200.0f && a.voiced_active_ratio < 0.50f;
    const bool low_articulation_hum = a.duration_ms >= 900 && a.duration_ms <= 2400 &&
        a.rms < 900.0f && a.zcr < 0.012f && a.start_prob < 0.88f;
    const bool diffuse_exhale = a.active_ratio >= 0.10f &&
        a.air_active_ratio >= 0.20f && a.voiced_active_ratio < 0.75f;
    /* High-ZCR periodic noise (drink/contact sounds) can have enough
     * autocorrelation to avoid diffuse_exhale while still lacking voiced
     * frames. The 07/14 live hallucination measured zcr=0.270, voiced=0.118,
     * active=0.230, start_prob=0.403. Non-literal acoustic gate only. */
    const bool high_zcr_low_voice_noise = a.duration_ms >= 900 && a.duration_ms <= 3000 &&
        a.zcr >= 0.18f && a.voiced_active_ratio < 0.20f &&
        a.active_ratio <= 0.30f && a.start_prob < 0.55f &&
        (a.rms >= 1000.0f || a.peak >= 10000.0f);
    /* STT adaptation: Mobfy tiers 1-2 (active_ratio up to 0.65) mis-fire on
     * fully-voiced short greetings (こんにちは: voiced 0.96, periodicity 0.53,
     * zcr 0.019, active 0.51). Keep only the low-activity tier: real hums are
     * sparse within the active span, articulated speech is not. Outer pre-roll
     * and VAD silence must not turn the same voiced region into a hum.
     * Keep whole-segment active_ratio for the other established noise gates. */
    const bool voiced_monotone_hum = a.duration_ms >= 900 && a.duration_ms <= 2600 &&
        a.voiced_active_ratio >= 0.82f && a.periodicity >= 0.40f &&
        a.air_active_ratio <= 0.02f && a.active_span_ratio <= 0.43f &&
        a.zcr < 0.060f; /* articulated speech has consonant ZCR (KO fixture 0.118); hums sit at 0.02-0.05 */
    const bool weak_short = very_short && (!voiced || !audible || air_like);
    return !weak_short && !weak_long_hallucination && !low_articulation_hum &&
           !diffuse_exhale && !high_zcr_low_voice_noise && !voiced_monotone_hum;
}
/* ---- end P0 hallucination gates ---- */

// Returns true if we should skip ASR for this segment (input gate)
static bool should_skip_realtime_final_input(const PcmStats& stats, std::string& reason) {
    // V1: MIN_AUDIO_DATA_LENGTH = 16000 bytes = 8000 samples = 500ms
    // But we use 240ms as minimum to avoid dropping very short utterances
    if (stats.duration_ms < 240.0) {
        reason = "too_short";
        return true;
    }
    // Near silence: peak < 250 and rms < 60
    if (stats.peak < 250 && stats.rms < 60.0) {
        reason = "no_speech";
        return true;
    }
    return false;
}

static std::string normalize_text_for_similarity(const std::string& text);
static bool is_asr_boilerplate_hallucination(const std::string& text);

/* Reject cross-language decode drift before any final reaches TTS. Laughter,
 * clipping, and non-speech can make Moonshine emit a fluent Latin-script
 * sentence even when the session language is fixed to JA/KO/ZH. */
static bool rt_text_has_expected_script(const std::string& text, const std::string& language) {
    const bool is_ja = language == "ja" || language.rfind("ja-", 0) == 0;
    const bool is_ko = language == "ko" || language.rfind("ko-", 0) == 0;
    const bool is_zh = language == "zh" || language.rfind("zh-", 0) == 0;
    if (!is_ja && !is_ko && !is_zh) return true;

    for (size_t i = 0; i < text.size();) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        uint32_t cp = c;
        size_t len = 1;
        if ((c & 0xE0) == 0xC0 && i + 1 < text.size()) {
            cp = ((c & 0x1F) << 6) |
                 (static_cast<unsigned char>(text[i + 1]) & 0x3F);
            len = 2;
        } else if ((c & 0xF0) == 0xE0 && i + 2 < text.size()) {
            cp = ((c & 0x0F) << 12) |
                 ((static_cast<unsigned char>(text[i + 1]) & 0x3F) << 6) |
                 (static_cast<unsigned char>(text[i + 2]) & 0x3F);
            len = 3;
        } else if ((c & 0xF8) == 0xF0 && i + 3 < text.size()) {
            cp = ((c & 0x07) << 18) |
                 ((static_cast<unsigned char>(text[i + 1]) & 0x3F) << 12) |
                 ((static_cast<unsigned char>(text[i + 2]) & 0x3F) << 6) |
                 (static_cast<unsigned char>(text[i + 3]) & 0x3F);
            len = 4;
        }
        if (is_ja && ((cp >= 0x3040 && cp <= 0x30FF) ||
                      (cp >= 0x3400 && cp <= 0x4DBF) ||
                      (cp >= 0x4E00 && cp <= 0x9FFF))) return true;
        if (is_ko && ((cp >= 0x1100 && cp <= 0x11FF) ||
                      (cp >= 0xAC00 && cp <= 0xD7AF))) return true;
        if (is_zh && ((cp >= 0x3400 && cp <= 0x4DBF) ||
                      (cp >= 0x4E00 && cp <= 0x9FFF))) return true;
        i += len;
    }
    return false;
}

// Returns true if we should accept the ASR result (text gate)
// For short segments with weak signal, we may drop even after ASR
static bool should_accept_realtime_final_text(const std::string& text, const PcmStats& stats,
                                              const std::string& engine, const std::string& language,
                                              std::string& reason,
                                              const std::vector<int16_t>* pcm_samples = nullptr,
                                              double start_prob = -1.0) {
    // Empty text is not acceptable
    if (text.empty()) {
        reason = "empty_text";
        return false;
    }
    /* 2026-07-14: engine-agnostic hard blacklist of ASR training-set
     * boilerplate (YouTube outro class). These phrases are never plausible
     * live utterances; they only appear when the model hallucinates on
     * silence/noise. Applies to all engines and all call sites. */
    if (is_asr_boilerplate_hallucination(text)) {
        reason = "boilerplate_hallucination";
        return false;
    }
    if (!rt_text_has_expected_script(text, language)) {
        reason = "language_script_mismatch";
        return false;
    }
    // Short segment with weak signal: be more conservative
    // V1: MobMateWhisp.java:8049 (FORCE_FINAL_MS) - but we use a simpler gate
    if (stats.duration_ms < 700.0 && stats.peak < 500 && stats.rms < 150.0) {
        reason = "weak_short_segment";
        return false;
    }
    // P0 2026-07-14: Mobfy hallucination gate port (text rate + acoustic shape).
    if (pcm_samples && !pcm_samples->empty()) {
        const RtAsrGateAnalysis a = rt_analyze_asr_gate_audio(
            pcm_samples->data(), pcm_samples->size(), 16000,
            start_prob >= 0.0 ? static_cast<float>(start_prob) : -1.0f);
        if (rt_asr_gate_rejects_text_rate(a, text)) {
            std::ostringstream oss;
            oss << "text_rate active_ms=" << a.duration_ms
                << " chars=" << rt_utf8_codepoint_count(text);
            reason = oss.str();
            return false;
        }
        if (!rt_asr_gate_accepts_nonliteral_text(a)) {
            std::ostringstream oss;
            oss << "acoustic_hallucination active_ms=" << a.duration_ms
                << " rms=" << a.rms << " peak=" << a.peak
                << " zcr=" << a.zcr << " periodicity=" << a.periodicity
                << " active_ratio=" << a.active_ratio
                << " voiced=" << a.voiced_active_ratio
                << " air=" << a.air_active_ratio
                << " start_prob=" << a.start_prob;
            reason = oss.str();
            return false;
        }
    }
    return true;
}

static std::string normalize_text_for_similarity(const std::string& text) {
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size();) {
        unsigned char c = static_cast<unsigned char>(text[i]);
        if (c < 0x80) {
            char ch = static_cast<char>(c);
            if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n' &&
                ch != '.' && ch != ',' && ch != '!' && ch != '?' &&
                ch != '"' && ch != '\'') {
                out.push_back(static_cast<char>(std::tolower(c)));
            }
            ++i;
            continue;
        }
        size_t len = (c & 0xE0) == 0xC0 ? 2 : ((c & 0xF0) == 0xE0 ? 3 : ((c & 0xF8) == 0xF0 ? 4 : 1));
        std::string cp = text.substr(i, (std::min)(len, text.size() - i));
        if (cp != "　" && cp != "。" && cp != "、" && cp != "！" && cp != "？" && cp != "「" && cp != "」") {
            out += cp;
        }
        i += len;
    }
    return out;
}

static double byte_lcs_similarity(const std::string& a, const std::string& b) {
    const std::string x = normalize_text_for_similarity(a);
    const std::string y = normalize_text_for_similarity(b);
    if (x.empty() || y.empty()) return 0.0;
    auto split_units = [](const std::string& s) {
        std::vector<std::string> units;
        for (size_t i = 0; i < s.size();) {
            unsigned char c = static_cast<unsigned char>(s[i]);
            size_t len = c < 0x80 ? 1 : ((c & 0xE0) == 0xC0 ? 2 : ((c & 0xF0) == 0xE0 ? 3 : ((c & 0xF8) == 0xF0 ? 4 : 1)));
            len = (std::min)(len, s.size() - i);
            units.push_back(s.substr(i, len));
            i += len;
        }
        return units;
    };
    const auto xu = split_units(x);
    const auto yu = split_units(y);
    std::vector<int> prev(yu.size() + 1, 0), cur(yu.size() + 1, 0);
    for (size_t i = 1; i <= xu.size(); ++i) {
        for (size_t j = 1; j <= yu.size(); ++j) {
            cur[j] = (xu[i - 1] == yu[j - 1]) ? prev[j - 1] + 1 : (std::max)(prev[j], cur[j - 1]);
        }
        std::swap(prev, cur);
        std::fill(cur.begin(), cur.end(), 0);
    }
    return static_cast<double>(prev[yu.size()]) / static_cast<double>((std::max)(xu.size(), yu.size()));
}

/* Hard blacklist: ASR training-set boilerplate that models emit on
 * silence/noise (YouTube outro / subscribe prompts). Substring match on
 * normalized text (spaces/punctuation stripped, ASCII lowercased).
 * These phrases are effectively impossible as genuine live STT input. */
static bool is_asr_boilerplate_hallucination(const std::string& text) {
    const std::string norm = normalize_text_for_similarity(text);
    if (norm.empty()) return false;
    static const char* kHardBoilerplate[] = {
        /* JA */
        "ご視聴ありがとう", "ご清聴ありがとう", "チャンネル登録",
        "次の動画でお会いしましょう", "動画をご覧いただき", "字幕は自動生成",
        /* EN (normalized: lowercase, no spaces) */
        "thanksforwatching", "thankyouforwatching", "pleasesubscribe",
        /* KO / ZH */
        "시청해주셔서감사합니다", "구독과좋아요", "谢谢观看", "感谢观看", "请订阅",
    };
    for (const char* p : kHardBoilerplate) {
        if (norm.find(p) != std::string::npos) return true;
    }
    return false;
}

/* Decode-time Whisper confidence gate. no_speech_prob comes from the
 * <|nospeech|> token probability at decode time: genuine speech typically
 * scores < 0.1 while silence/noise hallucinations sit above 0.5-0.6 even
 * when text is emitted. Three tiers:
 *   1. near-certain silence (>= 0.85): drop unconditionally
 *   2. OpenAI-style combined gate (no_speech >= 0.60 AND weak avg token p):
 *      drops noise hallucinations without touching confident genuine speech
 *   3. soft blacklist (no_speech >= 0.50 + exact-match closing phrase):
 *      catches memorized boilerplate that decodes with HIGH token confidence
 *      on silence (e.g. ありがとうございました), which tier 2 cannot catch.
 * Returns false when metrics are unavailable (< 0). */
static bool whisper_confidence_rejects_final(float max_no_speech_prob, float avg_token_p,
                                             const std::string& text, std::string& reason) {
    if (max_no_speech_prob < 0.0f) return false;
    if (max_no_speech_prob >= 0.85f) {
        reason = "no_speech_hard";
        return true;
    }
    if (max_no_speech_prob >= 0.60f && avg_token_p >= 0.0f && avg_token_p < 0.62f) {
        reason = "no_speech_low_token_p";
        return true;
    }
    if (max_no_speech_prob >= 0.50f) {
        static const char* kSoftBoilerplate[] = {
            "ありがとうございました", "ありがとうございます",
            "おわり", "終わり", "おしまい", "ご覧いただきありがとうございます",
        };
        const std::string norm = normalize_text_for_similarity(text);
        for (const char* p : kSoftBoilerplate) {
            if (norm == p) {
                reason = "no_speech_soft_boilerplate";
                return true;
            }
        }
    }
    return false;
}

/* Returns true if the text looks like a laughter / non-verbal vocalisation
 * (e.g. んんんん, ほほほ, フフフフ, わあわあわあ).
 * Algorithm: strip punctuation, split to UTF-8 codepoints, then:
 *   - 1 distinct codepoint, length >= 3: pure repetition (んんんん, ほほほ)
 *   - 2 distinct codepoints, length >= 4, each appearing >= 2 times: alternating (わあわあ)
 * Upper bound of 15 chars prevents matching long sentences. */
static bool is_laughter_like(const std::string& text) {
    const std::string s = normalize_text_for_similarity(text);
    if (s.empty()) return false;
    // split to UTF-8 codepoints
    std::vector<std::string> units;
    for (size_t i = 0; i < s.size();) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        size_t len = c < 0x80 ? 1 : ((c & 0xE0) == 0xC0 ? 2 : ((c & 0xF0) == 0xE0 ? 3 : ((c & 0xF8) == 0xF0 ? 4 : 1)));
        len = (std::min)(len, s.size() - i);
        units.push_back(s.substr(i, len));
        i += len;
    }
    const size_t n = units.size();
    if (n < 3 || n > 15) return false;
    // count distinct codepoints (small n, linear scan is fine)
    std::vector<std::string> distinct;
    for (const auto& u : units) {
        if (std::find(distinct.begin(), distinct.end(), u) == distinct.end())
            distinct.push_back(u);
    }
    if (distinct.size() == 1) return true;  // pure repetition
    if (distinct.size() == 2 && n >= 4) {
        // alternating pattern: each char must appear at least twice
        size_t ca = 0, cb = 0;
        for (const auto& u : units) {
            if (u == distinct[0]) ++ca; else ++cb;
        }
        return ca >= 2 && cb >= 2;
    }
    return false;
}

static std::vector<uint8_t> pcm16_to_bytes(const std::vector<int16_t>& samples) {
    std::vector<uint8_t> bytes(samples.size() * 2);
    for (size_t i = 0; i < samples.size(); ++i) {
        int16_t s = samples[i];
        bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
        bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
    }
    return bytes;
}

static std::vector<float> pcm16_to_float32(const std::vector<int16_t>& samples) {
    std::vector<float> float_samples(samples.size());
    for (size_t i = 0; i < samples.size(); ++i) {
        float_samples[i] = static_cast<float>(samples[i]) / 32768.0f;
    }
    return float_samples;
}

static std::vector<int16_t> bytes_to_pcm16(const std::vector<uint8_t>& bytes) {
    std::vector<int16_t> samples;
    samples.reserve(bytes.size() / 2);
    for (size_t i = 0; i + 1 < bytes.size(); i += 2) {
        int lo = bytes[i] & 0xFF;
        int hi = bytes[i + 1];
        samples.push_back(static_cast<int16_t>(lo | (hi << 8)));
    }
    return samples;
}

/* Phase 6B P0-1: Final chunk normalization (matching V1 normalizeFinalChunkForAsr).
 * Target RMS dBFS: -21.0 (V1 default).
 * Min RMS: 220.0. No peak ceiling (V1 has none). Gain clamped to int16 range. */
/* Phase 6B Step 6: V1 normalizeFinalChunkForAsr exact match
   V1: base target -21.0dBFS + extraGainDb(+20dB) clamped to -7.0dBFS
   V2: use effective target directly */
static constexpr double FINAL_NORM_TARGET_DBFS = -7.0;  /* V1 effective target after extraGainDb */
static constexpr double FINAL_NORM_PEAK_CEILING_DBFS = -3.0;  /* V1 PEAK_CEILING_DBFS */
static constexpr double FINAL_NORM_MIN_RMS = 220.0;
static constexpr double FINAL_NORM_GAIN_FLOOR = 1.03;  /* V1: skip if gain < 1.03 */
static constexpr double PCM16_MAX = 32767.0;
static constexpr double PCM16_MIN = -32768.0;

static std::vector<int16_t> normalize_final_chunk(const std::vector<int16_t>& samples,
                                                      double target_dbfs) {
    if (samples.size() < 2) return samples;

    double sum_sq = 0.0;
    int peak = 0;
    for (int16_t s : samples) {
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
        int abs_val = s < 0 ? -s : s;
        if (abs_val > peak) peak = abs_val;
    }
    double rms = std::sqrt(sum_sq / static_cast<double>(samples.size()));
    if (rms < FINAL_NORM_MIN_RMS || peak <= 0) return samples;

    double target_linear = std::pow(10.0, target_dbfs / 20.0) * PCM16_MAX;
    double ceiling_linear = std::pow(10.0, FINAL_NORM_PEAK_CEILING_DBFS / 20.0) * PCM16_MAX;
    double gain = target_linear / (rms > 1.0 ? rms : 1.0);
    /* V1: clamp gain so peak doesn't exceed ceiling */
    gain = std::min<double>(gain, ceiling_linear / (peak > 0 ? static_cast<double>(peak) : 1.0));
    if (!std::isfinite(gain) || gain < FINAL_NORM_GAIN_FLOOR) return samples;

    std::vector<int16_t> out(samples.size());
    for (size_t i = 0; i < samples.size(); ++i) {
        double v = static_cast<double>(samples[i]) * gain;
        v = std::round(v);
        if (v > PCM16_MAX) v = PCM16_MAX;
        if (v < PCM16_MIN) v = PCM16_MIN;
        out[i] = static_cast<int16_t>(v);
    }
    return out;
}

static std::vector<int16_t> apply_whisper_prefilter(const std::vector<int16_t>& samples,
                                                     const std::string& scene,
                                                     const std::string& profile,
                                                     int sample_rate_hz) {
    auto pcm_bytes = pcm16_to_bytes(samples);
    mobmate::AudioPrefilterState pstate;
    pstate.reset();
    bool use_strong = (profile == "strong") ||
        (profile == "auto" && (scene == "talk_noisy" || scene == "desktop_hearing"));
    auto filtered = use_strong
        ? mobmate::prefilter_process_strong(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), sample_rate_hz, &pstate)
        : mobmate::prefilter_process(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), sample_rate_hz, &pstate);
    if (filtered.empty()) return samples;
    return bytes_to_pcm16(filtered);
}

/* UTF-8 to std::filesystem::path helper (mirrors wave.cpp). */
static std::filesystem::path utf8_to_path(const std::string& utf8) {
#ifdef _WIN32
    std::wstring_convert<std::codecvt_utf8<wchar_t>> converter;
    return std::filesystem::path(converter.from_bytes(utf8));
#else
    return std::filesystem::path(utf8);
#endif
}

/* Phase 6B: compute dump file path for a given segment and type. */
static std::string make_dump_path(const std::string& dump_dir, int seg_id,
                                   const std::string& type, const std::string& ext) {
    if (dump_dir.empty()) return "";
    if ((ext == "wav" && (type == "asr_input" || type == "raw" || type == "filtered")) ||
        (ext == "json" && type == "meta")) {
        seg_id = ((seg_id % 10) + 10) % 10;
    }
    return (std::filesystem::path(dump_dir) /
            ("seg_" + std::to_string(seg_id) + "_" + type + "." + ext)).string();
}

static std::string base64_encode_bytes(const uint8_t* data, size_t len) {
    static constexpr char table[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve(((len + 2) / 3) * 4);
    for (size_t i = 0; i < len; i += 3) {
        uint32_t v = static_cast<uint32_t>(data[i]) << 16;
        if (i + 1 < len) v |= static_cast<uint32_t>(data[i + 1]) << 8;
        if (i + 2 < len) v |= static_cast<uint32_t>(data[i + 2]);
        out.push_back(table[(v >> 18) & 0x3F]);
        out.push_back(table[(v >> 12) & 0x3F]);
        out.push_back(i + 1 < len ? table[(v >> 6) & 0x3F] : '=');
        out.push_back(i + 2 < len ? table[v & 0x3F] : '=');
    }
    return out;
}

static bool is_rotated_asr_input_wav(const std::string& filename) {
    for (int i = 0; i < 10; ++i) {
        const std::string prefix = "seg_" + std::to_string(i);
        if (filename == prefix + "_asr_input.wav" ||
            filename == prefix + "_raw.wav" ||
            filename == prefix + "_filtered.wav") return true;
    }
    return false;
}

static bool is_rotated_meta_json(const std::string& filename) {
    for (int i = 0; i < 10; ++i) {
        if (filename == "seg_" + std::to_string(i) + "_meta.json") return true;
    }
    return false;
}

static void cleanup_debug_dumps(const std::string& dump_dir) {
    if (dump_dir.empty()) return;
    std::error_code ec;
    auto dir = std::filesystem::path(dump_dir);
    if (!std::filesystem::exists(dir, ec)) return;
    for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
        if (ec) break;
        if (!entry.is_regular_file(ec)) continue;
        auto path = entry.path();
        const auto filename = path.filename().string();
        if (path.extension() == ".wav" && is_rotated_asr_input_wav(filename)) continue;
        if (path.extension() == ".json" && is_rotated_meta_json(filename)) continue;
        if (path.extension() != ".wav" && path.extension() != ".json") continue;
        std::filesystem::remove(path, ec);
    }
}

/* Phase 6B: dump PCM16 segment as WAV. */
static void dump_pcm16_wav(const std::string& dump_dir, int seg_id,
                            const std::string& type,
                            const std::vector<int16_t>& samples, uint32_t sample_rate) {
    std::string path = make_dump_path(dump_dir, seg_id, type, "wav");
    if (path.empty() || samples.empty()) return;
    if (type == "asr_input") {
        cleanup_debug_dumps(dump_dir);
    }
    mobmate::write_wav_pcm16(path, samples.data(), static_cast<int>(samples.size()), sample_rate);
}

/* Phase 6B: dump metadata JSON for a segment. */
static void dump_segment_metadata(const std::string& dump_dir, int seg_id,
                                    const std::vector<int16_t>& raw_samples,
                                    const std::string& transcript,
                                    int latency_ms, const std::string& engine,
                                    const std::string& reason, double capture_rms,
                                    const std::string& vad_state,
                                    const std::vector<int16_t>* filtered_samples) {
    std::string path = make_dump_path(dump_dir, seg_id, "meta", "json");
    if (path.empty()) return;
    cleanup_debug_dumps(dump_dir);
    double raw_peak = 0.0;
    for (int16_t s : raw_samples) {
        double v = std::abs(static_cast<double>(s));
        if (v > raw_peak) raw_peak = v;
    }
    double raw_rms = 0.0;
    if (!raw_samples.empty()) {
        double sum_sq = 0.0;
        for (int16_t s : raw_samples) sum_sq += static_cast<double>(s) * static_cast<double>(s);
        raw_rms = std::sqrt(sum_sq / static_cast<double>(raw_samples.size()));
    }
    std::string json = "{"
        "\"seg_id\":" + std::to_string(seg_id) + ","
        "\"sample_rate\":16000,"
        "\"sample_count\":" + std::to_string(raw_samples.size()) + ","
        "\"duration_ms\":" + std::to_string(static_cast<int>(raw_samples.size() * 1000.0 / 16000.0)) + ","
        "\"rms\":" + std::to_string(raw_rms) + ","
        "\"peak\":" + std::to_string(raw_peak) + ","
        "\"capture_rms\":" + std::to_string(capture_rms) + ",";
    if (filtered_samples && !filtered_samples->empty()) {
        double f_peak = 0.0;
        for (int16_t s : *filtered_samples) {
            double v = std::abs(static_cast<double>(s));
            if (v > f_peak) f_peak = v;
        }
        double f_rms = 0.0;
        double sum_sq = 0.0;
        for (int16_t s : *filtered_samples) sum_sq += static_cast<double>(s) * static_cast<double>(s);
        f_rms = std::sqrt(sum_sq / static_cast<double>(filtered_samples->size()));
        json += "\"filtered_rms\":" + std::to_string(f_rms) + ",";
        json += "\"filtered_peak\":" + std::to_string(f_peak) + ",";
    }
    json += "\"vad_state\":\"" + json_escape(vad_state) + "\","
        "\"transcript\":\"" + json_escape(transcript) + "\","
        "\"latency_ms\":" + std::to_string(latency_ms) + ","
        "\"engine\":\"" + json_escape(engine) + "\","
        "\"decision_reason\":\"" + json_escape(reason) + "\""
        "}";
    auto fs_path = utf8_to_path(path);
    std::ofstream ofs(fs_path, std::ios::binary);
    ofs << json;
    ofs.close();
}

struct MoonshineProductPreset {
    int leading_padding_ms;
    int tail_padding_ms;
    double normalize_dbfs;
    bool without_streaming;
    int passes;
    std::string prefilter;
    int trim_abs_threshold;
    int trim_pad_ms;
    double vad_window_sec;
    int vad_look_behind_samples;
    double vad_threshold;
    double max_tokens_per_second;
};

static MoonshineProductPreset get_moonshine_product_preset(const std::string& language, const std::string& scene) {
    const std::string effective_scene = scene == "auto" ? "short_command" : scene;
    if (language == "ja") {
        if (effective_scene == "talk_clean") return {300, 300, -22.8, false, 3, "off", 300, 120, 0.15, 20000, 0.30, 13.0};
        if (effective_scene == "talk_noisy") return {320, 420, -22.8, true, 3, "off", 300, 160, 0.15, 26000, 0.34, 13.0};
        if (effective_scene == "talk_low_voice") return {360, 480, -26.0, true, 3, "off", 90, 300, 0.12, 32000, 0.24, 13.0};
        if (effective_scene == "short_command") return {600, 600, -22.8, true, 3, "normal", 0, 0, 0.15, 20000, 0.30, 6.5};
        if (effective_scene == "desktop_hearing") return {300, 450, -22.8, true, 3, "off", 300, 180, 0.18, 22000, 0.32, 13.0};
    }
    if (language == "en") {
        if (effective_scene == "talk_clean") return {300, 300, -22.8, false, 2, "normal", 300, 120, 0.15, 20000, 0.30, 6.5};
        if (effective_scene == "talk_noisy") return {320, 420, -22.8, true, 3, "strong", 300, 160, 0.15, 26000, 0.34, 6.5};
        if (effective_scene == "talk_low_voice") return {360, 480, -26.0, true, 3, "normal", 300, 180, 0.12, 32000, 0.24, 6.5};
        if (effective_scene == "short_command") return {420, 520, -23.4, true, 3, "normal", 180, 420, 0.10, 32000, 0.26, 6.5};
        if (effective_scene == "desktop_hearing") return {300, 450, -22.8, true, 3, "strong", 300, 180, 0.18, 22000, 0.32, 6.5};
    }
    if (language == "zh") {
        if (effective_scene == "talk_clean") return {420, 620, -23.4, true, 4, "normal", 180, 420, 0.16, 24000, 0.27, 16.0};
        if (effective_scene == "talk_noisy") return {620, 900, -22.8, true, 4, "normal", 300, 260, 0.16, 30000, 0.31, 16.0};
        if (effective_scene == "talk_low_voice") return {520, 820, -26.0, true, 4, "normal", 90, 300, 0.14, 34000, 0.23, 16.0};
        if (effective_scene == "short_command") return {460, 760, -23.4, true, 4, "normal", 180, 420, 0.12, 36000, 0.24, 16.0};
        if (effective_scene == "desktop_hearing") return {700, 1100, -22.8, true, 4, "off", 300, 340, 0.18, 28000, 0.29, 16.0};
    }
    if (language == "ko") {
        if (effective_scene == "talk_clean") return {480, 760, -22.8, true, 4, "normal", 300, 240, 0.16, 26000, 0.27, 13.0};
        if (effective_scene == "talk_noisy") return {500, 840, -22.8, true, 4, "normal", 300, 280, 0.16, 32000, 0.31, 13.0};
        if (effective_scene == "talk_low_voice") return {620, 960, -26.0, true, 4, "normal", 90, 320, 0.14, 36000, 0.23, 13.0};
        if (effective_scene == "short_command") return {700, 980, -23.4, true, 4, "off", 180, 420, 0.12, 36000, 0.23, 13.0};
        if (effective_scene == "desktop_hearing") return {520, 840, -22.8, true, 4, "normal", 300, 280, 0.18, 30000, 0.28, 13.0};
    }
    return {300, 300, -22.8, false, 1, "normal", 300, 200, 0.15, 20000, 0.30, 6.5};
}

static std::pair<std::string, int> resolve_moonshine_product_model(const std::string& root_or_model,
                                                                    const std::string& language,
                                                                    int default_arch) {
    namespace fs = std::filesystem;
    fs::path root(root_or_model);
    if (!fs::exists(root / "en") && !fs::exists(root / "ja") &&
        !fs::exists(root / "zh") && !fs::exists(root / "ko") && !fs::exists(root / "vi")) {
        return {root_or_model, default_arch};
    }
    if (language == "ja") return {(root / "ja" / "quantized" / "base-ja").string(), mobmate::MoonshineBridge::ARCH_BASE};
    if (language == "zh") return {(root / "zh" / "quantized" / "base-zh").string(), mobmate::MoonshineBridge::ARCH_BASE};
    if (language == "ko") return {(root / "ko" / "quantized" / "tiny-ko").string(), mobmate::MoonshineBridge::ARCH_TINY};
    if (language == "vi") return {(root / "vi" / "quantized" / "base-vi").string(), mobmate::MoonshineBridge::ARCH_BASE};
    return {(root / "en" / "quantized").string(), mobmate::MoonshineBridge::ARCH_BASE_STREAMING};
}

static char* dup_string(const std::string& s) {
    char* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (p) {
        std::memcpy(p, s.data(), s.size() + 1);
    }
    return p;
}

/* ---- Minimal JSON string value extractor (no external dependency) ---- */
/* Finds "key": "value" in a flat JSON object and returns the value. */
static std::string json_get_string(const char* json, const char* key,
                                    const char* default_val) {
    std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    // Skip whitespace and colon
    while (*pos == ' ' || *pos == ':' || *pos == '\t') pos++;
    if (*pos != '"') return default_val;
    pos++; // skip opening quote
    const char* end = std::strstr(pos, "\"");
    if (!end) return default_val;
    return std::string(pos, static_cast<size_t>(end - pos));
}

/* Finds "key": N or "key": true/false */
static int json_get_int(const char* json, const char* key, int default_val) {
    std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t') pos++;
    if (*pos == '"') {
        pos++;
        return std::stoi(std::string(pos, static_cast<size_t>(std::strcspn(pos, "\""))));
    }
    return std::stoi(pos);
}

static bool json_get_bool(const char* json, const char* key, bool default_val) {
    std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t') pos++;
    if (std::strncmp(pos, "true", 4) == 0) return true;
    if (std::strncmp(pos, "false", 5) == 0) return false;
    return default_val;
}

/* ---- exported C ABI ---- */

MMV2_API int mmv2_api_version(void) {
    return 1;
}

MMV2_API const char* mmv2_native_version(void) {
    return mobmate::VERSION;
}

MMV2_API void mmv2_free_string(const char* p) {
    std::free(const_cast<char*>(reinterpret_cast<const char*>(p)));
}

MMV2_API int mmv2_audio_input_devices(const char** out_json) {
    if (!out_json) return -1;
    *out_json = nullptr;
    try {
#ifdef _WIN32
    std::string err;
    auto devices = mobmate::enumerate_capture_devices(err);
    std::string json = "{\"ok\":" + std::string(err.empty() ? "true" : "false") + ",\"devices\":[";
    for (size_t i = 0; i < devices.size(); ++i) {
        if (i > 0) json += ",";
        json += "{\"id\":\"" + json_escape(devices[i].id) + "\",\"name\":\"" + json_escape(devices[i].name) + "\",\"isDefault\":" + std::string(devices[i].is_default ? "true" : "false") + "}";
    }
    json += "]";
    if (!err.empty()) json += ",\"error\":\"" + json_escape(err) + "\"";
    json += "}";
    *out_json = dup_string(json);
    return err.empty() ? 0 : -1;
#else
    std::string err;
    auto devices = mobmate::enumerate_capture_devices(err);
    std::string json = "{\"ok\":" + std::string(err.empty() ? "true" : "false") + ",\"devices\":[";
    for (size_t i = 0; i < devices.size(); ++i) {
        if (i > 0) json += ",";
        json += "{\"id\":\"" + json_escape(devices[i].id)
              + "\",\"name\":\"" + json_escape(devices[i].name)
              + "\",\"isDefault\":" + std::string(devices[i].is_default ? "true" : "false") + "}";
    }
    json += "]";
    if (!err.empty()) json += ",\"error\":\"" + json_escape(err) + "\"";
    json += "}";
    *out_json = dup_string(json);
    return err.empty() ? 0 : -1;
#endif
    } catch (const std::exception& ex) {
        *out_json = dup_string("{\"ok\":false,\"devices\":[],\"error\":\"" + json_escape(ex.what()) + "\"}");
        return -2;
    } catch (...) {
        *out_json = dup_string("{\"ok\":false,\"devices\":[],\"error\":\"unknown device enumeration failure\"}");
        return -3;
    }
}

/* ---- Shared transcription core (no WAV file I/O) ---- */
static int transcribe_pcm16_core(
    const std::vector<int16_t>& samples,
    int sample_rate_hz,
    const char* options_json_utf8,
    const char** out_json)
{
    /* Parse options */
    std::string model_path = json_get_string(options_json_utf8, "modelPath", "");
    std::string whisper_dll_dir = json_get_string(options_json_utf8, "whisperDllDir", "");
    std::string language = json_get_string(options_json_utf8, "language", "");
    std::string engine_name = json_get_string(options_json_utf8, "engine", "whisper");
    std::string moonshine_dll_dir = json_get_string(options_json_utf8, "moonshineDllDir", "");
    std::string moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelPath", "");
    if (moonshine_model_path.empty()) {
        moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelRoot", "");
    }
    int threads = json_get_int(options_json_utf8, "threads", 4);
    bool use_gpu = json_get_bool(options_json_utf8, "useGpu", false);
    bool use_prefilter = json_get_bool(options_json_utf8, "usePrefilter", true);
    std::string prefilter_profile = json_get_string(options_json_utf8, "prefilterProfile", "auto");
    std::string scene = json_get_string(options_json_utf8, "scene", "");

    if (engine_name == "moonshine" && (moonshine_dll_dir.empty() || moonshine_model_path.empty() || language.empty())) {
        *out_json = dup_string(make_error_json(
            "moonshineDllDir, moonshineModelPath/moonshineModelRoot, and language are required", "load"));
        return -1;
    }
    if (engine_name != "moonshine" && (model_path.empty() || whisper_dll_dir.empty())) {
        *out_json = dup_string(make_error_json(
            "modelPath and whisperDllDir are required", "load"));
        return -1;
    }

    if (is_near_silence_pcm16(samples)) {
        *out_json = dup_string(make_no_speech_json(language));
        return 0;
    }

    if (engine_name == "moonshine") {
        auto preset = get_moonshine_product_preset(language, scene);
        mobmate::MoonshineBridge bridge;
        if (!bridge.init(moonshine_dll_dir)) {
            *out_json = dup_string(make_error_json("failed to load moonshine.dll", "load"));
            return -1;
        }
        int default_arch = json_get_int(options_json_utf8, "moonshineArch", mobmate::MoonshineBridge::ARCH_TINY);
        auto model_choice = resolve_moonshine_product_model(moonshine_model_path, language, default_arch);
        mobmate::MoonshineBridge::LoadOptions load_options;
        load_options.vad_window_duration = preset.vad_window_sec;
        load_options.vad_look_behind_sample_count = preset.vad_look_behind_samples;
        load_options.vad_threshold = preset.vad_threshold;
        load_options.max_tokens_per_second = preset.max_tokens_per_second;
        if (!bridge.load_model(model_choice.first, model_choice.second, load_options)) {
            *out_json = dup_string(make_error_json("Moonshine model load failed: " + model_choice.first, "load"));
            return -1;
        }

        const std::vector<int16_t>* use_samples = &samples;
        std::vector<int16_t> trimmed_samples;
        std::vector<int16_t> filtered_samples;
        std::vector<int16_t> normalized_samples;
        if (preset.trim_abs_threshold > 0) {
            auto pcm_bytes = pcm16_to_bytes(*use_samples);
            auto trimmed = mobmate::trim_silence(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()),
                                                 sample_rate_hz, preset.trim_abs_threshold, preset.trim_pad_ms + 80);
            if (!trimmed.empty()) {
                trimmed_samples = bytes_to_pcm16(trimmed);
                use_samples = &trimmed_samples;
            }
        }
        if (use_prefilter && preset.prefilter != "off") {
            filtered_samples = apply_whisper_prefilter(*use_samples, scene, preset.prefilter, sample_rate_hz);
            use_samples = &filtered_samples;
        }
        auto pcm_bytes = pcm16_to_bytes(*use_samples);
        double effective_normalize_dbfs = std::min<double>(-7.0, preset.normalize_dbfs + 20.0);
        auto normalized = mobmate::normalize_final_chunk_for_asr(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()),
                                                                 effective_normalize_dbfs);
        if (!normalized.empty() && normalized.size() == pcm_bytes.size()) {
            normalized_samples = bytes_to_pcm16(normalized);
            use_samples = &normalized_samples;
        }
        auto float_samples = pcm16_to_float32(*use_samples);

        auto start = std::chrono::steady_clock::now();
        std::string best_text;
        bool success = false;
        int passes = preset.passes < 1 ? 1 : (preset.passes > 6 ? 6 : preset.passes);
        for (int p = 0; p < passes; ++p) {
            auto results = bridge.transcribeAccurate(float_samples.data(), static_cast<int>(float_samples.size()),
                                                     sample_rate_hz, preset.leading_padding_ms, preset.tail_padding_ms);
            if (!results.empty() && results[0].success && !results[0].text.empty()) {
                best_text = results[0].text;
                success = true;
                break;
            }
        }
        auto end = std::chrono::steady_clock::now();
        int latency_ms = static_cast<int>(
            std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
        if (!success) {
            *out_json = dup_string(make_error_json("moonshine transcription failed", "transcribe"));
            return -1;
        }
        *out_json = dup_string(make_success_json(best_text, latency_ms, "moonshine", language));
        return 0;
    }

    /* Load Whisper engine.
     * GPU path is cached + serialized to avoid repeated Vulkan context
     * create/free crashes in one-shot Hearing transcription.
     */
    std::string err_msg;
    std::unique_lock<std::mutex> gpu_lock;
    std::unique_ptr<mobmate::WhisperEngine> local_engine;
    mobmate::WhisperEngine* engine = nullptr;
    if (use_gpu) {
        gpu_lock = std::unique_lock<std::mutex>(g_whisper_gpu_oneshot.mutex);
        engine = get_cached_whisper_gpu_engine_locked(whisper_dll_dir, model_path, err_msg);
    } else {
        local_engine = mobmate::WhisperEngine::load(whisper_dll_dir, model_path, err_msg, false);
        engine = local_engine.get();
    }
    if (!engine) {
        *out_json = dup_string(make_error_json(err_msg, "load"));
        return -1;
    }

    const std::vector<int16_t>* use_samples = &samples;
    std::vector<int16_t> filtered_samples;
    if (use_prefilter) {
        filtered_samples = apply_whisper_prefilter(samples, scene, prefilter_profile, sample_rate_hz);
        use_samples = &filtered_samples;
    }

    /* Convert int16 PCM to float32 */
    auto float_samples = pcm16_to_float32(*use_samples);

    /* Transcribe */
    auto start = std::chrono::steady_clock::now();
    auto transcript = engine->transcribe(float_samples.data(),
                                           static_cast<int>(float_samples.size()),
                                           language, threads);
    auto end = std::chrono::steady_clock::now();
    int latency_ms = static_cast<int>(
        std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

    if (!transcript) {
        *out_json = dup_string(make_error_json("transcription failed", "transcribe"));
        return -1;
    }

    *out_json = dup_string(make_success_json(*transcript, latency_ms, "whisper", language));
    return 0;
}

/* ---- mmv2_transcribe_wav: thin wrapper around transcribe_pcm16_core ---- */
MMV2_API int mmv2_transcribe_wav(const char* wav_path_utf8,
                                   const char* options_json_utf8,
                                   const char** out_json) {
    if (!wav_path_utf8 || !options_json_utf8 || !out_json) {
        *out_json = dup_string(make_error_json("null argument", "unknown"));
        return -1;
    }
    std::string wav_err;
    auto samples_opt = mobmate::read_wav_samples(wav_path_utf8, wav_err);
    if (!samples_opt) {
        *out_json = dup_string(make_error_json(wav_err, "wav"));
        return -1;
    }
    std::string meta_err;
    auto wav_meta = mobmate::read_wav_metadata(wav_path_utf8, meta_err);
    int sample_rate_hz = wav_meta ? static_cast<int>(wav_meta->sample_rate) : 16000;
    return transcribe_pcm16_core(*samples_opt, sample_rate_hz, options_json_utf8, out_json);
}

/* ---- mmv2_transcribe_pcm16: transcribe raw PCM without WAV file I/O ---- */
MMV2_API int mmv2_transcribe_pcm16(const int16_t* pcm16,
                                    int count,
                                    int sample_rate_hz,
                                    const char* options_json_utf8,
                                    const char** out_json) {
    if (!pcm16 || count <= 0 || !options_json_utf8 || !out_json) {
        if (out_json) *out_json = dup_string(make_error_json("null/empty argument", "unknown"));
        return -1;
    }
    std::vector<int16_t> samples(pcm16, pcm16 + count);
    return transcribe_pcm16_core(samples, sample_rate_hz, options_json_utf8, out_json);
}

/* ---- Realtime session with VAD and PCM chunking ---- */

enum class RealtimeState {
    kIdle,
    kRecording,
    kProcessing,
    kCompleted,
    kFailed,
};

static const char* realtime_state_to_string(RealtimeState state) {
    switch (state) {
        case RealtimeState::kIdle:       return "idle";
        case RealtimeState::kRecording:  return "recording";
        case RealtimeState::kProcessing: return "processing";
        case RealtimeState::kCompleted:  return "completed";
        case RealtimeState::kFailed:     return "failed";
    }
    return "idle";
}

// Simple RMS-based VAD state
enum class VadState {
    kSilence,
    kSpeech,
    kEndDetected,
};

static const char* vad_state_to_string(VadState state) {
    switch (state) {
        case VadState::kSilence:      return "silence";
        case VadState::kSpeech:       return "speech";
        case VadState::kEndDetected:  return "end_detected";
    }
    return "silence";
}

// Phase 6F-2: Adaptive noise profile collector
struct RealtimeNoiseProfile {
    // Rolling silence-frame samples (circular buffer)
    static constexpr int MAX_SAMPLES = 200; // ~6.4s of silence frames @ 512/16k
    std::vector<double> rms_samples;
    std::vector<double> peak_samples;
    std::vector<double> silero_prob_samples;
    int write_idx = 0;
    int count = 0;

    // Derived metrics
    double noise_floor_rms = 0.0;
    double noise_rms_stddev = 0.0;
    double peak_p95 = 0.0;
    double silero_p90 = 0.0;
    bool low_gain_mic = false;
    bool ready = false;

    // Timing
    double last_trace_time_ms = 0.0;

    void reset() {
        rms_samples.clear();
        peak_samples.clear();
        silero_prob_samples.clear();
        write_idx = 0;
        count = 0;
        noise_floor_rms = 0.0;
        noise_rms_stddev = 0.0;
        peak_p95 = 0.0;
        silero_p90 = 0.0;
        low_gain_mic = false;
        ready = false;
        last_trace_time_ms = 0.0;
    }

    void add_sample(double rms, double peak, double silero_prob) {
        if (rms <= 0.0 || silero_prob < 0.0) return;
        if (static_cast<int>(rms_samples.size()) < MAX_SAMPLES) {
            rms_samples.push_back(rms);
            peak_samples.push_back(peak);
            silero_prob_samples.push_back(silero_prob);
        } else {
            rms_samples[write_idx] = rms;
            peak_samples[write_idx] = peak;
            silero_prob_samples[write_idx] = silero_prob;
        }
        write_idx = (write_idx + 1) % MAX_SAMPLES;
        count++;
    }

    void recompute() {
        int n = static_cast<int>(rms_samples.size());
        if (n < 10) { // need minimum samples
            ready = false;
            return;
        }
        ready = true;

        // RMS mean and stddev
        double sum = 0.0, sum_sq = 0.0;
        for (int i = 0; i < n; i++) {
            sum += rms_samples[i];
            sum_sq += rms_samples[i] * rms_samples[i];
        }
        double mean = sum / n;
        double variance = (sum_sq / n) - (mean * mean);
        if (variance < 0.0) variance = 0.0;

        noise_floor_rms = mean;
        noise_rms_stddev = std::sqrt(variance);

        // Peak P95: sort copy and pick 95th percentile
        std::vector<double> peaks_sorted = peak_samples;
        std::sort(peaks_sorted.begin(), peaks_sorted.end());
        int p95_idx = static_cast<int>(std::ceil(0.95 * n)) - 1;
        if (p95_idx < 0) p95_idx = 0;
        if (p95_idx >= n) p95_idx = n - 1;
        peak_p95 = peaks_sorted[p95_idx];

        // Silero P90
        std::vector<double> probs_sorted = silero_prob_samples;
        std::sort(probs_sorted.begin(), probs_sorted.end());
        int p90_idx = static_cast<int>(std::ceil(0.90 * n)) - 1;
        if (p90_idx < 0) p90_idx = 0;
        if (p90_idx >= n) p90_idx = n - 1;
        silero_p90 = probs_sorted[p90_idx];

        // Low gain mic detection: if noise floor RMS is very low (< 30)
        low_gain_mic = (noise_floor_rms < 30.0);
    }
};

static std::string resolve_realtime_auto_scene(const std::string& requested_scene,
                                               const PcmStats* stats,
                                               const RealtimeNoiseProfile* noise_profile) {
    if (requested_scene != "auto") return requested_scene;
    if (!stats) return "short_command";
    if (stats->duration_ms > 0.0 && stats->duration_ms < 4500.0) {
        return "short_command";
    }
    if (noise_profile && noise_profile->ready &&
        (noise_profile->noise_floor_rms > 300.0 || noise_profile->silero_p90 > 0.08)) {
        return "talk_noisy";
    }
    if ((stats->rms > 0.0 && stats->rms < 700.0) || (stats->peak > 0 && stats->peak < 4500)) {
        return "talk_low_voice";
    }
    return "talk_clean";
}

static double default_realtime_vad_silence_ms(const std::string& scene) {
    (void)scene;
    return 1000.0;
}

static constexpr double REALTIME_SPECULATIVE_FINAL_DELAY_MS = 450.0;

// Phase 7A: Speaker identification global state
static bool gSpeakerIdEnabled = true;  // enabled by default per spec
static std::string gSpeakerProfilePath;
static SpeakerProfile* gActiveSpeakerProfile = nullptr;

#if defined(_WIN32) || defined(__linux__)
struct RealtimeWhisperValidationJob {
    uint64_t generation = 0;
    int seg_id = -1;
    int64_t final_detected_at_ms = 0;
    std::vector<int16_t> pcm_samples;
    PcmStats stats;
    double vad_start_prob = -1.0;
    std::string language;
    std::string effective_scene;
    std::string prefilter_profile;
    std::string initial_prompt;
    int threads = 4;
    bool use_prefilter = false;
    bool prepare_windows_input = false;
};

struct RealtimeWhisperValidationResult {
    uint64_t generation = 0;
    int seg_id = -1;
    int64_t final_detected_at_ms = 0;
    std::vector<int16_t> pcm_samples;
    std::string text;
    std::string reason;
    int latency_ms = 0;
    float avg_token_p = -1.0f;
    float max_no_speech_prob = -1.0f;
    float confidence_score = -1.0f;
    bool accepted = false;
};
#endif

#if defined(_WIN32) || defined(__linux__)
struct JointCandidateTrace {
    bool attempted = false;
    bool success = false;
    bool accepted = false;
    std::string text;
    std::string reason = "not_run";
    int64_t latency_ms = -1;
};
#endif

struct RealtimeSession {
    RealtimeState state = RealtimeState::kIdle;
    std::string partial;
    std::string final_text;
    int latency_ms = 0;
    std::string error;
#ifdef _WIN32
    mobmate::WasapiCapture capture;
#else
    mobmate::MiniAudioCapture capture;
#endif
    bool capture_initialized = false;

    // VAD state
    VadState vad_state = VadState::kSilence;
    double vad_rms_threshold = 500.0; // RMS threshold for speech detection (16-bit PCM)
    double vad_silence_duration_ms = 800.0; // Silence duration to mark end of speech
    double vad_elapsed_ms = 0.0;
    double vad_last_speech_time_ms = 0.0;
    double vad_session_start_ms = 0.0;
    // Phase 6F-2: Adaptive noise profile
    RealtimeNoiseProfile noise_profile;
    // Phase 6F-5: Input auto gain (low-gain mic rescue, max 2.0)
    double input_auto_gain = 1.0;
    // SileroVAD
    silero_vad_handle* vad_silero = nullptr;
    std::string vad_silero_model_path;
    std::string vad_silero_ort_dll_dir;
    /* P0 2026-07-14: defaults unified with Linux (was 0.42/0.30/0.20 on Windows
     * because the permille options were only parsed on the Linux path). The
     * lower start threshold let prob 0.42-0.50 noise (drink/contact sounds)
     * open segments that Whisper then hallucinated into polite closing phrases. */
    double vad_silero_start_threshold = 0.55;
    double vad_silero_continue_threshold = 0.40;
    double vad_silero_soft_start_threshold = 0.30;
    double vad_start_prob = 0.0; // Silero prob of the frame that opened the current segment
    int vad_soft_start_frames = 0;
    int64_t vad_soft_start_sample = -1;

    // PCM chunk info
    int64_t total_samples = 0;
    int64_t chunk_samples = 0;
    int64_t partial_last_sample_count = 0;
    int64_t partial_speech_start_sample = -1;
    int64_t vad_end_sample = -1;
    int64_t final_detected_at_ms = 0;
    int vad_speech_count = 0; // Number of speech chunks detected
    std::mutex vad_callback_mutex;
    mmv2_vad_start_callback vad_start_callback = nullptr;
    void* vad_start_user_data = nullptr;
    uint64_t vad_start_sequence = 0;
    // Phase 6D-1: V1-style ASR pre-roll buffer (capped, appended during non-speech)
    std::vector<int16_t> asr_pre_roll;
    static constexpr int ASR_PRE_ROLL_MAX_SAMPLES = 16000; // ~1s @ 16kHz; avoid losing utterance heads on late VAD start
    bool auto_transcribing = false;
    bool partial_transcribing = false;
    bool has_transcript = false;
    bool speculative_final_attempted = false;
    int dump_seg_counter = 0;
    std::vector<int16_t> last_final_pcm16;
    int last_final_pcm_sample_rate = 16000;
    bool last_final_pcm_reported = false;
    /* 2026-07-14: decode-time Whisper confidence of the latest whisper final.
     * Reported in poll JSON (confidence_available/confidence_score) so the C#
     * AdaptiveVocab gate (>= 0.80) can function. -1 = unavailable. */
    float whisper_avg_token_p = -1.0f;
    float whisper_max_no_speech_prob = -1.0f;
    float whisper_confidence_score = -1.0f;

    // ASR engine state (non-platform-specific metadata)
    std::string realtime_engine = "whisper";
    std::string selected_engine = "none";
    std::string decision_reason;
    std::string language;
    std::string scene;
    std::string resolved_scene;

    // ASR engine (loaded at create time if options provided)
#if defined(_WIN32) || defined(__linux__)
    std::unique_ptr<mobmate::WhisperEngine> engine;
    std::string engine_error;
    int threads = 8;  /* Phase 6B: match V1 default (max(8, CPUs/2)) */
    bool use_gpu = false;
    std::mutex whisper_validation_mutex;
    std::condition_variable whisper_validation_cv;
    std::thread whisper_validation_thread;
    std::deque<RealtimeWhisperValidationJob> whisper_validation_jobs;
    std::deque<RealtimeWhisperValidationResult> whisper_validation_results;
    bool whisper_validation_shutdown = false;
    bool whisper_validation_active = false;
    uint64_t whisper_validation_generation = 0;
#endif
#if defined(_WIN32) || defined(__linux__)
    bool addon_joint_requested = false;
    std::string addon_joint_language = "ja";
    std::string addon_engine_id = "haas_ja";
    std::string addon_language_mode = "requested";
    std::string addon_model_id;
    std::string addon_model_revision;
    bool production_replay = false;
    bool joint_collecting = false;
    bool joint_arbitration_permitted = false;
    JointCandidateTrace joint_moonshine;
    JointCandidateTrace joint_whisper;
    std::vector<std::string> joint_execution_order;
    std::string joint_existing_status;
    std::string joint_addon_status;
    std::string joint_selection_reason;
    int64_t joint_existing_wall_ms = -1;
    int64_t joint_total_wall_ms = -1;
    int64_t joint_added_wait_ms = -1;
    bool addon_candidate_attempted = false;
    bool addon_candidate_success = false;
    std::string addon_candidate_text;
    int64_t addon_candidate_ms = -1;
    int64_t addon_candidate_wall_ms = -1;
    int64_t addon_wall_ms = -1;
    bool copy_final_to_clipboard = false;
#endif
#ifdef _WIN32
    bool experimental_asr_candidates_enabled = false;
    std::string experimental_asr_candidate_engine;
    std::string experimental_asr_candidate_language;
    std::string experimental_asr_candidate_model_dir;
    int paraformer_zh_candidate_seg_id = -1;
    std::string paraformer_zh_candidate_text;
    int paraformer_zh_candidate_latency_ms = -1;
    std::string paraformer_zh_candidate_error;
    std::string initial_prompt;  /* Phase 6B: initial prompt for whisper */
    bool use_prefilter = true;
    std::string prefilter_profile = "auto";
    // Phase 6B debug dump
    std::string dump_dir;
    // realtime_trace rotation (date-based)
    std::ofstream trace_file;
    int64_t trace_file_date = 0; // yyyymmdd of currently open trace file
#endif
    // Phase 14-1: Moonshine is supported on Windows and Linux
#if defined(_WIN32) || defined(__linux__)
    std::unique_ptr<mobmate::MoonshineBridge> moonshine;
    MoonshineProductPreset moonshine_preset{300, 300, -22.8, false, 1, "normal", 300, 200, 0.15, 20000, 0.30, 6.5};
    // Phase 6D-4: Moonshine candidate memory (safe, one-shot final results only)
    struct MoonshineCandidate {
        int seg_id = -1;
        std::string text;
        int latency_ms = 0;
        std::string source = "one_shot";
        /* 2026-07-14: Mobfy Task A port — multi-pass agreement pseudo-confidence.
         * Same audio decoded twice with different padding contexts; real speech
         * decodes stably, hallucinations flip. -1 = not measured. */
        float pseudo_confidence = -1.0f;
    };
    MoonshineCandidate moonshine_latest_candidate;
    MoonshineCandidate moonshine_peak_candidate;  // longest text so far
    MoonshineCandidate speculative_final_candidate;
    bool moonshine_stream_supported = false;
    bool moonshine_streaming = false;
    int64_t moonshine_stream_sample_count = 0;
#endif
    std::string capture_device_id; // used on both platforms
    // Phase 7A: Speaker identification
    std::unique_ptr<SpeakerProfile> speaker_profile;
    bool speaker_id_enabled = false;
    std::string speaker_profile_path;

    ~RealtimeSession() {
#if defined(_WIN32) || defined(__linux__)
        {
            std::lock_guard<std::mutex> lock(whisper_validation_mutex);
            whisper_validation_shutdown = true;
            whisper_validation_jobs.clear();
        }
        whisper_validation_cv.notify_all();
        if (whisper_validation_thread.joinable()) {
            whisper_validation_thread.join();
        }
#endif
#ifdef _WIN32
        if (trace_file.is_open()) {
            trace_file.close();
        }
        capture.release();
#else
        capture.release();
#endif
        if (vad_silero) {
            silero_vad_free(vad_silero);
            vad_silero = nullptr;
        }
        // Phase 7A: Save speaker profile on session destruction
        if (speaker_profile && !speaker_profile_path.empty()) {
            speaker_save(*speaker_profile, speaker_profile_path.c_str());
        }
        if (gActiveSpeakerProfile == speaker_profile.get()) {
            gActiveSpeakerProfile = nullptr;
        }
    }
};

static int64_t unix_epoch_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
}

#if defined(_WIN32) || defined(__linux__)
static bool realtime_whisper_validation_busy(RealtimeSession* session);
#endif

#ifdef _WIN32
#ifdef _WIN32
static int64_t realtime_trace_today_yyyymmdd() {
    auto now = std::chrono::system_clock::now();
    std::time_t tt = std::chrono::system_clock::to_time_t(now);
    struct tm tb;
    localtime_s(&tb, &tt);
    return (int64_t)(tb.tm_year + 1900) * 10000LL + (int64_t)(tb.tm_mon + 1) * 100LL + (int64_t)tb.tm_mday;
}

static void realtime_trace(RealtimeSession* session, const std::string& message) {
    if (!session || session->dump_dir.empty()) return;
    try {
        auto now_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch()).count();
        int64_t today = realtime_trace_today_yyyymmdd();
        // Rotate if date changed or file not yet opened
        if (session->trace_file_date != today) {
            if (session->trace_file.is_open()) {
                session->trace_file.close();
            }
            std::string fname = "realtime_trace_" + std::to_string(today) + ".log";
            std::filesystem::path path = std::filesystem::path(session->dump_dir) / fname;
            session->trace_file.open(path, std::ios::app);
            session->trace_file_date = today;
        }
        if (session->trace_file.is_open()) {
            session->trace_file << now_ms << " " << message << "\n";
            session->trace_file.flush();
        }
        OutputDebugStringA((message + "\n").c_str());
    } catch (...) {
    }
}
#endif // _WIN32 (realtime_trace)

static void update_noise_profile(RealtimeSession* session, double frame_rms, double frame_peak, double silero_prob, double current_time_ms) {
    if (!session) return;
    auto* profile = &session->noise_profile;
    profile->add_sample(frame_rms, frame_peak, silero_prob);
    profile->recompute();

    // Trace every ~5s when ready
    if (profile->ready && (current_time_ms - profile->last_trace_time_ms) >= 5000.0) {
        profile->last_trace_time_ms = current_time_ms;
        std::ostringstream oss;
        oss << "noise_profile ready=1"
            << " floor=" << profile->noise_floor_rms
            << " std=" << profile->noise_rms_stddev
            << " peak_p95=" << profile->peak_p95
            << " p90_silero=" << profile->silero_p90
            << " low_gain=" << (profile->low_gain_mic ? 1 : 0)
            << " samples=" << profile->count;
        realtime_trace(session, oss.str());
    }

    // Phase 6F-5: actual gain is decided per accepted speech segment.
    session->input_auto_gain = 1.0;
}

static double compute_safe_input_auto_gain(RealtimeSession* session, const PcmStats& stats) {
    if (!session || !session->noise_profile.ready) return 1.0;
    const auto& profile = session->noise_profile;
    if (stats.duration_ms < 450.0 || stats.rms < 180.0 || stats.rms > 900.0) return 1.0;
    if (stats.peak <= 0 || stats.peak > 12000) return 1.0;
    const double noise_reference = profile.noise_floor_rms + (profile.noise_rms_stddev * 1.5);
    const double snr = stats.rms / (std::max)(1.0, noise_reference);
    if (snr < 1.8) return 1.0;
    double gain = 1100.0 / stats.rms;
    if (gain < 1.05) return 1.0;
    if (gain > 1.6) gain = 1.6;
    if (static_cast<double>(stats.peak) * gain > 18000.0) return 1.0;
    return gain;
}

static void apply_gain_in_place(std::vector<int16_t>& samples, double gain) {
    if (gain <= 1.0) return;
    for (auto& s : samples) {
        int32_t boosted = static_cast<int32_t>(static_cast<double>(s) * gain);
        if (boosted > 32767) boosted = 32767;
        if (boosted < -32768) boosted = -32768;
        s = static_cast<int16_t>(boosted);
    }
}

static bool should_skip_stop_fallback_with_noise_profile(RealtimeSession* session, const PcmStats& stats) {
    if (!session || !session->noise_profile.ready) return false;
    if (session->vad_state != VadState::kSilence || session->vad_speech_count != 0) return false;
    if (stats.duration_ms < 2500.0) return false;
    const auto& profile = session->noise_profile;
    const double adaptive_limit = profile.noise_floor_rms + (std::max)(250.0, profile.noise_rms_stddev * 1.5);
    const double speech_peak_limit = (std::max)(2400.0, profile.peak_p95 + 900.0);
    const double speech_rms_limit = (std::max)(320.0,
        profile.noise_floor_rms + (std::max)(180.0, profile.noise_rms_stddev * 1.2));
    const bool strong_speech_like = stats.peak >= speech_peak_limit && stats.rms >= speech_rms_limit;
    return profile.silero_p90 < 0.12 && stats.rms < adaptive_limit && !strong_speech_like;
}

static bool should_skip_realtime_final_input_with_profile(RealtimeSession* session, const PcmStats& stats, std::string& reason) {
    if (should_skip_realtime_final_input(stats, reason)) return true;
    // V1 does not drop an accepted VAD segment just because its SNR is low.
    // The adaptive low_snr_noise_segment gate skipped audible short commands
    // such as "次の道を左へ" before ASR had any chance to recognize them.
    (void)session;
    return false;
}
#endif

static void notify_vad_start(RealtimeSession* session) {
    // Held only across the bounded enqueue callback, never across recognition.
    // Unregister returning is the context/delegate lifetime boundary.
    std::lock_guard<std::mutex> lock(session->vad_callback_mutex);
    const auto sequence = ++session->vad_start_sequence;
    if (session->vad_start_callback)
        session->vad_start_callback(sequence, session->vad_start_user_data);
}

static void update_vad_state(RealtimeSession* session, double current_rms, double current_time_ms) {
    // Simple RMS-based VAD
    if (current_rms > session->vad_rms_threshold) {
        // Speech detected
        if (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected) {
            session->vad_speech_count++;
            notify_vad_start(session);
            session->final_detected_at_ms = 0;
        }
        session->vad_state = VadState::kSpeech;
        session->vad_last_speech_time_ms = current_time_ms;
    } else {
        // Silence detected
        if (session->vad_state == VadState::kSpeech) {
            double silence_duration = current_time_ms - session->vad_last_speech_time_ms;
            if (silence_duration >= session->vad_silence_duration_ms) {
                session->vad_state = VadState::kEndDetected;
                session->final_detected_at_ms = unix_epoch_ms();
            }
        } else if (session->vad_state == VadState::kEndDetected) {
            // Already in end state, stay there
        } else {
            session->vad_state = VadState::kSilence;
        }
    }
}

static std::string sv_compare_key(const std::string& raw);
#ifdef _WIN32
static bool zh_tail_agree_prefer_pf(const std::string& selected_key,
                                    const std::string& moonshine_key,
                                    const std::string& pf_key);
static std::string pf_transcribe_pcm16(const std::vector<int16_t>& pcm16,
                                       const std::string& model_dir);
static std::string pf_default_model_dir();

static int transcribe_realtime_samples(RealtimeSession* session,
                                       const std::vector<int16_t>& pcm_samples,
                                       bool keep_recording, int seg_id,
                                       double capture_rms, const PcmStats* stats = nullptr);

static int transcribe_realtime_samples_impl(RealtimeSession* session,
                                             const std::vector<int16_t>& pcm_samples,
                                             bool keep_recording, int seg_id,
                                             double capture_rms,
                                           const PcmStats* stats) {
    const bool use_joint_auto = session->realtime_engine == "joint_auto";
    const bool use_moonshine = session->realtime_engine == "moonshine";
    const bool use_paraformer_zh = session->realtime_engine == "paraformer_zh";
    const bool use_addon = is_addon_engine(session->realtime_engine);
    const auto addon_wall_start = std::chrono::steady_clock::now();
    if (use_addon) {
        session->addon_candidate_attempted = false;
        session->addon_candidate_success = false;
        session->addon_candidate_text.clear();
        session->addon_candidate_ms = -1;
        session->addon_candidate_wall_ms = -1;
        session->addon_wall_ms = -1;
    }
    if (pcm_samples.empty()) return 0;
    if (use_joint_auto) {
        if (!session->moonshine || !session->engine) return 0;
    } else if (use_moonshine) {
        if (!session->moonshine) return 0;
    } else if (use_paraformer_zh) {
        if (session->language != "zh") return 0;
    } else if (use_addon) {
        // A lost callback must reach the fresh-segment failure path, not retain an old final.
        if (!addon_language_supported(session->addon_engine_id, session->language)) return -1;
    } else if (!session->engine) {
        return 0;
    }
    {
        std::ostringstream oss;
        oss << "asr_final_start keep=" << (keep_recording ? 1 : 0)
            << " seg=" << seg_id
            << " samples=" << pcm_samples.size()
            << " state=" << realtime_state_to_string(session->state)
            << " vad=" << vad_state_to_string(session->vad_state)
            << " capture_rms=" << capture_rms;
        realtime_trace(session, oss.str());
    }

    if (is_near_silence_pcm16(pcm_samples)) {
        realtime_trace(session, "asr_final_skip_near_silence");
        if (!session->has_transcript) {
            session->final_text.clear();
            session->selected_engine = "none";
            session->decision_reason = "no_speech";
            session->latency_ms = 0;
        }
        if (!keep_recording) session->state = RealtimeState::kCompleted;
        /* Phase 6B: dump metadata for silence segment */
        dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                              session->final_text, session->latency_ms,
                              session->selected_engine, session->decision_reason,
                              capture_rms, vad_state_to_string(session->vad_state),
                              nullptr);
        return 0;
    }

    session->last_final_pcm16 = pcm_samples;
    session->last_final_pcm_sample_rate = 16000;
    session->last_final_pcm_reported = false;

    if (use_joint_auto) {
        PcmStats local_stats{0.0, 0, 0.0, 0.0, 0};
        const PcmStats* joint_stats = stats;
        if (!joint_stats) {
            local_stats = compute_pcm_stats(pcm_samples);
            joint_stats = &local_stats;
        }
        const std::string effective_scene = resolve_realtime_auto_scene(
            session->scene, joint_stats, &session->noise_profile);
        session->resolved_scene = effective_scene;
        const bool hard_whisper_scene = effective_scene == "talk_low_voice" ||
            effective_scene == "talk_noisy" || effective_scene == "desktop_hearing";
        const bool moonshine_allowed_scene = effective_scene == "short_command" ||
            effective_scene == "talk_clean";
        /* Ultra-low SNR guard: when noise floor is extremely high (> 1200) and
         * segment RMS is at or below noise floor (ratio < 1.05, ≈ 0.4dB).
         * Only skip truly impossible segments (noise >= signal). Segments with
         * snr_ratio 1.05-1.2 are marginal but recoverable via joint agreement;
         * Fix E (noisy_environment validate) + Fix D (disagreement reject) handle
         * the quality gate for those cases. Only applies when noise_profile is ready. */
        if (session->noise_profile.ready) {
            const float effective_floor = (float)session->noise_profile.noise_floor_rms;
            const float snr_ratio = (effective_floor > 10.0f) ? (float)joint_stats->rms / effective_floor : 99.0f;
            if (effective_floor > 1200.0f && snr_ratio < 1.05f) {
                std::ostringstream oss;
                oss << "asr_skip_extreme_noise seg=" << seg_id
                    << " noise_floor=" << effective_floor
                    << " seg_rms=" << joint_stats->rms
                    << " snr_ratio=" << snr_ratio;
                realtime_trace(session, oss.str());
                session->final_text.clear();
                session->has_transcript = false;
                session->selected_engine = "none";
                session->decision_reason = "asr_skip_extreme_noise";
                session->latency_ms = 0;
                session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
                return 0;
            }
        }

        /* new segment boundary: clear stale final_text before rollback snapshot.
         * Without this, a previous segment's text (e.g. "ありがとうございます") stays
         * in session->final_text and gets saved as saved_final_text. When
         * joint_low_energy_disagreement_reject fires it restores the stale text,
         * C# GetNewFinalText sees no change, and TTS is never triggered. */
        session->final_text.clear();
        session->has_transcript = false;

        const std::string saved_final_text = session->final_text;
        const bool saved_has_transcript = session->has_transcript;
        const std::string saved_selected_engine = session->selected_engine;
        const std::string saved_decision_reason = session->decision_reason;
        const int saved_latency_ms = session->latency_ms;
        const RealtimeState saved_state = session->state;
        const std::string saved_error = session->error;
        const auto saved_moonshine_latest_candidate = session->moonshine_latest_candidate;
        const auto saved_moonshine_peak_candidate = session->moonshine_peak_candidate;

        int moon_ret = -1;
        std::string moon_final_text;
        std::string moon_segment_text;
        bool moon_has_transcript = false;
        int moon_latency_ms = -1;
        RealtimeState moon_state = saved_state;
        std::string moon_error;
        bool moon_success = false;
      auto moon_moonshine_latest_candidate = saved_moonshine_latest_candidate;
        auto moon_moonshine_peak_candidate = saved_moonshine_peak_candidate;
        bool moonshine_hard_failed = false;
        auto apply_paraformer_zh_candidate = [&](const char* context_reason) -> bool {
            if (session->language != "zh") return false;
            std::string pf_text;
            int pf_latency_ms = -1;
            if (session->paraformer_zh_candidate_seg_id == seg_id) {
                pf_text = session->paraformer_zh_candidate_text;
                pf_latency_ms = session->paraformer_zh_candidate_latency_ms;
            } else {
                const auto pf_start = std::chrono::steady_clock::now();
                pf_text = pf_transcribe_pcm16(pcm_samples, session->experimental_asr_candidate_model_dir);
                const auto pf_end = std::chrono::steady_clock::now();
                pf_latency_ms = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(pf_end - pf_start).count());
                session->paraformer_zh_candidate_seg_id = seg_id;
                session->paraformer_zh_candidate_text = pf_text;
                session->paraformer_zh_candidate_latency_ms = pf_latency_ms;
                session->paraformer_zh_candidate_error = pf_text.empty() ? "paraformer_empty_or_unavailable" : "";
            }
            if (pf_text.empty()) {
                realtime_trace(session, std::string("joint_zh_paraformer_unavailable context=") + context_reason);
                return false;
            }
            const std::string selected_key = sv_compare_key(session->final_text);
            const std::string pf_key = sv_compare_key(pf_text);
            const bool selected_empty = !session->has_transcript || session->final_text.empty() ||
                session->selected_engine.empty() || session->selected_engine == "none";
            const bool key_agree = !selected_key.empty() && selected_key == pf_key;
            const bool pf_tail_agree = session->selected_engine == "whisper" &&
                zh_tail_agree_prefer_pf(selected_key, sv_compare_key(moon_final_text), pf_key);
            if (!selected_empty && !key_agree && !pf_tail_agree) {
                realtime_trace(session, std::string("joint_zh_paraformer_rejected_by_agreement context=") + context_reason +
                    " selected_engine=" + session->selected_engine +
                    " selected=" + json_escape(session->final_text) +
                    " paraformer=" + json_escape(pf_text));
                return false;
            }
            session->final_text = pf_text;
            session->has_transcript = true;
            session->selected_engine = "paraformer_zh";
            session->decision_reason = selected_empty ? "joint_zh_paraformer_selected" :
                (pf_tail_agree ? "joint_zh_paraformer_tail_agree" : "joint_zh_paraformer_agree");
            session->latency_ms = pf_latency_ms;
            session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
            if (session->copy_final_to_clipboard) {
                mmv2_clipboard_set_text(session->final_text.c_str());
            }
            realtime_trace(session, std::string("joint_zh_paraformer_selected context=") + context_reason +
                " text=" + json_escape(pf_text) +
                " latency_ms=" + std::to_string(pf_latency_ms));
            return true;
        };
        /* Whisper validation is only needed for low-energy / low-SNR speech.
         * High-energy clear utterances (rms >= 600 AND peak >= 2500) are
         * recognizable by Moonshine alone; forcing Whisper on every segment
         * wastes ~300ms and prevents joint_v1_staged_fastpath_moonshine from
         * ever firing. Threshold chosen so that clear short commands like
         * "ありがとうございます" (rms ~870–1000, peak ~3000+) bypass validation
         * while navigation commands (rms ~300–740) still get Whisper. */
        const bool noisy_environment =
             !session->noise_profile.ready ||
             session->noise_profile.noise_floor_rms > 200.0;
        /* When SNR ratio is below 2.0, both engines hallucinate differently in
         * ultra-noisy environments (e.g. game BGM, noise_floor ~1800), making the
         * agreement gate unreliable. Skip Whisper validation and trust Moonshine alone. */
        const float snr_ratio_validate = (session->noise_profile.ready &&
             session->noise_profile.noise_floor_rms > 1.0f)
             ? (float)joint_stats->rms / (float)session->noise_profile.noise_floor_rms
             : 99.0f;
         /* Language-agnostic Whisper validation: when signal energy is moderate
          * or SNR is borderline, run Whisper to cross-check Moonshine's output.
          * Removed ja-only restriction to cover EN/KO/ZH as well. */
         const bool validate_low_energy_command_with_whisper =
              effective_scene == "short_command" &&
              joint_stats->active_duration_ms <= 4000.0 &&
              snr_ratio_validate >= 2.0f &&
              (noisy_environment || joint_stats->rms < 750.0 || joint_stats->peak < 2500);
         const float speculative_conf =
              session->speculative_final_candidate.seg_id == seg_id
              ? session->speculative_final_candidate.pseudo_confidence
              : -1.0f;
         const bool force_whisper_for_low_pseudo_conf =
              speculative_conf >= 0.0f && speculative_conf < 0.75f;

        if (!hard_whisper_scene && moonshine_allowed_scene && joint_stats->active_duration_ms <= 4000.0) {
            realtime_trace(session, "joint_auto_live_staged selector=joint-v1-staged-fastpath no_len_delta_check=1");
            std::ostringstream vtrace;
            vtrace << "joint_validate_check noisy_env=" << (noisy_environment ? 1 : 0)
                   << " validate=" << (validate_low_energy_command_with_whisper ? 1 : 0)
                   << " noise_floor=" << session->noise_profile.noise_floor_rms
                   << " snr_ratio=" << snr_ratio_validate
                   << " rms=" << joint_stats->rms
                   << " peak=" << joint_stats->peak;
            realtime_trace(session, vtrace.str());
            session->realtime_engine = "moonshine";
            moon_ret = transcribe_realtime_samples(session, pcm_samples, keep_recording, seg_id, capture_rms, joint_stats);
            session->realtime_engine = "joint_auto";
            moon_success = moon_ret == 0 && session->selected_engine == "moonshine" &&
                session->latency_ms <= 180;
            moon_final_text = session->final_text;
            if (session->moonshine_latest_candidate.seg_id == seg_id) {
                moon_segment_text = session->moonshine_latest_candidate.text;
            }
            if (moon_segment_text.empty() && !moon_final_text.empty()) {
                auto pos = moon_final_text.rfind('\n');
                moon_segment_text = (pos == std::string::npos) ? moon_final_text : moon_final_text.substr(pos + 1);
            }
            moon_has_transcript = session->has_transcript;
            moon_latency_ms = session->latency_ms;
            moon_state = session->state;
            moon_error = session->error;
           /* Save Moonshine-run candidate snapshot for use in low-SNR fallback below.
             * Must be captured here, BEFORE state is restored to saved_* values. */
            moon_moonshine_latest_candidate = session->moonshine_latest_candidate;
            moon_moonshine_peak_candidate = session->moonshine_peak_candidate;
            /* Detect Moonshine hard failure: the function returned 0 (API success) but
             * internally failed to produce text (selected_engine=="none").
             * Note: session->error may be empty when keep_recording=1 (Line 1617-1618),
             * so we rely on selected_engine=="none" which is always set on failure. */
            moonshine_hard_failed = (moon_ret == 0 && !moon_success &&
                session->selected_engine == "none");
            /* A laughter tail can yield a stable speculative non-verbal token
             * (e.g. フフフ。) but no one-shot Moonshine final. Falling through to
             * Whisper then blocks the realtime poll/capture loop for ~5.8s and
             * queues the user's next utterance behind it. The speculative text
             * is enough to identify this as non-verbal; drop only that segment
             * instead of running synchronous Whisper fallback. */
            const bool speculative_laughter_tail =
                moonshine_hard_failed &&
                session->speculative_final_candidate.seg_id == seg_id &&
                is_laughter_like(session->speculative_final_candidate.text);
            if (speculative_laughter_tail) {
                std::ostringstream oss;
                oss << "joint_laughter_tail_skip_whisper seg=" << seg_id
                    << " duration_ms=" << joint_stats->active_duration_ms
                    << " text=" << json_escape(session->speculative_final_candidate.text);
                realtime_trace(session, oss.str());
                session->final_text = saved_final_text;
                session->has_transcript = saved_has_transcript;
                session->selected_engine = saved_selected_engine;
                session->decision_reason = "joint_laughter_tail_skip_whisper";
                session->latency_ms = saved_latency_ms;
                session->state = saved_state;
                session->error = saved_error;
                session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
                session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
                return 0;
            }
            /* Realtime poll must not block capture for several seconds after
             * Moonshine has already failed. Recent live traces show these
             * Whisper fallbacks taking 4.8-5.7s and then being rejected by the
             * existing hallucination/stall gates. Keep offline/explicit final
             * transcription unchanged, but drop this realtime segment before
             * starting synchronous Whisper. */
            if (keep_recording && moonshine_hard_failed) {
                std::ostringstream oss;
                oss << "joint_realtime_moonshine_failed_skip_whisper seg=" << seg_id
                    << " duration_ms=" << joint_stats->active_duration_ms;
                realtime_trace(session, oss.str());
                session->final_text = saved_final_text;
                session->has_transcript = saved_has_transcript;
                session->selected_engine = saved_selected_engine;
                session->decision_reason = "joint_realtime_moonshine_failed_skip_whisper";
                session->latency_ms = saved_latency_ms;
                session->state = saved_state;
                session->error = saved_error;
                session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
                session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
                return 0;
            }
            if (moon_success) {
                /* Skip Whisper only for high-energy, high-SNR, clear utterances.
                 * Thresholds: rms>=600/peak>=2500.  rms=750 was too strict for
                 * live noisy environments (rms~700 speech was forced into Whisper
                 * validation where Whisper hallucinated and caused rejection). */
                const bool skip_whisper_for_clear_command =
                    validate_low_energy_command_with_whisper &&
                    moon_latency_ms <= 180 &&
                    joint_stats->rms >= 600.0 &&
                    joint_stats->peak >= 2500 &&
                    snr_ratio_validate >= 2.5f &&
                    !force_whisper_for_low_pseudo_conf &&
                    !hard_whisper_scene;
                if (skip_whisper_for_clear_command) {
                    std::ostringstream oss;
                    oss << "joint_fastpath_moonshine_skip_whisper seg=" << seg_id
                        << " rms=" << joint_stats->rms
                        << " peak=" << joint_stats->peak
                        << " snr_ratio=" << snr_ratio_validate
                        << " moon_latency=" << moon_latency_ms
                        << " text=" << json_escape(moon_segment_text.empty() ? moon_final_text : moon_segment_text);
                    realtime_trace(session, oss.str());
                    if (!apply_paraformer_zh_candidate("fastpath_moonshine_skip_whisper")) {
                        session->decision_reason = "joint_v1_staged_fastpath_moonshine";
                    }
                    return 0;
                }
                if (validate_low_energy_command_with_whisper) {
                    std::ostringstream oss;
                    oss << "joint_moonshine_validate_with_whisper seg=" << seg_id
                        << " rms=" << joint_stats->rms
                        << " peak=" << joint_stats->peak
                        << " text=" << json_escape(moon_segment_text.empty() ? moon_final_text : moon_segment_text);
                    realtime_trace(session, oss.str());
                } else {
                    /* Previously returned fastpath here, skipping Whisper entirely.
                     * Now fall through to Whisper validation for SNR<2.0 and other
                     * low-confidence cases to catch Moonshine mis-recognitions. */
                    std::ostringstream oss;
                    oss << "joint_moonshine_low_confidence_validate seg=" << seg_id
                        << " snr=" << snr_ratio_validate
                        << " rms=" << joint_stats->rms
                        << " peak=" << joint_stats->peak
                        << " text=" << json_escape(moon_segment_text.empty() ? moon_final_text : moon_segment_text);
                    realtime_trace(session, oss.str());
                }
            }
            /* Very short low-confidence Japanese fillers/units (e.g. two-mora words)
             * can make Whisper spend 2-3s and then reject/hallucinate, while the C#
             * PhoneticAssist path can recover from final PCM quickly. If Moonshine
             * hard-failed or only produced a non-peak candidate, do not block on
             * Whisper validation; return no native transcript and let PhoneticAssist
             * decide. This keeps clear high-energy Moonshine fastpath untouched. */
            const bool moon_candidate_non_peak =
                moon_success && session->moonshine_peak_candidate.seg_id != seg_id;
            const bool defer_short_phonetic_assist =
                validate_low_energy_command_with_whisper &&
                !hard_whisper_scene &&
                effective_scene == "short_command" &&
                joint_stats->active_duration_ms <= 2500.0 &&
                snr_ratio_validate >= 2.5f &&
                (joint_stats->rms < 300.0 || joint_stats->peak < 2500) &&
                (moonshine_hard_failed || moon_candidate_non_peak);
            if (defer_short_phonetic_assist) {
                std::ostringstream oss;
                oss << "joint_short_phonetic_assist_defer seg=" << seg_id
                    << " reason=" << (moonshine_hard_failed ? "moonshine_failed" : "moonshine_non_peak")
                    << " duration_ms=" << joint_stats->active_duration_ms
                    << " rms=" << joint_stats->rms
                    << " peak=" << joint_stats->peak
                    << " snr_ratio=" << snr_ratio_validate
                    << " moon_latency=" << moon_latency_ms
                    << " moon=" << json_escape(moon_segment_text.empty() ? moon_final_text : moon_segment_text);
                realtime_trace(session, oss.str());
                session->final_text = saved_final_text;
                session->has_transcript = saved_has_transcript;
                session->selected_engine = saved_selected_engine;
                session->decision_reason = "joint_short_phonetic_assist_defer";
                session->latency_ms = saved_latency_ms;
                session->state = saved_state;
                session->error = saved_error;
                session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
                session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
                return 0;
            }
            session->final_text = saved_final_text;
            session->has_transcript = saved_has_transcript;
            session->selected_engine = saved_selected_engine;
            session->decision_reason = saved_decision_reason;
            session->latency_ms = saved_latency_ms;
            session->state = saved_state;
            session->error = saved_error;
            session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
            session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
        }

        /* Whisper validation always uses prefiltered audio (session->prefilter_profile).
         * Previously this was disabled to match Moonshine's raw audio, on the theory
         * that different inputs cause artificially low agreement. In practice the
         * opposite is true: raw-audio Whisper hallucinates more than prefiltered
         * Whisper, so disabling prefilter *lowered* agreement and increased
         * disagreement_reject. Whisper is specifically trained to handle
         * denoised/normalized audio (same conditions as the benchmark at 72% exact).
         * With prefilter ON, Whisper's output is closer to the correct transcription
         * and agreement with Moonshine's partial-correct output is higher.
         * Removing the prefilter override also closes the benchmark/realtime gap. */
        const bool short_ja_low_dur_mismatch = false;
        bool saved_validate_prefilter = session->use_prefilter;
        if (short_ja_low_dur_mismatch) {
            session->use_prefilter = false;
            realtime_trace(session, "joint_whisper_validate_skip_prefilter short_ja_low_dur=1");
        }
        if (keep_recording && realtime_whisper_validation_busy(session)) {
            realtime_trace(session, "joint_whisper_validate_skip_async_busy");
            session->use_prefilter = saved_validate_prefilter;
            session->final_text = saved_final_text;
            session->has_transcript = saved_has_transcript;
            session->selected_engine = saved_selected_engine;
            session->decision_reason = "joint_whisper_validation_busy";
            session->latency_ms = saved_latency_ms;
            session->state = saved_state;
            session->error = saved_error;
            session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
            session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
            return 0;
        }
        session->realtime_engine = "whisper";
        const int whisper_ret = transcribe_realtime_samples(session, pcm_samples, keep_recording, seg_id, capture_rms, joint_stats);
        session->realtime_engine = "joint_auto";
        if (short_ja_low_dur_mismatch) {
            session->use_prefilter = saved_validate_prefilter;
        }
        if (whisper_ret == 0 && session->selected_engine == "whisper") {
            std::string whisper_segment_text;
            if (session->moonshine_latest_candidate.seg_id == seg_id) {
                whisper_segment_text = session->moonshine_latest_candidate.text;
            }
            if (whisper_segment_text.empty() && !session->final_text.empty()) {
                auto pos = session->final_text.rfind('\n');
                whisper_segment_text = (pos == std::string::npos) ? session->final_text : session->final_text.substr(pos + 1);
            }
            if ((validate_low_energy_command_with_whisper || force_whisper_for_low_pseudo_conf) &&
                !moon_segment_text.empty() && !whisper_segment_text.empty()) {
                double agreement = byte_lcs_similarity(moon_segment_text, whisper_segment_text);
                /* Laughter agreement: both engines recognized a laughter / non-verbal
                 * vocalisation (e.g. んんんん vs ほほほ vs フフフフ). LCS similarity is
                 * near 0 because the characters differ, but semantically both agree on
                 * "laughter". Treat this as agreement and fall through to Whisper output. */
                const bool laughter_agree = agreement < 0.35 &&
                    is_laughter_like(moon_segment_text) && is_laughter_like(whisper_segment_text);
                if (laughter_agree) {
                    std::ostringstream oss;
                    oss << "joint_laughter_agree seg=" << seg_id
                        << " moon=" << json_escape(moon_segment_text)
                        << " whisper=" << json_escape(whisper_segment_text);
                    realtime_trace(session, oss.str());
                    // fall through: use Whisper output (already in session->final_text)
                } else if (agreement < 0.35) {
                    std::ostringstream oss;
                    oss << "joint_low_energy_disagreement_reject seg=" << seg_id
                        << " agreement=" << agreement
                        << " moon=" << json_escape(moon_segment_text)
                        << " whisper=" << json_escape(whisper_segment_text);
                    realtime_trace(session, oss.str());

                   /* Low-SNR Moonshine fallback: when segment RMS is close to or slightly
                      * below the noise floor (snr_margin < 100), Whisper validation is
                      * unreliable — both engines hallucinate different texts, but Moonshine's
                      * sub-160ms response still reflects the acoustic structure better than
                      * Whisper's often-stalled output. Accept Moonshine to give user TTS
                      * feedback rather than silent rejection.
                      * Guard: moon_success (latency≤160ms) ensures we only do this when
                      * Moonshine was fast/confident.
                      * snr_margin > -200 allows for noise-floor over-estimation: e.g. when
                      * a loud preceding utterance inflates the background estimate, the
                      * speech RMS can read slightly below the floor (seen as -20 to -50).
                      * In that case the agreement gate is also skipped because both engines
                      * fail on the same degraded signal; agreement reflects two wrong results
                      * rather than a genuine confidence measure.
                      * Very negative margins (≤ -200) are left as-is (likely pure noise). */
                    const float noise_floor_val = (float)session->noise_profile.noise_floor_rms;
                    const float snr_margin = (float)joint_stats->rms - noise_floor_val;
                    const bool snr_near_floor = snr_margin > -200.0f && snr_margin < 100.0f;
                    /* Whisper latency > 1500ms is a strong signal that Whisper is struggling
                     * with the audio. When Moonshine responded fast (< 120ms), trust Moonshine
                     * even if agreement is low, because Whisper's slow output is likely
                     * hallucination rather than a genuine alternative transcription. */
                    const bool whisper_stalled = (session->latency_ms > 1500);
                    if (!force_whisper_for_low_pseudo_conf && moon_success && snr_near_floor &&
                        (agreement >= 0.22f || snr_margin < 0.0f ||
                         (whisper_stalled && moon_latency_ms < 120))) {
                        std::ostringstream oss2;
                        oss2 << "joint_low_snr_moonshine_fallback seg=" << seg_id
                             << " snr_margin=" << snr_margin
                             << " noise_floor=" << noise_floor_val
                             << " rms=" << joint_stats->rms
                             << " moon_latency=" << moon_latency_ms
                             << " whisper_latency=" << session->latency_ms
                             << " moon=" << json_escape(moon_segment_text)
                             << " agreement=" << agreement;
                        realtime_trace(session, oss2.str());
                        session->final_text = moon_final_text;
                        session->has_transcript = moon_has_transcript;
                        session->selected_engine = "moonshine";
                        session->decision_reason = "joint_low_snr_moonshine_fallback";
                        session->latency_ms = moon_latency_ms;
                        session->state = moon_state;
                        session->error = moon_error;
                        session->moonshine_latest_candidate = moon_moonshine_latest_candidate;
                        session->moonshine_peak_candidate = moon_moonshine_peak_candidate;
                        apply_paraformer_zh_candidate("low_snr_moonshine_fallback");
                        return 0;
                    }

                    session->final_text = saved_final_text;
                    session->has_transcript = saved_has_transcript;
                    session->selected_engine = saved_selected_engine;
                    session->decision_reason = saved_has_transcript ? saved_decision_reason : "joint_low_energy_disagreement_reject";
                    session->latency_ms = saved_latency_ms;
                    session->state = saved_state;
                    session->error = saved_error;
                    session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
                    session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
                    apply_paraformer_zh_candidate("low_energy_disagreement_reject");
                    return 0;
                }
            }
            if (!force_whisper_for_low_pseudo_conf &&
                session->latency_ms > 1500 && moon_success && !moon_segment_text.empty()) {
                std::ostringstream oss;
                oss << "joint_whisper_stalled_moonshine_fallback seg=" << seg_id
                    << " moon_latency=" << moon_latency_ms
                    << " whisper_latency=" << session->latency_ms
                    << " moon=" << json_escape(moon_segment_text);
                realtime_trace(session, oss.str());
                session->final_text = moon_final_text;
                session->has_transcript = moon_has_transcript;
                session->selected_engine = "moonshine";
                session->decision_reason = "joint_whisper_stalled_moonshine_fallback";
                session->latency_ms = moon_latency_ms;
                session->state = moon_state;
                session->error = moon_error;
                session->moonshine_latest_candidate = moon_moonshine_latest_candidate;
                session->moonshine_peak_candidate = moon_moonshine_peak_candidate;
                apply_paraformer_zh_candidate("whisper_stalled_moonshine_fallback");
                return 0;
            }
            /* Suppress Whisper solo output when Moonshine hard-failed AND Whisper is stalled.
             * Moonshine failure alone is not sufficient evidence of hallucination:
             * Whisper can still produce correct output at normal latency (<1500ms)
             * even when Moonshine fails (e.g. "ありがとうございます" at 350ms).
             * Only suppress when Whisper itself is stalled (>1500ms), which is a
             * strong signal that Whisper is also struggling with degraded audio. */
            if (moonshine_hard_failed && moon_segment_text.empty() && session->latency_ms > 1500) {
                std::ostringstream oss;
                oss << "joint_moonshine_failed_whisper_suppressed seg=" << seg_id
                    << " whisper_text=" << json_escape(session->final_text);
                realtime_trace(session, oss.str());
                session->final_text = saved_final_text;
                session->has_transcript = saved_has_transcript;
                session->selected_engine = saved_selected_engine;
                session->decision_reason = "moonshine_failed_whisper_suppressed";
                session->latency_ms = saved_latency_ms;
                session->state = saved_state;
                session->error = saved_error;
                session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
                session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
                apply_paraformer_zh_candidate("moonshine_failed_whisper_suppressed");
                return 0;
            }
            if (!apply_paraformer_zh_candidate("fallback_whisper")) {
                session->decision_reason = "joint_v1_staged_fallback_whisper";
                session->joint_arbitration_permitted = !hard_whisper_scene && !force_whisper_for_low_pseudo_conf && moon_success;
            }
            return 0;
        }
        if (force_whisper_for_low_pseudo_conf) {
            std::ostringstream oss;
            oss << "joint_low_pseudo_conf_validation_failed seg=" << seg_id
                << " pseudo_conf=" << speculative_conf
                << " whisper_ret=" << whisper_ret;
            realtime_trace(session, oss.str());
            session->final_text = saved_final_text;
            session->has_transcript = saved_has_transcript;
            session->selected_engine = saved_selected_engine;
            session->decision_reason = "joint_low_pseudo_conf_validation_failed";
            session->latency_ms = saved_latency_ms;
            session->state = saved_state;
            session->error = saved_error;
            session->moonshine_latest_candidate = saved_moonshine_latest_candidate;
            session->moonshine_peak_candidate = saved_moonshine_peak_candidate;
            return 0;
        }
        if (moon_ret == 0 && moon_has_transcript) {
            session->final_text = moon_final_text;
            session->has_transcript = moon_has_transcript;
            session->selected_engine = "moonshine";
            session->decision_reason = "joint_v1_staged_last_resort_moonshine";
            session->latency_ms = moon_latency_ms;
            session->state = moon_state;
            session->error = moon_error;
            apply_paraformer_zh_candidate("last_resort_moonshine");
            return 0;
        }
        return apply_paraformer_zh_candidate("whisper_ret") ? 0 : whisper_ret;
    }

    /* fresh segment transcript - no accumulation from previous segments */
    session->final_text.clear();
    session->has_transcript = false;
    session->state = RealtimeState::kProcessing;
    const std::vector<int16_t>* use_samples = &pcm_samples;
    const std::string effective_scene = resolve_realtime_auto_scene(
        session->scene, stats, &session->noise_profile);
    session->resolved_scene = effective_scene;
    if (session->scene == "auto") {
        std::ostringstream oss;
        oss << "scene_auto_resolved scene=" << effective_scene;
        if (stats) {
            oss << " duration_ms=" << stats->duration_ms
                << " rms=" << stats->rms
                << " peak=" << stats->peak;
        }
        if (session->noise_profile.ready) {
            oss << " noise_floor=" << session->noise_profile.noise_floor_rms
                << " silero_p90=" << session->noise_profile.silero_p90;
        }
        realtime_trace(session, oss.str());
    }
    const auto effective_moonshine_preset = use_moonshine
        ? get_moonshine_product_preset(session->language, effective_scene)
        : session->moonshine_preset;
    const auto& moonshine_preset = effective_moonshine_preset;
    const bool moonshine_short_ja_low_duration = use_moonshine &&
        session->language == "ja" && effective_scene == "short_command" &&
        stats && stats->duration_ms > 0.0 && stats->duration_ms < 3500.0 &&
        stats->rms > 0.0 && stats->rms < 3000.0;
    int moonshine_trim_threshold = moonshine_preset.trim_abs_threshold;
    int moonshine_trim_pad_ms = moonshine_preset.trim_pad_ms + 80;
    if (use_moonshine) {
        auto raw_pcm_bytes = pcm16_to_bytes(pcm_samples);
        auto tuning = mobmate::select_final_asr_tuning(
            raw_pcm_bytes.data(), static_cast<int>(raw_pcm_bytes.size()), 16000);
        moonshine_trim_threshold = tuning.trimAbsThreshold;
        moonshine_trim_pad_ms = std::max<int>(moonshine_preset.trim_pad_ms + 80, tuning.trimPadMs) + 180;
    }
    std::vector<int16_t> trimmed_samples;
    if (use_moonshine && moonshine_trim_threshold > 0) {
        auto pcm_bytes = pcm16_to_bytes(*use_samples);
        auto trimmed = mobmate::trim_silence(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()),
                                             16000, moonshine_trim_threshold,
                                             moonshine_trim_pad_ms);
        if (!trimmed.empty()) {
            trimmed_samples = bytes_to_pcm16(trimmed);
            use_samples = &trimmed_samples;
        }
    }
    std::vector<int16_t> filtered_samples;
    if (session->use_prefilter && (!use_moonshine || (!moonshine_short_ja_low_duration && moonshine_preset.prefilter != "off"))) {
        const std::string& prefilter_profile = use_moonshine && !moonshine_short_ja_low_duration
            ? moonshine_preset.prefilter
            : session->prefilter_profile;
        filtered_samples = apply_whisper_prefilter(*use_samples, effective_scene, prefilter_profile, 16000);
        use_samples = &filtered_samples;
    }

    /* Phase 6B P0-1: Final chunk normalization (matching V1 normalizeFinalChunkForAsr) */
    const std::vector<int16_t>* norm_samples = use_samples;
    std::vector<int16_t> normalized_samples;
    double normalize_target_dbfs = use_moonshine
        ? std::min<double>(-7.0, moonshine_preset.normalize_dbfs + 20.0)
        : FINAL_NORM_TARGET_DBFS;
    normalized_samples = normalize_final_chunk(*use_samples, normalize_target_dbfs);
    if (!normalized_samples.empty() && normalized_samples.size() == use_samples->size()) {
        /* Check if normalization actually changed anything */
        bool changed = false;
        for (size_t i = 0; i < use_samples->size() && !changed; ++i) {
            if ((*use_samples)[i] != normalized_samples[i]) changed = true;
        }
        if (changed) {
            norm_samples = &normalized_samples;
        }
    }

    /* Phase 6B P1: Add 300ms leading/tail padding (matching V1 default) */
    /* 300ms @ 16kHz = 4800 samples. V1 uses silence padding for edge-word recognition. */
    static constexpr int PADDING_SAMPLES = 4800;  // 300ms @ 16kHz
    std::vector<int16_t> padded_samples;
    padded_samples.reserve(PADDING_SAMPLES + norm_samples->size() + PADDING_SAMPLES);
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);  // leading silence
    padded_samples.insert(padded_samples.end(), norm_samples->begin(), norm_samples->end());
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);  // tail silence

    dump_pcm16_wav(session->dump_dir, seg_id, "asr_input",
                   use_moonshine ? *norm_samples : padded_samples, 16000);

    auto start = std::chrono::steady_clock::now();
    if (use_addon) {
        char text[65536] = {};
        int64_t inference_ms = -1;
        session->addon_candidate_attempted = true;
        const auto callback = g_addon_transcriber.load();
        const auto callback_v2 = g_addon_transcriber_v2.load();
        const auto candidate_start = std::chrono::steady_clock::now();
        const bool success = callback_v2 ? callback_v2(session->addon_engine_id.c_str(), session->language.c_str(),
            session->addon_language_mode.c_str(), padded_samples.data(), static_cast<int>(padded_samples.size()),
            16000, text, static_cast<int>(sizeof(text)), &inference_ms) == 0 :
            session->addon_engine_id == "haas_ja" && callback && callback(padded_samples.data(), static_cast<int>(padded_samples.size()),
            16000, text, static_cast<int>(sizeof(text)), &inference_ms) == 0;
        session->addon_candidate_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - candidate_start).count();
        session->addon_candidate_success = success;
        session->addon_candidate_ms = inference_ms;
        text[sizeof(text) - 1] = '\0';
        session->addon_candidate_text = success ? std::string(text) : std::string();
        if (!success) {
            session->selected_engine = "none";
            session->decision_reason = "addon_inference_failed";
            session->latency_ms = 0;
            session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
            session->addon_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - addon_wall_start).count();
            return 0;
        }
        session->latency_ms = static_cast<int>(inference_ms);
        std::string text_gate_reason;
        const bool accept = !session->addon_candidate_text.empty() && (!stats ||
            should_accept_realtime_final_text(session->addon_candidate_text, *stats, session->addon_engine_id, session->language,
                                              text_gate_reason, &pcm_samples, session->vad_start_prob));
        session->final_text = accept ? session->addon_candidate_text : "";
        session->has_transcript = accept;
        session->selected_engine = accept ? session->addon_engine_id : "none";
        session->decision_reason = accept ? (keep_recording ? "vad_segment" : "single_engine") :
            (session->addon_candidate_text.empty() ? "no_valid_transcript" : "hallucination_gate");
        session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
        if (accept && session->copy_final_to_clipboard) mmv2_clipboard_set_text(session->final_text.c_str());
        session->addon_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - addon_wall_start).count();
        return 0;
    }
    if (use_paraformer_zh) {
        const std::string model_dir = session->experimental_asr_candidate_model_dir.empty()
            ? pf_default_model_dir()
            : session->experimental_asr_candidate_model_dir;
        const std::string text = pf_transcribe_pcm16(pcm_samples, model_dir);
        auto end = std::chrono::steady_clock::now();
        session->latency_ms = static_cast<int>(
            std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
        if (text.empty()) {
            if (!keep_recording) {
                session->state = RealtimeState::kFailed;
                session->error = "paraformer_zh transcription failed";
            } else {
                session->state = RealtimeState::kRecording;
            }
            session->selected_engine = "none";
            session->decision_reason = "transcription_failed";
            const std::vector<int16_t>* filtered_ptr_fail = session->use_prefilter ? &filtered_samples : nullptr;
            dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                                  session->final_text, session->latency_ms,
                                  session->selected_engine, session->decision_reason,
                                  capture_rms, vad_state_to_string(session->vad_state),
                                  filtered_ptr_fail);
            return keep_recording ? 0 : -1;
        }
        if (!session->final_text.empty()) session->final_text += "\n";
        session->final_text += text;
        session->has_transcript = true;
        session->selected_engine = "paraformer_zh";
        session->decision_reason = keep_recording ? "vad_segment" : "single_engine";
        session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
        if (session->copy_final_to_clipboard) {
            mmv2_clipboard_set_text(session->final_text.c_str());
        }
        std::ostringstream oss;
        oss << "asr_final_text seg=" << seg_id
            << " engine=paraformer_zh"
            << " latency_ms=" << session->latency_ms
            << " transcript=" << json_escape(text);
        realtime_trace(session, oss.str());
        const std::vector<int16_t>* filtered_ptr = session->use_prefilter ? &filtered_samples : nullptr;
        dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                              session->final_text, session->latency_ms,
                              session->selected_engine, session->decision_reason,
                              capture_rms, vad_state_to_string(session->vad_state),
                              filtered_ptr);
        return 0;
    }
    if (use_moonshine) {
        const auto& preset = moonshine_preset;
        bool moon_stream_reset_before_oneshot = false;
        if (session->moonshine_streaming && session->moonshine) {
            /* Isolate each finalized VAD segment from the persistent Moonshine
             * streaming buffer.  Otherwise the streaming context can leak the
             * previous utterance into the next one-shot result. */
            session->moonshine_streaming = session->moonshine->reset_stream(preset.leading_padding_ms, false);
            session->moonshine_stream_sample_count = 0;
            moon_stream_reset_before_oneshot = true;
            realtime_trace(session, session->moonshine_streaming
                ? "moon_stream_reset before_oneshot ok"
                : "moon_stream_reset before_oneshot failed");
        }
        auto float_samples = pcm16_to_float32(*norm_samples);
        std::string best_text;
        std::string best_source = "one_shot";
        bool success = false;
        int passes = preset.passes < 1 ? 1 : (preset.passes > 6 ? 6 : preset.passes);
        for (int p = 0; p < passes; ++p) {
            const int leading_padding_ms = moonshine_short_ja_low_duration ? 420 : preset.leading_padding_ms;
            const int tail_padding_ms = moonshine_short_ja_low_duration ? 520 : preset.tail_padding_ms;
            auto results = session->moonshine->transcribeAccurate(
                float_samples.data(), static_cast<int>(float_samples.size()),
                16000, leading_padding_ms, tail_padding_ms);
            if (!results.empty() && results[0].success && !results[0].text.empty()) {
                best_text = results[0].text;
                success = true;
                break;
            }
        }
        if (!success && session->moonshine_streaming && !moon_stream_reset_before_oneshot) {
            auto stream_results = session->moonshine->flush_stream(preset.tail_padding_ms, 2, 35);
            std::string joined_text;
            bool saw_complete = false;
            for (const auto& r : stream_results) {
                if (r.success && !r.text.empty()) {
                    if (!joined_text.empty()) joined_text += "\n";
                    joined_text += r.text;
                    saw_complete = saw_complete || r.is_complete;
                    success = true;
                }
            }
            if (!joined_text.empty()) {
                best_text = joined_text;
                best_source = saw_complete ? "stream_flush_complete" : "stream_flush_partial";
            }
        }
        auto end = std::chrono::steady_clock::now();
        session->latency_ms = static_cast<int>(
            std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
        if (!success) {
            if (!keep_recording) {
                session->state = RealtimeState::kFailed;
                session->error = "moonshine transcription failed";
            } else {
                realtime_trace(session, "moonshine transcription failed; keep_recording=1");
                session->state = RealtimeState::kRecording;
            }
            session->selected_engine = "none";
            session->decision_reason = "transcription_failed";
            const std::vector<int16_t>* filtered_ptr_fail = session->use_prefilter ? &filtered_samples : nullptr;
            dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                                  session->final_text, session->latency_ms,
                                  session->selected_engine, session->decision_reason,
                                  capture_rms, vad_state_to_string(session->vad_state),
                                  filtered_ptr_fail);
            return keep_recording ? 0 : -1;
        }
     if (!best_text.empty()) {
            // Phase 6D-2: Final acceptance gate - text gate (after ASR)
            std::string text_gate_reason;
            if (stats && !should_accept_realtime_final_text(best_text, *stats, "moonshine", session->language, text_gate_reason,
                                                            &pcm_samples, session->vad_start_prob)) {
                std::ostringstream oss;
                oss << "asr_final_text_drop seg=" << seg_id
                    << " reason=" << text_gate_reason
                    << " text=" << json_escape(best_text);
                realtime_trace(session, oss.str());
                if (!session->has_transcript) {
                    session->selected_engine = "none";
                    session->decision_reason = "hallucination_gate";
                }
            } else {
                if (!session->final_text.empty()) session->final_text += "\n";
                session->final_text += best_text;
                session->has_transcript = true;
                session->selected_engine = "moonshine";
                session->decision_reason = keep_recording ? "vad_segment" : "single_engine";
                if (session->copy_final_to_clipboard) {
                    mmv2_clipboard_set_text(session->final_text.c_str());
                }
                std::ostringstream oss;
                oss << "asr_final_text seg=" << seg_id
                    << " latency_ms=" << session->latency_ms
                    << " transcript=" << json_escape(best_text);
                realtime_trace(session, oss.str());
            }
            // Phase 7A: Speaker identification (enroll + match on final text)
            if (session->speaker_id_enabled && session->speaker_profile && !pcm_samples.empty()) {
                if (speaker_enroll(*session->speaker_profile, pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000)) {
                    std::ostringstream eos;
                    eos << "speaker_enrolled seg=" << seg_id;
                    realtime_trace(session, eos.str());
                }
                // score logged for future use; gate not applied (enrollment-only mode)
                bool pass = false;
                float score = speaker_match(*session->speaker_profile, pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000, &pass);
                std::ostringstream eos;
                eos << "speaker_match seg=" << seg_id
                    << " score=" << score
                    << " pass=" << (pass ? 1 : 0)
                    << " enrolled=" << session->speaker_profile->total_enrolled;
                realtime_trace(session, eos.str());
            }
            // Phase 6D-4: Moonshine candidate memory
            session->moonshine_latest_candidate = {seg_id, best_text, session->latency_ms, best_source};
            if (selector_text_len(best_text) > selector_text_len(session->moonshine_peak_candidate.text)) {
                session->moonshine_peak_candidate = {seg_id, best_text, session->latency_ms, best_source};
            }
            {
                std::ostringstream oss;
                oss << "moon_candidate seg=" << seg_id
                    << " text=" << json_escape(best_text)
                    << " latency_ms=" << session->latency_ms
                    << " source=" << best_source
                    << " is_peak=" << (session->moonshine_peak_candidate.seg_id == seg_id ? "true" : "false");
                realtime_trace(session, oss.str());
            }
        } else if (!session->has_transcript) {
            session->selected_engine = "none";
            session->decision_reason = "no_valid_transcript";
        }
        session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
        const std::vector<int16_t>* filtered_ptr = session->use_prefilter ? &filtered_samples : nullptr;
        dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                              session->final_text, session->latency_ms,
                              session->selected_engine, session->decision_reason,
                              capture_rms, vad_state_to_string(session->vad_state),
                              filtered_ptr);
        return 0;
    }

    auto float_samples = pcm16_to_float32(padded_samples);
    /* Phase 6B: use TranscribeOpts to pass initial_prompt and other params */
    {
        mobmate::WhisperEngine::TranscribeOpts opts;
        opts.language = session->language;
        opts.n_threads = session->threads;
        opts.initial_prompt = session->initial_prompt;
        auto detailed = session->engine->transcribe_detailed(
            float_samples.data(),
            static_cast<int>(float_samples.size()),
            opts);
        std::optional<std::string> transcript;
        if (detailed) {
            transcript = detailed->text;
            session->whisper_avg_token_p = detailed->avg_token_p;
            session->whisper_max_no_speech_prob = detailed->max_no_speech_prob;
            session->whisper_confidence_score = detailed->confidence_score;
        }
        auto end = std::chrono::steady_clock::now();
        session->latency_ms = static_cast<int>(
            std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

        if (!transcript) {
            session->state = RealtimeState::kFailed;
            session->error = "transcription failed";
            session->selected_engine = "none";
            session->decision_reason = "transcription_failed";
            /* Phase 6B: dump metadata for failed transcription */
            const std::vector<int16_t>* filtered_ptr_fail = session->use_prefilter ? &filtered_samples : nullptr;
            dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                                  session->final_text, session->latency_ms,
                                  session->selected_engine, session->decision_reason,
                                  capture_rms, vad_state_to_string(session->vad_state),
                                  filtered_ptr_fail);
            return -1;
        }

        if (!transcript->empty()) {
            /* 2026-07-14: decode-time confidence gate (no_speech_prob / avg token p).
             * Catches silence/noise hallucinations that pass acoustic gates. */
            std::string conf_gate_reason;
            std::string text_gate_reason;
            if (detailed && whisper_confidence_rejects_final(detailed->max_no_speech_prob,
                                                             detailed->avg_token_p,
                                                             *transcript, conf_gate_reason)) {
                std::ostringstream oss;
                oss << "asr_final_confidence_drop seg=" << seg_id
                    << " reason=" << conf_gate_reason
                    << " no_speech=" << detailed->max_no_speech_prob
                    << " token_p=" << detailed->avg_token_p
                    << " text=" << json_escape(*transcript);
                realtime_trace(session, oss.str());
                if (!session->has_transcript) {
                    session->selected_engine = "none";
                    session->decision_reason = "whisper_confidence_gate";
                }
            } else
            // Phase 6D-2: Final acceptance gate - text gate (after ASR)
            if (stats && !should_accept_realtime_final_text(*transcript, *stats, "whisper", session->language, text_gate_reason,
                                                            &pcm_samples, session->vad_start_prob)) {
                std::ostringstream oss;
                oss << "asr_final_text_drop seg=" << seg_id
                    << " reason=" << text_gate_reason
                    << " text=" << json_escape(*transcript);
                realtime_trace(session, oss.str());
                if (!session->has_transcript) {
                    session->selected_engine = "none";
                    session->decision_reason = "hallucination_gate";
                }
            } else {
                if (!session->final_text.empty()) session->final_text += "\n";
                session->final_text += *transcript;
                session->has_transcript = true;
                session->selected_engine = "whisper";
                session->decision_reason = keep_recording ? "vad_segment" : "single_engine";
                if (session->copy_final_to_clipboard) {
                    mmv2_clipboard_set_text(session->final_text.c_str());
                }
                std::ostringstream oss;
                oss << "asr_final_text seg=" << seg_id
                    << " latency_ms=" << session->latency_ms
                    << " transcript=" << json_escape(*transcript);
                realtime_trace(session, oss.str());
            }
            // Phase 7A: Speaker identification (enroll + match on final text)
            if (session->speaker_id_enabled && session->speaker_profile && !pcm_samples.empty()) {
                if (speaker_enroll(*session->speaker_profile, pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000)) {
                    std::ostringstream eos;
                    eos << "speaker_enrolled seg=" << seg_id;
                    realtime_trace(session, eos.str());
                }
                // score logged for future use; gate not applied (enrollment-only mode)
                bool pass = false;
                float score = speaker_match(*session->speaker_profile, pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000, &pass);
                std::ostringstream eos;
                eos << "speaker_match seg=" << seg_id
                    << " score=" << score
                    << " pass=" << (pass ? 1 : 0)
                    << " enrolled=" << session->speaker_profile->total_enrolled;
                realtime_trace(session, eos.str());
            }
            // Phase 6D-4: Moonshine candidate memory (also for Whisper final results)
            session->moonshine_latest_candidate = {seg_id, *transcript, session->latency_ms, "one_shot"};
            if (transcript->length() > session->moonshine_peak_candidate.text.length()) {
                session->moonshine_peak_candidate = {seg_id, *transcript, session->latency_ms, "one_shot"};
            }
            {
                std::ostringstream oss;
                oss << "moon_candidate seg=" << seg_id
                    << " text=" << json_escape(*transcript)
                    << " latency_ms=" << session->latency_ms
                    << " source=one_shot"
                    << " is_peak=" << (session->moonshine_peak_candidate.seg_id == seg_id ? "true" : "false");
                realtime_trace(session, oss.str());
            }
        } else if (!session->has_transcript) {
            session->selected_engine = "none";
            session->decision_reason = "no_valid_transcript";
        }

        session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
        /* Phase 6B: dump metadata JSON with filtered stats (filtered_samples still in scope) */
        const std::vector<int16_t>* filtered_ptr = session->use_prefilter ? &filtered_samples : nullptr;
        dump_segment_metadata(session->dump_dir, seg_id, pcm_samples,
                              session->final_text, session->latency_ms,
                              session->selected_engine, session->decision_reason,
                              capture_rms, vad_state_to_string(session->vad_state),
                              filtered_ptr);
        return 0;
    }
}

static void transcribe_realtime_partial(RealtimeSession* session,
                                        const std::vector<int16_t>& speech_samples) {
    if (session->realtime_engine == "moonshine") {
        if (!session->moonshine || !session->moonshine_streaming || session->auto_transcribing || session->partial_transcribing) return;
        session->partial_transcribing = true;
        auto results = session->moonshine->poll_stream(true);
        for (const auto& r : results) {
            if (!r.success || r.text.empty()) continue;
            if (r.is_complete || r.has_text_changed || r.is_new) {
                session->latency_ms = r.latency_ms;
                const int seg_id = session->dump_seg_counter;
                session->moonshine_latest_candidate = {seg_id, r.text, r.latency_ms, r.is_complete ? "stream_complete" : "stream_partial"};
                if (selector_text_len(r.text) > selector_text_len(session->moonshine_peak_candidate.text)) {
                    session->moonshine_peak_candidate = session->moonshine_latest_candidate;
                }
                if (!r.is_complete) {
                    session->partial = r.text;
                }
                std::ostringstream oss;
                oss << (r.is_complete ? "moon_stream_complete text=" : "moon_stream_partial text=") << json_escape(r.text)
                    << " complete=" << (r.is_complete ? 1 : 0)
                    << " latency_ms=" << r.latency_ms;
                realtime_trace(session, oss.str());
            }
        }
        session->partial_transcribing = false;
        return;
    }
    // joint_auto: use Moonshine one-shot for fast partial (~50ms)
    if (session->realtime_engine == "joint_auto") {
        if (!session->moonshine || speech_samples.empty()) return;
        if (session->auto_transcribing || session->partial_transcribing) return;
        if (is_near_silence_pcm16(speech_samples)) return;
        session->partial_transcribing = true;
        // Use last 1s max (same cap as whisper partial path)
        static constexpr size_t MAX_PARTIAL_SAMPLES = 16000;
        std::vector<int16_t> tail;
        const std::vector<int16_t>* use = &speech_samples;
        if (speech_samples.size() > MAX_PARTIAL_SAMPLES) {
            tail.assign(speech_samples.end() - MAX_PARTIAL_SAMPLES, speech_samples.end());
            use = &tail;
        }
        auto float_samples = pcm16_to_float32(*use);
        auto results = session->moonshine->transcribeAccurate(
            float_samples.data(), static_cast<int>(float_samples.size()), 16000,
            session->moonshine_preset.leading_padding_ms,
            session->moonshine_preset.tail_padding_ms);
        if (!results.empty() && results[0].success && !results[0].text.empty()) {
            session->partial = results[0].text;
            std::ostringstream oss;
            oss << "moon_partial_text text=" << json_escape(results[0].text)
                << " latency_ms=" << results[0].latency_ms;
            realtime_trace(session, oss.str());
        }
        session->partial_transcribing = false;
        return;
    }
    if (session->realtime_engine != "whisper") return;
    if (!session->engine || speech_samples.empty()) return;
    if (session->auto_transcribing || session->partial_transcribing) return;
    if (is_near_silence_pcm16(speech_samples)) return;

    session->partial_transcribing = true;
    {
        std::ostringstream oss;
        oss << "asr_partial_start speech_samples=" << speech_samples.size()
            << " state=" << realtime_state_to_string(session->state)
            << " vad=" << vad_state_to_string(session->vad_state);
        realtime_trace(session, oss.str());
    }
    const RealtimeState previous_state = session->state;
    session->state = RealtimeState::kProcessing;

    const std::vector<int16_t>* use_samples = &speech_samples;
    std::vector<int16_t> tail_samples;
    /* V1 PARTIAL_TAIL_BYTES = 16000*2 bytes = 16000 samples = last 1s @ 16kHz. */
    static constexpr size_t MAX_PARTIAL_SAMPLES = 16000;
    if (speech_samples.size() > MAX_PARTIAL_SAMPLES) {
        tail_samples.assign(speech_samples.end() - MAX_PARTIAL_SAMPLES, speech_samples.end());
        use_samples = &tail_samples;
    }

    std::vector<int16_t> filtered_samples;
    if (session->use_prefilter) {
        filtered_samples = apply_whisper_prefilter(*use_samples, session->scene, session->prefilter_profile, 16000);
        use_samples = &filtered_samples;
    }

    const std::vector<int16_t>* norm_samples = use_samples;
    std::vector<int16_t> normalized_samples = normalize_final_chunk(*use_samples, FINAL_NORM_TARGET_DBFS);
    if (!normalized_samples.empty() && normalized_samples.size() == use_samples->size()) {
        norm_samples = &normalized_samples;
    }

    static constexpr int PADDING_SAMPLES = 4800;  // 300ms @ 16kHz
    std::vector<int16_t> padded_samples;
    padded_samples.reserve(PADDING_SAMPLES + norm_samples->size() + PADDING_SAMPLES);
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);
    padded_samples.insert(padded_samples.end(), norm_samples->begin(), norm_samples->end());
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);

    auto float_samples = pcm16_to_float32(padded_samples);
    auto start = std::chrono::steady_clock::now();
    mobmate::WhisperEngine::TranscribeOpts opts;
    opts.language = session->language;
    opts.n_threads = session->threads;
    opts.initial_prompt = session->initial_prompt;
    auto transcript = session->engine->transcribe(
        float_samples.data(),
        static_cast<int>(float_samples.size()),
        opts);
    auto end = std::chrono::steady_clock::now();
    session->latency_ms = static_cast<int>(
        std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

    if (transcript && !transcript->empty()) {
        session->partial = *transcript;
        // Phase 6D-3: Don't overwrite selected_engine/decision_reason with partial values
        // These should only be set by final transcription to avoid UI confusion
        std::ostringstream oss;
        oss << "asr_partial_text latency_ms=" << session->latency_ms
            << " text=" << json_escape(*transcript);
        realtime_trace(session, oss.str());
    }
    session->state = previous_state == RealtimeState::kRecording ? RealtimeState::kRecording : previous_state;
    session->partial_transcribing = false;
}
#endif

#if defined(_WIN32) || defined(__linux__)
static void realtime_spec_trace(RealtimeSession* session, const std::string& message) {
#ifdef _WIN32
    realtime_trace(session, message);
#else
    (void)session;
    std::fprintf(stderr, "[realtime] %s\n", message.c_str());
    std::fflush(stderr);
#endif
}

static void reset_joint_trace(RealtimeSession* session) {
    session->joint_moonshine = {};
    session->joint_whisper = {};
    session->joint_execution_order.clear();
    session->joint_arbitration_permitted = false;
    session->joint_existing_status.clear();
    session->joint_addon_status = "not_run";
    session->joint_selection_reason = "existing";
    session->addon_candidate_attempted = false;
    session->addon_candidate_success = false;
    session->addon_candidate_text.clear();
    session->addon_candidate_ms = -1;
    session->addon_candidate_wall_ms = -1;
    session->addon_wall_ms = -1;
    session->joint_added_wait_ms = 0;
}

static int apply_joint_addon(RealtimeSession* session, const std::vector<int16_t>& pcm_samples,
                             bool keep_recording, const PcmStats* stats, int ret,
                             std::chrono::steady_clock::time_point start) {
    const auto existing_end = std::chrono::steady_clock::now();
    session->joint_existing_wall_ms = start == std::chrono::steady_clock::time_point{} ? -1 :
        std::chrono::duration_cast<std::chrono::milliseconds>(existing_end - start).count();
    const PcmStats computed = stats ? *stats : compute_pcm_stats(pcm_samples);
    const bool extreme_noise = session->noise_profile.ready && session->noise_profile.noise_floor_rms > 1200.0 &&
        computed.rms / session->noise_profile.noise_floor_rms < 1.05;
    const auto gate_reason = [](const std::string& reason) {
        return reason.rfind("hallucination_gate", 0) == 0 || reason.rfind("whisper_confidence_gate", 0) == 0 ||
            reason == "joint_low_energy_disagreement_reject" || reason == "moonshine_failed_whisper_suppressed" ||
            reason == "joint_laughter_tail_skip_whisper" || reason == "joint_short_phonetic_assist_defer" ||
            reason == "joint_low_pseudo_conf_validation_failed" || reason == "joint_whisper_validation_busy" ||
            reason == "linux_joint_moonshine_failed_skip_whisper";
    };
    const bool rejected = pcm_samples.empty() || is_near_silence_pcm16(pcm_samples) || extreme_noise ||
        gate_reason(session->decision_reason) ||
        (!session->has_transcript && (gate_reason(session->joint_moonshine.reason) || gate_reason(session->joint_whisper.reason)));
    if (rejected) session->joint_existing_status = "RejectedByExistingGate";
    else if (session->has_transcript && !session->final_text.empty()) session->joint_existing_status = "Valid";
    else if (!session->moonshine || !session->engine) session->joint_existing_status = "Unavailable";
    else if (ret != 0 || session->joint_moonshine.reason == "transcription_failed" || session->joint_whisper.reason == "transcription_failed")
        session->joint_existing_status = "InferenceFailure";
    else session->joint_existing_status = "EmptyRecognition";

    const auto callback = g_addon_joint_transcriber.load();
    const auto callback_v2 = g_addon_joint_transcriber_v2.load();
    const bool callback_available = callback_v2 || (session->addon_engine_id == "haas_ja" && callback);
    if (session->addon_joint_requested && addon_joint_language_supported(session->addon_engine_id, session->language, session->addon_joint_language) && callback_available && !rejected) {
        char text[65536] = {};
        int64_t inference_ms = -1;
        session->addon_candidate_attempted = true;
        session->joint_execution_order.push_back(session->addon_engine_id);
        const int callback_ret = callback_v2 ? callback_v2(session->addon_engine_id.c_str(), session->language.c_str(),
            session->addon_language_mode.c_str(), pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000,
            text, static_cast<int>(sizeof(text)), &inference_ms) : callback(pcm_samples.data(), static_cast<int>(pcm_samples.size()), 16000,
            text, static_cast<int>(sizeof(text)), &inference_ms);
        const auto addon_end = std::chrono::steady_clock::now();
        session->addon_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(addon_end - existing_end).count();
        session->addon_candidate_wall_ms = session->addon_wall_ms;
        session->joint_added_wait_ms = session->addon_wall_ms;
        text[sizeof(text) - 1] = '\0';
        session->addon_candidate_success = callback_ret == 0;
        session->addon_candidate_ms = inference_ms;
        session->addon_candidate_text = callback_ret == 0 ? std::string(text) : std::string();
        session->joint_addon_status = callback_ret == -2 ? "Timeout" : callback_ret == -3 ? "Unavailable" : callback_ret != 0 ? "InferenceFailure" :
            session->addon_candidate_text.empty() ? "EmptyRecognition" : "Valid";
        std::string text_gate_reason;
        const bool valid = session->joint_addon_status == "Valid" &&
            should_accept_realtime_final_text(session->addon_candidate_text, computed, session->addon_engine_id, session->language,
                                              text_gate_reason, &pcm_samples, session->vad_start_prob);
        if (session->joint_addon_status == "Valid" && !valid) session->joint_addon_status = "RejectedByExistingGate";
        if (valid && session->joint_existing_status != "Valid") {
            session->final_text = session->addon_candidate_text;
            session->has_transcript = true;
            session->selected_engine = session->addon_engine_id;
            session->decision_reason = session->addon_engine_id == "haas_ja" ? "joint_haas_rescue" : "joint_addon_rescue";
            session->latency_ms = static_cast<int>(inference_ms);
            session->state = keep_recording ? RealtimeState::kRecording : RealtimeState::kCompleted;
            session->error.clear();
            session->joint_selection_reason = session->addon_engine_id == "haas_ja" ? "haas_rescue" : "addon_rescue";
            if (session->copy_final_to_clipboard) mmv2_clipboard_set_text(session->final_text.c_str());
            ret = 0;
        } else if (valid) {
            const std::string key = sv_compare_key(session->addon_candidate_text);
            const std::string selected = sv_compare_key(session->final_text);
            const std::string moon = sv_compare_key(session->joint_moonshine.text);
            const std::string whisper = sv_compare_key(session->joint_whisper.text);
            if (!key.empty() && key == selected) {
                session->joint_selection_reason = session->addon_engine_id == "haas_ja" ? "existing_agrees_with_haas" : "existing_agrees_with_addon";
            } else if (!key.empty() && session->joint_arbitration_permitted && session->joint_moonshine.success && session->joint_moonshine.accepted &&
                       session->joint_whisper.success && session->joint_whisper.accepted && !moon.empty() && !whisper.empty() && moon != whisper &&
                       key == moon && session->selected_engine == "whisper") {
                session->final_text = session->joint_moonshine.text;
                session->selected_engine = "moonshine";
                session->latency_ms = static_cast<int>(session->joint_moonshine.latency_ms);
                session->decision_reason = session->addon_engine_id == "haas_ja" ? "joint_haas_agrees_moonshine" : "joint_addon_agrees_moonshine";
                session->joint_selection_reason = session->addon_engine_id == "haas_ja" ? "haas_supports_moonshine" : "addon_supports_moonshine";
                if (session->copy_final_to_clipboard) mmv2_clipboard_set_text(session->final_text.c_str());
            } else {
                session->joint_selection_reason = "existing_no_comparable_agreement";
            }
        } else {
            session->joint_selection_reason = "existing_addon_" + session->joint_addon_status;
        }
    } else if (session->addon_joint_requested && !callback_available) session->joint_addon_status = "Unavailable";
    session->joint_total_wall_ms = session->joint_existing_wall_ms < 0 ? -1 :
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
    return ret;
}

#ifdef _WIN32
// Both normal recognition and Production Replay call this same finalizer. It
// records only recursive candidates actually run by the existing Joint path.
static int transcribe_realtime_samples(RealtimeSession* session,
                                       const std::vector<int16_t>& pcm_samples,
                                       bool keep_recording, int seg_id,
                                       double capture_rms, const PcmStats* stats) {
    const std::string engine = session->realtime_engine;
    const bool joint = engine == "joint_auto" && (session->addon_joint_requested || session->production_replay);
    if (!joint) {
        const int ret = transcribe_realtime_samples_impl(session, pcm_samples, keep_recording, seg_id, capture_rms, stats);
        if (session->joint_collecting && (engine == "moonshine" || engine == "whisper")) {
            auto& candidate = engine == "moonshine" ? session->joint_moonshine : session->joint_whisper;
            candidate.attempted = true;
            candidate.success = ret == 0 && session->decision_reason != "transcription_failed";
            candidate.accepted = candidate.success && session->selected_engine == engine && session->has_transcript && !session->final_text.empty();
            candidate.text = session->moonshine_latest_candidate.seg_id == seg_id
                ? session->moonshine_latest_candidate.text : session->final_text;
            candidate.latency_ms = session->latency_ms;
            candidate.reason = session->decision_reason;
            session->joint_execution_order.push_back(engine);
        }
        return ret;
    }
    reset_joint_trace(session);
    const auto start = std::chrono::steady_clock::now();
    session->joint_collecting = true;
    const int ret = transcribe_realtime_samples_impl(session, pcm_samples, keep_recording, seg_id, capture_rms, stats);
    session->joint_collecting = false;
    return apply_joint_addon(session, pcm_samples, keep_recording, stats, ret, start);
}
#endif

static std::vector<float> prepare_realtime_whisper_validation_input(
    const RealtimeWhisperValidationJob& job) {
    if (!job.prepare_windows_input) {
        return pcm16_to_float32(job.pcm_samples);
    }

    const std::vector<int16_t>* use_samples = &job.pcm_samples;
    std::vector<int16_t> filtered_samples;
    if (job.use_prefilter) {
        filtered_samples = apply_whisper_prefilter(
            job.pcm_samples, job.effective_scene, job.prefilter_profile, 16000);
        use_samples = &filtered_samples;
    }

    std::vector<int16_t> normalized_samples =
        normalize_final_chunk(*use_samples, FINAL_NORM_TARGET_DBFS);
    if (!normalized_samples.empty() && normalized_samples.size() == use_samples->size()) {
        use_samples = &normalized_samples;
    }

    static constexpr int PADDING_SAMPLES = 4800;
    std::vector<int16_t> padded_samples;
    padded_samples.reserve(PADDING_SAMPLES + use_samples->size() + PADDING_SAMPLES);
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);
    padded_samples.insert(padded_samples.end(), use_samples->begin(), use_samples->end());
    padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);
    return pcm16_to_float32(padded_samples);
}

static void realtime_whisper_validation_worker(RealtimeSession* session) {
    for (;;) {
        RealtimeWhisperValidationJob job;
        {
            std::unique_lock<std::mutex> lock(session->whisper_validation_mutex);
            session->whisper_validation_cv.wait(lock, [session]() {
                return session->whisper_validation_shutdown ||
                    !session->whisper_validation_jobs.empty();
            });
            if (session->whisper_validation_shutdown &&
                session->whisper_validation_jobs.empty()) {
                session->whisper_validation_active = false;
                return;
            }
            job = std::move(session->whisper_validation_jobs.front());
            session->whisper_validation_jobs.pop_front();
            session->whisper_validation_active = true;
        }

        RealtimeWhisperValidationResult result;
        result.generation = job.generation;
        result.seg_id = job.seg_id;
        result.final_detected_at_ms = job.final_detected_at_ms;
        result.pcm_samples = job.pcm_samples;
        result.reason = "transcription_failed";
        try {
            if (session->engine) {
                const auto float_samples = prepare_realtime_whisper_validation_input(job);
                mobmate::WhisperEngine::TranscribeOpts opts;
                opts.language = job.language;
                opts.n_threads = job.threads;
                opts.initial_prompt = job.initial_prompt;
                const auto started = std::chrono::steady_clock::now();
                auto detailed = session->engine->transcribe_detailed(
                    float_samples.data(), static_cast<int>(float_samples.size()), opts);
                result.latency_ms = static_cast<int>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - started).count());
                if (detailed) {
                    result.text = detailed->text;
                    result.avg_token_p = detailed->avg_token_p;
                    result.max_no_speech_prob = detailed->max_no_speech_prob;
                    result.confidence_score = detailed->confidence_score;
                    std::string gate_reason;
                    if (result.text.empty()) {
                        result.reason = "empty_text";
                    } else if (whisper_confidence_rejects_final(
                                   result.max_no_speech_prob, result.avg_token_p,
                                   result.text, gate_reason)) {
                        result.reason = "whisper_confidence_gate:" + gate_reason;
                    } else if (!should_accept_realtime_final_text(
                                   result.text, job.stats, "whisper", job.language,
                                   gate_reason, &job.pcm_samples, job.vad_start_prob)) {
                        result.reason = "hallucination_gate:" + gate_reason;
                    } else {
                        result.accepted = true;
                        result.reason = "joint_low_pseudo_async_whisper";
                    }
                }
            }
        } catch (...) {
            result.reason = "validation_exception";
        }

        {
            std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
            if (result.generation == session->whisper_validation_generation) {
                session->whisper_validation_results.push_back(std::move(result));
            }
            session->whisper_validation_active = false;
        }
    }
}

static void shutdown_realtime_whisper_validation(RealtimeSession* session,
                                                 bool discard_pending) {
    if (!session) return;
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        session->whisper_validation_shutdown = true;
        if (discard_pending) {
            session->whisper_validation_jobs.clear();
            session->whisper_validation_results.clear();
        }
    }
    session->whisper_validation_cv.notify_all();
    if (session->whisper_validation_thread.joinable()) {
        session->whisper_validation_thread.join();
    }
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        session->whisper_validation_active = false;
    }
}

static void start_realtime_whisper_validation(RealtimeSession* session) {
    if (!session || !session->engine) return;
    shutdown_realtime_whisper_validation(session, true);
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        ++session->whisper_validation_generation;
        session->whisper_validation_shutdown = false;
    }
    session->whisper_validation_thread =
        std::thread(realtime_whisper_validation_worker, session);
}

static bool realtime_whisper_validation_busy(RealtimeSession* session) {
    if (!session) return false;
    std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
    return session->whisper_validation_active ||
        !session->whisper_validation_jobs.empty();
}

static bool enqueue_realtime_whisper_validation(RealtimeSession* session,
                                                const std::vector<int16_t>& pcm_samples,
                                                const PcmStats& stats,
                                                int seg_id) {
    if (!session || !session->engine || pcm_samples.empty() ||
        session->speculative_final_candidate.seg_id != seg_id) {
        return false;
    }
    const float pseudo_conf = session->speculative_final_candidate.pseudo_confidence;
    if (pseudo_conf < 0.0f || pseudo_conf >= 0.75f) return false;

    RealtimeWhisperValidationJob job;
    job.seg_id = seg_id;
    job.final_detected_at_ms = session->final_detected_at_ms;
    job.pcm_samples = pcm_samples;
    job.stats = stats;
    job.vad_start_prob = session->vad_start_prob;
    job.language = session->language;
    job.effective_scene = resolve_realtime_auto_scene(
        session->scene, &stats, &session->noise_profile);
    job.threads = session->threads;
#ifdef _WIN32
    job.use_prefilter = session->use_prefilter;
    job.prefilter_profile = session->prefilter_profile;
    job.initial_prompt = session->initial_prompt;
    job.prepare_windows_input = true;
#endif

    size_t queued = 0;
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        if (session->whisper_validation_shutdown) return false;
        if (session->whisper_validation_jobs.size() >= 8) {
            realtime_spec_trace(session,
                "async_whisper_validation_drop reason=queue_full seg=" +
                std::to_string(seg_id));
            return true;
        }
        job.generation = session->whisper_validation_generation;
        session->whisper_validation_jobs.push_back(std::move(job));
        queued = session->whisper_validation_jobs.size();
    }
    session->whisper_validation_cv.notify_one();
    session->final_text.clear();
    session->has_transcript = false;
    session->selected_engine = "none";
    session->decision_reason = "joint_low_pseudo_async_pending";
    session->latency_ms = 0;
    session->state = RealtimeState::kRecording;
    std::ostringstream oss;
    oss << "async_whisper_validation_queued seg=" << seg_id
        << " pseudo_conf=" << pseudo_conf
        << " queued=" << queued;
    realtime_spec_trace(session, oss.str());
    return true;
}

static void publish_realtime_whisper_validation(RealtimeSession* session) {
    if (!session) return;
    RealtimeWhisperValidationResult result;
    bool have_result = false;
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        while (!session->whisper_validation_results.empty()) {
            result = std::move(session->whisper_validation_results.front());
            session->whisper_validation_results.pop_front();
            if (result.generation == session->whisper_validation_generation) {
                have_result = true;
                break;
            }
        }
    }
    if (!have_result) return;

    session->final_detected_at_ms = result.final_detected_at_ms;
    session->latency_ms = result.latency_ms;
    session->whisper_avg_token_p = result.avg_token_p;
    session->whisper_max_no_speech_prob = result.max_no_speech_prob;
    session->whisper_confidence_score = result.confidence_score;
    session->last_final_pcm16 = std::move(result.pcm_samples);
    session->last_final_pcm_sample_rate = 16000;
    session->last_final_pcm_reported = false;
    if (result.accepted) {
        session->final_text = result.text;
        session->has_transcript = true;
        session->selected_engine = "whisper";
        session->decision_reason = result.reason;
#ifdef _WIN32
        if (session->copy_final_to_clipboard) {
            mmv2_clipboard_set_text(session->final_text.c_str());
        }
#endif
    } else {
        session->final_text.clear();
        session->has_transcript = false;
        session->selected_engine = "none";
        session->decision_reason = result.reason;
    }
    if (session->realtime_engine == "joint_auto" && session->addon_joint_requested && addon_joint_language_supported(session->addon_engine_id, session->language, session->addon_joint_language) && !result.accepted) {
        reset_joint_trace(session);
        // This asynchronous route was selected because the speculative candidate
        // failed its existing confidence rule; do not bypass that rule.
        session->joint_existing_status = "RejectedByExistingGate";
        session->joint_selection_reason = "existing_async_validation_rejected";
        session->joint_existing_wall_ms = session->joint_total_wall_ms = -1;
    } else if (result.accepted && session->realtime_engine == "joint_auto" && session->addon_joint_requested && addon_joint_language_supported(session->addon_engine_id, session->language, session->addon_joint_language)) {
        reset_joint_trace(session);
        session->joint_whisper.attempted = session->joint_whisper.success = session->joint_whisper.accepted = true;
        session->joint_whisper.text = result.text;
        session->joint_whisper.reason = result.reason;
        session->joint_whisper.latency_ms = result.latency_ms;
        session->joint_execution_order.push_back("whisper");
        const auto stats = compute_pcm_stats(session->last_final_pcm16);
        // Queue and previous speculative work are not timed here.
        apply_joint_addon(session, session->last_final_pcm16, true, &stats, 0, {});
    }
    std::ostringstream oss;
    oss << "async_whisper_validation_ready seg=" << result.seg_id
        << " accepted=" << (result.accepted ? 1 : 0)
        << " latency_ms=" << result.latency_ms
        << " reason=" << result.reason
        << " text=" << json_escape(result.text);
    realtime_spec_trace(session, oss.str());
}

static void clear_realtime_speculative_final(RealtimeSession* session, const char* reason) {
    if (!session) return;
    if (session->speculative_final_attempted && reason) {
        std::ostringstream oss;
        oss << "spec_final_clear reason=" << reason
            << " seg=" << session->speculative_final_candidate.seg_id;
        realtime_spec_trace(session, oss.str());
    }
    session->speculative_final_attempted = false;
    session->speculative_final_candidate = {};
}

static void start_realtime_speculative_final(RealtimeSession* session,
                                             const std::vector<int16_t>& segment_samples,
                                             int seg_id) {
    if (!session || session->speculative_final_attempted ||
        session->realtime_engine != "joint_auto" || !session->moonshine ||
        segment_samples.empty()) return;
    session->speculative_final_attempted = true;

    const PcmStats stats = compute_pcm_stats(segment_samples);
    const std::string effective_scene = resolve_realtime_auto_scene(
        session->scene, &stats, &session->noise_profile);
    if ((effective_scene != "short_command" && effective_scene != "talk_clean") ||
        stats.active_duration_ms > 4000.0) {
        realtime_spec_trace(session, "spec_final_skip scene_or_duration");
        return;
    }

    const auto preset = get_moonshine_product_preset(session->language, effective_scene);
    const std::vector<int16_t>* use_samples = &segment_samples;
    std::vector<int16_t> trimmed_samples;
    auto raw_pcm_bytes = pcm16_to_bytes(segment_samples);
    auto tuning = mobmate::select_final_asr_tuning(
        raw_pcm_bytes.data(), static_cast<int>(raw_pcm_bytes.size()), 16000);
    auto trimmed = mobmate::trim_silence(raw_pcm_bytes.data(), static_cast<int>(raw_pcm_bytes.size()),
                                         16000, tuning.trimAbsThreshold,
                                         (std::max)(preset.trim_pad_ms + 80, tuning.trimPadMs) + 180);
    if (!trimmed.empty()) {
        trimmed_samples = bytes_to_pcm16(trimmed);
        use_samples = &trimmed_samples;
    }
    std::vector<int16_t> normalized_samples = normalize_final_chunk(
        *use_samples, (std::min)(-7.0, static_cast<double>(preset.normalize_dbfs) + 20.0));
    if (!normalized_samples.empty() && normalized_samples.size() == use_samples->size())
        use_samples = &normalized_samples;

    const auto f32 = pcm16_to_float32(*use_samples);
    const auto started = std::chrono::steady_clock::now();
    auto results = session->moonshine->transcribeAccurate(
        f32.data(), static_cast<int>(f32.size()), 16000,
        preset.leading_padding_ms, preset.tail_padding_ms);
    const int latency_ms = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started).count());
    if (!results.empty() && results[0].success && !results[0].text.empty()) {
        session->speculative_final_candidate = {seg_id, results[0].text, latency_ms, "hold_overlap"};
        /* Mobfy Task A port (2026-07-14): multi-pass agreement pseudo-confidence.
         * Decode the same audio again with a perturbed padding context
         * (Mobfy production: 600/600 vs 360/840 → lead -240 / tail +240).
         * Hallucinations from noise decode unstably across contexts while real
         * speech stays stable. Runs inside the 450ms→1000ms silence-hold window,
         * so it adds no user-visible latency; candidate.latency_ms keeps the
         * first-pass value used by the ≤180ms commit gate. */
        auto results2 = session->moonshine->transcribeAccurate(
            f32.data(), static_cast<int>(f32.size()), 16000,
            (std::max)(0, preset.leading_padding_ms - 240),
            preset.tail_padding_ms + 240);
        if (!results2.empty() && results2[0].success) {
            session->speculative_final_candidate.pseudo_confidence =
                static_cast<float>(byte_lcs_similarity(results[0].text, results2[0].text));
        }
    }

    std::ostringstream oss;
    oss << "spec_final_ready seg=" << seg_id
        << " latency_ms=" << latency_ms
        << " pseudo_conf=" << session->speculative_final_candidate.pseudo_confidence
        << " text=" << json_escape(session->speculative_final_candidate.text);
    realtime_spec_trace(session, oss.str());
}

static bool commit_realtime_speculative_final(RealtimeSession* session,
                                              const std::vector<int16_t>& segment_samples,
                                              const PcmStats& stats,
                                              int seg_id,
                                              double capture_rms) {
    if (!session || session->speculative_final_candidate.seg_id != seg_id ||
        session->speculative_final_candidate.text.empty() ||
        session->speculative_final_candidate.latency_ms > 180 ||
        stats.active_duration_ms > 4000.0 || stats.rms < 600.0 || stats.peak < 2500)
        return false;

    /* 2026-07-14: speculative commit is Moonshine-only (no decode confidence
     * available). Short fillers (あれ, えっと, はい) are exactly the class that
     * hallucinates on noise bursts, and they carry little value at fastpath
     * speed. Require >= 4 normalized codepoints to commit speculatively;
     * shorter texts fall through to joint Whisper validation instead. */
    {
        const size_t spec_cp = rt_utf8_codepoint_count(
            normalize_text_for_similarity(session->speculative_final_candidate.text));
        if (spec_cp < 4) {
            std::ostringstream oss;
            oss << "spec_final_reject seg=" << seg_id
                << " reason=short_text_requires_validation cp=" << spec_cp
                << " text=" << json_escape(session->speculative_final_candidate.text);
            realtime_spec_trace(session, oss.str());
            return false;
        }
    }

    /* Mobfy Task A port (2026-07-14): multi-pass agreement pseudo-confidence gate.
     * Base thresholds match Mobfy production (vc_asr_gate_rejects_low_confidence):
     * < 0.35 = strong hallucination signal; very short segments (≤520ms) need
     * ≥ 0.55. -1 (second pass unavailable) keeps legacy accept behavior.
     * STT addition (live 07-14): mid-band confidence correlates with Moonshine
     * MIS-recognition (誤認識 0.6 vs 正解 0.875-1.0). Speculative fastpath commit
     * now requires >= 0.75; the 0.35-0.75 band falls through to normal joint
     * Moonshine+Whisper validation (adds ~2s only for shaky utterances). */
    {
        const float spec_conf = session->speculative_final_candidate.pseudo_confidence;
        if (spec_conf >= 0.0f) {
            const bool very_short = stats.duration_ms > 0 && stats.duration_ms <= 520.0;
            if (spec_conf < 0.35f || (very_short && spec_conf < 0.55f)) {
                std::ostringstream oss;
                oss << "spec_final_reject seg=" << seg_id
                    << " reason=low_pseudo_confidence conf=" << spec_conf
                    << " duration_ms=" << stats.duration_ms
                    << " text=" << json_escape(session->speculative_final_candidate.text);
                realtime_spec_trace(session, oss.str());
                return false;
            }
            if (spec_conf < 0.75f) {
                std::ostringstream oss;
                oss << "spec_final_reject seg=" << seg_id
                    << " reason=mid_pseudo_confidence_defer_validation conf=" << spec_conf
                    << " duration_ms=" << stats.duration_ms
                    << " text=" << json_escape(session->speculative_final_candidate.text);
                realtime_spec_trace(session, oss.str());
                return false;
            }
        }
    }

    std::string gate_reason;
    if (!should_accept_realtime_final_text(session->speculative_final_candidate.text, stats,
                                           "moonshine", session->language, gate_reason, &segment_samples,
                                           session->vad_start_prob)) {
        std::ostringstream oss;
        oss << "spec_final_reject seg=" << seg_id << " reason=" << gate_reason;
        realtime_spec_trace(session, oss.str());
        return false;
    }

    session->final_text = session->speculative_final_candidate.text;
    session->has_transcript = true;
    session->selected_engine = "moonshine";
    session->decision_reason = "joint_speculative_moonshine";
    session->latency_ms = session->speculative_final_candidate.latency_ms;
    session->last_final_pcm16 = segment_samples;
    session->last_final_pcm_sample_rate = 16000;
    session->last_final_pcm_reported = false;
    session->state = RealtimeState::kRecording;
#ifdef _WIN32
    if (session->copy_final_to_clipboard)
        mmv2_clipboard_set_text(session->final_text.c_str());
#endif

    if (session->speaker_id_enabled && session->speaker_profile && !segment_samples.empty()) {
        (void)speaker_enroll(*session->speaker_profile, segment_samples.data(),
                             static_cast<int>(segment_samples.size()), 16000);
        bool pass = false;
        const float score = speaker_match(*session->speaker_profile, segment_samples.data(),
                                          static_cast<int>(segment_samples.size()), 16000, &pass);
        std::ostringstream soss;
        soss << "speaker_match seg=" << seg_id << " score=" << score
             << " pass=" << (pass ? 1 : 0)
             << " enrolled=" << session->speaker_profile->total_enrolled;
        realtime_spec_trace(session, soss.str());
    }

    std::ostringstream oss;
    oss << "spec_final_commit seg=" << seg_id
        << " latency_ms=" << session->latency_ms
        << " text=" << json_escape(session->final_text);
    realtime_spec_trace(session, oss.str());
#ifdef _WIN32
    dump_pcm16_wav(session->dump_dir, seg_id, "raw", segment_samples, 16000);
    if (session->use_prefilter) {
        const std::string effective_scene = resolve_realtime_auto_scene(
            session->scene, &stats, &session->noise_profile);
        const auto filtered = apply_whisper_prefilter(
            segment_samples, effective_scene, session->prefilter_profile, 16000);
        if (!filtered.empty())
            dump_pcm16_wav(session->dump_dir, seg_id, "filtered", filtered, 16000);
    }
    dump_segment_metadata(session->dump_dir, seg_id, segment_samples,
                          session->final_text, session->latency_ms,
                          session->selected_engine, session->decision_reason,
                          capture_rms, vad_state_to_string(session->vad_state), nullptr);
#else
    (void)capture_rms;
#endif
    return true;
}
#endif

#ifdef _WIN32
// Accepted segment entry shared with live capture; replay cannot recreate a
// speculative candidate or an ambient profile not present in the recording.
static int finalize_accepted_realtime_segment(RealtimeSession* session, std::vector<int16_t>& samples,
                                              const PcmStats& stats, int seg_id, double capture_rms) {
    const double segment_gain = compute_safe_input_auto_gain(session, stats);
    if (segment_gain > 1.0) {
        apply_gain_in_place(samples, segment_gain);
        std::ostringstream oss;
        oss << "auto_gain_applied gain=" << segment_gain << " seg_rms=" << stats.rms
            << " seg_peak=" << stats.peak << " floor=" << session->noise_profile.noise_floor_rms
            << " std=" << session->noise_profile.noise_rms_stddev;
        realtime_trace(session, oss.str());
    }
    if (enqueue_realtime_whisper_validation(session, samples, stats, seg_id)) {
        if (session->addon_joint_requested && addon_joint_language_supported(session->addon_engine_id, session->language, session->addon_joint_language)) {
            reset_joint_trace(session);
            session->joint_existing_status = "Pending";
            session->joint_selection_reason = "existing_async_validation_pending";
            session->joint_existing_wall_ms = session->joint_total_wall_ms = -1;
        }
        return 0;
    }
    if (commit_realtime_speculative_final(session, samples, stats, seg_id, capture_rms)) {
        if (session->realtime_engine == "joint_auto" && session->addon_joint_requested && addon_joint_language_supported(session->addon_engine_id, session->language, session->addon_joint_language)) {
            reset_joint_trace(session);
            session->joint_moonshine.attempted = session->joint_moonshine.success = session->joint_moonshine.accepted = true;
            session->joint_moonshine.text = session->final_text;
            session->joint_moonshine.latency_ms = session->latency_ms;
            session->joint_moonshine.reason = "speculative_prior_pcm";
            session->joint_execution_order.push_back("moonshine_speculative");
            return apply_joint_addon(session, samples, true, &stats, 0, {});
        }
        return 0;
    }
    return transcribe_realtime_samples(session, samples, true, seg_id, capture_rms, &stats);
}
#endif

#ifndef _WIN32
// Linux retains the unconsumed capture buffer until the segment is finalized.
// Select its existing tail rather than copying/re-appending samples as a second buffer.
static size_t linux_asr_pre_roll_start(size_t speech_start) {
    const size_t limit = static_cast<size_t>(RealtimeSession::ASR_PRE_ROLL_MAX_SAMPLES);
    return speech_start > limit ? speech_start - limit : 0;
}

// Private diagnostic only: no PCM/text or public status fields are added.
static bool linux_endpoint_trace_enabled() {
    const char* enabled = std::getenv("MOBMATE_PRIVATE_ENDPOINT_TRACE");
    return enabled && std::strcmp(enabled, "1") == 0;
}

// A capture stop can arrive while the synchronous recognizer is running. Keep
// the last published final, including its metadata, rather than returning the
// result of that interrupted poll.
struct LinuxFinalSnapshot {
    std::string text, engine, reason, scene;
    bool has_transcript, pcm_reported;
    int latency, pcm_rate;
    int64_t detected;
    float avg_p, no_speech, confidence;
    std::vector<int16_t> pcm;
    JointCandidateTrace moonshine, whisper;
    std::vector<std::string> order;
    std::string existing_status, addon_status, selection_reason, addon_text;
    int64_t existing_wall, total_wall, added_wait, addon_ms, addon_candidate_wall, addon_wall;
    bool addon_attempted, addon_success;

    explicit LinuxFinalSnapshot(const RealtimeSession& s)
        : text(s.final_text), engine(s.selected_engine), reason(s.decision_reason), scene(s.resolved_scene),
          has_transcript(s.has_transcript), pcm_reported(s.last_final_pcm_reported),
          latency(s.latency_ms), pcm_rate(s.last_final_pcm_sample_rate), detected(s.final_detected_at_ms),
          avg_p(s.whisper_avg_token_p), no_speech(s.whisper_max_no_speech_prob), confidence(s.whisper_confidence_score),
          pcm(s.last_final_pcm16), moonshine(s.joint_moonshine), whisper(s.joint_whisper), order(s.joint_execution_order),
          existing_status(s.joint_existing_status), addon_status(s.joint_addon_status), selection_reason(s.joint_selection_reason),
          addon_text(s.addon_candidate_text), existing_wall(s.joint_existing_wall_ms), total_wall(s.joint_total_wall_ms),
          added_wait(s.joint_added_wait_ms), addon_ms(s.addon_candidate_ms), addon_candidate_wall(s.addon_candidate_wall_ms),
          addon_wall(s.addon_wall_ms), addon_attempted(s.addon_candidate_attempted), addon_success(s.addon_candidate_success) {}
    void restore(RealtimeSession& s) const {
        s.final_text = text; s.selected_engine = engine; s.decision_reason = reason; s.resolved_scene = scene;
        s.has_transcript = has_transcript; s.last_final_pcm_reported = pcm_reported;
        s.latency_ms = latency; s.last_final_pcm_sample_rate = pcm_rate; s.final_detected_at_ms = detected;
        s.whisper_avg_token_p = avg_p; s.whisper_max_no_speech_prob = no_speech; s.whisper_confidence_score = confidence;
        s.last_final_pcm16 = pcm; s.joint_moonshine = moonshine; s.joint_whisper = whisper; s.joint_execution_order = order;
        s.joint_existing_status = existing_status; s.joint_addon_status = addon_status; s.joint_selection_reason = selection_reason;
        s.addon_candidate_text = addon_text; s.joint_existing_wall_ms = existing_wall; s.joint_total_wall_ms = total_wall;
        s.joint_added_wait_ms = added_wait; s.addon_candidate_ms = addon_ms; s.addon_candidate_wall_ms = addon_candidate_wall;
        s.addon_wall_ms = addon_wall; s.addon_candidate_attempted = addon_attempted; s.addon_candidate_success = addon_success;
    }
};

static void fail_linux_capture(RealtimeSession* session) {
    {
        std::lock_guard<std::mutex> lock(session->whisper_validation_mutex);
        ++session->whisper_validation_generation;
        session->whisper_validation_shutdown = true;
        session->whisper_validation_jobs.clear();
        session->whisper_validation_results.clear();
    }
    session->whisper_validation_cv.notify_all();
    session->capture.stop(); // Control thread, never the miniaudio callback.
    session->capture.clear_samples();
    session->asr_pre_roll.clear();
    session->partial.clear();
    session->total_samples = session->chunk_samples = 0;
    session->partial_last_sample_count = 0;
    session->partial_speech_start_sample = session->vad_end_sample = -1;
    session->vad_state = VadState::kSilence;
    session->vad_speech_count = 0;
    session->auto_transcribing = session->partial_transcribing = false;
    clear_realtime_speculative_final(session, nullptr);
    if (session->vad_silero) silero_vad_reset(session->vad_silero);
    session->state = RealtimeState::kFailed;
    session->error = "capture_device_lost";
}
#endif

static std::string make_poll_json(RealtimeSession* session) {
#ifndef _WIN32
    const bool watch_capture = session->capture_initialized && session->state == RealtimeState::kRecording;
    std::unique_ptr<LinuxFinalSnapshot> previous;
    if (watch_capture) previous = std::make_unique<LinuxFinalSnapshot>(*session);
    bool capture_lost = watch_capture && session->capture.source_lost();
    if (capture_lost) fail_linux_capture(session);
#endif
#ifdef _WIN32
    auto samples = session->capture.get_samples();
    static constexpr size_t REALTIME_SILENCE_BUFFER_CAP_SAMPLES = 320000; // 20s @ 16kHz
    static constexpr size_t REALTIME_SILENCE_BUFFER_KEEP_SAMPLES = 16000; // keep 1s pre-roll/noise tail
    if (session->state == RealtimeState::kRecording &&
        (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected) &&
        session->vad_speech_count == 0 &&
        samples.size() > REALTIME_SILENCE_BUFFER_CAP_SAMPLES) {
        const size_t discard_count = samples.size() > REALTIME_SILENCE_BUFFER_KEEP_SAMPLES
            ? samples.size() - REALTIME_SILENCE_BUFFER_KEEP_SAMPLES
            : 0;
        if (discard_count > 0) {
            std::ostringstream oss;
            oss << "silence_buffer_cap discard=" << discard_count
                << " before=" << samples.size()
                << " keep=" << REALTIME_SILENCE_BUFFER_KEEP_SAMPLES;
            realtime_trace(session, oss.str());
            session->capture.discard_samples(discard_count);
            session->asr_pre_roll.clear();
            session->total_samples = 0;
            session->chunk_samples = 0;
            session->partial_last_sample_count = 0;
            session->partial_speech_start_sample = -1;
            session->vad_end_sample = -1;
            session->vad_soft_start_frames = 0;
            session->vad_soft_start_sample = -1;
            // Reset VAD timing state to match the truncated buffer
            session->vad_elapsed_ms = 0.0;
            session->vad_session_start_ms = 0.0;
            session->vad_last_speech_time_ms = 0.0;
            // Reset Silero VAD internal LSTM state after buffer discontinuity
            if (session->vad_silero) {
                silero_vad_reset(session->vad_silero);
                realtime_trace(session, "silero_vad_reset after_silence_buffer_cap");
            }
            // Reset Moonshine streaming state after buffer discontinuity
            if (session->realtime_engine == "moonshine" && session->moonshine_streaming && session->moonshine) {
                session->moonshine_streaming = session->moonshine->reset_stream(session->moonshine_preset.leading_padding_ms, false);
                session->moonshine_stream_sample_count = 0;
                realtime_trace(session, session->moonshine_streaming ? "moon_stream_reset after_silence_cap ok" : "moon_stream_reset after_silence_cap failed");
            }
            samples = session->capture.get_samples();
        }
    }
    double rms = session->capture.get_rms_level();
    session->total_samples = samples.size();

    if (session->realtime_engine == "moonshine" && session->moonshine_streaming && session->moonshine) {
        size_t new_start = session->moonshine_stream_sample_count >= 0
            ? static_cast<size_t>(session->moonshine_stream_sample_count)
            : 0;
        if (new_start < samples.size()) {
            std::vector<int16_t> new_pcm(samples.begin() + new_start, samples.end());
            auto float_samples = pcm16_to_float32(new_pcm);
            if (session->moonshine->add_audio(float_samples.data(), static_cast<int>(float_samples.size()), 16000)) {
                session->moonshine_stream_sample_count = static_cast<int64_t>(samples.size());
            }
        }
    }

    // Update VAD state
    if (session->state == RealtimeState::kRecording) {
        double current_time_ms = session->total_samples * 1000.0 / 16000.0; // Approximate time based on samples
        if (session->vad_session_start_ms == 0.0 && session->total_samples > 0) {
            session->vad_session_start_ms = current_time_ms;
        }
        session->vad_elapsed_ms = current_time_ms; // sample-accurate (was: fixed +1000ms)

        if (session->vad_silero) {
            // SileroVAD-based detection: feed new samples in 512-sample frames
            size_t new_start = session->chunk_samples;
            size_t new_end = samples.size();
            session->chunk_samples = new_end;

            if (new_end > new_start) {
                const int16_t* pcm_ptr = samples.data();
                size_t frame = new_start;
                while (frame + 512 <= new_end) {
                    const size_t frame_start = frame;
                    double prob = silero_vad_score(session->vad_silero,
                                                    pcm_ptr + frame, 512, 16000);
                    frame += 512;
                    if (prob < 0.0) {
                        const char* vad_err = silero_vad_last_error(session->vad_silero);
                        if ((frame_start & 0x1FFF) == 0) {
                            std::ostringstream oss;
                            oss << "silero_vad_score_error frame=" << frame_start
                                << " error=\"" << json_escape(vad_err ? vad_err : "unknown") << "\"";
                            realtime_trace(session, oss.str());
                        }
                        // Conservative fallback: avoid dead-zone by using energy gate when Silero fails.
                        FrameStats ef = compute_frame_stats(pcm_ptr + frame_start, 512);
                        prob = (ef.rms >= 450.0 || ef.peak >= 1400.0)
                            ? session->vad_silero_continue_threshold
                            : 0.0;
                    }
                    const double frame_time_ms = static_cast<double>(frame) * 1000.0 / 16000.0;

                    /* P0 2026-07-14: noise-adaptive start threshold (Mobfy port) */
                    const double effective_start_threshold = realtime_effective_start_threshold(
                        session->vad_silero_start_threshold,
                        session->noise_profile.ready,
                        session->noise_profile.silero_p90);
                    const bool hard_start = prob >= effective_start_threshold;
                    const bool soft_start_candidate = !hard_start &&
                        prob >= session->vad_silero_soft_start_threshold &&
                        (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected);
                    if (hard_start || soft_start_candidate) {
                        if (session->speculative_final_attempted &&
                            session->vad_state == VadState::kSpeech)
                            clear_realtime_speculative_final(session, "speech_resumed");
                        // Phase 6F-3: Adaptive talk pre-gate
                        bool gate_skip = false;
                        std::string gate_reason;
                        if (session->vad_state == VadState::kSilence ||
                            session->vad_state == VadState::kEndDetected) {
                            if (soft_start_candidate) {
                                if (session->vad_soft_start_frames == 0) {
                                    session->vad_soft_start_sample = static_cast<int64_t>(frame_start);
                                }
                                session->vad_soft_start_frames++;
                                size_t pre_frame_len = frame - frame_start;
                                FrameStats pre_fs = compute_frame_stats(pcm_ptr + frame_start, pre_frame_len);
                                double strong_rms = 700.0;
                                double strong_peak = 1800.0;
                                if (session->noise_profile.ready) {
                                    strong_rms = (std::max)(strong_rms,
                                        session->noise_profile.noise_floor_rms + session->noise_profile.noise_rms_stddev);
                                    strong_peak = (std::max)(strong_peak,
                                        session->noise_profile.peak_p95 + 400.0);
                                }
                                const bool strong_soft_signal = prob >= 0.27 &&
                                    (pre_fs.rms >= strong_rms || pre_fs.peak >= strong_peak);
                                double two_frame_rms = 500.0;
                                double two_frame_peak = 900.0;
                                if (session->noise_profile.ready) {
                                    two_frame_rms = (std::max)(350.0,
                                        session->noise_profile.noise_floor_rms + (std::max)(120.0, session->noise_profile.noise_rms_stddev * 1.2));
                                    two_frame_peak = (std::max)(700.0,
                                        session->noise_profile.peak_p95 + 150.0);
                                }
                                const bool two_frame_soft_signal = prob >= 0.20 &&
                                    pre_fs.rms >= two_frame_rms && pre_fs.peak >= two_frame_peak;
                                const bool energy_soft_signal = prob >= 0.22 &&
                                    (pre_fs.rms >= strong_rms * 1.3 || pre_fs.peak >= strong_peak);
                                const int required_soft_frames = strong_soft_signal ? 1 :
                                    (two_frame_soft_signal || energy_soft_signal || prob >= session->vad_silero_continue_threshold ? 2 : 3);
                                if (session->vad_soft_start_frames < required_soft_frames) {
                                    std::ostringstream oss;
                                    oss << "vad_soft_start_candidate frame=" << frame_start
                                        << " prob=" << prob
                                        << " pending_frames=" << session->vad_soft_start_frames
                                        << " required_frames=" << required_soft_frames
                                        << " rms=" << pre_fs.rms
                                        << " peak=" << pre_fs.peak
                                        << " total=" << samples.size();
                                    realtime_trace(session, oss.str());
                                    continue;
                                }
                            }
                            // Compute frame stats for gate check
                            size_t frame_len = frame - frame_start;
                            FrameStats fs = compute_frame_stats(pcm_ptr + frame_start, frame_len);
                            // Phase 6F-5: Apply input auto gain to frame stats for VAD/pre-gate
                            if (session->input_auto_gain > 1.0) {
                                fs.rms *= session->input_auto_gain;
                                fs.peak *= session->input_auto_gain;
                            }

                            // Adaptive thresholds from noise profile
                            double open_rms, open_peak;
                            if (session->noise_profile.ready) {
                                auto& np = session->noise_profile;
                                open_rms = np.noise_floor_rms * 0.28 + np.noise_rms_stddev * 0.35;
                                if (open_rms < 250.0) open_rms = 250.0;
                                {
                                    double inner = np.noise_rms_stddev * 2.0;
                                    if (inner < 80.0) inner = 80.0;
                                    open_peak = np.peak_p95 + inner;
                                    if (open_peak < 250.0) open_peak = 250.0;
                                    if (open_peak > 10000.0) open_peak = 10000.0;
                                }
                                if (np.low_gain_mic) {
                                    if (open_rms > 150.0) open_rms = 150.0;
                                    if (open_peak > 300.0) open_peak = 300.0;
                                }
                            } else {
                                // Static fallback thresholds
                                open_rms = 250.0;
                                open_peak = 300.0;
                            }

                            // Check gate: require rms OR peak above threshold, OR voiceBandRatio strongly voice-like
                            if (fs.rms < open_rms && fs.peak < open_peak && fs.voiceBandRatio < 0.3) {
                                gate_skip = true;
                                if (fs.rms < open_rms) {
                                    gate_reason = "low_rms";
                                } else if (fs.peak < open_peak) {
                                    gate_reason = "low_peak";
                                } else {
                                    gate_reason = "low_vbr";
                                }
                            }

                            /* P0 2026-07-14: breath/exhale air-like veto (Mobfy port,
                             * voice_conversion.cpp:6500-6525). Diffuse air bursts
                             * (breath, exhale, drink/contact noise) show high ZCR +
                             * low periodicity; articulated speech has periodic frames
                             * or very high Silero prob. A vetoed frame only delays the
                             * start by 1-2 frames; the 1s ASR pre-roll keeps the audio. */
                            if (!gate_skip) {
                                size_t air_len = frame - frame_start;
                                const float periodicity = rt_calc_periodicity_i16(pcm_ptr + frame_start, air_len);
                                const bool strong_voice = prob >= 0.92 || periodicity >= 0.24f;
                                const bool breath_like = fs.zcr >= 0.10 && periodicity < 0.12f && prob < 0.62;
                                const bool high_zcr_air_like = fs.zcr >= 0.20 && periodicity < 0.32f && prob < 0.84;
                                const bool mid_prob_air_like = prob < 0.78 &&
                                    ((fs.zcr >= 0.085 && periodicity < 0.70f) || (fs.zcr >= 0.060 && periodicity < 0.58f));
                                if (!strong_voice && (breath_like || high_zcr_air_like || mid_prob_air_like)) {
                                    gate_skip = true;
                                    gate_reason = "air_like";
                                    std::ostringstream aoss;
                                    aoss << "talk_pre_gate_air_like frame=" << frame_start
                                         << " prob=" << prob
                                         << " zcr=" << fs.zcr
                                         << " periodicity=" << periodicity
                                         << " breath=" << (breath_like ? 1 : 0)
                                         << " high_zcr=" << (high_zcr_air_like ? 1 : 0)
                                         << " mid_prob=" << (mid_prob_air_like ? 1 : 0);
                                    realtime_trace(session, aoss.str());
                                }
                            }

                            // Phase 6F-4: Periodic low-frequency noise veto
                            mobmate::LowFrequencyPeriodicMetrics lfp_metrics;
                            bool has_lfp = false;
                            if (!gate_skip) {
                                // Use up to 2048 samples for periodic analysis
                                size_t periodic_window = (frame > 2048) ? (frame - 2048) : new_start;
                                if (periodic_window < new_start) periodic_window = new_start;
                                size_t window_len = frame - periodic_window;
                                if (window_len >= 512) {
                                    int bytes = static_cast<int>(window_len * 2);
                                    lfp_metrics = mobmate::analyze_low_frequency_periodic(
                                        reinterpret_cast<const uint8_t*>(pcm_ptr + periodic_window),
                                        bytes, 16000);
                                    has_lfp = true;
                                    if (lfp_metrics.likelyMechanicalNoise() && fs.voiceBandRatio < 0.3) {
                                        gate_skip = true;
                                        gate_reason = "periodic_noise";
                                    }
                                }
                            }

                            if (gate_skip) {
                                std::ostringstream oss;
                                oss << "talk_pre_gate_skip reason=" << gate_reason
                                    << " rms=" << fs.rms << " open_rms=" << open_rms
                                    << " peak=" << fs.peak << " open_peak=" << open_peak
                                    << " vbr=" << fs.voiceBandRatio
                                    << " zcr=" << fs.zcr
                                    << " prob=" << prob
                                    << " floor=" << session->noise_profile.noise_floor_rms
                                    << " std=" << session->noise_profile.noise_rms_stddev;
                                if (gate_reason == "periodic_noise" && has_lfp) {
                                    oss << " dominantLowHz=" << lfp_metrics.dominantLowHz
                                        << " lowToVoiceRatio=" << lfp_metrics.lowToVoiceRatio;
                                }
                                realtime_trace(session, oss.str());
                            } else {
                                // Accept speech start
                                session->vad_speech_count++;
                                notify_vad_start(session);
                                session->partial_speech_start_sample = session->vad_soft_start_sample >= 0
                                    ? session->vad_soft_start_sample
                                    : static_cast<int64_t>(frame_start);
                                session->final_detected_at_ms = 0;
                                session->vad_start_prob = prob; // P0: feed hallucination gate
                                std::ostringstream oss;
                                oss << (soft_start_candidate ? "vad_soft_speech_start frame=" : "vad_speech_start frame=") << frame_start
                                    << " start_sample=" << session->partial_speech_start_sample
                                    << " prob=" << prob
                                    << " total=" << samples.size()
                                    << " speech_count=" << session->vad_speech_count
                                    << " rms=" << fs.rms
                                    << " peak=" << fs.peak
                                    << " zcr=" << fs.zcr
                                    << " vbr=" << fs.voiceBandRatio;
                                realtime_trace(session, oss.str());
                            }
                        }
                        if (!gate_skip) {
                            session->vad_state = VadState::kSpeech;
                            session->vad_last_speech_time_ms = frame_time_ms;
                            session->vad_soft_start_frames = 0;
                            session->vad_soft_start_sample = -1;
                        } else {
                            session->vad_soft_start_frames = 0;
                            session->vad_soft_start_sample = -1;
                        }
                    } else if (prob >= session->vad_silero_continue_threshold &&
                               session->vad_state == VadState::kSpeech) {
                        // Continue speech
                        if (session->speculative_final_attempted)
                            clear_realtime_speculative_final(session, "speech_resumed");
                        session->vad_state = VadState::kSpeech;
                        session->vad_last_speech_time_ms = frame_time_ms;
                    } else {
                        // Silence
                        if (session->vad_state == VadState::kSpeech) {
                            double silence_dur = frame_time_ms - session->vad_last_speech_time_ms;
                            if (session->vad_silence_duration_ms > REALTIME_SPECULATIVE_FINAL_DELAY_MS &&
                                silence_dur >= REALTIME_SPECULATIVE_FINAL_DELAY_MS &&
                                !session->speculative_final_attempted) {
                                const size_t spec_start = session->partial_speech_start_sample >= 0
                                    ? static_cast<size_t>(session->partial_speech_start_sample) : 0;
                                std::vector<int16_t> spec_segment;
                                spec_segment.reserve(session->asr_pre_roll.size() + frame);
                                spec_segment.insert(spec_segment.end(), session->asr_pre_roll.begin(), session->asr_pre_roll.end());
                                if (spec_start < frame)
                                    spec_segment.insert(spec_segment.end(), samples.begin() + spec_start, samples.begin() + frame);
                                start_realtime_speculative_final(session, spec_segment, session->dump_seg_counter);
                            }
                            if (silence_dur >= session->vad_silence_duration_ms) {
                              session->vad_state = VadState::kEndDetected;
                                  session->final_detected_at_ms = unix_epoch_ms();
                                  session->vad_end_sample = static_cast<int64_t>(frame);
                                 // Phase 6F-0: frame stats at end detection
                                 size_t eframe_len = frame - frame_start;
                                 FrameStats efs = compute_frame_stats(pcm_ptr + frame_start, eframe_len);
                                 std::ostringstream oss;
                                 oss << "vad_end_detected frame=" << frame
                                     << " prob=" << prob
                                     << " silence_ms=" << silence_dur
                                     << " last_speech_ms=" << session->vad_last_speech_time_ms
                                     << " total=" << samples.size()
                                     << " rms=" << efs.rms
                                     << " peak=" << efs.peak
                                     << " zcr=" << efs.zcr
                                     << " vbr=" << efs.voiceBandRatio;
                                 realtime_trace(session, oss.str());
                                break;
                            }
                        } else if (session->vad_state != VadState::kEndDetected) {
                            session->vad_state = VadState::kSilence;
                            session->vad_soft_start_frames = 0;
                            session->vad_soft_start_sample = -1;
                            // Phase 6D-1: V1-style pre-roll capped append during non-speech
                            // V1: MobMateWhisp.java:8893-8905 (appendCappedBytes)
                            // Simple version: append 512-sample frame, cap at ASR_PRE_ROLL_MAX_SAMPLES
                            for (int i = 0; i < 512 && (frame_start + static_cast<size_t>(i)) < frame; i++) {
                                session->asr_pre_roll.push_back(pcm_ptr[frame_start + i]);
                            }
                            while (static_cast<int>(session->asr_pre_roll.size()) > RealtimeSession::ASR_PRE_ROLL_MAX_SAMPLES) {
                                session->asr_pre_roll.erase(session->asr_pre_roll.begin());
                            }
                            // Phase 6F-2: Update noise profile during silence frames
                            size_t nframe_len = frame - frame_start;
                            FrameStats nfs = compute_frame_stats(pcm_ptr + frame_start, nframe_len);
                            update_noise_profile(session, nfs.rms, nfs.peak, prob, frame_time_ms);
                        }
                    }
                }
            }
        } else {
            // Fallback: RMS-based VAD
            double recent_rms = session->capture.get_recent_rms_level(1600);
            VadState previous_vad_state = session->vad_state;
            update_vad_state(session, recent_rms, session->vad_elapsed_ms);
            if (session->vad_state == VadState::kSpeech && previous_vad_state != VadState::kSpeech) {
                session->partial_speech_start_sample = static_cast<int64_t>(samples.size() > 1600 ? samples.size() - 1600 : 0);
            }
        }

        if (session->vad_state == VadState::kEndDetected &&
            !session->auto_transcribing &&
            session->vad_speech_count > 0 &&
            samples.size() >= 16000) {
            session->auto_transcribing = true;
            /* Phase 6B: dump raw captured PCM before ASR */
            int seg_id = session->dump_seg_counter++;
            size_t segment_end = session->vad_end_sample > 0
                ? static_cast<size_t>(session->vad_end_sample)
                : samples.size();
            if (segment_end > samples.size()) segment_end = samples.size();
            size_t segment_start = session->partial_speech_start_sample >= 0
                ? static_cast<size_t>(session->partial_speech_start_sample)
                : 0;
            if (segment_start > segment_end) segment_start = 0;
            std::vector<int16_t> speech_samples(samples.begin() + segment_start,
                                                samples.begin() + segment_end);
            // Phase 6D-1: V1-style segment = pre_roll + speech
            // V1: MobMateWhisp.java:8924-8982 (prepend pre-roll tail to ASR buffer)
            std::vector<int16_t> segment_samples;
            size_t pre_roll_samples = session->asr_pre_roll.size();
            segment_samples.reserve(pre_roll_samples + speech_samples.size());
            segment_samples.insert(segment_samples.end(),
                                   session->asr_pre_roll.begin(),
                                   session->asr_pre_roll.end());
           segment_samples.insert(segment_samples.end(),
                                    speech_samples.begin(),
                                    speech_samples.end());
            // Phase 6D-2: Final acceptance gate - input gate (before ASR)
            PcmStats seg_stats = compute_pcm_stats(segment_samples);
            {
                std::ostringstream oss;
                oss << "auto_final_start seg=" << seg_id
                    << " total=" << samples.size()
                    << " segment_start=" << segment_start
                    << " segment_end=" << segment_end
                    << " pre_roll_samples=" << pre_roll_samples
                    << " speech_samples=" << speech_samples.size()
                    << " segment_total=" << segment_samples.size()
                    << " leftover=" << (samples.size() - segment_end)
                    << " speech_count=" << session->vad_speech_count
                    << " partial_start=" << session->partial_speech_start_sample
                    << " vad_end=" << session->vad_end_sample
                    << " rms=" << rms
                    << " seg_rms=" << seg_stats.rms
                    << " seg_peak=" << seg_stats.peak
                    << " seg_duration_ms=" << seg_stats.duration_ms;
                realtime_trace(session, oss.str());
            }
            std::string gate_reason;
            if (should_skip_realtime_final_input_with_profile(session, seg_stats, gate_reason)) {
                if (session->realtime_engine == "joint_auto" && session->addon_joint_requested) {
                    reset_joint_trace(session);
                    session->joint_existing_status = "RejectedByExistingGate";
                    session->joint_selection_reason = "existing_gate:" + gate_reason;
                    session->joint_existing_wall_ms = session->joint_total_wall_ms = 0;
                }
                std::ostringstream oss;
                oss << "asr_final_gate_skip seg=" << seg_id
                    << " reason=" << gate_reason
                    << " duration_ms=" << seg_stats.duration_ms
                    << " rms=" << seg_stats.rms
                    << " peak=" << seg_stats.peak
                    << " vad=" << vad_state_to_string(session->vad_state)
                    << " speech_count=" << session->vad_speech_count;
                realtime_trace(session, oss.str());
                // Still discard and reset, but don't run ASR
                session->capture.discard_samples(segment_end);
                session->asr_pre_roll.clear();
                session->total_samples = 0;
                session->chunk_samples = 0;
                session->vad_state = VadState::kSilence;
                session->vad_last_speech_time_ms = 0.0;
                session->vad_session_start_ms = 0.0;
                session->vad_elapsed_ms = 0.0;
                session->vad_speech_count = 0;
                session->partial.clear();
                session->partial_last_sample_count = 0;
                session->partial_speech_start_sample = -1;
                session->vad_end_sample = -1;
                session->vad_soft_start_frames = 0;
                session->vad_soft_start_sample = -1;
                clear_realtime_speculative_final(session, nullptr);
                if (session->vad_silero) {
                    silero_vad_reset(session->vad_silero);
                    realtime_trace(session, "silero_vad_reset after_auto_final_skip");
                }
                if (session->realtime_engine == "moonshine" && session->moonshine_streaming && session->moonshine) {
                    session->moonshine_streaming = session->moonshine->reset_stream(session->moonshine_preset.leading_padding_ms, false);
                    session->moonshine_stream_sample_count = 0;
                    realtime_trace(session, session->moonshine_streaming ? "moon_stream_reset ok" : "moon_stream_reset failed");
                }
                session->auto_transcribing = false;
         } else {
                finalize_accepted_realtime_segment(session, segment_samples, seg_stats, seg_id, rms);
                session->capture.discard_samples(segment_end);
                {
                    std::ostringstream oss;
                    oss << "auto_final_discard seg=" << seg_id
                        << " discard=" << segment_end
                        << " expected_leftover=" << (samples.size() - segment_end);
                    realtime_trace(session, oss.str());
                }
                // Phase 6D-1: clear pre-roll after auto-final; next utterance will rebuild
                session->asr_pre_roll.clear();
                session->total_samples = 0;
                session->chunk_samples = 0;
                session->vad_state = VadState::kSilence;
                session->vad_last_speech_time_ms = 0.0;
                session->vad_session_start_ms = 0.0;
                session->vad_elapsed_ms = 0.0;
                session->vad_speech_count = 0;
                session->partial.clear();
                session->partial_last_sample_count = 0;
                session->partial_speech_start_sample = -1;
                session->vad_end_sample = -1;
                session->vad_soft_start_frames = 0;
                session->vad_soft_start_sample = -1;
                clear_realtime_speculative_final(session, nullptr);
                if (session->vad_silero) {
                    silero_vad_reset(session->vad_silero);
                    realtime_trace(session, "silero_vad_reset after_auto_final_discard");
                }
                if (session->realtime_engine == "moonshine" && session->moonshine_streaming && session->moonshine) {
                    session->moonshine_streaming = session->moonshine->reset_stream(session->moonshine_preset.leading_padding_ms, false);
                    session->moonshine_stream_sample_count = 0;
                    realtime_trace(session, session->moonshine_streaming ? "moon_stream_reset ok" : "moon_stream_reset failed");
                }
                session->auto_transcribing = false;
            }
        } else if (session->vad_state == VadState::kSpeech &&
                   !session->auto_transcribing &&
                   !session->partial_transcribing &&
                   session->partial_speech_start_sample >= 0 &&
                   samples.size() > static_cast<size_t>(session->partial_speech_start_sample) &&
                   samples.size() - static_cast<size_t>(session->partial_speech_start_sample) >= 6000 &&
                   samples.size() - static_cast<size_t>(session->partial_last_sample_count) >= 4800) {
            session->partial_last_sample_count = static_cast<int64_t>(samples.size());
            std::vector<int16_t> speech_samples(
                samples.begin() + session->partial_speech_start_sample,
                samples.end());
            {
                std::ostringstream oss;
                oss << "partial_trigger total=" << samples.size()
                    << " speech_start=" << session->partial_speech_start_sample
                    << " speech_samples=" << speech_samples.size()
                    << " last_count=" << session->partial_last_sample_count;
                realtime_trace(session, oss.str());
            }
            transcribe_realtime_partial(session, speech_samples);
        }
    }
#else
    // Linux: minimal capture + VAD processing (no ASR yet)
    {
        const bool endpoint_trace = linux_endpoint_trace_enabled();
        auto samples = session->capture.get_samples();
        static constexpr size_t REALTIME_SILENCE_BUFFER_CAP_SAMPLES = 320000;
        static constexpr size_t REALTIME_SILENCE_BUFFER_KEEP_SAMPLES = 16000;
        if (session->state == RealtimeState::kRecording &&
            (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected) &&
            session->vad_speech_count == 0 &&
            samples.size() > REALTIME_SILENCE_BUFFER_CAP_SAMPLES) {
            const size_t discard_count = samples.size() > REALTIME_SILENCE_BUFFER_KEEP_SAMPLES
                ? samples.size() - REALTIME_SILENCE_BUFFER_KEEP_SAMPLES : 0;
            if (discard_count > 0) {
                session->capture.discard_samples(discard_count);
                session->asr_pre_roll.clear();
                session->total_samples = 0;
                session->chunk_samples = 0;
                session->vad_elapsed_ms = 0.0;
                session->vad_session_start_ms = 0.0;
                session->vad_last_speech_time_ms = 0.0;
                if (session->vad_silero) silero_vad_reset(session->vad_silero);
                samples = session->capture.get_samples();
            }
        }
        session->total_samples = static_cast<int64_t>(samples.size());

        if (session->state == RealtimeState::kRecording) {
            double current_time_ms = session->total_samples * 1000.0 / 16000.0;
            if (session->vad_session_start_ms == 0.0 && session->total_samples > 0)
                session->vad_session_start_ms = current_time_ms;
            session->vad_elapsed_ms = current_time_ms;

            if (session->vad_silero) {
                size_t new_start = static_cast<size_t>(session->chunk_samples);
                size_t new_end = samples.size();
                // If a short segment ended before the minimum final size, keep
                // its end boundary until it can be discarded and reset. The
                // unscored suffix then belongs to the next segment.
                if (new_end > new_start && session->vad_state != VadState::kEndDetected) {
                    const int16_t* pcm_ptr = samples.data();
                    for (size_t frame = new_start; frame + 512 <= new_end; frame += 512) {
                        const auto state_before = session->vad_state;
                        const double last_speech_before = session->vad_last_speech_time_ms;
                        double prob = silero_vad_score(session->vad_silero,
                                                       pcm_ptr + frame, 512, 16000);
                        // Keep a short tail for the next poll; a VAD end can also
                        // break this loop before the end of the captured buffer.
                        session->chunk_samples = static_cast<int64_t>(frame + 512);
                        if (prob < 0.0) {
                            FrameStats ef = compute_frame_stats(pcm_ptr + frame, 512);
                            prob = (ef.rms >= 450.0 || ef.peak >= 1400.0)
                                ? session->vad_silero_continue_threshold : 0.0;
                        }
                        const double ft_ms = static_cast<double>(frame + 512) * 1000.0 / 16000.0;
                        /* P0 2026-07-14: noise-adaptive start threshold (parity with Windows).
                         * Linux noise_profile is not yet populated, so this currently
                         * resolves to the base threshold. */
                        const double effective_start_threshold = realtime_effective_start_threshold(
                            session->vad_silero_start_threshold,
                            session->noise_profile.ready,
                            session->noise_profile.silero_p90);
                        const bool hard_start = prob >= effective_start_threshold;
                        const bool soft_start = prob >= session->vad_silero_soft_start_threshold &&
                            (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected);
                        if (hard_start || soft_start) {
                            if (session->speculative_final_attempted &&
                                session->vad_state == VadState::kSpeech)
                                clear_realtime_speculative_final(session, "speech_resumed");
                            if (session->vad_state == VadState::kSilence || session->vad_state == VadState::kEndDetected) {
                                session->vad_speech_count++;
                                notify_vad_start(session);
                                session->partial_speech_start_sample = static_cast<int64_t>(frame);
                                session->final_detected_at_ms = 0;
                                session->vad_start_prob = prob; // P0: feed hallucination gate
                            }
                            session->vad_state = VadState::kSpeech;
                            session->vad_last_speech_time_ms = ft_ms;
                        } else if (prob >= session->vad_silero_continue_threshold &&
                                   session->vad_state == VadState::kSpeech) {
                            if (session->speculative_final_attempted)
                                clear_realtime_speculative_final(session, "speech_resumed");
                            session->vad_last_speech_time_ms = ft_ms;
                        } else if (session->vad_state == VadState::kSpeech) {
                            const double silence_dur = ft_ms - session->vad_last_speech_time_ms;
                            if (session->vad_silence_duration_ms > REALTIME_SPECULATIVE_FINAL_DELAY_MS &&
                                silence_dur >= REALTIME_SPECULATIVE_FINAL_DELAY_MS &&
                                !session->speculative_final_attempted) {
                                const size_t spec_start = linux_asr_pre_roll_start(session->partial_speech_start_sample >= 0
                                    ? static_cast<size_t>(session->partial_speech_start_sample) : 0);
                                std::vector<int16_t> spec_segment;
                                if (spec_start < frame + 512)
                                    spec_segment.assign(samples.begin() + spec_start, samples.begin() + frame + 512);
                                start_realtime_speculative_final(session, spec_segment, session->dump_seg_counter);
                            }
                            if (silence_dur >= session->vad_silence_duration_ms) {
                                session->vad_state = VadState::kEndDetected;
                                session->vad_end_sample = static_cast<int64_t>(frame + 512);
                                session->final_detected_at_ms = unix_epoch_ms();
                            }
                        } else {
                            session->vad_state = VadState::kSilence;
                        }
                        if (endpoint_trace) {
                            const auto stats = compute_frame_stats(pcm_ptr + frame, 512);
                            fprintf(stderr, "[PrivateEndpoint] frame seg=%d end_sample=%zu prob=%.9f rms=%.3f peak=%d before=%d after=%d last_before_ms=%.3f last_after_ms=%.3f silence_limit_ms=%.3f\n",
                                session->dump_seg_counter, frame + 512, prob, stats.rms, stats.peak,
                                static_cast<int>(state_before), static_cast<int>(session->vad_state),
                                last_speech_before, session->vad_last_speech_time_ms, session->vad_silence_duration_ms);
                        }
                        if (session->vad_state == VadState::kEndDetected) break;
                    }
                }
            } else {
                // Fallback: RMS-based VAD
                double recent_rms = session->capture.get_recent_rms_level(1600);
                update_vad_state(session, recent_rms, session->vad_elapsed_ms);
            }

            if (session->vad_state == VadState::kEndDetected &&
                !session->auto_transcribing &&
                session->vad_speech_count > 0 &&
                samples.size() >= 8000) {
                session->auto_transcribing = true;
                size_t segment_end = session->vad_end_sample > 0
                    ? static_cast<size_t>(session->vad_end_sample)
                    : samples.size();
                if (segment_end > samples.size()) segment_end = samples.size();
                size_t segment_start = session->partial_speech_start_sample >= 0
                    ? static_cast<size_t>(session->partial_speech_start_sample)
                    : 0;
                if (segment_start > segment_end) segment_start = 0;
                else segment_start = linux_asr_pre_roll_start(segment_start);
                std::vector<int16_t> segment_samples(samples.begin() + segment_start,
                                                    samples.begin() + segment_end);
                const int seg_id = session->dump_seg_counter++;
                if (endpoint_trace)
                    fprintf(stderr, "[PrivateEndpoint] final seg=%d start_sample=%zu end_sample=%zu last_speech_ms=%.3f detected_ms=%lld reason=%s\n",
                        seg_id, segment_start, segment_end, session->vad_last_speech_time_ms,
                        static_cast<long long>(session->final_detected_at_ms),
                        session->vad_silero ? "silero_silence" : "rms_fallback");
                PcmStats seg_stats = compute_pcm_stats(segment_samples);
                std::string gate_reason;
                const bool skip_final = should_skip_realtime_final_input(seg_stats, gate_reason);
                if (skip_final && (is_addon_engine(session->realtime_engine) ||
                    (session->realtime_engine == "joint_auto" && session->addon_joint_requested))) {
                    reset_joint_trace(session);
                    session->joint_existing_status = "RejectedByExistingGate";
                    session->joint_selection_reason = "existing_gate:" + gate_reason;
                    session->joint_existing_wall_ms = session->joint_total_wall_ms = 0;
                }
                const bool async_validation_queued = !skip_final &&
                    enqueue_realtime_whisper_validation(
                        session, segment_samples, seg_stats, seg_id);
                const bool speculative_committed = !skip_final && !async_validation_queued &&
                    commit_realtime_speculative_final(session, segment_samples, seg_stats,
                                                      seg_id, 0.0);
                if (!skip_final && !async_validation_queued && !speculative_committed) {
                    const auto existing_start = std::chrono::steady_clock::now();
                    const bool use_addon = session->realtime_engine == "qwen3_asr_ja";
                    if (use_addon || session->addon_joint_requested) reset_joint_trace(session);
                    // The additional Joint candidate must compare only against
                    // this segment, never a retained final from an earlier one.
                    // Preserve the existing base-only last-final contract.
                    if (use_addon || (session->realtime_engine == "joint_auto" && session->addon_joint_requested)) {
                        session->final_text.clear();
                        session->has_transcript = false;
                        session->selected_engine = "none";
                        session->decision_reason.clear();
                        session->latency_ms = 0;
                    }
                    // Linux ASR core: matching Windows V2 JointAuto behavior.
                    // joint_auto: run both Moonshine and Whisper, apply fast-path selection.
                    // C# SenseVoice/Paraformer overlay is applied after for JA/ZH/KO.
                    const bool use_joint_auto = (session->realtime_engine == "joint_auto");
                    // Convert PCM to float32 once
                    std::vector<float> pcm_f32(segment_samples.size());
                    for (size_t i = 0; i < segment_samples.size(); ++i)
                        pcm_f32[i] = segment_samples[i] / 32768.0f;

                    std::string moon_text, whisper_text;
                    int moon_latency_ms = -1, whisper_latency_ms = -1;
                    bool moonshine_attempted = false;
                    bool moonshine_ran = false, whisper_ran = false;
                    bool whisper_confidence_rejected = false;

                    // Run Moonshine (joint_auto or moonshine engine)
                    if (session->moonshine &&
                        (use_joint_auto || session->realtime_engine == "moonshine")) {
                        moonshine_attempted = true;
                        auto t0 = std::chrono::steady_clock::now();
                        auto results = session->moonshine->transcribe(
                            pcm_f32.data(), static_cast<int>(pcm_f32.size()), 16000);
                        moon_latency_ms = static_cast<int>(
                            std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - t0).count());
                        if (!results.empty() && results[0].success && !results[0].text.empty()) {
                            moon_text = results[0].text;
                            moonshine_ran = true;
                            session->moonshine_latest_candidate.seg_id = seg_id;
                            session->moonshine_latest_candidate.text = moon_text;
                            session->moonshine_latest_candidate.latency_ms = moon_latency_ms;
                            session->moonshine_latest_candidate.source = "linux_one_shot";
                        }
                    }

                    /* Windows parity: a realtime joint_auto poll must not run a
                     * multi-second synchronous Whisper fallback after Moonshine
                     * already failed. Whisper-only mode remains unchanged. */
                    const bool skip_whisper_after_moonshine_failure =
                        use_joint_auto && moonshine_attempted && !moonshine_ran;
                    const bool skip_whisper_while_async_busy =
                        use_joint_auto && realtime_whisper_validation_busy(session);

                    // Run Whisper (joint_auto or whisper engine)
                    if (session->engine &&
                        (use_joint_auto || session->realtime_engine == "whisper") &&
                        !skip_whisper_after_moonshine_failure &&
                        !skip_whisper_while_async_busy) {
                        auto t0 = std::chrono::steady_clock::now();
                        mobmate::WhisperEngine::TranscribeOpts opts;
                        opts.language = session->language;
                        opts.n_threads = session->threads;
                        auto detailed = session->engine->transcribe_detailed(
                            pcm_f32.data(), static_cast<int>(pcm_f32.size()), opts);
                        whisper_latency_ms = static_cast<int>(
                            std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - t0).count());
                        if (detailed) {
                            session->whisper_avg_token_p = detailed->avg_token_p;
                            session->whisper_max_no_speech_prob = detailed->max_no_speech_prob;
                            session->whisper_confidence_score = detailed->confidence_score;
                        }
                        if (detailed && !detailed->text.empty()) {
                            /* 2026-07-14: decode-time confidence gate (Windows parity) */
                            std::string conf_gate_reason;
                            if (whisper_confidence_rejects_final(detailed->max_no_speech_prob,
                                                                 detailed->avg_token_p,
                                                                 detailed->text, conf_gate_reason)) {
                                whisper_confidence_rejected = true;
                                fprintf(stderr,
                                        "[realtime] asr_final_confidence_drop reason=%s no_speech=%.3f token_p=%.3f text=%s\n",
                                        conf_gate_reason.c_str(),
                                        detailed->max_no_speech_prob,
                                        detailed->avg_token_p,
                                        detailed->text.c_str());
                            } else {
                                whisper_text = detailed->text;
                                whisper_ran = true;
                            }
                        }
                    }

                    // Fast-path selection: matching Windows V2 JointAuto logic.
                    // Moonshine wins if fast (≤180ms), short segment, and not much shorter than Whisper.
                    if (use_joint_auto && moonshine_ran && whisper_ran) {
                        const int len_delta = static_cast<int>(moon_text.size()) -
                                              static_cast<int>(whisper_text.size());
                        const bool moonshine_fast_path =
                            !moon_text.empty() &&
                            moon_latency_ms <= 180 &&
                            seg_stats.active_duration_ms <= 4000.0 &&
                            len_delta >= -4;
                        if (moonshine_fast_path) {
                            session->final_text = moon_text;
                            session->selected_engine = "moonshine";
                            session->decision_reason = "linux_joint_fastpath_moonshine";
                            session->latency_ms = moon_latency_ms;
                        } else if (!whisper_text.empty()) {
                            session->final_text = whisper_text;
                            session->selected_engine = "whisper";
                            session->decision_reason = "linux_joint_fallback_whisper";
                            session->latency_ms = whisper_latency_ms;
                        } else {
                            session->final_text = moon_text;
                            session->selected_engine = "moonshine";
                            session->decision_reason = "linux_joint_last_resort_moonshine";
                            session->latency_ms = moon_latency_ms;
                        }
                        session->has_transcript = !session->final_text.empty();
                    } else if (moonshine_ran) {
                        session->final_text = moon_text;
                        session->selected_engine = "moonshine";
                        session->decision_reason = "linux_moonshine";
                        session->latency_ms = moon_latency_ms;
                        session->has_transcript = true;
                    } else if (whisper_ran) {
                        session->final_text = whisper_text;
                        session->selected_engine = "whisper";
                        session->decision_reason = "linux_whisper";
                        session->latency_ms = whisper_latency_ms;
                        session->has_transcript = true;
                    }

                    // P0 2026-07-14: hallucination gate (parity with Windows call sites)
                    if (session->has_transcript && !session->final_text.empty()) {
                        std::string text_gate_reason;
                        if (!should_accept_realtime_final_text(session->final_text, seg_stats,
                                                               session->selected_engine, session->language, text_gate_reason,
                                                               &segment_samples, session->vad_start_prob)) {
                            fprintf(stderr, "[realtime] asr_final_text_drop reason=%s text=%s\n",
                                    text_gate_reason.c_str(), session->final_text.c_str());
                            session->final_text.clear();
                            session->has_transcript = false;
                            session->selected_engine = "none";
                            session->decision_reason = "hallucination_gate";
                        }
                    }

                    if (!moonshine_ran && !whisper_ran) {
                        session->decision_reason = skip_whisper_after_moonshine_failure
                            ? "linux_joint_moonshine_failed_skip_whisper"
                            : whisper_confidence_rejected && use_joint_auto && session->addon_joint_requested
                                ? "whisper_confidence_gate" : "linux_pcm_ready";
                    }
                    if (use_addon) {
                        char text[65536] = {};
                        int64_t inference_ms = -1;
                        const auto callback = g_addon_transcriber_v2.load();
                        session->addon_candidate_attempted = true;
                        const int code = callback ? callback(session->addon_engine_id.c_str(), session->language.c_str(),
                            session->addon_language_mode.c_str(), segment_samples.data(), static_cast<int>(segment_samples.size()),
                            16000, text, static_cast<int>(sizeof(text)), &inference_ms) : -1;
                        text[sizeof(text) - 1] = '\0';
                        session->addon_candidate_success = code == 0;
                        session->addon_candidate_text = code == 0 ? std::string(text) : "";
                        session->addon_candidate_ms = inference_ms;
                        session->addon_wall_ms = session->addon_candidate_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() - existing_start).count();
                        std::string text_gate_reason;
                        const bool accepted = code == 0 && !session->addon_candidate_text.empty() &&
                            should_accept_realtime_final_text(session->addon_candidate_text, seg_stats, session->addon_engine_id,
                                session->language, text_gate_reason, &segment_samples, session->vad_start_prob);
                        session->final_text = accepted ? session->addon_candidate_text : "";
                        session->has_transcript = accepted;
                        session->selected_engine = accepted ? session->addon_engine_id : "none";
                        session->decision_reason = accepted ? "addon_selected" : code != 0 ? "addon_inference_failed" : "hallucination_gate";
                        session->latency_ms = code == 0 ? static_cast<int>(inference_ms) : 0;
                    } else if (use_joint_auto && session->addon_joint_requested) {
                        session->joint_moonshine = {moonshine_attempted, moonshine_ran,
                            moonshine_ran && session->selected_engine == "moonshine", moon_text, "linux_one_shot", moon_latency_ms};
                        session->joint_whisper = {whisper_latency_ms >= 0, whisper_ran,
                            whisper_ran && session->selected_engine == "whisper", whisper_text, "linux_one_shot", whisper_latency_ms};
                        // Keep the Linux base selector; no new JA arbitration policy.
                        apply_joint_addon(session, segment_samples, true, &seg_stats, 0, existing_start);
                    }

                    // Always store PCM for C# SenseVoice/Paraformer overlay (JA/ZH/KO)
                    session->last_final_pcm16 = std::move(segment_samples);
                    session->last_final_pcm_sample_rate = 16000;
                    session->last_final_pcm_reported = false;
                } else if (!session->has_transcript) {
                    session->decision_reason = gate_reason;
                }
                if (!skip_final && session->realtime_engine == "joint_auto" && session->addon_joint_requested) {
                    if (async_validation_queued) {
                        reset_joint_trace(session);
                        session->joint_existing_status = "Pending";
                        session->joint_selection_reason = "existing_async_validation_pending";
                    } else if (speculative_committed) {
                        reset_joint_trace(session);
                        session->joint_moonshine.attempted = session->joint_moonshine.success = session->joint_moonshine.accepted = true;
                        session->joint_moonshine.text = session->final_text;
                        session->joint_moonshine.latency_ms = session->latency_ms;
                        apply_joint_addon(session, segment_samples, true, &seg_stats, 0, {});
                    }
                }
                session->capture.discard_samples(segment_end);
                session->asr_pre_roll.clear();
                session->total_samples = 0;
                session->chunk_samples = 0;
                session->vad_state = VadState::kSilence;
                session->vad_last_speech_time_ms = 0.0;
                session->vad_session_start_ms = 0.0;
                session->vad_elapsed_ms = 0.0;
                session->vad_speech_count = 0;
                session->partial.clear();
                session->partial_last_sample_count = 0;
                session->partial_speech_start_sample = -1;
                session->vad_end_sample = -1;
                clear_realtime_speculative_final(session, nullptr);
                if (session->vad_silero) silero_vad_reset(session->vad_silero);
                session->auto_transcribing = false;
            }
        }
    }
#endif

#ifndef _WIN32
    if (!capture_lost) {
#endif
        publish_realtime_whisper_validation(session);
#ifndef _WIN32
    }
    if (!capture_lost && watch_capture && session->capture.source_lost()) {
        previous->restore(*session);
        fail_linux_capture(session);
    }
#endif

    std::string json = "{";
    json += "\"state\":\"" + std::string(realtime_state_to_string(session->state)) + "\",";
    json += "\"partial\":\"" + json_escape(session->partial) + "\",";
    json += "\"final\":\"" + json_escape(session->final_text) + "\",";
    std::string selected_text = session->final_text;
    std::string selected_engine = selected_text.empty() ? "none" : session->selected_engine;
    std::string decision_reason = selected_text.empty()
        ? (session->decision_reason.empty() ? "no_valid_transcript" : session->decision_reason)
        : session->decision_reason;
    json += "\"selected_text\":\"" + json_escape(selected_text) + "\",";
    json += "\"selected_engine\":\"" + json_escape(selected_engine) + "\",";
    json += "\"decision_reason\":\"" + json_escape(decision_reason) + "\",";
    json += "\"language\":\"" + json_escape(session->language) + "\",";
    json += "\"resolvedScene\":\"" + json_escape(session->resolved_scene.empty() ? session->scene : session->resolved_scene) + "\",";
    /* 2026-07-14: decode-time Whisper confidence is now wired through
     * (avg_token_p * (1 - max_no_speech_prob) from whisper_bridge).
     * Moonshine finals remain unavailable (no word confidence). */
    const bool conf_available = selected_engine == "whisper" &&
        session->whisper_confidence_score >= 0.0f;
    const std::string conf_score_str = conf_available
        ? std::to_string(session->whisper_confidence_score)
        : std::string("null");
    json += "\"confidence_available\":" + std::string(conf_available ? "true" : "false") + ",";
    json += "\"confidence_score\":" + conf_score_str + ",";
    if (!conf_available) {
        const char* confidence_reason = selected_engine == "moonshine" ? "moonshine_word_confidence_unavailable"
            : is_addon_engine(selected_engine) ? "addon_confidence_unavailable" : "whisper_confidence_unavailable";
        json += "\"confidence_unavailable_reason\":\"" + std::string(confidence_reason) + "\",";
    }
    json += "\"candidates\":[{\"engine\":\"" + json_escape(selected_engine)
        + "\",\"text\":\"" + json_escape(selected_text)
        + "\",\"confidence_available\":" + (conf_available ? "true" : "false")
        + ",\"confidence_score\":" + conf_score_str + "}";
    if ((is_addon_engine(session->realtime_engine) || session->realtime_engine == "joint_auto") && session->addon_candidate_attempted) {
        json += ",{\"engine\":\"" + json_escape(session->addon_engine_id) + "\",\"text\":\"" + json_escape(session->addon_candidate_text)
            + "\",\"success\":" + (session->addon_candidate_success ? "true" : "false")
            + ",\"latencyMs\":" + std::to_string(session->addon_candidate_ms)
            + ",\"confidence_available\":false,\"confidence_score\":null}";
    }
#ifdef _WIN32
    if (session->selected_engine != "paraformer_zh" &&
        session->realtime_engine == "joint_auto" && session->language == "zh" &&
        session->paraformer_zh_candidate_seg_id >= 0) {
        json += ",{\"engine\":\"paraformer_zh\""
            + std::string(",\"text\":\"") + json_escape(session->paraformer_zh_candidate_text) + "\""
            + ",\"confidence_available\":false,\"confidence_score\":null"
            + ",\"source\":\"joint_zh_candidate\"";
        if (!session->paraformer_zh_candidate_error.empty()) {
            json += ",\"error\":\"" + json_escape(session->paraformer_zh_candidate_error) + "\"";
        }
        json += "}";
    } else if (session->experimental_asr_candidates_enabled &&
        !session->experimental_asr_candidate_engine.empty() &&
        (session->experimental_asr_candidate_language.empty() ||
         session->experimental_asr_candidate_language == session->language)) {
        json += ",{\"engine\":\"" + json_escape(session->experimental_asr_candidate_engine)
            + "\",\"text\":\"\",\"confidence_available\":false,\"confidence_score\":null"
            + ",\"source\":\"experimental_hook\",\"error\":\"engine_not_registered\"}";
    }
#endif
    json += "],";
    json += "\"latencyMs\":" + std::to_string(session->latency_ms);
    if (is_addon_engine(session->realtime_engine) || (session->realtime_engine == "joint_auto" && session->addon_joint_requested))
        json += ",\"addonWallMs\":" + std::to_string(session->addon_wall_ms);
    if (session->realtime_engine == "joint_auto" && session->addon_joint_requested) {
        json += ",\"jointExistingStatus\":\"" + json_escape(session->joint_existing_status) + "\"";
        json += ",\"jointAddonStatus\":\"" + json_escape(session->joint_addon_status) + "\"";
        json += ",\"jointSelectionReason\":\"" + json_escape(session->joint_selection_reason) + "\"";
        json += ",\"existingJointWallMs\":" + std::to_string(session->joint_existing_wall_ms);
        json += ",\"jointTotalWallMs\":" + std::to_string(session->joint_total_wall_ms);
        json += ",\"addonAddedWaitMs\":" + std::to_string(session->joint_added_wait_ms);
    }
    json += ",\"finalDetectedAtMs\":" + std::to_string(session->final_detected_at_ms);
#ifdef _WIN32
    json += ",\"captureSamples\":" + std::to_string(session->total_samples);
    json += ",\"captureRms\":" + std::to_string(session->capture.get_rms_level());
    json += ",\"captureAgcGain\":" + std::to_string(session->capture.get_agc_gain());
    if (!session->last_final_pcm16.empty() && !session->last_final_pcm_reported) {
        auto pcm_bytes = pcm16_to_bytes(session->last_final_pcm16);
        json += ",\"finalPcm16Base64\":\"" + base64_encode_bytes(pcm_bytes.data(), pcm_bytes.size()) + "\"";
        json += ",\"finalPcmSampleRate\":" + std::to_string(session->last_final_pcm_sample_rate);
        session->last_final_pcm_reported = true;
    }
    bool engine_loaded = is_addon_engine(session->realtime_engine)
        ? addon_dispatch_available(session->addon_engine_id, session->language, session->addon_language_mode)
        : session->realtime_engine == "moonshine" ? static_cast<bool>(session->moonshine)
        : session->realtime_engine == "paraformer_zh" ? !pf_default_model_dir().empty()
        : static_cast<bool>(session->engine);
    json += ",\"engineLoaded\":" + std::string(engine_loaded ? "true" : "false");
#else
    json += ",\"captureSamples\":" + std::to_string(session->total_samples);
    json += ",\"captureRms\":" + std::to_string(session->capture.get_rms_level());
    json += ",\"captureAgcGain\":" + std::to_string(session->capture.get_agc_gain());
    if (!session->last_final_pcm16.empty() && !session->last_final_pcm_reported) {
        auto pcm_bytes = pcm16_to_bytes(session->last_final_pcm16);
        json += ",\"finalPcm16Base64\":\"" + base64_encode_bytes(pcm_bytes.data(), pcm_bytes.size()) + "\"";
        json += ",\"finalPcmSampleRate\":" + std::to_string(session->last_final_pcm_sample_rate);
        session->last_final_pcm_reported = true;
    }
    // Phase 14-1: reflect actual Moonshine state on Linux
    bool engine_loaded_linux = static_cast<bool>(session->moonshine) || static_cast<bool>(session->engine) || session->addon_candidate_success;
    json += ",\"engineLoaded\":" + std::string(engine_loaded_linux ? "true" : "false");
#endif
    json += ",\"vadState\":\"" + std::string(vad_state_to_string(session->vad_state)) + "\",";
    json += "\"vadSpeechCount\":" + std::to_string(session->vad_speech_count);
    if (!session->error.empty()) {
        json += ",\"error\":\"" + json_escape(session->error) + "\"";
    } else {
        json += ",\"error\":null";
    }
    json += "}";
    return json;
}

MMV2_API int mmv2_realtime_create(const char* options_json_utf8, void** out_handle) {
    if (!out_handle) return -1;
    *out_handle = nullptr;

    auto session = new RealtimeSession();

#ifdef _WIN32
    // Parse optional ASR engine parameters from options JSON
    if (options_json_utf8) {
        std::string whisper_dll_dir = json_get_string(options_json_utf8, "whisperDllDir", "");
        std::string model_path = json_get_string(options_json_utf8, "modelPath", "");
        session->language = json_get_string(options_json_utf8, "language", "");
        session->addon_joint_requested = json_get_bool(options_json_utf8, "addonJointRequested", false);
        session->addon_joint_language = json_get_string(options_json_utf8, "addonJointLanguage", "ja");
        session->addon_engine_id = json_get_string(options_json_utf8, "addonEngineId", "haas_ja");
        session->addon_language_mode = json_get_string(options_json_utf8, "addonLanguageMode", "requested");
        session->addon_model_id = json_get_string(options_json_utf8, "addonModelId", "");
        session->addon_model_revision = json_get_string(options_json_utf8, "addonModelRevision", "");
        session->production_replay = json_get_bool(options_json_utf8, "productionReplay", false);
        session->experimental_asr_candidates_enabled = json_get_bool(options_json_utf8, "experimentalAsrCandidatesEnabled", false);
        session->experimental_asr_candidate_engine = json_get_string(options_json_utf8, "experimentalAsrCandidateEngine", "");
        session->experimental_asr_candidate_language = json_get_string(options_json_utf8, "experimentalAsrCandidateLanguage", "");
        session->experimental_asr_candidate_model_dir = json_get_string(options_json_utf8, "experimentalAsrCandidateModelDir", "");
        session->threads = json_get_int(options_json_utf8, "threads", 8);  /* Phase 6B: match V1 default */
        session->initial_prompt = json_get_string(options_json_utf8, "initialPrompt", "");
        session->use_gpu = json_get_bool(options_json_utf8, "useGpu", false);
        session->use_prefilter = json_get_bool(options_json_utf8, "usePrefilter", true);
        session->prefilter_profile = json_get_string(options_json_utf8, "prefilterProfile", "auto");
        session->copy_final_to_clipboard = json_get_bool(options_json_utf8, "copyFinalToClipboard", false);
        session->scene = json_get_string(options_json_utf8, "scene", "");
        session->vad_silence_duration_ms = static_cast<double>(json_get_int(options_json_utf8, "vadSilenceDurationMs", static_cast<int>(default_realtime_vad_silence_ms(session->scene))));
        /* P0 2026-07-14: parse Silero thresholds on Windows too (previously Linux-only,
         * leaving Windows on the lower struct defaults). Defaults match Linux Program.cs. */
        session->vad_silero_start_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroStartThresholdPermille", 550)) / 1000.0;
        session->vad_silero_continue_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroContinueThresholdPermille", 400)) / 1000.0;
        session->vad_silero_soft_start_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroSoftStartThresholdPermille", 300)) / 1000.0;
        session->capture_device_id = json_get_string(options_json_utf8, "captureDeviceId", "");
        session->vad_silero_model_path = json_get_string(options_json_utf8, "sileroVadModelPath", "");
        session->vad_silero_ort_dll_dir = json_get_string(options_json_utf8, "onnxRuntimeDllDir", "");
        session->realtime_engine = json_get_string(options_json_utf8, "engine", "whisper");
        session->resolved_scene = session->scene == "auto" ? "short_command" : session->scene;
        if (is_addon_engine(session->realtime_engine)) session->addon_engine_id = session->realtime_engine;
        if (!is_addon_engine(session->addon_engine_id) ||
            (session->addon_language_mode != "requested" && session->addon_language_mode != "auto") ||
            (session->addon_language_mode == "auto" && session->addon_engine_id != "qwen3_asr_ja") ||
            (is_addon_engine(session->realtime_engine) &&
             !addon_dispatch_available(session->addon_engine_id, session->language, session->addon_language_mode))) {
            delete session;
            return -1;
        }
        // Phase 7A: Speaker identification options
        session->speaker_id_enabled = json_get_bool(options_json_utf8, "speakerIdEnabled", false) || gSpeakerIdEnabled;
        session->speaker_profile_path = json_get_string(options_json_utf8, "speakerProfilePath", "");
        if (session->speaker_id_enabled) {
            session->speaker_profile = std::make_unique<SpeakerProfile>();
            if (!session->speaker_profile_path.empty()) {
                speaker_load(*session->speaker_profile, session->speaker_profile_path.c_str());
                gSpeakerProfilePath = session->speaker_profile_path;
            }
            gActiveSpeakerProfile = session->speaker_profile.get();
        }
        const bool load_moonshine = session->realtime_engine == "moonshine" || session->realtime_engine == "joint_auto";
        const bool load_whisper = session->realtime_engine != "moonshine" && session->realtime_engine != "paraformer_zh" && !is_addon_engine(session->realtime_engine);
        if (load_moonshine) {
            std::string moonshine_dll_dir = json_get_string(options_json_utf8, "moonshineDllDir", "");
            std::string moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelPath", "");
            if (moonshine_model_path.empty()) {
                moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelRoot", "");
            }
            if (moonshine_dll_dir.empty() || moonshine_model_path.empty() || session->language.empty()) {
                session->state = RealtimeState::kFailed;
                session->error = "moonshine realtime config missing";
                OutputDebugStringA("[mmv2_realtime_create] FAIL: moonshine config missing\n");
                delete session;
                return -1;
            }
            session->moonshine_preset = get_moonshine_product_preset(session->language, session->scene);
            session->moonshine = std::make_unique<mobmate::MoonshineBridge>();
            if (!session->moonshine->init(moonshine_dll_dir)) {
                session->state = RealtimeState::kFailed;
                session->error = "failed to load moonshine.dll";
                OutputDebugStringA("[mmv2_realtime_create] FAIL: moonshine.dll load\n");
                delete session;
                return -1;
            }
            int default_arch = json_get_int(options_json_utf8, "moonshineArch", mobmate::MoonshineBridge::ARCH_TINY);
            auto model_choice = resolve_moonshine_product_model(moonshine_model_path, session->language, default_arch);
            session->moonshine_stream_supported = model_choice.second >= mobmate::MoonshineBridge::ARCH_TINY_STREAMING;
            mobmate::MoonshineBridge::LoadOptions load_options;
            load_options.vad_window_duration = session->moonshine_preset.vad_window_sec;
            load_options.vad_look_behind_sample_count = session->moonshine_preset.vad_look_behind_samples;
            load_options.vad_threshold = session->moonshine_preset.vad_threshold;
            load_options.max_tokens_per_second = session->moonshine_preset.max_tokens_per_second;
            if (!session->moonshine->load_model(model_choice.first, model_choice.second, load_options)) {
                session->state = RealtimeState::kFailed;
                session->error = "Moonshine model load failed: " + model_choice.first;
                OutputDebugStringA(("[mmv2_realtime_create] FAIL: Moonshine model load: " + model_choice.first + "\n").c_str());
                delete session;
                return -1;
            }
        }
        if (load_whisper && !whisper_dll_dir.empty() && !model_path.empty()) {
            std::string err_msg;
            session->engine = mobmate::WhisperEngine::load(whisper_dll_dir, model_path, err_msg, session->use_gpu);
            if (!session->engine) {
                session->state = RealtimeState::kFailed;
                session->engine_error = err_msg;
                session->error = "engine load: " + err_msg;
                OutputDebugStringA(("[mmv2_realtime_create] FAIL: Whisper engine load: " + err_msg + "\n").c_str());
                delete session;
                return -1;
            }
        }
    }
    /* Phase 6B: initialize dump directory (relative to working directory) */
    {
        std::filesystem::path dump_fs_path =
            std::filesystem::current_path() / "temp" / "realtime";
        std::error_code ec;
        std::filesystem::create_directories(dump_fs_path, ec);
        session->dump_dir = dump_fs_path.string();
        session->dump_seg_counter = 0;
    }
#else
    // Linux: parse minimum options for capture + VAD
    if (options_json_utf8) {
        session->capture_device_id = json_get_string(options_json_utf8, "captureDeviceId", "");
        session->language = json_get_string(options_json_utf8, "language", "");
        session->scene = json_get_string(options_json_utf8, "scene", "");
        session->vad_silero_model_path = json_get_string(options_json_utf8, "sileroVadModelPath", "");
        session->vad_silero_ort_dll_dir = json_get_string(options_json_utf8, "onnxRuntimeDllDir", "");
        session->realtime_engine = json_get_string(options_json_utf8, "engine", "");
        session->addon_joint_requested = json_get_bool(options_json_utf8, "addonJointRequested", false);
        session->addon_joint_language = json_get_string(options_json_utf8, "addonJointLanguage", "ja");
        session->addon_engine_id = json_get_string(options_json_utf8, "addonEngineId", "haas_ja");
        session->addon_language_mode = json_get_string(options_json_utf8, "addonLanguageMode", "requested");
        session->addon_model_id = json_get_string(options_json_utf8, "addonModelId", "");
        session->addon_model_revision = json_get_string(options_json_utf8, "addonModelRevision", "");
        if (is_addon_engine(session->realtime_engine)) session->addon_engine_id = session->realtime_engine;
        if ((is_addon_engine(session->realtime_engine) &&
             (session->realtime_engine != "qwen3_asr_ja" || !addon_language_supported(session->addon_engine_id, session->language) || !g_addon_transcriber_v2.load())) ||
            (session->addon_joint_requested && session->addon_engine_id != "qwen3_asr_ja") ||
            (session->addon_language_mode != "requested" && session->addon_language_mode != "auto")) {
            delete session;
            return -1;
        }
        session->resolved_scene = session->scene == "auto" ? "short_command" : session->scene;
        session->vad_rms_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadRmsThreshold", 500));
        session->vad_silence_duration_ms = static_cast<double>(json_get_int(options_json_utf8, "vadSilenceDurationMs", static_cast<int>(default_realtime_vad_silence_ms(session->scene))));
        session->vad_silero_start_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroStartThresholdPermille", 550)) / 1000.0;
        session->vad_silero_continue_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroContinueThresholdPermille", 400)) / 1000.0;
        session->vad_silero_soft_start_threshold = static_cast<double>(json_get_int(options_json_utf8, "vadSileroSoftStartThresholdPermille", 300)) / 1000.0;
        session->threads = json_get_int(options_json_utf8, "threads", 4);
        session->use_gpu = json_get_bool(options_json_utf8, "useGpu", false);
        // Phase 14-1: Linux Moonshine loading
        const bool load_moonshine_linux = session->realtime_engine == "moonshine" ||
                                          session->realtime_engine == "joint_auto";
        if (load_moonshine_linux && !session->language.empty()) {
            std::string moonshine_dll_dir = json_get_string(options_json_utf8, "moonshineDllDir", "");
            std::string moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelPath", "");
            if (moonshine_model_path.empty())
                moonshine_model_path = json_get_string(options_json_utf8, "moonshineModelRoot", "");
            if (!moonshine_dll_dir.empty() && !moonshine_model_path.empty()) {
                session->moonshine_preset = get_moonshine_product_preset(session->language, session->scene);
                session->moonshine = std::make_unique<mobmate::MoonshineBridge>();
                if (!session->moonshine->init(moonshine_dll_dir)) {
                    fprintf(stderr, "[mmv2_realtime_create] WARN: Moonshine init failed\n");
                    session->moonshine.reset();
                } else {
                    int default_arch = json_get_int(options_json_utf8, "moonshineArch", mobmate::MoonshineBridge::ARCH_TINY);
                    auto model_choice = resolve_moonshine_product_model(moonshine_model_path, session->language, default_arch);
                    session->moonshine_stream_supported = model_choice.second >= mobmate::MoonshineBridge::ARCH_TINY_STREAMING;
                    mobmate::MoonshineBridge::LoadOptions load_opts;
                    load_opts.vad_window_duration        = session->moonshine_preset.vad_window_sec;
                    load_opts.vad_look_behind_sample_count = session->moonshine_preset.vad_look_behind_samples;
                    load_opts.vad_threshold              = session->moonshine_preset.vad_threshold;
                    load_opts.max_tokens_per_second      = session->moonshine_preset.max_tokens_per_second;
                    if (!session->moonshine->load_model(model_choice.first, model_choice.second, load_opts)) {
                        fprintf(stderr, "[mmv2_realtime_create] WARN: Moonshine model load failed: %s\n", model_choice.first.c_str());
                        session->moonshine.reset();
                    } else {
                        fprintf(stdout, "[mmv2_realtime_create] Moonshine loaded: %s (Linux)\n", model_choice.first.c_str());
                    }
                }
            }
        }
        // joint_auto: load Whisper alongside Moonshine; selection runs both candidates later.
        const bool need_whisper_linux = session->realtime_engine == "whisper" ||
            session->realtime_engine == "joint_auto";
        if (need_whisper_linux) {
            std::string whisper_dll_dir = json_get_string(options_json_utf8, "whisperDllDir", "");
            std::string whisper_cpu_dll_dir = json_get_string(options_json_utf8, "whisperCpuDllDir", "");
            std::string model_path = json_get_string(options_json_utf8, "modelPath", "");
            if (!whisper_dll_dir.empty() && !model_path.empty()) {
                std::string err_msg;
                session->engine = mobmate::WhisperEngine::load(whisper_dll_dir, model_path, err_msg, session->use_gpu);
                if (!session->engine) {
                    fprintf(stderr, "[mmv2_realtime_create] WARN: Whisper primary load failed: %s\n", err_msg.c_str());
                    if (session->use_gpu && !whisper_cpu_dll_dir.empty() && whisper_cpu_dll_dir != whisper_dll_dir) {
                        std::string cpu_err_msg;
                        session->engine = mobmate::WhisperEngine::load(whisper_cpu_dll_dir, model_path, cpu_err_msg, false);
                        if (!session->engine) {
                            session->engine_error = err_msg + " | cpu fallback: " + cpu_err_msg;
                            session->error = "whisper optional load failed: " + session->engine_error;
                            fprintf(stderr, "[mmv2_realtime_create] WARN: Whisper CPU fallback load failed: %s\n", cpu_err_msg.c_str());
                        } else {
                            session->use_gpu = false;
                            session->engine_error.clear();
                            session->error.clear();
                            fprintf(stdout, "[mmv2_realtime_create] Whisper CPU fallback loaded: %s (Linux)\n", model_path.c_str());
                        }
                    } else {
                        session->engine_error = err_msg;
                        session->error = "whisper optional load failed: " + err_msg;
                    }
                } else {
                    fprintf(stdout, "[mmv2_realtime_create] Whisper loaded: %s (Linux)\n", model_path.c_str());
                }
            }
        }
    }
#endif

    // Both platforms require the two-engine route, not a silent single-engine fallback.
    if (session->realtime_engine == "joint_auto" && (!session->moonshine || !session->engine)) {
        fprintf(stderr, "[mmv2_realtime_create] FAIL: JointAUTO requires Moonshine and Whisper\n");
        delete session;
        return -1;
    }

    *out_handle = static_cast<void*>(session);
    return 0;
}

MMV2_API int mmv2_realtime_start(void* handle) {
    if (!handle) return -1;
    auto* session = static_cast<RealtimeSession*>(handle);
    try {
    session->state = RealtimeState::kRecording;
#ifdef _WIN32
    realtime_trace(session, "realtime_start");
#endif
    session->partial.clear();
    session->final_text.clear();
    session->selected_engine = "none";
    session->decision_reason.clear();
    session->latency_ms = 0;
    session->error.clear();
    session->vad_state = VadState::kSilence;
    session->vad_elapsed_ms = 0.0;
    session->vad_last_speech_time_ms = 0.0;
    session->vad_session_start_ms = 0.0;
    session->total_samples = 0;
    session->chunk_samples = 0;
    session->partial_last_sample_count = 0;
    session->partial_speech_start_sample = -1;
    session->vad_end_sample = -1;
    session->vad_speech_count = 0;
    session->auto_transcribing = false;
    session->partial_transcribing = false;
    session->has_transcript = false;
    session->speculative_final_attempted = false;
    session->speculative_final_candidate = {};
    session->dump_seg_counter = 0;
    start_realtime_whisper_validation(session);
#ifdef _WIN32
    session->moonshine_streaming = false;
    session->moonshine_stream_sample_count = 0;
#endif
    session->noise_profile.reset();

    // Initialize SileroVAD only when package-relative paths are provided by the host.
    if (!session->vad_silero_model_path.empty() && !session->vad_silero_ort_dll_dir.empty()) {
        char errbuf[256] = {};
        session->vad_silero = silero_vad_init(session->vad_silero_model_path.c_str(),
                                              session->vad_silero_ort_dll_dir.c_str(),
                                              errbuf, sizeof(errbuf));
        if (!session->vad_silero) {
            session->error = "SileroVAD init failed: " + std::string(errbuf);
            fprintf(stderr, "[SileroVAD] init failed: model=%s ort=%s error=%s\n",
                    session->vad_silero_model_path.c_str(),
                    session->vad_silero_ort_dll_dir.c_str(),
                    errbuf);
        } else {
            fprintf(stderr, "[SileroVAD] enabled: model=%s ort=%s start=%.3f continue=%.3f soft=%.3f\n",
                    session->vad_silero_model_path.c_str(),
                    session->vad_silero_ort_dll_dir.c_str(),
                    session->vad_silero_start_threshold,
                    session->vad_silero_continue_threshold,
                    session->vad_silero_soft_start_threshold);
            fflush(stderr);
        }
    }

#ifdef _WIN32
    if (!session->capture_initialized) {
        std::string err;
        int ret = session->capture.initialize(session->capture_device_id, err);
        if (ret != 0) {
            session->state = RealtimeState::kFailed;
            session->error = "capture init: " + err;
            return -1;
        }
        session->capture_initialized = true;
    }
    std::string err;
    int ret = session->capture.start(err);
    if (ret != 0) {
        session->state = RealtimeState::kFailed;
        session->error = "capture start: " + err;
        return -1;
    }
    if (session->realtime_engine == "moonshine" && session->moonshine && session->moonshine_stream_supported) {
        session->moonshine_streaming = session->moonshine->start_stream(session->moonshine_preset.leading_padding_ms);
        realtime_trace(session, session->moonshine_streaming ? "moon_stream_start ok" : "moon_stream_start failed");
        if (!session->moonshine_streaming) {
            session->error = "moonshine stream start failed";
        }
    } else if (session->realtime_engine == "moonshine" && session->moonshine) {
        realtime_trace(session, "moon_stream_start skipped unsupported_model_arch");
    }
#else
    // Linux: initialize and start MiniAudio capture
    if (!session->capture_initialized) {
        std::string err;
        int ret = session->capture.initialize(session->capture_device_id, err);
        if (ret != 0) {
            session->state = RealtimeState::kFailed;
            session->error = "capture init: " + err;
            return -1;
        }
        session->capture_initialized = true;
    }
    {
        std::string err;
        int ret = session->capture.start(err);
        if (ret != 0) {
            session->state = RealtimeState::kFailed;
            session->error = "capture start: " + err;
            return -1;
        }
    }
#endif
    return 0;
    } catch (const std::exception& ex) {
        session->state = RealtimeState::kFailed;
        session->error = std::string("realtime start exception: ") + ex.what();
        session->capture.stop();
        return -2;
    } catch (...) {
        session->state = RealtimeState::kFailed;
        session->error = "realtime start unknown exception";
        session->capture.stop();
        return -3;
    }
}

MMV2_API int mmv2_realtime_stop(void* handle) {
    if (!handle) return -1;
    auto* session = static_cast<RealtimeSession*>(handle);
    try {
    session->capture.stop();
    shutdown_realtime_whisper_validation(session, false);
    if (session->vad_silero) {
        silero_vad_reset(session->vad_silero);
    }
    if (session->state == RealtimeState::kRecording) {
#ifdef _WIN32
        auto pcm_samples = session->capture.get_samples();
        // Resolve pending addon PCM even if its callback vanished: the result is an empty failure.
        bool engine_loaded = is_addon_engine(session->realtime_engine) ? true : session->realtime_engine == "moonshine" ? static_cast<bool>(session->moonshine) : static_cast<bool>(session->engine);
        if (engine_loaded && !pcm_samples.empty()) {
            /* Phase 6B: dump raw PCM for stop-time transcription */
            int seg_id = session->dump_seg_counter++;
            // Phase 6D-2: Final acceptance gate - input gate (before ASR)
            PcmStats stop_stats = compute_pcm_stats(pcm_samples);
            std::string stop_gate_reason;
            if (should_skip_realtime_final_input(stop_stats, stop_gate_reason)) {
                std::ostringstream oss;
                oss << "asr_final_gate_skip seg=" << seg_id
                    << " reason=" << stop_gate_reason
                    << " duration_ms=" << stop_stats.duration_ms
                    << " rms=" << stop_stats.rms
                    << " peak=" << stop_stats.peak
                    << " vad=" << vad_state_to_string(session->vad_state)
                    << " speech_count=" << session->vad_speech_count;
                realtime_trace(session, oss.str());
                session->state = RealtimeState::kCompleted;
            } else {
                // Phase 6F-1: Stop fallback silence guard
                bool silence_guard_skip = false;
                if (session->vad_state == VadState::kSilence &&
                    stop_stats.rms < 140.0 &&
                    (session->vad_speech_count == 0 ||
                     (stop_stats.duration_ms > 2500 && stop_stats.rms < 160.0))) {
                    silence_guard_skip = true;
                }
                if (!silence_guard_skip && should_skip_stop_fallback_with_noise_profile(session, stop_stats)) {
                    silence_guard_skip = true;
                }
                if (silence_guard_skip) {
                    std::ostringstream oss;
                    oss << "stop_fallback_skip seg=" << seg_id
                        << " reason=low_energy_silence"
                        << " duration_ms=" << stop_stats.duration_ms
                        << " rms=" << stop_stats.rms
                        << " peak=" << stop_stats.peak
                        << " vad=" << vad_state_to_string(session->vad_state)
                        << " speech_count=" << session->vad_speech_count;
                    realtime_trace(session, oss.str());
                    // Preserve previous final text; set decision_reason only if no transcript exists
                    if (!session->has_transcript) {
                        session->decision_reason = "no_speech";
                    }
                    session->state = RealtimeState::kCompleted;
                } else {
                    {
                        std::ostringstream oss;
                        oss << "stop_fallback_start seg=" << seg_id
                            << " samples=" << pcm_samples.size()
                            << " rms=" << session->capture.get_rms_level()
                            << " seg_rms=" << stop_stats.rms
                            << " seg_peak=" << stop_stats.peak
                            << " seg_duration_ms=" << stop_stats.duration_ms
                            << " vad=" << vad_state_to_string(session->vad_state)
                            << " speech_count=" << session->vad_speech_count
                            << " reason=stop_fallback";
                        realtime_trace(session, oss.str());
                    }
                    const double stop_gain = compute_safe_input_auto_gain(session, stop_stats);
                    if (stop_gain > 1.0) {
                        apply_gain_in_place(pcm_samples, stop_gain);
                        std::ostringstream oss;
                        oss << "auto_gain_stop gain=" << stop_gain
                            << " seg_rms=" << stop_stats.rms
                            << " seg_peak=" << stop_stats.peak
                            << " floor=" << session->noise_profile.noise_floor_rms
                            << " std=" << session->noise_profile.noise_rms_stddev;
                        realtime_trace(session, oss.str());
                    }
                    transcribe_realtime_samples(session, pcm_samples, false, seg_id,
                                                      session->capture.get_rms_level(), &stop_stats);
                }
            }
        } else {
            session->state = RealtimeState::kCompleted;
        }
#else
        session->state = RealtimeState::kCompleted;
#endif
    }
#ifdef _WIN32
    if (session->realtime_engine == "moonshine" && session->moonshine) {
        session->moonshine->stop_stream();
        session->moonshine_streaming = false;
    }
#endif
    return 0;
    } catch (const std::exception& ex) {
        session->state = RealtimeState::kFailed;
        session->error = std::string("realtime stop exception: ") + ex.what();
        return -2;
    } catch (...) {
        session->state = RealtimeState::kFailed;
        session->error = "realtime stop unknown exception";
        return -3;
    }
}

MMV2_API int mmv2_realtime_poll(void* handle, const char** out_json) {
    if (!handle || !out_json) return -1;
    *out_json = nullptr;
    auto* session = static_cast<RealtimeSession*>(handle);
    std::string json = make_poll_json(session);
    // Debug: print JSON to stderr
#ifdef _WIN32
    OutputDebugStringA(json.c_str());
    OutputDebugStringA("\n");
#endif
    *out_json = dup_string(json);
    return 0;
}

MMV2_API int mmv2_realtime_set_vad_start_callback(void* handle,
    mmv2_vad_start_callback callback, void* user_data) {
    if (!handle) return -1;
    auto* session = static_cast<RealtimeSession*>(handle);
    std::lock_guard<std::mutex> lock(session->vad_callback_mutex);
    session->vad_start_callback = callback;
    session->vad_start_user_data = callback ? user_data : nullptr;
    return 0;
}

MMV2_API void mmv2_realtime_destroy(void* handle) {
    if (handle) {
        auto* session = static_cast<RealtimeSession*>(handle);
        mmv2_realtime_set_vad_start_callback(handle, nullptr, nullptr);
#ifdef _WIN32
        session->capture.release();
#endif
        delete session;
    }
}

static void reset_realtime_bench_session(RealtimeSession* session,
                                         const mobmate::ManifestRow& row) {
    if (!session) return;
    session->state = RealtimeState::kRecording;
    session->partial.clear();
    session->final_text.clear();
    session->latency_ms = 0;
    session->error.clear();
    session->vad_state = VadState::kEndDetected;
    session->vad_elapsed_ms = 0.0;
    session->vad_last_speech_time_ms = 0.0;
    session->total_samples = 0;
    session->chunk_samples = 0;
    session->partial_last_sample_count = 0;
    session->partial_speech_start_sample = -1;
    session->vad_end_sample = -1;
    session->vad_soft_start_frames = 0;
    session->vad_soft_start_sample = -1;
    session->vad_speech_count = 0;
    session->asr_pre_roll.clear();
    session->auto_transcribing = false;
    session->partial_transcribing = false;
    session->has_transcript = false;
    session->speculative_final_attempted = false;
    session->speculative_final_candidate = {};
#ifdef _WIN32
    session->selected_engine = "none";
    session->decision_reason.clear();
    if (!row.language.empty()) session->language = row.language;
    if (!row.scene.empty()) session->scene = row.scene;
    session->moonshine_latest_candidate = {};
    session->moonshine_peak_candidate = {};
    session->moonshine_stream_sample_count = 0;
#endif
}

static std::string set_json_string_field(std::string json,
                                         const std::string& field,
                                         const std::string& value) {
    std::string key = "\"" + field + "\"";
    size_t key_pos = json.find(key);
    if (key_pos == std::string::npos) {
        size_t end = json.rfind('}');
        if (end == std::string::npos) return json;
        std::string insert = (end > 0 && json[end - 1] != '{') ? "," : "";
        insert += key + ":\"" + json_escape(value) + "\"";
        json.insert(end, insert);
        return json;
    }
    size_t colon = json.find(':', key_pos + key.size());
    if (colon == std::string::npos) return json;
    size_t first_quote = json.find('"', colon + 1);
    if (first_quote == std::string::npos) return json;
    size_t second_quote = json.find('"', first_quote + 1);
    if (second_quote == std::string::npos) return json;
    json.replace(first_quote + 1, second_quote - first_quote - 1, json_escape(value));
    return json;
}

// ===== SenseVoice offline bridge (sherpa-onnx C API) =====

static std::mutex g_sv_mutex;

struct SvRecognizerEntry {
    const SherpaOnnxOfflineRecognizer* recognizer = nullptr;
    ~SvRecognizerEntry() {
        if (recognizer) { SherpaOnnxDestroyOfflineRecognizer(recognizer); recognizer = nullptr; }
    }
};
static std::map<std::string, std::unique_ptr<SvRecognizerEntry>> g_sv_cache;
static std::string g_sv_model_path;
static std::string g_sv_tokens_path;
static bool g_sv_init_done = false;
static bool g_sv_available = false;

static std::mutex g_pf_mutex;
struct PfRecognizerEntry {
    const SherpaOnnxOfflineRecognizer* recognizer = nullptr;
    ~PfRecognizerEntry() {
        if (recognizer) { SherpaOnnxDestroyOfflineRecognizer(recognizer); recognizer = nullptr; }
    }
};
static std::map<std::string, std::unique_ptr<PfRecognizerEntry>> g_pf_cache;

static std::string sv_get_dll_dir() {
#ifdef _WIN32
    HMODULE hm = nullptr;
    GetModuleHandleExA(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCSTR>(&sv_get_dll_dir), &hm);
    char path[MAX_PATH] = {};
    GetModuleFileNameA(hm, path, MAX_PATH);
    return std::filesystem::path(path).parent_path().string();
#else
    return "";
#endif
}

static std::string pf_default_model_dir() {
    const std::string dll_dir = sv_get_dll_dir();
    if (dll_dir.empty()) return "";
    const std::string app_root = std::filesystem::path(dll_dir).parent_path().string();
    return app_root + "\\models\\paraformer_zh";
}

// DLL ディレクトリ相対で SenseVoice モデルを探す。ロック外から呼ぶこと。
static void sv_maybe_init() {
    std::lock_guard<std::mutex> lk(g_sv_mutex);
    if (g_sv_init_done) return;
    g_sv_init_done = true;
    const std::string dll_dir = sv_get_dll_dir();
    if (dll_dir.empty()) return;
    // DLL は native/ サブディレクトリにある → 親(アプリルート)を使う
    const std::string app_root = std::filesystem::path(dll_dir).parent_path().string();
    std::string model_dir  = app_root + "\\models\\sense_voice";
    std::string model_path = model_dir + "\\model.int8.onnx";
    std::string tokens_path= model_dir + "\\tokens.txt";
    if (!std::filesystem::exists(model_path) || !std::filesystem::exists(tokens_path)) return;
    g_sv_model_path = model_path;
    g_sv_tokens_path= tokens_path;
    g_sv_available  = true;
}

// g_sv_mutex を保持した状態で呼ぶ
static const SherpaOnnxOfflineRecognizer* sv_get_or_create(const std::string& lang) {
    auto it = g_sv_cache.find(lang);
    if (it != g_sv_cache.end()) return it->second->recognizer;

    SherpaOnnxOfflineRecognizerConfig config;
    memset(&config, 0, sizeof(config));
    config.model_config.sense_voice.model    = g_sv_model_path.c_str();
    config.model_config.sense_voice.language = lang.c_str();
    config.model_config.sense_voice.use_itn  = 1;
    config.model_config.tokens      = g_sv_tokens_path.c_str();
    config.model_config.num_threads = 1;
    config.model_config.debug       = 0;
    config.model_config.provider    = "cpu";
    config.decoding_method          = "greedy_search";

    auto entry = std::make_unique<SvRecognizerEntry>();
    entry->recognizer = SherpaOnnxCreateOfflineRecognizer(&config);
    const SherpaOnnxOfflineRecognizer* r = entry->recognizer;
    g_sv_cache[lang] = std::move(entry);
    return r;
}

// 文中空白除去:
//   ZH/JA → スペースをすべて除去 (CJK は空白不要)
//   KO    → 連続スペースを1つに正規化 (韓国語は単語間スペースが意味を持つ)
static std::string sv_clean_text(const std::string& raw, const std::string& lang) {
    std::string out;
    out.reserve(raw.size());
    bool prev_sp = false;
    for (char c : raw) {
        bool is_sp = (c == ' ' || c == '\t');
        if (is_sp) {
            if (lang == "ko") prev_sp = true;
            // zh/ja: スペースを無視
        } else {
            if (prev_sp && lang == "ko") out += ' ';
            prev_sp = false;
            out += c;
        }
    }
    // leading/trailing trim
    auto s = out.find_first_not_of(" \t\n\r");
    if (s == std::string::npos) return "";
    auto e = out.find_last_not_of(" \t\n\r");
    return out.substr(s, e - s + 1);
}

static std::string sv_compare_key(const std::string& raw) {
    std::string out;
    out.reserve(raw.size());
    for (size_t i = 0; i < raw.size();) {
        const unsigned char c = static_cast<unsigned char>(raw[i]);
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r' ||
            c == '.' || c == ',' || c == '?' || c == '!') { ++i; continue; }
        if (raw.compare(i, 3, "。") == 0 || raw.compare(i, 3, "、") == 0 ||
            raw.compare(i, 3, "，") == 0 || raw.compare(i, 3, "？") == 0 ||
            raw.compare(i, 3, "！") == 0) { i += 3; continue; }
        out += raw[i++];
    }
    return out;
}

static size_t utf8_tail_offset(const std::string& s, int chars) {
    size_t pos = s.size();
    for (int n = 0; n < chars && pos > 0; ++n) {
        --pos;
        while (pos > 0 && (static_cast<unsigned char>(s[pos]) & 0xC0) == 0x80) --pos;
    }
    return pos;
}

static bool zh_tail_agree_prefer_pf(const std::string& selected_key,
                                    const std::string& moonshine_key,
                                    const std::string& pf_key) {
    if (selected_key.empty() || moonshine_key.empty() || pf_key.empty()) return false;
    const size_t pf_tail = utf8_tail_offset(pf_key, 3);
    if (pf_tail == 0 && pf_key.size() < 9) return false;
    const std::string tail = pf_key.substr(pf_tail);
    return moonshine_key.size() >= tail.size() &&
        moonshine_key.compare(moonshine_key.size() - tail.size(), tail.size(), tail) == 0 &&
        (selected_key.size() < tail.size() ||
         selected_key.compare(selected_key.size() - tail.size(), tail.size(), tail) != 0);
}

// int16_t 16kHz PCM → SenseVoice 推論 → クリーン済みテキスト
// chars_per_sec: 推論結果の文字/秒 (confidence proxy, 呼び出し元がフィルター判断に使用)
static std::string sv_transcribe_pcm16(const std::vector<int16_t>& pcm16,
                                        const std::string& language,
                                        float* chars_per_sec = nullptr) {
    if (chars_per_sec) *chars_per_sec = 0.0f;
    if (!g_sv_available || pcm16.empty()) return "";

    std::vector<float> samples(pcm16.size());
    for (size_t i = 0; i < pcm16.size(); i++)
        samples[i] = static_cast<float>(pcm16[i]) / 32768.0f;

    std::lock_guard<std::mutex> lk(g_sv_mutex);
    const SherpaOnnxOfflineRecognizer* recognizer = sv_get_or_create(language);
    if (!recognizer) return "";

    const SherpaOnnxOfflineStream* stream = SherpaOnnxCreateOfflineStream(recognizer);
    if (!stream) return "";

    SherpaOnnxAcceptWaveformOffline(stream, 16000, samples.data(), static_cast<int32_t>(samples.size()));
    SherpaOnnxDecodeOfflineStream(recognizer, stream);

    std::string text;
    const SherpaOnnxOfflineRecognizerResult* r = SherpaOnnxGetOfflineStreamResult(stream);
    if (r && r->text) {
        text = r->text;
        SherpaOnnxDestroyOfflineRecognizerResult(r);
    }
    SherpaOnnxDestroyOfflineStream(stream);

    const std::string cleaned = sv_clean_text(text, language);

    // chars/sec (confidence proxy): 異常に高ければ hallucination の可能性
    if (chars_per_sec && !cleaned.empty()) {
        const float dur_sec = static_cast<float>(pcm16.size()) / 16000.0f;
        // UTF-8 CJK は3バイト/文字。バイト長/3 で文字数を近似
        const float approx_chars = static_cast<float>(cleaned.size()) / 3.0f;
        *chars_per_sec = (dur_sec > 0.0f) ? approx_chars / dur_sec : 0.0f;
    }
    return cleaned;
}

static std::string pf_transcribe_pcm16(const std::vector<int16_t>& pcm16,
                                       const std::string& model_dir) {
    const std::string effective_model_dir = model_dir.empty() ? pf_default_model_dir() : model_dir;
    if (pcm16.empty() || effective_model_dir.empty()) return "";
    const std::string model_path = effective_model_dir + "\\model.int8.onnx";
    const std::string tokens_path = effective_model_dir + "\\tokens.txt";
    if (!std::filesystem::exists(model_path) || !std::filesystem::exists(tokens_path)) return "";

    std::vector<float> samples(pcm16.size());
    for (size_t i = 0; i < pcm16.size(); i++)
        samples[i] = static_cast<float>(pcm16[i]) / 32768.0f;

    std::lock_guard<std::mutex> lk(g_pf_mutex);
    auto it = g_pf_cache.find(effective_model_dir);
    const SherpaOnnxOfflineRecognizer* recognizer = it == g_pf_cache.end() ? nullptr : it->second->recognizer;
    if (!recognizer) {
        SherpaOnnxOfflineRecognizerConfig config;
        memset(&config, 0, sizeof(config));
        config.model_config.paraformer.model = model_path.c_str();
        config.model_config.tokens = tokens_path.c_str();
        config.model_config.num_threads = 1;
        config.model_config.debug = 0;
        config.model_config.provider = "cpu";
        config.decoding_method = "greedy_search";

        auto entry = std::make_unique<PfRecognizerEntry>();
        entry->recognizer = SherpaOnnxCreateOfflineRecognizer(&config);
        recognizer = entry->recognizer;
        g_pf_cache[effective_model_dir] = std::move(entry);
    }
    if (!recognizer) return "";

    const SherpaOnnxOfflineStream* stream = SherpaOnnxCreateOfflineStream(recognizer);
    if (!stream) return "";
    SherpaOnnxAcceptWaveformOffline(stream, 16000, samples.data(), static_cast<int32_t>(samples.size()));
    SherpaOnnxDecodeOfflineStream(recognizer, stream);

    std::string text;
    const SherpaOnnxOfflineRecognizerResult* r = SherpaOnnxGetOfflineStreamResult(stream);
    if (r && r->text) {
        text = r->text;
        SherpaOnnxDestroyOfflineRecognizerResult(r);
    }
    SherpaOnnxDestroyOfflineStream(stream);
    return sv_clean_text(text, "zh");
}

static const std::set<std::string> kSvLanguages = {"ja", "zh", "ko"};

// ===== end SenseVoice bridge =====

static int realtime_bench_manifest_impl(const char* manifest_path_utf8,
                                          const char* out_jsonl_path_utf8,
                                          const char* options_json_utf8,
                                          const char** out_json, mmv2_replay_finalizer finalizer) {
    if (out_json) *out_json = nullptr;
    if (!manifest_path_utf8 || !out_jsonl_path_utf8 || !out_json) return -1;
    std::string err_msg;
    auto rows = mobmate::read_manifest(manifest_path_utf8, err_msg);
    if (!rows) {
        *out_json = dup_string(make_error_json("manifest read failed: " + err_msg, "manifest"));
        return -1;
    }

    std::vector<mobmate::BenchResult> results;
    results.reserve(rows->size());

    const std::string base_options = options_json_utf8 ? options_json_utf8 : "{}";
    const std::string requested_engine = json_get_string(base_options.c_str(), "engine", "whisper");
#ifndef _WIN32
    for (const auto& row : *rows) {
        mobmate::BenchResult result;
        result.index = row.index;
        result.wav_path = row.wav_path;
        result.expected = row.expected;
        result.transcript.clear();
        result.latency_ms = -1;

        std::string wav_err;
        auto wav_meta = mobmate::read_wav_metadata(row.wav_path, wav_err);
        auto samples = mobmate::read_wav_samples(row.wav_path, wav_err);
        if (!wav_meta) {
            result.error = "WAV metadata failed: " + wav_err;
        } else if (!samples) {
            result.error = "WAV read failed: " + wav_err;
        } else {
            std::string row_options = set_json_string_field(base_options, "language", row.language);
            row_options = set_json_string_field(row_options, "scene", row.scene);
            const std::string effective_engine = requested_engine == "joint_auto" ? "moonshine" : requested_engine;
            row_options = set_json_string_field(row_options, "engine", effective_engine);

            const char* tx_json = nullptr;
            int ret = transcribe_pcm16_core(*samples, static_cast<int>(wav_meta->sample_rate), row_options.c_str(), &tx_json);
            std::string raw = tx_json ? tx_json : "{}";
            if (tx_json) std::free((void*)tx_json);

            if (ret != 0) {
                result.error = json_get_string(raw.c_str(), "message", raw.c_str());
                if (result.error.empty()) result.error = json_get_string(raw.c_str(), "error", "transcription failed");
            } else {
                result.transcript = json_get_string(raw.c_str(), "text", "");
                result.latency_ms = json_get_int(raw.c_str(), "latencyMs", -1);
                if (result.transcript.empty()) {
                    result.transcript = json_get_string(raw.c_str(), "transcript", "");
                }
            }
            result.extra_json_fields = "\"route\":\"realtime_dll_linux_oneshot\",\"engine\":\"" + json_escape(requested_engine) +
                "\",\"selected_engine\":\"" + json_escape(effective_engine) +
                "\",\"decision_reason\":\"linux_manifest_oneshot\",\"language\":\"" + json_escape(row.language) + "\"";
        }

        result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, false);
        result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, false);
        results.push_back(result);
    }

    std::ofstream ofs(out_jsonl_path_utf8);
    if (!ofs) {
        *out_json = dup_string(make_error_json("cannot open output file", "write"));
        return -1;
    }
    for (const auto& result : results) {
        ofs << mobmate::bench_result_to_jsonl(result) << "\n";
    }
    ofs.close();

    auto summary = mobmate::compute_summary(results);
    std::ostringstream oss;
    oss << "{\"ok\":true"
        << ",\"route\":\"realtime_dll_linux_oneshot\""
        << ",\"total\":" << summary.total
        << ",\"exactCount\":" << summary.exact_count
        << ",\"avgCer\":" << summary.avg_cer
        << ",\"avgLatencyMs\":" << summary.avg_latency_ms
        << ",\"errorCount\":" << summary.error_count
        << ",\"resultPath\":\"" << json_escape(out_jsonl_path_utf8) << "\"}";
    *out_json = dup_string(oss.str());
    return 0;
#else
    const bool bench_joint_auto = requested_engine == "joint_auto";
    const bool bench_moonshine = requested_engine == "moonshine";
    const bool production_replay = json_get_bool(base_options.c_str(), "productionReplay", false);
    const std::string bench_moonshine_root = json_get_string(base_options.c_str(), "moonshineModelRoot", "");
    const bool experimental_candidates_enabled = json_get_bool(base_options.c_str(), "experimentalAsrCandidatesEnabled", false);
    const std::string experimental_candidate_engine = json_get_string(base_options.c_str(), "experimentalAsrCandidateEngine", "");
    const std::string experimental_candidate_language = json_get_string(base_options.c_str(), "experimentalAsrCandidateLanguage", "");
    const std::string experimental_candidate_model_dir = json_get_string(base_options.c_str(), "experimentalAsrCandidateModelDir", "");
    void* current_handle = nullptr;
    std::string current_key;
    auto destroy_current_session = [&]() {
        if (current_handle) {
            mmv2_realtime_destroy(current_handle);
            current_handle = nullptr;
        }
        current_key.clear();
    };

    struct JointBenchCandidate {
        std::string engine;
        std::string text;
        int64_t latency_ms = -1;
        bool success = false;
        std::string selected_engine;
        std::string decision_reason;
        std::string error;
        std::string source = "one_shot";
        std::string resolved_scene;
    };
    auto candidate_to_json = [](const JointBenchCandidate& c,
                                 const PcmStats& stats,
                                 const mobmate::ManifestRow& row) {
        std::ostringstream oss;
        oss << "{\"engine\":\"" << json_escape(c.engine)
            << "\",\"text\":\"" << json_escape(c.text)
            << "\",\"normalizedText\":\"" << json_escape(c.text)
            << "\",\"latencyMs\":" << c.latency_ms
            << ",\"success\":" << (c.success ? "true" : "false")
            << ",\"rejectReasons\":[";
        if (!c.error.empty()) {
            oss << "\"" << json_escape(c.error) << "\"";
        } else if (c.text.empty()) {
            oss << "\"empty_text\"";
        }
        oss << "]"
            << ",\"source\":\"" << json_escape(c.source) << "\""
            << ",\"segmentStats\":{\"durationMs\":" << stats.duration_ms
            << ",\"activeDurationMs\":" << stats.active_duration_ms
            << ",\"rms\":" << stats.rms
            << ",\"peak\":" << stats.peak
            << ",\"sampleCount\":" << stats.sample_count << "}"
            << ",\"contextStats\":{\"requestedScene\":\"" << json_escape(row.scene)
            << "\",\"resolvedScene\":\"" << json_escape(c.resolved_scene.empty() ? row.scene : c.resolved_scene)
            << "\",\"language\":\"" << json_escape(row.language)
            << "\",\"noiseFloor\":null,\"sileroP90\":null}"
            << ",\"scoreBreakdown\":{\"repetitionScore\":0,\"lengthPlausibilityScore\":0,"
            << "\"genericHallucinationPenalty\":0,\"agreementScore\":0,\"audioFitScore\":0,\"enginePriorScore\":0}"
            << ",\"selectedEngine\":\"" << json_escape(c.selected_engine)
            << "\",\"decisionReason\":\"" << json_escape(c.decision_reason) << "\"";
        if (!c.error.empty()) {
            oss << ",\"error\":\"" << json_escape(c.error) << "\"";
        }
        oss << "}";
        return oss.str();
    };
    auto append_experimental_asr_candidate = [&](std::vector<JointBenchCandidate>& candidates,
                                                 const mobmate::ManifestRow& row,
                                                 const std::vector<int16_t>& pcm16) {
        if (!experimental_candidates_enabled || experimental_candidate_engine.empty()) return;
        if (!experimental_candidate_language.empty() && experimental_candidate_language != row.language) return;

        JointBenchCandidate candidate;
        candidate.engine = experimental_candidate_engine;
        candidate.selected_engine = "none";
        candidate.source = "experimental_hook";
        candidate.resolved_scene = row.scene;
        if (experimental_candidate_engine == "paraformer_zh") {
            const auto start = std::chrono::steady_clock::now();
            candidate.text = pf_transcribe_pcm16(pcm16, experimental_candidate_model_dir);
            const auto end = std::chrono::steady_clock::now();
            candidate.latency_ms = std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count();
            candidate.success = !candidate.text.empty();
            candidate.decision_reason = "experimental_candidate_inference_only";
            candidate.error = candidate.success ? "" : "paraformer_empty_or_unavailable";
        } else {
            candidate.latency_ms = -1;
            candidate.success = false;
            candidate.decision_reason = "experimental_candidate_hook_only";
            candidate.error = "engine_not_registered";
        }
        candidates.push_back(candidate);
    };

    for (const auto& row : *rows) {
        const auto row_wall_start = std::chrono::steady_clock::now();
        mobmate::BenchResult result;
        result.index = row.index;
        result.wav_path = row.wav_path;
        result.expected = row.expected;
        result.transcript.clear();
        result.latency_ms = -1;

        std::string wav_err;
        auto wav_meta = mobmate::read_wav_metadata(row.wav_path, wav_err);
        auto samples = mobmate::read_wav_samples(row.wav_path, wav_err);
        if (!wav_meta) {
            result.error = "WAV metadata failed: " + wav_err;
        } else if (!samples) {
            result.error = "WAV read failed: " + wav_err;
        } else {
            const std::vector<int16_t>* input_samples = &*samples;
            std::optional<std::vector<int16_t>> resampled_samples;
            if (wav_meta->sample_rate != 16000) {
                auto pcm_bytes = pcm16_to_bytes(*samples);
                auto resampled = mobmate::resample_audio(
                    pcm_bytes.data(), static_cast<int>(pcm_bytes.size()),
                    static_cast<int>(wav_meta->sample_rate), 16000);
                if (!resampled.empty()) {
                    resampled_samples = bytes_to_pcm16(resampled);
                    input_samples = &*resampled_samples;
                } else {
                    result.error = "WAV resample failed";
                }
            }
            const std::string cache_key = row.language + "|" + row.scene;
            if (!current_handle || (!bench_moonshine && current_key != cache_key)) {
                destroy_current_session();
                std::string row_options = set_json_string_field(base_options, "language", row.language);
                row_options = set_json_string_field(row_options, "scene", row.scene);
                int create_ret = mmv2_realtime_create(row_options.c_str(), &current_handle);
                if (create_ret != 0 || !current_handle) {
                    result.error = "mmv2_realtime_create failed";
                } else {
                    current_key = cache_key;
                }
            }
            if (!current_handle) {
                result.extra_json_fields = "\"route\":\"realtime_dll\",\"engine\":\"unknown\",\"selected_engine\":\"none\",\"decision_reason\":\"create_failed\",\"language\":\"" + json_escape(row.language) + "\"";
            } else {
            auto* session = static_cast<RealtimeSession*>(current_handle);
            if (bench_moonshine && current_key != cache_key) {
                session->language = row.language;
                session->scene = row.scene;
                session->moonshine_preset = get_moonshine_product_preset(row.language, row.scene);
                if (!session->moonshine || bench_moonshine_root.empty()) {
                    result.error = "moonshine realtime config missing";
                } else {
                    auto model_choice = resolve_moonshine_product_model(bench_moonshine_root, row.language, mobmate::MoonshineBridge::ARCH_TINY);
                    session->moonshine_stream_supported = model_choice.second >= mobmate::MoonshineBridge::ARCH_TINY_STREAMING;
                    session->moonshine_streaming = false;
                    mobmate::MoonshineBridge::LoadOptions load_options;
                    load_options.vad_window_duration = session->moonshine_preset.vad_window_sec;
                    load_options.vad_look_behind_sample_count = session->moonshine_preset.vad_look_behind_samples;
                    load_options.vad_threshold = session->moonshine_preset.vad_threshold;
                    load_options.max_tokens_per_second = session->moonshine_preset.max_tokens_per_second;
                    if (!session->moonshine->load_model(model_choice.first, model_choice.second, load_options)) {
                        result.error = "Moonshine model load failed: " + model_choice.first;
                    } else {
                        current_key = cache_key;
                    }
                }
            }
            if (!result.error.empty()) {
                result.extra_json_fields = "\"route\":\"realtime_dll\",\"engine\":\"moonshine\",\"selected_engine\":\"none\",\"decision_reason\":\"load_failed\",\"language\":\"" + json_escape(row.language) + "\"";
            } else if (bench_joint_auto && !production_replay) {
            PcmStats stats = compute_pcm_stats(*input_samples);
            std::vector<JointBenchCandidate> candidates;
            {
                std::ostringstream oss;
                oss << "bench_row_start index=" << row.index
                    << " language=" << row.language
                    << " scene=" << row.scene
                    << " samples=" << input_samples->size();
                realtime_trace(session, oss.str());
            }
            for (const auto& candidate_engine : {std::string("moonshine"), std::string("whisper")}) {
                reset_realtime_bench_session(session, row);
                session->realtime_engine = candidate_engine;
                int ret = transcribe_realtime_samples(session, *input_samples, false, row.index, stats.rms, &stats);
                JointBenchCandidate candidate;
                candidate.engine = candidate_engine;
                candidate.text = session->final_text;
                candidate.latency_ms = session->latency_ms;
                candidate.selected_engine = session->selected_engine;
                candidate.decision_reason = session->decision_reason;
                candidate.resolved_scene = session->resolved_scene;
                candidate.error = (ret != 0 || !session->error.empty())
                    ? (session->error.empty() ? "realtime transcription failed" : session->error)
                    : "";
                candidate.success = candidate.error.empty() && !candidate.text.empty();
                candidates.push_back(candidate);
            }
            append_experimental_asr_candidate(candidates, row, *input_samples);
            bool has_paraformer_zh_candidate = false;
            for (const auto& candidate : candidates) {
                if (candidate.engine == "paraformer_zh") has_paraformer_zh_candidate = true;
            }
            if (row.language == "zh" && !has_paraformer_zh_candidate) {
                JointBenchCandidate candidate;
                candidate.engine = "paraformer_zh";
                candidate.selected_engine = "none";
                candidate.source = "joint_zh_candidate";
                candidate.resolved_scene = row.scene;
                realtime_trace(session, "bench_paraformer_zh_start index=" + std::to_string(row.index));
                const auto start = std::chrono::steady_clock::now();
                candidate.text = pf_transcribe_pcm16(*input_samples, experimental_candidate_model_dir);
                const auto end = std::chrono::steady_clock::now();
                realtime_trace(session, "bench_paraformer_zh_end index=" + std::to_string(row.index));
                candidate.latency_ms = std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count();
                candidate.success = !candidate.text.empty();
                candidate.decision_reason = candidate.success ? "joint_zh_candidate" : "joint_zh_paraformer_unavailable";
                candidate.error = candidate.success ? "" : "paraformer_empty_or_unavailable";
                candidates.push_back(candidate);
            }
            const JointBenchCandidate* moonshine_candidate = nullptr;
            const JointBenchCandidate* whisper_candidate = nullptr;
            const JointBenchCandidate* paraformer_zh_candidate = nullptr;
            for (const auto& candidate : candidates) {
                if (candidate.engine == "moonshine") moonshine_candidate = &candidate;
                if (candidate.engine == "whisper") whisper_candidate = &candidate;
                if (candidate.engine == "paraformer_zh") paraformer_zh_candidate = &candidate;
            }
            const JointBenchCandidate* selected = nullptr;
            std::string joint_decision_reason = "no_valid_transcript";
            const std::string resolved_scene = moonshine_candidate && !moonshine_candidate->resolved_scene.empty()
                ? moonshine_candidate->resolved_scene
                : row.scene;
            const bool hard_whisper_scene = resolved_scene == "talk_low_voice" ||
                resolved_scene == "talk_noisy" || resolved_scene == "desktop_hearing";
            const bool moonshine_allowed_scene = resolved_scene == "short_command" ||
                resolved_scene == "talk_clean";
            const int len_delta = moonshine_candidate && whisper_candidate
                ? static_cast<int>(selector_text_len(moonshine_candidate->text)) -
                  static_cast<int>(selector_text_len(whisper_candidate->text))
                : 0;
            /* Bench selector aligned with live staged-fastpath conditions:
             * Moonshine fastpath only when signal is clearly strong (rms>=750,
             * peak>=2500, SNR>=2.5). Otherwise prefer Whisper for accuracy. */
            const float bench_snr = (session->noise_profile.ready &&
                session->noise_profile.noise_floor_rms > 1.0)
                ? (float)stats.rms / (float)session->noise_profile.noise_floor_rms
                : 99.0f;
            const bool moonshine_fast_path = moonshine_candidate && moonshine_candidate->success &&
                !hard_whisper_scene && moonshine_allowed_scene &&
                moonshine_candidate->latency_ms <= 180 &&
                stats.active_duration_ms <= 4000.0 &&
                len_delta >= -2 &&
                stats.rms >= 600.0 &&
                stats.peak >= 2500 &&
                bench_snr >= 2.5f;
            if (moonshine_fast_path) {
                selected = moonshine_candidate;
                joint_decision_reason = "joint_v1_fastpath_moonshine";
            } else if (whisper_candidate && whisper_candidate->success) {
                selected = whisper_candidate;
                joint_decision_reason = "joint_v1_fallback_whisper";
            } else if (moonshine_candidate && moonshine_candidate->success) {
                selected = moonshine_candidate;
                joint_decision_reason = "joint_v1_last_resort_moonshine";
            }
            if (paraformer_zh_candidate && paraformer_zh_candidate->success) {
                const bool selected_empty = !selected || selected->text.empty();
                const std::string selected_key = selected ? sv_compare_key(selected->text) : "";
                const std::string pf_key = sv_compare_key(paraformer_zh_candidate->text);
                const std::string moonshine_key = moonshine_candidate ? sv_compare_key(moonshine_candidate->text) : "";
                const bool pf_tail_agree = selected && selected->engine == "whisper" &&
                    zh_tail_agree_prefer_pf(selected_key, moonshine_key, pf_key);
                if (selected_empty || (!selected_key.empty() && selected_key == pf_key) || pf_tail_agree) {
                    selected = paraformer_zh_candidate;
                    joint_decision_reason = selected_empty ? "joint_zh_paraformer_selected" :
                        (pf_tail_agree ? "joint_zh_paraformer_tail_agree" : "joint_zh_paraformer_agree");
                }
            }
            if (selected) {
                result.transcript = selected->text;
                result.latency_ms = selected->latency_ms;
            } else {
                result.transcript.clear();
                result.latency_ms = candidates.empty() ? -1 : candidates.front().latency_ms;
            }
            std::ostringstream extra;
            extra << "\"route\":\"realtime_dll\",\"mode\":\"full_candidate_diagnostic\",\"engine\":\"joint_auto\""
                  << ",\"selected_engine\":\"" << json_escape(selected ? selected->engine : "none") << "\""
                  << ",\"decision_reason\":\"" << json_escape(joint_decision_reason) << "\""
                  << ",\"language\":\"" << json_escape(row.language) << "\""
                  << ",\"jointMode\":\"joint_router\""
                  << ",\"selectorVersion\":\"joint-v1-fastpath-conservative\""
                  << ",\"candidates\":[";
            for (size_t i = 0; i < candidates.size(); ++i) {
                if (i > 0) extra << ",";
                extra << candidate_to_json(candidates[i], stats, row);
            }
            extra << "]";
            result.extra_json_fields = extra.str();

            // SenseVoice overlay: JA/ZH/KO では推論し、フィルターを通過したら優先
            if (kSvLanguages.count(row.language)) {
                sv_maybe_init();
                float cps = 0.0f;
                realtime_trace(session, "bench_sensevoice_start index=" + std::to_string(row.index));
                const std::string sv_text = sv_transcribe_pcm16(*input_samples, row.language, &cps);
                realtime_trace(session, "bench_sensevoice_end index=" + std::to_string(row.index));

                // --- フィルター ---
                // 1) chars/sec (confidence proxy): 閾値を超えたら hallucination 疑い
                //    JA/ZH/KO の自然発話は概ね ≤ 10 chars/sec (3byte/char 換算)
                const bool cps_ok = sv_text.empty() || cps <= 10.0f;

                // 2) native との文字列長比率チェック
                //    native が非空の場合のみ。sv が 40%未満か 250%超なら棄却
                const size_t nat_len = result.transcript.size();
                const size_t sv_len  = sv_text.size();
                const bool len_ok = sv_text.empty() || nat_len == 0
                    || (sv_len >= nat_len * 40 / 100 && sv_len <= nat_len * 250 / 100);

                const std::string sv_key = sv_compare_key(sv_text);
                bool candidate_agree = false;
                bool selected_agree = false;
                bool moonshine_agree = false;
                for (const auto& c : candidates) {
                    const std::string ck = sv_compare_key(c.text);
                    if (!ck.empty() && ck == sv_key) {
                        candidate_agree = true;
                        if (selected && c.engine == selected->engine) selected_agree = true;
                        if (c.engine == "moonshine") moonshine_agree = true;
                    }
                }
                const std::string native_key = sv_compare_key(result.transcript);
                const bool ja_native_contained = row.language == "ja" && !native_key.empty() &&
                    sv_key.find(native_key) != std::string::npos;
                const bool zh_whisper_selected = row.language == "zh" && selected && selected->engine == "whisper";
                const bool sv_moonshine_conflict = selected && selected->engine == "whisper" &&
                    moonshine_agree && !selected_agree;
                const bool sv_paraformer_conflict = selected && selected->engine == "paraformer_zh" &&
                    candidate_agree && !selected_agree;
                const bool agreement_ok = !sv_moonshine_conflict && !sv_paraformer_conflict &&
                    (candidate_agree || ja_native_contained || zh_whisper_selected);

                const bool sv_accept = !sv_text.empty() && cps_ok && len_ok && agreement_ok;
                std::string reject_reason;
                if (!sv_text.empty() && !cps_ok)  reject_reason = "cps_too_high";
                else if (!sv_text.empty() && !len_ok) reject_reason = "len_ratio";
                else if (!sv_text.empty() && sv_moonshine_conflict) reject_reason = "moonshine_conflict";
                else if (!sv_text.empty() && sv_paraformer_conflict) reject_reason = "paraformer_conflict";
                else if (!sv_text.empty() && !agreement_ok) reject_reason = "candidate_disagree";

                if (sv_accept) {
                    result.transcript = sv_text;
                    result.extra_json_fields += ",\"sv_text\":\"" + json_escape(sv_text)
                        + "\",\"sv_cps\":" + std::to_string(cps)
                        + ",\"sv_winner\":true,\"route_final\":\"realtime_dll+sv\"";
                } else {
                    result.extra_json_fields += ",\"sv_text\":\"" + json_escape(sv_text)
                        + "\",\"sv_cps\":" + std::to_string(cps)
                        + ",\"sv_winner\":false"
                        + (reject_reason.empty() ? "" : ",\"sv_reject\":\"" + reject_reason + "\"")
                        + ",\"route_final\":\"realtime_dll\"";
                }
            }
            } else {
            reset_realtime_bench_session(session, row);
            PcmStats stats = compute_pcm_stats(*input_samples);
            int ret = 0;
            if (production_replay) {
                std::string gate_reason;
                if (should_skip_realtime_final_input_with_profile(session, stats, gate_reason)) {
                    reset_joint_trace(session);
                    session->selected_engine = "none";
                    session->decision_reason = gate_reason;
                    session->joint_existing_status = "RejectedByExistingGate";
                    session->joint_selection_reason = "existing_gate";
                    session->joint_existing_wall_ms = 0;
                    session->joint_total_wall_ms = 0;
                } else {
                    auto segment = *input_samples;
                    ret = finalize_accepted_realtime_segment(session, segment, stats, row.index, stats.rms);
                }
            } else {
                ret = transcribe_realtime_samples(session, *input_samples, false, row.index, stats.rms, &stats);
            }
            result.transcript = session->final_text;
            result.latency_ms = session->latency_ms;
            if (ret != 0 || !session->error.empty()) {
                result.error = session->error.empty() ? "realtime transcription failed" : session->error;
            }
            std::string native_transcript = result.transcript;
            std::string output_engine = session->selected_engine;
            std::string output_reason = session->decision_reason;
            int64_t managed_wall_ms = -1;
            if (production_replay && finalizer && result.error.empty()) {
                std::vector<char> output(65536, '\0');
                char selected_engine[256] = {};
                char reason[1024] = {};
                const auto managed_start = std::chrono::steady_clock::now();
                const auto poll_json = make_poll_json(session);
                // Polling can publish an already completed staged validation.
                // Preserve the same native snapshot the managed caller receives.
                native_transcript = session->final_text;
                result.latency_ms = session->latency_ms;
                output_engine = session->selected_engine;
                output_reason = session->decision_reason;
                const int status = finalizer(poll_json.c_str(), row.language.c_str(), row.scene.c_str(),
                    output.data(), static_cast<int>(output.size()), selected_engine, sizeof(selected_engine), reason, sizeof(reason));
                managed_wall_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - managed_start).count();
                if (status != 0 || !std::memchr(output.data(), '\0', output.size()) ||
                    !std::memchr(selected_engine, '\0', sizeof(selected_engine)) ||
                    !std::memchr(reason, '\0', sizeof(reason)) || selected_engine[0] == '\0') {
                    result.error = "managed realtime output processing failed";
                    result.transcript.clear();
                    output_engine = "none";
                    output_reason = "managed_finalizer_failed";
                } else {
                    result.transcript = output.data();
                    output_engine = selected_engine;
                    output_reason = reason;
                }
            }
            result.extra_json_fields = "\"route\":\"realtime_dll\",\"engine\":\"" + json_escape(session->realtime_engine) +
                "\",\"selected_engine\":\"" + json_escape(output_engine) +
                "\",\"decision_reason\":\"" + json_escape(output_reason) +
                "\",\"language\":\"" + json_escape(session->language) + "\"";
            if (production_replay) result.extra_json_fields += finalizer
                ? ",\"mode\":\"production_replay\"" : ",\"mode\":\"native_segment_replay\"";
            if (production_replay && finalizer) {
                result.extra_json_fields += ",\"native_transcript\":\"" + json_escape(native_transcript) +
                    "\",\"native_selected_engine\":\"" + json_escape(session->selected_engine) +
                    "\",\"native_decision_reason\":\"" + json_escape(session->decision_reason) +
                    "\",\"latency_source\":\"native_selected_candidate\",\"managed_output_wall_ms\":" + std::to_string(managed_wall_ms);
            }
            if (production_replay && bench_joint_auto) {
                std::ostringstream extra;
                extra << ",\"existing_joint_status\":\"" << json_escape(session->joint_existing_status)
                      << "\",\"haas_status\":\"" << (session->addon_engine_id == "haas_ja" ? json_escape(session->joint_addon_status) : "NotRequested")
                      << "\",\"selection_reason\":\"" << json_escape(session->joint_selection_reason)
                      << "\",\"existing_joint_wall_ms\":" << session->joint_existing_wall_ms
                      << ",\"haas_wall_ms\":" << (session->addon_engine_id == "haas_ja" ? session->addon_candidate_wall_ms : -1)
                      << ",\"joint_total_wall_ms\":" << session->joint_total_wall_ms
                      << ",\"haas_added_wait_ms\":" << (session->addon_engine_id == "haas_ja" ? session->joint_added_wait_ms : -1)
                      << ",\"execution_order\":[";
                for (size_t i = 0; i < session->joint_execution_order.size(); ++i) {
                    if (i) extra << ",";
                    extra << "\"" << json_escape(session->joint_execution_order[i]) << "\"";
                }
                extra << "],\"candidates\":[";
                const auto append_candidate = [&](const char* name, const JointCandidateTrace& c) {
                    extra << "{\"engine\":\"" << name << "\",\"attempted\":" << (c.attempted ? "true" : "false")
                          << ",\"success\":" << (c.success ? "true" : "false") << ",\"accepted\":" << (c.accepted ? "true" : "false")
                          << ",\"text\":\"" << json_escape(c.text) << "\",\"latencyMs\":" << c.latency_ms
                          << ",\"reason\":\"" << json_escape(c.reason) << "\"}";
                };
                append_candidate("moonshine", session->joint_moonshine);
                extra << ",";
                append_candidate("whisper", session->joint_whisper);
                extra << ",{\"engine\":\"" << json_escape(session->addon_engine_id) << "\",\"attempted\":" << (session->addon_candidate_attempted ? "true" : "false")
                      << ",\"success\":" << (session->addon_candidate_success ? "true" : "false")
                      << ",\"text\":\"" << json_escape(session->addon_candidate_text) << "\",\"latencyMs\":" << session->addon_candidate_ms
                      << ",\"reason\":\"" << json_escape(session->joint_addon_status) << "\"}]";
                result.extra_json_fields += extra.str();
            }
            if (is_addon_engine(session->realtime_engine) && session->addon_candidate_attempted) {
                result.extra_json_fields += ",\"addon_candidate\":{\"engine\":\"" + json_escape(session->addon_engine_id) + "\",\"text\":\"" + json_escape(session->addon_candidate_text)
                    + "\",\"success\":" + (session->addon_candidate_success ? "true" : "false")
                    + ",\"latencyMs\":" + std::to_string(session->addon_candidate_ms)
                    + ",\"wallMs\":" + std::to_string(session->addon_candidate_wall_ms)
                    + ",\"confidence_available\":false,\"confidence_score\":null}";
            }
            if (is_addon_engine(session->realtime_engine) || session->addon_joint_requested) {
                const std::string addon_status = session->realtime_engine == "joint_auto" ? session->joint_addon_status :
                    !session->addon_candidate_attempted ? "NotAttempted" : !session->addon_candidate_success ? "InferenceFailure" :
                    session->addon_candidate_text.empty() ? "Empty" : "Succeeded";
                result.extra_json_fields += ",\"addon_engine_id\":\"" + json_escape(session->addon_engine_id) +
                    "\",\"addon_model_id\":\"" + json_escape(session->addon_model_id) +
                    "\",\"addon_model_revision\":\"" + json_escape(session->addon_model_revision) +
                    "\",\"addon_language_mode\":\"" + json_escape(session->addon_language_mode) +
                    "\",\"addon_status\":\"" + json_escape(addon_status) +
                    "\",\"addon_wall_ms\":" + std::to_string(session->addon_candidate_wall_ms) +
                    ",\"addon_added_wait_ms\":" + std::to_string(session->joint_added_wait_ms);
            }
            }
            }
        }

        const bool moonshine_profile = result.extra_json_fields.find("\"selected_engine\":\"moonshine\"") != std::string::npos;
        result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, moonshine_profile);
        result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, moonshine_profile);
        if (!result.extra_json_fields.empty()) result.extra_json_fields += ",";
        result.extra_json_fields += "\"wall_ms\":" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - row_wall_start).count());
        results.push_back(result);
    }

    destroy_current_session();

    std::ofstream ofs(out_jsonl_path_utf8);
    if (!ofs) {
        *out_json = dup_string(make_error_json("cannot open output file", "write"));
        return -1;
    }
    for (const auto& result : results) {
        ofs << mobmate::bench_result_to_jsonl(result) << "\n";
    }
    ofs.close();

    auto summary = mobmate::compute_summary(results);
    std::ostringstream oss;
    oss << "{\"ok\":true"
        << ",\"route\":\"realtime_dll\""
        << ",\"total\":" << summary.total
        << ",\"exactCount\":" << summary.exact_count
        << ",\"avgCer\":" << summary.avg_cer
        << ",\"avgLatencyMs\":" << summary.avg_latency_ms
        << ",\"errorCount\":" << summary.error_count
        << ",\"resultPath\":\"" << json_escape(out_jsonl_path_utf8) << "\"}";
    *out_json = dup_string(oss.str());
    return 0;
#endif // _WIN32
}

MMV2_API int mmv2_realtime_bench_manifest(const char* manifest_path_utf8,
    const char* out_jsonl_path_utf8, const char* options_json_utf8, const char** out_json) {
    return realtime_bench_manifest_impl(manifest_path_utf8, out_jsonl_path_utf8, options_json_utf8, out_json, nullptr);
}

MMV2_API int mmv2_realtime_bench_manifest_processed(const char* manifest_path_utf8,
    const char* out_jsonl_path_utf8, const char* options_json_utf8,
    mmv2_replay_finalizer finalizer, const char** out_json) {
    if (out_json) *out_json = nullptr;
#ifdef _WIN32
    if (!out_json || !options_json_utf8 || !finalizer || !json_get_bool(options_json_utf8, "productionReplay", false)) return -1;
    return realtime_bench_manifest_impl(manifest_path_utf8, out_jsonl_path_utf8, options_json_utf8, out_json, finalizer);
#else
    if (out_json) *out_json = dup_string(make_error_json("managed production replay is Windows-only", "unsupported"));
    return -1;
#endif
}

MMV2_API int mmv2_clipboard_set_text(const char* utf8_text) {
#ifdef _WIN32
    if (!utf8_text) return -1;

    // Convert UTF-8 to UTF-16
    std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>> converter;
    std::wstring wide = converter.from_bytes(utf8_text);

    if (!OpenClipboard(nullptr)) return -1;
    bool ok = true;
    if (!EmptyClipboard()) { ok = false; }
    else {
        size_t byte_count = (wide.size() + 1) * sizeof(wchar_t);
        HGLOBAL h = GlobalAlloc(GMEM_MOVEABLE, byte_count);
        if (!h) { ok = false; }
        else {
            void* p = GlobalLock(h);
            if (!p) { GlobalFree(h); ok = false; }
            else {
                memcpy(p, wide.c_str(), byte_count);
                GlobalUnlock(h);
                if (!SetClipboardData(CF_UNICODETEXT, h)) {
                    ok = false;
                }
                // On success, GlobalFree is handled by clipboard
            }
        }
    }
    CloseClipboard();
    return ok ? 0 : -1;
#else
    (void)utf8_text;
    return -1;
#endif
}

/* ---- Phase 7.4-2: TTS PCM16 playback ---- */

static mobmate::PcmPlaybackOwner gPcmPlaybackOwner;
static std::mutex gTtsOutputMutex;

#ifdef _WIN32
// Windows: TTS output device ID/GUID (empty = default render endpoint)
static std::string gTtsWinOutputDevice;

static std::wstring utf8_to_wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int wide_len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.c_str(), -1, nullptr, 0);
    if (wide_len <= 0) return std::wstring();
    std::wstring w(static_cast<size_t>(wide_len), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.c_str(), -1, w.data(), wide_len) <= 0) return std::wstring();
    if (!w.empty() && w.back() == L'\0') w.pop_back();
    return w;
}

static std::wstring lower_wide(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    return s;
}

static bool wasapi_id_matches(const wchar_t* endpoint_id, const std::wstring& requested_guid) {
    if (!endpoint_id || requested_guid.empty()) return false;
    auto endpoint = lower_wide(endpoint_id);
    auto requested = lower_wide(requested_guid);
    return endpoint.find(requested) != std::wstring::npos;
}

static IMMDevice* resolve_wasapi_render_device(const std::string& device_id_or_guid, bool allow_default = true) {
    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    if (FAILED(hr) || !enumerator) return nullptr;

    IMMDevice* device = nullptr;
    std::wstring requested = utf8_to_wide(device_id_or_guid);
    if (!requested.empty()) {
        IMMDeviceCollection* collection = nullptr;
        hr = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &collection);
        if (SUCCEEDED(hr) && collection) {
            UINT count = 0;
            collection->GetCount(&count);
            for (UINT i = 0; i < count && !device; ++i) {
                IMMDevice* candidate = nullptr;
                if (SUCCEEDED(collection->Item(i, &candidate)) && candidate) {
                    LPWSTR endpointId = nullptr;
                    if (SUCCEEDED(candidate->GetId(&endpointId)) && endpointId) {
                        if (wasapi_id_matches(endpointId, requested)) {
                            device = candidate;
                            candidate = nullptr;
                        }
                        CoTaskMemFree(endpointId);
                    }
                    if (candidate) candidate->Release();
                }
            }
            collection->Release();
        }
    }

    if (!device && allow_default) {
        enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    }
    enumerator->Release();
    return device;
}

static bool wasapi_same_device(IMMDevice* a, IMMDevice* b) {
    if (!a || !b) return false;
    LPWSTR aid = nullptr;
    LPWSTR bid = nullptr;
    bool same = false;
    if (SUCCEEDED(a->GetId(&aid)) && aid && SUCCEEDED(b->GetId(&bid)) && bid) {
        same = lower_wide(aid) == lower_wide(bid);
    }
    if (aid) CoTaskMemFree(aid);
    if (bid) CoTaskMemFree(bid);
    return same;
}

struct WasapiTtsEndpoint {
    IMMDevice* device = nullptr;
    IAudioClient* audioClient = nullptr;
    IAudioRenderClient* renderClient = nullptr;
    UINT32 bufferFrames = 0;
    UINT32 writtenFrames = 0;
    bool started = false;
    int volumePercent = 100;
};

static void release_wasapi_tts_endpoint(WasapiTtsEndpoint& ep) {
    if (ep.started && ep.audioClient) ep.audioClient->Stop();
    if (ep.renderClient) ep.renderClient->Release();
    if (ep.audioClient) ep.audioClient->Release();
    if (ep.device) ep.device->Release();
    ep = {};
}

static int init_wasapi_tts_endpoint(WasapiTtsEndpoint& ep, IMMDevice* device,
                                    int sample_rate, int channels, int volumePercent) {
    if (!device) return -3;
    ep.device = device;
    if (volumePercent < 0) ep.volumePercent = 0;
    else if (volumePercent > 100) ep.volumePercent = 100;
    else ep.volumePercent = volumePercent;

    HRESULT hr = ep.device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                     reinterpret_cast<void**>(&ep.audioClient));
    if (FAILED(hr) || !ep.audioClient) return -3;

    WAVEFORMATEX fmt = {};
    fmt.wFormatTag = WAVE_FORMAT_PCM;
    fmt.nChannels = static_cast<WORD>(channels);
    fmt.nSamplesPerSec = static_cast<DWORD>(sample_rate);
    fmt.wBitsPerSample = 16;
    fmt.nBlockAlign = static_cast<WORD>(channels * sizeof(int16_t));
    fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
    fmt.cbSize = 0;

    REFERENCE_TIME bufferDuration = 1000000; // 100ms
    DWORD flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    hr = ep.audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, bufferDuration, 0, &fmt, nullptr);
    if (FAILED(hr)) return -3;

    hr = ep.audioClient->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(&ep.renderClient));
    if (FAILED(hr) || !ep.renderClient) return -4;

    hr = ep.audioClient->GetBufferSize(&ep.bufferFrames);
    if (FAILED(hr) || ep.bufferFrames == 0) return -4;

    hr = ep.audioClient->Start();
    if (FAILED(hr)) return -4;
    ep.started = true;
    return 0;
}

static int write_wasapi_tts_endpoint(WasapiTtsEndpoint& ep, const int16_t* samples,
                                     UINT32 totalFrames, int channels) {
    if (!ep.started || ep.writtenFrames >= totalFrames) return 0;
    UINT32 padding = 0;
    HRESULT hr = ep.audioClient->GetCurrentPadding(&padding);
    if (FAILED(hr)) return -4;
    UINT32 available = ep.bufferFrames > padding ? (ep.bufferFrames - padding) : 0;
    if (available == 0) return 0;

    UINT32 framesToWrite = (available < (totalFrames - ep.writtenFrames))
        ? available : (totalFrames - ep.writtenFrames);
    BYTE* pData = nullptr;
    hr = ep.renderClient->GetBuffer(framesToWrite, &pData);
    if (FAILED(hr) || !pData) return -4;

    const int16_t* src = samples + static_cast<size_t>(ep.writtenFrames) * channels;
    auto* dst = reinterpret_cast<int16_t*>(pData);
    const size_t sampleCount = static_cast<size_t>(framesToWrite) * channels;
    if (ep.volumePercent >= 100) {
        memcpy(dst, src, sampleCount * sizeof(int16_t));
    } else if (ep.volumePercent <= 0) {
        memset(dst, 0, sampleCount * sizeof(int16_t));
    } else {
        for (size_t i = 0; i < sampleCount; ++i) {
            dst[i] = static_cast<int16_t>((static_cast<int>(src[i]) * ep.volumePercent) / 100);
        }
    }

    hr = ep.renderClient->ReleaseBuffer(framesToWrite, 0);
    if (FAILED(hr)) return -4;
    ep.writtenFrames += framesToWrite;
    return 0;
}
#else
// Linux: miniaudio-based TTS playback state
static std::string gTtsLinuxOutputDevice; // empty = default output device

struct TtsPlaybackLinuxState {
    std::vector<int16_t> pcmData;
    std::atomic<size_t>  offset{0};
    std::atomic<bool>    done{false};
    std::atomic<bool>    published{false};
    mobmate::PcmPlaybackOwner::Ticket ticket;
    ma_device            device{};
};

static void tts_linux_data_callback(ma_device* pDevice, void* pOutput,
                                     const void* /*pInput*/, ma_uint32 frameCount) {
    auto* state = static_cast<TtsPlaybackLinuxState*>(pDevice->pUserData);
    if (!state || !pOutput) return;
    auto* out = static_cast<int16_t*>(pOutput);
    size_t needed = frameCount * pDevice->playback.channels;
    // ma_device_start waits for backend startup and can invoke this callback
    // before returning. Never hold the owner gate across that wait: protect
    // only the actual PCM publication, including the initial prefill.
    const int result = gPcmPlaybackOwner.commit(state->ticket, [&] {
        size_t cur = state->offset.load();
        size_t avail = (state->pcmData.size() > cur) ? (state->pcmData.size() - cur) : 0;
        size_t toCopy = std::min(needed, avail);
        if (toCopy > 0) {
            memcpy(out, state->pcmData.data() + cur, toCopy * sizeof(int16_t));
            state->offset.store(cur + toCopy);
            state->published.store(true);
        }
        if (toCopy < needed) {
            memset(out + toCopy, 0, (needed - toCopy) * sizeof(int16_t));
            state->done.store(true);
        }
        return 0;
    });
    if (result != 0) memset(out, 0, needed * sizeof(int16_t));
}
#endif // !_WIN32

static int play_pcm_backend(const mobmate::PcmPlaybackOwner::Ticket& ticket,
    const int16_t* samples, int sample_count, int sample_rate, int channels,
    int monitor_volume_percent, mmv2_tts_started_callback started, void* user_data) {
#ifdef _WIN32
    int ret = 0;
    std::string output_device;
    { std::lock_guard<std::mutex> lock(gTtsOutputMutex); output_device = gTtsWinOutputDevice; }
    // Identified fillers never fall back from a missing selected endpoint.
    if (ticket->filler && output_device.empty()) return -3;

    HRESULT cohr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool coInitialized = SUCCEEDED(cohr);
    if (cohr == RPC_E_CHANGED_MODE) coInitialized = false;

    WasapiTtsEndpoint mainEp;
    WasapiTtsEndpoint monitorEp;
    struct Cleanup {
        WasapiTtsEndpoint& main;
        WasapiTtsEndpoint& monitor;
        bool com;
        ~Cleanup() {
            release_wasapi_tts_endpoint(monitor);
            release_wasapi_tts_endpoint(main);
            if (com) CoUninitialize();
        }
    } cleanup{mainEp, monitorEp, coInitialized};
    bool acknowledged = false;

    do {
        if (ticket->cancelled.load()) { ret = -6; break; }
        IMMDevice* mainDevice = resolve_wasapi_render_device(output_device, !ticket->filler);
        if (!mainDevice) { ret = -3; break; }
        ret = init_wasapi_tts_endpoint(mainEp, mainDevice, sample_rate, channels, 100);
        if (ret != 0) break;

        if (monitor_volume_percent > 0 && !output_device.empty()) {
            IMMDevice* defaultDevice = resolve_wasapi_render_device(std::string());
            if (defaultDevice) {
                if (wasapi_same_device(mainEp.device, defaultDevice)) {
                    defaultDevice->Release();
                } else {
                    WasapiTtsEndpoint candidate;
                    if (init_wasapi_tts_endpoint(candidate, defaultDevice, sample_rate, channels, monitor_volume_percent) == 0) {
                        monitorEp = candidate;
                    } else {
                        release_wasapi_tts_endpoint(candidate);
                    }
                }
            }
        }

        const UINT32 totalFrames = static_cast<UINT32>(sample_count / channels);
        DWORD startTick = GetTickCount();
        while ((mainEp.writtenFrames < totalFrames || (monitorEp.started && monitorEp.writtenFrames < totalFrames)) && !ticket->cancelled.load()) {
            if (GetTickCount() - startTick > 30000) { ret = -5; break; }
            UINT32 beforeMain = mainEp.writtenFrames;
            UINT32 beforeMonitor = monitorEp.writtenFrames;
            ret = gPcmPlaybackOwner.commit(ticket, [&] {
                const int main_result = write_wasapi_tts_endpoint(mainEp, samples, totalFrames, channels);
                return main_result != 0 ? main_result : write_wasapi_tts_endpoint(monitorEp, samples, totalFrames, channels);
            });
            // Outside the owner gate; enqueue-only and rooted by synchronous Play.
            if (!acknowledged && mainEp.writtenFrames > 0) {
                acknowledged = true;
                if (started) started(ticket->id, user_data);
            }
            if (ret != 0) break;
            if (beforeMain == mainEp.writtenFrames && beforeMonitor == monitorEp.writtenFrames)
                gPcmPlaybackOwner.wait_cancelled(ticket, std::chrono::milliseconds(5));
        }

        DWORD drainStart = GetTickCount();
        while (!ticket->cancelled.load() && GetTickCount() - drainStart < 2000) {
            UINT32 mainPadding = 0;
            UINT32 monitorPadding = 0;
            bool mainBusy = mainEp.audioClient && SUCCEEDED(mainEp.audioClient->GetCurrentPadding(&mainPadding)) && mainPadding > 0;
            bool monitorBusy = monitorEp.audioClient && SUCCEEDED(monitorEp.audioClient->GetCurrentPadding(&monitorPadding)) && monitorPadding > 0;
            if (!mainBusy && !monitorBusy) break;
            gPcmPlaybackOwner.wait_cancelled(ticket, std::chrono::milliseconds(10));
        }
    } while (false);

    return ticket->cancelled.load() ? -6 : ret;
#else
    // Linux: miniaudio-based playback
    (void)monitor_volume_percent;
    TtsPlaybackLinuxState state;
    state.pcmData.assign(samples, samples + sample_count);
    state.ticket = ticket;
    std::string output_device;
    { std::lock_guard<std::mutex> lock(gTtsOutputMutex); output_device = gTtsLinuxOutputDevice; }
    if (ticket->filler && output_device.empty()) return -3;
    if (ticket->cancelled.load()) return -6;

    // Build device config
    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.format   = ma_format_s16;
    cfg.playback.channels = static_cast<ma_uint32>(channels);
    cfg.sampleRate        = static_cast<ma_uint32>(sample_rate);
    cfg.dataCallback      = tts_linux_data_callback;
    cfg.pUserData         = &state;

    // Resolve output device by name (if configured)
    ma_context ctx;
    ma_device_id targetId;
    bool contextInitialized = false;
    bool useTargetId = false;
    if (!output_device.empty() &&
        ma_context_init(nullptr, 0, nullptr, &ctx) == MA_SUCCESS) {
        contextInitialized = true;
        ma_device_info* pDevs = nullptr;
        ma_uint32 devCount = 0;
        if (ma_context_get_devices(&ctx, &pDevs, &devCount, nullptr, nullptr) == MA_SUCCESS) {
            for (ma_uint32 i = 0; i < devCount; ++i) {
                if (std::string(pDevs[i].name) == output_device) {
                    targetId = pDevs[i].id;
                    useTargetId = true;
                    break;
                }
            }
            for (ma_uint32 i = 0; i < devCount; ++i) {
                if (useTargetId) break;
                if (!ticket->filler && std::string(pDevs[i].name).find(output_device) != std::string::npos) {
                    targetId = pDevs[i].id;
                    useTargetId = true;
                    break;
                }
            }
        }
    }
    if (useTargetId) cfg.playback.pDeviceID = &targetId;
    if (ticket->filler && !useTargetId) {
        if (contextInitialized) ma_context_uninit(&ctx);
        return -3;
    }

    ma_context* initContext = contextInitialized ? &ctx : nullptr;
    if (ma_device_init(initContext, &cfg, &state.device) != MA_SUCCESS) {
        if (contextInitialized) ma_context_uninit(&ctx);
        return -3;
    }
    if (ticket->cancelled.load()) {
        ma_device_uninit(&state.device);
        if (contextInitialized) ma_context_uninit(&ctx);
        return -6;
    }
    // This may wait for the backend worker and prefill, so it must be outside
    // the owner gate. The data callback commits each actual PCM publication.
    const int start_result = ma_device_start(&state.device) == MA_SUCCESS ? 0 : -4;
    if (start_result != 0) {
        ma_device_uninit(&state.device);
        if (contextInitialized) ma_context_uninit(&ctx);
        return start_result;
    }
    bool acknowledged = false;
    const auto acknowledge = [&] {
        if (!acknowledged && state.published.load()) {
            acknowledged = true;
            if (started) started(ticket->id, user_data);
        }
    };
    acknowledge();

    // Completion progress uses the existing bounded backend timeout. Cancel
    // wakes this condition immediately; no delay is used as a handoff barrier.
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (!state.done.load() && !ticket->cancelled.load() && std::chrono::steady_clock::now() < deadline) {
        acknowledge();
        gPcmPlaybackOwner.wait_cancelled(ticket, std::chrono::milliseconds(10));
    }
    acknowledge();
    ma_device_stop(&state.device);
    // A callback may have published between the last check and a timeout stop.
    // Stop has drained callbacks, so this is the last possible publication.
    acknowledge();
    ma_device_uninit(&state.device);
    if (contextInitialized) ma_context_uninit(&ctx);
    return ticket->cancelled.load() ? -6 : state.done.load() ? 0 : -5;
#endif
}

static int play_owned_pcm(uint64_t id, bool filler, const int16_t* samples,
    int sample_count, int sample_rate, int channels, int monitor_volume_percent,
    mmv2_tts_started_callback started, void* user_data) {
    if (!samples || sample_count <= 0 || sample_rate <= 0 || channels <= 0) return -2;
    const auto [ticket, status] = gPcmPlaybackOwner.begin(id, filler);
    if (!ticket) return status;
    int result;
    try { result = play_pcm_backend(ticket, samples, sample_count, sample_rate, channels, monitor_volume_percent, started, user_data); }
    catch (...) { result = -4; }
    gPcmPlaybackOwner.finish(ticket); // Backend and its destructors have exited.
    // Legacy callers historically received completion on an explicit Stop.
    return !filler && result == -6 ? 0 : result;
}

extern "C" MMV2_API int mmv2_tts_play_pcm16(const int16_t* samples, int sample_count,
    int sample_rate, int channels, int monitor_volume_percent) {
    return play_owned_pcm(0, false, samples, sample_count, sample_rate, channels, monitor_volume_percent, nullptr, nullptr);
}

MMV2_API int mmv2_tts_play_filler_pcm16(uint64_t request_id, const int16_t* samples,
    int sample_count, int sample_rate, int channels, int monitor_volume_percent,
    mmv2_tts_started_callback started, void* user_data) {
    return play_owned_pcm(request_id, true, samples, sample_count, sample_rate, channels, monitor_volume_percent, started, user_data);
}

MMV2_API int mmv2_tts_cancel_filler_and_wait(uint64_t request_id) {
    return gPcmPlaybackOwner.cancel_filler_and_wait(request_id);
}

MMV2_API int mmv2_tts_stop(void) {
    return gPcmPlaybackOwner.stop_legacy_and_wait();
}

MMV2_API int mmv2_tts_set_output_device(const char* device_name_or_id) {
    std::lock_guard<std::mutex> lock(gTtsOutputMutex);
#ifdef _WIN32
    gTtsWinOutputDevice = device_name_or_id ? std::string(device_name_or_id) : std::string();
    return 0;
#else
    gTtsLinuxOutputDevice = device_name_or_id ? std::string(device_name_or_id) : std::string();
    return 0;
#endif
}

// Phase 7A: Speaker identification API

MMV2_API int mmv2_speaker_reset(void) {
    // Reset active session's profile data and delete the JSON file
    if (gActiveSpeakerProfile) {
        speaker_reset(*gActiveSpeakerProfile);
    }
    if (!gSpeakerProfilePath.empty()) {
        std::remove(gSpeakerProfilePath.c_str());
    }
    return 0;
}

MMV2_API int mmv2_speaker_set_enabled(int enabled) {
    gSpeakerIdEnabled = enabled != 0;
    return 0;
}

/* ---- SileroVAD: standalone C#-callable exports for HearingService ---- */

/* Create a SileroVAD handle. Returns NULL on failure. Caller must mmv2_vad_free. */
MMV2_API void* mmv2_vad_create(const char* model_path_utf8, const char* ort_dll_dir_utf8) {
    char err[256] = {};
    return silero_vad_init(model_path_utf8, ort_dll_dir_utf8, err, sizeof(err));
}

/* Score a PCM16LE chunk (16kHz mono). Returns speech probability [0,1], or -1 on error. */
MMV2_API double mmv2_vad_score(void* handle, const int16_t* pcm16, int sample_count) {
    if (!handle) return -1.0;
    return silero_vad_score(static_cast<silero_vad_handle*>(handle),
                             pcm16, sample_count, 16000);
}

/* Reset LSTM state (call at start of new utterance / session restart). */
MMV2_API void mmv2_vad_reset(void* handle) {
    if (handle) silero_vad_reset(static_cast<silero_vad_handle*>(handle));
}

/* Free handle. Safe to call with NULL. */
MMV2_API void mmv2_vad_free(void* handle) {
    silero_vad_free(static_cast<silero_vad_handle*>(handle));
}
