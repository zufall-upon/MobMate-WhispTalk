#include "mobmate_native_core/native_api.h"
#include "mobmate_native_core/voice_conversion.h"
#include "mobmate_native_core/moonshine_bridge.h"
#include "mobmate_native_core/audio_prefilter.h"
#include "mobmate_native_core/silero_vad.h"
#include "mobmate_native_core/wave.h"
#include "vc_onset_filler.h"

// Prevent Windows min/max macros from colliding with std::min/std::max.
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#endif

// miniaudio: on Windows compile implementation here; on Linux it is already
// compiled in audio_capture_linux.cpp (part of mobmate_native_core).
#ifdef _WIN32
#define MINIAUDIO_IMPLEMENTATION
#endif
#include "../../vendor/miniaudio.h"

#include "onnxruntime_c_api.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <functional>
#include <future>
#include <map>
#include <limits>
#include <random>
#include <sstream>
#include <string>
#include <condition_variable>
#include <cstdint>
#include <thread>
#include <utility>
#include <vector>

namespace {

// ── helpers ─────────────────────────────────────────────────────────

static char* vc_dup_string(const std::string& s) {
    char* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (p) std::memcpy(p, s.data(), s.size() + 1);
    return p;
}

static std::string vc_json_escape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default: out += c; break;
        }
    }
    return out;
}

#ifdef _WIN32
static std::string vc_wide_to_utf8(const wchar_t* value) {
    if (!value) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) return "";
    std::string out(static_cast<size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, value, -1, out.data(), size, nullptr, nullptr) <= 0) return "";
    out.resize(static_cast<size_t>(size - 1));
    return out;
}

static std::wstring vc_utf8_to_wide(const std::string& value) {
    if (value.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, nullptr, 0);
    if (size <= 1) return {};
    std::wstring out(static_cast<size_t>(size), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, out.data(), size) <= 0) return {};
    out.resize(static_cast<size_t>(size - 1));
    return out;
}
#endif

static const char* vc_json_value_start(const char* json, const char* key) {
    if (!json || !key) return nullptr;
    const std::string search_key = "\"" + std::string(key) + "\"";
    const char* search = json;
    while (const char* pos = std::strstr(search, search_key.c_str())) {
        pos += search_key.size();
        search = pos;
        while (*pos == ' ' || *pos == '\t' || *pos == '\r' || *pos == '\n') ++pos;
        if (*pos != ':') continue; // a value string is not an option key
        ++pos;
        while (*pos == ' ' || *pos == '\t' || *pos == '\r' || *pos == '\n') ++pos;
        return pos;
    }
    return nullptr;
}

static std::string vc_json_get_string(const char* json, const char* key, const char* default_val) {
    const char* pos = vc_json_value_start(json, key);
    if (!pos || *pos != '"') return default_val ? default_val : "";
    ++pos;
    const char* end = pos;
    while (*end) {
        if (*end == '"' && (end == pos || *(end - 1) != '\\')) break;
        ++end;
    }
    if (*end != '"') return default_val ? default_val : "";
    return std::string(pos, static_cast<size_t>(end - pos));
}

static int vc_json_get_int(const char* json, const char* key, int default_val) {
    if (!json || !key) return default_val;
    const std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t') ++pos;
    try {
        if (*pos == '"') {
            ++pos;
            return std::stoi(std::string(pos, static_cast<size_t>(std::strcspn(pos, "\""))));
        }
        return std::stoi(pos);
    } catch (...) {
        return default_val;
    }
}

static float vc_json_get_float(const char* json, const char* key, float default_val) {
    if (!json || !key) return default_val;
    const std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t') ++pos;
    try {
        if (*pos == '"') { ++pos; return std::stof(std::string(pos, static_cast<size_t>(std::strcspn(pos, "\"")))); }
        return std::stof(pos);
    } catch (...) { return default_val; }
}

// Extract a nested JSON object value as a substring (e.g. "ncnnComponents": { ... })
static std::string vc_json_get_object(const char* json, const char* key) {
    if (!json || !key) return "";
    const std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return "";
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t' || *pos == '\n' || *pos == '\r') ++pos;
    if (*pos != '{') return "";
    int depth = 1;
    const char* start = pos;
    ++pos;
    while (*pos && depth > 0) {
        if (*pos == '{') ++depth;
        else if (*pos == '}') --depth;
        ++pos;
    }
    if (depth != 0) return "";
    return std::string(start, static_cast<size_t>(pos - start));
}

static bool vc_json_get_bool(const char* json, const char* key, bool default_val) {
    if (!json || !key) return default_val;
    const std::string search_key = "\"" + std::string(key) + "\"";
    const char* pos = std::strstr(json, search_key.c_str());
    if (!pos) return default_val;
    pos += search_key.size();
    while (*pos == ' ' || *pos == ':' || *pos == '\t') ++pos;
    if (std::strncmp(pos, "true", 4) == 0) return true;
    if (std::strncmp(pos, "false", 5) == 0) return false;
    return default_val;
}

static std::string vc_make_dump_path(const std::string& dump_dir, int seg_id,
                                     const std::string& type, const std::string& ext) {
    if (dump_dir.empty()) return "";
    seg_id = ((seg_id % 10) + 10) % 10;
    return (std::filesystem::path(dump_dir) /
            ("seg_" + std::to_string(seg_id) + "_" + type + "." + ext)).string();
}

static void vc_cleanup_debug_dumps(const std::string& dump_dir) {
    if (dump_dir.empty()) return;
    std::error_code ec;
    auto dir = std::filesystem::path(dump_dir);
    std::filesystem::create_directories(dir, ec);
    if (!std::filesystem::exists(dir, ec)) return;
    for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
        if (ec) break;
        if (!entry.is_regular_file(ec)) continue;
        auto path = entry.path();
        const std::string name = path.filename().string();
        bool keep = false;
        for (int i = 0; i < 10; ++i) {
            keep = keep || name == ("seg_" + std::to_string(i) + "_vc_raw_input.wav");
            keep = keep || name == ("seg_" + std::to_string(i) + "_vc_input.wav");
            keep = keep || name == ("seg_" + std::to_string(i) + "_vc_output.wav");
            keep = keep || name == ("seg_" + std::to_string(i) + "_vc_rvc_raw.wav");
            keep = keep || name == ("seg_" + std::to_string(i) + "_vc_rvc_final.wav");
            keep = keep || name == ("seg_" + std::to_string(i) + "_rvc_meta.json");
            keep = keep || name == ("seg_" + std::to_string(i) + "_meta.json");
        }
        if (!keep && (path.extension() == ".wav" || path.extension() == ".json"))
            std::filesystem::remove(path, ec);
    }
}

struct VcInt16Stats {
    float rms = 0.0f;
    float peak = 0.0f;
    size_t clip_samples = 0;
};

static VcInt16Stats vc_compute_int16_stats(const std::vector<int16_t>& samples) {
    VcInt16Stats st{};
    if (samples.empty()) return st;
    double sum_sq = 0.0;
    double peak = 0.0;
    for (int16_t s : samples) {
        double v = std::abs(static_cast<double>(s));
        sum_sq += v * v;
        if (v > peak) peak = v;
        if (v >= 32767.0) ++st.clip_samples;
    }
    st.rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(samples.size())));
    st.peak = static_cast<float>(peak);
    return st;
}

static void vc_dump_segment_async(std::string dump_dir, int seg_id, std::vector<int16_t> raw_samples,
                                   std::vector<int16_t> input_samples, std::vector<int16_t> output_samples,
                                    uint32_t sample_rate, float noise_floor, float noise_std,
                                    float peak_p95, float silero_p90, float start_prob,
                                    float end_prob, size_t underruns, size_t overflows,
                                    float agc_start_gain, float agc_max_gain) {
    std::thread([dump_dir = std::move(dump_dir), seg_id, raw_samples = std::move(raw_samples),
                 input_samples = std::move(input_samples),
                 output_samples = std::move(output_samples), sample_rate,
                 noise_floor, noise_std, peak_p95, silero_p90, start_prob, end_prob, underruns, overflows,
                 agc_start_gain, agc_max_gain]() {
        if (dump_dir.empty() || (raw_samples.empty() && input_samples.empty() && output_samples.empty())) return;
        vc_cleanup_debug_dumps(dump_dir);
        std::string raw_wav_path = vc_make_dump_path(dump_dir, seg_id, "vc_raw_input", "wav");
        if (!raw_wav_path.empty() && !raw_samples.empty())
            mobmate::write_wav_pcm16(raw_wav_path, raw_samples.data(), static_cast<int>(raw_samples.size()), sample_rate);
        std::string input_wav_path = vc_make_dump_path(dump_dir, seg_id, "vc_input", "wav");
        if (!input_wav_path.empty() && !input_samples.empty())
            mobmate::write_wav_pcm16(input_wav_path, input_samples.data(), static_cast<int>(input_samples.size()), sample_rate);
        std::string output_wav_path = vc_make_dump_path(dump_dir, seg_id, "vc_output", "wav");
        if (!output_wav_path.empty() && !output_samples.empty()) {
            mobmate::write_wav_pcm16(output_wav_path, output_samples.data(), static_cast<int>(output_samples.size()), sample_rate);
        }

        VcInt16Stats raw_stats = vc_compute_int16_stats(raw_samples);
        VcInt16Stats input_stats = vc_compute_int16_stats(input_samples);
        VcInt16Stats output_stats = vc_compute_int16_stats(output_samples);
        std::string meta_path = vc_make_dump_path(dump_dir, seg_id, "meta", "json");
        if (!meta_path.empty()) {
            std::ofstream os(meta_path, std::ios::trunc);
            if (os.is_open()) {
                os << "{"
                   << "\"seg_id\":" << seg_id << ","
                   << "\"sample_rate\":" << sample_rate << ","
                   << "\"raw_sample_count\":" << raw_samples.size() << ","
                   << "\"raw_rms\":" << raw_stats.rms << ","
                   << "\"raw_peak\":" << raw_stats.peak << ","
                   << "\"raw_clip_samples\":" << raw_stats.clip_samples << ","
                   << "\"sample_count\":" << input_samples.size() << ","
                   << "\"duration_ms\":" << static_cast<int>(input_samples.size() * 1000.0 / std::max(1u, sample_rate)) << ","
                   << "\"rms\":" << input_stats.rms << ","
                   << "\"peak\":" << input_stats.peak << ","
                   << "\"input_sample_count\":" << input_samples.size() << ","
                   << "\"input_rms\":" << input_stats.rms << ","
                   << "\"input_peak\":" << input_stats.peak << ","
                   << "\"input_clip_samples\":" << input_stats.clip_samples << ","
                   << "\"output_sample_count\":" << output_samples.size() << ","
                   << "\"output_duration_ms\":" << static_cast<int>(output_samples.size() * 1000.0 / std::max(1u, sample_rate)) << ","
                   << "\"output_rms\":" << output_stats.rms << ","
                   << "\"output_peak\":" << output_stats.peak << ","
                   << "\"output_clip_samples\":" << output_stats.clip_samples << ","
                   << "\"agc_start_gain\":" << agc_start_gain << ","
                   << "\"agc_max_gain\":" << agc_max_gain << ","
                   << "\"noise_floor_rms\":" << noise_floor << ","
                   << "\"noise_rms_stddev\":" << noise_std << ","
                   << "\"peak_p95\":" << peak_p95 << ","
                   << "\"silero_p90\":" << silero_p90 << ","
                   << "\"start_prob\":" << start_prob << ","
                   << "\"end_prob\":" << end_prob << ","
                   << "\"underrun_count\":" << underruns << ","
                   << "\"overflow_count\":" << overflows
                   << "}\n";
            }
        }
    }).detach();
}

static void vc_save_final_wav_async(std::string output_dir, std::vector<int16_t> samples, uint32_t sample_rate) {
    std::thread([output_dir = std::move(output_dir), samples = std::move(samples), sample_rate]() {
        if (output_dir.empty() || samples.empty() || sample_rate == 0) return;
        static std::mutex output_mutex;
        static std::atomic<uint64_t> output_sequence{0};
        std::lock_guard<std::mutex> lock(output_mutex);
        std::error_code ec;
        std::filesystem::create_directories(output_dir, ec);
        if (ec) return;
        const auto now = std::chrono::system_clock::now();
        const auto millis = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()).count();
        const auto sequence = output_sequence.fetch_add(1, std::memory_order_relaxed);
        const auto final_path = std::filesystem::path(output_dir) /
            (std::to_string(millis) + "_" + std::to_string(sequence) + "_mobfy.wav");
        const auto temporary_path = final_path.string() + ".tmp";
        mobmate::write_wav_pcm16(temporary_path, samples.data(), static_cast<int>(samples.size()), sample_rate);
        if (!std::filesystem::exists(temporary_path, ec)) return;
        std::filesystem::rename(temporary_path, final_path, ec);
        if (ec) {
            std::filesystem::remove(temporary_path, ec);
            return;
        }
        std::vector<std::filesystem::directory_entry> wavs;
        for (const auto& entry : std::filesystem::directory_iterator(output_dir, ec)) {
            if (ec) break;
            if (entry.is_regular_file(ec) && entry.path().extension() == ".wav") wavs.push_back(entry);
        }
        std::sort(wavs.begin(), wavs.end(), [](const auto& a, const auto& b) {
            std::error_code aec, bec;
            const auto at = a.last_write_time(aec);
            const auto bt = b.last_write_time(bec);
            return at == bt ? a.path().filename() < b.path().filename() : at < bt;
        });
        while (wavs.size() > 100) {
            std::filesystem::remove(wavs.front().path(), ec);
            wavs.erase(wavs.begin());
        }
    }).detach();
}

static void vc_dump_rvc_final_async(std::string dump_dir, int seg_id,
                                    std::vector<int16_t> rvc_raw, std::vector<int16_t> rvc_final,
                                    uint32_t sample_rate, size_t head_trimmed,
                                    int64_t onset_soften_sample, const std::string& decision,
                                    const std::string& asr_text, float asr_confidence) {
    std::thread([dump_dir = std::move(dump_dir), seg_id, rvc_raw = std::move(rvc_raw),
                 rvc_final = std::move(rvc_final), sample_rate, head_trimmed,
                 onset_soften_sample, decision, asr_text, asr_confidence]() {
        if (dump_dir.empty()) return;
        vc_cleanup_debug_dumps(dump_dir);
        const std::string raw_path = vc_make_dump_path(dump_dir, seg_id, "vc_rvc_raw", "wav");
        if (!raw_path.empty() && !rvc_raw.empty())
            mobmate::write_wav_pcm16(raw_path, rvc_raw.data(), static_cast<int>(rvc_raw.size()), sample_rate);
        else if (!raw_path.empty()) {
            std::error_code ec;
            std::filesystem::remove(raw_path, ec);
        }
        const std::string final_path = vc_make_dump_path(dump_dir, seg_id, "vc_rvc_final", "wav");
        if (!final_path.empty() && !rvc_final.empty())
            mobmate::write_wav_pcm16(final_path, rvc_final.data(), static_cast<int>(rvc_final.size()), sample_rate);
        else if (!final_path.empty()) {
            std::error_code ec;
            std::filesystem::remove(final_path, ec);
        }
        const VcInt16Stats raw_stats = vc_compute_int16_stats(rvc_raw);
        const VcInt16Stats final_stats = vc_compute_int16_stats(rvc_final);
        const std::string meta_path = vc_make_dump_path(dump_dir, seg_id, "rvc_meta", "json");
        if (!meta_path.empty()) {
            std::ofstream os(meta_path, std::ios::trunc);
            if (os.is_open()) {
                os << "{"
                   << "\"seg_id\":" << seg_id << ","
                   << "\"sample_rate\":" << sample_rate << ","
                   << "\"decision\":\"" << vc_json_escape(decision) << "\","
                   << "\"asr_text\":\"" << vc_json_escape(asr_text) << "\","
                   << "\"asr_confidence\":" << asr_confidence << ","
                   << "\"head_trimmed_samples\":" << head_trimmed << ","
                   << "\"onset_soften_sample\":" << onset_soften_sample << ","
                   << "\"rvc_raw_sample_count\":" << rvc_raw.size() << ","
                   << "\"rvc_raw_rms\":" << raw_stats.rms << ","
                   << "\"rvc_raw_peak\":" << raw_stats.peak << ","
                   << "\"rvc_raw_clip_samples\":" << raw_stats.clip_samples << ","
                   << "\"rvc_final_sample_count\":" << rvc_final.size() << ","
                   << "\"rvc_final_rms\":" << final_stats.rms << ","
                   << "\"rvc_final_peak\":" << final_stats.peak << ","
                   << "\"rvc_final_clip_samples\":" << final_stats.clip_samples
                   << "}\n";
            }
        }
    }).detach();
}

static mobmate::vc::VoiceConversionOptions parse_options(const char* options_json_utf8) {
    mobmate::vc::VoiceConversionOptions options;
    const char* json = options_json_utf8 ? options_json_utf8 : "";
    options.raw_json = json;
    options.model_id = vc_json_get_string(json, "modelId", "");
    if (options.model_id.empty()) options.model_id = vc_json_get_string(json, "id", "");
    options.model_path = vc_json_get_string(json, "modelPath", "");
    if (options.model_path.empty()) options.model_path = vc_json_get_string(json, "rvcModelDir", "");
    options.engine = vc_json_get_string(json, "engine", "");
    options.input_device_id = vc_json_get_string(json, "inputDeviceId", "");
    options.output_device_id = vc_json_get_string(json, "outputDeviceId", "");
    options.sample_rate = vc_json_get_int(json, "sampleRate", 0);
    options.frame_ms = vc_json_get_int(json, "frameMs", 0);
    options.hop_ms = vc_json_get_int(json, "hopMs", 0);
    // DSP parameters
    options.prosody_neutralizer_enabled = vc_json_get_bool(json, "prosodyNeutralizerEnabled", true);
    options.pressure_estimator_enabled  = vc_json_get_bool(json, "pressureEstimatorEnabled", true);
    options.adaptive_strength           = vc_json_get_bool(json, "adaptiveConversionStrength", true);
    options.neutralizer_preset           = vc_json_get_string(json, "neutralizerPreset", "flat");
    options.neutralizer_strength         = std::max(0.0f, std::min(1.0f, vc_json_get_float(json, "neutralizerStrength", 1.00f)));
    options.noise_cleanup                = vc_json_get_string(json, "noiseCleanup", "dtln");
    options.pressure_flatten             = vc_json_get_float(json, "pressureFlatten", -1.0f);
    options.pressure_suppressor_strength = std::max(0.0f, std::min(1.0f, vc_json_get_float(json, "pressureSuppressorStrength", 0.80f)));
    options.breath_suppress              = vc_json_get_float(json, "breathSuppress", -1.0f);
    options.f0_flatten                   = std::max(0.0f, std::min(1.0f, vc_json_get_float(json, "f0Flatten", 0.50f)));
    options.world_wet                    = std::max(0.0f, std::min(3.0f, vc_json_get_float(json, "worldWet", 2.0f)));
    options.experimental_voice_model     = vc_json_get_bool(json, "experimentalVoiceModel", false);
    options.energy_compression    = vc_json_get_float(json, "energyCompression", 0.95f);
    options.shout_suppression     = vc_json_get_float(json, "shoutSuppression", 0.75f);
    options.sharpness_suppression = vc_json_get_float(json, "sharpnessSuppression", 0.75f);
    options.noise_gate_threshold  = vc_json_get_float(json, "noiseGate", 0.35f);
    options.output_limiter_ceiling = vc_json_get_float(json, "outputLimiterCeiling", 0.85f);
    options.output_volume_percent = std::max(0.0f, std::min(300.0f, vc_json_get_float(json, "outputVolumePercent", 140.0f)));
    options.monitor_volume_percent = std::max(0.0f, std::min(100.0f, vc_json_get_float(json, "monitorVolumePercent", 0.0f)));
    options.wav_output_enabled = vc_json_get_bool(json, "wavOutputEnabled", false);
    options.wav_output_directory = vc_json_get_string(json, "wavOutputDirectory", "");
    // RVC ONNX model parameters
    options.content_encoder_file = vc_json_get_string(json, "contentEncoderFile", "");
    options.f0_model_file        = vc_json_get_string(json, "f0ModelFile", "");
    options.vc_model_file        = vc_json_get_string(json, "vcModelFile", "");
    options.speaker_id           = vc_json_get_int(json, "speakerId", 0);
    options.f0_shift_semitones   = vc_json_get_float(json, "f0ShiftSemitones", 1.0f);
    options.use_gpu              = vc_json_get_bool(json, "useGpu", false);
    // SileroVAD gating
    options.silero_vad_model_path = vc_json_get_string(json, "sileroVadModelPath", "");
    options.onnx_runtime_dll_dir  = vc_json_get_string(json, "onnxRuntimeDllDir", "");
    options.vad_start_threshold    = vc_json_get_float(json, "vadStartThreshold", 0.54f);
    options.vad_continue_threshold = vc_json_get_float(json, "vadContinueThreshold", 0.36f);
    options.laugh_asr_assist_enabled = vc_json_get_bool(json, "laughAsrAssistEnabled", true);
    options.moonshine_dll_dir = vc_json_get_string(json, "moonshineDllDir", "");
    options.moonshine_model_root = vc_json_get_string(json, "moonshineModelRoot", "");
    options.laugh_asr_language = vc_json_get_string(json, "laughAsrLanguage", "ja");
    options.laugh_asr_tokens = vc_json_get_string(json, "laughAsrTokens", "");
    // Speech Enhancement (DTLN)
    options.se_model_dir = vc_json_get_string(json, "seModelDir", "");
    return options;
}

static std::string normalize_engine_name(std::string engine) {
    std::transform(engine.begin(), engine.end(), engine.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return engine;
}

static bool vc_conversion_requested(const mobmate::vc::VoiceConversionOptions& options) {
    // Null remains an absent optional value. Other non-string selections must
    // not silently become the explicit empty-selection passthrough route.
    for (const char* key : {"mode", "engine", "modelId", "id", "modelPath", "rvcModelDir"}) {
        const char* value = vc_json_value_start(options.raw_json.c_str(), key);
        if (value && *value != '"' && std::strncmp(value, "null", 4) != 0) return true;
    }
    const std::string mode = normalize_engine_name(vc_json_get_string(options.raw_json.c_str(), "mode", ""));
    const std::string engine = normalize_engine_name(options.engine);
    // An empty model selection is the existing DSP passthrough. Raw bypasses
    // optional DSP/ASR, not the conversion model itself.
    return mode == "rvc_raw" || !options.model_id.empty() || !options.model_path.empty() ||
        (!engine.empty() && engine != "passthrough");
}

struct NcnnRuntimeProbe {
    bool ok = false;
    std::string path;
    std::string version;
    std::string error;

#ifdef _WIN32
    HMODULE handle = nullptr;
#else
    void* handle = nullptr;
#endif

    ~NcnnRuntimeProbe() {
#ifdef _WIN32
        if (handle) FreeLibrary(handle);
#else
        if (handle) dlclose(handle);
#endif
    }

    static std::vector<std::string> candidate_paths() {
        std::vector<std::string> paths;
        if (const char* env = std::getenv("MOBMATE_NCNN_DIR")) {
            std::string base = env;
            if (!base.empty() && base.back() != '/' && base.back() != '\\') base += '/';
#ifdef _WIN32
            paths.push_back(base + "ncnn.dll");
#else
            paths.push_back(base + "libncnn.so");
#endif
        }

        std::error_code ec;
        auto cwd = std::filesystem::current_path(ec);
        if (!ec) {
#ifdef _WIN32
            paths.push_back((cwd / "libs" / "ncnn" / "win-x64" / "ncnn.dll").string());
            paths.push_back((cwd / "libs" / "ncnn" / "ncnn.dll").string());
            paths.push_back("ncnn.dll");
#else
            paths.push_back((cwd / "libs" / "ncnn" / "linux-x64" / "libncnn.so").string());
            paths.push_back((cwd / "libs" / "ncnn" / "libncnn.so").string());
            paths.push_back("libncnn.so");
#endif
        }
        return paths;
    }

    bool load() {
        using VersionFn = const char* (*)();
        for (const auto& p : candidate_paths()) {
#ifdef _WIN32
            const auto wide = vc_utf8_to_wide(p);
            HMODULE h = wide.empty() ? nullptr : LoadLibraryW(wide.c_str());
#else
            void* h = dlopen(p.c_str(), RTLD_LAZY);
#endif
            if (!h) continue;
#ifdef _WIN32
            auto version_fn = reinterpret_cast<VersionFn>(GetProcAddress(h, "ncnn_version"));
#else
            auto version_fn = reinterpret_cast<VersionFn>(dlsym(h, "ncnn_version"));
#endif
            if (!version_fn) {
#ifdef _WIN32
                FreeLibrary(h);
#else
                dlclose(h);
#endif
                continue;
            }
            handle = h;
            path = p;
            const char* v = version_fn();
            version = v ? v : "unknown";
            ok = true;
            return true;
        }
        error = "ncnn runtime not found; set MOBMATE_NCNN_DIR or place runtime under libs/ncnn/{win-x64,linux-x64}";
        return false;
    }
};

struct NcnnApi {
    using ncnn_net_t = void*;
    using ncnn_option_t = void*;
    using ncnn_paramdict_t = void*;
    using ncnn_modelbin_t = void*;
    using ncnn_mat_t = void*;
    using ncnn_extractor_t = void*;

    struct NcnnLayerC;
    using ncnn_layer_t = NcnnLayerC*;
    using ncnn_layer_creator_t = ncnn_layer_t (*)(void* userdata);
    using ncnn_layer_destroyer_t = void (*)(ncnn_layer_t layer, void* userdata);

    struct NcnnLayerC {
        void* pthis;
        int (*load_param)(ncnn_layer_t layer, ncnn_paramdict_t pd);
        int (*load_model)(ncnn_layer_t layer, ncnn_modelbin_t mb);
        int (*create_pipeline)(ncnn_layer_t layer, ncnn_option_t opt);
        int (*destroy_pipeline)(ncnn_layer_t layer, ncnn_option_t opt);
        int (*forward_1)(ncnn_layer_t layer, ncnn_mat_t bottom_blob, ncnn_mat_t* top_blob, ncnn_option_t opt);
        int (*forward_n)(ncnn_layer_t layer, const ncnn_mat_t* bottom_blobs, int n, ncnn_mat_t* top_blobs, int n2, ncnn_option_t opt);
        int (*forward_inplace_1)(ncnn_layer_t layer, ncnn_mat_t bottom_top_blob, ncnn_option_t opt);
        int (*forward_inplace_n)(ncnn_layer_t layer, ncnn_mat_t* bottom_top_blobs, int n, ncnn_option_t opt);
    };

    bool loaded = false;
    std::string path;
    std::string version;
    std::string error;
#ifdef _WIN32
    HMODULE handle = nullptr;
#else
    void* handle = nullptr;
#endif

    const char* (*ncnn_version)() = nullptr;
    ncnn_net_t (*ncnn_net_create)() = nullptr;
    void (*ncnn_net_destroy)(ncnn_net_t) = nullptr;
    int (*ncnn_net_load_param)(ncnn_net_t, const char*) = nullptr;
    int (*ncnn_net_load_model)(ncnn_net_t, const char*) = nullptr;
    void (*ncnn_net_set_vulkan_device)(ncnn_net_t, int) = nullptr;
    ncnn_option_t (*ncnn_net_get_option)(ncnn_net_t) = nullptr;
    void (*ncnn_option_set_use_vulkan_compute)(ncnn_option_t, int) = nullptr;
    ncnn_layer_t (*ncnn_layer_create)() = nullptr;
    void (*ncnn_layer_destroy)(ncnn_layer_t) = nullptr;
    void (*ncnn_net_register_custom_layer_by_type)(ncnn_net_t, const char*, ncnn_layer_creator_t, ncnn_layer_destroyer_t, void*) = nullptr;
    ncnn_extractor_t (*ncnn_extractor_create)(ncnn_net_t) = nullptr;
    void (*ncnn_extractor_destroy)(ncnn_extractor_t) = nullptr;
    int (*ncnn_extractor_input)(ncnn_extractor_t, const char*, ncnn_mat_t) = nullptr;
    int (*ncnn_extractor_extract)(ncnn_extractor_t, const char*, ncnn_mat_t*) = nullptr;
    ncnn_mat_t (*ncnn_mat_create_1d_elem)(int, size_t, int, void*) = nullptr;
    ncnn_mat_t (*ncnn_mat_create_2d_elem)(int, int, size_t, int, void*) = nullptr;
    ncnn_mat_t (*ncnn_mat_create_3d_elem)(int, int, int, size_t, int, void*) = nullptr;
    ncnn_mat_t (*ncnn_mat_clone)(ncnn_mat_t, void*) = nullptr;
    void (*ncnn_mat_destroy)(ncnn_mat_t) = nullptr;
    void* (*ncnn_mat_get_data)(ncnn_mat_t) = nullptr;
    int (*ncnn_mat_get_dims)(ncnn_mat_t) = nullptr;
    int (*ncnn_mat_get_w)(ncnn_mat_t) = nullptr;
    int (*ncnn_mat_get_h)(ncnn_mat_t) = nullptr;
    int (*ncnn_mat_get_c)(ncnn_mat_t) = nullptr;
    size_t (*ncnn_mat_get_elemsize)(ncnn_mat_t) = nullptr;
    int (*ncnn_paramdict_get_int)(ncnn_paramdict_t, int, int) = nullptr;

    template<typename T>
    bool bind(T& out, const char* name) {
#ifdef _WIN32
        out = reinterpret_cast<T>(GetProcAddress(handle, name));
#else
        out = reinterpret_cast<T>(dlsym(handle, name));
#endif
        if (!out) { error = std::string("ncnn symbol not found: ") + name; return false; }
        return true;
    }

    bool load() {
        if (loaded) return true;
        for (const auto& p : NcnnRuntimeProbe::candidate_paths()) {
#ifdef _WIN32
            const auto wide = vc_utf8_to_wide(p);
            HMODULE h = wide.empty() ? nullptr : LoadLibraryW(wide.c_str());
#else
            void* h = dlopen(p.c_str(), RTLD_LAZY);
#endif
            if (!h) continue;
            handle = h;
            path = p;
            if (!bind(ncnn_version, "ncnn_version") ||
                !bind(ncnn_net_create, "ncnn_net_create") ||
                !bind(ncnn_net_destroy, "ncnn_net_destroy") ||
                !bind(ncnn_net_load_param, "ncnn_net_load_param") ||
                !bind(ncnn_net_load_model, "ncnn_net_load_model")) {
#ifdef _WIN32
                FreeLibrary(handle);
#else
                dlclose(handle);
#endif
                handle = nullptr;
                continue;
            }
            // Vulkan symbols are expected in the approved ncnn runtime. If absent, CPU can still load models.
#ifdef _WIN32
            ncnn_net_set_vulkan_device = reinterpret_cast<decltype(ncnn_net_set_vulkan_device)>(GetProcAddress(handle, "ncnn_net_set_vulkan_device"));
            ncnn_net_get_option = reinterpret_cast<decltype(ncnn_net_get_option)>(GetProcAddress(handle, "ncnn_net_get_option"));
            ncnn_option_set_use_vulkan_compute = reinterpret_cast<decltype(ncnn_option_set_use_vulkan_compute)>(GetProcAddress(handle, "ncnn_option_set_use_vulkan_compute"));
            ncnn_layer_create = reinterpret_cast<decltype(ncnn_layer_create)>(GetProcAddress(handle, "ncnn_layer_create"));
            ncnn_layer_destroy = reinterpret_cast<decltype(ncnn_layer_destroy)>(GetProcAddress(handle, "ncnn_layer_destroy"));
            ncnn_net_register_custom_layer_by_type = reinterpret_cast<decltype(ncnn_net_register_custom_layer_by_type)>(GetProcAddress(handle, "ncnn_net_register_custom_layer_by_type"));
            ncnn_extractor_create = reinterpret_cast<decltype(ncnn_extractor_create)>(GetProcAddress(handle, "ncnn_extractor_create"));
            ncnn_extractor_destroy = reinterpret_cast<decltype(ncnn_extractor_destroy)>(GetProcAddress(handle, "ncnn_extractor_destroy"));
            ncnn_extractor_input = reinterpret_cast<decltype(ncnn_extractor_input)>(GetProcAddress(handle, "ncnn_extractor_input"));
            ncnn_extractor_extract = reinterpret_cast<decltype(ncnn_extractor_extract)>(GetProcAddress(handle, "ncnn_extractor_extract"));
            ncnn_mat_create_1d_elem = reinterpret_cast<decltype(ncnn_mat_create_1d_elem)>(GetProcAddress(handle, "ncnn_mat_create_1d_elem"));
            ncnn_mat_create_2d_elem = reinterpret_cast<decltype(ncnn_mat_create_2d_elem)>(GetProcAddress(handle, "ncnn_mat_create_2d_elem"));
            ncnn_mat_create_3d_elem = reinterpret_cast<decltype(ncnn_mat_create_3d_elem)>(GetProcAddress(handle, "ncnn_mat_create_3d_elem"));
            ncnn_mat_clone = reinterpret_cast<decltype(ncnn_mat_clone)>(GetProcAddress(handle, "ncnn_mat_clone"));
            ncnn_mat_destroy = reinterpret_cast<decltype(ncnn_mat_destroy)>(GetProcAddress(handle, "ncnn_mat_destroy"));
            ncnn_mat_get_data = reinterpret_cast<decltype(ncnn_mat_get_data)>(GetProcAddress(handle, "ncnn_mat_get_data"));
            ncnn_mat_get_dims = reinterpret_cast<decltype(ncnn_mat_get_dims)>(GetProcAddress(handle, "ncnn_mat_get_dims"));
            ncnn_mat_get_w = reinterpret_cast<decltype(ncnn_mat_get_w)>(GetProcAddress(handle, "ncnn_mat_get_w"));
            ncnn_mat_get_h = reinterpret_cast<decltype(ncnn_mat_get_h)>(GetProcAddress(handle, "ncnn_mat_get_h"));
            ncnn_mat_get_c = reinterpret_cast<decltype(ncnn_mat_get_c)>(GetProcAddress(handle, "ncnn_mat_get_c"));
            ncnn_mat_get_elemsize = reinterpret_cast<decltype(ncnn_mat_get_elemsize)>(GetProcAddress(handle, "ncnn_mat_get_elemsize"));
            ncnn_paramdict_get_int = reinterpret_cast<decltype(ncnn_paramdict_get_int)>(GetProcAddress(handle, "ncnn_paramdict_get_int"));
#else
            ncnn_net_set_vulkan_device = reinterpret_cast<decltype(ncnn_net_set_vulkan_device)>(dlsym(handle, "ncnn_net_set_vulkan_device"));
            ncnn_net_get_option = reinterpret_cast<decltype(ncnn_net_get_option)>(dlsym(handle, "ncnn_net_get_option"));
            ncnn_option_set_use_vulkan_compute = reinterpret_cast<decltype(ncnn_option_set_use_vulkan_compute)>(dlsym(handle, "ncnn_option_set_use_vulkan_compute"));
            ncnn_layer_create = reinterpret_cast<decltype(ncnn_layer_create)>(dlsym(handle, "ncnn_layer_create"));
            ncnn_layer_destroy = reinterpret_cast<decltype(ncnn_layer_destroy)>(dlsym(handle, "ncnn_layer_destroy"));
            ncnn_net_register_custom_layer_by_type = reinterpret_cast<decltype(ncnn_net_register_custom_layer_by_type)>(dlsym(handle, "ncnn_net_register_custom_layer_by_type"));
            ncnn_extractor_create = reinterpret_cast<decltype(ncnn_extractor_create)>(dlsym(handle, "ncnn_extractor_create"));
            ncnn_extractor_destroy = reinterpret_cast<decltype(ncnn_extractor_destroy)>(dlsym(handle, "ncnn_extractor_destroy"));
            ncnn_extractor_input = reinterpret_cast<decltype(ncnn_extractor_input)>(dlsym(handle, "ncnn_extractor_input"));
            ncnn_extractor_extract = reinterpret_cast<decltype(ncnn_extractor_extract)>(dlsym(handle, "ncnn_extractor_extract"));
            ncnn_mat_create_1d_elem = reinterpret_cast<decltype(ncnn_mat_create_1d_elem)>(dlsym(handle, "ncnn_mat_create_1d_elem"));
            ncnn_mat_create_2d_elem = reinterpret_cast<decltype(ncnn_mat_create_2d_elem)>(dlsym(handle, "ncnn_mat_create_2d_elem"));
            ncnn_mat_create_3d_elem = reinterpret_cast<decltype(ncnn_mat_create_3d_elem)>(dlsym(handle, "ncnn_mat_create_3d_elem"));
            ncnn_mat_clone = reinterpret_cast<decltype(ncnn_mat_clone)>(dlsym(handle, "ncnn_mat_clone"));
            ncnn_mat_destroy = reinterpret_cast<decltype(ncnn_mat_destroy)>(dlsym(handle, "ncnn_mat_destroy"));
            ncnn_mat_get_data = reinterpret_cast<decltype(ncnn_mat_get_data)>(dlsym(handle, "ncnn_mat_get_data"));
            ncnn_mat_get_dims = reinterpret_cast<decltype(ncnn_mat_get_dims)>(dlsym(handle, "ncnn_mat_get_dims"));
            ncnn_mat_get_w = reinterpret_cast<decltype(ncnn_mat_get_w)>(dlsym(handle, "ncnn_mat_get_w"));
            ncnn_mat_get_h = reinterpret_cast<decltype(ncnn_mat_get_h)>(dlsym(handle, "ncnn_mat_get_h"));
            ncnn_mat_get_c = reinterpret_cast<decltype(ncnn_mat_get_c)>(dlsym(handle, "ncnn_mat_get_c"));
            ncnn_mat_get_elemsize = reinterpret_cast<decltype(ncnn_mat_get_elemsize)>(dlsym(handle, "ncnn_mat_get_elemsize"));
            ncnn_paramdict_get_int = reinterpret_cast<decltype(ncnn_paramdict_get_int)>(dlsym(handle, "ncnn_paramdict_get_int"));
#endif
            const char* v = ncnn_version();
            version = v ? v : "unknown";
            loaded = true;
            return true;
        }
        error = "ncnn runtime not found; set MOBMATE_NCNN_DIR or place runtime under libs/ncnn/{win-x64,linux-x64}";
        return false;
    }
};

static NcnnApi& ncnn_api() {
    static NcnnApi instance;
    return instance;
}

static std::atomic<int> g_seedvc_ncnn_custom_layer_active{0};

static int ncnn_pnnx_layer_forward_unimplemented_1(NcnnApi::ncnn_layer_t, NcnnApi::ncnn_mat_t, NcnnApi::ncnn_mat_t*, NcnnApi::ncnn_option_t) {
    return -1001;
}

static int ncnn_pnnx_layer_forward_unimplemented_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t*, int, NcnnApi::ncnn_mat_t*, int, NcnnApi::ncnn_option_t) {
    return -1001;
}

// forward_1 wrapper for identity custom layers (clone only, no helpers needed)
static int ncnn_pnnx_identity_forward_1(NcnnApi::ncnn_layer_t, NcnnApi::ncnn_mat_t bottom_blob, NcnnApi::ncnn_mat_t* top_blob, NcnnApi::ncnn_option_t) {
    auto& api = ncnn_api();
    if (!bottom_blob || !top_blob || !api.ncnn_mat_clone) return -1001;
    void* out = api.ncnn_mat_clone(bottom_blob, nullptr);
    if (!out) return -1001;
    *top_blob = out;
    return 0;
}

static int ncnn_pnnx_expression_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t*, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    if (n != 0 || !top_blobs || n2 <= 0) return -1001;
    auto& api = ncnn_api();
    if (!api.ncnn_mat_create_1d_elem || !api.ncnn_mat_get_data) return -1001;
    void* out = api.ncnn_mat_create_1d_elem(1, sizeof(float), 1, nullptr);
    if (!out) return -1001;
    float* data = static_cast<float*>(api.ncnn_mat_get_data(out));
    if (data) data[0] = 0.0f;
    top_blobs[0] = out;
    return 0;
}

static int ncnn_pnnx_identity_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    auto& api = ncnn_api();
    if (!bottom_blobs || n <= 0 || !top_blobs || n2 <= 0 || !api.ncnn_mat_clone) return -1001;
    void* out = api.ncnn_mat_clone(bottom_blobs[0], nullptr);
    if (!out) return -1001;
    top_blobs[0] = out;
    return 0;
}

static int64_t ncnn_read_scalar_i64(NcnnApi::ncnn_mat_t mat) {
    auto& api = ncnn_api();
    if (!mat || !api.ncnn_mat_get_data || !api.ncnn_mat_get_elemsize) return 0;
    void* data = api.ncnn_mat_get_data(mat);
    size_t es = api.ncnn_mat_get_elemsize(mat);
    if (!data) return 0;
    if (es == sizeof(int64_t)) return *static_cast<int64_t*>(data);
    if (es == sizeof(int)) return *static_cast<int*>(data);
    return static_cast<int64_t>(*static_cast<float*>(data));
}

static float ncnn_read_scalar_f32(NcnnApi::ncnn_mat_t mat) {
    auto& api = ncnn_api();
    if (!mat || !api.ncnn_mat_get_data || !api.ncnn_mat_get_elemsize) return 0.0f;
    void* data = api.ncnn_mat_get_data(mat);
    size_t es = api.ncnn_mat_get_elemsize(mat);
    if (!data) return 0.0f;
    if (es == sizeof(int64_t)) return static_cast<float>(*static_cast<int64_t*>(data));
    if (es == sizeof(int)) return static_cast<float>(*static_cast<int*>(data));
    return *static_cast<float*>(data);
}

static int ncnn_torch_arange_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(1, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 1 || !top_blobs || n2 < 1 || !api.ncnn_mat_create_1d_elem || !api.ncnn_mat_get_data) return -1001;
    int64_t end = ncnn_read_scalar_i64(bottom_blobs[0]);
    if (end <= 0 || end > 4096) return -1001;
    void* out = api.ncnn_mat_create_1d_elem(static_cast<int>(end), sizeof(int64_t), 1, nullptr);
    if (!out) return -1001;
    int64_t* data = static_cast<int64_t*>(api.ncnn_mat_get_data(out));
    if (!data) { api.ncnn_mat_destroy(out); return -1001; }
    for (int64_t i = 0; i < end; ++i) data[i] = i;
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static int ncnn_torch_lt_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(2, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 2 || !top_blobs || n2 < 1 || !api.ncnn_mat_create_2d_elem || !api.ncnn_mat_create_1d_elem ||
        !api.ncnn_mat_get_data || !api.ncnn_mat_get_dims || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h) return -1001;
    int dims = api.ncnn_mat_get_dims(bottom_blobs[0]);
    int w = api.ncnn_mat_get_w(bottom_blobs[0]);
    int h = dims >= 2 ? api.ncnn_mat_get_h(bottom_blobs[0]) : 1;
    if (w <= 0 || h <= 0) return -1001;
    void* out = (dims >= 2) ? api.ncnn_mat_create_2d_elem(w, h, sizeof(float), 1, nullptr)
                            : api.ncnn_mat_create_1d_elem(w, sizeof(float), 1, nullptr);
    if (!out) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    if (!od) { api.ncnn_mat_destroy(out); return -1001; }
    const float rhs_scalar = ncnn_read_scalar_f32(bottom_blobs[1]);
    void* lhs_data = api.ncnn_mat_get_data(bottom_blobs[0]);
    size_t lhs_es = api.ncnn_mat_get_elemsize ? api.ncnn_mat_get_elemsize(bottom_blobs[0]) : sizeof(float);
    for (int i = 0; i < w * h; ++i) {
        float lhs = 0.0f;
        if (lhs_es == sizeof(int64_t)) lhs = static_cast<float>(static_cast<int64_t*>(lhs_data)[i]);
        else if (lhs_es == sizeof(int)) lhs = static_cast<float>(static_cast<int*>(lhs_data)[i]);
        else lhs = static_cast<float*>(lhs_data)[i];
        od[i] = lhs < rhs_scalar ? 1.0f : 0.0f;
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static NcnnApi::ncnn_mat_t ncnn_create_like_f32(NcnnApi::ncnn_mat_t ref) {
    auto& api = ncnn_api();
    if (!ref || !api.ncnn_mat_get_dims || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h || !api.ncnn_mat_create_1d_elem || !api.ncnn_mat_create_2d_elem) return nullptr;
    int dims = api.ncnn_mat_get_dims(ref);
    int w = api.ncnn_mat_get_w(ref);
    int h = dims >= 2 ? api.ncnn_mat_get_h(ref) : 1;
    int c = (dims >= 3 && api.ncnn_mat_get_c) ? api.ncnn_mat_get_c(ref) : 1;
    if (dims >= 3 && api.ncnn_mat_create_3d_elem) return api.ncnn_mat_create_3d_elem(w, h, c, sizeof(float), 1, nullptr);
    if (dims >= 2) return api.ncnn_mat_create_2d_elem(w, h, sizeof(float), 1, nullptr);
    return api.ncnn_mat_create_1d_elem(w, sizeof(float), 1, nullptr);
}

static int ncnn_mat_count(NcnnApi::ncnn_mat_t mat) {
    auto& api = ncnn_api();
    if (!mat || !api.ncnn_mat_get_dims || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h) return 0;
    int dims = api.ncnn_mat_get_dims(mat);
    int w = api.ncnn_mat_get_w(mat);
    int h = dims >= 2 ? api.ncnn_mat_get_h(mat) : 1;
    int c = (dims >= 3 && api.ncnn_mat_get_c) ? api.ncnn_mat_get_c(mat) : 1;
    return w * h * c;
}

static int ncnn_torch_logical_not_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(3, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 1 || !top_blobs || n2 < 1 || !api.ncnn_mat_get_data) return -1001;
    int count = ncnn_mat_count(bottom_blobs[0]);
    void* out = ncnn_create_like_f32(bottom_blobs[0]);
    if (!out || count <= 0) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    void* id = api.ncnn_mat_get_data(bottom_blobs[0]);
    size_t es = api.ncnn_mat_get_elemsize ? api.ncnn_mat_get_elemsize(bottom_blobs[0]) : sizeof(float);
    if (!od || !id) { api.ncnn_mat_destroy(out); return -1001; }
    for (int i = 0; i < count; ++i) {
        float v = es == sizeof(int64_t) ? static_cast<float>(static_cast<int64_t*>(id)[i]) : (es == sizeof(int) ? static_cast<float>(static_cast<int*>(id)[i]) : static_cast<float*>(id)[i]);
        od[i] = v == 0.0f ? 1.0f : 0.0f;
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static int ncnn_torch_logical_not_forward_1(NcnnApi::ncnn_layer_t, NcnnApi::ncnn_mat_t bottom_blob, NcnnApi::ncnn_mat_t* top_blob, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(3, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blob || !top_blob || !api.ncnn_mat_get_data) return -1001;
    int count = ncnn_mat_count(bottom_blob);
    void* out = ncnn_create_like_f32(bottom_blob);
    if (!out || count <= 0) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    void* id = api.ncnn_mat_get_data(bottom_blob);
    size_t es = api.ncnn_mat_get_elemsize ? api.ncnn_mat_get_elemsize(bottom_blob) : sizeof(float);
    if (!od || !id) { api.ncnn_mat_destroy(out); return -1001; }
    for (int i = 0; i < count; ++i) {
        float v = es == sizeof(int64_t) ? static_cast<float>(static_cast<int64_t*>(id)[i]) : (es == sizeof(int) ? static_cast<float>(static_cast<int*>(id)[i]) : static_cast<float*>(id)[i]);
        od[i] = v == 0.0f ? 1.0f : 0.0f;
    }
    *top_blob = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static int ncnn_torch_where_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(4, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 2 || !top_blobs || n2 < 1 || !api.ncnn_mat_get_data) return -1001;
    int val_count = ncnn_mat_count(bottom_blobs[1]);
    int cond_count = ncnn_mat_count(bottom_blobs[0]);
    void* out = ncnn_create_like_f32(bottom_blobs[1]);
    if (!out || val_count <= 0 || cond_count <= 0) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    void* cd = api.ncnn_mat_get_data(bottom_blobs[0]);
    void* vd = api.ncnn_mat_get_data(bottom_blobs[1]);
    size_t ces = api.ncnn_mat_get_elemsize ? api.ncnn_mat_get_elemsize(bottom_blobs[0]) : sizeof(float);
    size_t ves = api.ncnn_mat_get_elemsize ? api.ncnn_mat_get_elemsize(bottom_blobs[1]) : sizeof(float);
    if (!od || !cd || !vd) { api.ncnn_mat_destroy(out); return -1001; }
    for (int i = 0; i < val_count; ++i) {
        int ci = cond_count == 1 ? 0 : (i % cond_count);
        float cond = ces == sizeof(int64_t) ? static_cast<float>(static_cast<int64_t*>(cd)[ci]) : (ces == sizeof(int) ? static_cast<float>(static_cast<int*>(cd)[ci]) : static_cast<float*>(cd)[ci]);
        float val = ves == sizeof(int64_t) ? static_cast<float>(static_cast<int64_t*>(vd)[i]) : (ves == sizeof(int) ? static_cast<float>(static_cast<int*>(vd)[i]) : static_cast<float*>(vd)[i]);
        od[i] = cond != 0.0f ? -std::numeric_limits<float>::infinity() : val;
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

// ── MatMul custom layer ─────────────────────────────────────────────
// Per-instance transB flag stored via load_param callback.
static std::unordered_map<void*, int> g_matmul_transb;

static int ncnn_matmul_load_param(NcnnApi::ncnn_layer_t layer, NcnnApi::ncnn_paramdict_t pd) {
    auto& api = ncnn_api();
    int transb = (api.ncnn_paramdict_get_int) ? api.ncnn_paramdict_get_int(pd, 0, 0) : 0;
    g_matmul_transb[static_cast<void*>(layer)] = transb;
    return 0;
}

static int ncnn_matmul_forward_n(NcnnApi::ncnn_layer_t layer, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(5, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 2 || !top_blobs || n2 < 1 || !api.ncnn_mat_get_data || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h || !api.ncnn_mat_get_dims) return -1001;

    int transb = 0;
    auto it = g_matmul_transb.find(static_cast<void*>(layer));
    if (it != g_matmul_transb.end()) transb = it->second;

    // A: ncnn 2D mat w_a x h_a → matrix [h_a, w_a]  (rows=h_a, cols=w_a)
    // B: ncnn 2D mat w_b x h_b → matrix [h_b, w_b]
    int dims_a = api.ncnn_mat_get_dims(bottom_blobs[0]);
    int dims_b = api.ncnn_mat_get_dims(bottom_blobs[1]);
    int w_a = api.ncnn_mat_get_w(bottom_blobs[0]);
    int h_a = (dims_a >= 2) ? api.ncnn_mat_get_h(bottom_blobs[0]) : 1;
    int w_b = api.ncnn_mat_get_w(bottom_blobs[1]);
    int h_b = (dims_b >= 2) ? api.ncnn_mat_get_h(bottom_blobs[1]) : 1;

    // Matrix dims: A=[M,K], B=[K,N] or B=[N,K] if transB
    int M = h_a;
    int K_a = w_a;
    int K_b, N;
    if (transb) {
        // B^T: matrix [h_b, w_b] transposed → [w_b, h_b], so K=h_b, N=w_b
        K_b = h_b;
        N = w_b;
    } else {
        // B: matrix [h_b, w_b], so K=h_b, N=w_b
        K_b = h_b;
        N = w_b;
    }
    if (K_a != K_b || M <= 0 || N <= 0 || K_a <= 0) {
        g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
        return -1001;
    }

    void* out = (M > 1) ? api.ncnn_mat_create_2d_elem(N, M, sizeof(float), 1, nullptr)
                        : api.ncnn_mat_create_1d_elem(N, sizeof(float), 1, nullptr);
    if (!out) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    const float* ad = static_cast<const float*>(api.ncnn_mat_get_data(bottom_blobs[0]));
    const float* bd = static_cast<const float*>(api.ncnn_mat_get_data(bottom_blobs[1]));
    if (!od || !ad || !bd) { api.ncnn_mat_destroy(out); return -1001; }

    // C[i,j] = sum_k A[i,k] * B[k,j]  (or B^T[j,k] if transb)
    for (int i = 0; i < M; ++i) {
        for (int j = 0; j < N; ++j) {
            float sum = 0.0f;
            if (transb) {
                // A row-major [M,K]: A[i,k] = ad[i*K+k]
                // B row-major [N,K] (transposed): B[j,k] = bd[j*K+k]
                for (int k = 0; k < K_a; ++k)
                    sum += ad[i * K_a + k] * bd[j * K_a + k];
            } else {
                // A row-major [M,K]: A[i,k] = ad[i*K+k]
                // B row-major [K,N]: B[k,j] = bd[k*N+j]
                for (int k = 0; k < K_a; ++k)
                    sum += ad[i * K_a + k] * bd[k * N + j];
            }
            od[i * N + j] = sum;
        }
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

// ── Custom CatW layer: concatenate N inputs along w dimension (2D mats, same h) ──
static int ncnn_catw_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(6, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 1 || !top_blobs || n2 < 1 || !api.ncnn_mat_get_data || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h || !api.ncnn_mat_get_dims) return -1001;

    // All inputs must be 2D with same h
    int h = api.ncnn_mat_get_h(bottom_blobs[0]);
    int total_w = 0;
    for (int i = 0; i < n; ++i) {
        int d = api.ncnn_mat_get_dims(bottom_blobs[i]);
        int hi = (d >= 2) ? api.ncnn_mat_get_h(bottom_blobs[i]) : 1;
        if (hi != h || d < 2) { g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }
        total_w += api.ncnn_mat_get_w(bottom_blobs[i]);
    }
    if (total_w <= 0 || h <= 0) { g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }

    void* out = api.ncnn_mat_create_2d_elem(total_w, h, sizeof(float), 1, nullptr);
    if (!out) return -1001;
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    if (!od) { api.ncnn_mat_destroy(out); return -1001; }

    // Copy row-by-row: for each row r, concatenate w elements from each input
    for (int r = 0; r < h; ++r) {
        int col = 0;
        for (int i = 0; i < n; ++i) {
            int wi = api.ncnn_mat_get_w(bottom_blobs[i]);
            const float* src = static_cast<const float*>(api.ncnn_mat_get_data(bottom_blobs[i]));
            std::memcpy(od + r * total_w + col, src + r * wi, wi * sizeof(float));
            col += wi;
        }
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static NcnnApi::ncnn_layer_t ncnn_create_catw_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_catw_forward_n;
    return layer;
}

// ── Custom CatT layer: concatenate timestep-like inputs into 3D channels ──
static int ncnn_catt_forward_n(NcnnApi::ncnn_layer_t, const NcnnApi::ncnn_mat_t* bottom_blobs, int n, NcnnApi::ncnn_mat_t* top_blobs, int n2, NcnnApi::ncnn_option_t) {
    g_seedvc_ncnn_custom_layer_active.store(7, std::memory_order_relaxed);
    auto& api = ncnn_api();
    if (!bottom_blobs || n < 1 || !top_blobs || n2 < 1 || !api.ncnn_mat_get_data || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h || !api.ncnn_mat_get_c || !api.ncnn_mat_get_dims || !api.ncnn_mat_create_3d_elem) return -1001;

    const int w = api.ncnn_mat_get_w(bottom_blobs[0]);
    int total_c = 0;
    for (int i = 0; i < n; ++i) {
        const int dims = api.ncnn_mat_get_dims(bottom_blobs[i]);
        const int wi = api.ncnn_mat_get_w(bottom_blobs[i]);
        if (wi != w || dims < 2 || dims > 3) { g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }
        if (dims == 2) {
            total_c += api.ncnn_mat_get_h(bottom_blobs[i]);
        } else {
            if (api.ncnn_mat_get_h(bottom_blobs[i]) != 1) { g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }
            total_c += api.ncnn_mat_get_c(bottom_blobs[i]);
        }
    }
    void* out = api.ncnn_mat_create_3d_elem(w, 1, total_c, sizeof(float), 1, nullptr);
    if (!out) { g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }
    float* od = static_cast<float*>(api.ncnn_mat_get_data(out));
    if (!od) { api.ncnn_mat_destroy(out); g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }

    int dst_c = 0;
    for (int i = 0; i < n; ++i) {
        const int dims = api.ncnn_mat_get_dims(bottom_blobs[i]);
        const int count = (dims == 2) ? api.ncnn_mat_get_h(bottom_blobs[i]) : api.ncnn_mat_get_c(bottom_blobs[i]);
        const float* src = static_cast<const float*>(api.ncnn_mat_get_data(bottom_blobs[i]));
        if (!src) { api.ncnn_mat_destroy(out); g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed); return -1001; }
        for (int c = 0; c < count; ++c) {
            std::memcpy(od + static_cast<size_t>(dst_c + c) * w, src + static_cast<size_t>(c) * w, static_cast<size_t>(w) * sizeof(float));
        }
        dst_c += count;
    }
    top_blobs[0] = out;
    g_seedvc_ncnn_custom_layer_active.store(0, std::memory_order_relaxed);
    return 0;
}

static NcnnApi::ncnn_layer_t ncnn_create_catt_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_catt_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_matmul_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->load_param = ncnn_matmul_load_param;
    layer->forward_n = ncnn_matmul_forward_n;
    return layer;
}

static void ncnn_destroy_matmul_layer(NcnnApi::ncnn_layer_t layer, void*) {
    g_matmul_transb.erase(static_cast<void*>(layer));
    auto& api = ncnn_api();
    if (api.ncnn_layer_destroy && layer) api.ncnn_layer_destroy(layer);
}

static NcnnApi::ncnn_layer_t ncnn_create_pnnx_load_placeholder(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    // Load-only placeholder. pnnx.Expression / pnnx.Attribute need real CPU kernels before inference.
    layer->forward_1 = ncnn_pnnx_layer_forward_unimplemented_1;
    layer->forward_n = ncnn_pnnx_layer_forward_unimplemented_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_pnnx_expression_placeholder(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_pnnx_expression_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_pnnx_identity_placeholder(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_1 = ncnn_pnnx_identity_forward_1;
    layer->forward_n = ncnn_pnnx_identity_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_torch_arange_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_torch_arange_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_torch_lt_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_torch_lt_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_torch_logical_not_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_1 = ncnn_torch_logical_not_forward_1;
    layer->forward_n = ncnn_torch_logical_not_forward_n;
    return layer;
}

static NcnnApi::ncnn_layer_t ncnn_create_torch_where_layer(void*) {
    auto& api = ncnn_api();
    if (!api.ncnn_layer_create) return nullptr;
    auto* layer = api.ncnn_layer_create();
    if (!layer) return nullptr;
    layer->forward_n = ncnn_torch_where_forward_n;
    return layer;
}

static void ncnn_destroy_pnnx_load_placeholder(NcnnApi::ncnn_layer_t layer, void*) {
    auto& api = ncnn_api();
    if (api.ncnn_layer_destroy && layer) api.ncnn_layer_destroy(layer);
}

static void ncnn_register_seedvc_pnnx_layers(NcnnApi::ncnn_net_t net) {
    auto& api = ncnn_api();
    if (!net || !api.ncnn_net_register_custom_layer_by_type || !api.ncnn_layer_create || !api.ncnn_layer_destroy) return;
    api.ncnn_net_register_custom_layer_by_type(net, "pnnx.Expression", ncnn_create_pnnx_expression_placeholder, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "aten::to", ncnn_create_pnnx_identity_placeholder, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "torch.arange", ncnn_create_torch_arange_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "torch.lt", ncnn_create_torch_lt_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "torch.logical_not", ncnn_create_torch_logical_not_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "torch.where", ncnn_create_torch_where_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "torch.tile", ncnn_create_pnnx_identity_placeholder, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "CatW", ncnn_create_catw_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    api.ncnn_net_register_custom_layer_by_type(net, "CatT", ncnn_create_catt_layer, ncnn_destroy_pnnx_load_placeholder, nullptr);
    const char* load_only_layers[] = {
        "pnnx.Attribute",
    };
    for (const char* type : load_only_layers) {
        api.ncnn_net_register_custom_layer_by_type(net, type, ncnn_create_pnnx_load_placeholder, ncnn_destroy_pnnx_load_placeholder, nullptr);
    }
}

static std::string error_json(const std::string& message) {
    return "{\"ok\":false,\"mode\":\"voice_conversion\",\"error\":\"" + vc_json_escape(message) + "\"}";
}

// ── Lock-free SPSC ring buffer ──────────────────────────────────────

struct VcRingBuffer {
    std::vector<int16_t> data;
    std::atomic<size_t> write_pos{0};
    std::atomic<size_t> read_pos{0};
    size_t cap = 0;

    void init(size_t capacity) {
        cap = capacity;
        data.assign(capacity, 0);
        write_pos.store(0, std::memory_order_relaxed);
        read_pos.store(0, std::memory_order_relaxed);
    }

    size_t available() const {
        size_t w = write_pos.load(std::memory_order_acquire);
        size_t r = read_pos.load(std::memory_order_acquire);
        return (w >= r) ? (w - r) : (cap - r + w);
    }

    size_t free_space() const {
        return cap - 1 - available();
    }

    void clear() {
        size_t w = write_pos.load(std::memory_order_acquire);
        read_pos.store(w, std::memory_order_release);
    }

    size_t write(const int16_t* src, size_t count) {
        size_t space = free_space();
        size_t n = std::min(count, space);
        size_t w = write_pos.load(std::memory_order_relaxed);
        for (size_t i = 0; i < n; ++i) {
            data[w] = src[i];
            w = (w + 1) % cap;
        }
        write_pos.store(w, std::memory_order_release);
        return n;
    }

    size_t read(int16_t* dst, size_t count) {
        size_t avail = available();
        size_t n = std::min(count, avail);
        size_t r = read_pos.load(std::memory_order_relaxed);
        for (size_t i = 0; i < n; ++i) {
            dst[i] = data[r];
            r = (r + 1) % cap;
        }
        read_pos.store(r, std::memory_order_release);
        if (n < count) {
            std::memset(dst + n, 0, (count - n) * sizeof(int16_t));
        }
        return n;
    }
};

// ── DSP processing blocks ───────────────────────────────────────────

static constexpr float kSampleScale = 1.0f / 32768.0f;
static constexpr float kSampleMax   = 32767.0f;

static inline float clampf(float x, float lo, float hi) {
    return x < lo ? lo : (x > hi ? hi : x);
}

static inline float soft_limit_i16(float x, float ceiling) {
    const float c = std::max(1.0f, ceiling);
    const float ax = std::fabs(x);
    const float knee = c * 0.90f;
    if (ax <= knee) return x;
    const float span = std::max(1.0f, c - knee);
    const float limited = knee + span * std::tanh((ax - knee) / span);
    const float y = std::min(c, limited);
    return x < 0.0f ? -y : y;
}

// ── Simple radix-2 FFT for power-of-2 lengths ──────────────────────
// Computes in-place complex FFT. `re` and `im` arrays of length `n`.
static void fft_radix2(float* re, float* im, int n, bool inverse) {
    // Bit-reversal permutation
    for (int i = 1, j = 0; i < n; ++i) {
        int bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) { std::swap(re[i], re[j]); std::swap(im[i], im[j]); }
    }
    const float sign = inverse ? 1.0f : -1.0f;
    for (int len = 2; len <= n; len <<= 1) {
        float ang = sign * 6.28318530718f / static_cast<float>(len);
        float wRe = std::cos(ang), wIm = std::sin(ang);
        for (int i = 0; i < n; i += len) {
            float curRe = 1.0f, curIm = 0.0f;
            for (int j = 0; j < len / 2; ++j) {
                int u = i + j, v = i + j + len / 2;
                float tRe = curRe * re[v] - curIm * im[v];
                float tIm = curRe * im[v] + curIm * re[v];
                re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                re[u] += tRe;        im[u] += tIm;
                float newCurRe = curRe * wRe - curIm * wIm;
                curIm = curRe * wIm + curIm * wRe;
                curRe = newCurRe;
            }
        }
    }
    if (inverse) {
        float inv_n = 1.0f / static_cast<float>(n);
        for (int i = 0; i < n; ++i) { re[i] *= inv_n; im[i] *= inv_n; }
    }
}

// Real FFT wrapper: 512-point real input → 257 magnitude + phase bins.
static void rfft_512(const float* input, float* mag, float* phase) {
    float re[512], im[512];
    std::memcpy(re, input, 512 * sizeof(float));
    std::memset(im, 0, 512 * sizeof(float));
    fft_radix2(re, im, 512, false);
    for (int i = 0; i < 257; ++i) {
        mag[i]   = std::sqrt(re[i] * re[i] + im[i] * im[i]);
        phase[i] = std::atan2(im[i], re[i]);
    }
}

// Inverse real FFT: 257 complex bins → 512 real output.
static void irfft_512(const float* mag, const float* phase, float* output) {
    float re[512], im[512];
    for (int i = 0; i < 257; ++i) {
        re[i] = mag[i] * std::cos(phase[i]);
        im[i] = mag[i] * std::sin(phase[i]);
    }
    // Conjugate symmetry
    for (int i = 257; i < 512; ++i) {
        re[i] =  re[512 - i];
        im[i] = -im[512 - i];
    }
    fft_radix2(re, im, 512, true);
    std::memcpy(output, re, 512 * sizeof(float));
}

// ── ONNX Runtime dynamic loader (moved before SE for dependency) ────

// Loads onnxruntime DLL at runtime; avoids link-time dependency.
struct OrtLoader {
    const OrtApi* api = nullptr;
    bool loaded = false;
    std::string error;

#ifdef _WIN32
    HMODULE lib = nullptr;
#else
    void* lib = nullptr;
#endif

    bool load(const char* hint_path = nullptr) {
        if (loaded) return true;

#ifdef _WIN32
        if (hint_path && *hint_path) {
            const auto wide = vc_utf8_to_wide(hint_path);
            if (!wide.empty()) lib = LoadLibraryW(wide.c_str());
        }
        if (!lib) lib = LoadLibraryW(L"onnxruntime.dll");
        if (!lib) { error = "cannot load onnxruntime.dll"; return false; }
        auto fn = reinterpret_cast<decltype(&OrtGetApiBase)>(GetProcAddress(lib, "OrtGetApiBase"));
#else
        if (hint_path && *hint_path) lib = dlopen(hint_path, RTLD_LAZY);
        if (!lib) lib = dlopen("libonnxruntime.so", RTLD_LAZY);
        if (!lib) { error = "cannot load libonnxruntime.so"; return false; }
        auto fn = reinterpret_cast<decltype(&OrtGetApiBase)>(dlsym(lib, "OrtGetApiBase"));
#endif
        if (!fn) { error = "OrtGetApiBase not found"; return false; }
        auto* base = fn();
        if (!base) { error = "OrtGetApiBase returned null"; return false; }
        api = base->GetApi(ORT_API_VERSION);
        if (!api) { error = "ORT API version mismatch"; return false; }
        loaded = true;
        return true;
    }

    ~OrtLoader() {
        // Don't unload — sessions may still reference the runtime.
    }
};

// Thread-safe singleton loader.
static OrtLoader& ort_loader() {
    static OrtLoader instance;
    return instance;
}

#ifdef _WIN32
static std::wstring utf8_to_wide(const std::string& utf8) {
    if (utf8.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
    if (len <= 0) return {};
    std::wstring wide(len, 0);
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, wide.data(), len);
    if (!wide.empty() && wide.back() == L'\0') wide.pop_back();
    return wide;
}
#endif

// ── DTLN Speech Enhancer ────────────────────────────────────────────
// Two-stage ONNX model (model_1 = STFT mask, model_2 = time-domain refinement).
// Processes 16kHz audio in 128-sample hops with 512-sample windows.
// Requires ONNX Runtime (loaded via existing OrtLoader singleton).

struct VcSpeechEnhancer {
    static constexpr int BLOCK_LEN = 512;
    static constexpr int BLOCK_SHIFT = 128;
    static constexpr int NUM_BINS = 257;
    static constexpr int STATE_DIM = 2 * 128 * 2; // [1,2,128,2] flattened

    // ONNX sessions
    const OrtApi* api = nullptr;
    OrtEnv* env = nullptr;
    OrtSessionOptions* se_opts = nullptr;
    OrtSession* model1 = nullptr;
    OrtSession* model2 = nullptr;
    OrtMemoryInfo* mem_info = nullptr;

    // LSTM states
    float states1[STATE_DIM] = {};
    float states2[STATE_DIM] = {};

    // Overlap-add buffers
    float in_buf[BLOCK_LEN] = {};
    float out_buf[BLOCK_LEN] = {};

    // Accumulator for arbitrary-sized input → 128-sample blocks
    std::vector<float> pending_in;
    std::vector<float> pending_out;

    // Output gain to compensate DTLN level attenuation (default 1.0 = no change).
    // Final loudness is handled by output auto gain, so avoid boosting DTLN noise here.
    float output_gain = 1.3f;

    bool loaded = false;
    bool enabled = false;
    std::string error;

    int init(const std::string& model_dir, const std::string& ort_dll_dir) {
        if (model_dir.empty()) { enabled = false; return 0; }

        // Check model files exist
        std::string m1_path = model_dir + "/model_1.onnx";
        std::string m2_path = model_dir + "/model_2.onnx";
        // On Windows also check backslash
        auto fix_sep = [](std::string& s) {
            for (auto& c : s) if (c == '/') c = '\\';
        };
        #ifdef _WIN32
        fix_sep(m1_path); fix_sep(m2_path);
        #endif

        if (!std::filesystem::exists(m1_path) || !std::filesystem::exists(m2_path)) {
            error = "DTLN model files not found in " + model_dir;
            enabled = false;
            return -1;
        }

        // Load ONNX Runtime
        auto& loader = ort_loader();
        std::string ort_hint;
        if (!ort_dll_dir.empty()) {
            #ifdef _WIN32
            ort_hint = ort_dll_dir + "\\onnxruntime.dll";
            #else
            ort_hint = ort_dll_dir + "/libonnxruntime.so";
            #endif
        }
        if (!loader.load(ort_hint.empty() ? nullptr : ort_hint.c_str())) {
            error = "ONNX Runtime load failed: " + loader.error;
            enabled = false;
            return -2;
        }
        api = loader.api;

        // Create env + session options
        OrtStatus* status = api->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "mobmate_se", &env);
        if (status) { error = "SE CreateEnv failed"; api->ReleaseStatus(status); return -3; }

        status = api->CreateSessionOptions(&se_opts);
        if (status) { error = "SE CreateSessionOptions failed"; api->ReleaseStatus(status); return -4; }
        api->SetIntraOpNumThreads(se_opts, 1);
        api->SetInterOpNumThreads(se_opts, 1);
        api->SetSessionGraphOptimizationLevel(se_opts, ORT_ENABLE_ALL);

        status = api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &mem_info);
        if (status) { error = "SE CreateCpuMemoryInfo failed"; api->ReleaseStatus(status); return -5; }

        // Load model_1
        #ifdef _WIN32
        auto m1_wide = utf8_to_wide(m1_path);
        status = api->CreateSession(env, m1_wide.c_str(), se_opts, &model1);
        #else
        status = api->CreateSession(env, m1_path.c_str(), se_opts, &model1);
        #endif
        if (status) {
            const char* msg = api->GetErrorMessage(status);
            error = std::string("DTLN model_1 load failed: ") + (msg ? msg : "?");
            api->ReleaseStatus(status); return -10;
        }

        // Load model_2
        #ifdef _WIN32
        auto m2_wide = utf8_to_wide(m2_path);
        status = api->CreateSession(env, m2_wide.c_str(), se_opts, &model2);
        #else
        status = api->CreateSession(env, m2_path.c_str(), se_opts, &model2);
        #endif
        if (status) {
            const char* msg = api->GetErrorMessage(status);
            error = std::string("DTLN model_2 load failed: ") + (msg ? msg : "?");
            api->ReleaseStatus(status); return -11;
        }

        // Reset states and buffers
        std::memset(states1, 0, sizeof(states1));
        std::memset(states2, 0, sizeof(states2));
        std::memset(in_buf, 0, sizeof(in_buf));
        std::memset(out_buf, 0, sizeof(out_buf));
        pending_in.clear();
        pending_out.clear();

        loaded = true;
        enabled = true;
        std::fprintf(stderr, "[SE] DTLN loaded from %s\n", model_dir.c_str());
        return 0;
    }

    void cleanup() {
        if (api) {
            if (model2)   { api->ReleaseSession(model2);   model2 = nullptr; }
            if (model1)   { api->ReleaseSession(model1);   model1 = nullptr; }
            if (mem_info) { api->ReleaseMemoryInfo(mem_info); mem_info = nullptr; }
            if (se_opts)  { api->ReleaseSessionOptions(se_opts); se_opts = nullptr; }
            if (env)      { api->ReleaseEnv(env);           env = nullptr; }
        }
        loaded = false;
        enabled = false;
    }

    ~VcSpeechEnhancer() { cleanup(); }

    // Reset LSTM states and pending buffers (for clean per-file processing in bench).
    void reset_states() {
        std::memset(states1, 0, sizeof(states1));
        std::memset(states2, 0, sizeof(states2));
        std::memset(in_buf, 0, sizeof(in_buf));
        std::memset(out_buf, 0, sizeof(out_buf));
        pending_in.clear();
        pending_out.clear();
    }

    // Process one DTLN block: 128 new samples in, 128 enhanced samples out.
    // Uses overlap-add with BLOCK_LEN=512 window, BLOCK_SHIFT=128 hop.
    void process_block(const float* new_samples, float* out_samples) {
        if (!loaded || !model1 || !model2) {
            std::memcpy(out_samples, new_samples, BLOCK_SHIFT * sizeof(float));
            return;
        }

        // Shift input buffer and append new samples
        std::memmove(in_buf, in_buf + BLOCK_SHIFT, (BLOCK_LEN - BLOCK_SHIFT) * sizeof(float));
        std::memcpy(in_buf + (BLOCK_LEN - BLOCK_SHIFT), new_samples, BLOCK_SHIFT * sizeof(float));

        // FFT
        float mag[NUM_BINS], phase[NUM_BINS];
        rfft_512(in_buf, mag, phase);

        // ── Model 1: magnitude mask estimation ──
        float mag_input[NUM_BINS];
        std::memcpy(mag_input, mag, NUM_BINS * sizeof(float));

        // Input shapes: [1,1,257] for mag, [1,2,128,2] for states
        int64_t mag_shape[] = {1, 1, NUM_BINS};
        int64_t state_shape[] = {1, 2, 128, 2};

        OrtValue* input_tensors1[2] = {};
        api->CreateTensorWithDataAsOrtValue(
            mem_info, mag_input, NUM_BINS * sizeof(float),
            mag_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &input_tensors1[0]);
        api->CreateTensorWithDataAsOrtValue(
            mem_info, states1, STATE_DIM * sizeof(float),
            state_shape, 4, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &input_tensors1[1]);

        const char* input_names1[] = {"input_2", "input_3"};
        const char* output_names1[] = {"activation_2", "tf_op_layer_stack_2"};
        OrtValue* output_tensors1[2] = {};

        OrtStatus* st = api->Run(model1, nullptr,
            input_names1, input_tensors1, 2,
            output_names1, 2, output_tensors1);

        if (st) {
            api->ReleaseStatus(st);
            if (input_tensors1[0]) api->ReleaseValue(input_tensors1[0]);
            if (input_tensors1[1]) api->ReleaseValue(input_tensors1[1]);
            std::memcpy(out_samples, new_samples, BLOCK_SHIFT * sizeof(float));
            return;
        }

        // Extract mask and update states
        float* mask_data = nullptr;
        api->GetTensorMutableData(output_tensors1[0], reinterpret_cast<void**>(&mask_data));
        float* new_states1 = nullptr;
        api->GetTensorMutableData(output_tensors1[1], reinterpret_cast<void**>(&new_states1));
        if (new_states1) std::memcpy(states1, new_states1, STATE_DIM * sizeof(float));

        // Apply mask and reconstruct
        float est_mag[NUM_BINS];
        for (int i = 0; i < NUM_BINS; ++i)
            est_mag[i] = mag[i] * (mask_data ? mask_data[i] : 1.0f);

        float est_block[BLOCK_LEN];
        irfft_512(est_mag, phase, est_block);

        // ── Model 2: time-domain refinement ──
        float block_input[BLOCK_LEN];
        std::memcpy(block_input, est_block, BLOCK_LEN * sizeof(float));

        int64_t block_shape[] = {1, 1, BLOCK_LEN};
        OrtValue* input_tensors2[2] = {};
        api->CreateTensorWithDataAsOrtValue(
            mem_info, block_input, BLOCK_LEN * sizeof(float),
            block_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &input_tensors2[0]);
        api->CreateTensorWithDataAsOrtValue(
            mem_info, states2, STATE_DIM * sizeof(float),
            state_shape, 4, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &input_tensors2[1]);

        const char* input_names2[] = {"input_4", "input_5"};
        const char* output_names2[] = {"conv1d_3", "tf_op_layer_stack_5"};
        OrtValue* output_tensors2[2] = {};

        st = api->Run(model2, nullptr,
            input_names2, input_tensors2, 2,
            output_names2, 2, output_tensors2);

        if (st) {
            api->ReleaseStatus(st);
            // fallback: use est_block
        } else {
            float* out_data = nullptr;
            api->GetTensorMutableData(output_tensors2[0], reinterpret_cast<void**>(&out_data));
            float* new_states2 = nullptr;
            api->GetTensorMutableData(output_tensors2[1], reinterpret_cast<void**>(&new_states2));
            if (new_states2) std::memcpy(states2, new_states2, STATE_DIM * sizeof(float));
            if (out_data) std::memcpy(est_block, out_data, BLOCK_LEN * sizeof(float));
        }

        // Overlap-add
        std::memmove(out_buf, out_buf + BLOCK_SHIFT, (BLOCK_LEN - BLOCK_SHIFT) * sizeof(float));
        std::memset(out_buf + (BLOCK_LEN - BLOCK_SHIFT), 0, BLOCK_SHIFT * sizeof(float));
        for (int i = 0; i < BLOCK_LEN; ++i) out_buf[i] += est_block[i];
        std::memcpy(out_samples, out_buf, BLOCK_SHIFT * sizeof(float));

        // Cleanup ORT values
        for (auto* v : output_tensors2) if (v) api->ReleaseValue(v);
        for (auto* v : input_tensors2)  if (v) api->ReleaseValue(v);
        for (auto* v : output_tensors1) if (v) api->ReleaseValue(v);
        for (auto* v : input_tensors1)  if (v) api->ReleaseValue(v);
    }

    // Process arbitrary-length int16 PCM at 16kHz.
    // Accumulates samples, processes in 128-sample blocks, returns enhanced.
    // Audio is processed in-place via intermediate float buffers.
    void process(int16_t* buf, size_t count) {
        if (!enabled || !loaded || count == 0) return;

        // Convert int16 → float and append to pending
        size_t old_size = pending_in.size();
        pending_in.resize(old_size + count);
        for (size_t i = 0; i < count; ++i)
            pending_in[old_size + i] = buf[i] * kSampleScale;

        // Process all complete blocks
        while (pending_in.size() >= static_cast<size_t>(BLOCK_SHIFT)) {
            float out_block[BLOCK_SHIFT];
            process_block(pending_in.data(), out_block);
            pending_in.erase(pending_in.begin(), pending_in.begin() + BLOCK_SHIFT);
            pending_out.insert(pending_out.end(), out_block, out_block + BLOCK_SHIFT);
        }

        // Copy enhanced samples back to buf (as many as available)
        size_t copy_count = std::min(count, pending_out.size());
        float gain = output_gain;
        for (size_t i = 0; i < copy_count; ++i)
            buf[i] = static_cast<int16_t>(clampf(pending_out[i] * gain, -1.0f, 1.0f) * kSampleMax);
        pending_out.erase(pending_out.begin(), pending_out.begin() + static_cast<std::ptrdiff_t>(copy_count));

        // If we didn't have enough enhanced samples, zero-fill (startup latency)
        for (size_t i = copy_count; i < count; ++i) buf[i] = 0;
    }
};

// Noise gate: zero out samples below threshold.
struct VcNoiseGate {
    float threshold = 0.01f; // linear amplitude
    bool  enabled   = true;

    void init(float gate_param) {
        // gate_param 0..1 → threshold 0.001 .. 0.05
        threshold = 0.001f + gate_param * 0.049f;
        enabled = gate_param > 0.001f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        // Compute frame RMS
        float sum = 0.0f;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            sum += s * s;
        }
        float rms = std::sqrt(sum / static_cast<float>(count));
        if (rms < threshold) {
            std::memset(buf, 0, count * sizeof(int16_t));
        }
    }
};

struct VcExternalNeutralizer {
    using CreateFn = void* (*)(int, int, float, float, float);
    using DestroyFn = void (*)(void*);
    using ProcessFn = int (*)(void*, int16_t*, int);
    using StatusFn = const char* (*)(void*);

#ifdef _WIN32
    HMODULE lib = nullptr;
#else
    void* lib = nullptr;
#endif
    void* state = nullptr;
    CreateFn create_fn = nullptr;
    DestroyFn destroy_fn = nullptr;
    ProcessFn process_fn = nullptr;
    StatusFn status_fn = nullptr;
    bool enabled = false;
    std::string status = "unavailable";

    ~VcExternalNeutralizer() { shutdown(); }

    void shutdown() {
        if (state && destroy_fn) destroy_fn(state);
        state = nullptr;
#ifdef _WIN32
        if (lib) FreeLibrary(lib);
#else
        if (lib) dlclose(lib);
#endif
        lib = nullptr;
        enabled = false;
    }

    void* symbol(const char* name) {
#ifdef _WIN32
        return lib ? reinterpret_cast<void*>(GetProcAddress(lib, name)) : nullptr;
#else
        return lib ? dlsym(lib, name) : nullptr;
#endif
    }

    bool load_library() {
#ifdef _WIN32
        wchar_t module_path[MAX_PATH] = {};
        HMODULE self_mod = nullptr;
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&vc_dup_string), &self_mod) && self_mod) {
            GetModuleFileNameW(self_mod, module_path, MAX_PATH);
            std::wstring base(module_path);
            size_t slash = base.find_last_of(L"\\/");
            if (slash != std::wstring::npos) {
                std::wstring local = base.substr(0, slash + 1) + L"mobmate_vn_ext.dll";
                lib = LoadLibraryExW(local.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (lib) return true;
            }
        }
        const wchar_t* names[] = {L"mobmate_vn_ext.dll", L"native\\mobmate_vn_ext.dll", L"libs\\voice-neutralizer\\win-x64\\mobmate_vn_ext.dll"};
        for (auto* n : names) {
            lib = LoadLibraryW(n);
            if (lib) return true;
        }
#else
        Dl_info info = {};
        if (dladdr(reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(&vc_dup_string)), &info) && info.dli_fname) {
            std::string base(info.dli_fname);
            size_t slash = base.find_last_of('/');
            if (slash != std::string::npos) {
                std::string local = base.substr(0, slash + 1) + "libmobmate_vn_ext.so";
                lib = dlopen(local.c_str(), RTLD_NOW | RTLD_LOCAL);
                if (lib) return true;
            }
        }
        const char* names[] = {"./libmobmate_vn_ext.so", "./native/libmobmate_vn_ext.so", "./libs/voice-neutralizer/linux-x64/libmobmate_vn_ext.so"};
        for (auto* n : names) {
            lib = dlopen(n, RTLD_NOW | RTLD_LOCAL);
            if (lib) return true;
        }
#endif
        return false;
    }

    void init(const mobmate::vc::VoiceConversionOptions& opts, int sr) {
        shutdown();
        std::string cleanup = opts.noise_cleanup;
        std::transform(cleanup.begin(), cleanup.end(), cleanup.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        std::string preset = opts.neutralizer_preset;
        std::transform(preset.begin(), preset.end(), preset.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        float strength = clampf(opts.neutralizer_strength, 0.0f, 1.0f);
        float rnnoise_preset = 0.30f;
        float webrtc_preset = 0.35f;
        float world_preset = 0.18f;
        if (preset == "light") {
            rnnoise_preset = 0.20f;
            webrtc_preset = 0.25f;
            world_preset = 0.0f;
        } else if (preset == "flat") {
            rnnoise_preset = 0.35f;
            webrtc_preset = 0.45f;
            world_preset = 0.30f;
        } else if (preset == "privacy") {
            rnnoise_preset = 0.45f;
            webrtc_preset = 0.50f;
            world_preset = 0.40f;
        }
        float scale = 0.50f + 0.50f * strength;
        const float cleanup_strength = 0.25f;
        float rnnoise_wet = (cleanup == "rnnoise" || cleanup == "hybrid" || cleanup == "all") ? rnnoise_preset * scale * cleanup_strength : 0.0f;
        float webrtc_wet = (cleanup == "webrtc" || cleanup == "apm" || cleanup == "hybrid" || cleanup == "all") ? webrtc_preset * scale * cleanup_strength : 0.0f;
        bool has_world_wet = opts.raw_json.find("worldWet") != std::string::npos;
        float world_wet = clampf(has_world_wet ? opts.world_wet : world_preset * scale, 0.0f, 3.0f);
        if (rnnoise_wet <= 0.001f && webrtc_wet <= 0.001f && world_wet <= 0.001f) {
            status = "disabled";
            return;
        }
        if (!load_library()) {
            status = "missing mobmate_vn_ext";
            return;
        }
        create_fn = reinterpret_cast<CreateFn>(symbol("mmvn_create"));
        destroy_fn = reinterpret_cast<DestroyFn>(symbol("mmvn_destroy"));
        process_fn = reinterpret_cast<ProcessFn>(symbol("mmvn_process_i16"));
        status_fn = reinterpret_cast<StatusFn>(symbol("mmvn_status"));
        if (!create_fn || !destroy_fn || !process_fn) {
            status = "invalid mobmate_vn_ext ABI";
            shutdown();
            return;
        }
        state = create_fn(sr, 1, rnnoise_wet, webrtc_wet, world_wet);
        enabled = state != nullptr;
        const char* s = (enabled && status_fn) ? status_fn(state) : nullptr;
        status = s ? s : (enabled ? "loaded" : "create failed");
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled || !process_fn || !state || count > static_cast<size_t>(std::numeric_limits<int>::max())) return;
        process_fn(state, buf, static_cast<int>(count));
    }
};

// DC blocker: removes low-frequency bias before pressure flattening.
struct VcDcBlocker {
    float x1 = 0.0f;
    float y1 = 0.0f;
    bool enabled = true;

    void init(float strength) {
        enabled = strength > 0.01f;
        x1 = y1 = 0.0f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        constexpr float r = 0.995f;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;
            float y = x - x1 + r * y1;
            x1 = x;
            y1 = y;
            buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// One-pole high-pass: trims rumble/proximity that reads as vocal pressure.
struct VcHighPassFilter {
    float x1 = 0.0f;
    float y1 = 0.0f;
    float a = 0.0f;
    bool enabled = true;

    void init(float strength, int sr) {
        enabled = strength > 0.01f;
        x1 = y1 = 0.0f;
        float cutoff = 70.0f + 65.0f * strength;
        float rc = 1.0f / (2.0f * 3.14159265f * cutoff);
        float dt = 1.0f / static_cast<float>(std::max(1, sr));
        a = rc / (rc + dt);
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;
            float y = a * (y1 + x - x1);
            x1 = x;
            y1 = y;
            buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Loudness smoother: gently pulls each short frame toward a neutral RMS while
// constraining gain-up and gain-down to avoid pumping.
struct VcLoudnessSmoother {
    float target_rms = 0.10f;
    float max_gain_up = 1.35f;
    float max_gain_down = 0.55f;
    float gain = 1.0f;
    float attack_blend = 0.25f;
    float release_blend = 0.06f;
    bool enabled = true;
    std::atomic<float> gain_reduction_db{0.0f};

    void init(float strength, int /*sr*/) {
        enabled = strength > 0.01f;
        target_rms = 0.115f - 0.035f * strength;
        max_gain_up = 1.0f + 0.55f * strength;
        max_gain_down = 1.0f - 0.55f * strength;
        attack_blend = 0.12f + 0.22f * strength;
        release_blend = 0.035f + 0.05f * strength;
        gain = 1.0f;
        gain_reduction_db.store(0.0f, std::memory_order_relaxed);
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled || count == 0) return;
        float sum_sq = 0.0f;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            sum_sq += s * s;
        }
        float rms = std::sqrt(sum_sq / static_cast<float>(count));
        float target_gain = 1.0f;
        if (rms > 0.006f) {
            target_gain = clampf(target_rms / rms, max_gain_down, max_gain_up);
            if (rms < 0.025f) target_gain = std::min(target_gain, 1.0f); // do not lift breaths/noise
        }
        float blend = (target_gain < gain) ? attack_blend : release_blend;
        gain += (target_gain - gain) * blend;
        float gr = gain < 0.999f ? -20.0f * std::log10(std::max(gain, 0.0001f)) : 0.0f;
        gain_reduction_db.store(gr, std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            buf[i] = static_cast<int16_t>(clampf(s * gain, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Breath gate: attenuates quiet high-frequency/noisy frames without hard muting.
struct VcBreathGate {
    float strength = 0.0f;
    bool enabled = true;

    void init(float s) {
        strength = clampf(s, 0.0f, 1.0f);
        enabled = strength > 0.01f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled || count < 2) return;
        float sum_sq = 0.0f;
        float diff_sum = 0.0f;
        float prev = buf[0] * kSampleScale;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            sum_sq += s * s;
            if (i > 0) diff_sum += std::fabs(s - prev);
            prev = s;
        }
        float rms = std::sqrt(sum_sq / static_cast<float>(count));
        float hf = diff_sum / static_cast<float>(count - 1);
        float noisy = hf / std::max(rms, 0.0005f);
        if (rms > (0.020f + 0.018f * strength) || noisy < 0.75f) return;
        float atten = 1.0f - 0.45f * strength;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            buf[i] = static_cast<int16_t>(clampf(s * atten, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// De-esser: detects high-band energy via a simple low-pass residual and lowers it.
struct VcDeEsser {
    float lp = 0.0f;
    float env = 0.0f;
    float lp_coeff = 0.0f;
    float threshold = 0.025f;
    float strength = 0.0f;
    bool enabled = true;

    void init(float s, int sr) {
        strength = clampf(s, 0.0f, 1.0f);
        enabled = strength > 0.01f;
        lp = env = 0.0f;
        float cutoff = 4800.0f;
        lp_coeff = std::exp(-2.0f * 3.14159265f * cutoff / static_cast<float>(std::max(1, sr)));
        threshold = 0.035f - 0.018f * strength;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;
            lp = lp_coeff * lp + (1.0f - lp_coeff) * x;
            float hi = x - lp;
            env = 0.92f * env + 0.08f * std::fabs(hi);
            float hi_gain = 1.0f;
            if (env > threshold) {
                float over = clampf((env - threshold) / std::max(threshold, 0.001f), 0.0f, 1.0f);
                hi_gain = 1.0f - over * (0.45f * strength);
            }
            float y = lp + hi * hi_gain;
            buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Spectral tilt: reduces low-end weight a little so pressure feels less dense.
struct VcSpectralTilt {
    float low = 0.0f;
    float coeff = 0.0f;
    float strength = 0.0f;
    bool enabled = true;

    void init(float s, int sr) {
        strength = clampf(s, 0.0f, 1.0f);
        enabled = strength > 0.01f;
        low = 0.0f;
        float cutoff = 260.0f;
        coeff = std::exp(-2.0f * 3.14159265f * cutoff / static_cast<float>(std::max(1, sr)));
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        float low_trim = 0.08f + 0.16f * strength;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;
            low = coeff * low + (1.0f - coeff) * x;
            float y = x - low * low_trim;
            buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Pressure suppressor: dynamically removes vocal body/pressure bands while
// leaving intelligibility bands mostly intact.  It targets 120-350Hz chest
// weight and 400-800Hz mud/body, clamps fast attacks, then lightly restores
// frame loudness to keep speech readable without preserving intimidation.
struct VcPressureSuppressor {
    float strength = 0.0f;
    bool enabled = true;
    float lp120 = 0.0f, lp350 = 0.0f, lp400 = 0.0f, lp800 = 0.0f;
    float c120 = 0.0f, c350 = 0.0f, c400 = 0.0f, c800 = 0.0f;
    float body_env = 0.0f, mud_env = 0.0f, rms_env = 0.0f, peak_env = 0.0f;
    float gain = 1.0f;

    static float one_pole_coeff(float cutoff, int sr) {
        return std::exp(-2.0f * 3.14159265f * cutoff / static_cast<float>(std::max(1, sr)));
    }

    void init(float s, int sr) {
        strength = clampf(s, 0.0f, 1.0f);
        enabled = strength > 0.01f;
        lp120 = lp350 = lp400 = lp800 = 0.0f;
        body_env = mud_env = rms_env = peak_env = 0.0f;
        gain = 1.0f;
        c120 = one_pole_coeff(120.0f, sr);
        c350 = one_pole_coeff(350.0f, sr);
        c400 = one_pole_coeff(400.0f, sr);
        c800 = one_pole_coeff(800.0f, sr);
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled || count == 0) return;

        float sum_in = 0.0f;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;
            sum_in += x * x;
        }
        float frame_rms_in = std::sqrt(sum_in / static_cast<float>(count));

        float sum_out = 0.0f;
        const float max_body_cut = 0.46f + 0.24f * strength; // privacy-first: suppress vocal body without over-thinning
        const float max_mud_cut  = 0.36f + 0.24f * strength;
        const float attack_ceiling = 0.36f - 0.14f * strength;
        for (size_t i = 0; i < count; ++i) {
            float x = buf[i] * kSampleScale;

            lp120 = c120 * lp120 + (1.0f - c120) * x;
            lp350 = c350 * lp350 + (1.0f - c350) * x;
            lp400 = c400 * lp400 + (1.0f - c400) * x;
            lp800 = c800 * lp800 + (1.0f - c800) * x;
            float body = lp350 - lp120; // 120-350Hz
            float mud  = lp800 - lp400; // 400-800Hz

            float level = std::fabs(x);
            rms_env = 0.985f * rms_env + 0.015f * level;
            body_env = 0.965f * body_env + 0.035f * std::fabs(body);
            mud_env  = 0.965f * mud_env  + 0.035f * std::fabs(mud);
            peak_env = std::max(level, 0.93f * peak_env);

            float body_ratio = body_env / std::max(rms_env, 0.0025f);
            float mud_ratio  = mud_env  / std::max(rms_env, 0.0025f);
            float pressure = clampf((rms_env - 0.016f) / 0.100f, 0.0f, 1.0f);
            float body_dyn = clampf((body_ratio - 0.16f) / 0.34f, 0.0f, 1.0f) * pressure;
            float mud_dyn  = clampf((mud_ratio  - 0.13f) / 0.30f, 0.0f, 1.0f) * pressure;

            float y = x - body * (max_body_cut * body_dyn) - mud * (max_mud_cut * mud_dyn);

            float target_gain = 1.0f;
            if (peak_env > attack_ceiling) {
                float over = clampf((peak_env - attack_ceiling) / std::max(attack_ceiling, 0.001f), 0.0f, 1.0f);
                target_gain = 1.0f - over * (0.28f + 0.22f * strength);
            }
            if (target_gain < gain) gain = target_gain;
            else gain += (target_gain - gain) * (0.012f + 0.018f * (1.0f - strength));
            y *= gain;

            sum_out += y * y;
            buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
        }

        float frame_rms_out = std::sqrt(sum_out / static_cast<float>(count));
        if (frame_rms_in > 0.018f && frame_rms_out > 0.001f) {
            float restore = clampf(frame_rms_in / frame_rms_out, 1.0f, 1.0f + 0.10f * (1.0f - strength));
            for (size_t i = 0; i < count; ++i) {
                float y = buf[i] * kSampleScale * restore;
                buf[i] = static_cast<int16_t>(clampf(y, -1.0f, 1.0f) * kSampleMax);
            }
        }
    }
};

// Energy compressor: reduces dynamic range of loud signals.
// Uses envelope-following gain with attack/release.
struct VcEnergyCompressor {
    float threshold  = 0.25f;  // linear threshold
    float ratio      = 4.0f;   // compression ratio
    float attack_coeff  = 0.0f;
    float release_coeff = 0.0f;
    float envelope   = 0.0f;   // current envelope level
    bool  enabled    = true;

    void init(float strength, int sr) {
        // strength 0..1 maps to ratio 1.5..8 and threshold 0.4..0.15
        ratio     = 1.5f + strength * 6.5f;
        threshold = 0.40f - strength * 0.25f;
        float attack_ms  = 3.0f;
        float release_ms = 40.0f;
        attack_coeff  = std::exp(-1.0f / (attack_ms  * sr / 1000.0f));
        release_coeff = std::exp(-1.0f / (release_ms * sr / 1000.0f));
        envelope = 0.0f;
        enabled = strength > 0.01f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            float level = std::fabs(s);
            // Envelope follower
            if (level > envelope)
                envelope = attack_coeff * envelope + (1.0f - attack_coeff) * level;
            else
                envelope = release_coeff * envelope + (1.0f - release_coeff) * level;
            // Compute gain
            float gain = 1.0f;
            if (envelope > threshold) {
                float db_above = 20.0f * std::log10(envelope / threshold);
                float db_reduction = db_above * (1.0f - 1.0f / ratio);
                gain = std::pow(10.0f, -db_reduction / 20.0f);
            }
            buf[i] = static_cast<int16_t>(clampf(s * gain, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// High-shelf biquad filter: attenuate high frequencies (sharpness/sibilance).
struct VcHighShelf {
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
    float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    bool  enabled = true;

    void init(float strength, int sr) {
        // strength 0..1 → gain -0.5..-3 dB at 6500 Hz.
        // Keep enough 4-8kHz presence for whisper/STT readability while still
        // trimming harshness on sharp voices.
        float gain_db  = -0.5f - strength * 2.5f;
        float freq_hz  = 6500.0f;
        enabled = strength > 0.01f;
        if (!enabled) return;

        float A   = std::pow(10.0f, gain_db / 40.0f);
        float w0  = 2.0f * 3.14159265f * freq_hz / static_cast<float>(sr);
        float cw  = std::cos(w0);
        float sw  = std::sin(w0);
        float S   = 1.0f; // shelf slope
        float alpha = sw / 2.0f * std::sqrt((A + 1.0f/A) * (1.0f/S - 1.0f) + 2.0f);
        float sqA = 2.0f * std::sqrt(A) * alpha;

        float a0_inv = 1.0f / ((A+1) - (A-1)*cw + sqA);
        b0 =  A * ((A+1) + (A-1)*cw + sqA) * a0_inv;
        b1 = -2*A * ((A-1) + (A+1)*cw)      * a0_inv;
        b2 =  A * ((A+1) + (A-1)*cw - sqA)  * a0_inv;
        a1 =  2 * ((A-1) - (A+1)*cw)        * a0_inv;
        a2 = ((A+1) - (A-1)*cw - sqA)       * a0_inv;
        x1 = x2 = y1 = y2 = 0;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        for (size_t i = 0; i < count; ++i) {
            float x0 = buf[i] * kSampleScale;
            float y0 = b0*x0 + b1*x1 + b2*x2 - a1*y1 - a2*y2;
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
            buf[i] = static_cast<int16_t>(clampf(y0, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Peak limiter with instant attack, smooth release.
struct VcPeakLimiter {
    float ceiling      = 0.95f;
    float release_coeff = 0.0f;
    float gain         = 1.0f;
    bool  enabled      = true;

    void init(float ceiling_param, int sr) {
        ceiling = clampf(ceiling_param, 0.1f, 1.0f);
        float release_ms = 8.0f;
        release_coeff = std::exp(-1.0f / (release_ms * sr / 1000.0f));
        gain = 1.0f;
        enabled = ceiling < 0.999f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        for (size_t i = 0; i < count; ++i) {
            float s = buf[i] * kSampleScale;
            float level = std::fabs(s);
            float target = (level > ceiling) ? ceiling / level : 1.0f;
            if (target < gain)
                gain = target; // instant attack
            else
                gain = release_coeff * gain + (1.0f - release_coeff) * target;
            buf[i] = static_cast<int16_t>(clampf(s * gain, -1.0f, 1.0f) * kSampleMax);
        }
    }
};

// Pressure estimator: computes a 0..1 pressure score from audio features.
// Runs on the *original* input (before DSP) so it measures the speaker's
// actual intensity, not the compressed output.
struct VcPressureEstimator {
    std::atomic<float> pressure_score{0.0f};
    std::atomic<float> rms_level{0.0f};
    std::atomic<float> peak_level{0.0f};
    float smoothing = 0.92f;
    bool  enabled   = true;

    void init(bool en) { enabled = en; pressure_score.store(0.0f); }

    void estimate(const int16_t* buf, size_t count) {
        if (!enabled || count == 0) return;
        float sum_sq = 0.0f, peak = 0.0f;
        for (size_t i = 0; i < count; ++i) {
            float s = std::fabs(buf[i] * kSampleScale);
            sum_sq += s * s;
            if (s > peak) peak = s;
        }
        float rms = std::sqrt(sum_sq / static_cast<float>(count));
        rms_level.store(rms, std::memory_order_relaxed);
        peak_level.store(peak, std::memory_order_relaxed);

        // Simple composite pressure: volume + peak strength
        float vol_p  = clampf(rms / 0.35f, 0.0f, 1.0f);
        float peak_p = clampf(peak / 0.7f, 0.0f, 1.0f);
        float raw = vol_p * 0.5f + peak_p * 0.5f;

        float prev = pressure_score.load(std::memory_order_relaxed);
        float next = smoothing * prev + (1.0f - smoothing) * raw;
        pressure_score.store(next, std::memory_order_relaxed);
    }
};

// Conversion strength controller: adjusts DSP strength based on pressure.
struct VcStrengthController {
    float base_strength       = 0.3f;
    float pressure_sensitivity = 0.7f;
    bool  adaptive            = true;

    void init(bool adapt) { adaptive = adapt; }

    // Returns a multiplier 0..1 to scale DSP aggressiveness.
    float get_strength(float pressure) const {
        if (!adaptive) return 1.0f; // fixed full strength
        float s = base_strength + pressure_sensitivity * clampf(pressure, 0.0f, 1.0f);
        return clampf(s, 0.0f, 1.0f);
    }
};

// Hermite cubic interpolation for high-quality fractional delay reads.
static inline float hermite4(float frac, float ym1, float y0, float y1, float y2) {
    float c0 = y0;
    float c1 = 0.5f * (y1 - ym1);
    float c2 = ym1 - 2.5f * y0 + 2.0f * y1 - 0.5f * y2;
    float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
    return ((c3 * frac + c2) * frac + c1) * frac + c0;
}

// Real-time pitch shifter using dual-head sawtooth delay modulation.
// Two read heads sweep through a delay line at a rate determined by the
// pitch ratio.  Hann-windowed crossfade hides the discontinuity when
// each head resets.  Hermite cubic interpolation for quality.
// Adaptive sweep range for smoother grains.
struct VcPitchShifter {
    static constexpr size_t DELAY_LEN = 4096;
    float delay_buf[DELAY_LEN] = {};
    size_t write_pos = 0;
    float phase[2]   = {0.0f, 0.5f};
    float phase_inc   = 0.0f;
    float sweep_range = 0.0f;
    bool  pitch_up    = true;
    bool  enabled     = false;

    void init(float semitones, int sr) {
        float ratio = std::pow(2.0f, semitones / 12.0f);
        enabled = std::fabs(semitones) > 0.05f;
        if (!enabled) return;

        pitch_up    = (ratio > 1.0f);
        // Adaptive sweep: larger = smoother grains, less flanging.
        float abs_shift = std::fabs(ratio - 1.0f);
        sweep_range = clampf(384.0f / abs_shift, 768.0f, 2600.0f);
        phase_inc   = abs_shift / sweep_range;
        std::memset(delay_buf, 0, sizeof(delay_buf));
        write_pos   = 0;
        phase[0]    = 0.0f;
        phase[1]    = 0.5f;
    }

    void process(int16_t* buf, size_t count) {
        if (!enabled) return;
        const float base_delay  = DELAY_LEN * 0.5f;
        const float half_sweep  = sweep_range * 0.5f;
        const float two_pi      = 6.28318530f;
        const int   dlen        = static_cast<int>(DELAY_LEN);

        for (size_t i = 0; i < count; ++i) {
            delay_buf[write_pos] = buf[i] * kSampleScale;

            float out = 0.0f;
            for (int h = 0; h < 2; ++h) {
                float sweep_offset = pitch_up
                    ? (1.0f - phase[h]) * sweep_range
                    : phase[h] * sweep_range;
                float delay = base_delay - half_sweep + sweep_offset;

                float rp = static_cast<float>(write_pos) - delay;
                while (rp < 0.0f) rp += DELAY_LEN;
                int idx0 = static_cast<int>(rp) % dlen;
                int idx_m1 = (idx0 + dlen - 1) % dlen;
                int idx1 = (idx0 + 1) % dlen;
                int idx2 = (idx0 + 2) % dlen;
                float frac = rp - std::floor(rp);
                float sample = hermite4(frac,
                    delay_buf[idx_m1], delay_buf[idx0],
                    delay_buf[idx1],   delay_buf[idx2]);

                float w = 0.5f * (1.0f - std::cos(two_pi * phase[h]));
                out += sample * w;
            }

            buf[i] = static_cast<int16_t>(clampf(out, -1.0f, 1.0f) * kSampleMax);

            write_pos = (write_pos + 1) % DELAY_LEN;
            for (int h = 0; h < 2; ++h) {
                phase[h] += phase_inc;
                if (phase[h] >= 1.0f) phase[h] -= 1.0f;
            }
        }
    }
};

// (removed: VcPreEmphasis / VcDeEmphasis - not beneficial for DNSMOS)

// Bundles all DSP blocks into one chain.
struct VcDspChain {
    VcSpeechEnhancer     se;   // DTLN neural SE (front of chain)
    VcExternalNeutralizer external;
    VcDcBlocker          dc_blocker;
    VcHighPassFilter     highpass;
    VcNoiseGate          gate;
    VcBreathGate         breath_gate;
    VcLoudnessSmoother   loudness;
    VcEnergyCompressor   compressor;
    VcDeEsser            de_esser;
    VcHighShelf          shelf;
    VcSpectralTilt       tilt;
    VcPressureSuppressor pressure_suppressor;
    VcPeakLimiter        limiter;
    VcPitchShifter       pitch_shifter;
    VcPressureEstimator  pressure;
    VcStrengthController strength_ctrl;
    bool                 enabled = true;

    // Pre-allocated processing buffer (avoid allocation in audio callback).
    std::vector<int16_t> buf;

    // Resampler state for 48kHz ↔ 16kHz conversion around SE
    std::vector<int16_t> se_16k_buf;

    static float preset_strength(const std::string& preset, float fallback) {
        std::string p = preset;
        std::transform(p.begin(), p.end(), p.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        if (p == "light") return 0.35f;
        if (p == "flat") return 0.80f;
        if (p == "privacy") return 0.90f;
        if (p == "neutral") return 0.60f;
        return fallback;
    }

    void init(const mobmate::vc::VoiceConversionOptions& opts, int sr) {
        enabled = opts.prosody_neutralizer_enabled;
        float neutral_strength = clampf(opts.neutralizer_strength, 0.0f, 1.0f);
        bool has_neutralizer_strength = opts.raw_json.find("neutralizerStrength") != std::string::npos;
        bool has_neutralizer_preset = opts.raw_json.find("neutralizerPreset") != std::string::npos;
        if (!has_neutralizer_strength && !has_neutralizer_preset) {
            neutral_strength = 0.18f; // legacy UI/API compatibility; explicit presets opt into stronger v1 processing
        } else if (!has_neutralizer_strength) {
            neutral_strength = preset_strength(opts.neutralizer_preset, neutral_strength);
        }
        float pressure_strength = opts.pressure_flatten >= 0.0f ? clampf(opts.pressure_flatten, 0.0f, 1.0f) : neutral_strength;
        float breath_strength = opts.breath_suppress >= 0.0f ? clampf(opts.breath_suppress, 0.0f, 1.0f) : neutral_strength * 0.25f;
        dc_blocker.init(pressure_strength);
        highpass.init(pressure_strength, sr);
        gate.init(opts.noise_gate_threshold * (0.65f + 0.35f * neutral_strength));
        breath_gate.init(breath_strength);
        loudness.init(pressure_strength, sr);
        compressor.init(clampf(opts.energy_compression * (0.80f + 0.35f * neutral_strength), 0.0f, 1.0f), sr);
        de_esser.init(neutral_strength * 0.80f, sr);
        shelf.init(clampf(opts.sharpness_suppression * (0.75f + 0.35f * neutral_strength), 0.0f, 1.0f), sr);
        tilt.init(neutral_strength * 0.80f, sr);
        pressure_suppressor.init(opts.pressure_suppressor_strength, sr);
        limiter.init(opts.output_limiter_ceiling, sr);
        pitch_shifter.init(opts.f0_shift_semitones, sr);
        pressure.init(opts.pressure_estimator_enabled);
        strength_ctrl.init(opts.adaptive_strength);
        // Pre-allocate for up to 100ms at sample rate
        buf.resize(static_cast<size_t>(sr / 10), 0);
        // Init SE (DTLN) — operates at 16kHz internally
        std::string cleanup = opts.noise_cleanup;
        std::transform(cleanup.begin(), cleanup.end(), cleanup.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        se.init((cleanup == "off" || cleanup == "none") ? std::string() : opts.se_model_dir, opts.onnx_runtime_dll_dir);
        external.init(opts, sr);
    }

    // Process a frame of audio in-place via the DSP chain.
    // `src` is the original capture buffer (const); processed result goes to
    // the internal `buf` which is returned.  Caller writes `buf` to ring.
    const int16_t* process(const int16_t* src, size_t count, int sr) {
        if (!enabled) return src;
        if (count > buf.size()) buf.resize(count);

        // Estimate pressure on original signal (before DSP).
        pressure.estimate(src, count);

        // Copy to mutable buffer.
        std::memcpy(buf.data(), src, count * sizeof(int16_t));

        // Input conditioning first: remove DC/rumble before neural cleanup.
        dc_blocker.process(buf.data(), count);
        highpass.process(buf.data(), count);

        // ── Speech Enhancement (DTLN) at front of cleanup chain ──
        // DTLN operates at 16kHz. If capture is 48kHz, downsample→SE→upsample.
        if (se.enabled && se.loaded) {
            if (sr == 16000) {
                se.process(buf.data(), count);
            } else if (sr == 48000) {
                // Simple 3:1 decimation for 48k→16k
                size_t n16 = count / 3;
                se_16k_buf.resize(n16);
                for (size_t i = 0; i < n16; ++i) se_16k_buf[i] = buf[i * 3];
                se.process(se_16k_buf.data(), n16);
                // 16k→48k: replicate each sample 3x (simple zero-order hold)
                for (size_t i = 0; i < n16; ++i) {
                    buf[i * 3]     = se_16k_buf[i];
                    buf[i * 3 + 1] = se_16k_buf[i];
                    buf[i * 3 + 2] = se_16k_buf[i];
                }
            }
            // Other sample rates: skip SE (rare case)
        }
        external.process(buf.data(), count);

        // Adaptive strength multiplier (affects compressor/shelf intensity
        // indirectly via the parameters already set; for now just gate bypass
        // when pressure is very low).
        float p = pressure.pressure_score.load(std::memory_order_relaxed);
        float str = strength_ctrl.get_strength(p);

        // Apply neutralizer shaping chain.
        gate.process(buf.data(), count);
        breath_gate.process(buf.data(), count);
        if (str > 0.15f) {
            loudness.process(buf.data(), count);
            compressor.process(buf.data(), count);
            de_esser.process(buf.data(), count);
            shelf.process(buf.data(), count);
            tilt.process(buf.data(), count);
            pressure_suppressor.process(buf.data(), count);
        }
        pitch_shifter.process(buf.data(), count);
        limiter.process(buf.data(), count);

        return buf.data();
    }
};

// ── YIN F0 extractor (no ONNX dependency) ───────────────────────────

// Simple YIN algorithm for fundamental frequency estimation.
// Operates on float audio at a given sample rate.
// Returns F0 in Hz for each hop; 0 = unvoiced.
static std::vector<float> yin_f0(const float* audio, size_t length, int sr,
                                  int hop_samples, float threshold = 0.35f) {
    int min_period = sr / 350;   // ~350 Hz max; avoid octave/harmonic F0 picks
    int max_period = sr / 50;    // ~50 Hz min
    if (max_period <= min_period || length == 0) return {};

    size_t num_frames = (length > static_cast<size_t>(max_period))
        ? (length - max_period) / hop_samples + 1 : 0;
    std::vector<float> f0_out(num_frames, 0.0f);

    std::vector<float> diff(max_period + 1, 0.0f);
    std::vector<float> cmnd(max_period + 1, 0.0f); // cumulative mean normalised difference

    for (size_t frame = 0; frame < num_frames; ++frame) {
        size_t start = frame * hop_samples;
        int W = max_period;
        if (start + W + max_period > length) break;

        // Difference function
        diff[0] = 0.0f;
        for (int tau = 1; tau <= W; ++tau) {
            float sum = 0.0f;
            for (int j = 0; j < W; ++j) {
                float d = audio[start + j] - audio[start + j + tau];
                sum += d * d;
            }
            diff[tau] = sum;
        }

        // Cumulative mean normalised difference
        cmnd[0] = 1.0f;
        float running_sum = 0.0f;
        for (int tau = 1; tau <= W; ++tau) {
            running_sum += diff[tau];
            cmnd[tau] = (running_sum > 0.0f) ? diff[tau] * tau / running_sum : 1.0f;
        }

        // Find global CMND minimum in valid period range.
        // Use conservative threshold (0.80) — VAD already confirmed speech,
        // but truly silent/noise frames (CMND~1.0) should remain unvoiced.
        // Post-YIN interpolation fills remaining gaps from neighboring voiced frames.
        int best_tau = 0;
        float best_cmnd_val = 1.0f;
        for (int tau = min_period; tau <= W; ++tau) {
            if (cmnd[tau] < best_cmnd_val) {
                best_cmnd_val = cmnd[tau];
                best_tau = tau;
            }
        }
        // Only accept if CMND minimum is reasonably low
        if (best_cmnd_val >= 0.80f) best_tau = 0;

        if (best_tau > 0) {
            // Parabolic interpolation for sub-sample accuracy
            float s0 = (best_tau > 0) ? cmnd[best_tau - 1] : cmnd[best_tau];
            float s1 = cmnd[best_tau];
            float s2 = (best_tau < W) ? cmnd[best_tau + 1] : cmnd[best_tau];
            float shift = (s0 - s2) / (2.0f * (s0 - 2.0f * s1 + s2) + 1e-10f);
            float refined_tau = static_cast<float>(best_tau) + clampf(shift, -1.0f, 1.0f);
            f0_out[frame] = static_cast<float>(sr) / refined_tau;
        }
    }

    // Debug: histogram of min CMND for unvoiced frames
    if (num_frames > 0) {
        int unvoiced = 0;
        float min_cmnd_all = 1.0f;
        int bins[5] = {}; // [0..0.2), [0.2..0.4), [0.4..0.6), [0.6..0.8), [0.8..1.0]
        for (size_t frame = 0; frame < num_frames; ++frame) {
            if (f0_out[frame] > 0.0f) continue;
            ++unvoiced;
            // Recompute min cmnd for this frame (fast: we already have the data pattern)
            size_t start = frame * hop_samples;
            if (start + 2 * max_period > length) continue;
            // Re-run CMND to find minimum in valid range
            float best_cmnd = 1.0f;
            // Difference + CMND
            float running = 0.0f;
            for (int tau = 1; tau <= max_period; ++tau) {
                float sum = 0.0f;
                for (int j = 0; j < max_period; ++j) {
                    float d = audio[start + j] - audio[start + j + tau];
                    sum += d * d;
                }
                running += sum;
                float c = (running > 0.0f) ? sum * tau / running : 1.0f;
                if (tau >= min_period && c < best_cmnd) best_cmnd = c;
            }
            if (best_cmnd < min_cmnd_all) min_cmnd_all = best_cmnd;
            int b = std::min(4, static_cast<int>(best_cmnd * 5.0f));
            ++bins[b];
        }
        if (unvoiced > 0) {
            std::fprintf(stderr, "[VC-YIN] unvoiced=%d min_cmnd=%.3f hist=[<0.2:%d <0.4:%d <0.6:%d <0.8:%d >=0.8:%d]\n",
                         unvoiced, min_cmnd_all, bins[0], bins[1], bins[2], bins[3], bins[4]);
        }
    }

    return f0_out;
}

// ── RVC ONNX Pipeline ───────────────────────────────────────────────

} // anonymous namespace

namespace mobmate::vc {

// ONNX RVC inference pipeline.
struct VcRawOutputGainState {
    float target_gain = 1.0f;
    float applied_gain = 1.0f;
    bool resolved = false;

    void reset(float target) {
        target_gain = clampf(target, 0.0f, 3.0f);
        applied_gain = target_gain;
        resolved = false;
    }
};

struct VcOnnxPipeline {
    const OrtApi* api = nullptr;
    OrtEnv* env = nullptr;
    OrtSessionOptions* session_opts = nullptr;

    OrtSession* content_encoder = nullptr;  // ContentVec: audio → features
    OrtSession* vc_model = nullptr;         // RVC: features + F0 → audio

    OrtMemoryInfo* mem_info = nullptr;
    OrtAllocator* allocator = nullptr;

    int model_sr = 16000;        // ContentVec operates at 16kHz
    int output_sr = 48000;       // Model output sample rate (from config)
    int hop_length = 512;        // ContentVec hop length
    int speaker_id = 0;
    float f0_shift_semitones = 0.0f;
    bool loaded = false;
    bool vcclient_format = false;
    std::string content_input_name = "source";
    std::string content_output_name = "embed";
    std::string rvc_output_name = "audio";
    std::string error;

    // Statistics
    std::atomic<int64_t> inference_count{0};
    std::atomic<float>   last_rtf{0.0f};

    ~VcOnnxPipeline() { unload(); }

    int load(const VoiceConversionOptions& opts) {
        auto& loader = ort_loader();
        if (!loader.load()) { error = loader.error; return -1; }
        api = loader.api;

        // Create environment
        OrtStatus* status = api->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "mobmate_vc", &env);
        if (status) { error = "CreateEnv failed"; api->ReleaseStatus(status); return -2; }

        // Session options
        status = api->CreateSessionOptions(&session_opts);
        if (status) { error = "CreateSessionOptions failed"; api->ReleaseStatus(status); return -3; }

        api->SetIntraOpNumThreads(session_opts, 4);
        api->SetInterOpNumThreads(session_opts, 1);
        api->SetSessionGraphOptimizationLevel(session_opts, ORT_ENABLE_ALL);

        // Memory info
        status = api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &mem_info);
        if (status) { error = "CreateCpuMemoryInfo failed"; api->ReleaseStatus(status); return -4; }

        // Allocator
        status = api->GetAllocatorWithDefaultOptions(&allocator);
        if (status) { error = "GetAllocator failed"; api->ReleaseStatus(status); return -5; }

        speaker_id = opts.speaker_id;
        f0_shift_semitones = opts.f0_shift_semitones;

        // Resolve model paths
        std::string base = opts.model_path;
        if (!base.empty() && base.back() != '/' && base.back() != '\\') base += '/';

        std::string ce_file = resolve_model_file(base, opts.content_encoder_file.empty() ? "contentvec.onnx" : opts.content_encoder_file);
        std::string vc_file = resolve_model_file(base, opts.vc_model_file.empty() ? "model.onnx" : opts.vc_model_file);

        // Load ContentVec
#ifdef _WIN32
        auto ce_wide = utf8_to_wide(ce_file);
        status = api->CreateSession(env, ce_wide.c_str(), session_opts, &content_encoder);
#else
        status = api->CreateSession(env, ce_file.c_str(), session_opts, &content_encoder);
#endif
        if (status) {
            const char* msg = api->GetErrorMessage(status);
            error = std::string("ContentVec load failed: ") + (msg ? msg : "unknown");
            api->ReleaseStatus(status);
            return -10;
        }

        // Load RVC model
#ifdef _WIN32
        auto vc_wide = utf8_to_wide(vc_file);
        status = api->CreateSession(env, vc_wide.c_str(), session_opts, &vc_model);
#else
        status = api->CreateSession(env, vc_file.c_str(), session_opts, &vc_model);
#endif
        if (status) {
            const char* msg = api->GetErrorMessage(status);
            error = std::string("RVC model load failed: ") + (msg ? msg : "unknown");
            api->ReleaseStatus(status);
            return -11;
        }

        resolve_io_names();

        // Parse config.json for output sample rate if available
        std::string config_path = base + "config.json";
        FILE* cf = fopen(config_path.c_str(), "r");
        if (cf) {
            char cbuf[4096];
            size_t n = fread(cbuf, 1, sizeof(cbuf) - 1, cf);
            cbuf[n] = '\0';
            fclose(cf);
            output_sr = vc_json_get_int(cbuf, "sampleRate", 48000);
            if (output_sr <= 0) output_sr = vc_json_get_int(cbuf, "sample_rate", 48000);
            hop_length = vc_json_get_int(cbuf, "hopLength", 512);
            if (hop_length <= 0) hop_length = vc_json_get_int(cbuf, "hop_length", 512);
        }
        std::string params_path = base + "params.json";
        FILE* pf = fopen(params_path.c_str(), "r");
        if (pf) {
            char pbuf[4096];
            size_t n = fread(pbuf, 1, sizeof(pbuf) - 1, pf);
            pbuf[n] = '\0';
            fclose(pf);
            output_sr = vc_json_get_int(pbuf, "sample_rate", output_sr);
            if (output_sr <= 0) output_sr = 48000;
        }

        loaded = true;
        return 0;
    }

    void unload() {
        if (api) {
            if (vc_model) { api->ReleaseSession(vc_model); vc_model = nullptr; }
            if (content_encoder) { api->ReleaseSession(content_encoder); content_encoder = nullptr; }
            if (mem_info) { api->ReleaseMemoryInfo(mem_info); mem_info = nullptr; }
            if (session_opts) { api->ReleaseSessionOptions(session_opts); session_opts = nullptr; }
            if (env) { api->ReleaseEnv(env); env = nullptr; }
        }
        loaded = false;
    }

    // Run the full RVC inference pipeline on a chunk of audio (int16, any sample rate).
    // Returns output audio in int16 at output_sr.
    // This is compute-heavy and should NOT run in an audio callback.
    std::vector<int16_t> infer(const int16_t* input, size_t num_samples, int input_sr,
                               VcRawOutputGainState* raw_output_gain = nullptr) {
        if (!loaded || !content_encoder || !vc_model || num_samples == 0)
            return {};

        // ── 1. Resample to 16kHz for ContentVec ──
        std::vector<float> audio16k;
        {
            if (input_sr == 48000) {
                // ContentVec is trained for 16 kHz audio.  For the common live
                // path (48 kHz capture), avoid plain sample picking because it
                // aliases high-frequency consonants into the 16 kHz feature
                // stream and can turn short Japanese commands into wrong phones.
                size_t out_len = (num_samples + 2) / 3;
                audio16k.resize(out_len);
                for (size_t i = 0; i < out_len; ++i) {
                    size_t j = i * 3;
                    double acc = 0.0;
                    double weight = 0.0;
                    for (size_t k = 0; k < 3; ++k) {
                        size_t idx = j + k;
                        if (idx < num_samples) {
                            acc += static_cast<double>(input[idx]);
                            weight += 1.0;
                        }
                    }
                    audio16k[i] = static_cast<float>((weight > 0.0 ? acc / weight : 0.0) * kSampleScale);
                }
            } else {
                double ratio = 16000.0 / input_sr;
                size_t out_len = static_cast<size_t>(num_samples * ratio) + 1;
                audio16k.resize(out_len);
                for (size_t i = 0; i < out_len; ++i) {
                    double src_idx = i / ratio;
                    size_t idx0 = static_cast<size_t>(src_idx);
                    float frac = static_cast<float>(src_idx - idx0);
                    if (idx0 + 1 < num_samples)
                        audio16k[i] = (input[idx0] * (1.0f - frac) + input[idx0 + 1] * frac) * kSampleScale;
                    else if (idx0 < num_samples)
                        audio16k[i] = input[idx0] * kSampleScale;
                    else
                        audio16k[i] = 0.0f;
                }
            }
        }

        const size_t original_audio16k_len = audio16k.size();
        size_t trim_lead16 = 0;
        size_t trim_tail16 = 0;
        size_t rvc_context_lead16 = 0;
        size_t rvc_context_tail16 = 0;
        size_t rvc_active16_without_context = audio16k.size();
        bool has_active_audio16 = false;

        // Trim long leading/trailing silence before ContentVec/RVC, then pad it
        // back after inference. VCClient-style RVC models can collapse weak
        // speech when a short utterance is surrounded by long silence/noise.
        {
            constexpr int kSr16k = 16000;
            constexpr int kWinMs = 20;
            constexpr int kWinSamples = kSr16k * kWinMs / 1000;
            constexpr float kActiveRms = 0.0035f; // about -49 dBFS
            constexpr size_t kLeadMarginSamples = kSr16k * 80 / 1000;
            constexpr size_t kTailMarginSamples = kSr16k * 180 / 1000;
            const size_t n = audio16k.size();
            if (n > static_cast<size_t>(kWinSamples) * 4) {
                size_t first = n;
                size_t last = 0;
                for (size_t start = 0; start < n; start += kWinSamples) {
                    size_t end = std::min(start + static_cast<size_t>(kWinSamples), n);
                    double sum = 0.0;
                    for (size_t i = start; i < end; ++i) sum += audio16k[i] * audio16k[i];
                    float rms = static_cast<float>(std::sqrt(sum / std::max<size_t>(1, end - start)));
                    if (rms >= kActiveRms) {
                        has_active_audio16 = true;
                        first = std::min(first, start);
                        last = end;
                    }
                }
                if (first < last) {
                    size_t trim_start = (first > kLeadMarginSamples) ? (first - kLeadMarginSamples) : 0;
                    // Preserve low-energy final consonants such as Japanese /s/.
                    size_t trim_end = std::min(n, last + kTailMarginSamples);
                    if (trim_start > 0 || trim_end < n) {
                        trim_lead16 = trim_start;
                        trim_tail16 = n - trim_end;
                        std::vector<float> trimmed(audio16k.begin() + static_cast<std::ptrdiff_t>(trim_start),
                                                   audio16k.begin() + static_cast<std::ptrdiff_t>(trim_end));
                        audio16k.swap(trimmed);
                        std::fprintf(stderr, "[VC-RVC] trim: lead16=%zu active16=%zu tail16=%zu\n",
                                     trim_lead16, audio16k.size(), trim_tail16);
                    }
                }
            }
        }

        // Never run the generative RVC decoder on an all-silent/very-low-energy
        // chunk. Some models turn this input into a sustained vowel-like sound.
        if (!has_active_audio16 && original_audio16k_len > 0 && output_sr > 0) {
            const size_t silent_out = static_cast<size_t>(std::llround(
                static_cast<double>(original_audio16k_len) * static_cast<double>(output_sr) / 16000.0));
            std::fprintf(stderr, "[VC-RVC] bypass_inactive_chunk samples16=%zu output=%zu\n",
                         original_audio16k_len, silent_out);
            return std::vector<int16_t>(silent_out, 0);
        }

        rvc_active16_without_context = audio16k.size();
        if (trim_lead16 == 0 && !audio16k.empty()) {
            constexpr size_t kLeadContext16 = 16000 * 120 / 1000;
            audio16k.insert(audio16k.begin(), kLeadContext16, 0.0f);
            rvc_context_lead16 = kLeadContext16;
        }
        if (trim_tail16 == 0 && !audio16k.empty()) {
            constexpr size_t kTailContext16 = 16000 * 120 / 1000;
            audio16k.insert(audio16k.end(), kTailContext16, 0.0f);
            rvc_context_tail16 = kTailContext16;
        }
        if (rvc_context_lead16 > 0 || rvc_context_tail16 > 0) {
            std::fprintf(stderr, "[VC-RVC] context: lead16=%zu tail16=%zu active16=%zu infer16=%zu\n",
                         rvc_context_lead16, rvc_context_tail16,
                         rvc_active16_without_context, audio16k.size());
        }

        auto pad_rvc_output = [&](std::vector<int16_t> active_out) -> std::vector<int16_t> {
            if ((rvc_context_lead16 > 0 || rvc_context_tail16 > 0) && output_sr > 0) {
                size_t lead_context_out = static_cast<size_t>(std::llround(
                    static_cast<double>(rvc_context_lead16) * static_cast<double>(output_sr) / 16000.0));
                if (lead_context_out > 0 && active_out.size() > lead_context_out) {
                    active_out.erase(active_out.begin(), active_out.begin() + static_cast<std::ptrdiff_t>(lead_context_out));
                }
                size_t active_limit = static_cast<size_t>(std::llround(
                    static_cast<double>(rvc_active16_without_context) * static_cast<double>(output_sr) / 16000.0));
                if (active_limit > 0 && active_out.size() > active_limit) {
                    active_out.resize(active_limit);
                }
            }
            if ((trim_lead16 == 0 && trim_tail16 == 0) || original_audio16k_len == 0 || output_sr <= 0)
                return active_out;
            size_t total_out = static_cast<size_t>(std::llround(
                static_cast<double>(original_audio16k_len) * static_cast<double>(output_sr) / 16000.0));
            size_t lead_out = static_cast<size_t>(std::llround(
                static_cast<double>(trim_lead16) * static_cast<double>(output_sr) / 16000.0));
            if (total_out == 0) return active_out;
            if (lead_out > total_out) lead_out = total_out;
            std::vector<int16_t> padded(total_out, 0);
            size_t room = total_out - lead_out;
            size_t copy_len = std::min(room, active_out.size());
            if (copy_len > 0) std::memcpy(padded.data() + lead_out, active_out.data(), copy_len * sizeof(int16_t));
            std::fprintf(stderr, "[VC-RVC] pad: active_out=%zu lead_out=%zu total_out=%zu\n",
                         active_out.size(), lead_out, total_out);
            return padded;
        };

        // ── 1b. RVC input level conditioning + peak limiter ──
        // Keep the temporal envelope intact for ContentVec. Per-window heavy
        // compression made short Japanese vowels collapse into wrong phonemes.
        // Use one global gain for active speech and leave near-silence untouched.
        {
            constexpr int kSr16k = 16000;
            constexpr int kWinMs = 20;
            constexpr int kWinSamples = kSr16k * kWinMs / 1000; // 320 samples
            constexpr float kTargetRms = 0.04f;
            constexpr float kMaxGain = 4.0f;
            constexpr float kMinGain = 1.0f;
            constexpr float kPeakLimit = 0.95f;
            constexpr float kNoiseFloorRms = 0.0030f;            // do not boost near-silence

            size_t n = audio16k.size();
            if (n > 0) {
                // 1. Compute per-window RMS envelope
                size_t num_wins = (n + kWinSamples - 1) / kWinSamples;
                std::vector<float> win_rms(num_wins, 0.0f);
                for (size_t w = 0; w < num_wins; ++w) {
                    size_t start = w * kWinSamples;
                    size_t end = std::min(start + kWinSamples, n);
                    double sum = 0.0;
                    for (size_t i = start; i < end; ++i)
                        sum += audio16k[i] * audio16k[i];
                    win_rms[w] = static_cast<float>(std::sqrt(sum / (end - start)));
                }

                double active_sum = 0.0;
                size_t active_count = 0;
                for (size_t w = 0; w < num_wins; ++w) {
                    if (win_rms[w] < kNoiseFloorRms) continue;
                    size_t start = w * kWinSamples;
                    size_t end = std::min(start + kWinSamples, n);
                    for (size_t i = start; i < end; ++i) {
                        active_sum += audio16k[i] * audio16k[i];
                    }
                    active_count += end - start;
                }
                float active_rms = (active_count > 0)
                    ? static_cast<float>(std::sqrt(active_sum / active_count))
                    : 0.0f;
                float global_gain = 1.0f;
                if (active_rms > 1e-7f) {
                    global_gain = std::max(kMinGain, std::min(kMaxGain, kTargetRms / active_rms));
                }

                // 2. Apply one gain to speech windows, no gain to near-silence.
                std::vector<float> win_gain(num_wins, 1.0f);
                for (size_t w = 0; w < num_wins; ++w) {
                    if (win_rms[w] >= kNoiseFloorRms) {
                        win_gain[w] = global_gain;
                    } else {
                        win_gain[w] = 1.0f; // silence — don't amplify noise
                    }
                }

                // 3. Apply gain. Smooth only transitions to/from silence.
                float pre_peak = 0.0f;
                float smooth_gain = win_gain[0];
                for (size_t i = 0; i < n; ++i) {
                    size_t w = i / kWinSamples;
                    if (w >= num_wins) w = num_wins - 1;
                    float target_g = win_gain[w];
                    float coeff = (target_g > smooth_gain) ? 0.02f : 0.01f;
                    smooth_gain += coeff * (target_g - smooth_gain);
                    float a = std::fabs(audio16k[i]);
                    if (a > pre_peak) pre_peak = a;
                    audio16k[i] *= smooth_gain;
                }

                // 4. Peak limiter — soft clip anything above limit
                float post_peak = 0.0f;
                for (size_t i = 0; i < n; ++i) {
                    if (audio16k[i] > kPeakLimit)
                        audio16k[i] = kPeakLimit;
                    else if (audio16k[i] < -kPeakLimit)
                        audio16k[i] = -kPeakLimit;
                    float a = std::fabs(audio16k[i]);
                    if (a > post_peak) post_peak = a;
                }

                // Debug: overall RMS after compression
                double rms_sum = 0.0;
                for (size_t i = 0; i < n; ++i) rms_sum += audio16k[i] * audio16k[i];
                float rms_post = static_cast<float>(std::sqrt(rms_sum / n));
                std::fprintf(stderr, "[VC-RVC] level: pre_peak=%.4f post_peak=%.4f active_rms=%.4f rms_post=%.4f wins=%zu gain=%.1f range=[%.1f..%.1f]\n",
                             pre_peak, post_peak, active_rms, rms_post, num_wins, global_gain,
                             *std::min_element(win_gain.begin(), win_gain.end()),
                             *std::max_element(win_gain.begin(), win_gain.end()));
            }
        }

        // ── 2. ContentVec inference ──
        // Legacy:   source [1, 1, T] → embed
        // VCClient: audio  [1, T]    → unit12
        int64_t ce_shape_legacy[] = {1, 1, static_cast<int64_t>(audio16k.size())};
        int64_t ce_shape_vcclient[] = {1, static_cast<int64_t>(audio16k.size())};
        int64_t* ce_shape = vcclient_format ? ce_shape_vcclient : ce_shape_legacy;
        size_t ce_rank = vcclient_format ? 2 : 3;
        OrtValue* ce_input = nullptr;
        OrtStatus* st = api->CreateTensorWithDataAsOrtValue(
            mem_info, audio16k.data(), audio16k.size() * sizeof(float),
            ce_shape, ce_rank, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &ce_input);
        if (st || !ce_input) {
            if (st) api->ReleaseStatus(st);
            return {};
        }

        const char* ce_input_names[] = {content_input_name.c_str()};
        const char* ce_output_names[] = {content_output_name.c_str()};
        OrtValue* ce_output = nullptr;
        st = api->Run(content_encoder, nullptr,
                      ce_input_names, &ce_input, 1,
                      ce_output_names, 1, &ce_output);
        api->ReleaseValue(ce_input);
        if (st || !ce_output) {
            if (st) api->ReleaseStatus(st);
            return {};
        }

        // Get content features shape and data
        OrtTensorTypeAndShapeInfo* ce_info = nullptr;
        api->GetTensorTypeAndShape(ce_output, &ce_info);
        size_t ce_dims = 0;
        api->GetDimensionsCount(ce_info, &ce_dims);
        std::vector<int64_t> ce_shape_out(ce_dims);
        api->GetDimensions(ce_info, ce_shape_out.data(), ce_dims);
        api->ReleaseTensorTypeAndShapeInfo(ce_info);

        float* ce_data = nullptr;
        api->GetTensorMutableData(ce_output, reinterpret_cast<void**>(&ce_data));
        int64_t num_content_frames = (ce_dims >= 2) ? ce_shape_out[1] : 0;
        int64_t feat_dim = (ce_dims >= 3) ? ce_shape_out[2] : 256;

        if (num_content_frames <= 0 || !ce_data) {
            api->ReleaseValue(ce_output);
            return {};
        }

        std::fprintf(stderr, "[VC-RVC] infer: input=%zu@%dHz → 16k=%zu samples, ce_frames=%lld feat_dim=%lld\n",
                     num_samples, input_sr, audio16k.size(),
                     (long long)num_content_frames, (long long)feat_dim);

        // ── 3. F0 extraction (YIN on 16kHz audio) ──
        int f0_hop = hop_length; // typically 512 for legacy RVC
        if (vcclient_format && num_content_frames > 0) {
            // VCClient ContentVec-f produces ~20ms frames (hop≈320 at 16kHz).
            // params.json often has no hop_length, so derive it from the actual
            // content frame count; otherwise trailing F0 becomes zero and the
            // converted voice loses model character.
            f0_hop = std::max(1, static_cast<int>(std::llround(
                static_cast<double>(audio16k.size()) / static_cast<double>(num_content_frames))));
        }
        auto f0_raw = yin_f0(audio16k.data(), audio16k.size(), 16000, f0_hop);

        // Adjust F0 length to match content frames
        if (static_cast<int64_t>(f0_raw.size()) < num_content_frames)
            f0_raw.resize(num_content_frames, 0.0f);
        else if (static_cast<int64_t>(f0_raw.size()) > num_content_frames)
            f0_raw.resize(num_content_frames);

        // ── F0 interpolation: fill short unvoiced gaps between voiced frames ──
        // YIN often misses voiced frames in DSP-processed audio (WORLD vocoder
        // alters harmonic structure). Interpolate interior F0 gaps only; keep
        // leading/trailing silence unvoiced.
        {
            int64_t voiced_before = 0;
            for (auto f : f0_raw) if (f > 0.0f) ++voiced_before;

            if (voiced_before > 0) {
                // Find first/last voiced indices
                int64_t first_voiced = -1, last_voiced = -1;
                for (int64_t i = 0; i < static_cast<int64_t>(f0_raw.size()); ++i) {
                    if (f0_raw[i] > 0.0f) { if (first_voiced < 0) first_voiced = i; last_voiced = i; }
                }
                // Interpolate interior gaps only. Do not fill leading/trailing
                // silence with voiced F0; that makes RVC synthesize/collapse
                // around silent ContentVec frames.
                constexpr int64_t MAX_GAP = 2;
                int64_t gap_start = -1;
                for (int64_t i = 0; i < static_cast<int64_t>(f0_raw.size()); ++i) {
                    if (f0_raw[i] <= 0.0f) {
                        if (gap_start < 0) gap_start = i;
                    } else {
                        if (gap_start >= 0) {
                            int64_t gap_len = i - gap_start;
                            if (gap_len <= MAX_GAP && gap_start > 0) {
                                float f0_left = f0_raw[gap_start - 1];
                                float f0_right = f0_raw[i];
                                for (int64_t j = gap_start; j < i; ++j) {
                                    float t = static_cast<float>(j - gap_start + 1) / static_cast<float>(gap_len + 1);
                                    f0_raw[j] = f0_left * (1.0f - t) + f0_right * t;
                                }
                            }
                            gap_start = -1;
                        }
                    }
                }
            }

            int64_t voiced_after = 0;
            float f0_min = 1e9f, f0_max = 0.0f;
            for (auto f : f0_raw) {
                if (f > 0.0f) { ++voiced_after; f0_min = std::min(f0_min, f); f0_max = std::max(f0_max, f); }
            }
            if (voiced_after == 0) f0_min = 0.0f;
            double rms_sum = 0.0;
            for (size_t i = 0; i < audio16k.size(); ++i) rms_sum += audio16k[i] * audio16k[i];
            float rms16k = static_cast<float>(std::sqrt(rms_sum / std::max<size_t>(1, audio16k.size())));
            std::fprintf(stderr, "[VC-RVC] f0: hop=%d voiced=%lld→%lld/%lld f0=[%.0f..%.0f]Hz rms16k=%.4f\n",
                         f0_hop, (long long)voiced_before, (long long)voiced_after,
                         (long long)num_content_frames, f0_min, f0_max, rms16k);
        }

        // Apply pitch shift
        if (std::fabs(f0_shift_semitones) > 0.01f) {
            float ratio = std::pow(2.0f, f0_shift_semitones / 12.0f);
            for (auto& f : f0_raw) {
                if (f > 0.0f) f *= ratio;
            }
        }

        // Coarse F0 (integer MIDI-like pitch for some RVC models)
        std::vector<int64_t> f0_coarse(num_content_frames);
        for (int64_t i = 0; i < num_content_frames; ++i) {
            if (f0_raw[i] > 0.0f) {
                int64_t coarse = static_cast<int64_t>(
                    12.0f * std::log2f(f0_raw[i] / 440.0f) + 69.0f + 0.5f);
                f0_coarse[i] = std::max<int64_t>(1, std::min<int64_t>(255, coarse));
            } else {
                f0_coarse[i] = vcclient_format ? 1 : 0;
            }
        }

        // ── 4. RVC model inference ──
        // Typical RVC v2 inputs:
        //   phone [1, T', 256], phone_lengths [1], pitch [1, T'], pitchf [1, T'],
        //   ds [1] (speaker id), rnd [1, 192, T'] (noise)
        // Output: audio [1, 1, T_out]

        int64_t phone_shape[] = {1, num_content_frames, feat_dim};
        int64_t len_shape[] = {1};
        int64_t pitch_shape[] = {1, num_content_frames};
        int64_t ds_shape[] = {1};
        int64_t rnd_shape[] = {1, 192, num_content_frames};

        int64_t phone_lengths_data[] = {num_content_frames};
        int64_t ds_data[] = {static_cast<int64_t>(speaker_id)};

        if (vcclient_format) {
            // ── VCClient RVC models: ContentVec hop=320@16kHz (20ms/frame)
            // but HiFi-GAN decoder expects 10ms/frame → 2x repeat-interpolate ──
            int64_t rvc_frames = num_content_frames * 2;
            std::vector<float> feats_2x(rvc_frames * feat_dim);
            for (int64_t i = 0; i < num_content_frames; ++i) {
                const float* cur = ce_data + i * feat_dim;
                const float* next = (i + 1 < num_content_frames) ? (ce_data + (i + 1) * feat_dim) : cur;
                float* even = &feats_2x[(i * 2) * feat_dim];
                float* odd  = &feats_2x[(i * 2 + 1) * feat_dim];
                std::memcpy(even, cur, feat_dim * sizeof(float));
                for (int64_t d = 0; d < feat_dim; ++d) {
                    odd[d] = 0.5f * (cur[d] + next[d]);
                }
            }
            std::vector<int64_t> f0c_2x(rvc_frames);
            std::vector<float>   f0r_2x(rvc_frames);
            for (int64_t i = 0; i < num_content_frames; ++i) {
                float f0_next = (i + 1 < num_content_frames) ? f0_raw[i + 1] : f0_raw[i];
                float f0_mid = (f0_raw[i] > 0.0f && f0_next > 0.0f) ? 0.5f * (f0_raw[i] + f0_next) : f0_raw[i];
                f0r_2x[i * 2] = f0_raw[i];
                f0r_2x[i * 2 + 1] = f0_mid;
                f0c_2x[i * 2] = f0_coarse[i];
                if (f0_mid > 0.0f) {
                    f0c_2x[i * 2 + 1] = std::max<int64_t>(1, std::min<int64_t>(255,
                        static_cast<int64_t>(12.0f * std::log2f(f0_mid / 440.0f) + 69.0f + 0.5f)));
                } else {
                    f0c_2x[i * 2 + 1] = vcclient_format ? 1 : 0;
                }
            }

            int64_t rvc_phone_shape[] = {1, rvc_frames, feat_dim};
            int64_t rvc_pitch_shape[] = {1, rvc_frames};
            int64_t len_data[] = {rvc_frames};
            int64_t sid_data[] = {static_cast<int64_t>(speaker_id)};
            OrtValue *t_feats = nullptr, *t_len = nullptr, *t_pitch = nullptr;
            OrtValue *t_pitchf = nullptr, *t_sid = nullptr;

            api->CreateTensorWithDataAsOrtValue(mem_info, feats_2x.data(),
                rvc_frames * feat_dim * sizeof(float),
                rvc_phone_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &t_feats);
            api->CreateTensorWithDataAsOrtValue(mem_info, len_data,
                sizeof(int64_t), len_shape, 1, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_len);
            api->CreateTensorWithDataAsOrtValue(mem_info, f0c_2x.data(),
                rvc_frames * sizeof(int64_t),
                rvc_pitch_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_pitch);
            api->CreateTensorWithDataAsOrtValue(mem_info, f0r_2x.data(),
                rvc_frames * sizeof(float),
                rvc_pitch_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &t_pitchf);
            api->CreateTensorWithDataAsOrtValue(mem_info, sid_data,
                sizeof(int64_t), ds_shape, 1, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_sid);

            const char* rvc_input_names[] = {"feats", "p_len", "pitch", "pitchf", "sid"};
            OrtValue* rvc_inputs[] = {t_feats, t_len, t_pitch, t_pitchf, t_sid};
            const char* rvc_output_names[] = {rvc_output_name.c_str()};
            OrtValue* rvc_output = nullptr;

            st = api->Run(vc_model, nullptr,
                          rvc_input_names, rvc_inputs, 5,
                          rvc_output_names, 1, &rvc_output);

            for (auto* t : {t_feats, t_len, t_pitch, t_pitchf, t_sid})
                if (t) api->ReleaseValue(t);
            api->ReleaseValue(ce_output);

            if (st || !rvc_output) {
                if (st) api->ReleaseStatus(st);
                return {};
            }

            return pad_rvc_output(extract_audio_output(rvc_output, raw_output_gain));
        }

        // Random noise tensor
        std::vector<float> rnd_data(192 * num_content_frames);
        {
            // Simple pseudo-random (deterministic for reproducibility)
            uint32_t seed = 42;
            for (auto& v : rnd_data) {
                seed = seed * 1103515245 + 12345;
                v = (static_cast<float>((seed >> 16) & 0x7FFF) / 32768.0f - 0.5f) * 0.1f;
            }
        }

        // Create tensors
        OrtValue *t_phone = nullptr, *t_lengths = nullptr, *t_pitch = nullptr;
        OrtValue *t_pitchf = nullptr, *t_ds = nullptr, *t_rnd = nullptr;

        api->CreateTensorWithDataAsOrtValue(mem_info, ce_data,
            num_content_frames * feat_dim * sizeof(float),
            phone_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &t_phone);
        api->CreateTensorWithDataAsOrtValue(mem_info, phone_lengths_data,
            sizeof(int64_t), len_shape, 1, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_lengths);
        api->CreateTensorWithDataAsOrtValue(mem_info, f0_coarse.data(),
            num_content_frames * sizeof(int64_t),
            pitch_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_pitch);
        api->CreateTensorWithDataAsOrtValue(mem_info, f0_raw.data(),
            num_content_frames * sizeof(float),
            pitch_shape, 2, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &t_pitchf);
        api->CreateTensorWithDataAsOrtValue(mem_info, ds_data,
            sizeof(int64_t), ds_shape, 1, ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &t_ds);
        api->CreateTensorWithDataAsOrtValue(mem_info, rnd_data.data(),
            rnd_data.size() * sizeof(float),
            rnd_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &t_rnd);

        const char* rvc_input_names[] = {"phone", "phone_lengths", "pitch", "pitchf", "ds", "rnd"};
        OrtValue* rvc_inputs[] = {t_phone, t_lengths, t_pitch, t_pitchf, t_ds, t_rnd};
        const char* rvc_output_names[] = {rvc_output_name.c_str()};
        OrtValue* rvc_output = nullptr;

        st = api->Run(vc_model, nullptr,
                      rvc_input_names, rvc_inputs, 6,
                      rvc_output_names, 1, &rvc_output);

        // Cleanup input tensors
        for (auto* t : {t_phone, t_lengths, t_pitch, t_pitchf, t_ds, t_rnd})
            if (t) api->ReleaseValue(t);
        api->ReleaseValue(ce_output); // releases ce_data memory too

        if (st || !rvc_output) {
            if (st) api->ReleaseStatus(st);
            return {};
        }

        return pad_rvc_output(extract_audio_output(rvc_output, raw_output_gain));
    }

private:
    std::vector<int16_t> extract_audio_output(OrtValue* rvc_output, VcRawOutputGainState* raw_output_gain) {
        if (!rvc_output) return {};
        OrtTensorTypeAndShapeInfo* out_info = nullptr;
        api->GetTensorTypeAndShape(rvc_output, &out_info);
        size_t out_dims = 0;
        api->GetDimensionsCount(out_info, &out_dims);
        std::vector<int64_t> out_shape(out_dims);
        api->GetDimensions(out_info, out_shape.data(), out_dims);
        api->ReleaseTensorTypeAndShapeInfo(out_info);

        float* out_data = nullptr;
        api->GetTensorMutableData(rvc_output, reinterpret_cast<void**>(&out_data));

        int64_t total_out = 1;
        for (auto d : out_shape) total_out *= d;
        {
            std::string shape_str;
            for (size_t i = 0; i < out_shape.size(); ++i) {
                if (i > 0) shape_str += "x";
                shape_str += std::to_string(out_shape[i]);
            }
            std::fprintf(stderr, "[VC-RVC] rvc_output shape=[%s] total=%lld\n",
                         shape_str.c_str(), (long long)total_out);
        }
        if (total_out <= 0 || !out_data) {
            api->ReleaseValue(rvc_output);
            return {};
        }

        std::vector<int16_t> result(total_out);
        if (!raw_output_gain) {
            // Ordinary route: preserve the legacy clamp and truncating conversion.
            for (int64_t i = 0; i < total_out; ++i)
                result[i] = static_cast<int16_t>(clampf(out_data[i], -1.0f, 1.0f) * kSampleMax);
            api->ReleaseValue(rvc_output);
            inference_count.fetch_add(1, std::memory_order_relaxed);
            return result;
        }

        // Raw route: solve once for this session/utterance, then reuse the same
        // scalar for every later chunk. A later ceiling shortage is reported;
        // the scalar is never recomputed and no normalization stage is added.
        constexpr float kRawOutputCeiling = 0.9440609f; // -0.5 dBFS
        float peak_raw = 0.0f;
        if (!raw_output_gain->resolved) {
            for (int64_t i = 0; i < total_out; ++i) {
                if (std::isfinite(out_data[i]))
                    peak_raw = std::max(peak_raw, std::fabs(out_data[i]));
            }
            const float peak_safe_gain = peak_raw > 0.0f
                ? kRawOutputCeiling / peak_raw
                : raw_output_gain->target_gain;
            raw_output_gain->applied_gain = std::min(raw_output_gain->target_gain, peak_safe_gain);
            raw_output_gain->resolved = true;
            std::fprintf(stderr,
                         "[VC-RVC] raw_output_gain target=%.3f applied=%.3f peak_raw=%.6f ceiling_dbfs=-0.5 shortfall=%s\n",
                         raw_output_gain->target_gain, raw_output_gain->applied_gain, peak_raw,
                         raw_output_gain->applied_gain + 1.0e-6f < raw_output_gain->target_gain ? "peak_ceiling" : "none");
            std::fflush(stderr);
        }

        bool ceiling_shortfall = false;
        for (int64_t i = 0; i < total_out; ++i) {
            const float raw = std::isfinite(out_data[i]) ? out_data[i] : 0.0f;
            const float gained = raw * raw_output_gain->applied_gain;
            if (std::fabs(gained) > kRawOutputCeiling) ceiling_shortfall = true;
            const float scaled = clampf(gained, -kRawOutputCeiling, kRawOutputCeiling);
            result[i] = static_cast<int16_t>(std::lrintf(scaled * kSampleMax));
        }
        if (ceiling_shortfall) {
            std::fprintf(stderr,
                         "[VC-RVC] raw_output_gain fixed=%.3f shortfall=later_chunk_peak scalar_reused=1\n",
                         raw_output_gain->applied_gain);
            std::fflush(stderr);
        }

        api->ReleaseValue(rvc_output);
        inference_count.fetch_add(1, std::memory_order_relaxed);
        return result;
    }

    static bool is_absolute_path(const std::string& path) {
        if (path.empty()) return false;
        if (path[0] == '/' || path[0] == '\\') return true;
        return path.size() >= 3 && std::isalpha(static_cast<unsigned char>(path[0])) && path[1] == ':' && (path[2] == '/' || path[2] == '\\');
    }

    static std::string lower_ascii(std::string value) {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return value;
    }

    static bool is_content_encoder_name(const std::string& name) {
        auto n = lower_ascii(name);
        return n.find("contentvec") != std::string::npos || n.find("hubert") != std::string::npos || n.rfind("vec-", 0) == 0;
    }

    static std::string find_rvc_onnx_file(const std::string& base, bool content_encoder) {
        namespace fs = std::filesystem;
        std::error_code ec;
        fs::path root(base.empty() ? "." : base);
        if (!fs::exists(root, ec)) return "";

        std::vector<fs::path> candidates;
        for (const auto& entry : fs::recursive_directory_iterator(root, fs::directory_options::skip_permission_denied, ec)) {
            if (ec) break;
            if (!entry.is_regular_file(ec)) continue;
            auto path = entry.path();
            auto ext = lower_ascii(path.extension().string());
            if (ext != ".onnx") continue;
            auto name = path.filename().string();
            auto lower = lower_ascii(name);
            if (lower.find("rmvpe") != std::string::npos || lower.find("f0") != std::string::npos) continue;
            auto is_content = is_content_encoder_name(name);
            if (content_encoder != is_content) continue;
            candidates.push_back(path);
        }
        if (candidates.empty()) return "";
        std::sort(candidates.begin(), candidates.end(), [](const fs::path& a, const fs::path& b) {
            return a.generic_string().size() < b.generic_string().size();
        });
        return candidates.front().generic_string();
    }

    static std::string resolve_model_file(const std::string& base, const std::string& file) {
        namespace fs = std::filesystem;
        auto path = is_absolute_path(file) ? file : base + file;
        std::error_code ec;
        if (fs::exists(fs::path(path), ec)) return path;
        auto lower = lower_ascii(file);
        if (lower == "contentvec.onnx") {
            auto fallback = find_rvc_onnx_file(base, true);
            if (!fallback.empty()) return fallback;
        } else if (lower == "model.onnx") {
            auto fallback = find_rvc_onnx_file(base, false);
            if (!fallback.empty()) return fallback;
        }
        return path;
    }

    std::vector<std::string> session_names(OrtSession* session, bool inputs) {
        std::vector<std::string> names;
        if (!session || !allocator) return names;
        size_t count = 0;
        OrtStatus* st = inputs ? api->SessionGetInputCount(session, &count) : api->SessionGetOutputCount(session, &count);
        if (st) { api->ReleaseStatus(st); return names; }
        for (size_t i = 0; i < count; ++i) {
            char* name = nullptr;
            st = inputs ? api->SessionGetInputName(session, i, allocator, &name)
                        : api->SessionGetOutputName(session, i, allocator, &name);
            if (st) { api->ReleaseStatus(st); continue; }
            if (name) {
                names.emplace_back(name);
                api->AllocatorFree(allocator, name);
            }
        }
        return names;
    }

    static bool has_name(const std::vector<std::string>& names, const char* target) {
        return std::find(names.begin(), names.end(), std::string(target)) != names.end();
    }

    void resolve_io_names() {
        auto ce_inputs = session_names(content_encoder, true);
        auto ce_outputs = session_names(content_encoder, false);
        auto rvc_inputs = session_names(vc_model, true);
        auto rvc_outputs = session_names(vc_model, false);

        vcclient_format = has_name(ce_inputs, "audio") && has_name(ce_outputs, "unit12")
                       && has_name(rvc_inputs, "feats") && has_name(rvc_inputs, "p_len")
                       && has_name(rvc_inputs, "sid");
        if (vcclient_format) {
            content_input_name = "audio";
            content_output_name = "unit12";
        } else {
            content_input_name = has_name(ce_inputs, "source") ? "source" : (ce_inputs.empty() ? "source" : ce_inputs[0]);
            content_output_name = has_name(ce_outputs, "embed") ? "embed" : (ce_outputs.empty() ? "embed" : ce_outputs[0]);
        }
        if (has_name(rvc_outputs, "audio")) rvc_output_name = "audio";
        else if (!rvc_outputs.empty()) rvc_output_name = rvc_outputs[0];
    }

    // utf8_to_wide is now a free function defined earlier in the file
#ifdef _WIN32
    // (kept for reference – actual implementation is the free function utf8_to_wide above)
#if 0
    static std::wstring utf8_to_wide(const std::string& utf8) {
        if (utf8.empty()) return {};
        int len = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
        if (len <= 0) return {};
        std::wstring wide(len, 0);
        MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, wide.data(), len);
        if (!wide.empty() && wide.back() == L'\0') wide.pop_back();
        return wide;
    }
#endif  // #if 0
#endif  // _WIN32
};

// ── Seed-VC ncnn Pipeline ───────────────────────────────────────────

// SEH wrapper: ncnn_extractor_extract can crash on invalid param (e.g. bad Concat axis)
#ifdef _WIN32
static int seh_ncnn_extract(NcnnApi& api, void* extractor, const char* name, void** out) {
    __try {
        return api.ncnn_extractor_extract(extractor, name, out);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return -9999;
    }
}
#else
static int seh_ncnn_extract(NcnnApi& api, void* extractor, const char* name, void** out) {
    return api.ncnn_extractor_extract(extractor, name, out);
}
#endif

struct SeedVcNcnnPipeline {
    struct NetHandle {
        std::string name;
        void* net = nullptr;
    };

    std::vector<NetHandle> nets;
    bool loaded = false;
    bool vulkan_enabled = false;
    std::string backend = "ncnn";
    std::string error;
    std::vector<float> preset_mel;
    std::vector<float> preset_cond;
    std::vector<float> preset_style;
    std::string preset_condition_mode = "prompt";
    int preset_solver_steps = 1;
    int cfm_total_frames = 16;
    bool cfm_uses_prompt_condition = false;

    std::atomic<int64_t> inference_count{0};
    std::atomic<float>   last_rtf{0.0f};

    static std::string stats_summary(const char* name, const float* data, size_t count) {
        if (!name || !data || count == 0) return std::string(name ? name : "?") + "=empty";
        float mn = data[0];
        float mx = data[0];
        double sum = 0.0;
        double sum_sq = 0.0;
        for (size_t i = 0; i < count; ++i) {
            float v = data[i];
            mn = std::min(mn, v);
            mx = std::max(mx, v);
            sum += static_cast<double>(v);
            sum_sq += static_cast<double>(v) * static_cast<double>(v);
        }
        double mean = sum / static_cast<double>(count);
        double var = std::max(0.0, sum_sq / static_cast<double>(count) - mean * mean);
        std::ostringstream oss;
        oss.setf(std::ios::fixed);
        oss.precision(4);
        oss << name << "[n=" << count << ",min=" << mn << ",max=" << mx
            << ",mean=" << mean << ",std=" << std::sqrt(var) << "]";
        return oss.str();
    }

    ~SeedVcNcnnPipeline() { unload(); }

    bool has_precomputed_preset() const {
        return preset_mel.size() == (80u * 16u) && preset_style.size() == 192u;
    }

    static void fill_mel_condition(const float* audio16k,
                                   int sample_count,
                                   float* mel,
                                   float* cond,
                                   float* style) {
        if (!audio16k || sample_count <= 0 || !mel || !cond || !style) return;
        constexpr int frames = 16;
        constexpr int bins = 80;
        constexpr int nfft = 512;
        constexpr int half = nfft / 2;
        constexpr float pi = 3.14159265358979323846f;
        auto hz_to_mel = [](float hz) { return 2595.0f * std::log10(1.0f + hz / 700.0f); };
        auto mel_to_hz = [](float m) { return 700.0f * (std::pow(10.0f, m / 2595.0f) - 1.0f); };

        float edges[bins + 2];
        float mel_min = hz_to_mel(30.0f);
        float mel_max = hz_to_mel(8000.0f);
        for (int i = 0; i < bins + 2; ++i) {
            float hz = mel_to_hz(mel_min + (mel_max - mel_min) * i / static_cast<float>(bins + 1));
            edges[i] = hz * half / 8000.0f;
        }

        float raw[frames * bins];
        double sum = 0.0;
        double sum_sq = 0.0;
        for (int t = 0; t < frames; ++t) {
            int center = (sample_count <= 1) ? 0 : static_cast<int>((static_cast<double>(t) + 0.5) * sample_count / frames);
            int start = center - nfft / 2;
            float power[half + 1];
            for (int k = 0; k <= half; ++k) {
                double re = 0.0;
                double im = 0.0;
                for (int n = 0; n < nfft; ++n) {
                    int idx = start + n;
                    float x = (idx >= 0 && idx < sample_count) ? audio16k[idx] : 0.0f;
                    float win = 0.5f - 0.5f * std::cos(2.0f * pi * n / static_cast<float>(nfft - 1));
                    double phase = -2.0 * static_cast<double>(pi) * k * n / nfft;
                    re += static_cast<double>(x * win) * std::cos(phase);
                    im += static_cast<double>(x * win) * std::sin(phase);
                }
                power[k] = static_cast<float>(re * re + im * im);
            }
            for (int b = 0; b < bins; ++b) {
                int l = std::max(0, static_cast<int>(std::floor(edges[b])));
                int c = std::max(l + 1, static_cast<int>(std::floor(edges[b + 1])));
                int r = std::min(half, std::max(c + 1, static_cast<int>(std::ceil(edges[b + 2]))));
                double e = 0.0;
                for (int k = l; k <= r; ++k) {
                    double w = (k <= c)
                        ? (static_cast<double>(k - l) / std::max(1, c - l))
                        : (static_cast<double>(r - k) / std::max(1, r - c));
                    if (w > 0.0) e += power[k] * w;
                }
                float v = std::log(std::max(1.0e-8, e));
                raw[b * frames + t] = v;
                sum += v;
                sum_sq += static_cast<double>(v) * v;
            }
        }

        float mean = static_cast<float>(sum / static_cast<double>(frames * bins));
        float var = static_cast<float>(sum_sq / static_cast<double>(frames * bins) - static_cast<double>(mean) * mean);
        float inv_std = 1.0f / std::sqrt(std::max(1.0e-4f, var));
        float bin_mean[bins];
        std::memset(bin_mean, 0, sizeof(bin_mean));
        for (int b = 0; b < bins; ++b) {
            for (int t = 0; t < frames; ++t) {
                float v = (raw[b * frames + t] - mean) * inv_std;
                v = std::max(-4.0f, std::min(4.0f, v));
                mel[b * frames + t] = v;
                cond[b * frames + t] = v;
                bin_mean[b] += v / static_cast<float>(frames);
            }
        }
        for (int i = 0; i < 192; ++i) {
            style[i] = bin_mean[i % bins] * 0.5f;
        }
    }

    static std::string join_path(const std::string& base, const std::string& rel) {
        if (base.empty()) return rel;
        std::filesystem::path rel_path(rel);
        if (rel_path.is_absolute()) return rel;
        char last = base.back();
        if (last == '/' || last == '\\') return base + rel;
        return base + "/" + rel;
    }

    static bool load_float32_file_exact(const std::string& path, size_t count, std::vector<float>& out) {
        out.clear();
        std::ifstream ifs(path, std::ios::binary);
        if (!ifs) return false;
        ifs.seekg(0, std::ios::end);
        std::streamoff bytes = ifs.tellg();
        ifs.seekg(0, std::ios::beg);
        if (bytes != static_cast<std::streamoff>(count * sizeof(float))) return false;
        out.resize(count);
        ifs.read(reinterpret_cast<char*>(out.data()), static_cast<std::streamsize>(bytes));
        if (!ifs) {
            out.clear();
            return false;
        }
        return true;
    }

    int load_one(const VoiceConversionOptions& opts, const char* name, const char* stem) {
        auto& api = ncnn_api();
        std::string param = join_path(opts.model_path, std::string(stem) + ".param");
        std::string bin   = join_path(opts.model_path, std::string(stem) + ".bin");
        if (!std::filesystem::exists(param)) { error = std::string("missing Seed-VC ncnn param: ") + param; return -30; }
        if (!std::filesystem::exists(bin))   { error = std::string("missing Seed-VC ncnn bin: ") + bin; return -31; }

        void* net = api.ncnn_net_create();
        if (!net) { error = std::string("ncnn_net_create failed: ") + name; return -32; }
        ncnn_register_seedvc_pnnx_layers(net);

        if (opts.use_gpu && api.ncnn_net_set_vulkan_device) {
            api.ncnn_net_set_vulkan_device(net, 0);
            vulkan_enabled = true;
        }
        if (opts.use_gpu && api.ncnn_net_get_option && api.ncnn_option_set_use_vulkan_compute) {
            void* opt = api.ncnn_net_get_option(net);
            if (opt) api.ncnn_option_set_use_vulkan_compute(opt, 1);
            vulkan_enabled = true;
        }

        int r = api.ncnn_net_load_param(net, param.c_str());
        if (r != 0) { api.ncnn_net_destroy(net); error = std::string("ncnn load_param failed: ") + name; return -33; }
        r = api.ncnn_net_load_model(net, bin.c_str());
        if (r != 0) { api.ncnn_net_destroy(net); error = std::string("ncnn load_model failed: ") + name; return -34; }

        nets.push_back(NetHandle{name, net});
        return 0;
    }

    int load(const VoiceConversionOptions& opts) {
        auto& api = ncnn_api();
        if (!api.load()) { error = api.error; return -20; }
        backend = std::string("ncnn ") + api.version + (opts.use_gpu ? " vulkan" : " cpu");

        // Read stem names from manifest.json ncnnComponents (if present).
        // Fallback to hardcoded defaults for backward compatibility.
        std::string stem_hubert        = "hubert_large_l18_1s";
        std::string stem_content       = "content_wide_logits_t50";
        std::string stem_length_reg    = "cfm_length_regulator_t16";
        std::string stem_cfm_estimator = "cfm_estimator_t16";
        std::string stem_vocoder       = "bigvgan_mel16";
        std::string preset_style_file  = "style.bin";
        std::string preset_mel_file    = "prompt_mel.bin";
        std::string preset_cond_file   = "prompt_cond.bin";
        bool require_precomputed = false;

        std::string manifest_path = join_path(opts.model_path, "manifest.json");
        if (std::filesystem::exists(manifest_path)) {
            std::ifstream ifs(manifest_path);
            std::string manifest_json((std::istreambuf_iterator<char>(ifs)), std::istreambuf_iterator<char>());
            std::string comps = vc_json_get_object(manifest_json.c_str(), "ncnnComponents");
            if (!comps.empty()) {
                auto get = [&](const char* key, const std::string& fallback) {
                    auto v = vc_json_get_string(comps.c_str(), key, "");
                    return v.empty() ? fallback : v;
                };
                stem_hubert        = get("hubert", stem_hubert);
                stem_content       = get("content", stem_content);
                stem_length_reg    = get("lengthRegulator", stem_length_reg);
                stem_cfm_estimator = get("cfmEstimator", stem_cfm_estimator);
                stem_vocoder       = get("vocoder", stem_vocoder);
            }
            std::string precomputed = vc_json_get_object(manifest_json.c_str(), "precomputed");
            if (!precomputed.empty()) {
                require_precomputed = true;
                auto get = [&](const char* key, const std::string& fallback) {
                    auto v = vc_json_get_string(precomputed.c_str(), key, "");
                    return v.empty() ? fallback : v;
                };
                preset_style_file = get("style", preset_style_file);
                preset_mel_file = get("promptMel", preset_mel_file);
                preset_cond_file = get("promptCond", preset_cond_file);
                preset_condition_mode = get("conditionMode", preset_condition_mode);
                preset_solver_steps = std::max(1, vc_json_get_int(precomputed.c_str(), "solverSteps", preset_solver_steps));
            }
        }

        std::vector<float> loaded_style;
        std::vector<float> loaded_mel;
        std::vector<float> loaded_cond;
        std::string style_path = join_path(opts.model_path, preset_style_file);
        std::string mel_path = join_path(opts.model_path, preset_mel_file);
        std::string cond_path = join_path(opts.model_path, preset_cond_file);
        bool style_ok = load_float32_file_exact(style_path, 192u, loaded_style);
        bool mel_ok = load_float32_file_exact(mel_path, 80u * 16u, loaded_mel);
        bool cond_ok = load_float32_file_exact(cond_path, 512u * 16u, loaded_cond);
        if (!cond_ok) cond_ok = load_float32_file_exact(cond_path, 80u * 16u, loaded_cond);
        if (require_precomputed && (!style_ok || !mel_ok)) {
            error = "Seed-VC ncnn v2 precomputed preset missing or invalid (style=" + style_path + ", promptMel=" + mel_path + ")";
            return -35;
        }
        preset_style = std::move(loaded_style);
        preset_mel = std::move(loaded_mel);
        preset_cond = cond_ok ? std::move(loaded_cond) : std::vector<float>{};
        cfm_total_frames = (stem_cfm_estimator.find("t32") != std::string::npos || preset_cond.size() == (512u * 16u)) ? 32 : 16;
        cfm_uses_prompt_condition = (cfm_total_frames == 32);

        const std::pair<std::string, std::string> required[] = {
            {"hubert",               stem_hubert},
            {"content_wide",         stem_content},
            {"cfm_length_regulator", stem_length_reg},
            {"cfm_estimator",        stem_cfm_estimator},
            {"bigvgan",              stem_vocoder},
        };
        for (const auto& item : required) {
            int ret = load_one(opts, item.first.c_str(), item.second.c_str());
            if (ret != 0) { unload(); return ret; }
        }
        loaded = true;
        return 0;
    }

    void* find_net(const char* name) const {
        for (const auto& h : nets) {
            if (h.name == name) return h.net;
        }
        return nullptr;
    }

    void unload() {
        auto& api = ncnn_api();
        if (api.loaded && api.ncnn_net_destroy) {
            for (auto& h : nets) {
                if (h.net) api.ncnn_net_destroy(h.net);
                h.net = nullptr;
            }
        }
        nets.clear();
        preset_mel.clear();
        preset_cond.clear();
        preset_style.clear();
        preset_condition_mode = "prompt";
        preset_solver_steps = 1;
        cfm_total_frames = 16;
        cfm_uses_prompt_condition = false;
        loaded = false;
    }

    std::vector<int16_t> infer(const int16_t* input, size_t num_samples, int input_sr) {
        if (!loaded || num_samples == 0) return {};

        // HuBERT export is fixed to 1s. Process long inputs in 1s chunks and
        // expand each vocoder result back to the original chunk duration before
        // concatenation; otherwise a short 4608-sample vocoder output is
        // stretched over the whole utterance and becomes effectively unusable.
        const size_t chunk_input_samples = static_cast<size_t>(std::max(1, input_sr > 0 ? input_sr : 16000));
        if (num_samples > chunk_input_samples) {
            std::vector<int16_t> joined;
            joined.reserve(num_samples);
            for (size_t pos = 0; pos < num_samples; pos += chunk_input_samples) {
                size_t n = std::min(chunk_input_samples, num_samples - pos);
                auto chunk = infer(input + pos, n, input_sr);
                if (chunk.empty()) return {};
                size_t base = joined.size();
                joined.resize(base + n);
                if (chunk.size() == n) {
                    std::memcpy(joined.data() + base, chunk.data(), n * sizeof(int16_t));
                } else {
                    double r = static_cast<double>(chunk.size()) / static_cast<double>(n);
                    for (size_t i = 0; i < n; ++i) {
                        double src_idx = static_cast<double>(i) * r;
                        size_t idx0 = static_cast<size_t>(src_idx);
                        float frac = static_cast<float>(src_idx - static_cast<double>(idx0));
                        if (idx0 + 1 < chunk.size()) {
                            joined[base + i] = static_cast<int16_t>(chunk[idx0] * (1.0f - frac) + chunk[idx0 + 1] * frac);
                        } else if (idx0 < chunk.size()) {
                            joined[base + i] = chunk[idx0];
                        } else {
                            joined[base + i] = 0;
                        }
                    }
                }
            }
            return joined;
        }

        auto t0 = std::chrono::steady_clock::now();
        auto& api = ncnn_api();
        const bool debug_stats = std::getenv("MOBMATE_SEEDVC_DEBUG_STATS") != nullptr;
        const bool debug_vocoder_preset = std::getenv("MOBMATE_SEEDVC_DEBUG_VOCODER_PRESET") != nullptr;
        const bool debug_no_cfg = std::getenv("MOBMATE_SEEDVC_DEBUG_NO_CFG") != nullptr;
        const char* debug_vel_scale_env = std::getenv("MOBMATE_SEEDVC_DEBUG_VEL_SCALE");
        const char* debug_vel_bias_env = std::getenv("MOBMATE_SEEDVC_DEBUG_VEL_BIAS");
        const float debug_vel_scale = debug_vel_scale_env ? std::strtof(debug_vel_scale_env, nullptr) : 1.0f;
        const float debug_vel_bias = debug_vel_bias_env ? std::strtof(debug_vel_bias_env, nullptr) : 0.0f;
        const bool debug_vel_affine = (debug_vel_scale_env != nullptr || debug_vel_bias_env != nullptr);
        void* hubert = find_net("hubert");
        void* content_wide = find_net("content_wide");
        void* cfm_lr = find_net("cfm_length_regulator");
        void* cfm_estimator = find_net("cfm_estimator");
        void* bigvgan = find_net("bigvgan");
        if (!input || !hubert || !api.ncnn_extractor_create || !api.ncnn_extractor_destroy ||
            !api.ncnn_extractor_input || !api.ncnn_extractor_extract ||
            !api.ncnn_mat_create_1d_elem || !api.ncnn_mat_create_2d_elem || !api.ncnn_mat_destroy ||
            !api.ncnn_mat_get_data || !api.ncnn_mat_get_w || !api.ncnn_mat_get_h) {
            error = "Seed-VC ncnn HuBERT forward unavailable";
            return {};
        }

        // Model architecture constraints (baked into ncnn export, not configurable via manifest).
        // HuBERT was exported with fixed 1s input at 16kHz.
        const int target_sr = 16000;
        const int target_samples = 16000;
        void* in_mat = api.ncnn_mat_create_1d_elem(target_samples, sizeof(float), 1, nullptr);
        if (!in_mat) { error = "Seed-VC ncnn input mat allocation failed"; return {}; }
        float* in_data = static_cast<float*>(api.ncnn_mat_get_data(in_mat));
        if (!in_data) {
            api.ncnn_mat_destroy(in_mat);
            error = "Seed-VC ncnn input mat data unavailable";
            return {};
        }
        double ratio = (input_sr > 0) ? (static_cast<double>(target_sr) / static_cast<double>(input_sr)) : (1.0 / 3.0);
        double input_sum_sq = 0.0;
        for (int i = 0; i < target_samples; ++i) {
            double src_idx = static_cast<double>(i) / ratio;
            size_t idx0 = static_cast<size_t>(src_idx);
            float frac = static_cast<float>(src_idx - static_cast<double>(idx0));
            if (idx0 + 1 < num_samples) {
                in_data[i] = (input[idx0] * (1.0f - frac) + input[idx0 + 1] * frac) * kSampleScale;
            } else if (idx0 < num_samples) {
                in_data[i] = input[idx0] * kSampleScale;
            } else {
                in_data[i] = 0.0f;
            }
            input_sum_sq += static_cast<double>(in_data[i]) * static_cast<double>(in_data[i]);
        }
        float input_rms = static_cast<float>(std::sqrt(input_sum_sq / static_cast<double>(target_samples)));

        void* ex = api.ncnn_extractor_create(hubert);
        if (!ex) {
            api.ncnn_mat_destroy(in_mat);
            error = "Seed-VC ncnn extractor allocation failed";
            return {};
        }
        int ri = api.ncnn_extractor_input(ex, "in0", in_mat);
        void* hubert_out = nullptr;
        int re = (ri == 0) ? api.ncnn_extractor_extract(ex, "out0", &hubert_out) : -1;
        api.ncnn_extractor_destroy(ex);
        api.ncnn_mat_destroy(in_mat);

        if (ri != 0 || re != 0) {
            if (hubert_out) api.ncnn_mat_destroy(hubert_out);
            error = "Seed-VC ncnn HuBERT forward failed";
            return {};
        }

        if (!content_wide) {
            if (hubert_out) api.ncnn_mat_destroy(hubert_out);
            error = "Seed-VC ncnn HuBERT forward ok; content_wide net unavailable";
            return {};
        }

        int hw = api.ncnn_mat_get_w(hubert_out);
        int hh = api.ncnn_mat_get_h(hubert_out);
        float* hdata = static_cast<float*>(api.ncnn_mat_get_data(hubert_out));
        void* content_in = nullptr;
        if (hw == 1024 && hh > 0 && hdata) {
            content_in = api.ncnn_mat_create_2d_elem(hh, hw, sizeof(float), 1, nullptr);
            float* cdata = static_cast<float*>(api.ncnn_mat_get_data(content_in));
            if (cdata) {
                for (int t = 0; t < hh; ++t) {
                    for (int c = 0; c < hw; ++c) {
                        cdata[c * hh + t] = hdata[t * hw + c];
                    }
                }
            }
        }
        api.ncnn_mat_destroy(hubert_out);
        if (!content_in) {
            error = "Seed-VC ncnn content input reshape failed";
            return {};
        }

        void* cex = api.ncnn_extractor_create(content_wide);
        int ci = cex ? api.ncnn_extractor_input(cex, "in0", content_in) : -1;
        void* content_out = nullptr;
        int ce = (ci == 0) ? api.ncnn_extractor_extract(cex, "out0", &content_out) : -1;
        std::vector<int> wide_tokens;
        if (content_out && api.ncnn_mat_get_data) {
            int cw = api.ncnn_mat_get_w(content_out);
            int ch = api.ncnn_mat_get_h(content_out);
            float* logits = static_cast<float*>(api.ncnn_mat_get_data(content_out));
            if (logits && cw == 11 && ch > 0) {
                wide_tokens.resize(static_cast<size_t>(ch));
                for (int t = 0; t < ch; ++t) {
                    int code = 0;
                    for (int b = 0; b < 11; ++b) {
                        if (logits[t * 11 + b] > 0.0f) code |= (1 << b);
                    }
                    wide_tokens[static_cast<size_t>(t)] = code;
                }
            } else if (logits && ch == 11 && cw > 0) {
                wide_tokens.resize(static_cast<size_t>(cw));
                for (int t = 0; t < cw; ++t) {
                    int code = 0;
                    for (int b = 0; b < 11; ++b) {
                        if (logits[b * cw + t] > 0.0f) code |= (1 << b);
                    }
                    wide_tokens[static_cast<size_t>(t)] = code;
                }
            }
        }
        if (content_out) api.ncnn_mat_destroy(content_out);
        if (cex) api.ncnn_extractor_destroy(cex);
        api.ncnn_mat_destroy(content_in);

        if (ci != 0 || ce != 0) {
            error = "Seed-VC ncnn content_wide forward failed";
            return {};
        }
        if (wide_tokens.empty()) {
            error = "Seed-VC ncnn content_wide token mapping failed";
            return {};
        }
        std::string debug_note;
        if (debug_stats) {
            int token_min = wide_tokens[0];
            int token_max = wide_tokens[0];
            std::vector<int> token_seen;
            token_seen.reserve(wide_tokens.size());
            for (int v : wide_tokens) {
                token_min = std::min(token_min, v);
                token_max = std::max(token_max, v);
                if (std::find(token_seen.begin(), token_seen.end(), v) == token_seen.end()) token_seen.push_back(v);
            }
            debug_note = ", dbg=tokens[min=" + std::to_string(token_min) + ",max=" + std::to_string(token_max) + ",uniq=" + std::to_string(token_seen.size()) + "]";
        }

        if (!cfm_lr) {
            error = "Seed-VC ncnn content tokens ok; CFM length regulator unavailable";
            return {};
        }
        // PNNX generated ncnn wrapper feeds torch.int (int32) as a squeezed 1-D Mat.
        // Feeding int64 makes ncnn Embed read indices incorrectly and LR collapses near zero.
        void* token_mat = api.ncnn_mat_create_1d_elem(static_cast<int>(wide_tokens.size()), sizeof(int32_t), 1, nullptr);
        if (!token_mat) {
            if (token_mat) api.ncnn_mat_destroy(token_mat);
            error = "Seed-VC ncnn CFM LR input allocation failed";
            return {};
        }
        int32_t* token_data = static_cast<int32_t*>(api.ncnn_mat_get_data(token_mat));
        if (!token_data) {
            api.ncnn_mat_destroy(token_mat);
            error = "Seed-VC ncnn CFM LR input data unavailable";
            return {};
        }
        for (size_t i = 0; i < wide_tokens.size(); ++i) token_data[i] = static_cast<int32_t>(wide_tokens[i]);

        void* lex = api.ncnn_extractor_create(cfm_lr);
        int li0 = lex ? api.ncnn_extractor_input(lex, "in0", token_mat) : -1;
        void* lr_out = nullptr;
        int le = (li0 == 0) ? api.ncnn_extractor_extract(lex, "out0", &lr_out) : -1;
        int lr_w = lr_out ? api.ncnn_mat_get_w(lr_out) : 0;
        int lr_h = lr_out ? api.ncnn_mat_get_h(lr_out) : 0;
        if (debug_stats && lr_out && api.ncnn_mat_get_data && lr_w > 0 && lr_h > 0) {
            debug_note += ", " + stats_summary("lr", static_cast<const float*>(api.ncnn_mat_get_data(lr_out)), static_cast<size_t>(lr_w) * static_cast<size_t>(lr_h));
        }
        if (debug_stats && cfm_lr) {
            const char* lr_blobs[] = {"1", "3", "4", "5", "16", "17", "out0"};
            for (const char* blob : lr_blobs) {
                void* pex = api.ncnn_extractor_create(cfm_lr);
                int pi = pex ? api.ncnn_extractor_input(pex, "in0", token_mat) : -1;
                void* pout = nullptr;
                int pe = (pi == 0) ? api.ncnn_extractor_extract(pex, blob, &pout) : -1;
                if (pe == 0 && pout && api.ncnn_mat_get_data) {
                    int pw = api.ncnn_mat_get_w(pout);
                    int ph = api.ncnn_mat_get_h(pout);
                    if (pw > 0 && ph > 0) debug_note += ", lr" + std::string(blob) + "=" + std::to_string(pw) + "x" + std::to_string(ph) + ":" + stats_summary("v", static_cast<const float*>(api.ncnn_mat_get_data(pout)), static_cast<size_t>(pw) * static_cast<size_t>(ph));
                } else {
                    debug_note += ", lr" + std::string(blob) + "=err" + std::to_string(pe);
                }
                if (pout) api.ncnn_mat_destroy(pout);
                if (pex) api.ncnn_extractor_destroy(pex);
            }
        }
        if (const char* dump_lr_path = std::getenv("MOBMATE_SEEDVC_DEBUG_DUMP_LR")) {
            if (lr_out && api.ncnn_mat_get_data && lr_w > 0 && lr_h > 0) {
                std::ofstream ofs(dump_lr_path, std::ios::binary);
                const float* lr_data = static_cast<const float*>(api.ncnn_mat_get_data(lr_out));
                if (ofs && lr_data) {
                    ofs.write(reinterpret_cast<const char*>(lr_data), static_cast<std::streamsize>(static_cast<size_t>(lr_w) * static_cast<size_t>(lr_h) * sizeof(float)));
                }
            }
        }
        if (lex) api.ncnn_extractor_destroy(lex);
        api.ncnn_mat_destroy(token_mat);
        if (li0 != 0 || le != 0) {
            if (lr_out) api.ncnn_mat_destroy(lr_out);
            error = "Seed-VC ncnn CFM length regulator forward failed";
            return {};
        }
        if (!cfm_estimator || !lr_out) {
            if (lr_out) api.ncnn_mat_destroy(lr_out);
            error = "Seed-VC ncnn CFM LR ok; CFM estimator unavailable";
            return {};
        }

        const int cfm_frames = cfm_uses_prompt_condition ? 32 : 16;
        void* mel0 = api.ncnn_mat_create_2d_elem(cfm_frames, 80, sizeof(float), 1, nullptr);
        void* cond0 = api.ncnn_mat_create_2d_elem(cfm_frames, 80, sizeof(float), 1, nullptr);
        void* ylen = api.ncnn_mat_create_1d_elem(1, cfm_uses_prompt_condition ? sizeof(int32_t) : sizeof(float), 1, nullptr);
        void* time = api.ncnn_mat_create_1d_elem(1, sizeof(float), 1, nullptr);
        void* style = api.ncnn_mat_create_1d_elem(192, sizeof(float), 1, nullptr);
        if (!mel0 || !cond0 || !ylen || !time || !style) {
            if (mel0) api.ncnn_mat_destroy(mel0);
            if (cond0) api.ncnn_mat_destroy(cond0);
            if (ylen) api.ncnn_mat_destroy(ylen);
            if (time) api.ncnn_mat_destroy(time);
            if (style) api.ncnn_mat_destroy(style);
            api.ncnn_mat_destroy(lr_out);
            error = "Seed-VC ncnn CFM estimator input allocation failed";
            return {};
        }
        float* mel_data = static_cast<float*>(api.ncnn_mat_get_data(mel0));
        float* cond_data = static_cast<float*>(api.ncnn_mat_get_data(cond0));
        float* style_data = static_cast<float*>(api.ncnn_mat_get_data(style));
        if (!mel_data || !cond_data || !style_data) {
            api.ncnn_mat_destroy(mel0);
            api.ncnn_mat_destroy(cond0);
            api.ncnn_mat_destroy(ylen);
            api.ncnn_mat_destroy(time);
            api.ncnn_mat_destroy(style);
            api.ncnn_mat_destroy(lr_out);
            error = "Seed-VC ncnn CFM estimator input data unavailable";
            return {};
        }
        const bool use_cfm_solver = has_precomputed_preset() && preset_solver_steps > 1;
        if (use_cfm_solver) {
            // CFM inference starts from noise. Use deterministic pseudo-noise for
            // repeatable package builds/tests. prompt_x is zero for styleOnly;
            // prompt mode keeps the packaged prompt as conditioning.
            uint32_t s = 0x6d6f626du;
            for (int i = 0; i < cfm_frames * 80; ++i) {
                s = 1664525u * s + 1013904223u;
                float u = (static_cast<float>((s >> 8) & 0x00ffffffu) + 1.0f) / 16777217.0f;
                s = 1664525u * s + 1013904223u;
                float v = (static_cast<float>((s >> 8) & 0x00ffffffu) + 1.0f) / 16777217.0f;
                mel_data[i] = std::sqrt(-2.0f * std::log(std::max(1.0e-7f, u))) * std::cos(6.28318530718f * v);
                cond_data[i] = 0.0f;
            }
            if (cfm_uses_prompt_condition) {
                for (int b = 0; b < 80; ++b) {
                    for (int f = 0; f < 16; ++f) cond_data[b * cfm_frames + f] = preset_mel[b * 16 + f];
                }
            }
            if (preset_condition_mode != "styleOnly" && preset_condition_mode != "style-only") {
                if (!cfm_uses_prompt_condition) {
                    const std::vector<float>& cond_src = (preset_cond.size() == (80u * 16u)) ? preset_cond : preset_mel;
                    std::memcpy(cond_data, cond_src.data(), cond_src.size() * sizeof(float));
                }
            }
            std::memcpy(style_data, preset_style.data(), preset_style.size() * sizeof(float));
        } else if (has_precomputed_preset()) {
            if (preset_condition_mode == "styleOnly" || preset_condition_mode == "style-only") {
                fill_mel_condition(in_data, target_samples, mel_data, cond_data, style_data);
            } else {
                std::memcpy(mel_data, preset_mel.data(), preset_mel.size() * sizeof(float));
                const std::vector<float>& cond_src = (preset_cond.size() == (80u * 16u)) ? preset_cond : preset_mel;
                std::memcpy(cond_data, cond_src.data(), cond_src.size() * sizeof(float));
            }
            std::memcpy(style_data, preset_style.data(), preset_style.size() * sizeof(float));
        } else {
            fill_mel_condition(in_data, target_samples, mel_data, cond_data, style_data);
        }
        if (cfm_uses_prompt_condition) {
            *static_cast<int32_t*>(api.ncnn_mat_get_data(ylen)) = cfm_frames;
        } else {
            *static_cast<float*>(api.ncnn_mat_get_data(ylen)) = static_cast<float>(cfm_frames);
        }
        *static_cast<float*>(api.ncnn_mat_get_data(time)) = 0.5f;

        void* cfm_cond_in = lr_out;
        if (cfm_uses_prompt_condition) {
            // T32 estimator cond is [frames=32, channels=512] in ncnn 2d layout: w=512, h=32.
            void* joined = api.ncnn_mat_create_2d_elem(512, 32, sizeof(float), 1, nullptr);
            float* jd = joined ? static_cast<float*>(api.ncnn_mat_get_data(joined)) : nullptr;
            const float* sd = lr_out ? static_cast<const float*>(api.ncnn_mat_get_data(lr_out)) : nullptr;
            if (!joined || !jd || !sd) {
                if (joined) api.ncnn_mat_destroy(joined);
                api.ncnn_mat_destroy(mel0); api.ncnn_mat_destroy(cond0); api.ncnn_mat_destroy(ylen); api.ncnn_mat_destroy(time); api.ncnn_mat_destroy(style); api.ncnn_mat_destroy(lr_out);
                error = "Seed-VC ncnn T32 condition allocation failed";
                return {};
            }
            std::fill(jd, jd + 32 * 512, 0.0f);
            if (preset_cond.size() == (512u * 16u)) {
                for (int c = 0; c < 512; ++c) for (int f = 0; f < 16; ++f) jd[f * 512 + c] = preset_cond[c * 16 + f];
            }
            if (lr_w >= 512 && lr_h >= 16) {
                if (preset_cond.size() != (512u * 16u)) {
                    for (int f = 0; f < 16; ++f) for (int c = 0; c < 512; ++c) jd[f * 512 + c] = sd[f * lr_w + c];
                }
                for (int f = 0; f < 16; ++f) for (int c = 0; c < 512; ++c) jd[(16 + f) * 512 + c] = sd[f * lr_w + c];
            } else {
                if (preset_cond.size() != (512u * 16u)) {
                    for (int c = 0; c < std::min(512, lr_h); ++c) for (int f = 0; f < std::min(16, lr_w); ++f) jd[f * 512 + c] = sd[c * lr_w + f];
                }
                for (int c = 0; c < std::min(512, lr_h); ++c) for (int f = 0; f < std::min(16, lr_w); ++f) jd[(16 + f) * 512 + c] = sd[c * lr_w + f];
            }
            cfm_cond_in = joined;
        }

        // Diagnostic: lr_out shape/format
        int lr_dims = lr_out ? api.ncnn_mat_get_dims(lr_out) : -1;
        int lr_ep = -1; // elempack not loaded in NcnnApi yet
        size_t lr_es = (lr_out && api.ncnn_mat_get_elemsize) ? api.ncnn_mat_get_elemsize(lr_out) : 0;

        void* est_out = nullptr;
        int ei = 0;
        int ee = 0;
        int ew = 0;
        int eh = 0;
        std::string cfm_probe;
        // T32 v2wrapper export has 5 inputs (x_lens eliminated by mask-always-true patch):
        //   in0=x, in1=prompt_x, in2=t, in3=style, in4=cond
        // T16 original export has 6 inputs:
        //   in0=x, in1=prompt_x, in2=x_lens, in3=t, in4=style, in5=cond
        // run_estimator_ex: run CFM estimator with explicit inputs for CFG support.
        // If null is passed for in_prompt/in_style/in_cond, zeros are used.
        auto run_estimator_ex = [&](float t_value, void* in_x, void* in_prompt,
                                    void* in_style_mat, void* in_cond_mat, void** out) -> int {
            *static_cast<float*>(api.ncnn_mat_get_data(time)) = t_value;
            void* eex = api.ncnn_extractor_create(cfm_estimator);
            int ri = eex ? api.ncnn_extractor_input(eex, "in0", in_x) : -1;
            if (ri == 0) ri = api.ncnn_extractor_input(eex, "in1", in_prompt);
            if (cfm_uses_prompt_condition) {
                // T32: 5 inputs (no x_lens)
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in2", time);
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in3", in_style_mat);
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in4", in_cond_mat);
            } else {
                // T16: 6 inputs
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in2", ylen);
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in3", time);
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in4", in_style_mat);
                if (ri == 0) ri = api.ncnn_extractor_input(eex, "in5", in_cond_mat);
            }
            int re2 = (ri == 0) ? seh_ncnn_extract(api, eex, "out0", out) : -1;
            if (eex) api.ncnn_extractor_destroy(eex);
            ei = ri;
            ee = re2;
            return (ri == 0 && re2 == 0) ? 0 : -1;
        };
        auto run_estimator = [&](float t_value, void** out) -> int {
            return run_estimator_ex(t_value, mel0, cond0, style, cfm_cond_in, out);
        };

        void* vocoder_mel = nullptr;
        if (use_cfm_solver) {
            const int steps = std::min(32, std::max(2, preset_solver_steps));
            const int prompt_len = cfm_uses_prompt_condition ? 16 : 0;
            // CFG rate [0.5, 0.5] for T32 (v2 multi-condition CFG)
            const float cfg_rate0 = (cfm_uses_prompt_condition && !debug_no_cfg) ? 0.5f : 0.0f;
            const float cfg_rate1 = (cfm_uses_prompt_condition && !debug_no_cfg) ? 0.5f : 0.0f;
            const bool use_cfg = (cfg_rate0 > 0.0f || cfg_rate1 > 0.0f);
            std::vector<float> debug_last_velocity;

            // Allocate zero mats for CFG unconditional passes
            void* zero_prompt = nullptr;
            void* zero_style_mat = nullptr;
            void* zero_cond_mat = nullptr;
            if (use_cfg) {
                zero_prompt = api.ncnn_mat_create_2d_elem(cfm_frames, 80, sizeof(float), 1, nullptr);
                if (zero_prompt) std::memset(api.ncnn_mat_get_data(zero_prompt), 0, cfm_frames * 80 * sizeof(float));
                zero_style_mat = api.ncnn_mat_create_1d_elem(static_cast<int>(preset_style.size()), sizeof(float), 1, nullptr);
                if (zero_style_mat) std::memset(api.ncnn_mat_get_data(zero_style_mat), 0, preset_style.size() * sizeof(float));
                int cond_w = cfm_cond_in ? api.ncnn_mat_get_w(cfm_cond_in) : 0;
                int cond_h = cfm_cond_in ? api.ncnn_mat_get_h(cfm_cond_in) : 0;
                if (cond_w > 0 && cond_h > 0) {
                    zero_cond_mat = api.ncnn_mat_create_2d_elem(cond_w, cond_h, sizeof(float), 1, nullptr);
                    if (zero_cond_mat) std::memset(api.ncnn_mat_get_data(zero_cond_mat), 0, static_cast<size_t>(cond_w) * cond_h * sizeof(float));
                }
            }

            // Zero prompt region of x (noise) before solver loop
            for (int b = 0; b < 80; ++b)
                for (int f = 0; f < prompt_len; ++f)
                    mel_data[b * cfm_frames + f] = 0.0f;

            auto cfm_time = [&](int step_index) -> float {
                float linear_t = static_cast<float>(step_index) / static_cast<float>(steps);
                if (!cfm_uses_prompt_condition) return linear_t;
                // v2 CFM uses sway sampling: t_span = 1 - cos(pi/2 * t)
                return 1.0f - std::cos(1.57079632679f * linear_t);
            };
            for (int step = 0; step < steps; ++step) {
                const float t_now = cfm_time(step);
                const float dt = cfm_time(step + 1) - t_now;

                // Determine velocity: with or without CFG
                std::vector<float> velocity;
                if (use_cfg && zero_prompt && zero_style_mat && zero_cond_mat) {
                    // Multi-condition CFG (v2): 3 forward passes
                    // Pass 1: cond_txt_spk (full conditioning)
                    void* out1 = nullptr;
                    if (run_estimator(t_now, &out1) != 0 || !out1) {
                        if (out1) api.ncnn_mat_destroy(out1);
                        break;
                    }
                    // Pass 2: cond_txt (zero prompt_x, zero style, keep cond)
                    void* out2 = nullptr;
                    if (run_estimator_ex(t_now, mel0, zero_prompt, zero_style_mat, cfm_cond_in, &out2) != 0 || !out2) {
                        api.ncnn_mat_destroy(out1);
                        if (out2) api.ncnn_mat_destroy(out2);
                        break;
                    }
                    // Pass 3: uncond (zero prompt_x, zero style, zero cond)
                    void* out3 = nullptr;
                    if (run_estimator_ex(t_now, mel0, zero_prompt, zero_style_mat, zero_cond_mat, &out3) != 0 || !out3) {
                        api.ncnn_mat_destroy(out1);
                        api.ncnn_mat_destroy(out2);
                        if (out3) api.ncnn_mat_destroy(out3);
                        break;
                    }
                    ew = api.ncnn_mat_get_w(out1);
                    eh = api.ncnn_mat_get_h(out1);
                    const float* d1 = static_cast<const float*>(api.ncnn_mat_get_data(out1));
                    const float* d2 = static_cast<const float*>(api.ncnn_mat_get_data(out2));
                    const float* d3 = static_cast<const float*>(api.ncnn_mat_get_data(out3));
                    size_t n = std::min(static_cast<size_t>(cfm_frames * 80),
                                       static_cast<size_t>(std::max(0, ew)) * static_cast<size_t>(std::max(1, eh)));
                    velocity.resize(n);
                    // dphi_dt = (1 + cfg0 + cfg1) * cond_txt_spk - cfg0 * uncond - cfg1 * cond_txt
                    const float w_full = 1.0f + cfg_rate0 + cfg_rate1;
                    for (size_t i = 0; i < n; ++i)
                        velocity[i] = w_full * d1[i] - cfg_rate0 * d3[i] - cfg_rate1 * d2[i];
                    api.ncnn_mat_destroy(out1);
                    api.ncnn_mat_destroy(out2);
                    api.ncnn_mat_destroy(out3);
                } else {
                    // No CFG: single forward pass
                    void* dphi = nullptr;
                    if (run_estimator(t_now, &dphi) != 0 || !dphi) {
                        if (dphi) api.ncnn_mat_destroy(dphi);
                        break;
                    }
                    ew = api.ncnn_mat_get_w(dphi);
                    eh = api.ncnn_mat_get_h(dphi);
                    const float* dphi_data = static_cast<const float*>(api.ncnn_mat_get_data(dphi));
                    size_t n = std::min(static_cast<size_t>(cfm_frames * 80),
                                       static_cast<size_t>(std::max(0, ew)) * static_cast<size_t>(std::max(1, eh)));
                    velocity.assign(dphi_data, dphi_data + n);
                    api.ncnn_mat_destroy(dphi);
                }

                if (velocity.empty()) {
                    ei = -1; ee = -1;
                    break;
                }
                if (debug_vel_affine) {
                    for (float& v : velocity) v = v * debug_vel_scale + debug_vel_bias;
                }
                if (debug_stats) debug_last_velocity = velocity;

                // Euler update: x = x + dt * velocity
                for (size_t i = 0; i < velocity.size(); ++i) mel_data[i] += dt * velocity[i];

                // Re-zero prompt region of x after each step (Python: x[:, :, :prompt_len] = 0)
                for (int b = 0; b < 80; ++b)
                    for (int f = 0; f < prompt_len; ++f)
                        mel_data[b * cfm_frames + f] = 0.0f;
            }

            // Cleanup CFG zero mats
            if (zero_prompt) api.ncnn_mat_destroy(zero_prompt);
            if (zero_style_mat) api.ncnn_mat_destroy(zero_style_mat);
            if (zero_cond_mat) api.ncnn_mat_destroy(zero_cond_mat);
            if (ei == 0 && ee == 0) {
                if (debug_stats && !debug_last_velocity.empty()) {
                    debug_note += ", " + stats_summary("velLast", debug_last_velocity.data(), debug_last_velocity.size());
                }
                if (cfm_uses_prompt_condition) {
                    void* cropped = api.ncnn_mat_create_2d_elem(16, 80, sizeof(float), 1, nullptr);
                    float* cd = cropped ? static_cast<float*>(api.ncnn_mat_get_data(cropped)) : nullptr;
                    if (cropped && cd) {
                        for (int b = 0; b < 80; ++b) for (int f = 0; f < 16; ++f) cd[b * 16 + f] = mel_data[b * cfm_frames + 16 + f];
                        vocoder_mel = cropped;
                    }
                } else {
                    vocoder_mel = mel0;
                }
                ew = 16;
                eh = 80;
            }
        } else {
            run_estimator(0.5f, &est_out);
            ew = est_out ? api.ncnn_mat_get_w(est_out) : 0;
            eh = est_out ? api.ncnn_mat_get_h(est_out) : 0;
            vocoder_mel = est_out;
        }
        void* debug_vocoder_mel = nullptr;
        if (debug_vocoder_preset && has_precomputed_preset()) {
            debug_vocoder_mel = api.ncnn_mat_create_2d_elem(16, 80, sizeof(float), 1, nullptr);
            float* dd = debug_vocoder_mel ? static_cast<float*>(api.ncnn_mat_get_data(debug_vocoder_mel)) : nullptr;
            if (dd) std::memcpy(dd, preset_mel.data(), preset_mel.size() * sizeof(float));
        }
        void* vocoder_input = debug_vocoder_mel ? debug_vocoder_mel : vocoder_mel;
        if (debug_stats) {
            if (vocoder_input && api.ncnn_mat_get_data) {
                int mw = api.ncnn_mat_get_w(vocoder_input);
                int mh = api.ncnn_mat_get_h(vocoder_input);
                if (mw > 0 && mh > 0) debug_note += ", " + stats_summary(debug_vocoder_mel ? "melPreset" : "melVc", static_cast<const float*>(api.ncnn_mat_get_data(vocoder_input)), static_cast<size_t>(mw) * static_cast<size_t>(mh));
            }
            if (!preset_mel.empty()) debug_note += ", " + stats_summary("promptMel", preset_mel.data(), preset_mel.size());
            if (!preset_style.empty()) debug_note += ", " + stats_summary("style", preset_style.data(), preset_style.size());
        }
        if (!use_cfm_solver && ei == 0 && ee != 0) {
            const char* probes[] = {"21", "22", "28", "80", "83", "84", "86", "88", "89", "90", "150", "151", "200", "202", "203"};
            std::string last_ok = "none";
            std::string fail = "none";
            std::string shapes;
            int fail_code = 0;
            for (const char* blob : probes) {
                void* pex = api.ncnn_extractor_create(cfm_estimator);
                int pi = pex ? api.ncnn_extractor_input(pex, "in0", mel0) : -1;
                if (pi == 0) pi = api.ncnn_extractor_input(pex, "in1", cond0);
                if (cfm_uses_prompt_condition) {
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in2", time);
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in3", style);
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in4", lr_out);
                } else {
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in2", ylen);
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in3", time);
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in4", style);
                    if (pi == 0) pi = api.ncnn_extractor_input(pex, "in5", lr_out);
                }
                void* pout = nullptr;
                int pe = (pi == 0) ? seh_ncnn_extract(api, pex, blob, &pout) : pi;
                if (pout && pe == 0 && shapes.size() < 512) {
                    int pdims = api.ncnn_mat_get_dims ? api.ncnn_mat_get_dims(pout) : -1;
                    int pw = api.ncnn_mat_get_w ? api.ncnn_mat_get_w(pout) : 0;
                    int ph = api.ncnn_mat_get_h ? api.ncnn_mat_get_h(pout) : 1;
                    int pc = (api.ncnn_mat_get_c && pdims >= 3) ? api.ncnn_mat_get_c(pout) : 1;
                    shapes += std::string(blob) + "=" + std::to_string(pdims) + ":" + std::to_string(pw) + "x" + std::to_string(ph) + "x" + std::to_string(pc) + ";";
                }
                if (pout) api.ncnn_mat_destroy(pout);
                if (pex) api.ncnn_extractor_destroy(pex);
                if (pe == 0) {
                    last_ok = blob;
                } else {
                    fail = blob;
                    fail_code = pe;
                    break;
                }
            }
            cfm_probe = ", probeLastOk=" + last_ok + ", probeFail=" + fail + ", probeCode=" + std::to_string(fail_code) + ", probeShapes=" + shapes;
        }
        if (!use_cfm_solver) api.ncnn_mat_destroy(mel0);
        api.ncnn_mat_destroy(cond0);
        api.ncnn_mat_destroy(ylen);
        api.ncnn_mat_destroy(time);
        api.ncnn_mat_destroy(style);
        if (cfm_cond_in && cfm_cond_in != lr_out) api.ncnn_mat_destroy(cfm_cond_in);
        api.ncnn_mat_destroy(lr_out);
        if (ei != 0 || ee != 0) {
            if (est_out) api.ncnn_mat_destroy(est_out);
            if (use_cfm_solver && mel0) api.ncnn_mat_destroy(mel0);
            if (use_cfm_solver && cfm_uses_prompt_condition && vocoder_mel) api.ncnn_mat_destroy(vocoder_mel);
            error = "Seed-VC ncnn CFM estimator forward failed (input=" + std::to_string(ei) + ", extract=" + std::to_string(ee) + ", custom=" + std::to_string(g_seedvc_ncnn_custom_layer_active.load(std::memory_order_relaxed)) +
                    ", lr_dims=" + std::to_string(lr_dims) + ", lr_w=" + std::to_string(lr_w) + "x" + std::to_string(lr_h) + ", lr_ep=" + std::to_string(lr_ep) + ", lr_es=" + std::to_string(lr_es) + ", in5=" + std::string(cfm_uses_prompt_condition ? "prompt+lr" : "lr_out") + cfm_probe + ")";
            return {};
        }
        if (!bigvgan || !vocoder_input) {
            if (est_out) api.ncnn_mat_destroy(est_out);
            if (debug_vocoder_mel) api.ncnn_mat_destroy(debug_vocoder_mel);
            if (use_cfm_solver && mel0) api.ncnn_mat_destroy(mel0);
            if (use_cfm_solver && cfm_uses_prompt_condition && vocoder_mel) api.ncnn_mat_destroy(vocoder_mel);
            error = "Seed-VC ncnn HuBERT+content+CFM LR+estimator ok (tokens=" + std::to_string(wide_tokens.size()) + ", lr=" + std::to_string(lr_w) + "x" + std::to_string(lr_h) + ", est=" + std::to_string(ew) + "x" + std::to_string(eh) + "); BigVGAN unavailable";
            return {};
        }

        void* vex = api.ncnn_extractor_create(bigvgan);
        int vi = vex ? api.ncnn_extractor_input(vex, "in0", vocoder_input) : -1;
        void* voc_out = nullptr;
        int ve = (vi == 0) ? seh_ncnn_extract(api, vex, "out0", &voc_out) : -1;
        int vw = voc_out ? api.ncnn_mat_get_w(voc_out) : 0;
        int vh = voc_out ? api.ncnn_mat_get_h(voc_out) : 0;
        int vdims = voc_out && api.ncnn_mat_get_dims ? api.ncnn_mat_get_dims(voc_out) : 0;
        int vc = (voc_out && api.ncnn_mat_get_c && vdims >= 3) ? api.ncnn_mat_get_c(voc_out) : 1;
        std::vector<int16_t> voc_pcm;
        float voc_peak = 0.0f;
        float voc_rms = 0.0f;
        float voc_gain = 1.0f;
        if (voc_out && ve == 0 && api.ncnn_mat_get_data && vw > 0) {
            size_t sample_count = static_cast<size_t>(vw) * static_cast<size_t>(std::max(1, vh)) * static_cast<size_t>(std::max(1, vc));
            const float* samples = static_cast<const float*>(api.ncnn_mat_get_data(voc_out));
            if (samples && sample_count > 0) {
                double sum_sq = 0.0;
                for (size_t i = 0; i < sample_count; ++i) {
                    voc_peak = std::max(voc_peak, std::fabs(samples[i]));
                    sum_sq += static_cast<double>(samples[i]) * static_cast<double>(samples[i]);
                }
                voc_rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(sample_count)));
                if (voc_rms > 1.0e-6f) {
                    float target_rms = std::min(0.12f, std::max(0.03f, input_rms));
                    constexpr float min_gain = 0.05f;
                    constexpr float max_gain = 4.0f;
                    voc_gain = std::min(max_gain, std::max(min_gain, target_rms / voc_rms));
                }
                constexpr float seedvc_output_gain = 1.0f;
                voc_pcm.resize(sample_count);
                for (size_t i = 0; i < sample_count; ++i) {
                    float s = std::max(-0.98f, std::min(0.98f, samples[i] * voc_gain * seedvc_output_gain));
                    voc_pcm[i] = static_cast<int16_t>(std::lrintf(s * 32767.0f));
                }
            }
        }
        if (voc_out) api.ncnn_mat_destroy(voc_out);
        if (vex) api.ncnn_extractor_destroy(vex);
        if (debug_vocoder_mel) api.ncnn_mat_destroy(debug_vocoder_mel);
        if (est_out) api.ncnn_mat_destroy(est_out);
        if (use_cfm_solver && mel0) api.ncnn_mat_destroy(mel0);
        if (use_cfm_solver && cfm_uses_prompt_condition && vocoder_mel && vocoder_mel != mel0) api.ncnn_mat_destroy(vocoder_mel);
        if (vi != 0 || ve != 0) {
            error = "Seed-VC ncnn BigVGAN forward failed (input=" + std::to_string(vi) + ", extract=" + std::to_string(ve) + ", est=" + std::to_string(ew) + "x" + std::to_string(eh) + ")";
            return {};
        }
        if (voc_pcm.empty()) {
            error = "Seed-VC ncnn BigVGAN produced empty audio (voc=" + std::to_string(vdims) + ":" + std::to_string(vw) + "x" + std::to_string(vh) + ")";
            return {};
        }

            error = "Seed-VC ncnn full pipeline ok (tokens=" + std::to_string(wide_tokens.size()) + ", lr=" + std::to_string(lr_w) + "x" + std::to_string(lr_h) + ", est=" + std::to_string(ew) + "x" + std::to_string(eh) + ", voc=" + std::to_string(vdims) + ":" + std::to_string(vw) + "x" + std::to_string(vh) + ", preset=" + std::to_string(has_precomputed_preset() ? 1 : 0) + ", presetMode=" + preset_condition_mode + ", vocoderPreset=" + std::to_string(debug_vocoder_mel ? 1 : 0) + ", peak=" + std::to_string(voc_peak) + ", rms=" + std::to_string(voc_rms) + ", inputRms=" + std::to_string(input_rms) + ", normGain=" + std::to_string(voc_gain) + ", outGain=1.000000, samples=" + std::to_string(voc_pcm.size()) + debug_note + ")";
        auto t1 = std::chrono::steady_clock::now();
        float elapsed = std::chrono::duration<float>(t1 - t0).count();
        float audio_sec = (input_sr > 0) ? (static_cast<float>(num_samples) / static_cast<float>(input_sr)) : 0.0f;
        last_rtf.store(audio_sec > 0.0f ? elapsed / audio_sec : elapsed, std::memory_order_relaxed);
        inference_count.fetch_add(1, std::memory_order_relaxed);
        return voc_pcm;
    }
};

static bool vc_apply_loaded_inference(VcOnnxPipeline* onnx,
                                      SeedVcNcnnPipeline* seedvc,
                                      int16_t* samples,
                                      size_t num_samples,
                                      int sr,
                                      VcRawOutputGainState* raw_output_gain = nullptr) {
#ifdef MOBMATE_VC_BOUNDARY_TEST
    return vc_boundary_test_convert(samples, num_samples, sr);
#endif
    if (!samples || num_samples == 0) return false;
    std::vector<int16_t> vc_out;
    bool seedvc_applied = false;
    if (onnx && onnx->loaded) {
        vc_out = onnx->infer(samples, num_samples, sr, raw_output_gain);
    } else if (seedvc && seedvc->loaded) {
        vc_out = seedvc->infer(samples, num_samples, sr);
        seedvc_applied = !vc_out.empty();
    }
    if (vc_out.empty()) return false;

    std::fprintf(stderr, "[VC-RVC] apply: vc_out=%zu vs input=%zu (ratio=%.3f)\n",
                 vc_out.size(), num_samples,
                 static_cast<double>(vc_out.size()) / static_cast<double>(num_samples));

    std::vector<int16_t> resampled;
    const int16_t* src = vc_out.data();
    if (vc_out.size() == num_samples) {
        src = vc_out.data();
    } else {
        resampled.resize(num_samples);
        double ratio = static_cast<double>(vc_out.size()) / static_cast<double>(num_samples);
        for (size_t i = 0; i < num_samples; ++i) {
            double src_idx = static_cast<double>(i) * ratio;
            size_t idx0 = static_cast<size_t>(src_idx);
            float frac = static_cast<float>(src_idx - static_cast<double>(idx0));
            if (idx0 + 1 < vc_out.size()) {
                resampled[i] = static_cast<int16_t>(vc_out[idx0] * (1.0f - frac) + vc_out[idx0 + 1] * frac);
            } else if (idx0 < vc_out.size()) {
                resampled[i] = vc_out[idx0];
            } else {
                resampled[i] = 0;
            }
        }
        src = resampled.data();
    }

    if (seedvc_applied) {
        // v2 bundles provide precomputed target style/prompt conditions and can
        // run full wet. Legacy v1 bundles lack CAMPPlus/prompt conditions, so
        // keep the previous safe blend for intelligibility.
        const float wet = (seedvc && seedvc->has_precomputed_preset()) ? 1.0f : 0.25f;
        const float dry = 1.0f - wet;
        for (size_t i = 0; i < num_samples; ++i) {
            float mixed = samples[i] * dry + src[i] * wet;
            mixed = std::max(-32767.0f, std::min(32767.0f, mixed));
            samples[i] = static_cast<int16_t>(std::lrintf(mixed));
        }
    } else {
        std::memcpy(samples, src, num_samples * sizeof(int16_t));
    }
    return true;
}

// ── VcAudioState ────────────────────────────────────────────────────

struct VcFrameStats {
    float rms = 0.0f;
    float peak = 0.0f;
};

static VcFrameStats vc_compute_stats(const int16_t* samples, size_t count) {
    VcFrameStats st{};
    if (!samples || count == 0) return st;
    double sum_sq = 0.0;
    for (size_t i = 0; i < count; ++i) {
        float s = std::fabs(samples[i] * kSampleScale);
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
        if (s > st.peak) st.peak = s;
    }
    st.rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(count)));
    return st;
}

static float vc_calc_zcr_i16(const int16_t* samples, size_t count) {
    if (!samples || count < 2) return 0.0f;
    int crossings = 0;
    int16_t prev = samples[0];
    for (size_t i = 1; i < count; ++i) {
        const int16_t s = samples[i];
        if ((s >= 0 && prev < 0) || (s < 0 && prev >= 0)) ++crossings;
        prev = s;
    }
    return static_cast<float>(crossings) / static_cast<float>(count);
}

static float vc_calc_periodicity_i16(const int16_t* samples, size_t count) {
    if (!samples || count < 160) return 0.0f;
    double best = 0.0;
    const size_t max_lag = std::min<size_t>(200, count / 2);
    for (size_t lag = 40; lag <= max_lag; ++lag) {
        double corr = 0.0, e0 = 0.0, e1 = 0.0;
        for (size_t i = 0; i + lag < count; ++i) {
            const double a = static_cast<double>(samples[i]);
            const double b = static_cast<double>(samples[i + lag]);
            corr += a * b;
            e0 += a * a;
            e1 += b * b;
        }
        best = std::max(best, corr / (std::sqrt(e0 * e1) + 1e-9));
    }
    return static_cast<float>(best);
}

struct VcNoiseProfile {
    static constexpr int MAX_SAMPLES = 200;
    std::vector<float> rms_samples;
    std::vector<float> peak_samples;
    std::vector<float> silero_prob_samples;
    int write_idx = 0;
    int count = 0;
    float noise_floor_rms = 0.0f;
    float noise_rms_stddev = 0.0f;
    float peak_p95 = 0.0f;
    float silero_p90 = 0.0f;
    bool low_gain_mic = false;
    bool ready = false;
    double last_trace_time_ms = 0.0;

    void add_sample(float rms, float peak, float silero_prob) {
        if (rms <= 0.0f || silero_prob < 0.0f) return;
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
        recompute();
    }

    void recompute() {
        int n = static_cast<int>(rms_samples.size());
        if (n < 10) { ready = false; return; }
        ready = true;
        auto percentile = [](std::vector<float> values, double q) -> float {
            if (values.empty()) return 0.0f;
            std::sort(values.begin(), values.end());
            int idx = static_cast<int>(std::ceil(q * values.size())) - 1;
            idx = std::max(0, std::min(idx, static_cast<int>(values.size()) - 1));
            return values[idx];
        };

        // Use lower-percentile noise floor so false-negative speech frames do not
        // raise the VAD open threshold and make normal speech require shouting.
        float rms_p30 = percentile(rms_samples, 0.30);
        float rms_p70 = percentile(rms_samples, 0.70);
        double sum = 0.0, sum_sq = 0.0;
        int robust_n = 0;
        for (float v : rms_samples) {
            if (v <= rms_p70) {
                sum += v;
                sum_sq += static_cast<double>(v) * v;
                robust_n++;
            }
        }
        if (robust_n <= 0) robust_n = n;
        double mean = sum / robust_n;
        double variance = (sum_sq / robust_n) - (mean * mean);
        if (variance < 0.0) variance = 0.0;
        noise_floor_rms = std::min(static_cast<float>(mean), rms_p30);
        noise_rms_stddev = static_cast<float>(std::sqrt(variance));

        auto peaks = peak_samples;
        std::sort(peaks.begin(), peaks.end());
        int p95 = static_cast<int>(std::ceil(0.95 * n)) - 1;
        p95 = std::max(0, std::min(p95, n - 1));
        peak_p95 = peaks[p95];

        auto probs = silero_prob_samples;
        std::sort(probs.begin(), probs.end());
        int p90 = static_cast<int>(std::ceil(0.90 * n)) - 1;
        p90 = std::max(0, std::min(p90, n - 1));
        silero_p90 = probs[p90];
        low_gain_mic = noise_floor_rms < (18.0f * kSampleScale) && peak_p95 < (1800.0f * kSampleScale);
    }

    bool should_add_sample(float rms, float peak, float silero_prob) const {
        if (rms <= 0.0f || silero_prob < 0.0f) return false;
        // Avoid poisoning the noise profile with missed speech/laugh bursts.
        if (silero_prob >= 0.08f) return false;
        if (!ready) {
            return rms <= (420.0f * kSampleScale) && peak <= (1600.0f * kSampleScale);
        }
        float max_noise_rms = noise_floor_rms + std::max(260.0f * kSampleScale, noise_rms_stddev * 2.0f);
        float max_noise_peak = peak_p95 + std::max(900.0f * kSampleScale, noise_rms_stddev * 3.0f);
        max_noise_rms = std::max(max_noise_rms, 420.0f * kSampleScale);
        max_noise_peak = std::max(max_noise_peak, 1600.0f * kSampleScale);
        return rms <= max_noise_rms && peak <= max_noise_peak;
    }
};

// ── RT-RVC: Real-time RVC streaming with SOLA crossfade ─────────────

// SOLA (Similarity Overlap-Add): find best overlap offset via cross-correlation,
// then apply linear crossfade. Returns crossfaded overlap region.
static std::vector<int16_t> sola_crossfade(
    const int16_t* prev_tail, size_t tail_len,
    const int16_t* new_head, size_t new_len,
    size_t overlap_samples)
{
    if (tail_len == 0 || overlap_samples == 0)
        return {};
    size_t ov = std::min(overlap_samples, std::min(tail_len, new_len));
    // Cross-correlation to find best alignment (search ±half overlap)
    size_t search = std::min(ov, new_len - ov);
    search = std::min(search, ov / 2 + 1);
    int best_offset = 0;
    double best_corr = -1e30;
    for (size_t s = 0; s < search; ++s) {
        double corr = 0.0;
        for (size_t i = 0; i < ov; ++i) {
            corr += static_cast<double>(prev_tail[tail_len - ov + i]) *
                    static_cast<double>(new_head[s + i]);
        }
        if (corr > best_corr) {
            best_corr = corr;
            best_offset = static_cast<int>(s);
        }
    }
    // Linear crossfade at best offset
    std::vector<int16_t> result(ov);
    for (size_t i = 0; i < ov; ++i) {
        float alpha = static_cast<float>(i) / static_cast<float>(ov);
        float a = static_cast<float>(prev_tail[tail_len - ov + i]);
        float b = static_cast<float>(new_head[best_offset + i]);
        result[i] = static_cast<int16_t>(clampf(a * (1.0f - alpha) + b * alpha, -32768.0f, 32767.0f));
    }
    return result;
}

// Resample int16 audio using linear interpolation.
static std::vector<int16_t> resample_linear_i16(
    const int16_t* src, size_t src_len, int src_sr, int dst_sr)
{
    if (src_sr == dst_sr || src_len == 0 || src_sr <= 0 || dst_sr <= 0)
        return {src, src + src_len};
    double ratio = static_cast<double>(dst_sr) / src_sr;
    size_t dst_len = std::max<size_t>(1, static_cast<size_t>(std::llround(static_cast<double>(src_len) * ratio)));
    std::vector<int16_t> dst(dst_len);
    for (size_t i = 0; i < dst_len; ++i) {
        double si = i / ratio;
        size_t i0 = static_cast<size_t>(si);
        float frac = static_cast<float>(si - i0);
        if (i0 + 1 < src_len)
            dst[i] = static_cast<int16_t>(clampf(
                src[i0] * (1.0f - frac) + src[i0 + 1] * frac, -32768.0f, 32767.0f));
        else if (i0 < src_len)
            dst[i] = src[i0];
        else
            dst[i] = 0;
    }
    return dst;
}

struct VcLaughAnalysis {
    bool laugh_like = false;
    size_t first_sample = 0;
    size_t last_sample = 0;
    int active_ms = 0;
    int bursts = 0;
    float active_ratio = 0.0f;
    float zcr = 1.0f;
    float max_rms = 0.0f;
};

struct VcAsrGateAnalysis {
    int duration_ms = 0;
    float rms = 0.0f;
    float peak = 0.0f;
    float zcr = 1.0f;
    float periodicity = 0.0f;
    float start_prob = 0.0f;
    float active_ratio = 0.0f;
    float voiced_active_ratio = 0.0f;
    float air_active_ratio = 0.0f;
};

static VcAsrGateAnalysis vc_analyze_asr_gate_audio(const int16_t* samples, size_t num_samples, int sr, float start_prob) {
    VcAsrGateAnalysis a{};
    a.start_prob = start_prob;
    if (!samples || num_samples == 0 || sr <= 0) return a;
    a.duration_ms = static_cast<int>((num_samples * 1000) / static_cast<size_t>(std::max(1, sr)));
    double sum_sq = 0.0;
    int16_t peak_i16 = 0;
    for (size_t i = 0; i < num_samples; ++i) {
        const float s = static_cast<float>(samples[i]);
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
        peak_i16 = std::max<int16_t>(peak_i16, static_cast<int16_t>(std::abs(static_cast<int>(samples[i]))));
    }
    a.rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(num_samples)));
    a.peak = static_cast<float>(peak_i16);
    const size_t analyze_n = std::min(num_samples, static_cast<size_t>(std::max(1, sr)));
    a.zcr = vc_calc_zcr_i16(samples, analyze_n);
    a.periodicity = vc_calc_periodicity_i16(samples, analyze_n);
    const size_t frame_n = static_cast<size_t>(std::max(1, sr / 50)); // 20ms
    size_t frame_count = 0;
    size_t active_count = 0;
    size_t voiced_count = 0;
    size_t air_count = 0;
    for (size_t pos = 0; pos + frame_n <= num_samples; pos += frame_n) {
        double frame_sum_sq = 0.0;
        for (size_t i = 0; i < frame_n; ++i) {
            const double s = static_cast<double>(samples[pos + i]);
            frame_sum_sq += s * s;
        }
        ++frame_count;
        const float frame_rms = static_cast<float>(std::sqrt(frame_sum_sq / static_cast<double>(frame_n)));
        if (frame_rms < 650.0f) continue;
        ++active_count;
        const float frame_zcr = vc_calc_zcr_i16(samples + pos, frame_n);
        const float frame_periodicity = vc_calc_periodicity_i16(samples + pos, frame_n);
        if (frame_periodicity >= 0.18f && frame_zcr < 0.12f) ++voiced_count;
        if (frame_zcr >= 0.075f && frame_periodicity < 0.20f) ++air_count;
    }
    if (frame_count > 0)
        a.active_ratio = static_cast<float>(active_count) / static_cast<float>(frame_count);
    if (active_count > 0) {
        a.voiced_active_ratio = static_cast<float>(voiced_count) / static_cast<float>(active_count);
        a.air_active_ratio = static_cast<float>(air_count) / static_cast<float>(active_count);
    }
    return a;
}

// Task B (2026-07-12): text/audio length consistency gate.
// Hallucinated long sentences appear as unrealistically high char rates for the
// captured speech duration; long exhales sometimes yield 1-2 char interjections.
// Non-literal: only codepoint count vs duration, no text content comparison.
static size_t vc_utf8_codepoint_count(const std::string& s) {
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

static bool vc_asr_gate_rejects_text_rate(const VcAsrGateAnalysis& a, const std::string& text) {
    if (a.duration_ms <= 0 || text.empty()) return false;
    const double dur_sec = static_cast<double>(a.duration_ms) / 1000.0;
    const double chars = static_cast<double>(vc_utf8_codepoint_count(text));
    if (chars / dur_sec > 14.0) return true;              // hallucinated long text
    if (chars <= 2.0 && a.duration_ms > 1500) return true; // long audio, near-empty text
    return false;
}

// Task A (2026-07-12): pseudo-confidence hallucination gate.
// Moonshine ja models lack word-confidence assets, so confidence is estimated
// from multi-pass agreement: the same audio is transcribed twice with different
// zero-padding contexts. Real speech decodes stably (high similarity), while
// hallucinations from breath/noise are unstable across contexts. Non-literal:
// similarity of the two ASR outputs only, no fixed keyword comparison.
static std::vector<uint32_t> vc_utf8_to_codepoints_for_similarity(const std::string& s) {
    std::vector<uint32_t> cps;
    cps.reserve(s.size());
    for (size_t i = 0; i < s.size();) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        uint32_t cp = c;
        size_t len = 1;
        if ((c & 0xF8) == 0xF0 && i + 3 < s.size()) {
            cp = ((c & 0x07u) << 18) | ((s[i+1] & 0x3Fu) << 12) | ((s[i+2] & 0x3Fu) << 6) | (s[i+3] & 0x3Fu);
            len = 4;
        } else if ((c & 0xF0) == 0xE0 && i + 2 < s.size()) {
            cp = ((c & 0x0Fu) << 12) | ((s[i+1] & 0x3Fu) << 6) | (s[i+2] & 0x3Fu);
            len = 3;
        } else if ((c & 0xE0) == 0xC0 && i + 1 < s.size()) {
            cp = ((c & 0x1Fu) << 6) | (s[i+1] & 0x3Fu);
            len = 2;
        }
        i += len;
        // Skip whitespace and common punctuation so pass-to-pass punctuation
        // jitter does not lower the similarity score.
        if (cp <= 0x40 || (cp >= 0x5B && cp <= 0x60) || (cp >= 0x7B && cp <= 0x7F)) continue; // ASCII space/punct/digits keep letters only above
        if (cp == 0x3001 || cp == 0x3002 || cp == 0xFF01 || cp == 0xFF1F || cp == 0xFF0C || cp == 0xFF0E) continue; // 、。！？，．
        cps.push_back(cp);
    }
    return cps;
}

static float vc_text_similarity(const std::string& a, const std::string& b) {
    auto ca = vc_utf8_to_codepoints_for_similarity(a);
    auto cb = vc_utf8_to_codepoints_for_similarity(b);
    if (ca.empty() && cb.empty()) return 1.0f;
    if (ca.empty() || cb.empty()) return 0.0f;
    const size_t n = ca.size(), m = cb.size();
    std::vector<size_t> prev(m + 1), cur(m + 1);
    for (size_t j = 0; j <= m; ++j) prev[j] = j;
    for (size_t i = 1; i <= n; ++i) {
        cur[0] = i;
        for (size_t j = 1; j <= m; ++j) {
            const size_t cost = (ca[i-1] == cb[j-1]) ? 0 : 1;
            cur[j] = std::min({ prev[j] + 1, cur[j-1] + 1, prev[j-1] + cost });
        }
        std::swap(prev, cur);
    }
    const size_t dist = prev[m];
    const size_t max_len = std::max(n, m);
    return 1.0f - static_cast<float>(dist) / static_cast<float>(max_len);
}

static bool vc_asr_gate_rejects_low_confidence(const VcAsrGateAnalysis& a, float asr_confidence) {
    if (asr_confidence < 0.0f) return false; // unavailable -> keep legacy behavior
    if (asr_confidence < 0.35f) return true; // strong hallucination signal
    const bool very_short = a.duration_ms > 0 && a.duration_ms <= 520;
    if (very_short && asr_confidence < 0.55f) return true; // low-confidence interjection
    return false;
}

static bool vc_asr_gate_accepts_nonliteral_text(const VcAsrGateAnalysis& a) {
    const bool very_short = a.duration_ms > 0 && a.duration_ms <= 520;
    const bool voiced = a.periodicity >= 0.18f || a.start_prob >= 0.62f;
    const bool audible = a.rms >= 650.0f || a.peak >= 2200.0f;
    const bool air_like = a.zcr >= 0.075f && a.periodicity < 0.20f && a.start_prob < 0.72f;
    const bool weak_long_hallucination = a.duration_ms >= 900 && a.duration_ms <= 2600 &&
        a.rms < 520.0f && a.peak < 5200.0f;
    const bool low_articulation_hum = a.duration_ms >= 900 && a.duration_ms <= 2400 &&
        a.rms < 900.0f && a.zcr < 0.012f && a.start_prob < 0.88f;
    // A stable ASR hallucination can still be produced by a sigh or tobacco
    // exhale. Reject only when audible frames are dominated by diffuse air and
    // lack the periodic frame ratio seen in articulated speech.
    const bool diffuse_exhale = a.active_ratio >= 0.10f &&
        a.air_active_ratio >= 0.20f && a.voiced_active_ratio < 0.75f;
    // A voiced "hoo" exhale is a fully voiced monotone hum (voiced ratio ~1.0,
    // no air frames, near-zero ZCR, very high periodicity). Articulated speech
    // always contains consonant frames that lower voiced ratio or raise ZCR.
    // Tier 1: pure hum (fully voiced). Tier 2: slightly noisy hum such as a
    // swallow/gulp followed by a voiced sigh. Tier 3 covers breathy reactions
    // whose whole-segment ZCR is raised by the initial consonant but whose
    // active frames remain strongly voiced and contain virtually no air.
    const bool voiced_monotone_hum = a.duration_ms >= 900 && a.duration_ms <= 2600 &&
        a.air_active_ratio <= 0.05f &&
        ((a.voiced_active_ratio >= 0.95f && a.periodicity >= 0.50f && a.zcr < 0.055f && a.active_ratio <= 0.65f) ||
         (a.voiced_active_ratio >= 0.85f && a.periodicity >= 0.60f && a.zcr < 0.070f && a.active_ratio <= 0.55f) ||
         (a.voiced_active_ratio >= 0.82f && a.periodicity >= 0.40f &&
          a.air_active_ratio <= 0.02f && a.active_ratio <= 0.43f));
    const bool weak_short = very_short && (!voiced || !audible || air_like);
    return !weak_short && !weak_long_hallucination && !low_articulation_hum &&
           !diffuse_exhale && !voiced_monotone_hum;
}

static bool vc_asr_gate_accepts_early_acoustic(const VcAsrGateAnalysis& a) {
    // Early release must be conservative: tobacco/exhale bursts can raise Silero
    // probability, so never accept from probability alone. Require voiced shape.
    const bool voiced = a.periodicity >= 0.24f && a.start_prob >= 0.94f;
    const bool confident_voiced = a.periodicity >= 0.20f && a.start_prob >= 0.96f && a.zcr >= 0.015f && a.zcr < 0.065f;
    const bool audible = a.rms >= 1200.0f || a.peak >= 5000.0f;
    const bool air_like = a.zcr >= 0.075f && a.periodicity < 0.30f;
    return (voiced || confident_voiced) && audible && !air_like;
}

static VcLaughAnalysis vc_analyze_laugh_like(const int16_t* samples, size_t num_samples, int sr) {
    VcLaughAnalysis a{};
    if (!samples || num_samples == 0 || sr <= 0) return a;
    const size_t win = static_cast<size_t>(std::max(1, sr / 100)); // 10 ms
    const size_t frames = (num_samples + win - 1) / win;
    std::vector<float> rms(frames, 0.0f);
    for (size_t f = 0; f < frames; ++f) {
        const size_t start = f * win;
        const size_t end = std::min(num_samples, start + win);
        double sum = 0.0;
        for (size_t i = start; i < end; ++i) {
            const float s = static_cast<float>(samples[i]);
            sum += static_cast<double>(s) * static_cast<double>(s);
        }
        rms[f] = static_cast<float>(std::sqrt(sum / static_cast<double>(std::max<size_t>(1, end - start))));
        a.max_rms = std::max(a.max_rms, rms[f]);
    }
    const float active_thr = std::max(900.0f, a.max_rms * 0.20f);
    int active_frames = 0;
    bool in_burst = false;
    bool found_first = false;
    int active_samples = 0;
    int zc = 0;
    int16_t prev = 0;
    bool have_prev = false;
    for (size_t f = 0; f < frames; ++f) {
        const bool active = rms[f] >= active_thr;
        if (active) {
            if (!found_first) {
                a.first_sample = std::min(num_samples, f * win);
                found_first = true;
            }
            ++active_frames;
            if (!in_burst) ++a.bursts;
            const size_t start = f * win;
            const size_t end = std::min(num_samples, start + win);
            a.last_sample = end;
            for (size_t i = start; i < end; ++i) {
                const int16_t s = samples[i];
                if (have_prev && ((prev < 0 && s >= 0) || (prev >= 0 && s < 0))) ++zc;
                prev = s;
                have_prev = true;
                ++active_samples;
            }
        }
        in_burst = active;
    }
    a.active_ms = active_frames * 10;
    a.active_ratio = static_cast<float>(active_frames) / static_cast<float>(std::max<size_t>(1, frames));
    a.zcr = active_samples > 0 ? static_cast<float>(zc) / static_cast<float>(active_samples) : 1.0f;
    a.laugh_like = (a.bursts >= 2 && a.bursts <= 10 && a.active_ms >= 120 && a.active_ms <= 2000 &&
                    a.active_ratio >= 0.05f && a.active_ratio <= 0.75f &&
                    a.max_rms >= 1800.0f && a.zcr >= 0.010f && a.zcr <= 0.18f);
    return a;
}

static int64_t vc_steady_now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

class VcVadLaughDetector {
public:
    void reset() {
        burst_times_.clear();
        burst_energy_sum_ = 0.0;
        zcr_stable_count_ = 0;
        voiced_stable_count_ = 0;
    }

    void set_min_peak_for_burst(int v) { min_peak_for_burst_ = std::max(60, v); }
    void set_low_gain(bool v) { low_gain_ = v; }

    void notify_speech_state(bool speech, int64_t now_ms) {
        if (speech) {
            if (last_speech_start_ms_ < last_speech_end_ms_ || last_speech_start_ms_ < 0)
                last_speech_start_ms_ = now_ms;
        } else {
            last_speech_end_ms_ = now_ms;
        }
    }

    bool update_and_detect(const int16_t* pcm, size_t frames, int peak, float speech_prob, int64_t now_ms) {
        if (!pcm || frames == 0) return false;

        if (now_ms < cooldown_until_ms_) return false;

        // V1 avoided false positives by skipping laugh detection when ASR partial
        // text existed. The VC path has no partial text, so use high Silero speech
        // probability as the equivalent "this is language" guard.
        if (speech_prob < 0.08f) {
            cleanup_old(now_ms);
            return false;
        }
        if (speech_prob >= 0.92f) {
            reset();
            return false;
        }

        const int cough_threshold = std::max(30000, static_cast<int>(min_peak_for_burst_ * 12.0f));
        if (peak > cough_threshold) {
            reset();
            return false;
        }
        if (peak < min_peak_for_burst_) {
            cleanup_old(now_ms);
            return false;
        }

        const double zcr = calc_zcr(pcm, frames);
        const bool zcr_ok = (zcr >= 0.04 && zcr <= 0.25);
        if (zcr_ok) {
            ++zcr_stable_count_;
        } else {
            zcr_stable_count_ = 0;
            cleanup_old(now_ms);
            return false;
        }
        if (zcr_stable_count_ < 2) return false;

        const double periodicity = calc_periodicity(pcm, frames);
        if (periodicity >= 0.16) {
            ++voiced_stable_count_;
        } else {
            voiced_stable_count_ = 0;
            cleanup_old(now_ms);
            return false;
        }
        if (voiced_stable_count_ < 2) return false;

        const float low_ratio = calc_low_ratio(pcm, frames);
        if (low_ratio > 0.55f) {
            reset();
            return false;
        }

        burst_times_.push_back(now_ms);
        cleanup_old(now_ms);

        if (burst_times_.size() >= 2) {
            const int64_t last = burst_times_.back();
            const int64_t prev = *(burst_times_.rbegin() + 1);
            const int64_t d = last - prev;
            if (d < 100) {
                burst_times_.pop_back();
                return false;
            }
            if (d > 320) {
                while (burst_times_.size() > 1) burst_times_.pop_front();
                burst_energy_sum_ = 0.0;
            }
        }

        burst_energy_sum_ += static_cast<double>(peak) * static_cast<double>(peak);
        bool dense_short_laugh = false;
        if (burst_times_.size() >= 3) {
            const int64_t span = burst_times_.back() - burst_times_.front();
            dense_short_laugh = span <= 250;
        }

        constexpr size_t burst_min = 3;
        if (burst_times_.size() < burst_min && !dense_short_laugh) return false;

        const double mid_ratio = calc_mid_ratio(pcm, frames);
        if (mid_ratio < 0.10) return false;

        double energy_factor = dense_short_laugh ? 0.42 : 0.60;
        const double min_energy = static_cast<double>(min_peak_for_burst_) *
            static_cast<double>(min_peak_for_burst_) * static_cast<double>(burst_min) * energy_factor;
        if (burst_energy_sum_ < min_energy) return false;

        std::fprintf(stderr,
                     "[VC-RVC] vad_laugh_candidate bursts=%zu energy=%.0f mid=%.3f zcr=%.4f periodic=%.3f peak=%d low=%.2f\n",
                     burst_times_.size(), burst_energy_sum_, mid_ratio, zcr, periodicity, peak, low_ratio);
        std::fflush(stderr);
        cooldown_until_ms_ = now_ms + 1200;
        reset();
        return true;
    }

private:
    int min_peak_for_burst_ = 1200;
    bool low_gain_ = false;
    int zcr_stable_count_ = 0;
    int voiced_stable_count_ = 0;
    double burst_energy_sum_ = 0.0;
    int64_t cooldown_until_ms_ = 0;
    int64_t last_speech_start_ms_ = -1;
    int64_t last_speech_end_ms_ = -1;
    std::deque<int64_t> burst_times_;

    void cleanup_old(int64_t now_ms) {
        while (!burst_times_.empty() && now_ms - burst_times_.front() > 1000)
            burst_times_.pop_front();
        if (burst_times_.empty()) {
            burst_energy_sum_ = 0.0;
            zcr_stable_count_ = 0;
            voiced_stable_count_ = 0;
        }
    }

    static double calc_periodicity(const int16_t* pcm, size_t frames) {
        if (!pcm || frames < 160) return 0.0;
        double energy = 0.0;
        for (size_t i = 0; i < frames; ++i) {
            const double x = static_cast<double>(pcm[i]);
            energy += x * x;
        }
        if (energy < 1.0) return 0.0;
        double best = 0.0;
        const size_t max_lag = std::min<size_t>(200, frames / 2); // ~80Hz @16k
        for (size_t lag = 40; lag <= max_lag; ++lag) {           // ~400Hz @16k
            double corr = 0.0;
            double e0 = 0.0;
            double e1 = 0.0;
            for (size_t i = 0; i + lag < frames; ++i) {
                const double a = static_cast<double>(pcm[i]);
                const double b = static_cast<double>(pcm[i + lag]);
                corr += a * b;
                e0 += a * a;
                e1 += b * b;
            }
            const double denom = std::sqrt(e0 * e1) + 1e-9;
            best = std::max(best, corr / denom);
        }
        return best;
    }

    static double calc_zcr(const int16_t* pcm, size_t frames) {
        if (!pcm || frames == 0) return 0.0;
        int16_t prev = pcm[0];
        int crossings = 0;
        for (size_t i = 1; i < frames; ++i) {
            const int16_t s = pcm[i];
            if ((s >= 0 && prev < 0) || (s < 0 && prev >= 0)) ++crossings;
            prev = s;
        }
        return static_cast<double>(crossings) / static_cast<double>(frames);
    }

    static float calc_low_ratio(const int16_t* pcm, size_t frames) {
        int64_t low_energy = 0;
        int64_t total_energy = 0;
        for (size_t i = 0; i < frames; ++i) {
            const int a = std::abs(static_cast<int>(pcm[i]));
            total_energy += a;
            if (a < 600) low_energy += a;
        }
        return total_energy > 0 ? static_cast<float>(low_energy) / static_cast<float>(total_energy) : 0.0f;
    }

    static double calc_mid_ratio(const int16_t* pcm, size_t frames) {
        constexpr double kPi = 3.14159265358979323846;
        constexpr double sr = 16000.0;
        const double hp_a = std::exp(-2.0 * kPi * 300.0 / sr);
        const double lp_a = std::exp(-2.0 * kPi * 3000.0 / sr);
        double x_prev = 0.0, hp_prev = 0.0, lp_prev = 0.0;
        double e_total = 0.0, e_mid = 0.0;
        for (size_t i = 0; i < frames; ++i) {
            const double x = static_cast<double>(pcm[i]);
            const double hp = hp_a * (hp_prev + x - x_prev);
            x_prev = x;
            hp_prev = hp;
            const double lp = (1.0 - lp_a) * hp + lp_a * lp_prev;
            lp_prev = lp;
            e_total += x * x;
            e_mid += lp * lp;
        }
        return e_total > 1e-9 ? e_mid / e_total : 0.0;
    }
};

struct VcMoonshineModelSelection {
    std::string path;
    int arch;
    bool valid = true;
};

static VcMoonshineModelSelection vc_resolve_moonshine_model(const std::string& root, const std::string& language) {
    namespace fs = std::filesystem;
    std::string lang = language;
    std::transform(lang.begin(), lang.end(), lang.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    // Private, process-local Mobfy opt-in; the regular ASR resolver is untouched.
    // A present but invalid value must fail closed, never select the default.
    if (lang == "ja") {
        if (const char* tiny = std::getenv("MOBMATE_MOBFY_JA_TINY_MODEL_DIR")) {
            const fs::path path(tiny);
            std::error_code ec;
            const bool valid = path.is_absolute() && fs::is_directory(path, ec) && !ec;
            return {tiny, mobmate::MoonshineBridge::ARCH_TINY_STREAMING, valid};
        }
    }
    if (lang == "zh" || lang == "zh_cn" || lang == "zh_tw")
        return { (fs::path(root) / "zh" / "quantized" / "base-zh").string(), mobmate::MoonshineBridge::ARCH_BASE };
    if (lang == "ko")
        return { (fs::path(root) / "ko" / "quantized" / "tiny-ko").string(), mobmate::MoonshineBridge::ARCH_TINY };
    if (lang == "en")
        return { (fs::path(root) / "en" / "quantized").string(), mobmate::MoonshineBridge::ARCH_BASE_STREAMING };
    return { (fs::path(root) / "ja" / "quantized" / "base-ja").string(), mobmate::MoonshineBridge::ARCH_BASE };
}

static std::vector<float> vc_i16_to_f32(const std::vector<int16_t>& samples) {
    std::vector<float> out(samples.size());
    for (size_t i = 0; i < samples.size(); ++i)
        out[i] = static_cast<float>(samples[i]) / 32768.0f;
    return out;
}

static std::vector<uint8_t> vc_pcm16_to_bytes(const std::vector<int16_t>& samples) {
    std::vector<uint8_t> bytes(samples.size() * sizeof(int16_t));
    if (!bytes.empty()) std::memcpy(bytes.data(), samples.data(), bytes.size());
    return bytes;
}

static std::vector<int16_t> vc_bytes_to_pcm16(const std::vector<uint8_t>& bytes) {
    std::vector<int16_t> samples(bytes.size() / sizeof(int16_t));
    if (!samples.empty()) std::memcpy(samples.data(), bytes.data(), samples.size() * sizeof(int16_t));
    return samples;
}

static std::string vc_trim_copy(std::string s) {
    auto not_space = [](unsigned char c) { return !std::isspace(c); };
    s.erase(s.begin(), std::find_if(s.begin(), s.end(), not_space));
    s.erase(std::find_if(s.rbegin(), s.rend(), not_space).base(), s.end());
    return s;
}

static std::string vc_normalize_laugh_text(const std::string& text) {
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        if (c < 0x80) {
            if (std::isalnum(c)) out.push_back(static_cast<char>(std::tolower(c)));
            continue;
        }
        out.push_back(static_cast<char>(c));
    }
    return out;
}

static std::vector<std::string> vc_split_laugh_tokens(const std::string& csv) {
    std::vector<std::string> tokens;
    std::string cur;
    auto flush = [&]() {
        auto t = vc_normalize_laugh_text(vc_trim_copy(cur));
        if (!t.empty() && std::find(tokens.begin(), tokens.end(), t) == tokens.end())
            tokens.push_back(t);
        cur.clear();
    };
    for (size_t i = 0; i < csv.size(); ++i) {
        const unsigned char c = static_cast<unsigned char>(csv[i]);
        if (c == ',') { flush(); continue; }
        if (c == 0xE3 && i + 2 < csv.size() &&
            static_cast<unsigned char>(csv[i + 1]) == 0x80 &&
            static_cast<unsigned char>(csv[i + 2]) == 0x81) { // U+3001 IDEOGRAPHIC COMMA
            flush(); i += 2; continue;
        }
        cur.push_back(static_cast<char>(c));
    }
    flush();
    std::sort(tokens.begin(), tokens.end(), [](const std::string& a, const std::string& b) { return a.size() > b.size(); });
    return tokens;
}

static bool vc_is_laugh_asr_text(const std::string& text, const std::string& tokens_csv) {
    const std::string normalized = vc_normalize_laugh_text(text);
    if (normalized.empty()) return false;
    auto tokens = vc_split_laugh_tokens(tokens_csv);
    for (const auto& token : tokens) {
        if (token.size() >= 2 && normalized.find(token) != std::string::npos)
            return true;
    }

    // Fallback for common ASR laughter renderings across supported languages.
    static const char* kFallbackTokens[] = {
        "haha", "hahaha", "hehe", "lol", "lmao", "rofl", "www",
        "ハハ", "ハハハ", "はは", "ははは", "フフ", "ふふ", "笑", "草",
        "ㅋㅋ", "ㅎㅎ", "웃음", "哈哈", "呵呵"
    };
    for (const char* token : kFallbackTokens) {
        if (normalized.find(vc_normalize_laugh_text(token)) != std::string::npos)
            return true;
    }
    return false;
}

static std::vector<std::filesystem::path> vc_find_laugh_samples(const std::string& model_path) {
    std::vector<std::filesystem::path> paths;
    if (model_path.empty()) return paths;
    std::vector<std::filesystem::path> dirs = {
        std::filesystem::path(model_path) / "expressions",
        std::filesystem::path(model_path),
        std::filesystem::path(model_path).parent_path() / "expressions"
    };
    for (const auto& dir : dirs) {
        std::error_code ec;
        if (!std::filesystem::exists(dir, ec) || !std::filesystem::is_directory(dir, ec)) continue;
        for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
            if (ec) break;
            if (!entry.is_regular_file(ec)) continue;
            auto p = entry.path();
            std::string name = p.filename().string();
            std::string ext = p.extension().string();
            std::transform(name.begin(), name.end(), name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            if (ext == ".wav" && (name == "laugh.wav" || name.rfind("laugh_", 0) == 0)) {
                paths.push_back(p);
            }
        }
    }
    std::sort(paths.begin(), paths.end());
    return paths;
}

static bool vc_load_mono_wav_resampled(const std::filesystem::path& path, int dst_sr, std::vector<int16_t>& out) {
    out.clear();
    std::string err;
    auto meta = mobmate::read_wav_metadata(path.string(), err);
    auto samples = mobmate::read_wav_samples(path.string(), err);
    if (!meta || !samples || samples->empty() || meta->sample_rate == 0 || meta->channels == 0) return false;
    std::vector<int16_t> mono;
    if (meta->channels == 1) {
        mono = std::move(*samples);
    } else {
        const size_t frames = samples->size() / meta->channels;
        mono.resize(frames);
        for (size_t i = 0; i < frames; ++i) {
            int sum = 0;
            for (uint16_t ch = 0; ch < meta->channels; ++ch)
                sum += (*samples)[i * meta->channels + ch];
            mono[i] = static_cast<int16_t>(sum / static_cast<int>(meta->channels));
        }
    }
    out = resample_linear_i16(mono.data(), mono.size(), static_cast<int>(meta->sample_rate), dst_sr);
    return !out.empty();
}

static bool vc_try_replace_laugh_with_expression(const std::string& model_path,
                                                 VcOnnxPipeline* onnx,
                                                 SeedVcNcnnPipeline* seedvc,
                                                 const int16_t* detector_samples,
                                                 int16_t* output_samples,
                                                size_t num_samples,
                                                int sr,
                                                bool convert_output_samples,
                                                size_t* selected_index = nullptr) {
    if (!detector_samples || !output_samples || num_samples == 0 || sr <= 0) return false;
    VcLaughAnalysis a = vc_analyze_laugh_like(detector_samples, num_samples, sr);
    if (!a.laugh_like) {
        if (a.max_rms >= 1200.0f && a.bursts >= 1 && a.active_ms >= 80) {
            std::fprintf(stderr,
                         "[VC-RVC] laugh_expression_skip bursts=%d active_ms=%d ratio=%.2f zcr=%.3f max_rms=%.0f first=%zu last=%zu\n",
                         a.bursts, a.active_ms, a.active_ratio, a.zcr, a.max_rms, a.first_sample, a.last_sample);
        }
        return false;
    }
    auto samples = vc_find_laugh_samples(model_path);
    if (samples.empty()) return false;

    static thread_local std::mt19937 rng{static_cast<uint32_t>(std::chrono::steady_clock::now().time_since_epoch().count())};
    std::uniform_int_distribution<size_t> dist(0, samples.size() - 1);
    size_t idx = dist(rng);
    if (selected_index) *selected_index = idx;

    std::vector<int16_t> replacement;
    if (!vc_load_mono_wav_resampled(samples[idx], sr, replacement) || replacement.empty()) return false;

    // Keep the normal RVC voice for non-laugh parts, then replace only the detected laugh span.
    if (convert_output_samples)
        vc_apply_loaded_inference(onnx, seedvc, output_samples, num_samples, sr);
    vc_apply_loaded_inference(onnx, seedvc, replacement.data(), replacement.size(), sr);

    const size_t start = std::min(a.first_sample, num_samples);
    const size_t gap = static_cast<size_t>(std::max(1, sr / 12)); // ~83 ms
    const size_t tail = static_cast<size_t>(std::max(1, sr / 8)); // 125 ms
    const size_t desired_end = start + replacement.size() * 2 + gap;
    const size_t clear_end = std::min(num_samples, std::max(a.last_sample + tail, desired_end));
    if (start < clear_end) std::fill(output_samples + start, output_samples + clear_end, 0);

    size_t total_copied = 0;
    const float gain = 0.62f;
    for (int repeat = 0; repeat < 2; ++repeat) {
        const size_t pos = start + static_cast<size_t>(repeat) * (replacement.size() + gap);
        if (pos >= num_samples || pos >= clear_end) break;
        const size_t copy_len = std::min(replacement.size(), std::min(num_samples, clear_end) - pos);
        for (size_t i = 0; i < copy_len; ++i) {
            const float v = static_cast<float>(replacement[i]) * gain;
            output_samples[pos + i] = static_cast<int16_t>(std::max(-32768.0f, std::min(32767.0f, v)));
        }
        total_copied += copy_len;
    }
    std::fprintf(stderr,
                 "[VC-RVC] laugh_expression_replace sample=%s idx=%zu bursts=%d active_ms=%d ratio=%.2f zcr=%.3f max_rms=%.0f start=%zu end=%zu copied=%zu gain=%.2f repeats=2\n",
                 samples[idx].string().c_str(), idx, a.bursts, a.active_ms, a.active_ratio,
                 a.zcr, a.max_rms, start, clear_end, total_copied, gain);
    return total_copied > 0;
}

static bool vc_try_insert_laugh_expression(const std::string& model_path,
                                           VcOnnxPipeline* onnx,
                                           SeedVcNcnnPipeline* seedvc,
                                           int16_t* output_samples,
                                           size_t num_samples,
                                           int sr,
                                           size_t* selected_index = nullptr,
                                           std::vector<int16_t>* full_expression = nullptr,
                                           size_t target_samples = 0) {
    if (!output_samples || num_samples == 0 || sr <= 0) return false;
    auto samples = vc_find_laugh_samples(model_path);
    if (samples.empty()) return false;

    static thread_local std::mt19937 rng{static_cast<uint32_t>(std::chrono::steady_clock::now().time_since_epoch().count())};
    std::uniform_int_distribution<size_t> dist(0, samples.size() - 1);
    size_t idx = dist(rng);
    if (selected_index) *selected_index = idx;

    std::vector<int16_t> replacement;
    if (!vc_load_mono_wav_resampled(samples[idx], sr, replacement) || replacement.empty()) return false;
    vc_apply_loaded_inference(onnx, seedvc, replacement.data(), replacement.size(), sr);

    const size_t gap = static_cast<size_t>(std::max(1, sr / 12)); // ~83 ms
    const float gain = 0.62f;
    if (full_expression) {
        const size_t desired = target_samples > 0 ? target_samples : num_samples;
        full_expression->assign(desired, 0);
        size_t written = 0;
        for (size_t pos = 0; pos < desired; pos += replacement.size() + gap) {
            const size_t copy_len = std::min(replacement.size(), desired - pos);
            for (size_t i = 0; i < copy_len; ++i) {
                const float v = static_cast<float>(replacement[i]) * gain;
                (*full_expression)[pos + i] = static_cast<int16_t>(std::max(-32768.0f, std::min(32767.0f, v)));
            }
            written += copy_len;
        }
        std::fprintf(stderr,
                     "[VC-RVC] laugh_expression_stream sample=%s idx=%zu copied=%zu gain=%.2f target=%zu\n",
                     samples[idx].string().c_str(), idx, written, gain, desired);
    }
    std::fill(output_samples, output_samples + num_samples, 0);
    size_t total_copied = 0;
    for (int repeat = 0; repeat < 2; ++repeat) {
        const size_t pos = static_cast<size_t>(repeat) * (replacement.size() + gap);
        if (pos >= num_samples) break;
        const size_t copy_len = std::min(replacement.size(), num_samples - pos);
        for (size_t i = 0; i < copy_len; ++i) {
            const float v = static_cast<float>(replacement[i]) * gain;
            output_samples[pos + i] = static_cast<int16_t>(std::max(-32768.0f, std::min(32767.0f, v)));
        }
        total_copied += copy_len;
    }
    std::fprintf(stderr,
                 "[VC-RVC] laugh_expression_insert sample=%s idx=%zu copied=%zu gain=%.2f repeats=2\n",
                 samples[idx].string().c_str(), idx, total_copied, gain);
    std::fflush(stderr);
    return total_copied > 0;
}

struct VcRtRvc {
    // Configuration
    size_t chunk_samples = 0;    // 500ms at sample_rate
    size_t overlap_samples = 0;  // 20ms at sample_rate
    size_t advance_samples = 0;  // chunk - overlap
    int sample_rate = 48000;
    bool raw_output_gain_enabled = false;
    VcRawOutputGainState raw_output_gain;

    // Pre-RVC accumulation buffer (written by capture callback)
    std::vector<int16_t> pre_buf;
    std::mutex pre_mtx;
    std::condition_variable pre_cv;
    bool chunk_ready = false;
    std::vector<int16_t> worker_input; // swapped from pre_buf
    bool reset_stream_on_next_feed = false;
    bool next_chunk_starts_segment = false;
    bool worker_input_starts_segment = false;

    // Post-RVC ring buffer (written by worker, read by playback)
    VcRingBuffer post_ring;
    mutable std::mutex post_mutex;

    size_t post_available() const {
        std::lock_guard<std::mutex> lock(post_mutex);
        return post_ring.available();
    }
    size_t post_read(int16_t* dst, size_t count) {
        std::lock_guard<std::mutex> lock(post_mutex);
        return post_ring.read(dst, count);
    }
    void post_write(const int16_t* src, size_t count) {
        std::lock_guard<std::mutex> lock(post_mutex);
        post_ring.write(src, count);
    }
    void post_clear() {
        std::lock_guard<std::mutex> lock(post_mutex);
        post_ring.clear();
    }

    // SOLA state
    std::vector<int16_t> prev_tail; // overlap tail from previous chunk

    // Worker thread
    std::thread worker_thread;
    std::atomic<bool> running{false};
    VcOnnxPipeline* onnx_ref = nullptr; // non-owning
    std::string model_path;
    int model_output_sr = 40000;        // from model config
    std::atomic<int64_t>* laugh_detect_count_ref = nullptr; // non-owning; ASR-guarded laugh trigger
    int64_t seen_laugh_detect_count = 0;
    std::atomic<int64_t>* asr_gate_accept_count_ref = nullptr; // non-owning
    std::atomic<int64_t>* asr_gate_reject_count_ref = nullptr; // non-owning
    int64_t seen_asr_gate_accept_count = 0;
    int64_t seen_asr_gate_reject_count = 0;
    bool asr_gate_active = false;
    bool asr_gate_drop_late_output = false;
    std::vector<int16_t> asr_gate_pending_output;
    std::mutex asr_gate_mutex;
    std::atomic<bool> asr_gate_active_status{false};
    std::atomic<size_t> asr_gate_pending_samples{0};

    // Stats
    std::atomic<float> last_chunk_ms{0.0f};
    std::atomic<int64_t> chunks_processed{0};
    std::atomic<int64_t> laugh_replace_count{0};
    std::atomic<int64_t> laugh_skip_count{0};
    std::atomic<int64_t> last_laugh_sample_index{-1};
    int laugh_cooldown_chunks = 0;
    std::vector<int16_t> laugh_pending;
    size_t laugh_pending_pos = 0;

    void init(int sr, size_t chunk_ms, size_t overlap_ms, VcOnnxPipeline* onnx, const std::string& model_path_in,
              bool raw_output_gain_enabled_in = false, float raw_output_gain_target = 1.0f,
              std::atomic<int64_t>* laugh_detect_count_in = nullptr,
              std::atomic<int64_t>* asr_gate_accept_count_in = nullptr,
              std::atomic<int64_t>* asr_gate_reject_count_in = nullptr) {
        sample_rate = sr;
        raw_output_gain_enabled = raw_output_gain_enabled_in;
        raw_output_gain.reset(raw_output_gain_target);
        chunk_samples = static_cast<size_t>(sr) * chunk_ms / 1000;
        overlap_samples = static_cast<size_t>(sr) * overlap_ms / 1000;
        advance_samples = chunk_samples - overlap_samples;
        onnx_ref = onnx;
        model_path = model_path_in;
        laugh_detect_count_ref = laugh_detect_count_in;
        seen_laugh_detect_count = laugh_detect_count_ref ? laugh_detect_count_ref->load(std::memory_order_relaxed) : 0;
        asr_gate_accept_count_ref = asr_gate_accept_count_in;
        asr_gate_reject_count_ref = asr_gate_reject_count_in;
        seen_asr_gate_accept_count = asr_gate_accept_count_ref ? asr_gate_accept_count_ref->load(std::memory_order_relaxed) : 0;
        seen_asr_gate_reject_count = asr_gate_reject_count_ref ? asr_gate_reject_count_ref->load(std::memory_order_relaxed) : 0;
        if (onnx && onnx->loaded) model_output_sr = onnx->output_sr;
        pre_buf.reserve(chunk_samples * 2);
        prev_tail.clear();
        post_ring.init(static_cast<size_t>(sr) * 3); // 3 sec capacity for latency headroom
        running.store(true, std::memory_order_relaxed);
        worker_thread = std::thread(&VcRtRvc::worker_loop, this);
    }

    void shutdown() {
        running.store(false, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lk(pre_mtx);
            chunk_ready = true; // unblock worker
        }
        pre_cv.notify_one();
        if (worker_thread.joinable()) worker_thread.join();
    }

    void begin_asr_gate() {
        {
            std::lock_guard<std::mutex> pre_lk(pre_mtx);
            reset_stream_on_next_feed = true;
            next_chunk_starts_segment = true;
        }
        if (!asr_gate_accept_count_ref || !asr_gate_reject_count_ref) return;
        std::lock_guard<std::mutex> lk(asr_gate_mutex);
        asr_gate_active = true;
        asr_gate_drop_late_output = false;
        asr_gate_active_status.store(true, std::memory_order_relaxed);
        asr_gate_pending_output.clear();
        asr_gate_pending_samples.store(0, std::memory_order_relaxed);
        seen_asr_gate_accept_count = asr_gate_accept_count_ref->load(std::memory_order_relaxed);
        seen_asr_gate_reject_count = asr_gate_reject_count_ref->load(std::memory_order_relaxed);
    }

    void cancel_asr_gate() {
        std::lock_guard<std::mutex> lk(asr_gate_mutex);
        asr_gate_active = false;
        asr_gate_drop_late_output = false;
        asr_gate_active_status.store(false, std::memory_order_relaxed);
        asr_gate_pending_output.clear();
        asr_gate_pending_samples.store(0, std::memory_order_relaxed);
    }

    // Head de-click (2026-07-12): VAD can trigger on lip/mouth clicks that
    // precede real speech ("tch" before arigatou). The whole segment is then
    // accepted by the ASR gate, so the click leaks out amplified. Before
    // releasing pending output, locate the voiced onset and drop audio older
    // than 120ms before it. Returns number of samples trimmed from the head.
    size_t asr_gate_trim_head_noise(std::vector<int16_t>& buf) const {
        if (buf.empty() || sample_rate <= 0) return 0;
        const size_t win = static_cast<size_t>(std::max(1, sample_rate / 50)); // 20ms
        size_t onset = buf.size();
        for (size_t i = 0; i + win <= buf.size(); i += win) {
            double sum_sq = 0.0;
            int zc = 0;
            for (size_t j = i; j < i + win; ++j) {
                const float s = static_cast<float>(buf[j]);
                sum_sq += static_cast<double>(s) * static_cast<double>(s);
                if (j > i && ((buf[j] >= 0) != (buf[j - 1] >= 0))) ++zc;
            }
            const float rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(win)));
            const float zcr = static_cast<float>(zc) / static_cast<float>(win);
            const bool voiced = (rms >= 1500.0f && zcr < 0.15f) || (rms >= 800.0f && zcr < 0.06f);
            if (voiced) { onset = i; break; }
        }
        if (onset == buf.size()) return 0; // no voiced onset -> leave as is
        const size_t keep_before = static_cast<size_t>(sample_rate) * 120 / 1000; // 120ms guard
        if (onset <= keep_before) return 0;
        size_t trim = onset - keep_before;
        const size_t min_trim = static_cast<size_t>(sample_rate) * 60 / 1000; // skip micro trims
        if (trim < min_trim) return 0;
        buf.erase(buf.begin(), buf.begin() + static_cast<std::ptrdiff_t>(trim));
        // Short fade-in to avoid a step discontinuity at the new head.
        const size_t fade = std::min(buf.size(), static_cast<size_t>(sample_rate) * 10 / 1000);
        for (size_t i = 0; i < fade; ++i)
            buf[i] = static_cast<int16_t>(static_cast<float>(buf[i]) * (static_cast<float>(i) / static_cast<float>(fade)));
        return trim;
    }

    void complete_asr_gate(bool accept) {
        std::lock_guard<std::mutex> lk(asr_gate_mutex);
        if (!asr_gate_active) {
            if (!accept) {
                post_clear();
                std::fprintf(stderr, "[VC-RT] asr_gate_drop_inactive flush_post_ring=1\n");
                std::fflush(stderr);
            }
            return;
        }
        size_t pending = asr_gate_pending_output.size();
        if (accept && pending > 0) {
            const size_t trimmed = asr_gate_trim_head_noise(asr_gate_pending_output);
            if (trimmed > 0) {
                std::fprintf(stderr, "[VC-RT] asr_gate_head_trim samples=%zu\n", trimmed);
                pending = asr_gate_pending_output.size();
            }
            if (pending > 0)
                post_write(asr_gate_pending_output.data(), pending);
        }
        if (!accept) {
            post_clear();
            asr_gate_drop_late_output = true;
        } else {
            asr_gate_drop_late_output = false;
        }
        asr_gate_active = false;
        asr_gate_active_status.store(false, std::memory_order_relaxed);
        asr_gate_pending_output.clear();
        asr_gate_pending_samples.store(0, std::memory_order_relaxed);
        std::fprintf(stderr, accept ? "[VC-RT] asr_gate_release_immediate samples=%zu\n" : "[VC-RT] asr_gate_drop_immediate samples=%zu\n", pending);
        std::fflush(stderr);
    }

    // Called from on_capture after DSP. Accumulates samples and signals worker when chunk is full.
    void feed(const int16_t* data, size_t frames) {
        std::lock_guard<std::mutex> lk(pre_mtx);
        {
            std::lock_guard<std::mutex> gate_lk(asr_gate_mutex);
            if (!asr_gate_active && asr_gate_drop_late_output && pre_buf.empty())
                asr_gate_drop_late_output = false;
        }
        if (reset_stream_on_next_feed) {
            pre_buf.clear();
            reset_stream_on_next_feed = false;
        }
        pre_buf.insert(pre_buf.end(), data, data + frames);
        if (pre_buf.size() >= chunk_samples) {
            // Copy chunk for worker
            worker_input.assign(pre_buf.begin(), pre_buf.begin() + static_cast<std::ptrdiff_t>(chunk_samples));
            worker_input_starts_segment = next_chunk_starts_segment;
            next_chunk_starts_segment = false;
            // Remove consumed samples (keep overlap for next chunk)
            pre_buf.erase(pre_buf.begin(), pre_buf.begin() + static_cast<std::ptrdiff_t>(advance_samples));
            chunk_ready = true;
            pre_cv.notify_one();
        }
    }

    void worker_loop() {
        while (running.load(std::memory_order_relaxed)) {
            std::vector<int16_t> input;
            bool input_starts_segment = false;
            {
                std::unique_lock<std::mutex> lk(pre_mtx);
                pre_cv.wait(lk, [this] { return chunk_ready || !running.load(std::memory_order_relaxed); });
                if (!running.load(std::memory_order_relaxed)) break;
                input = std::move(worker_input);
                input_starts_segment = worker_input_starts_segment;
                worker_input_starts_segment = false;
                chunk_ready = false;
            }
            if (input.empty() || !onnx_ref || !onnx_ref->loaded) continue;

            auto t0 = std::chrono::steady_clock::now();

            // RVC inference (returns audio at model_output_sr). Raw gain is
            // solved once at utterance start and reused by following chunks.
            if (raw_output_gain_enabled && input_starts_segment)
                raw_output_gain.reset(raw_output_gain.target_gain);
            auto* gain_state = raw_output_gain_enabled ? &raw_output_gain : nullptr;
            auto rvc_out = onnx_ref->infer(input.data(), input.size(), sample_rate, gain_state);
            if (rvc_out.empty()) continue;

            // Resample RVC output to playback sample rate
            auto resampled = resample_linear_i16(rvc_out.data(), rvc_out.size(),
                                                  model_output_sr, sample_rate);

            if (laugh_detect_count_ref && laugh_cooldown_chunks <= 0) {
                const int64_t current_laugh_count = laugh_detect_count_ref->load(std::memory_order_relaxed);
                if (current_laugh_count > seen_laugh_detect_count) {
                    size_t selected = 0;
                    std::vector<int16_t> scratch(resampled.size());
                    std::vector<int16_t> expression_stream;
                    if (vc_try_insert_laugh_expression(model_path, onnx_ref, nullptr,
                                                       scratch.data(), scratch.size(), sample_rate, &selected,
                                                       &expression_stream, resampled.size() * 6)) {
                        laugh_replace_count.fetch_add(1, std::memory_order_relaxed);
                        last_laugh_sample_index.store(static_cast<int64_t>(selected), std::memory_order_relaxed);
                        laugh_pending = std::move(expression_stream);
                        laugh_pending_pos = 0;
                        laugh_cooldown_chunks = 6;
                    } else {
                        laugh_skip_count.fetch_add(1, std::memory_order_relaxed);
                    }
                    seen_laugh_detect_count = current_laugh_count;
                }
            }
            if (laugh_cooldown_chunks > 0) --laugh_cooldown_chunks;

            bool laugh_expression_active_chunk = false;
            if (!laugh_pending.empty() && laugh_pending_pos < laugh_pending.size()) {
                laugh_expression_active_chunk = true;
                const size_t copy_len = std::min(resampled.size(), laugh_pending.size() - laugh_pending_pos);
                for (size_t i = 0; i < copy_len; ++i) {
                    const int16_t v = laugh_pending[laugh_pending_pos + i];
                    if (v != 0) resampled[i] = v;
                }
                laugh_pending_pos += copy_len;
                if (laugh_pending_pos >= laugh_pending.size()) {
                    laugh_pending.clear();
                    laugh_pending_pos = 0;
                }
            }

            // Real-time chunks are emitted every advance_samples (chunk-overlap).
            // The previous implementation wrote the current overlap again, so the
            // stream grew by overlap_ms every chunk and sounded stretched/delayed.
            size_t write_start = 0;
            if (input_starts_segment) {
                prev_tail.clear();
            }
            if (!input_starts_segment && !laugh_expression_active_chunk && !prev_tail.empty() && !resampled.empty()) {
                write_start = std::min(overlap_samples, resampled.size());
            }
            const int64_t gate_accept = asr_gate_accept_count_ref ? asr_gate_accept_count_ref->load(std::memory_order_relaxed) : seen_asr_gate_accept_count;
            const int64_t gate_reject = asr_gate_reject_count_ref ? asr_gate_reject_count_ref->load(std::memory_order_relaxed) : seen_asr_gate_reject_count;
            bool gate_consumed = false;
            {
                std::lock_guard<std::mutex> gate_lk(asr_gate_mutex);
                if (asr_gate_active) {
                    gate_consumed = true;
                    if (write_start < resampled.size()) {
                        asr_gate_pending_output.insert(asr_gate_pending_output.end(),
                            resampled.begin() + static_cast<std::ptrdiff_t>(write_start), resampled.end());
                        asr_gate_pending_samples.store(asr_gate_pending_output.size(), std::memory_order_relaxed);
                    }
                    if (gate_accept > seen_asr_gate_accept_count) {
                        const size_t head_trimmed = asr_gate_trim_head_noise(asr_gate_pending_output);
                        if (head_trimmed > 0)
                            std::fprintf(stderr, "[VC-RT] asr_gate_head_trim samples=%zu\n", head_trimmed);
                        const size_t released = asr_gate_pending_output.size();
                        if (!asr_gate_pending_output.empty())
                            post_write(asr_gate_pending_output.data(), asr_gate_pending_output.size());
                        asr_gate_active = false;
                        asr_gate_active_status.store(false, std::memory_order_relaxed);
                        asr_gate_pending_output.clear();
                        asr_gate_pending_samples.store(0, std::memory_order_relaxed);
                        seen_asr_gate_accept_count = gate_accept;
                        seen_asr_gate_reject_count = gate_reject;
                        std::fprintf(stderr, "[VC-RT] asr_gate_release samples=%zu\n", released);
                        std::fflush(stderr);
                    } else if (gate_reject > seen_asr_gate_reject_count) {
                        std::fprintf(stderr, "[VC-RT] asr_gate_drop samples=%zu\n", asr_gate_pending_output.size());
                        std::fflush(stderr);
                        post_clear();
                        asr_gate_drop_late_output = true;
                        asr_gate_active = false;
                        asr_gate_active_status.store(false, std::memory_order_relaxed);
                        asr_gate_pending_output.clear();
                        asr_gate_pending_samples.store(0, std::memory_order_relaxed);
                        seen_asr_gate_accept_count = gate_accept;
                        seen_asr_gate_reject_count = gate_reject;
                    }
                } else if (asr_gate_drop_late_output) {
                    gate_consumed = true;
                    asr_gate_drop_late_output = false;
                }
            }
            if (!gate_consumed && write_start < resampled.size()) {
                post_write(resampled.data() + write_start, resampled.size() - write_start);
            }
            // Save tail for next SOLA
            if (resampled.size() >= overlap_samples) {
                prev_tail.assign(resampled.end() - static_cast<std::ptrdiff_t>(overlap_samples),
                                  resampled.end());
            }

            auto t1 = std::chrono::steady_clock::now();
            float ms = std::chrono::duration<float, std::milli>(t1 - t0).count();
            last_chunk_ms.store(ms, std::memory_order_relaxed);
            chunks_processed.fetch_add(1, std::memory_order_relaxed);
            std::fprintf(stderr, "[VC-RT] RVC chunk %lld: %.1fms (%.3f RTF)\n",
                         static_cast<long long>(chunks_processed.load(std::memory_order_relaxed)),
                         ms,
                         ms / (static_cast<float>(chunk_samples) / sample_rate * 1000.0f));
            std::fflush(stderr);
        }
    }
};

struct VcAudioState {
    ma_context  context{};
    ma_device   capture_dev{};
    ma_device   playback_dev{};
    ma_device   monitor_dev{};
    bool context_ok  = false;
    bool capture_ok  = false;
    bool playback_ok = false;
    bool monitor_ok = false;

    VcRingBuffer ring;
    VcRingBuffer monitor_ring;
    VcRingBuffer filler_monitor_ring;
    VcOnsetFiller* filler = nullptr; // session-owned, including before start/after stop
    VcDspChain   dsp;
    std::mutex   dsp_mutex;
    VcOnnxPipeline* onnx = nullptr; // optional; if loaded, VC inference runs
    SeedVcNcnnPipeline* seedvc = nullptr; // optional; if loaded, VC inference runs
    VcRtRvc* rt_rvc = nullptr; // optional; if set, real-time RVC streaming is active
    // Serializes callbacks with changes to the model/RT worker lifetime.
    std::mutex route_mutex;
    bool conversion_required = false;
    bool conversion_ready = false;
    bool output_stopped = false;

    bool output_blocked() const {
        return output_stopped || (conversion_required && !conversion_ready);
    }
    void cancel_filler() {
        if (filler) filler->cancel();
        filler_monitor_ring.clear(); // never discard queued body monitor PCM
    }
    std::atomic<size_t> underrun_count{0};
    std::atomic<size_t> overflow_count{0};
    std::atomic<size_t> frames_captured{0};
    std::atomic<size_t> frames_played{0};
    std::atomic<size_t> zero_input_callbacks{0};
    std::atomic<float> input_rms_level{0.0f};
    std::atomic<float> input_peak_level{0.0f};
    std::atomic<float> output_rms_level{0.0f};
    std::atomic<float> output_peak_level{0.0f};
    float playback_gain = 1.0f;
    float output_ceiling = 0.85f;
    bool  output_auto_gain_enabled = true;
    float output_auto_gain = 1.0f;
    std::atomic<float> output_auto_gain_current{1.0f};
    float monitor_gain = 0.0f;
    float monitor_volume_percent = 0.0f;
    int sample_rate = 48000;
    std::string capture_device_name;
    std::string playback_device_name;
    std::string monitor_device_name;
    bool debug_dump_enabled = true;
    std::string dump_dir;
    bool wav_output_enabled = false;
    std::string wav_output_directory;
    int dump_seg_counter = 0;
    int current_debug_seg_id = -1;
    std::vector<int16_t> raw_speech_buffer;
    std::vector<int16_t> speech_buffer;
    std::vector<int16_t> speech_output_buffer;
    std::vector<int16_t> raw_pre_roll_buffer;
    std::vector<int16_t> pre_roll_buffer;
    float pre_roll_agc_max_gain = 1.0f;
    float segment_agc_start_gain = 1.0f;
    float segment_agc_max_gain = 1.0f;
    static constexpr size_t PRE_ROLL_MAX_SAMPLES = 48000; // ~1s @ 48k
    static constexpr size_t SPEECH_BUFFER_MAX_SAMPLES = 48000 * 20; // safety cap
    bool collecting_speech = false;
    float segment_start_prob = 0.0f;
    size_t speech_active_samples = 0;
    bool asr_gate_early_released = false;

    // ── SileroVAD gating ──
    silero_vad_handle* vad = nullptr;
    int16_t vad_16k_buf[512] = {};      // accumulation buffer for 16kHz VAD chunks
    size_t  vad_16k_count    = 0;
    std::atomic<bool>  vad_speech_active{false};
    std::atomic<float> vad_last_prob{0.0f};
    float vad_start_threshold    = 0.54f;
    float vad_continue_threshold = 0.36f;
    float vad_soft_start_threshold = 0.14f;
    int vad_hold_frames_remaining = 0;
    int vad_hold_frames = 24; // ~770ms @ 512/16k; prevents chopped phrases without holding noise too long
    VcNoiseProfile noise_profile;
    VcVadLaughDetector laugh_detector;
    std::atomic<bool> laugh_prev_speech_active{false};
    std::atomic<int64_t> laugh_detect_count{0};
    std::atomic<int64_t> last_laugh_detect_ms{0};

    // ── Moonshine speech guard for laugh candidates ──
    bool laugh_asr_assist_enabled = false;
    bool raw_rvc_mode = false;
    std::string laugh_asr_moonshine_dll_dir;
    std::string laugh_asr_moonshine_model_root;
    std::string laugh_asr_language = "ja";
    std::string laugh_asr_tokens;
    std::vector<int16_t> laugh_asr_roll16k;
    std::thread laugh_asr_thread;
    std::mutex laugh_asr_mutex;
    std::condition_variable laugh_asr_cv;
    bool laugh_asr_stop = false;
    uint64_t asr_segment_gen = 0; // capture only; unchanged across speculative cancel/resume
    struct SegmentAsrJob {
        std::vector<int16_t> asr16k;
        std::vector<int16_t> audio;
        int audio_sr = 0;
        VcAsrGateAnalysis analysis;
        uint64_t spec_gen = 0; // 0 = final segment; >0 = speculative hold-overlap job
        int debug_seg_id = -1;
        uint64_t segment_gen = 0; // physical capture segment, not speculative job generation
    };
    // Speculative hold-overlap (2026-07-12): a segment job is queued while the
    // VAD hold is still counting down so ASR+RVC compute overlaps the hold
    // window. Emission waits for a commit (hold expired) / cancel (speech
    // resumed) decision, so segmentation is identical to non-speculative mode.
    static constexpr int kSpecDelayFrames = 12; // ~384ms of trailing tail kept before snapshot
    uint64_t spec_next_gen = 0;    // capture thread only
    uint64_t spec_pending_gen = 0; // capture thread only
    std::map<uint64_t, bool> spec_decisions; // guarded by laugh_asr_mutex; true=commit
    std::deque<SegmentAsrJob> laugh_asr_jobs;
    std::atomic<bool> laugh_asr_pending{false};
    std::atomic<int64_t> laugh_asr_checked_count{0};
    std::atomic<int64_t> laugh_asr_rejected_speech_count{0};
    std::atomic<int64_t> laugh_asr_error_count{0};
    std::atomic<int64_t> asr_gate_accept_count{0};
    std::atomic<int64_t> asr_gate_reject_count{0};
    std::atomic<int64_t> last_laugh_asr_ms{0};
    std::string laugh_asr_last_text;
    std::string laugh_asr_last_decision = "idle";
    // Observation only: do not infer release permission from recognizer health.
    // Kept private so the existing status JSON remains unchanged.
    std::string laugh_asr_last_outcome = "idle";
    float laugh_asr_last_confidence = -1.0f;
    VcAsrGateAnalysis laugh_asr_last_gate_analysis;

    // ── AGC (Automatic Gain Control) ──
    bool  agc_enabled      = true;
    float agc_gain         = 1.0f;   // current smoothed gain
    float agc_smoothed_rms = 0.0f;   // EMA of raw input RMS
    std::atomic<float> agc_current_gain{1.0f};  // for status reporting
    std::vector<int16_t> agc_buffer;             // mutable copy of capture

    static constexpr float kAgcTargetRms   = 0.08f;   // ~-22dBFS, meter mid-point
    static constexpr float kAgcMaxGain     = 60.0f;   // +36dB, rescue very quiet mics
    static constexpr float kAgcMinGain     = 0.3f;    // -10dB, prevent clipping on loud input
    static constexpr float kAgcSilenceRms  = 0.0008f; // do not boost near-silence/noise
    static constexpr float kAgcRmsSmooth   = 0.08f;   // EMA coeff for RMS tracking
    static constexpr float kAgcAttackCoeff = 0.02f;   // gain increase (signal got quieter) - slow
    static constexpr float kAgcReleaseCoeff= 0.15f;   // gain decrease (signal got louder)  - fast

    // ── Output auto gain ──
    static constexpr float kOutputAutoTargetRms   = 0.165f;  // ~-15.7dBFS after Mobfy DSP (2026-07-13: +2dB for clarity)
    static constexpr float kOutputAutoMinGain     = 0.65f;
    static constexpr float kOutputAutoMaxGain     = 2.20f;
    static constexpr float kOutputAutoSilenceRms  = 0.0030f;
    static constexpr float kOutputAutoAttackCoeff = 0.035f;  // louder when too quiet: slow
    static constexpr float kOutputAutoReleaseCoeff= 0.35f;   // quieter when too hot: fast

    // Per-segment loudness normalization (2026-07-13): quiet utterances are
    // boosted to normal speech level and loud ones attenuated before the
    // converted segment is emitted. Uses active-frame RMS so silence tails do
    // not skew the measurement; applied once per segment (no pumping).
    static constexpr float kSegNormMaxBoost  = 8.0f;  // +18dB rescue for quiet voice
    static constexpr float kSegNormMinGain   = 0.40f; // -8dB for shouting
    static constexpr float kSegNormOverdrive = 1.5f;  // drive peaks into the soft limiter (~+3.5dB loudness)
    void normalize_segment_output(std::vector<int16_t>& seg) {
        if (!output_auto_gain_enabled || seg.empty() || sample_rate <= 0) return;
        const size_t frame_n = static_cast<size_t>(std::max(1, sample_rate / 50)); // 20ms
        double active_sum_sq = 0.0;
        size_t active_n = 0;
        float peak = 0.0f;
        for (size_t pos = 0; pos + frame_n <= seg.size(); pos += frame_n) {
            double fs = 0.0;
            for (size_t i = 0; i < frame_n; ++i) {
                const float s = seg[pos + i] * kSampleScale;
                fs += static_cast<double>(s) * s;
                const float a = std::fabs(s);
                if (a > peak) peak = a;
            }
            const float frms = static_cast<float>(std::sqrt(fs / static_cast<double>(frame_n)));
            if (frms >= 0.010f) { active_sum_sq += fs; active_n += frame_n; }
        }
        if (active_n == 0 || peak <= 0.0001f) return;
        const float active_rms = static_cast<float>(std::sqrt(active_sum_sq / static_cast<double>(active_n)));
        const float base = std::max(0.1f, playback_gain);
        float g = kOutputAutoTargetRms / std::max(0.0001f, active_rms * base);
        g = clampf(g, kSegNormMinGain, kSegNormMaxBoost);
        // Loudness over peak: allow driving peaks up to kSegNormOverdrive into
        // the soft limiter (crest reduction), but hard-guarantee the stored
        // samples never exceed the pre-base ceiling so the playback stage
        // (x base_gain) stays below full scale without further reduction.
        const float pre_ceiling = 0.98f / base;            // linear, so peak*base <= 0.98
        const float peak_gain = pre_ceiling / peak;        // gain that puts peak at ceiling
        g = std::min(g, std::max(0.25f, peak_gain * kSegNormOverdrive));
        if (std::fabs(g - 1.0f) < 0.02f) return;
        const float ceiling_i16 = pre_ceiling * 32767.0f;
        for (auto& v : seg)
            v = static_cast<int16_t>(soft_limit_i16(static_cast<float>(v) * g, ceiling_i16));
        std::fprintf(stderr, "[VC-RVC] seg_norm gain=%.2f active_rms=%.4f peak=%.3f\n",
                     g, active_rms, peak);
        std::fflush(stderr);
    }

    // Round only the first active onset of a converted segment. RVC can turn a
    // strong vowel onset into a short "shat"-like burst; processing the whole
    // segment would also dull useful consonants. Dip smoothly at onset, then
    // recover within 30ms before the segment enters the playback ring.
    void soften_segment_onset(std::vector<int16_t>& seg, int64_t* onset_sample = nullptr) const {
        if (onset_sample) *onset_sample = -1;
        if (seg.empty() || sample_rate <= 0) return;
        const size_t frame_n = static_cast<size_t>(std::max(1, sample_rate / 200)); // 5ms
        const size_t search_n = std::min(seg.size(), static_cast<size_t>(sample_rate) * 300 / 1000);
        size_t onset = search_n;
        for (size_t pos = 0; pos + frame_n <= search_n; pos += frame_n) {
            double sum_sq = 0.0;
            for (size_t i = 0; i < frame_n; ++i) {
                const float s = seg[pos + i] * kSampleScale;
                sum_sq += static_cast<double>(s) * s;
            }
            const float rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(frame_n)));
            if (rms >= 0.035f) { onset = pos; break; }
        }
        if (onset >= search_n) return;
        if (onset_sample) *onset_sample = static_cast<int64_t>(onset);

        constexpr float kMinGain = 0.45f; // -6.9dB at the burst
        const size_t lead_n = static_cast<size_t>(sample_rate) * 5 / 1000;
        const size_t recover_n = static_cast<size_t>(sample_rate) * 30 / 1000;
        const size_t begin = onset > lead_n ? onset - lead_n : 0;
        const size_t end = std::min(seg.size(), onset + recover_n);
        for (size_t i = begin; i < end; ++i) {
            float gain = kMinGain;
            if (i < onset && onset > begin) {
                const float t = static_cast<float>(i - begin) / static_cast<float>(onset - begin);
                gain = 1.0f - (1.0f - kMinGain) * (0.5f - 0.5f * std::cos(3.14159265f * t));
            } else if (recover_n > 0) {
                const float t = std::min(1.0f, static_cast<float>(i - onset) / static_cast<float>(recover_n));
                gain = kMinGain + (1.0f - kMinGain) * (0.5f - 0.5f * std::cos(3.14159265f * t));
            }
            seg[i] = static_cast<int16_t>(static_cast<float>(seg[i]) * gain);
        }
        std::fprintf(stderr, "[VC-RVC] onset_soften onset_ms=%zu min_gain=%.2f recover_ms=30\n",
                     onset * 1000 / static_cast<size_t>(sample_rate), kMinGain);
        std::fflush(stderr);
    }

    void apply_output_gain(int16_t* pcm, size_t frames) {
        if (!pcm || frames == 0) return;

        const float base_gain = playback_gain;
        const float ceiling = clampf(output_ceiling, 0.1f, 1.0f) * 32767.0f;
        double sum_sq = 0.0;
        float peak = 0.0f;
        for (size_t i = 0; i < frames; ++i) {
            const float s = std::fabs(pcm[i] * kSampleScale);
            sum_sq += static_cast<double>(s) * static_cast<double>(s);
            if (s > peak) peak = s;
        }

        const float rms = static_cast<float>(std::sqrt(sum_sq / static_cast<double>(frames)));
        float target_auto = 1.0f;
        if (output_auto_gain_enabled && rms >= kOutputAutoSilenceRms && peak >= 0.010f) {
            const float post_base_rms = std::max(0.0001f, rms * base_gain);
            target_auto = clampf(kOutputAutoTargetRms / post_base_rms, kOutputAutoMinGain, kOutputAutoMaxGain);
            const float post_base_peak = std::max(0.0001f, peak * base_gain);
            const float peak_safe_auto = (ceiling * kSampleScale * 0.98f) / post_base_peak;
            target_auto = std::min(target_auto, clampf(peak_safe_auto, 0.45f, kOutputAutoMaxGain));
        }

        const float base_coeff = (target_auto < output_auto_gain) ? kOutputAutoReleaseCoeff : kOutputAutoAttackCoeff;
        const float block_frames = static_cast<float>(std::max(1, sample_rate)) * 0.02f;
        const float blocks = std::max(1.0f, static_cast<float>(frames) / std::max(1.0f, block_frames));
        const float coeff = 1.0f - std::pow(1.0f - base_coeff, blocks);
        output_auto_gain += coeff * (target_auto - output_auto_gain);
        output_auto_gain = clampf(output_auto_gain, 0.45f, kOutputAutoMaxGain);
        output_auto_gain_current.store(output_auto_gain, std::memory_order_relaxed);

        const float gain = base_gain * output_auto_gain;
        if (std::fabs(gain - 1.0f) > 0.001f || ceiling < 32766.0f) {
            for (size_t i = 0; i < frames; ++i) {
                pcm[i] = static_cast<int16_t>(soft_limit_i16(static_cast<float>(pcm[i]) * gain, ceiling));
            }
        }
    }

    // ── miniaudio callbacks ──

    void append_pre_roll(const int16_t* src, const int16_t* raw_src, size_t frames) {
        if (!src || !raw_src || frames == 0) return;
        pre_roll_buffer.insert(pre_roll_buffer.end(), src, src + frames);
        raw_pre_roll_buffer.insert(raw_pre_roll_buffer.end(), raw_src, raw_src + frames);
        pre_roll_agc_max_gain = std::max(pre_roll_agc_max_gain, agc_gain);
        if (pre_roll_buffer.size() > PRE_ROLL_MAX_SAMPLES) {
            const size_t excess = pre_roll_buffer.size() - PRE_ROLL_MAX_SAMPLES;
            pre_roll_buffer.erase(pre_roll_buffer.begin(), pre_roll_buffer.begin() + static_cast<std::ptrdiff_t>(excess));
            if (raw_pre_roll_buffer.size() >= excess)
                raw_pre_roll_buffer.erase(raw_pre_roll_buffer.begin(), raw_pre_roll_buffer.begin() + static_cast<std::ptrdiff_t>(excess));
        }
    }

    void append_speech(const int16_t* src, const int16_t* raw_src, size_t frames) {
        if (!src || !raw_src || frames == 0) return;
        if (!collecting_speech) {
            ++asr_segment_gen;
            raw_speech_buffer = raw_pre_roll_buffer;
            speech_buffer = pre_roll_buffer;
            speech_output_buffer = pre_roll_buffer;
            raw_pre_roll_buffer.clear();
            pre_roll_buffer.clear();
            collecting_speech = true;
            current_debug_seg_id = debug_dump_enabled ? dump_seg_counter++ : -1;
            segment_start_prob = vad_last_prob.load(std::memory_order_relaxed);
            segment_agc_start_gain = agc_gain;
            segment_agc_max_gain = std::max(pre_roll_agc_max_gain, agc_gain);
            pre_roll_agc_max_gain = agc_gain;
            speech_active_samples = 0;
            asr_gate_early_released = false;
        }
        raw_speech_buffer.insert(raw_speech_buffer.end(), raw_src, raw_src + frames);
        speech_buffer.insert(speech_buffer.end(), src, src + frames);
        segment_agc_max_gain = std::max(segment_agc_max_gain, agc_gain);
        speech_active_samples += frames;
        if (!asr_gate_early_released && rt_rvc && sample_rate > 0 &&
            speech_active_samples >= static_cast<size_t>(sample_rate) * 220 / 1000) {
            const size_t early_len = std::min(speech_buffer.size(), static_cast<size_t>(sample_rate) * 360 / 1000);
            const int16_t* early_src = speech_buffer.data() + (speech_buffer.size() - early_len);
            const VcAsrGateAnalysis early = vc_analyze_asr_gate_audio(early_src, early_len, sample_rate, segment_start_prob);
            if (vc_asr_gate_accepts_early_acoustic(early)) {
                rt_rvc->complete_asr_gate(true);
                asr_gate_early_released = true;
                std::fprintf(stderr,
                             "[VC-RVC] asr_gate_early_acoustic_release dur=%d rms=%.1f peak=%.1f zcr=%.3f per=%.3f start=%.3f\n",
                             early.duration_ms, early.rms, early.peak, early.zcr, early.periodicity, early.start_prob);
                std::fflush(stderr);
            }
        }
        if (speech_buffer.size() > SPEECH_BUFFER_MAX_SAMPLES) {
            finish_speech_segment(vad_last_prob.load(std::memory_order_relaxed));
        }
    }

    void append_speech_output(const int16_t* src, size_t frames) {
        if (!src || frames == 0 || !collecting_speech) return;
        speech_output_buffer.insert(speech_output_buffer.end(), src, src + frames);
        if (speech_output_buffer.size() > SPEECH_BUFFER_MAX_SAMPLES) {
            speech_output_buffer.erase(speech_output_buffer.begin(), speech_output_buffer.begin() +
                static_cast<std::ptrdiff_t>(speech_output_buffer.size() - SPEECH_BUFFER_MAX_SAMPLES));
        }
    }

    void finish_speech_segment(float end_prob) {
        if (!collecting_speech || speech_buffer.empty()) {
            collecting_speech = false;
            raw_speech_buffer.clear();
            speech_buffer.clear();
            current_debug_seg_id = -1;
            speech_active_samples = 0;
            asr_gate_early_released = false;
            return;
        }
        if (spec_pending_gen != 0) {
            // Hold expired without speech resuming: commit the speculative job
            // queued during the hold window instead of queueing a duplicate.
            {
                std::lock_guard<std::mutex> lk(laugh_asr_mutex);
                spec_decisions[spec_pending_gen] = true;
            }
            laugh_asr_cv.notify_all();
            std::fprintf(stderr, "[VC-RVC] spec_commit gen=%llu\n",
                         static_cast<unsigned long long>(spec_pending_gen));
            std::fflush(stderr);
            spec_pending_gen = 0;
        } else if (!queue_laugh_asr_segment(speech_buffer, speech_output_buffer, sample_rate, 0, current_debug_seg_id)) {
            asr_gate_reject_count.fetch_add(1, std::memory_order_relaxed);
            std::fprintf(stderr, "[VC-RVC] segment_drop queue_unavailable samples=%zu\n", speech_output_buffer.size());
            std::fflush(stderr);
        }
        if (debug_dump_enabled) {
            vc_dump_segment_async(dump_dir, current_debug_seg_id, std::move(raw_speech_buffer),
                                  std::move(speech_buffer), std::move(speech_output_buffer),
                                  static_cast<uint32_t>(sample_rate),
                                  noise_profile.noise_floor_rms, noise_profile.noise_rms_stddev,
                                  noise_profile.peak_p95, noise_profile.silero_p90,
                                  segment_start_prob, end_prob,
                                  underrun_count.load(std::memory_order_relaxed),
                                  overflow_count.load(std::memory_order_relaxed),
                                  segment_agc_start_gain, segment_agc_max_gain);
        } else {
            raw_speech_buffer.clear();
            speech_buffer.clear();
            speech_output_buffer.clear();
        }
        collecting_speech = false;
        current_debug_seg_id = -1;
        speech_active_samples = 0;
        asr_gate_early_released = false;
        raw_speech_buffer.clear();
        speech_buffer.clear();
        speech_output_buffer.clear();
    }

    void trace_noise_profile(double current_time_ms) {
        if (!noise_profile.ready || (current_time_ms - noise_profile.last_trace_time_ms) < 5000.0) return;
        noise_profile.last_trace_time_ms = current_time_ms;
        std::fprintf(stderr,
                     "[VC] noise_profile ready=1 floor=%.6f std=%.6f peak_p95=%.6f p90_silero=%.3f low_gain=%d samples=%d\n",
                     noise_profile.noise_floor_rms, noise_profile.noise_rms_stddev,
                     noise_profile.peak_p95, noise_profile.silero_p90,
                     noise_profile.low_gain_mic ? 1 : 0, noise_profile.count);
        std::fflush(stderr);
    }

    void append_laugh_asr_roll(const int16_t* pcm, size_t frames) {
        if (!laugh_asr_assist_enabled || !pcm || frames == 0) return;
        laugh_asr_roll16k.insert(laugh_asr_roll16k.end(), pcm, pcm + frames);
        constexpr size_t max_samples = 16000 * 3;
        if (laugh_asr_roll16k.size() > max_samples) {
            laugh_asr_roll16k.erase(laugh_asr_roll16k.begin(), laugh_asr_roll16k.begin() +
                static_cast<std::ptrdiff_t>(laugh_asr_roll16k.size() - max_samples));
        }
    }

    void accept_laugh_detect(int64_t now_ms, const char* source) {
        laugh_detect_count.fetch_add(1, std::memory_order_relaxed);
        last_laugh_detect_ms.store(now_ms, std::memory_order_relaxed);
        std::fprintf(stderr, "[VC-RVC] vad_laugh_detect source=%s\n", source ? source : "unknown");
        std::fflush(stderr);
        // Moonshine text accept often arrives after the laugh segment has ended.
        // Feed one silent RT chunk so the existing ASR-gated replacement path can
        // still emit a follow-up laugh expression without re-enabling acoustic VAD triggers.
        if (rt_rvc && rt_rvc->running.load(std::memory_order_relaxed) && rt_rvc->chunk_samples > 0) {
            std::vector<int16_t> chase(rt_rvc->chunk_samples, 0);
            rt_rvc->feed(chase.data(), chase.size());
            std::fprintf(stderr, "[VC-RVC] vad_laugh_chase_chunk samples=%zu\n", chase.size());
            std::fflush(stderr);
        }
    }

    bool queue_laugh_asr_job(std::vector<int16_t> job, const VcAsrGateAnalysis& gate_analysis = {},
                             std::vector<int16_t> audio = {}, int audio_sr = 0, uint64_t spec_gen = 0,
                             int debug_seg_id = -1, uint64_t segment_gen = 0) {
        if ((!laugh_asr_assist_enabled && !raw_rvc_mode) || job.size() < static_cast<size_t>(16000 / 4)) return false;
        if (job.size() > static_cast<size_t>(16000 * 4)) {
            job.erase(job.begin(), job.begin() + static_cast<std::ptrdiff_t>(job.size() - static_cast<size_t>(16000 * 4)));
        }
        {
            std::lock_guard<std::mutex> lk(laugh_asr_mutex);
            if (laugh_asr_jobs.size() >= 4) return false;
            laugh_asr_jobs.push_back({std::move(job), std::move(audio), audio_sr, gate_analysis, spec_gen, debug_seg_id, segment_gen});
            laugh_asr_last_decision = "pending";
            laugh_asr_last_outcome = "pending";
            laugh_asr_last_text.clear();
            laugh_asr_pending.store(true, std::memory_order_relaxed);
        }
        laugh_asr_cv.notify_one();
        return true;
    }

    bool queue_laugh_asr_segment(const std::vector<int16_t>& segment,
                                 const std::vector<int16_t>& processed_segment, int sr,
                                 uint64_t spec_gen = 0, int debug_seg_id = -1) {
        if (!laugh_asr_assist_enabled && !raw_rvc_mode) return false;
        if (!raw_rvc_mode && (laugh_asr_moonshine_dll_dir.empty() || laugh_asr_moonshine_model_root.empty())) return false;
        if (segment.empty() || sr <= 0) return false;
        const VcAsrGateAnalysis gate_analysis = vc_analyze_asr_gate_audio(segment.data(), segment.size(), sr, segment_start_prob);
        const size_t max_src = static_cast<size_t>(std::max(1, sr)) * 4;
        const size_t len = std::min(segment.size(), max_src);
        const int16_t* src = segment.data() + (segment.size() - len);
        auto job = resample_linear_i16(src, len, sr, 16000);
        return queue_laugh_asr_job(std::move(job), gate_analysis, processed_segment, sr, spec_gen, debug_seg_id, asr_segment_gen);
    }

    void queue_laugh_asr_or_accept(int64_t now_ms) {
        (void)now_ms;
        if (!laugh_asr_assist_enabled || laugh_asr_moonshine_dll_dir.empty() || laugh_asr_moonshine_model_root.empty()) {
            return;
        }
        std::vector<int16_t> job;
        const size_t keep = std::min(laugh_asr_roll16k.size(), static_cast<size_t>(16000 * 2));
        if (keep >= static_cast<size_t>(16000 / 4)) {
            job.assign(laugh_asr_roll16k.end() - static_cast<std::ptrdiff_t>(keep), laugh_asr_roll16k.end());
        }
        (void)queue_laugh_asr_job(std::move(job));
    }

    void start_laugh_asr_worker() {
        if ((!laugh_asr_assist_enabled && !raw_rvc_mode) || laugh_asr_thread.joinable()) return;
        {
            std::lock_guard<std::mutex> lk(laugh_asr_mutex);
            laugh_asr_stop = false;
            spec_decisions.clear();
        }
        spec_pending_gen = 0;
        laugh_asr_thread = std::thread(&VcAudioState::laugh_asr_worker_loop, this);
    }

    void stop_laugh_asr_worker() {
        {
            std::lock_guard<std::mutex> lk(laugh_asr_mutex);
            laugh_asr_stop = true;
        }
        laugh_asr_cv.notify_one();
        if (laugh_asr_thread.joinable()) laugh_asr_thread.join();
    }

    // Call after closing the route and joining its worker. No captured PCM or
    // unfinished job may survive a model generation change.
    void reset_conversion_history() {
        std::lock_guard<std::mutex> route_lock(route_mutex);
        {
            std::lock_guard<std::mutex> lock(laugh_asr_mutex);
            laugh_asr_jobs.clear();
            spec_decisions.clear();
            laugh_asr_pending.store(false, std::memory_order_relaxed);
        }
        spec_pending_gen = 0;
        collecting_speech = false;
        raw_pre_roll_buffer.clear();
        pre_roll_buffer.clear();
        raw_speech_buffer.clear();
        speech_buffer.clear();
        speech_output_buffer.clear();
        laugh_asr_roll16k.clear();
        vad_16k_count = 0;
        vad_speech_active.store(false, std::memory_order_relaxed);
        vad_hold_frames_remaining = 0;
        current_debug_seg_id = -1;
        speech_active_samples = 0;
        asr_gate_early_released = false;
        ring.clear();
        monitor_ring.clear();
        cancel_filler();
    }

    void laugh_asr_worker_loop() {
#ifdef MOBMATE_VC_BOUNDARY_TEST
        VcBoundaryAsrBridge bridge;
#else
        mobmate::MoonshineBridge bridge;
#endif
        bool loaded = false;
        std::string load_error;
        // Jobs are FIFO on this worker. Keep a load fault tied to the physical
        // segment, including snapshots cancelled before that segment ends.
        uint64_t fault_segment_gen = 0;
        std::string segment_load_fault;
        while (true) {
            std::vector<int16_t> job;
            std::vector<int16_t> segment_audio;
            int segment_sr = 0;
            VcAsrGateAnalysis gate_analysis;
            uint64_t job_spec_gen = 0;
            int job_debug_seg_id = -1;
            uint64_t job_segment_gen = 0;
            {
                std::unique_lock<std::mutex> lk(laugh_asr_mutex);
                laugh_asr_cv.wait(lk, [&] { return laugh_asr_stop || !laugh_asr_jobs.empty(); });
                if (laugh_asr_stop) break;
                SegmentAsrJob queued = std::move(laugh_asr_jobs.front());
                laugh_asr_jobs.pop_front();
                job = std::move(queued.asr16k);
                segment_audio = std::move(queued.audio);
                segment_sr = queued.audio_sr;
                gate_analysis = queued.analysis;
                job_spec_gen = queued.spec_gen;
                job_debug_seg_id = queued.debug_seg_id;
                job_segment_gen = queued.segment_gen;
            }

            std::string text;
            float asr_confidence = -1.0f;
            std::string decision = "pending";
            const char* asr_outcome = "unavailable";
            const int64_t job_start_ms = vc_steady_now_ms();
            // Speed (2026-07-12): run one-shot RVC inference concurrently with the
            // Moonshine ASR gate. The ONNX session is still used by at most one
            // thread at a time (worker is sequential); rejected segments simply
            // discard the converted audio without emitting it.
            std::future<bool> rvc_future;
            if (!segment_audio.empty() && segment_sr > 0 && rt_rvc) {
                int16_t* seg_data = segment_audio.data();
                const size_t seg_len = segment_audio.size();
                const int seg_sr = segment_sr;
                rvc_future = std::async(std::launch::async, [this, seg_data, seg_len, seg_sr] {
                    VcRawOutputGainState raw_gain;
                    if (raw_rvc_mode) raw_gain.reset(playback_gain);
                    return vc_apply_loaded_inference(onnx, seedvc, seg_data, seg_len, seg_sr,
                        raw_rvc_mode ? &raw_gain : nullptr);
                });
            }
            if (raw_rvc_mode) {
                decision = "accept_raw_rvc";
                asr_outcome = "bypassed_raw";
            } else if (!loaded) {
                if (!bridge.init(laugh_asr_moonshine_dll_dir)) {
                    load_error = "moonshine_init_failed";
                    asr_outcome = "init_failed";
                } else {
                    auto model = vc_resolve_moonshine_model(laugh_asr_moonshine_model_root, laugh_asr_language);
                    mobmate::MoonshineBridge::LoadOptions opts;
                    opts.vad_window_duration = 0.15;
                    opts.vad_look_behind_sample_count = 20000;
                    opts.vad_threshold = 0.30;
                    opts.max_tokens_per_second = 13.0;
                    opts.force_word_timestamps = true; // needed for confidence gate (Task A)
                    if (!model.valid)
                        std::fprintf(stderr, "[VC-RVC] invalid private Japanese tiny model directory\n");
                    loaded = model.valid && bridge.load_model(model.path, model.arch, opts);
                    if (!loaded) load_error = "moonshine_model_load_failed";
                    if (!loaded) asr_outcome = "model_load_failed";
                }
            }
            if (!raw_rvc_mode && loaded) {
                auto pcm_bytes = vc_pcm16_to_bytes(job);
                auto normalized = mobmate::normalize_final_chunk_for_asr(
                    pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), -7.0);
                if (!normalized.empty() && normalized.size() == pcm_bytes.size()) {
                    job = vc_bytes_to_pcm16(normalized);
                }
                auto f32 = vc_i16_to_f32(job);
                auto results = bridge.transcribeAccurate(f32.data(), static_cast<int>(f32.size()), 16000, 600, 600);
                // An unsuccessful result (including the bridge's all-passes-empty
                // failure) is not a successful empty transcript. Neither is proof
                // of silence, and neither changes the legacy decision below.
                asr_outcome = results.empty() ? "no_result" : "result_failed";
                if (!results.empty() && results[0].success) {
                    text = vc_trim_copy(results[0].text);
                    asr_outcome = text.empty() ? "normal_empty" : "text";
                    asr_confidence = results[0].confidence_score;
                    // Pseudo-confidence via multi-pass agreement when word
                    // confidence is unavailable (ja/ko models lack the
                    // word-timestamp assets). Second pass uses a different
                    // padding context; hallucinations decode unstably.
                    if (asr_confidence < 0.0f && !text.empty()) {
                        auto results2 = bridge.transcribeAccurate(f32.data(), static_cast<int>(f32.size()), 16000, 360, 840);
                        if (!results2.empty() && results2[0].success) {
                            const std::string text2 = vc_trim_copy(results2[0].text);
                            asr_confidence = vc_text_similarity(text, text2);
                        }
                    }
                }
                const bool acoustic_accept = vc_asr_gate_accepts_nonliteral_text(gate_analysis);
                const bool low_confidence = vc_asr_gate_rejects_low_confidence(gate_analysis, asr_confidence);
                const bool text_rate_anomaly = vc_asr_gate_rejects_text_rate(gate_analysis, text);
                // A long, stable multi-pass transcription is stronger evidence
                // than the coarse audio-only hum gate. This rescues clearly
                // articulated phrases that happen to have a high voiced ratio,
                // without matching any literal word or phrase.
                const bool strong_text_evidence = asr_confidence >= 0.80f &&
                    vc_utf8_codepoint_count(text) >= 5 && !text_rate_anomaly;
                const bool effective_acoustic_accept = acoustic_accept || strong_text_evidence;
                decision = vc_is_laugh_asr_text(text, laugh_asr_tokens)
                    ? "accept_laugh_text"
                    : (text.empty()
                        ? "reject_no_asr_text"
                        : (!effective_acoustic_accept
                            ? "reject_weak_short_audio"
                            : (low_confidence
                                ? "reject_low_asr_confidence"
                                : (text_rate_anomaly ? "reject_text_rate_anomaly" : "accept_asr_text"))));
            } else if (!raw_rvc_mode) {
                decision = load_error.empty() ? "asr_unavailable" : load_error;
                laugh_asr_error_count.fetch_add(1, std::memory_order_relaxed);
            }

            if (!raw_rvc_mode && job_segment_gen != 0) {
                if (decision == "moonshine_init_failed" || decision == "moonshine_model_load_failed") {
                    fault_segment_gen = job_segment_gen;
                    segment_load_fault = decision;
                }
                // A successful retry of a cancelled snapshot is still the same
                // faulted utterance. Preserve the current recognizer outcome,
                // but only a new physical segment may regain release permission.
                if (fault_segment_gen == job_segment_gen) decision = segment_load_fault;
            }

            const int64_t asr_done_ms = vc_steady_now_ms();
            std::fprintf(stderr, "[VC-RVC] asr_outcome=%s decision=%s\n", asr_outcome, decision.c_str());
            std::fflush(stderr);
#ifdef MOBMATE_VC_BOUNDARY_TEST
            vc_boundary_test_event("asr_ready", asr_outcome, decision.c_str());
#endif
            // Speculative job: block until the capture thread decides the fate of
            // this segment (commit at hold expiry / cancel on speech resume).
            if (job_spec_gen != 0) {
                bool spec_cancelled = false;
                bool stopping = false;
                {
                    std::unique_lock<std::mutex> lk(laugh_asr_mutex);
                    laugh_asr_cv.wait(lk, [&] {
                        return laugh_asr_stop || spec_decisions.count(job_spec_gen) != 0;
                    });
                    if (laugh_asr_stop) {
                        stopping = true;
                    } else {
                        spec_cancelled = !spec_decisions[job_spec_gen];
                        spec_decisions.erase(job_spec_gen);
                    }
                }
                if (stopping) {
                    if (rvc_future.valid()) (void)rvc_future.get();
#ifdef MOBMATE_VC_BOUNDARY_TEST
                    vc_boundary_test_event("stopping", asr_outcome, decision.c_str());
#endif
                    break;
                }
                if (spec_cancelled) {
                    if (rvc_future.valid()) (void)rvc_future.get();
                    std::fprintf(stderr, "[VC-RVC] spec_discard gen=%llu asr_ms=%lld\n",
                                 static_cast<unsigned long long>(job_spec_gen),
                                 static_cast<long long>(asr_done_ms - job_start_ms));
                    std::fflush(stderr);
                    {
                        std::lock_guard<std::mutex> lk(laugh_asr_mutex);
                        laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);
                    }
#ifdef MOBMATE_VC_BOUNDARY_TEST
                    vc_boundary_test_event("cancelled", asr_outcome, decision.c_str());
#endif
                    continue;
                }
            }
            const int64_t now_ms = asr_done_ms;
            laugh_asr_checked_count.fetch_add(1, std::memory_order_relaxed);
            last_laugh_asr_ms.store(now_ms, std::memory_order_relaxed);
            {
                std::lock_guard<std::mutex> lk(laugh_asr_mutex);
                laugh_asr_last_text = text;
                laugh_asr_last_decision = decision;
                laugh_asr_last_outcome = asr_outcome;
                laugh_asr_last_gate_analysis = gate_analysis;
                laugh_asr_last_confidence = asr_confidence;
            }
            // An init/model fault discards this segment's speculative conversion;
            // a later successful load must only release a new accepted segment.
            const bool accepted = decision == "accept_raw_rvc" || decision == "accept_laugh_text" || decision == "accept_asr_text" ||
                decision == "asr_unavailable";
            if (accepted) {
                asr_gate_accept_count.fetch_add(1, std::memory_order_relaxed);
                bool converted = false;
                if (rvc_future.valid()) {
                    converted = rvc_future.get();
                    if (converted) {
                        std::lock_guard<std::mutex> route_lock(route_mutex);
                        if (output_blocked()) {
                            converted = false;
                        } else {
                            std::vector<int16_t> rvc_raw_dump;
                            if (debug_dump_enabled && job_debug_seg_id >= 0)
                                rvc_raw_dump = segment_audio;
                            size_t head_trimmed = 0;
                            int64_t onset_soften_sample = -1;
                            if (!raw_rvc_mode) {
                                head_trimmed = rt_rvc->asr_gate_trim_head_noise(segment_audio);
                                normalize_segment_output(segment_audio);
                                soften_segment_onset(segment_audio, &onset_soften_sample);
                            }
                            if (!segment_audio.empty()) {
                                cancel_filler();
                                rt_rvc->post_write(segment_audio.data(), segment_audio.size());
                            }
                            if (wav_output_enabled && !segment_audio.empty())
                                vc_save_final_wav_async(wav_output_directory, segment_audio, static_cast<uint32_t>(segment_sr));
                            std::fprintf(stderr, "[VC-RVC] segment_release samples=%zu head_trim=%zu\n",
                                         segment_audio.size(), head_trimmed);
                            if (debug_dump_enabled && job_debug_seg_id >= 0) {
                                vc_dump_rvc_final_async(dump_dir, job_debug_seg_id, std::move(rvc_raw_dump),
                                                     std::move(segment_audio), static_cast<uint32_t>(segment_sr),
                                                     head_trimmed, onset_soften_sample, decision, text, asr_confidence);
                            }
                        }
                    }
                }
                if (!converted && debug_dump_enabled && job_debug_seg_id >= 0) {
                    vc_dump_rvc_final_async(dump_dir, job_debug_seg_id, {}, {},
                                            static_cast<uint32_t>(segment_sr), 0, -1,
                                            decision + "_conversion_failed", text, asr_confidence);
                }
                const int64_t done_ms = vc_steady_now_ms();
                std::fprintf(stderr,
                             "[VC-RVC] asr_gate_accept decision=%s conf=%.3f converted=%d asr_ms=%lld total_ms=%lld active=%.3f voiced=%.3f air=%.3f zcr=%.3f per=%.3f dur=%d text='%s'\n",
                             decision.c_str(), asr_confidence, converted ? 1 : 0,
                             static_cast<long long>(asr_done_ms - job_start_ms),
                             static_cast<long long>(done_ms - job_start_ms),
                             gate_analysis.active_ratio, gate_analysis.voiced_active_ratio,
                             gate_analysis.air_active_ratio, gate_analysis.zcr,
                             gate_analysis.periodicity, gate_analysis.duration_ms, text.c_str());
                std::fflush(stderr);
            } else if (!raw_rvc_mode) {
                if (rvc_future.valid())
                    (void)rvc_future.get(); // discard speculative conversion
                if (debug_dump_enabled && job_debug_seg_id >= 0) {
                    vc_dump_rvc_final_async(dump_dir, job_debug_seg_id, {}, {},
                                            static_cast<uint32_t>(segment_sr), 0, -1,
                                            decision, text, asr_confidence);
                }
                asr_gate_reject_count.fetch_add(1, std::memory_order_relaxed);
                laugh_asr_rejected_speech_count.fetch_add(1, std::memory_order_relaxed);
                std::fprintf(stderr,
                             "[VC-RVC] asr_gate_reject decision=%s conf=%.3f active=%.3f voiced=%.3f air=%.3f zcr=%.3f per=%.3f dur=%d text='%s'\n",
                             decision.c_str(), asr_confidence, gate_analysis.active_ratio,
                             gate_analysis.voiced_active_ratio, gate_analysis.air_active_ratio,
                             gate_analysis.zcr, gate_analysis.periodicity, gate_analysis.duration_ms, text.c_str());
                std::fflush(stderr);
            }
            {
                std::lock_guard<std::mutex> lk(laugh_asr_mutex);
                laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);
            }
#ifdef MOBMATE_VC_BOUNDARY_TEST
            vc_boundary_test_event("job_done", asr_outcome, decision.c_str());
#endif
        }
    }

    void score_vad_frame() {
        if (!vad || vad_16k_count < 512) return;
#ifdef MOBMATE_VC_BOUNDARY_TEST
        double prob = vc_boundary_test_vad();
#else
        double prob = silero_vad_score(vad, vad_16k_buf, 512, 16000);
#endif
        if (prob < 0.0) {
            vad_16k_count = 0;
            return;
        }

        float p = static_cast<float>(prob);
        vad_last_prob.store(p, std::memory_order_relaxed);
        append_laugh_asr_roll(vad_16k_buf, 512);
        bool was_active = vad_speech_active.load(std::memory_order_relaxed);
        VcFrameStats fs = vc_compute_stats(vad_16k_buf, 512);
        double current_time_ms = sample_rate > 0
            ? static_cast<double>(frames_captured.load(std::memory_order_relaxed)) * 1000.0 / sample_rate
            : 0.0;

        float open_rms = 220.0f * kSampleScale;
        float open_peak = 700.0f * kSampleScale;
        if (noise_profile.ready) {
            open_rms = std::max(open_rms, noise_profile.noise_floor_rms + std::max(180.0f * kSampleScale, noise_profile.noise_rms_stddev * 1.4f));
            open_peak = std::max(open_peak, noise_profile.peak_p95 + std::max(650.0f * kSampleScale, noise_profile.noise_rms_stddev * 1.8f));
            open_rms = std::min(open_rms, 1800.0f * kSampleScale);
            open_peak = std::min(open_peak, 5000.0f * kSampleScale);
        }

        const float zcr = vc_calc_zcr_i16(vad_16k_buf, 512);
        const float periodicity = vc_calc_periodicity_i16(vad_16k_buf, 512);
        const float pressure_score = dsp.pressure.pressure_score.load(std::memory_order_relaxed);
        const bool breath_like = !was_active && zcr >= 0.10f && periodicity < 0.12f && p < 0.70f;
        // Tobacco/exhale bursts often score as mid-prob speech but have diffuse
        // air/noise shape. Keep them out of RT-RVC; true speech is recovered by pre-roll.
        const bool mid_prob_air_like = !was_active && p < 0.78f &&
            ((zcr >= 0.085f && periodicity < 0.70f) || (zcr >= 0.060f && periodicity < 0.58f));
        const bool high_zcr_air_like = !was_active && zcr >= 0.20f && periodicity < 0.32f && p < 0.84f;
        const bool pressure_exhale_like = !was_active && pressure_score >= 0.16f && p < 0.94f &&
            periodicity < 0.78f && (fs.rms >= open_rms * 0.65f || fs.peak >= open_peak * 0.65f);
        const bool exhale_like = !was_active && !breath_like &&
            ((zcr >= 0.065f && periodicity < 0.18f && p < 0.90f) || high_zcr_air_like || mid_prob_air_like) &&
            (fs.rms >= open_rms * 0.65f || fs.peak >= open_peak * 0.65f);
        float effective_start_threshold = vad_start_threshold;
        if (noise_profile.ready)
            effective_start_threshold = std::max(effective_start_threshold, std::min(0.72f, noise_profile.silero_p90 + 0.22f));
        if (breath_like)
            effective_start_threshold = std::max(effective_start_threshold, 0.62f);
        if (exhale_like)
            effective_start_threshold = std::max(effective_start_threshold, 0.84f);
        if (pressure_exhale_like)
            effective_start_threshold = std::max(effective_start_threshold, 0.94f);

        const bool has_open_energy = fs.rms >= (open_rms * 0.75f) || fs.peak >= (open_peak * 0.75f);
        const bool noisy_start = breath_like || exhale_like || pressure_exhale_like;
        const bool strong_voice_start = p >= 0.92f || periodicity >= 0.24f;
        bool hard_start = !was_active && p >= effective_start_threshold && has_open_energy &&
            (!noisy_start || (strong_voice_start && !pressure_exhale_like));
        const float relaxed_start_threshold = std::max(vad_soft_start_threshold, effective_start_threshold - 0.04f);
        bool soft_start = !was_active && !noisy_start && p >= relaxed_start_threshold && (fs.rms >= open_rms || fs.peak >= open_peak);
        float energy_start_rms = std::min(std::max(open_rms * 2.2f, 650.0f * kSampleScale), 2200.0f * kSampleScale);
        float energy_start_peak = std::min(std::max(open_peak * 2.0f, 2200.0f * kSampleScale), 6500.0f * kSampleScale);
        float energy_prob = noise_profile.ready ? std::max(relaxed_start_threshold, noise_profile.silero_p90 + 0.12f) : relaxed_start_threshold;
        bool energy_start = !was_active && !noisy_start && noise_profile.ready && p >= energy_prob &&
            ((fs.rms >= energy_start_rms && fs.peak >= open_peak) || fs.peak >= energy_start_peak);
        bool energy_continue = was_active && noise_profile.ready &&
            p >= std::max(0.12f, noise_profile.silero_p90 + 0.05f) &&
            (fs.rms >= open_rms || fs.peak >= open_peak);
        bool continue_speech = was_active && (p >= vad_continue_threshold || energy_continue);
        bool active = hard_start || soft_start || energy_start || continue_speech;
        if (active) {
            vad_hold_frames_remaining = vad_hold_frames;
            if (spec_pending_gen != 0) {
                // Speech resumed inside the hold window: the speculative job no
                // longer matches the final segment, discard its result.
                {
                    std::lock_guard<std::mutex> lk(laugh_asr_mutex);
                    spec_decisions[spec_pending_gen] = false;
                }
                laugh_asr_cv.notify_all();
                std::fprintf(stderr, "[VC-RVC] spec_cancel gen=%llu (speech resumed)\n",
                             static_cast<unsigned long long>(spec_pending_gen));
                std::fflush(stderr);
                spec_pending_gen = 0;
            }
        } else if (was_active && vad_hold_frames_remaining > 0) {
            --vad_hold_frames_remaining;
            active = true;
            if (collecting_speech && spec_pending_gen == 0 && rt_rvc &&
                vad_hold_frames_remaining == vad_hold_frames - kSpecDelayFrames) {
                const uint64_t gen = spec_next_gen + 1;
                if (queue_laugh_asr_segment(speech_buffer, speech_output_buffer, sample_rate, gen,
                                            current_debug_seg_id)) {
                    spec_next_gen = gen;
                    spec_pending_gen = gen;
                    std::fprintf(stderr, "[VC-RVC] spec_queue gen=%llu samples=%zu\n",
                                 static_cast<unsigned long long>(gen), speech_output_buffer.size());
                    std::fflush(stderr);
                }
            }
        }

        if (active && !was_active && filler) {
            filler_monitor_ring.clear();
            filler->onset(!output_blocked() && rt_rvc &&
                          rt_rvc->running.load(std::memory_order_relaxed));
        }
        vad_speech_active.store(active, std::memory_order_relaxed);
        // Laugh replacement is gated by Moonshine text on completed speech segments.
        // Acoustic/VAD laugh candidates must not trigger replacement or ASR jobs.
        if (!active && noise_profile.should_add_sample(fs.rms, fs.peak, p)) {
            noise_profile.add_sample(fs.rms, fs.peak, p);
            trace_noise_profile(current_time_ms);
        }
        vad_16k_count = 0;
    }

    static void on_capture(ma_device* dev, void* /*out*/, const void* in, ma_uint32 frames) {
        auto* st = static_cast<VcAudioState*>(dev->pUserData);
        if (!st || !in) return;
        std::lock_guard<std::mutex> route_lock(st->route_mutex);
        if (st->output_blocked()) return;
        const int16_t* raw_src = static_cast<const int16_t*>(in);
        const int16_t* src = raw_src;

        // ── AGC: automatic input gain normalisation ──
        if (st->agc_enabled && frames > 0) {
            // 1. Measure raw input RMS
            double raw_sum = 0.0;
            for (ma_uint32 i = 0; i < frames; ++i) {
                float s = raw_src[i] * kSampleScale;
                raw_sum += static_cast<double>(s) * s;
            }
            float raw_rms = static_cast<float>(std::sqrt(raw_sum / static_cast<double>(frames)));

            // 2. Smooth RMS (EMA) – tracks raw input, not AGC output
            if (st->agc_smoothed_rms < 0.0001f && raw_rms > 0.0001f)
                st->agc_smoothed_rms = raw_rms;
            else
                st->agc_smoothed_rms += kAgcRmsSmooth * (raw_rms - st->agc_smoothed_rms);

            // 3. Compute target gain from smoothed RMS
            float target_gain = 1.0f;
            if (raw_rms < kAgcSilenceRms) {
                target_gain = 1.0f;
            } else if (st->agc_smoothed_rms > 0.0001f) {
                target_gain = kAgcTargetRms / st->agc_smoothed_rms;
            }
            // Before the noise profile is ready, a quiet first utterance can
            // drive the EMA target above +20 dB and make RVC emit a loud
            // vowel/growl artifact. Keep startup gain conservative; normal
            // low-gain microphone recovery remains available after calibration.
            const float effective_max_gain = st->noise_profile.ready ? kAgcMaxGain : 4.0f;
            target_gain = std::max(kAgcMinGain, std::min(effective_max_gain, target_gain));

            // 4. Smooth gain: fast decrease (loud signal), slow increase (quiet signal)
            float coeff = (target_gain < st->agc_gain) ? kAgcReleaseCoeff : kAgcAttackCoeff;
            st->agc_gain += coeff * (target_gain - st->agc_gain);

            // 5. Apply gain without mutating the AGC envelope; soften only samples near clipping.
            st->agc_buffer.resize(static_cast<size_t>(frames));
            constexpr float agc_output_ceiling = kSampleMax * 0.98f;
            for (ma_uint32 i = 0; i < frames; ++i) {
                float v = static_cast<float>(raw_src[i]) * st->agc_gain;
                st->agc_buffer[i] = static_cast<int16_t>(soft_limit_i16(v, agc_output_ceiling));
            }
            src = st->agc_buffer.data();
            st->agc_current_gain.store(st->agc_gain, std::memory_order_relaxed);
        }

        // ── Measure post-AGC RMS / peak (for UI meter) ──
        double sum_sq = 0.0;
        float peak = 0.0f;
        for (ma_uint32 i = 0; i < frames; ++i) {
            float s = std::fabs(src[i] * kSampleScale);
            sum_sq += static_cast<double>(s) * static_cast<double>(s);
            if (s > peak) peak = s;
        }
        float rms = frames > 0 ? static_cast<float>(std::sqrt(sum_sq / static_cast<double>(frames))) : 0.0f;
        st->input_rms_level.store(rms, std::memory_order_relaxed);
        st->input_peak_level.store(peak, std::memory_order_relaxed);
        if (rms <= 0.00001f && peak <= 0.00001f)
            st->zero_input_callbacks.fetch_add(1, std::memory_order_relaxed);
        else
            st->zero_input_callbacks.store(0, std::memory_order_relaxed);

        // ── SileroVAD gating: downsample 48k→16k, accumulate 512 samples, score ──
        if (st->vad) {
            // Simple decimation by 3 (48000/16000). Score exactly every 512 samples;
            // preserve remainder instead of dropping ~128 samples every 4 callbacks.
            for (ma_uint32 i = 0; i < frames; i += 3) {
                st->vad_16k_buf[st->vad_16k_count++] = src[i];
                if (st->vad_16k_count >= 512)
                    st->score_vad_frame();
            }
        }

        st->frames_captured.fetch_add(frames, std::memory_order_relaxed);
        bool speech_active = !st->vad || st->vad_speech_active.load(std::memory_order_relaxed);
        std::vector<int16_t> live_preroll_to_route;
        if (st->vad) {
            if (speech_active)
            {
                if (!st->collecting_speech && !st->pre_roll_buffer.empty()) {
                    const bool segment_rvc = st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed);
                    size_t max_pre = segment_rvc ? st->pre_roll_buffer.size()
                        : static_cast<size_t>(std::max(1, st->sample_rate)) * 480 / 1000;
                    size_t keep = std::min(max_pre, st->pre_roll_buffer.size());
                    live_preroll_to_route.assign(st->pre_roll_buffer.end() - static_cast<std::ptrdiff_t>(keep),
                                                 st->pre_roll_buffer.end());
                }
                st->append_speech(src, raw_src, static_cast<size_t>(frames));
            }
            else {
                st->append_pre_roll(src, raw_src, static_cast<size_t>(frames));
                st->finish_speech_segment(st->vad_last_prob.load(std::memory_order_relaxed));
            }
        }

        // If VAD says no speech, do not send non-speech noise to DSP/output.
        // The RVC playback callback fills an empty post ring with silence itself.
        // Keep that ring reserved for released conversion output: queued clock
        // silence otherwise looks like ready body audio and can consume its space.
        if (st->vad && !speech_active) {
            if (!(st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed))) {
                std::vector<int16_t> silence(static_cast<size_t>(frames), 0);
                size_t written = st->ring.write(silence.data(), static_cast<size_t>(frames));
                if (written < frames)
                    st->overflow_count.fetch_add(1, std::memory_order_relaxed);
            }
            return;
        }

        // If VAD started late, prepend a short pre-roll to the live route so
        // word heads are not lost. RT-RVC already adds latency, so this is
        // preferable to dropping the beginning of short utterances.
        if (!live_preroll_to_route.empty()) {
            std::lock_guard<std::mutex> dsp_lock(st->dsp_mutex);
            const int16_t* pre_processed = st->dsp.process(live_preroll_to_route.data(), live_preroll_to_route.size(), st->sample_rate);
            if (st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed)) {
                const size_t replace_count = std::min(live_preroll_to_route.size(), st->speech_output_buffer.size());
                std::copy(pre_processed, pre_processed + replace_count, st->speech_output_buffer.begin());
            } else {
                size_t written = st->ring.write(pre_processed, live_preroll_to_route.size());
                if (written < live_preroll_to_route.size())
                    st->overflow_count.fetch_add(1, std::memory_order_relaxed);
            }
        }

        // Run DSP chain (returns processed buffer, or src if DSP disabled).
        {
            std::lock_guard<std::mutex> dsp_lock(st->dsp_mutex);
            const int16_t* processed = st->dsp.process(src, static_cast<size_t>(frames), st->sample_rate);
            if (st->vad && speech_active)
                st->append_speech_output(processed, static_cast<size_t>(frames));

            // Route: if RT-RVC active, feed to worker; otherwise direct to ring.
            if (st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed)) {
                // Segment mode: VAD completion queues the whole utterance for
                // final ASR gating and one-shot RVC inference.
            } else {
                size_t written = st->ring.write(processed, static_cast<size_t>(frames));
                if (written < frames)
                    st->overflow_count.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    static void on_playback(ma_device* dev, void* out, const void* /*in*/, ma_uint32 frames) {
        auto* st = static_cast<VcAudioState*>(dev->pUserData);
        auto* dst = static_cast<int16_t*>(out);
        if (!st || !dst) {
            if (dst) std::memset(dst, 0, frames * sizeof(int16_t));
            return;
        }
        std::lock_guard<std::mutex> route_lock(st->route_mutex);
        // Read from RT-RVC post ring when active, otherwise from main ring.
        size_t got = 0;
        bool filler_pcm = false;
        if (st->output_blocked()) {
            st->cancel_filler();
            std::memset(dst, 0, frames * sizeof(int16_t));
        } else {
            got = (st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed))
                ? st->rt_rvc->post_read(dst, static_cast<size_t>(frames))
                : st->ring.read(dst, static_cast<size_t>(frames));
            if (got > 0) {
                st->cancel_filler(); // even zero-valued body PCM has priority
            } else if (st->vad && st->rt_rvc &&
                       st->rt_rvc->running.load(std::memory_order_relaxed) && st->filler) {
                got = st->filler->read(dst, static_cast<size_t>(frames));
                filler_pcm = got > 0;
            } else {
                st->cancel_filler();
            }
        }
        if (st->monitor_ok && st->monitor_gain > 0.0f && got > 0) {
            if (filler_pcm) st->filler_monitor_ring.write(dst, got);
            else st->monitor_ring.write(dst, got);
        }
        if (!st->raw_rvc_mode)
            st->apply_output_gain(dst, static_cast<size_t>(frames));
        double sum_sq = 0.0;
        float peak = 0.0f;
        for (ma_uint32 i = 0; i < frames; ++i) {
            float s = std::fabs(dst[i] * kSampleScale);
            sum_sq += static_cast<double>(s) * static_cast<double>(s);
            if (s > peak) peak = s;
        }
        st->output_rms_level.store(frames > 0 ? static_cast<float>(std::sqrt(sum_sq / static_cast<double>(frames))) : 0.0f,
                                   std::memory_order_relaxed);
        st->output_peak_level.store(peak, std::memory_order_relaxed);
        st->frames_played.fetch_add(frames, std::memory_order_relaxed);
        const bool idle_rvc_silence = got == 0 && st->vad &&
            !st->vad_speech_active.load(std::memory_order_relaxed) &&
            st->rt_rvc && st->rt_rvc->running.load(std::memory_order_relaxed);
        if (got < frames && !idle_rvc_silence)
            st->underrun_count.fetch_add(1, std::memory_order_relaxed);
    }

    static void on_monitor_playback(ma_device* dev, void* out, const void* /*in*/, ma_uint32 frames) {
        auto* st = static_cast<VcAudioState*>(dev->pUserData);
        auto* dst = static_cast<int16_t*>(out);
        if (!st || !dst) {
            if (dst) std::memset(dst, 0, frames * sizeof(int16_t));
            return;
        }
        std::lock_guard<std::mutex> route_lock(st->route_mutex);
        if (st->output_blocked()) {
            std::memset(dst, 0, frames * sizeof(int16_t));
            return;
        }
        const bool body_ready = st->rt_rvc && st->rt_rvc->post_available() > 0;
        if (body_ready) st->cancel_filler();
        if (st->monitor_ring.available() > 0) {
            st->filler_monitor_ring.clear();
            st->monitor_ring.read(dst, static_cast<size_t>(frames));
        } else {
            st->filler_monitor_ring.read(dst, static_cast<size_t>(frames));
        }
        const float gain = st->monitor_gain;
        if (std::fabs(gain - 1.0f) > 0.001f) {
            for (ma_uint32 i = 0; i < frames; ++i) {
                dst[i] = static_cast<int16_t>(clampf(static_cast<float>(dst[i]) * gain, -32768.0f, 32767.0f));
            }
        }
    }
};

// Cleanup helper – safe to call with nullptr or partially initialised state.
static void cleanup_vc_audio(VcAudioState* a) {
    if (!a) return;
    {
        std::lock_guard<std::mutex> lock(a->route_mutex);
        a->output_stopped = true;
        a->conversion_ready = false;
        a->cancel_filler();
        if (a->filler) a->filler->invalidate();
    }
#ifdef MOBMATE_VC_BOUNDARY_TEST
    vc_boundary_test_event("route_stopped", "", "");
#endif
    if (a->monitor_ok) {
        ma_device_stop(&a->monitor_dev);
        ma_device_uninit(&a->monitor_dev);
        a->monitor_ok = false;
    }
    if (a->playback_ok) {
        ma_device_stop(&a->playback_dev);
        ma_device_uninit(&a->playback_dev);
        a->playback_ok = false;
    }
    if (a->capture_ok) {
        ma_device_stop(&a->capture_dev);
        ma_device_uninit(&a->capture_dev);
        a->capture_ok = false;
    }
    if (a->context_ok) {
        ma_context_uninit(&a->context);
        a->context_ok = false;
    }
    // The ASR worker can still hold RT and ONNX pointers while converting.
    a->stop_laugh_asr_worker();
    if (a->rt_rvc) {
        a->rt_rvc->shutdown();
        delete a->rt_rvc;
        a->rt_rvc = nullptr;
    }
    if (a->vad) {
        silero_vad_free(a->vad);
        a->vad = nullptr;
    }
    delete a;
}

// Try to find a device by id/name among enumerated devices.
static std::string trim_device_hint(std::string s) {
    auto not_space = [](unsigned char c) { return !std::isspace(c); };
    s.erase(s.begin(), std::find_if(s.begin(), s.end(), not_space));
    s.erase(std::find_if(s.rbegin(), s.rend(), not_space).base(), s.end());
    size_t bracket = s.rfind('[');
    if (bracket != std::string::npos) {
        size_t close = s.find(']', bracket);
        if (close != std::string::npos && close + 1 >= s.size()) {
            s.erase(bracket);
            s.erase(std::find_if(s.rbegin(), s.rend(), not_space).base(), s.end());
        }
    }
    return s;
}

static std::string lower_ascii(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return s;
}

static bool device_name_matches(const std::string& requested, const std::string& actual) {
    std::string req = lower_ascii(trim_device_hint(requested));
    std::string act = lower_ascii(trim_device_hint(actual));
    if (req.empty() || act.empty()) return false;
    return req == act || act.find(req) != std::string::npos || req.find(act) != std::string::npos;
}

static std::string device_id_string(const ma_device_id& id) {
#ifdef _WIN32
    return vc_wide_to_utf8(id.wasapi);
#else
    (void)id;
    return "";
#endif
}

static void log_device_candidates(const char* kind, const std::string& requested, ma_device_info* infos, ma_uint32 count) {
    std::fprintf(stderr, "[VC] %s device requested='%s' count=%u\n", kind, requested.c_str(), static_cast<unsigned>(count));
    for (ma_uint32 i = 0; i < count; ++i) {
        std::string id = device_id_string(infos[i].id);
        std::fprintf(stderr, "[VC] %s device[%u] name='%s' id='%s' default=%d\n",
                     kind, static_cast<unsigned>(i), infos[i].name, id.c_str(), infos[i].isDefault ? 1 : 0);
    }
    std::fflush(stderr);
}

static bool find_device_id(ma_device_info* infos, ma_uint32 count,
                           const std::string& requested, ma_device_id& out_id,
                           std::string* matched_name = nullptr) {
    if (requested.empty() || !infos || count == 0) return false;
    for (ma_uint32 i = 0; i < count; ++i) {
        std::string id = device_id_string(infos[i].id);
        if (device_name_matches(requested, infos[i].name) || device_name_matches(requested, id)) {
            out_id = infos[i].id;
            if (matched_name) *matched_name = infos[i].name;
            return true;
        }
    }
    return false;
}

// ── VoiceConversionSession ──────────────────────────────────────────

VoiceConversionSession::VoiceConversionSession(const char* options_json_utf8)
    : options_(parse_options(options_json_utf8)), filler_(std::make_unique<VcOnsetFiller>()) {
}

VoiceConversionSession::~VoiceConversionSession() {
    stop();
    unload_models();
}

int VoiceConversionSession::filler_exchange(const mmv2_vc_filler_update* update,
                                           mmv2_vc_filler_snapshot* snapshot) {
    if ((!update && !snapshot) ||
        (snapshot && (snapshot->size != sizeof(*snapshot) || snapshot->version != 1))) return -1;
    std::lock_guard<std::mutex> lock(mutex_);
    VcOnsetFiller::Bank prepared;
    if (update) {
        const auto sr = static_cast<uint32_t>(options_.sample_rate > 0 ? options_.sample_rate : 48000);
        if (!VcOnsetFiller::valid(*update, sr)) return -1;
        if (update->generation <= filler_->generation) return -2;
        prepared = VcOnsetFiller::prepare(*update);
    }
    {
        std::unique_lock<std::mutex> route_lock;
        if (audio_) route_lock = std::unique_lock<std::mutex>(audio_->route_mutex);
        if (update) {
            filler_->install(*update, prepared);
            if (audio_) audio_->filler_monitor_ring.clear();
        }
        if (snapshot) filler_->snapshot(*snapshot);
    } // Retired PCM is freed below, outside the callback's route lock.
    return 0;
}

int VoiceConversionSession::load_models() {
    std::lock_guard<std::mutex> lock(mutex_);
    if ((onnx_ && onnx_->loaded) || (seedvc_ && seedvc_->loaded)) return 0; // already loaded
    if (options_.model_path.empty()) {
        last_load_error_ = "voice conversion model path is empty";
        return -1;
    }

    last_load_error_.clear();
    std::string engine = normalize_engine_name(options_.engine.empty() ? "rvc_onnx" : options_.engine);
    if (engine == "seedvc_ncnn") {
        // The live route has no Seed-VC worker; do not report a running
        // conversion while silently routing unconverted audio.
        if (audio_) {
            last_load_error_ = "Seed-VC live conversion is unavailable";
            return -21;
        }
        auto* p = new SeedVcNcnnPipeline();
        int ret = p->load(options_);
        if (ret != 0) {
            last_load_error_ = p->error.empty() ? "Seed-VC ncnn model load failed" : p->error;
            delete p;
            return ret;
        }
        delete seedvc_;
        seedvc_ = p;
        if (audio_) audio_->seedvc = seedvc_;
        return 0;
    }
    if (engine != "rvc_onnx" && engine != "onnx" && engine != "") {
        last_load_error_ = "unsupported voice conversion engine: " + options_.engine;
        return -21;
    }

    auto* p = new VcOnnxPipeline();
#if defined(MOBMATE_VC_BOUNDARY_TEST) && !defined(_WIN32)
    int ret = vc_boundary_test_load_onnx(p);
#else
    int ret = p->load(options_);
#endif
    if (ret != 0) {
        last_load_error_ = p->error.empty() ? "RVC ONNX model load failed" : p->error;
        delete p;
        return ret;
    }
    delete onnx_;
    onnx_ = p;

    // Link ONNX pipeline to audio state if active
    if (audio_) {
        // Start RT-RVC streaming if session is already running but rt_rvc not yet initialized
        if (!audio_->rt_rvc && onnx_->loaded) {
            int sr = audio_->sample_rate;
            auto* rt = new VcRtRvc();
            rt->init(sr, 1200 /*chunk_ms: extended phoneme context*/, 20 /*overlap_ms*/, onnx_, options_.model_path,
                     audio_->raw_rvc_mode, audio_->playback_gain,
                     &audio_->laugh_detect_count, &audio_->asr_gate_accept_count, &audio_->asr_gate_reject_count);
            {
                std::lock_guard<std::mutex> route_lock(audio_->route_mutex);
                audio_->onnx = onnx_;
                audio_->rt_rvc = rt;
                audio_->start_laugh_asr_worker();
                audio_->conversion_ready = true;
            }
            std::fprintf(stderr, "[VC] RT-RVC started (post-load): chunk=%zu overlap=%zu sr=%d model_sr=%d\n",
                         rt->chunk_samples, rt->overlap_samples, sr, rt->model_output_sr);
            std::fflush(stderr);
        }
    }
    return 0;
}

void VoiceConversionSession::unload_models() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (audio_ && (audio_->conversion_required || audio_->rt_rvc)) {
        {
            std::lock_guard<std::mutex> route_lock(audio_->route_mutex);
            audio_->conversion_ready = false;
            audio_->monitor_ring.clear();
            audio_->cancel_filler();
        }
#ifdef MOBMATE_VC_BOUNDARY_TEST
        vc_boundary_test_event("route_unloaded", "", "");
#endif
        // Closing the route first prevents playback while in-flight ASR/RVC
        // finishes. Keep both the RT object and model alive until it joins.
        audio_->stop_laugh_asr_worker();
        if (audio_->rt_rvc) audio_->rt_rvc->shutdown();
        audio_->reset_conversion_history();
        {
            std::lock_guard<std::mutex> route_lock(audio_->route_mutex);
            delete audio_->rt_rvc;
            audio_->rt_rvc = nullptr;
            audio_->onnx = nullptr;
            audio_->seedvc = nullptr;
        }
    }
    delete onnx_;
    onnx_ = nullptr;
    delete seedvc_;
    seedvc_ = nullptr;
}

bool VoiceConversionSession::models_loaded() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return (onnx_ && onnx_->loaded) || (seedvc_ && seedvc_->loaded);
}

int VoiceConversionSession::start() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (running_) return 0;
    if (seedvc_ && seedvc_->loaded) {
        last_load_error_ = "Seed-VC live conversion is unavailable";
        return -21;
    }

    int sr = options_.sample_rate > 0 ? options_.sample_rate : 48000;

    auto* a = new VcAudioState();
    a->filler = filler_.get();
    a->conversion_required = vc_conversion_requested(options_);
    a->sample_rate = sr;
    float live_output_volume = options_.output_volume_percent <= 0.0f ? 140.0f : options_.output_volume_percent;
    a->playback_gain = clampf(live_output_volume, 0.0f, 300.0f) / 100.0f;
    a->output_ceiling = clampf(options_.output_limiter_ceiling, 0.1f, 1.0f);
    a->monitor_volume_percent = clampf(options_.monitor_volume_percent, 0.0f, 100.0f);
    // Mobfy monitor taps the already-processed live VC signal, which is much
    // louder than STT->TTS monitor at the same UI percent. Keep the public/UI
    // percent unchanged, but attenuate the internal monitor feed to match TTS.
    a->monitor_gain = (a->monitor_volume_percent / 100.0f) * 0.15f;
    a->ring.init(static_cast<size_t>(sr)); // 1 sec capacity
    a->monitor_ring.init(static_cast<size_t>(sr)); // 1 sec capacity
    a->filler_monitor_ring.init(static_cast<size_t>(sr)); // separate, body-first monitor tap
    a->dsp.init(options_, sr);
    a->speech_buffer.reserve(static_cast<size_t>(sr) * 8);
    a->pre_roll_buffer.reserve(static_cast<size_t>(sr));
    const char* dump_env = std::getenv("MOBMATE_VC_DUMP");
    a->debug_dump_enabled = dump_env && std::strcmp(dump_env, "1") == 0;
    if (a->debug_dump_enabled) {
        std::error_code ec;
        auto dir = std::filesystem::current_path() / "temp" / "voice_conversion";
        std::filesystem::create_directories(dir, ec);
        a->dump_dir = dir.string();
    }
    a->wav_output_enabled = options_.wav_output_enabled && !options_.wav_output_directory.empty();
    a->wav_output_directory = options_.wav_output_directory;

    // ── SileroVAD initialization ──
    a->vad_start_threshold    = options_.vad_start_threshold;
    a->vad_continue_threshold = options_.vad_continue_threshold;
    a->raw_rvc_mode = normalize_engine_name(
        vc_json_get_string(options_.raw_json.c_str(), "mode", "")) == "rvc_raw";
    a->laugh_asr_assist_enabled = options_.laugh_asr_assist_enabled;
    a->laugh_asr_moonshine_dll_dir = options_.moonshine_dll_dir;
    a->laugh_asr_moonshine_model_root = options_.moonshine_model_root;
    a->laugh_asr_language = options_.laugh_asr_language.empty() ? "ja" : options_.laugh_asr_language;
    a->laugh_asr_tokens = options_.laugh_asr_tokens;
    a->start_laugh_asr_worker();
    if (!options_.silero_vad_model_path.empty() && !options_.onnx_runtime_dll_dir.empty()) {
        char vad_err[256] = {};
        a->vad = silero_vad_init(options_.silero_vad_model_path.c_str(),
                                  options_.onnx_runtime_dll_dir.c_str(),
                                  vad_err, sizeof(vad_err));
        // If init fails, continue without VAD (non-fatal)
    }

    // ── context ──
    if (ma_context_init(nullptr, 0, nullptr, &a->context) != MA_SUCCESS) {
        cleanup_vc_audio(a);
        return -10;
    }
    a->context_ok = true;

    // ── enumerate devices ──
    ma_device_info* cap_infos  = nullptr;  ma_uint32 cap_count  = 0;
    ma_device_info* play_infos = nullptr;  ma_uint32 play_count = 0;
    ma_context_get_devices(&a->context, &play_infos, &play_count,
                           &cap_infos, &cap_count);

    ma_device_id found_cap{}, found_play{};
    log_device_candidates("capture", options_.input_device_id, cap_infos, cap_count);
    log_device_candidates("playback", options_.output_device_id, play_infos, play_count);
    bool has_cap  = find_device_id(cap_infos,  cap_count,  options_.input_device_id,  found_cap, &a->capture_device_name);
    bool has_play = find_device_id(play_infos, play_count, options_.output_device_id, found_play, &a->playback_device_name);
    std::fprintf(stderr, "[VC] device match capture=%d captureName='%s' playback=%d playbackName='%s'\n",
                 has_cap ? 1 : 0, a->capture_device_name.c_str(), has_play ? 1 : 0, a->playback_device_name.c_str());
    std::fflush(stderr);

    // ── capture device ──
    {
        ma_device_config cfg = ma_device_config_init(ma_device_type_capture);
        cfg.capture.format   = ma_format_s16;
        cfg.capture.channels = 1;
        cfg.sampleRate       = static_cast<ma_uint32>(sr);
        cfg.dataCallback     = VcAudioState::on_capture;
        cfg.pUserData        = a;
        if (has_cap) cfg.capture.pDeviceID = &found_cap;
        cfg.periodSizeInFrames = static_cast<ma_uint32>(sr) / 100; // 10 ms
        cfg.periods            = 2;

        if (ma_device_init(&a->context, &cfg, &a->capture_dev) != MA_SUCCESS) {
            cleanup_vc_audio(a);
            return -11;
        }
        a->capture_ok = true;
        ma_device_info actual{};
        if (ma_device_get_info(&a->capture_dev, ma_device_type_capture, &actual) == MA_SUCCESS && actual.name[0] != '\0')
            a->capture_device_name = actual.name;
    }

    // ── playback device ──
    {
        ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
        cfg.playback.format   = ma_format_s16;
        cfg.playback.channels = 1;
        cfg.sampleRate        = static_cast<ma_uint32>(sr);
        cfg.dataCallback      = VcAudioState::on_playback;
        cfg.pUserData         = a;
        if (has_play) cfg.playback.pDeviceID = &found_play;
        cfg.periodSizeInFrames = static_cast<ma_uint32>(sr) / 100; // 10 ms
        cfg.periods            = 2;

        if (ma_device_init(&a->context, &cfg, &a->playback_dev) != MA_SUCCESS) {
            cleanup_vc_audio(a);
            return -12;
        }
        a->playback_ok = true;
        ma_device_info actual{};
        if (ma_device_get_info(&a->playback_dev, ma_device_type_playback, &actual) == MA_SUCCESS && actual.name[0] != '\0')
            a->playback_device_name = actual.name;
    }

    // Publish any already-loaded converter before the first device callback.
    if (onnx_ && onnx_->loaded) {
        a->onnx = onnx_;
        auto* rt = new VcRtRvc();
        rt->init(sr, 1200, 20, onnx_, options_.model_path,
                 a->raw_rvc_mode, a->playback_gain,
                 &a->laugh_detect_count, &a->asr_gate_accept_count, &a->asr_gate_reject_count);
        a->rt_rvc = rt;
        a->conversion_ready = true;
    }

    // ── start devices ──
    if (ma_device_start(&a->capture_dev) != MA_SUCCESS) {
        cleanup_vc_audio(a);
        return -13;
    }
    if (ma_device_start(&a->playback_dev) != MA_SUCCESS) {
        cleanup_vc_audio(a);
        return -14;
    }

    // ── optional monitor playback to the default speaker/headphone device ──
    // Main playback usually targets a virtual microphone. Monitor is only opened
    // when a non-default output target is selected, and is disabled if miniaudio
    // resolves it to the same device name as the main playback endpoint.
    if (a->monitor_gain > 0.0f && has_play && !options_.output_device_id.empty()) {
        ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
        cfg.playback.format   = ma_format_s16;
        cfg.playback.channels = 1;
        cfg.sampleRate        = static_cast<ma_uint32>(sr);
        cfg.dataCallback      = VcAudioState::on_monitor_playback;
        cfg.pUserData         = a;
        cfg.periodSizeInFrames = static_cast<ma_uint32>(sr) / 100;
        cfg.periods            = 2;
        if (ma_device_init(&a->context, &cfg, &a->monitor_dev) == MA_SUCCESS) {
            ma_device_info actual{};
            if (ma_device_get_info(&a->monitor_dev, ma_device_type_playback, &actual) == MA_SUCCESS && actual.name[0] != '\0')
                a->monitor_device_name = actual.name;
            if (!a->monitor_device_name.empty() && a->monitor_device_name == a->playback_device_name) {
                ma_device_uninit(&a->monitor_dev);
                a->monitor_device_name.clear();
                a->monitor_gain = 0.0f;
            } else if (ma_device_start(&a->monitor_dev) == MA_SUCCESS) {
                a->monitor_ok = true;
            } else {
                ma_device_uninit(&a->monitor_dev);
                a->monitor_device_name.clear();
                a->monitor_gain = 0.0f;
            }
        } else {
            a->monitor_gain = 0.0f;
        }
    }

    audio_   = a;
    running_ = true;
    return 0;
}

int VoiceConversionSession::stop() {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!running_ && !audio_) {
        filler_->invalidate();
        return 0;
    }
    running_ = false;
    cleanup_vc_audio(audio_);
    audio_ = nullptr;
    return 0;
}

std::string VoiceConversionSession::status_json() const {
    std::lock_guard<std::mutex> lock(mutex_);

    std::string audio_fields;
    if (audio_) {
        size_t ring_fill = audio_->ring.available();
        double lat_ms = (audio_->sample_rate > 0)
            ? (static_cast<double>(ring_fill) / audio_->sample_rate * 1000.0)
            : 0.0;
        char lat_buf[32];
        std::snprintf(lat_buf, sizeof(lat_buf), "%.1f", lat_ms);

        // Pressure stats
        char pressure_buf[32], rms_buf[32], peak_buf[32], input_rms_buf[32], input_peak_buf[32], output_rms_buf[32], output_peak_buf[32], gr_buf[32], output_auto_gain_buf[32];
        char nf_buf[32], ns_buf[32], np_buf[32], sp_buf[32];
        float ps = audio_->dsp.pressure.pressure_score.load(std::memory_order_relaxed);
        float rm = audio_->dsp.pressure.rms_level.load(std::memory_order_relaxed);
        float pk = audio_->dsp.pressure.peak_level.load(std::memory_order_relaxed);
        float in_rm = audio_->input_rms_level.load(std::memory_order_relaxed);
        float in_pk = audio_->input_peak_level.load(std::memory_order_relaxed);
        float out_rm = audio_->output_rms_level.load(std::memory_order_relaxed);
        float out_pk = audio_->output_peak_level.load(std::memory_order_relaxed);
        std::snprintf(pressure_buf, sizeof(pressure_buf), "%.4f", ps);
        std::snprintf(rms_buf,      sizeof(rms_buf),      "%.4f", rm);
        std::snprintf(peak_buf,     sizeof(peak_buf),     "%.4f", pk);
        std::snprintf(input_rms_buf, sizeof(input_rms_buf), "%.4f", in_rm);
        std::snprintf(input_peak_buf, sizeof(input_peak_buf), "%.4f", in_pk);
        std::snprintf(output_rms_buf, sizeof(output_rms_buf), "%.4f", out_rm);
        std::snprintf(output_peak_buf, sizeof(output_peak_buf), "%.4f", out_pk);
        std::snprintf(output_auto_gain_buf, sizeof(output_auto_gain_buf), "%.2f", audio_->output_auto_gain_current.load(std::memory_order_relaxed));
        std::snprintf(gr_buf, sizeof(gr_buf), "%.2f", audio_->dsp.loudness.gain_reduction_db.load(std::memory_order_relaxed));
        std::snprintf(nf_buf, sizeof(nf_buf), "%.6f", audio_->noise_profile.noise_floor_rms);
        std::snprintf(ns_buf, sizeof(ns_buf), "%.6f", audio_->noise_profile.noise_rms_stddev);
        std::snprintf(np_buf, sizeof(np_buf), "%.6f", audio_->noise_profile.peak_p95);
        std::snprintf(sp_buf, sizeof(sp_buf), "%.3f", audio_->noise_profile.silero_p90);

        audio_fields =
            ",\"audioPassthroughActive\":" + std::string(audio_->output_blocked() ? "false" : "true") +
            ",\"sampleRate\":" + std::to_string(audio_->sample_rate) +
            ",\"captureDeviceName\":\"" + vc_json_escape(audio_->capture_device_name) + "\"" +
            ",\"playbackDeviceName\":\"" + vc_json_escape(audio_->playback_device_name) + "\"" +
            ",\"monitorPlaybackActive\":" + std::string(audio_->monitor_ok ? "true" : "false") +
            ",\"monitorPlaybackDeviceName\":\"" + vc_json_escape(audio_->monitor_device_name) + "\"" +
            ",\"inputRmsLevel\":" + std::string(input_rms_buf) +
            ",\"inputPeakLevel\":" + std::string(input_peak_buf) +
            ",\"outputRmsLevel\":" + std::string(output_rms_buf) +
            ",\"outputPeakLevel\":" + std::string(output_peak_buf) +
            ",\"zeroInputCallbacks\":" + std::to_string(audio_->zero_input_callbacks.load(std::memory_order_relaxed)) +
            ",\"debugDumpDir\":\"" + vc_json_escape(audio_->dump_dir) + "\"" +
            ",\"outputVolumePercent\":" + std::to_string(static_cast<int>(std::round(audio_->playback_gain * 100.0f))) +
            ",\"outputAutoGain\":" + std::string(output_auto_gain_buf) +
            ",\"monitorVolumePercent\":" + std::to_string(static_cast<int>(std::round(audio_->monitor_volume_percent))) +
            ",\"ringBufferFill\":" + std::to_string(ring_fill) +
            ",\"estimatedLatencyMs\":" + std::string(lat_buf) +
            ",\"underrunCount\":" + std::to_string(audio_->underrun_count.load(std::memory_order_relaxed)) +
            ",\"overflowCount\":" + std::to_string(audio_->overflow_count.load(std::memory_order_relaxed)) +
            ",\"framesCaptured\":" + std::to_string(audio_->frames_captured.load(std::memory_order_relaxed)) +
            ",\"framesPlayed\":" + std::to_string(audio_->frames_played.load(std::memory_order_relaxed)) +
            ",\"dsp\":{\"enabled\":" + std::string(audio_->dsp.enabled ? "true" : "false") +
            ",\"pitchShifterActive\":" + std::string(audio_->dsp.pitch_shifter.enabled ? "true" : "false") +
            ",\"pressureScore\":" + std::string(pressure_buf) +
            ",\"rmsLevel\":" + std::string(rms_buf) +
            ",\"peakLevel\":" + std::string(peak_buf) +
            ",\"gainReductionDb\":" + std::string(gr_buf) +
            ",\"external\":\"" + vc_json_escape(audio_->dsp.external.status) + "\"" +
            "}"
            ",\"vad\":{\"enabled\":" + std::string(audio_->vad ? "true" : "false") +
            ",\"speechActive\":" + std::string(audio_->vad_speech_active.load(std::memory_order_relaxed) ? "true" : "false") +
            ",\"noiseProfileReady\":" + std::string(audio_->noise_profile.ready ? "true" : "false") +
            ",\"noiseSampleCount\":" + std::to_string(audio_->noise_profile.count) +
            ",\"noiseFloorRms\":" + std::string(nf_buf) +
            ",\"noiseRmsStddev\":" + std::string(ns_buf) +
            ",\"peakP95\":" + std::string(np_buf) +
            ",\"sileroP90\":" + std::string(sp_buf) +
            ",\"lowGainMic\":" + std::string(audio_->noise_profile.low_gain_mic ? "true" : "false") +
            ",\"holdFramesRemaining\":" + std::to_string(audio_->vad_hold_frames_remaining);
        const int64_t last_laugh_ms = audio_->last_laugh_detect_ms.load(std::memory_order_relaxed);
        const int64_t laugh_age_ms = last_laugh_ms > 0 ? std::max<int64_t>(0, vc_steady_now_ms() - last_laugh_ms) : -1;
        audio_fields +=
            ",\"laughDetectedRecent\":" + std::string((laugh_age_ms >= 0 && laugh_age_ms <= 1600) ? "true" : "false") +
            ",\"laughDetectCount\":" + std::to_string(audio_->laugh_detect_count.load(std::memory_order_relaxed)) +
            ",\"laughDetectAgeMs\":" + std::to_string(laugh_age_ms);
        std::string laugh_asr_text;
        std::string laugh_asr_decision;
        float laugh_asr_confidence = -1.0f;
        {
            std::lock_guard<std::mutex> asr_lock(audio_->laugh_asr_mutex);
            laugh_asr_text = audio_->laugh_asr_last_text;
            laugh_asr_decision = audio_->laugh_asr_last_decision;
            laugh_asr_confidence = audio_->laugh_asr_last_confidence;
        }
        const int64_t last_laugh_asr_ms = audio_->last_laugh_asr_ms.load(std::memory_order_relaxed);
        const int64_t laugh_asr_age_ms = last_laugh_asr_ms > 0 ? std::max<int64_t>(0, vc_steady_now_ms() - last_laugh_asr_ms) : -1;
        audio_fields +=
            ",\"laughAsrAssistEnabled\":" + std::string(audio_->laugh_asr_assist_enabled ? "true" : "false") +
            ",\"laughAsrPending\":" + std::string(audio_->laugh_asr_pending.load(std::memory_order_relaxed) ? "true" : "false") +
            ",\"laughAsrCheckedCount\":" + std::to_string(audio_->laugh_asr_checked_count.load(std::memory_order_relaxed)) +
            ",\"laughAsrRejectedSpeechCount\":" + std::to_string(audio_->laugh_asr_rejected_speech_count.load(std::memory_order_relaxed)) +
            ",\"laughAsrErrorCount\":" + std::to_string(audio_->laugh_asr_error_count.load(std::memory_order_relaxed)) +
            ",\"asrGateAcceptCount\":" + std::to_string(audio_->asr_gate_accept_count.load(std::memory_order_relaxed)) +
            ",\"asrGateRejectCount\":" + std::to_string(audio_->asr_gate_reject_count.load(std::memory_order_relaxed)) +
            ",\"laughAsrAgeMs\":" + std::to_string(laugh_asr_age_ms) +
            ",\"laughAsrDecision\":\"" + vc_json_escape(laugh_asr_decision) + "\"" +
            ",\"laughAsrText\":\"" + vc_json_escape(laugh_asr_text) + "\"" +
            ",\"laughAsrConfidence\":" + std::string([&]{
                char cb[32]; std::snprintf(cb, sizeof(cb), "%.3f", laugh_asr_confidence);
                return std::string(cb);
            }());
        char vad_prob_buf[32];
        std::snprintf(vad_prob_buf, sizeof(vad_prob_buf), "%.3f", audio_->vad_last_prob.load(std::memory_order_relaxed));
        audio_fields += ",\"lastProb\":" + std::string(vad_prob_buf) + "}";
        // AGC status
        {
            char agc_gain_buf[32], agc_rms_buf[32];
            float agc_g = audio_->agc_current_gain.load(std::memory_order_relaxed);
            std::snprintf(agc_gain_buf, sizeof(agc_gain_buf), "%.2f", agc_g);
            std::snprintf(agc_rms_buf, sizeof(agc_rms_buf), "%.6f", audio_->agc_smoothed_rms);
            audio_fields +=
                ",\"agc\":{\"enabled\":" + std::string(audio_->agc_enabled ? "true" : "false") +
                ",\"gain\":" + std::string(agc_gain_buf) +
                ",\"gainDb\":" + std::string([&]{
                    char db[32]; std::snprintf(db, sizeof(db), "%.1f", 20.0f * std::log10(std::max(0.001f, agc_g)));
                    return std::string(db);
                }()) +
                ",\"smoothedInputRms\":" + std::string(agc_rms_buf) + "}";
        }
    } else {
        audio_fields = ",\"audioPassthroughActive\":false";
    }

    bool onnx_loaded = onnx_ && onnx_->loaded;
    bool seedvc_loaded = seedvc_ && seedvc_->loaded;
    std::string onnx_fields;
    if (onnx_) {
        onnx_fields =
            ",\"onnx\":{"
            "\"loaded\":" + std::string(onnx_->loaded ? "true" : "false") +
            ",\"inferenceCount\":" + std::to_string(onnx_->inference_count.load(std::memory_order_relaxed));
        char rtf_buf[32];
        std::snprintf(rtf_buf, sizeof(rtf_buf), "%.3f", onnx_->last_rtf.load(std::memory_order_relaxed));
        onnx_fields += ",\"lastRtf\":" + std::string(rtf_buf);
        if (!onnx_->error.empty())
            onnx_fields += ",\"error\":\"" + vc_json_escape(onnx_->error) + "\"";
        onnx_fields += "}";
    }

    std::string seedvc_fields;
    if (seedvc_) {
        seedvc_fields =
            ",\"seedvc\":{" 
            "\"loaded\":" + std::string(seedvc_->loaded ? "true" : "false") +
            ",\"backend\":\"" + vc_json_escape(seedvc_->backend) + "\"" +
            ",\"vulkanEnabled\":" + std::string(seedvc_->vulkan_enabled ? "true" : "false") +
            ",\"modelCount\":" + std::to_string(seedvc_->nets.size()) +
            ",\"inferenceCount\":" + std::to_string(seedvc_->inference_count.load(std::memory_order_relaxed));
        char rtf_buf[32];
        std::snprintf(rtf_buf, sizeof(rtf_buf), "%.3f", seedvc_->last_rtf.load(std::memory_order_relaxed));
        seedvc_fields += ",\"lastRtf\":" + std::string(rtf_buf);
        if (!seedvc_->error.empty())
            seedvc_fields += ",\"error\":\"" + vc_json_escape(seedvc_->error) + "\"";
        seedvc_fields += "}";
    }

    std::string last_error_field;
    if (!last_load_error_.empty())
        last_error_field = ",\"lastError\":\"" + vc_json_escape(last_load_error_) + "\"";

    std::string phase_msg = audio_ && audio_->output_blocked()
        ? "Voice Conversion: model not ready; output blocked."
        : seedvc_loaded
        ? "Voice Conversion Phase 5: Seed-VC ncnn full pipeline wired."
        : onnx_loaded
        ? "Voice Conversion Phase 4: RVC ONNX runtime active."
        : "Voice Conversion Phase 3: audio passthrough with Prosody Neutralizer DSP.";

    // RT-RVC streaming stats
    std::string rt_rvc_fields;
    if (audio_ && audio_->rt_rvc) {
        auto* rt = audio_->rt_rvc;
        char chunk_ms_buf[32];
        std::snprintf(chunk_ms_buf, sizeof(chunk_ms_buf), "%.1f", rt->last_chunk_ms.load(std::memory_order_relaxed));
        rt_rvc_fields =
            ",\"rtRvc\":{\"active\":true"
            ",\"chunksProcessed\":" + std::to_string(rt->chunks_processed.load(std::memory_order_relaxed)) +
            ",\"lastChunkMs\":" + std::string(chunk_ms_buf) +
            ",\"chunkSamples\":" + std::to_string(rt->chunk_samples) +
            ",\"overlapSamples\":" + std::to_string(rt->overlap_samples) +
            ",\"laughReplaceCount\":" + std::to_string(rt->laugh_replace_count.load(std::memory_order_relaxed)) +
            ",\"laughSkipCount\":" + std::to_string(rt->laugh_skip_count.load(std::memory_order_relaxed)) +
            ",\"lastLaughSampleIndex\":" + std::to_string(rt->last_laugh_sample_index.load(std::memory_order_relaxed)) +
            ",\"asrGateActive\":" + std::string(rt->asr_gate_active_status.load(std::memory_order_relaxed) ? "true" : "false") +
            ",\"asrGatePendingSamples\":" + std::to_string(rt->asr_gate_pending_samples.load(std::memory_order_relaxed)) +
            ",\"postRingFill\":" + std::to_string(rt->post_available()) +
            "}";
    }

    return "{\"ok\":true"
        ",\"mode\":\"voice_conversion\""
        ",\"created\":true"
        ",\"running\":" + std::string(running_ ? "true" : "false") +
        ",\"conversionRuntimeAvailable\":" + std::string((onnx_loaded || seedvc_loaded) ? "true" : "false") +
        ",\"audioPassthroughImplemented\":true"
        ",\"prosodyNeutralizerImplemented\":true"
        + audio_fields + onnx_fields + seedvc_fields + rt_rvc_fields + last_error_field +
        ",\"message\":\"" + vc_json_escape(phase_msg) + "\""
        ",\"options\":{"
            "\"modelId\":\"" + vc_json_escape(options_.model_id) + "\""
            ",\"modelPath\":\"" + vc_json_escape(options_.model_path) + "\""
            ",\"engine\":\"" + vc_json_escape(options_.engine) + "\""
            ",\"inputDeviceId\":\"" + vc_json_escape(options_.input_device_id) + "\""
            ",\"outputDeviceId\":\"" + vc_json_escape(options_.output_device_id) + "\""
            ",\"sampleRate\":" + std::to_string(options_.sample_rate) +
            ",\"frameMs\":" + std::to_string(options_.frame_ms) +
            ",\"hopMs\":" + std::to_string(options_.hop_ms) +
            ",\"prosodyNeutralizerEnabled\":" + std::string(options_.prosody_neutralizer_enabled ? "true" : "false") +
            ",\"neutralizerPreset\":\"" + vc_json_escape(options_.neutralizer_preset) + "\"" +
            ",\"neutralizerStrength\":" + std::to_string(options_.neutralizer_strength) +
            ",\"noiseCleanup\":\"" + vc_json_escape(options_.noise_cleanup) + "\"" +
            ",\"pressureFlatten\":" + std::to_string(options_.pressure_flatten) +
            ",\"pressureSuppressorStrength\":" + std::to_string(options_.pressure_suppressor_strength) +
            ",\"breathSuppress\":" + std::to_string(options_.breath_suppress) +
            ",\"f0Flatten\":" + std::to_string(options_.f0_flatten) +
            ",\"worldWet\":" + std::to_string(options_.world_wet) +
            ",\"experimentalVoiceModel\":" + std::string(options_.experimental_voice_model ? "true" : "false") +
            ",\"pressureEstimatorEnabled\":" + std::string(options_.pressure_estimator_enabled ? "true" : "false") +
            ",\"adaptiveStrength\":" + std::string(options_.adaptive_strength ? "true" : "false") +
            ",\"outputVolumePercent\":" + std::to_string(options_.output_volume_percent) +
            ",\"speakerId\":" + std::to_string(options_.speaker_id) +
        "}}";
}

int VoiceConversionSession::process_buffer(const int16_t* input, size_t num_samples,
                                           int16_t* output, char** out_pressure_json) {
    if (!input || !output || num_samples == 0) return -1;
    std::lock_guard<std::mutex> lock(mutex_);
    const bool conversion_required = vc_conversion_requested(options_);
    if (conversion_required && !((onnx_ && onnx_->loaded) || (seedvc_ && seedvc_->loaded))) {
        std::memset(output, 0, num_samples * sizeof(int16_t));
        if (out_pressure_json) *out_pressure_json = nullptr;
        return -3;
    }

    // Create a temporary DSP chain for offline processing.
    int sr = options_.sample_rate > 0 ? options_.sample_rate : 48000;
    VcDspChain dsp;
    dsp.init(options_, sr);

    // Process in chunks matching typical frame size (e.g. 480 samples = 10ms at 48kHz).
    size_t chunk = static_cast<size_t>(sr / 100); // 10 ms
    if (chunk == 0) chunk = 480;

    size_t pos = 0;
    while (pos < num_samples) {
        size_t n = std::min(chunk, num_samples - pos);
        const int16_t* processed = dsp.process(input + pos, n, sr);
        std::memcpy(output + pos, processed, n * sizeof(int16_t));
        pos += n;
    }

    // Run the same loaded-VC stage used by live debug segments and process-file-live.
    bool laugh_replaced = false;
    const bool raw_rvc_mode = normalize_engine_name(
        vc_json_get_string(options_.raw_json.c_str(), "mode", "")) == "rvc_raw";
    VcRawOutputGainState raw_gain;
    if (raw_rvc_mode)
        raw_gain.reset(clampf(options_.output_volume_percent, 0.0f, 300.0f) / 100.0f);
    bool vc_applied = vc_apply_loaded_inference(onnx_, seedvc_, output, num_samples, sr,
        raw_rvc_mode ? &raw_gain : nullptr);
    if (conversion_required && !vc_applied) {
        std::memset(output, 0, num_samples * sizeof(int16_t));
        if (out_pressure_json) *out_pressure_json = nullptr;
        return -4;
    }

    // Build pressure stats JSON.
    if (out_pressure_json) {
        float ps = dsp.pressure.pressure_score.load(std::memory_order_relaxed);
        float rm = dsp.pressure.rms_level.load(std::memory_order_relaxed);
        float pk = dsp.pressure.peak_level.load(std::memory_order_relaxed);

        // Also compute input (pre-DSP) stats.
        float in_sum_sq = 0.0f, in_peak = 0.0f;
        for (size_t i = 0; i < num_samples; ++i) {
            float s = std::fabs(input[i] * kSampleScale);
            in_sum_sq += s * s;
            if (s > in_peak) in_peak = s;
        }
        float in_rms = std::sqrt(in_sum_sq / static_cast<float>(num_samples));

        float out_sum_sq = 0.0f, out_peak = 0.0f;
        for (size_t i = 0; i < num_samples; ++i) {
            float s = std::fabs(output[i] * kSampleScale);
            out_sum_sq += s * s;
            if (s > out_peak) out_peak = s;
        }
        float out_rms = std::sqrt(out_sum_sq / static_cast<float>(num_samples));

        char buf[512];
        std::snprintf(buf, sizeof(buf),
            "{\"pressureScore\":%.4f,\"rmsLevel\":%.4f,\"peakLevel\":%.4f,"
            "\"inputRms\":%.4f,\"inputPeak\":%.4f,"
            "\"outputRms\":%.4f,\"outputPeak\":%.4f,"
            "\"rmsReduction\":%.4f,\"peakReduction\":%.4f,"
            "\"vcApplied\":%s,\"laughExpressionReplaced\":%s}",
            ps, rm, pk,
            in_rms, in_peak,
            out_rms, out_peak,
            (in_rms > 0.0001f) ? (1.0f - out_rms / in_rms) : 0.0f,
            (in_peak > 0.0001f) ? (1.0f - out_peak / in_peak) : 0.0f,
            vc_applied ? "true" : "false",
            laugh_replaced ? "true" : "false");
        *out_pressure_json = vc_dup_string(buf);
    }

    return static_cast<int>(num_samples);
}

int VoiceConversionSession::inject_and_capture(const int16_t* input, size_t num_samples,
                                                int16_t* output, char** out_pressure_json) {
    if (!input || !output || num_samples == 0) return -1;
    std::lock_guard<std::mutex> lock(mutex_);
    if (!audio_) {
        std::memset(output, 0, num_samples * sizeof(int16_t));
        if (out_pressure_json) *out_pressure_json = nullptr;
        return -2; // session not started
    }

    auto* st = audio_;
    if (st->output_blocked()) {
        std::memset(output, 0, num_samples * sizeof(int16_t));
        if (out_pressure_json) *out_pressure_json = nullptr;
        return -3;
    }
    struct CapturePauseGuard {
        VcAudioState* st = nullptr;
        bool restart_capture = false;
        explicit CapturePauseGuard(VcAudioState* s) : st(s) {
            if (st && st->capture_ok) {
                ma_device_stop(&st->capture_dev);
                restart_capture = true;
            }
        }
        ~CapturePauseGuard() {
            if (st && restart_capture) ma_device_start(&st->capture_dev);
        }
    } capture_pause(st);
    int sr = st->sample_rate > 0 ? st->sample_rate : 48000;

    // Reset SE LSTM states for clean per-file processing (bench/eval).
    // In live mode, states persist naturally across callbacks.
    st->dsp.se.reset_states();

    // Process in chunks matching the live capture callback (~10ms = sr/100).
    // Keep SileroVAD gating enabled here so process-file-live benchmarks the same
    // speech/non-speech decision used by the live capture path.
    if (st->vad) {
        silero_vad_reset(st->vad);
        st->vad_16k_count = 0;
        st->vad_last_prob.store(0.0f, std::memory_order_relaxed);
        st->vad_speech_active.store(true, std::memory_order_relaxed); // avoid chopping first frame before first score
        st->vad_hold_frames_remaining = st->vad_hold_frames;
    }
    size_t chunk = static_cast<size_t>(sr / 100);
    if (chunk == 0) chunk = 480;

    size_t out_pos = 0;
    float inject_agc_gain = 1.0f;
    float inject_agc_smoothed_rms = 0.0f;
    std::vector<int16_t> inject_agc_buffer;
    std::vector<int16_t> inject_pre_roll;
    const size_t inject_pre_roll_max = static_cast<size_t>(std::max(1, sr)) * 480 / 1000;
    for (size_t pos = 0; pos < num_samples; pos += chunk) {
        size_t n = std::min(chunk, num_samples - pos);
        const int16_t* raw_src = input + pos;
        const int16_t* src = raw_src;
        ma_uint32 frames = static_cast<ma_uint32>(n);

        // Apply same AGC as the live capture callback so process-file-live is
        // representative for low-gain microphone tuning.
        if (frames > 0) {
            double raw_sum = 0.0;
            for (ma_uint32 i = 0; i < frames; ++i) {
                float s = raw_src[i] * kSampleScale;
                raw_sum += static_cast<double>(s) * s;
            }
            float raw_rms = static_cast<float>(std::sqrt(raw_sum / static_cast<double>(frames)));
            if (inject_agc_smoothed_rms < 0.0001f && raw_rms > 0.0001f)
                inject_agc_smoothed_rms = raw_rms;
            else
                inject_agc_smoothed_rms += VcAudioState::kAgcRmsSmooth * (raw_rms - inject_agc_smoothed_rms);
            float target_gain = 1.0f;
            if (raw_rms >= VcAudioState::kAgcSilenceRms && inject_agc_smoothed_rms > 0.0001f)
                target_gain = VcAudioState::kAgcTargetRms / inject_agc_smoothed_rms;
            target_gain = std::max(VcAudioState::kAgcMinGain, std::min(VcAudioState::kAgcMaxGain, target_gain));
            float coeff = (target_gain < inject_agc_gain) ? VcAudioState::kAgcReleaseCoeff : VcAudioState::kAgcAttackCoeff;
            inject_agc_gain += coeff * (target_gain - inject_agc_gain);
            inject_agc_buffer.resize(frames);
            for (ma_uint32 i = 0; i < frames; ++i) {
                float v = static_cast<float>(raw_src[i]) * inject_agc_gain;
                inject_agc_buffer[i] = static_cast<int16_t>(
                    std::max(-32767.0f, std::min(32767.0f, v)));
            }
            src = inject_agc_buffer.data();
        }

        if (st->vad && (sr == 16000 || sr == 48000)) {
            int step = (sr == 48000) ? 3 : 1;
            for (ma_uint32 i = 0; i < frames; i += static_cast<ma_uint32>(step)) {
                st->vad_16k_buf[st->vad_16k_count++] = src[i];
                if (st->vad_16k_count >= 512)
                    st->score_vad_frame();
            }
        }
        st->frames_captured.fetch_add(frames, std::memory_order_relaxed);

        bool speech_active = !st->vad || st->vad_speech_active.load(std::memory_order_relaxed);
        if (st->vad && !speech_active) {
            inject_pre_roll.insert(inject_pre_roll.end(), src, src + n);
            if (inject_pre_roll.size() > inject_pre_roll_max) {
                inject_pre_roll.erase(inject_pre_roll.begin(), inject_pre_roll.begin() +
                    static_cast<std::ptrdiff_t>(inject_pre_roll.size() - inject_pre_roll_max));
            }
            std::memset(output + out_pos, 0, n * sizeof(int16_t));
            out_pos += n;
            continue;
        }

        if (!inject_pre_roll.empty()) {
            std::lock_guard<std::mutex> dsp_lock(st->dsp_mutex);
            const int16_t* pre_processed = st->dsp.process(inject_pre_roll.data(), inject_pre_roll.size(), sr);
            size_t write_len = std::min(inject_pre_roll.size(), out_pos);
            if (write_len > 0) {
                std::memcpy(output + out_pos - write_len,
                            pre_processed + (inject_pre_roll.size() - write_len),
                            write_len * sizeof(int16_t));
            }
            inject_pre_roll.clear();
        }

        // ── DSP chain with live VAD gating ──
        {
            std::lock_guard<std::mutex> dsp_lock(st->dsp_mutex);
            const int16_t* processed = st->dsp.process(src, static_cast<size_t>(frames), sr);

            // Write to output buffer instead of ring.
            std::memcpy(output + out_pos, processed, n * sizeof(int16_t));
        }
        out_pos += n;
    }

    // Mirror live finish_speech_segment(): process-file-live does not use the
    // capture callback speech buffer, but still exercises the configured
    // raw-worker or normal Moonshine-gated queue contract.
    const std::vector<int16_t> segment(input, input + num_samples);
    st->queue_laugh_asr_segment(segment, {}, sr);

    // ── Loaded VC inference (same helper as live debug segment dump) ──
    bool laugh_replaced = false;
    VcRawOutputGainState raw_gain;
    if (st->raw_rvc_mode) raw_gain.reset(st->playback_gain);
    bool vc_applied = vc_apply_loaded_inference(onnx_, seedvc_, output, num_samples, sr,
        st->raw_rvc_mode ? &raw_gain : nullptr);
    if (st->conversion_required && !vc_applied) {
        std::memset(output, 0, num_samples * sizeof(int16_t));
        if (out_pressure_json) *out_pressure_json = nullptr;
        return -4;
    }

    // Match live playback output: on_playback applies the session output volume
    // after RT-RVC/post processing, so injected live-route captures must do the same.
    if (!st->raw_rvc_mode)
        st->apply_output_gain(output, num_samples);

    // process-file-live represents one completed VAD segment. Persist the
    // same final PCM16 payload as the live finish_speech_segment() path.
    if (st->wav_output_enabled && num_samples > 0)
        vc_save_final_wav_async(st->wav_output_directory,
            std::vector<int16_t>(output, output + num_samples), static_cast<uint32_t>(sr));

    // ── Pressure stats JSON ──
    if (out_pressure_json) {
        float ps = st->dsp.pressure.pressure_score.load(std::memory_order_relaxed);
        float rm = st->dsp.pressure.rms_level.load(std::memory_order_relaxed);
        float pk = st->dsp.pressure.peak_level.load(std::memory_order_relaxed);

        float in_sum_sq = 0.0f, in_peak = 0.0f;
        for (size_t i = 0; i < num_samples; ++i) {
            float s = std::fabs(input[i] * kSampleScale);
            in_sum_sq += s * s;
            if (s > in_peak) in_peak = s;
        }
        float in_rms = std::sqrt(in_sum_sq / static_cast<float>(num_samples));

        float out_sum_sq = 0.0f, out_peak = 0.0f;
        for (size_t i = 0; i < num_samples; ++i) {
            float s = std::fabs(output[i] * kSampleScale);
            out_sum_sq += s * s;
            if (s > out_peak) out_peak = s;
        }
        float out_rms = std::sqrt(out_sum_sq / static_cast<float>(num_samples));

        char buf[512];
        std::snprintf(buf, sizeof(buf),
            "{\"pressureScore\":%.4f,\"rmsLevel\":%.4f,\"peakLevel\":%.4f,"
            "\"inputRms\":%.4f,\"inputPeak\":%.4f,"
            "\"outputRms\":%.4f,\"outputPeak\":%.4f,"
            "\"rmsReduction\":%.4f,\"peakReduction\":%.4f,"
            "\"vcApplied\":%s,\"laughExpressionReplaced\":%s,\"liveRoute\":true}",
            ps, rm, pk,
            in_rms, in_peak,
            out_rms, out_peak,
            (in_rms > 0.0001f) ? (1.0f - out_rms / in_rms) : 0.0f,
            (in_peak > 0.0001f) ? (1.0f - out_peak / in_peak) : 0.0f,
            vc_applied ? "true" : "false",
            laugh_replaced ? "true" : "false");
        *out_pressure_json = vc_dup_string(buf);
    }

    return static_cast<int>(num_samples);
}

} // namespace mobmate::vc

// ── extern "C" API ──────────────────────────────────────────────────

extern "C" MMV2_API int mmv2_vc_create(const char* options_json_utf8, void** out_handle) {
    if (!out_handle) return -1;
    *out_handle = nullptr;
    try {
        auto* session = new mobmate::vc::VoiceConversionSession(options_json_utf8);
        *out_handle = session;
        return 0;
    } catch (...) {
        return -2;
    }
}

extern "C" MMV2_API int mmv2_vc_start(void* handle) {
    if (!handle) return -1;
    try {
        return static_cast<mobmate::vc::VoiceConversionSession*>(handle)->start();
    } catch (...) {
        return -2;
    }
}

extern "C" MMV2_API int mmv2_vc_stop(void* handle) {
    if (!handle) return -1;
    try {
        return static_cast<mobmate::vc::VoiceConversionSession*>(handle)->stop();
    } catch (...) {
        return -2;
    }
}

extern "C" MMV2_API int mmv2_vc_filler_exchange(void* handle,
    const mmv2_vc_filler_update* update, mmv2_vc_filler_snapshot* snapshot) {
    if (!handle) return -1;
    try {
        return static_cast<mobmate::vc::VoiceConversionSession*>(handle)->filler_exchange(update, snapshot);
    } catch (...) {
        return -3;
    }
}

extern "C" MMV2_API int mmv2_vc_poll_status(void* handle, const char** out_json) {
    if (!out_json) return -1;
    *out_json = nullptr;
    if (!handle) {
        *out_json = vc_dup_string(error_json("invalid voice conversion session handle"));
        return -2;
    }
    try {
        *out_json = vc_dup_string(static_cast<mobmate::vc::VoiceConversionSession*>(handle)->status_json());
        return *out_json ? 0 : -3;
    } catch (...) {
        *out_json = vc_dup_string(error_json("voice conversion status failed"));
        return -4;
    }
}

extern "C" MMV2_API int mmv2_vc_process_buffer(void* handle,
    const int16_t* input, int num_samples, int16_t* output, const char** out_pressure_json) {
    if (!handle || !input || !output || num_samples <= 0) return -1;
    const char** json_ptr = out_pressure_json;
    char* json_buf = nullptr;
    int result = static_cast<mobmate::vc::VoiceConversionSession*>(handle)
        ->process_buffer(input, static_cast<size_t>(num_samples), output,
                         json_ptr ? &json_buf : nullptr);
    if (json_ptr) *json_ptr = json_buf;
    return result;
}

extern "C" MMV2_API int mmv2_vc_inject_and_capture(void* handle,
    const int16_t* input, int num_samples, int16_t* output, const char** out_json) {
    if (!handle || !input || !output || num_samples <= 0) return -1;
    char* json_buf = nullptr;
    int result = static_cast<mobmate::vc::VoiceConversionSession*>(handle)
        ->inject_and_capture(input, static_cast<size_t>(num_samples), output,
                             out_json ? &json_buf : nullptr);
    if (out_json) *out_json = json_buf;
    return result;
}

extern "C" MMV2_API int mmv2_vc_load_models(void* handle) {
    if (!handle) return -1;
    return static_cast<mobmate::vc::VoiceConversionSession*>(handle)->load_models();
}

extern "C" MMV2_API void mmv2_vc_unload_models(void* handle) {
    if (handle) static_cast<mobmate::vc::VoiceConversionSession*>(handle)->unload_models();
}

extern "C" MMV2_API void mmv2_vc_destroy(void* handle) {
    delete static_cast<mobmate::vc::VoiceConversionSession*>(handle);
}
