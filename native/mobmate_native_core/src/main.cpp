#include <iostream>
    #include <cstdlib>
    #include <fstream>
    #include <string>
    #include <vector>
    #include <map>
     #include <chrono>
     #include <filesystem>
     #include <memory>
     #include <optional>
     #include <sstream>
     #include <cmath>
     #include <cstdio>
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#endif
#include "mobmate_native_core/version.h"
#include "mobmate_native_core/wave.h"
#include "mobmate_native_core/manifest.h"
#include "mobmate_native_core/metrics.h"
#include "mobmate_native_core/whisper_bridge.h"
#include "mobmate_native_core/audio_prefilter.h"
#include "mobmate_native_core/moonshine_bridge.h"
#include "mobmate_native_core/silero_vad.h"

static void print_help() {
    std::cout << R"(Usage: mobmate-native <command> [options]

Commands:
  version          Show version information
  bench            Run benchmark (Whisper)
  bench-moonshine     Run benchmark (Moonshine)
  bench-realtime-route Run benchmark using realtime pipeline
  transcribe       Transcribe audio file (placeholder)
  wav-info         Show WAV file metadata as JSON
  vad-test         Run SileroVAD over a WAV file
  manifest-info    Show manifest CSV contents
  help             Show this help message
)" << std::endl;
}

#ifdef _WIN32
static std::string wide_to_utf8(const wchar_t* value) {
    if (value == nullptr) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return {};
    std::string utf8(static_cast<size_t>(len), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, value, -1, utf8.data(), len, nullptr, nullptr) <= 0) return {};
    utf8.resize(static_cast<size_t>(len - 1));
    return utf8;
}
#endif

static void cmd_version();
static void cmd_bench(const std::vector<std::string>& args);
static void cmd_bench_moonshine(const std::vector<std::string>& args);
static void cmd_bench_realtime_route(const std::vector<std::string>& args);
static void cmd_transcribe(const std::vector<std::string>& args);
static void cmd_wav_info(const std::vector<std::string>& args);
static void cmd_vad_test(const std::vector<std::string>& args);
static void cmd_manifest_info(const std::vector<std::string>& args);

static void append_json_float_or_null(std::ostringstream& oss, float value) {
    if (value >= 0.0f) {
        oss << value;
    } else {
        oss << "null";
    }
}

static std::string whisper_candidate_json(const mobmate::WhisperEngine::DetailedTranscribeResult& r) {
    std::ostringstream oss;
    oss.setf(std::ios::fixed);
    oss.precision(4);
    oss << "\"engine\":\"whisper\",\"selected_engine\":\"whisper\",\"decision_reason\":\"single_engine\",";
    oss << "\"confidence_score\":"; append_json_float_or_null(oss, r.confidence_score);
    oss << ",\"candidates\":[{\"engine\":\"whisper\",\"confidence_score\":";
    append_json_float_or_null(oss, r.confidence_score);
    oss << ",\"segment_count\":" << r.segment_count
        << ",\"token_count\":" << r.token_count
        << ",\"avg_token_p\":"; append_json_float_or_null(oss, r.avg_token_p);
    oss << ",\"min_token_p\":"; append_json_float_or_null(oss, r.min_token_p);
    oss << ",\"avg_no_speech_prob\":"; append_json_float_or_null(oss, r.avg_no_speech_prob);
    oss << ",\"max_no_speech_prob\":"; append_json_float_or_null(oss, r.max_no_speech_prob);
    oss << "}]";
    return oss.str();
}

static std::string moonshine_candidate_json(const mobmate::MoonshineBridge::TranscribeResult& r,
                                              int leading_pad, int tail_pad,
                                              double normalize_dbfs, bool without_streaming,
                                              double slowdown_ratio) {
    std::ostringstream oss;
    oss.setf(std::ios::fixed);
    oss.precision(4);
    oss << "\"engine\":\"moonshine\",\"selected_engine\":\"moonshine\",\"decision_reason\":\"single_engine\",";
    oss << "\"confidence_score\":"; append_json_float_or_null(oss, r.confidence_score);
    oss << ",\"confidence_available\":" << (r.confidence_score >= 0.0f ? "true" : "false");
    if (r.confidence_score < 0.0f) {
        oss << ",\"confidence_unavailable_reason\":\"moonshine_word_confidence_unavailable\"";
    }
    oss << ",\"candidates\":[{\"engine\":\"moonshine\",\"confidence_score\":";
    append_json_float_or_null(oss, r.confidence_score);
    oss << ",\"confidence_available\":" << (r.confidence_score >= 0.0f ? "true" : "false");
    oss << ",\"line_count\":" << r.line_count
        << ",\"word_count\":" << r.word_count
        << ",\"avg_word_confidence\":"; append_json_float_or_null(oss, r.avg_word_confidence);
    oss << ",\"min_word_confidence\":"; append_json_float_or_null(oss, r.min_word_confidence);
    oss << ",\"line_confidence\":"; append_json_float_or_null(oss, r.line_confidence);
    oss << "}]";
    // Add preset info
    oss.precision(2);
    oss << ",\"preset\":{\"leading_padding_ms\":" << leading_pad
        << ",\"tail_padding_ms\":" << tail_pad
        << ",\"normalize_dbfs\":" << normalize_dbfs
        << ",\"without_streaming\":" << (without_streaming ? "true" : "false")
        << ",\"slowdown_ratio\":" << slowdown_ratio << "}";
    return oss.str();
}

static int run_main(const std::vector<std::string>& argv) {
    if (argv.size() < 2) {
        print_help();
        return 0;
    }

    std::string command = argv[1];

    if (command == "help" || command == "--help" || command == "-h") {
        print_help();
    } else if (command == "version") {
        cmd_version();
   } else if (command == "bench") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_bench(args);
    } else if (command == "bench-moonshine") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_bench_moonshine(args);
    } else if (command == "bench-realtime-route") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_bench_realtime_route(args);
    } else if (command == "transcribe") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_transcribe(args);
    } else if (command == "wav-info") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_wav_info(args);
    } else if (command == "vad-test") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_vad_test(args);
    } else if (command == "manifest-info") {
        std::vector<std::string> args(argv.begin() + 2, argv.end());
        cmd_manifest_info(args);
    } else {
        std::cerr << "Unknown command: " << command << std::endl;
        print_help();
        return 1;
    }

    return 0;
}

static void cmd_version() {
    std::cout << "mobmate-native " << mobmate::get_version() << std::endl;
}

 /* ------------------------------------------------------------------ */
/*  UTF-8 helper                                                       */
/* ------------------------------------------------------------------ */

static size_t utf8_codepoint(const std::string& s, size_t pos, int& out_cp) {
    if (pos >= s.size()) return 0;
    unsigned char c = static_cast<unsigned char>(s[pos]);
    if (c < 0x80) { out_cp = c; return 1; }
    if (c < 0xC0) return 0; // continuation byte
    if (c < 0xE0) {
        if (pos + 1 >= s.size()) return 0;
        unsigned char c2 = static_cast<unsigned char>(s[pos + 1]);
        if ((c2 & 0xC0) != 0x80) return 0;
        out_cp = ((c & 0x1F) << 6) | (c2 & 0x3F);
        return 2;
    }
    if (c < 0xF0) {
        if (pos + 2 >= s.size()) return 0;
        unsigned char c2 = static_cast<unsigned char>(s[pos + 1]);
        unsigned char c3 = static_cast<unsigned char>(s[pos + 2]);
        if ((c2 & 0xC0) != 0x80 || (c3 & 0xC0) != 0x80) return 0;
        out_cp = ((c & 0x0F) << 12) | ((c2 & 0x3F) << 6) | (c3 & 0x3F);
        return 3;
    }
    if (c < 0xF8) {
        if (pos + 3 >= s.size()) return 0;
        unsigned char c2 = static_cast<unsigned char>(s[pos + 1]);
        unsigned char c3 = static_cast<unsigned char>(s[pos + 2]);
        unsigned char c4 = static_cast<unsigned char>(s[pos + 3]);
        if ((c2 & 0xC0) != 0x80 || (c3 & 0xC0) != 0x80 || (c4 & 0xC0) != 0x80) return 0;
        out_cp = ((c & 0x07) << 18) | ((c2 & 0x3F) << 12) | ((c3 & 0x3F) << 6) | (c4 & 0x3F);
        return 4;
    }
    return 0;
}

/* ------------------------------------------------------------------ */
/*  CM-style post-processing (matching Java WhisperBenchmarkCli)       */
/* ------------------------------------------------------------------ */

static bool has_long_repeat_run(const std::string& text, int min_run) {
    if (text.empty() || min_run <= 1) return false;
    int prev = -1;
    int run = 0;
    size_t i = 0;
    while (i < text.size()) {
        int cp;
        size_t chars = utf8_codepoint(text, i, cp);
        if (chars == 0) { i++; continue; }
        if (cp == prev) {
            run++;
            if (run >= min_run) return true;
        } else {
            prev = cp;
            run = 1;
        }
        i += chars;
    }
    return false;
}

static int codepoint_count(const std::string& text) {
    int count = 0;
    size_t i = 0;
    int cp;
    while (i < text.size()) {
        size_t chars = utf8_codepoint(text, i, cp);
        if (chars == 0) { i++; continue; }
        count++;
        i += chars;
    }
    return count;
}

static bool contains_latin(const std::string& text) {
    size_t i = 0;
    int cp;
    while (i < text.size()) {
        size_t chars = utf8_codepoint(text, i, cp);
        if (chars == 0) { i++; continue; }
        if (cp >= 0x41 && cp <= 0x5A) return true; // A-Z
        if (cp >= 0x61 && cp <= 0x7A) return true; // a-z
        i += chars;
    }
    return false;
}

static std::string strip_trailing_punct(const std::string& text) {
    // Strip trailing Japanese/English punctuation
    std::string result = text;
    while (!result.empty()) {
        size_t last = result.size() - 1;
        int cp;
        size_t chars = utf8_codepoint(result, last, cp);
        if (chars == 0) {
            result.pop_back();
            continue;
        }
        // Check if it's a punctuation character we want to strip
        bool is_punct = (cp == 0x3001) || // 、
                        (cp == 0x3002) || // 。
                        (cp == ',') || (cp == '.') ||
                        (cp == '!') || (cp == '?') ||
                        (cp == '"') || (cp == '\'') ||
                        (cp == '`') ||
                        (cp == 0x2019) || // '
                        (cp == 0x201C) || // "
                        std::isspace(cp);
        if (is_punct) {
            result.erase(last, chars);
        } else {
            break;
        }
    }
    return result;
}

static std::string apply_short_command_hallucination_guard(const std::string& text,
                                                            const std::string& scene,
                                                            const std::string& language) {
    std::string t = text;
    // Trim whitespace
    while (!t.empty() && std::isspace(t.front())) t.erase(t.begin());
    while (!t.empty() && std::isspace(t.back())) t.pop_back();

    if (scene != "short_command") return t;

    int cp = codepoint_count(t);
    if (cp > 40) return "";
    if (has_long_repeat_run(t, 5)) return "";
    if (language == "ja" && cp > 12 && contains_latin(t)) return "";

    return t;
}

static std::string apply_short_command_tail_guard(const std::string& text,
                                                    const std::string& scene,
                                                    const std::string& language) {
    std::string t = text;
    while (!t.empty() && std::isspace(t.front())) t.erase(t.begin());
    while (!t.empty() && std::isspace(t.back())) t.pop_back();

    if (scene != "short_command") return t;
    if (language != "ja") return t;

    std::string trimmed = strip_trailing_punct(t);
    if (trimmed.empty()) return "";

    int before = codepoint_count(t);
    int after = codepoint_count(trimmed);
    if (after >= before) return t;
    if ((before - after) <= 3) return trimmed;
    return t;
}

 static void cmd_bench(const std::vector<std::string>& args) {
   std::string manifest_path;
    std::string out_path;
    std::string model_path;
    std::string dll_dir;
    int n_threads = 4;
   bool use_prefilter = true;  // default: enabled (product path)
    std::string prefilter_profile = "auto";
    bool use_rms_norm = false;
     double rms_target = 0.07;  // default -23.2 dBFS; CM uses ~-7 dBFS (0.2)
     bool use_silence_trim = false;
    int silence_trim_threshold = 300;
    int silence_trim_tail_pad = 120;
    int silence_append_ms = 0;
    double slowdown_factor = 1.0;
   int long_mode_sec = 0;  // 0 = disabled; >0 = use long mode (no_context=false) for utterances >= this many seconds
    double highpass_hz = 0.0;  // 0 = disabled; >0 = apply standalone RC high-pass filter at this cutoff
   bool use_postprocess = false;  // CM-style hallucination guard + tail guard
    bool use_cm_slowdown = false;  // CM-style conditional slowdown (low_muffled_guard + short trimmed)
     bool use_cm_slowdown_ja = false;  // if true, only apply CM slowdown for Japanese items
     bool diagnose_cm = false;  // if true, print CM slowdown condition diagnostics per item
    bool use_gpu = false;
    int gpu_device = 0;
    double rms_target_ja = -1.0;  // -1 = disabled; >=0 = use this RMS target for Japanese only
    std::map<std::string, std::string> exp_params;

    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "--manifest" && i + 1 < args.size()) { manifest_path = args[++i]; }
        else if (args[i] == "--out" && i + 1 < args.size()) { out_path = args[++i]; }
        else if (args[i] == "--model" && i + 1 < args.size()) { model_path = args[++i]; }
        else if (args[i] == "--whisper-dll-dir" && i + 1 < args.size()) { dll_dir = args[++i]; }
        else if (args[i] == "--threads" && i + 1 < args.size()) { n_threads = std::stoi(args[++i]); }
      else if (args[i] == "--no-prefilter") { use_prefilter = false; }
         else if (args[i] == "--prefilter-profile" && i + 1 < args.size()) { prefilter_profile = args[++i]; }
        else if (args[i] == "--rms-norm") { use_rms_norm = true; }
        else if (args[i] == "--rms-target" && i + 1 < args.size()) { rms_target = std::stod(args[++i]); }
        else if (args[i] == "--silence-trim") { use_silence_trim = true; }
        else if (args[i] == "--silence-trim-threshold" && i + 1 < args.size()) { silence_trim_threshold = std::stoi(args[++i]); }
        else if (args[i] == "--silence-trim-tail-pad" && i + 1 < args.size()) { silence_trim_tail_pad = std::stoi(args[++i]); }
        else if (args[i] == "--silence-append" && i + 1 < args.size()) { silence_append_ms = std::stoi(args[++i]); }
        else if (args[i] == "--slowdown" && i + 1 < args.size()) { slowdown_factor = std::stod(args[++i]); }
        else if (args[i] == "--long-mode-sec" && i + 1 < args.size()) { long_mode_sec = std::stoi(args[++i]); }
        else if (args[i] == "--highpass-hz" && i + 1 < args.size()) { highpass_hz = std::stod(args[++i]); }
        else if (args[i] == "--postprocess") { use_postprocess = true; }
        else if (args[i] == "--cm-slowdown") { use_cm_slowdown = true; }
        else if (args[i] == "--cm-slowdown-ja") { use_cm_slowdown = true; use_cm_slowdown_ja = true; }
        else if (args[i] == "--diagnose-cm") { diagnose_cm = true; }
        else if (args[i] == "--gpu") { use_gpu = true; }
       else if (args[i] == "--gpu-device" && i + 1 < args.size()) { gpu_device = std::stoi(args[++i]); }
        else if (args[i] == "--rms-target-ja" && i + 1 < args.size()) { rms_target_ja = std::stod(args[++i]); }
        else if (args[i] == "--exp" && i + 1 < args.size()) { exp_params["__keys__"] += args[++i] + ";"; }
    }

    if (manifest_path.empty() || out_path.empty()) {
        std::cerr << "bench: --manifest and --out are required" << std::endl;
        std::exit(1);
    }
    if (prefilter_profile != "auto" && prefilter_profile != "normal" && prefilter_profile != "strong") {
        std::cerr << "bench: --prefilter-profile must be auto, normal, or strong" << std::endl;
        std::exit(1);
    }

    // Read manifest
    std::string err_msg;
    auto rows = mobmate::read_manifest(manifest_path, err_msg);
    if (!rows) {
        std::cerr << "bench: failed to read manifest: " << err_msg << std::endl;
        std::exit(1);
    }

    std::cout << "bench: loaded " << rows->size() << " rows from " << manifest_path << std::endl;

    // Optionally load Whisper engine
    std::unique_ptr<mobmate::WhisperEngine> engine;
    if (!model_path.empty() && !dll_dir.empty()) {
        engine = mobmate::WhisperEngine::load(dll_dir, model_path, err_msg, use_gpu, gpu_device);
        if (!engine) {
            std::cerr << "bench: failed to load Whisper engine: " << err_msg << std::endl;
            std::exit(1);
        }
        std::cout << "bench: Whisper engine loaded (model=" << model_path
                   << ", gpu=" << (use_gpu ? "on" : "off")
                   << ", gpu_device=" << gpu_device << ")" << std::endl;
            }
            // Log exp params
            auto it_keys = exp_params.find("__keys__");
            if (it_keys != exp_params.end() && !it_keys->second.empty()) {
                std::cout << "bench: exp params: " << it_keys->second << std::endl;
            }

    // Run benchmark
    std::vector<mobmate::BenchResult> results;
    for (const auto& row : *rows) {
        mobmate::BenchResult result;
        result.index = row.index;
        result.wav_path = row.wav_path;
        result.expected = row.expected;
        auto score_whisper_result = [&]() {
            result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, false);
            result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, false);
        };

        if (engine) {
            // Read WAV metadata to get actual sample rate
            std::string wav_err;
            auto wav_meta = mobmate::read_wav_metadata(row.wav_path, wav_err);
            if (!wav_meta) {
                result.transcript = "";
                score_whisper_result();
                result.latency_ms = -1;
                result.error = "WAV metadata failed: " + wav_err;
                results.push_back(result);
                continue;
            }

            // Read WAV and transcribe
            auto samples = mobmate::read_wav_samples(row.wav_path, wav_err);
            if (!samples) {
                result.transcript = "";
                score_whisper_result();
                result.latency_ms = -1;
                result.error = "WAV read failed: " + wav_err;
                results.push_back(result);
                continue;
            }

            // Resample to 16kHz if needed (Whisper requires 16kHz)
            const std::vector<int16_t>* use_samples_raw = &*samples;
            std::optional<std::vector<int16_t>> resampled_samples;
            if (wav_meta->sample_rate != 16000) {
                std::vector<uint8_t> pcm_bytes(samples->size() * 2);
                for (size_t i = 0; i < samples->size(); i++) {
                    int16_t s = (*samples)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                auto resampled = mobmate::resample_audio(pcm_bytes.data(),
                                                          static_cast<int>(pcm_bytes.size()),
                                                          static_cast<int>(wav_meta->sample_rate),
                                                          16000);
                if (!resampled.empty()) {
                    resampled_samples.emplace();
                    for (size_t i = 0; i < resampled.size(); i += 2) {
                        int lo = resampled[i] & 0xFF;
                        int hi = resampled[i + 1];
                        resampled_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples_raw = &*resampled_samples;
                }
            }

            // Optionally apply audio prefilter
            const std::vector<int16_t>* use_samples = use_samples_raw;
            std::optional<std::vector<int16_t>> prefiltred_samples;
          if (use_prefilter) {
                mobmate::AudioPrefilterState pstate;
                pstate.reset();
                // Convert int16 vector to uint8_t buffer for prefilter
                std::vector<uint8_t> pcm_bytes(use_samples->size() * 2);
                for (size_t i = 0; i < use_samples->size(); i++) {
                    int16_t s = (*use_samples)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                // STRONG profile for talk_noisy / desktop_hearing scenes
                bool use_strong = (prefilter_profile == "strong") ||
                    (prefilter_profile == "auto" && (row.scene == "talk_noisy" || row.scene == "desktop_hearing"));
                if (use_strong) {
                    std::cout << "bench: applying STRONG prefilter for scene=" << row.scene << std::endl;
                    std::cout.flush();
                }
                auto filtered = use_strong
                    ? mobmate::prefilter_process_strong(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &pstate)
                    : mobmate::prefilter_process(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &pstate);
                if (!filtered.empty()) {
                    prefiltred_samples.emplace();
                    for (size_t i = 0; i < filtered.size(); i += 2) {
                        int lo = filtered[i] & 0xFF;
                        int hi = filtered[i + 1];
                        prefiltred_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples = &*prefiltred_samples;
                }
            }

            // Optionally apply silence trimming
            const std::vector<int16_t>* use_samples_after_trim = use_samples;
            std::optional<std::vector<int16_t>> trimmed_samples;
            if (use_silence_trim) {
                // Convert int16 vector to uint8_t buffer for trim_silence
                std::vector<uint8_t> pcm_bytes(use_samples->size() * 2);
                for (size_t i = 0; i < use_samples->size(); i++) {
                    int16_t s = (*use_samples)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                auto trimmed = mobmate::trim_silence(pcm_bytes.data(),
                                                     static_cast<int>(pcm_bytes.size()),
                                                     16000,
                                                     silence_trim_threshold,
                                                     silence_trim_tail_pad);
                if (!trimmed.empty()) {
                    trimmed_samples.emplace();
                    for (size_t i = 0; i < trimmed.size(); i += 2) {
                        int lo = trimmed[i] & 0xFF;
                        int hi = trimmed[i + 1];
                        trimmed_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples_after_trim = &*trimmed_samples;
                }
            }

            // Optionally append silence (tail padding for Whisper context)
            const std::vector<int16_t>* use_samples_after_append = use_samples_after_trim;
            std::optional<std::vector<int16_t>> padded_samples;
            if (silence_append_ms > 0) {
                std::vector<uint8_t> pcm_bytes(use_samples_after_trim->size() * 2);
                for (size_t i = 0; i < use_samples_after_trim->size(); i++) {
                    int16_t s = (*use_samples_after_trim)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                auto padded = mobmate::append_silence(pcm_bytes.data(),
                                                      static_cast<int>(pcm_bytes.size()),
                                                      16000,
                                                      silence_append_ms);
                if (!padded.empty()) {
                    padded_samples.emplace();
                    for (size_t i = 0; i < padded.size(); i += 2) {
                        int lo = padded[i] & 0xFF;
                        int hi = padded[i + 1];
                        padded_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples_after_append = &*padded_samples;
                }
            }

         // Optionally apply CM-style conditional slowdown (low_muffled_guard + short trimmed)
            const std::vector<int16_t>* use_samples_after_slowdown = use_samples_after_append;
            std::optional<std::vector<int16_t>> slowed_samples;
            double effective_slowdown = slowdown_factor;

            if (use_cm_slowdown) {
                // CM logic: analyze raw audio for low_muffled_guard conditions,
                // then apply 1.06x slowdown if trimmed duration <= 1200ms
                std::vector<uint8_t> raw_pcm_bytes(use_samples_raw->size() * 2);
                for (size_t i = 0; i < use_samples_raw->size(); i++) {
                    int16_t s = (*use_samples_raw)[i];
                    raw_pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    raw_pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                mobmate::VoiceMetrics vm = mobmate::analyze_voice_like(
                    raw_pcm_bytes.data(),
                    static_cast<int>(raw_pcm_bytes.size()),
                    16000);

                // Raw duration in ms
                double raw_duration_ms = static_cast<double>(use_samples_raw->size()) / 16000.0 * 1000.0;

                // Check low_muffled_guard conditions
                bool is_low_muffled = (raw_duration_ms >= 2000.0)
                    && (vm.rms >= 700.0 && vm.rms <= 2100.0)
                    && (vm.peak >= 6000 && vm.peak <= 24000)
                    && (vm.voiceBandRatio >= 0.55 && vm.voiceBandRatio <= 0.82);

                // Check trimmed duration <= 1200ms
                double trimmed_duration_ms = static_cast<double>(use_samples_after_append->size()) / 16000.0 * 1000.0;
                bool is_short_trimmed = (trimmed_duration_ms > 0.0 && trimmed_duration_ms <= 1200.0);

                if (is_low_muffled && is_short_trimmed
                    && (!use_cm_slowdown_ja || row.language == "ja")) {
                    effective_slowdown = 1.06;
                }

                if (diagnose_cm) {
                    bool dur_ok = (raw_duration_ms >= 2000.0);
                    bool rms_ok = (vm.rms >= 700.0 && vm.rms <= 2100.0);
                    bool peak_ok = (vm.peak >= 6000 && vm.peak <= 24000);
                    bool vbr_ok = (vm.voiceBandRatio >= 0.55 && vm.voiceBandRatio <= 0.82);
                    bool lang_ok = (!use_cm_slowdown_ja || row.language == "ja");
                    fprintf(stderr,
                        "CM_DIAG idx=%d lang=%s raw_dur=%.0fms rms=%.0f peak=%d vbr=%.3f trim_dur=%.0fms | "
                        "dur_%s rms_%s peak_%s vbr_%s trim_%s lang_%s => slowdown_%s\n",
                        row.index, row.language.c_str(),
                        raw_duration_ms, vm.rms, (int)vm.peak, vm.voiceBandRatio, trimmed_duration_ms,
                        dur_ok ? "OK" : "NO", rms_ok ? "OK" : "NO", peak_ok ? "OK" : "NO",
                        vbr_ok ? "OK" : "NO", is_short_trimmed ? "OK" : "NO", lang_ok ? "OK" : "NO",
                        effective_slowdown > 1.0 ? "YES" : "NO");
                }
            }

            if (effective_slowdown > 1.0) {
                std::vector<uint8_t> pcm_bytes(use_samples_after_append->size() * 2);
                for (size_t i = 0; i < use_samples_after_append->size(); i++) {
                    int16_t s = (*use_samples_after_append)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                auto slowed = mobmate::slowdown_audio(pcm_bytes.data(),
                                                        static_cast<int>(pcm_bytes.size()),
                                                        16000,
                                                        effective_slowdown);
                if (!slowed.empty()) {
                    slowed_samples.emplace();
                    for (size_t i = 0; i < slowed.size(); i += 2) {
                        int lo = slowed[i] & 0xFF;
                        int hi = slowed[i + 1];
                        slowed_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples_after_slowdown = &*slowed_samples;
                }
            }

            // Optionally apply standalone high-pass filter
            const std::vector<int16_t>* use_samples_after_hp = use_samples_after_slowdown;
            std::optional<std::vector<int16_t>> hp_samples;
            if (highpass_hz > 0.0) {
                std::vector<uint8_t> pcm_bytes(use_samples_after_slowdown->size() * 2);
                for (size_t i = 0; i < use_samples_after_slowdown->size(); i++) {
                    int16_t s = (*use_samples_after_slowdown)[i];
                    pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                    pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
                }
                auto hp_filtered = mobmate::apply_highpass(pcm_bytes.data(),
                                                           static_cast<int>(pcm_bytes.size()),
                                                           16000,
                                                           highpass_hz);
                if (!hp_filtered.empty()) {
                    hp_samples.emplace();
                    for (size_t i = 0; i < hp_filtered.size(); i += 2) {
                        int lo = hp_filtered[i] & 0xFF;
                        int hi = hp_filtered[i + 1];
                        hp_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                    }
                    use_samples_after_hp = &*hp_samples;
                }
            }

            // Convert int16 to float32
            std::vector<float> float_samples(use_samples_after_hp->size());
            for (size_t i = 0; i < use_samples_after_hp->size(); ++i) {
                float_samples[i] = static_cast<float>((*use_samples_after_hp)[i]) / 32768.0f;
            }

            // RMS normalize (target RMS = 0.07 ≈ -23.2 dBFS, matching Current MobMate)
            // Language-specific override: --rms-target-ja for Japanese only
            if (use_rms_norm) {
                double rms_sum = 0.0;
                for (size_t i = 0; i < float_samples.size(); ++i) {
                    rms_sum += float_samples[i] * float_samples[i];
                }
                double rms = std::sqrt(rms_sum / float_samples.size());
                if (rms > 1e-6) {
                    double effective_target = rms_target;
                    if (rms_target_ja >= 0.0 && row.language == "ja") {
                        effective_target = rms_target_ja;
                    }
                    float target = static_cast<float>(effective_target);
                    float scale = target / static_cast<float>(rms);
                    for (size_t i = 0; i < float_samples.size(); ++i) {
                        float_samples[i] *= scale;
                    }
                }
            }

            // Transcribe with timing
            auto start = std::chrono::steady_clock::now();

            // Build TranscribeOpts from --exp params
            mobmate::WhisperEngine::TranscribeOpts topts;
            topts.language = row.language;
            topts.n_threads = n_threads;

            // Long mode: for utterances >= long_mode_sec, use no_context=false
            // (matching Current MobMate's longMode behavior at 3 seconds)
            if (long_mode_sec > 0) {
                double duration_sec = static_cast<double>(float_samples.size()) / 16000.0;
                if (duration_sec >= static_cast<double>(long_mode_sec)) {
                    topts.no_context = false;
                }
            }

            // Parse exp_params (stored as semicolon-separated key=val pairs)
            auto it_keys = exp_params.find("__keys__");
            if (it_keys != exp_params.end()) {
                std::string keys = it_keys->second;
                for (size_t k = 0; k < keys.size(); ) {
                    size_t semi = keys.find(';', k);
                    if (semi == std::string::npos) semi = keys.size();
                    std::string pair = keys.substr(k, semi - k);
                    k = semi + 1;
                    if (pair.empty()) continue;
                    size_t eq = pair.find('=');
                    if (eq == std::string::npos) continue;
                    std::string key = pair.substr(0, eq);
                    std::string val = pair.substr(eq + 1);

                    if (key == "no_context") topts.no_context = (val == "true" || val == "1");
                    else if (key == "temperature") topts.temperature = std::stof(val);
                    else if (key == "initial_prompt") topts.initial_prompt = val;
                    else if (key == "suppress_blank") topts.suppress_blank = (val == "true" || val == "1");
                    else if (key == "suppress_nst") topts.suppress_nst = (val == "true" || val == "1");
                    else if (key == "max_len") topts.max_len = std::stoi(val);
                    else if (key == "entropy_thold") topts.entropy_thold = std::stof(val);
                    else if (key == "logprob_thold") topts.logprob_thold = std::stof(val);
                    else if (key == "length_penalty") topts.length_penalty = std::stof(val);
                }
            }

            auto transcript = engine->transcribe_detailed(float_samples.data(),
                                                          static_cast<int>(float_samples.size()),
                                                          topts);
            auto end = std::chrono::steady_clock::now();
            result.latency_ms = static_cast<int64_t>(
                std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

            if (!transcript) {
                result.transcript = "";
                score_whisper_result();
                result.error = "transcription failed";
                result.extra_json_fields = "\"engine\":\"whisper\",\"selected_engine\":\"none\",\"decision_reason\":\"transcription_failed\",\"confidence_score\":null,\"candidates\":[]";
            } else {
                result.transcript = transcript->text;
                result.extra_json_fields = whisper_candidate_json(*transcript);
                // Apply CM-style post-processing if enabled
                if (use_postprocess) {
                    result.transcript = apply_short_command_hallucination_guard(
                        result.transcript, row.scene, row.language);
                    result.transcript = apply_short_command_tail_guard(
                        result.transcript, row.scene, row.language);
                }
                score_whisper_result();
                result.error = "";
            }
        } else {
            // Placeholder mode
            result.transcript = "";
            score_whisper_result();
            result.latency_ms = -1;
            result.error = "no ASR engine configured";
        }

        results.push_back(result);
    }

    // Write JSONL output
    std::ofstream ofs(out_path);
    if (!ofs) {
        std::cerr << "bench: cannot open output file: " << out_path << std::endl;
        std::exit(1);
    }

    for (const auto& r : results) {
        ofs << mobmate::bench_result_to_jsonl(r) << "\n";
    }
    ofs.close();

    // Compute and print summary
    auto summary = mobmate::compute_summary(results);
    std::cout << "bench: wrote " << results.size() << " results to " << out_path << std::endl;
    std::cout << "bench: summary:" << std::endl;
    std::cout << mobmate::summary_to_json(summary) << std::endl;
}
/* ------------------------------------------------------------------ */
/*  bench-realtime-route: benchmark using realtime processing pipeline  */
/*  Each WAV is treated as a single segment (no VAD segmentation).       */
/*  Pipeline: prefilter → normalize → padding → ASR → gate              */
/* ------------------------------------------------------------------ */

// V1-matching per-language+scene preset parameters for Moonshine
struct MoonshineLangPreset {
    int leading_padding_ms;
    int tail_padding_ms;
    double normalize_dbfs;
    bool without_streaming;
    double slowdown_ratio; // 1.0 = no slowdown
    int squash_abs_threshold; // 0 = disabled
    int squash_min_ms;        // minimum silence duration to squash
    int squash_keep_ms;       // how many ms of silence to keep
    int passes;               // V1-style multi-pass count (1-4)
    std::string prefilter;    // V1 prefilter: "off", "normal", "strong"
    int trim_abs_threshold;   // V1 silence trim amplitude threshold (0 = disabled)
    int trim_pad_ms;          // V1 silence trim padding to preserve at boundaries
    double vad_window_sec;
    int vad_look_behind_samples;
    double vad_threshold;
    double max_tokens_per_second;
};

// Forward declarations for multi-candidate helpers (defined later in this file)
static MoonshineLangPreset get_moonshine_lang_preset(const std::string& language, const std::string& scene);
static bool containsJapaneseScript(const std::string& text);
static int countCodePoints(const std::string& text);
static bool endsWithSentencePunctuation(const std::string& text);
static int score_moonshine_candidate(const std::string& text);
static bool isRepeatingSpam(const std::string& text);
static bool endsWith(const std::string& str, const std::string& suffix);
enum class MoonshineCandidateType {
    RAW,
    TRIMMED,
    PROCESSED
};
static std::vector<float> process_moonshine_candidate(
    const std::vector<int16_t>& samples_raw,
    MoonshineCandidateType type,
    bool do_prefilter, const std::string& prefilter_profile,
    const std::string& language, const std::string& scene,
    bool do_normalize, double normalize_dbfs,
    bool do_slowdown, double slowdown_ratio,
    bool do_rms_norm, double rms_target);

static void cmd_bench_realtime_route(const std::vector<std::string>& args) {
    std::string manifest_path;
    std::string out_path;
    std::string model_path;
    std::string whisper_dll_dir;
    std::string moonshine_dll_dir;
    std::string engine = "whisper";
    std::string language = "ja";
    std::string scene = "short_command";
    std::string prefilter_profile = "auto";
    std::string gate_mode = "on";
    int n_threads = 4;
    bool use_gpu = false;
    int gpu_device = 0;
    bool moonshine_multi_candidate = false; // V1-style multi-input ensemble for Moonshine
    bool moonshine_multi_pass = false;      // V1-style multi-pass streaming (3pass + settle)

    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "--manifest" && i + 1 < args.size()) { manifest_path = args[++i]; }
        else if (args[i] == "--out" && i + 1 < args.size()) { out_path = args[++i]; }
        else if (args[i] == "--model" && i + 1 < args.size()) { model_path = args[++i]; }
        else if (args[i] == "--whisper-dll-dir" && i + 1 < args.size()) { whisper_dll_dir = args[++i]; }
        else if (args[i] == "--moonshine-dll-dir" && i + 1 < args.size()) { moonshine_dll_dir = args[++i]; }
        else if (args[i] == "--engine" && i + 1 < args.size()) { engine = args[++i]; }
        else if (args[i] == "--language" && i + 1 < args.size()) { language = args[++i]; }
        else if (args[i] == "--scene" && i + 1 < args.size()) { scene = args[++i]; }
        else if (args[i] == "--prefilter" && i + 1 < args.size()) { prefilter_profile = args[++i]; }
        else if (args[i] == "--gate" && i + 1 < args.size()) { gate_mode = args[++i]; }
        else if (args[i] == "--threads" && i + 1 < args.size()) { n_threads = std::stoi(args[++i]); }
        else if (args[i] == "--gpu") { use_gpu = true; }
        else if (args[i] == "--gpu-device" && i + 1 < args.size()) { gpu_device = std::stoi(args[++i]); }
        else if (args[i] == "--moonshine-multi-candidate") { moonshine_multi_candidate = true; }
        else if (args[i] == "--moonshine-multi-pass") { moonshine_multi_pass = true; }
    }

    if (manifest_path.empty() || out_path.empty()) {
        std::cerr << "bench-realtime-route: --manifest and --out are required" << std::endl;
        std::exit(1);
    }
    if (engine == "whisper" && (model_path.empty() || whisper_dll_dir.empty())) {
        std::cerr << "bench-realtime-route: --model and --whisper-dll-dir required for engine=whisper" << std::endl;
        std::exit(1);
    }
    if (engine == "moonshine" && moonshine_dll_dir.empty()) {
        std::cerr << "bench-realtime-route: --moonshine-dll-dir required for engine=moonshine" << std::endl;
        std::exit(1);
    }
    if (engine != "whisper" && engine != "moonshine") {
        std::cerr << "bench-realtime-route: --engine must be whisper or moonshine" << std::endl;
        std::exit(1);
    }
    if (gate_mode != "on" && gate_mode != "off") {
        std::cerr << "bench-realtime-route: --gate must be on or off" << std::endl;
        std::exit(1);
    }
    if (engine == "moonshine" && (moonshine_multi_candidate || moonshine_multi_pass)) {
        std::cerr << "bench-realtime-route: experimental Moonshine multi-candidate/multi-pass "
                  << "is disabled because Phase 6G validation worsened 15-item CER; "
                  << "using baseline realtime route" << std::endl;
        moonshine_multi_candidate = false;
        moonshine_multi_pass = false;
    }
    if (prefilter_profile != "auto" && prefilter_profile != "normal" &&
        prefilter_profile != "strong" && prefilter_profile != "off") {
        std::cerr << "bench-realtime-route: --prefilter must be auto, normal, strong, or off" << std::endl;
        std::exit(1);
    }

    std::string err_msg;
    auto rows = mobmate::read_manifest(manifest_path, err_msg);
    if (!rows) {
        std::cerr << "bench-realtime-route: failed to read manifest: " << err_msg << std::endl;
        std::exit(1);
    }
    std::cout << "bench-realtime-route: loaded " << rows->size() << " rows from " << manifest_path << std::endl;
    std::cout << "bench-realtime-route: engine=" << engine
              << " language=" << language
              << " scene=" << scene
              << " prefilter=" << prefilter_profile
              << " gate=" << gate_mode << std::endl;

    std::unique_ptr<mobmate::WhisperEngine> whisper_engine;
    mobmate::MoonshineBridge moonshine_bridge;
    int moonshine_arch = mobmate::MoonshineBridge::ARCH_BASE;

    if (engine == "whisper") {
        std::string load_err;
        whisper_engine = mobmate::WhisperEngine::load(whisper_dll_dir, model_path, load_err, use_gpu);
        if (!whisper_engine) {
            std::cerr << "bench-realtime-route: failed to load Whisper: " << load_err << std::endl;
            std::exit(1);
        }
        std::cout << "bench-realtime-route: Whisper engine loaded" << std::endl;
    } else {
        if (!moonshine_bridge.init(moonshine_dll_dir)) {
            std::cerr << "bench-realtime-route: failed to load moonshine.dll" << std::endl;
            std::exit(1);
        }
        namespace fs = std::filesystem;
        fs::path model_root(model_path);
        std::string model_to_load;
        if (language == "ja") model_to_load = (model_root / "ja" / "quantized" / "base-ja").string();
        else if (language == "zh") model_to_load = (model_root / "zh" / "quantized" / "base-zh").string();
        else if (language == "ko") {
            model_to_load = (model_root / "ko" / "quantized" / "tiny-ko").string();
            moonshine_arch = mobmate::MoonshineBridge::ARCH_TINY;
        } else {
            model_to_load = (model_root / "en" / "quantized").string();
            moonshine_arch = mobmate::MoonshineBridge::ARCH_BASE_STREAMING;
        }
        if (!moonshine_bridge.load_model(model_to_load, moonshine_arch, mobmate::MoonshineBridge::LoadOptions{})) {
            std::cerr << "bench-realtime-route: failed to load Moonshine model: " << model_to_load << std::endl;
            std::exit(1);
        }
        std::cout << "bench-realtime-route: Moonshine engine loaded (model=" << model_to_load << ")" << std::endl;
    }

    /* Realtime pipeline constants (matching native_api.cpp) */
    static constexpr double RT_NORM_TARGET_DBFS = -7.0;
    static constexpr double RT_NORM_PEAK_CEILING_DBFS = -3.0;
    static constexpr double RT_NORM_MIN_RMS = 220.0;
    static constexpr double RT_NORM_GAIN_FLOOR = 1.03;
    static constexpr double PCM16_MAX = 32767.0;
    static constexpr double PCM16_MIN = -32768.0;
    static constexpr int PADDING_SAMPLES = 4800;

    auto compute_stats = [](const std::vector<int16_t>& samples) -> std::tuple<double, double, double> {
        if (samples.empty()) return {0.0, 0.0, 0.0};
        double sum_sq = 0.0;
        double peak = 0.0;
        for (int16_t s : samples) {
            double v = std::abs(static_cast<double>(s));
            peak = std::max(peak, v);
            sum_sq += static_cast<double>(s) * static_cast<double>(s);
        }
        double rms = std::sqrt(sum_sq / static_cast<double>(samples.size()));
        double duration_ms = static_cast<double>(samples.size()) * 1000.0 / 16000.0;
        return {rms, peak, duration_ms};
    };

    auto should_skip_input = [](double rms, double peak, double duration_ms, std::string& reason) -> bool {
        if (duration_ms < 240.0) { reason = "too_short"; return true; }
        if (peak < 250.0 && rms < 60.0) { reason = "no_speech"; return true; }
        return false;
    };

    auto should_accept_text = [](const std::string& text, double rms, double peak, double duration_ms, std::string& reason) -> bool {
        if (text.empty()) { reason = "empty_text"; return false; }
        if (duration_ms < 700.0 && peak < 500.0 && rms < 150.0) { reason = "weak_short_segment"; return false; }
        return true;
    };

    std::vector<mobmate::BenchResult> results;
    for (const auto& row : *rows) {
        mobmate::BenchResult result;
        result.index = row.index;
        result.wav_path = row.wav_path;
        result.expected = row.expected;
        result.latency_ms = -1;

        std::cout << "bench-realtime-route: processing index=" << row.index
                  << " wav=" << row.wav_path << std::endl;
        std::cout.flush();

        std::string wav_err;
        auto wav_meta = mobmate::read_wav_metadata(row.wav_path, wav_err);
        if (!wav_meta) {
            result.transcript = "";
            result.error = "WAV metadata failed: " + wav_err;
            result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, engine == "moonshine");
            result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, engine == "moonshine");
            results.push_back(result);
            continue;
        }

        auto samples = mobmate::read_wav_samples(row.wav_path, wav_err);
        if (!samples) {
            result.transcript = "";
            result.error = "WAV read failed: " + wav_err;
            result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, engine == "moonshine");
            result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, engine == "moonshine");
            results.push_back(result);
            continue;
        }

        /* Resample to 16kHz if needed */
        const std::vector<int16_t>* use_samples = &*samples;
        std::optional<std::vector<int16_t>> resampled_samples;
        if (wav_meta->sample_rate != 16000) {
            std::vector<uint8_t> pcm_bytes(samples->size() * 2);
            for (size_t i = 0; i < samples->size(); i++) {
                int16_t s = (*samples)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto resampled = mobmate::resample_audio(pcm_bytes.data(),
                                                      static_cast<int>(pcm_bytes.size()),
                                                      static_cast<int>(wav_meta->sample_rate),
                                                      16000);
            if (!resampled.empty()) {
                resampled_samples.emplace();
                for (size_t i = 0; i < resampled.size(); i += 2) {
                    int lo = resampled[i] & 0xFF;
                    int hi = resampled[i + 1];
                    resampled_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples = &*resampled_samples;
            }
        }

        /* Pipeline: prefilter -> normalize -> padding -> ASR -> gate */

        /* Step 1: Prefilter */
        const std::vector<int16_t>* use_after_prefilter = use_samples;
        std::optional<std::vector<int16_t>> prefilter_samples;
        if (prefilter_profile != "off") {
            std::vector<uint8_t> pcm_bytes(use_samples->size() * 2);
            for (size_t i = 0; i < use_samples->size(); i++) {
                int16_t s = (*use_samples)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            mobmate::AudioPrefilterState pstate;
            pstate.reset();
            bool use_strong = (prefilter_profile == "strong") ||
                (prefilter_profile == "auto" && (row.scene == "talk_noisy" || row.scene == "desktop_hearing"));
            auto filtered = use_strong
                ? mobmate::prefilter_process_strong(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &pstate)
                : mobmate::prefilter_process(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &pstate);
            if (!filtered.empty()) {
                prefilter_samples.emplace();
                for (size_t i = 0; i < filtered.size(); i += 2) {
                    int lo = filtered[i] & 0xFF;
                    int hi = filtered[i + 1];
                    prefilter_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_after_prefilter = &*prefilter_samples;
            }
        }

        /* Step 2: Normalize (matching native_api.cpp normalize_final_chunk) */
        const std::vector<int16_t>* use_after_normalize = use_after_prefilter;
        std::optional<std::vector<int16_t>> normalize_samples;
        {
            const auto& src = *use_after_prefilter;
            if (src.size() >= 2) {
                double sum_sq = 0.0;
                double peak = 0.0;
                for (int16_t s : src) {
                    sum_sq += static_cast<double>(s) * static_cast<double>(s);
                    double abs_val = s < 0 ? -static_cast<double>(s) : static_cast<double>(s);
                    if (abs_val > peak) peak = abs_val;
                }
                double rms = std::sqrt(sum_sq / static_cast<double>(src.size()));
                if (rms >= RT_NORM_MIN_RMS && peak > 0) {
                    double target_linear = std::pow(10.0, RT_NORM_TARGET_DBFS / 20.0) * PCM16_MAX;
                    double ceiling_linear = std::pow(10.0, RT_NORM_PEAK_CEILING_DBFS / 20.0) * PCM16_MAX;
                    double gain = target_linear / (rms > 1.0 ? rms : 1.0);
                    gain = std::min<double>(gain, ceiling_linear / (peak > 0 ? peak : 1.0));
                    if (std::isfinite(gain) && gain >= RT_NORM_GAIN_FLOOR) {
                        normalize_samples.emplace(src.size());
                        for (size_t i = 0; i < src.size(); ++i) {
                            double v = static_cast<double>(src[i]) * gain;
                            v = std::round(v);
                            if (v > PCM16_MAX) v = PCM16_MAX;
                            if (v < PCM16_MIN) v = PCM16_MIN;
                            (*normalize_samples)[i] = static_cast<int16_t>(v);
                        }
                        use_after_normalize = &*normalize_samples;
                    }
                }
            }
        }

        /* Step 3: Padding (300ms leading + tail) */
        std::vector<int16_t> padded_samples;
        padded_samples.reserve(PADDING_SAMPLES + use_after_normalize->size() + PADDING_SAMPLES);
        padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);
        padded_samples.insert(padded_samples.end(), use_after_normalize->begin(), use_after_normalize->end());
        padded_samples.insert(padded_samples.end(), PADDING_SAMPLES, 0);

        /* Step 4: ASR */
        std::string transcript;
        {
            std::vector<float> float_samples(padded_samples.size());
            for (size_t i = 0; i < padded_samples.size(); ++i) {
                float_samples[i] = static_cast<float>(padded_samples[i]) / 32768.0f;
            }

            auto start = std::chrono::steady_clock::now();
            if (engine == "whisper") {
                mobmate::WhisperEngine::TranscribeOpts opts;
                opts.language = row.language.empty() ? language : row.language;
                opts.n_threads = n_threads;
                auto tr = whisper_engine->transcribe(float_samples.data(),
                                                      static_cast<int>(float_samples.size()), opts);
                if (tr) transcript = *tr;
                auto end = std::chrono::steady_clock::now();
                result.latency_ms = static_cast<int64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
           } else if (moonshine_multi_candidate) {
                // V1-style multi-input ensemble: generate 3 PCM variants, transcribe each, select best
                auto ms_start = std::chrono::steady_clock::now();
                struct MoonshineCandidateResult {
                    MoonshineCandidateType type;
                    std::string text;
                    int64_t latency_ms;
                    bool success;
                };
                std::vector<MoonshineCandidateResult> mc_results;
                // Use preset parameters for multi-candidate processing
                auto preset = get_moonshine_lang_preset(row.language.empty() ? language : row.language,
                                                         row.scene.empty() ? scene : row.scene);
                for (int t = 0; t <= 2; t++) {
                    MoonshineCandidateType type = static_cast<MoonshineCandidateType>(t);
auto mc_floats = process_moonshine_candidate(*use_samples, type,
                        true, prefilter_profile,
                        row.language.empty() ? language : row.language,
                        row.scene.empty() ? scene : row.scene,
                        true, preset.normalize_dbfs,
                        false, 1.0, // no slowdown for realtime route
                        false, 0.0); // no RMS norm
                    MoonshineCandidateResult mc_res;
                    mc_res.type = type;
                    mc_res.latency_ms = 0;
                    mc_res.success = false;
                    mc_res.text = "";
                    if (!mc_floats.empty()) {
                        auto ms_res = moonshine_bridge.transcribeAccurate(mc_floats.data(),
                                                                          static_cast<int>(mc_floats.size()),
                                                                          16000,
                                                                          preset.leading_padding_ms,
                                                                          preset.tail_padding_ms);
                        if (!ms_res.empty() && ms_res[0].success && !ms_res[0].text.empty()) {
                            mc_res.text = ms_res[0].text;
                            mc_res.success = true;
                        }
                    }
                    mc_results.push_back(mc_res);
                }
                // Cross-candidate scoring
                int n_candidates = static_cast<int>(mc_results.size());
                int best_idx = 0;
                int best_score = -2147483647;
                int best_support_count = 0;
                double best_support_strength = 0.0;
                for (int i = 0; i < n_candidates; i++) {
                    if (mc_results[i].text.empty()) continue;
                    if (isRepeatingSpam(mc_results[i].text)) continue;
                    int score = score_moonshine_candidate(mc_results[i].text);
                    int supportCount = 0;
                    double supportStrength = 0.0;
                    for (int j = 0; j < n_candidates; j++) {
                        if (i == j || mc_results[j].text.empty()) continue;
                        if (isRepeatingSpam(mc_results[j].text)) continue;
                        int dist = mobmate::levenshtein_distance(mc_results[i].text, mc_results[j].text);
                        int maxLen = std::max(static_cast<int>(mc_results[i].text.length()),
                                              static_cast<int>(mc_results[j].text.length()));
                        double similarity = maxLen > 0 ? 1.0 - (static_cast<double>(dist) / maxLen) : 1.0;
                        if (similarity >= 0.72) {
                            supportCount++;
                            supportStrength += (similarity > 0.82 ? similarity : 0.82);
                        }
                    }
                    score += supportCount * 14;
                    score += static_cast<int>(std::round(supportStrength * 8.0));
                    score += static_cast<int>(mc_results[i].type); // prefer processed > trimmed > raw
                    if (score > best_score) {
                        best_score = score;
                        best_idx = i;
                        best_support_count = supportCount;
                        best_support_strength = supportStrength;
                    }
                }
                if (mc_results[best_idx].success && !mc_results[best_idx].text.empty()) {
                    transcript = mc_results[best_idx].text;
                } else {
                    // Fallback: use first successful result
                    for (const auto& mc : mc_results) {
                        if (mc.success && !mc.text.empty()) {
                            transcript = mc.text;
                            break;
                        }
                    }
                }
                auto ms_end = std::chrono::steady_clock::now();
                result.latency_ms = static_cast<int64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(ms_end - ms_start).count());
            } else if (moonshine_multi_pass) {
                // V1 multi-pass: streaming with 3 passes + 45ms settle
                auto mp_preset = get_moonshine_lang_preset(row.language.empty() ? language : row.language,
                                                            row.scene.empty() ? scene : row.scene);
                auto ms_start_mp = std::chrono::steady_clock::now();
                auto ms_res = moonshine_bridge.transcribeMultiPass(float_samples.data(),
                                                                    static_cast<int>(float_samples.size()),
                                                                    16000,
                                                                    mp_preset.leading_padding_ms,
                                                                    mp_preset.tail_padding_ms,
                                                                    3, 45); // 3 passes, 45ms settle
                if (!ms_res.empty() && ms_res[0].success && !ms_res[0].text.empty()) {
                    transcript = ms_res[0].text;
                }
                auto ms_end_mp = std::chrono::steady_clock::now();
                result.latency_ms = static_cast<int64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(ms_end_mp - ms_start_mp).count());
            } else {
                auto ms_res = moonshine_bridge.transcribeAccurate(float_samples.data(),
                                                                    static_cast<int>(float_samples.size()),
                                                                    16000, 300, 300);
                if (!ms_res.empty() && ms_res[0].success && !ms_res[0].text.empty()) {
                    transcript = ms_res[0].text;
                }
                auto end = std::chrono::steady_clock::now();
                result.latency_ms = static_cast<int64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
            }
        }

        /* Step 5: Gate (if enabled) */
        if (gate_mode == "on") {
            auto [rms, peak, duration_ms] = compute_stats(*use_after_normalize);
            std::string gate_reason;

            if (should_skip_input(rms, peak, duration_ms, gate_reason)) {
                result.transcript = "";
                result.error = "gate_skip: " + gate_reason;
            } else {
                if (should_accept_text(transcript, rms, peak, duration_ms, gate_reason)) {
                    result.transcript = transcript;
                } else {
                    result.transcript = "";
                    result.error = "gate_drop: " + gate_reason;
                }
            }
        } else {
            result.transcript = transcript;
        }

        result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript,
                                                             row.language, row.aliases, engine == "moonshine");
        result.cer = mobmate::compute_metric_cer(row.expected, result.transcript,
                                                  row.language, row.aliases, engine == "moonshine");

        std::cout << "bench-realtime-route: index=" << row.index
                  << " exact=" << (result.is_exact_match ? "Y" : "N")
                  << " cer=" << result.cer
                  << " latency=" << result.latency_ms << "ms"
                  << " text=\"" << result.transcript << "\"" << std::endl;

        results.push_back(result);
    }

    std::ofstream ofs(out_path);
    if (!ofs) {
        std::cerr << "bench-realtime-route: cannot open output file: " << out_path << std::endl;
        std::exit(1);
    }
    for (const auto& r : results) {
        ofs << mobmate::bench_result_to_jsonl(r) << "\n";
    }
    ofs.close();

    auto summary = mobmate::compute_summary(results);
    std::cout << "bench-realtime-route: wrote " << results.size() << " results to " << out_path << std::endl;
    std::cout << "bench-realtime-route: summary:" << std::endl;
    std::cout << mobmate::summary_to_json(summary) << std::endl;
}


static MoonshineLangPreset get_moonshine_lang_preset(const std::string& language, const std::string& scene) {
    // V1 actual presets from v1_reference_moonshine_50_*.jsonl (2026-05-21)
    // All languages use slowdown=1.0 (no slowdown). V1 slow_down_ratio=1 for all.
    // normalize_dbfs varies by scene.
    // prefilter: V1 per-scene selection (off/normal/strong)
    // trim_abs_threshold/trim_pad_ms: V1 per-scene silence trim params
    // Format: {leading_pad, tail_pad, normalize_dbfs, without_streaming, slowdown_ratio,
    //          squash_abs_threshold, squash_min_ms, squash_keep_ms, passes, prefilter,
    //          trim_abs_threshold, trim_pad_ms, vad_window_sec, vad_lookbehind, vad_threshold, max_tokens}
    // Note: V1 presets all have squash params = 0 (disabled). Infrastructure is in place for future use.
    if (language == "ja") {
        if (scene == "talk_clean") return {300, 300, -22.8, false, 1.00, 0, 0, 0, 3, "off", 300, 120, 0.15, 20000, 0.30, 13.0};
        if (scene == "talk_noisy") return {320, 420, -22.8, true, 1.00, 0, 0, 0, 3, "off", 300, 160, 0.15, 26000, 0.34, 13.0};
        if (scene == "talk_low_voice") return {360, 480, -26.0, true, 1.00, 0, 0, 0, 3, "off", 90, 300, 0.12, 32000, 0.24, 13.0};
        if (scene == "short_command") return {420, 520, -22.8, true, 1.00, 0, 0, 0, 3, "off", 300, 220, 0.10, 32000, 0.26, 13.0};
        if (scene == "desktop_hearing") return {300, 450, -22.8, true, 1.00, 0, 0, 0, 3, "off", 300, 180, 0.18, 22000, 0.32, 13.0};
    }
   if (language == "en") {
        if (scene == "talk_clean") return {300, 300, -22.8, false, 1.00, 0, 0, 0, 2, "normal", 300, 120, 0.15, 20000, 0.30, 6.5};
        if (scene == "talk_noisy") return {320, 420, -22.8, true, 1.00, 0, 0, 0, 3, "strong", 300, 160, 0.15, 26000, 0.34, 6.5};
        if (scene == "talk_low_voice") return {360, 480, -26.0, true, 1.00, 0, 0, 0, 3, "normal", 300, 180, 0.12, 32000, 0.24, 6.5};
        if (scene == "short_command") return {420, 520, -23.4, true, 1.00, 0, 0, 0, 3, "normal", 180, 420, 0.10, 32000, 0.26, 6.5};
        if (scene == "desktop_hearing") return {300, 450, -22.8, true, 1.00, 0, 0, 0, 3, "strong", 300, 180, 0.18, 22000, 0.32, 6.5};
    }
    if (language == "zh") {
        if (scene == "talk_clean") return {420, 620, -23.4, true, 1.00, 0, 0, 0, 4, "normal", 180, 420, 0.16, 24000, 0.27, 16.0};
        if (scene == "talk_noisy") return {620, 900, -22.8, true, 1.00, 0, 0, 0, 4, "normal", 300, 260, 0.16, 30000, 0.31, 16.0};
        if (scene == "talk_low_voice") return {520, 820, -26.0, true, 1.00, 0, 0, 0, 4, "normal", 90, 300, 0.14, 34000, 0.23, 16.0};
        if (scene == "short_command") return {460, 760, -23.4, true, 1.00, 0, 0, 0, 4, "normal", 180, 420, 0.12, 36000, 0.24, 16.0};
        if (scene == "desktop_hearing") return {700, 1100, -22.8, true, 1.00, 0, 0, 0, 4, "off", 300, 340, 0.18, 28000, 0.29, 16.0};
    }
  if (language == "ko") {
        if (scene == "talk_clean") return {480, 760, -22.8, true, 1.00, 0, 0, 0, 4, "normal", 300, 240, 0.16, 26000, 0.27, 13.0};
        if (scene == "talk_noisy") return {500, 840, -22.8, true, 1.00, 0, 0, 0, 4, "normal", 300, 280, 0.16, 32000, 0.31, 13.0};
        if (scene == "talk_low_voice") return {620, 960, -26.0, true, 1.00, 0, 0, 0, 4, "normal", 90, 320, 0.14, 36000, 0.23, 13.0};
        if (scene == "short_command") return {700, 980, -23.4, true, 1.00, 0, 0, 0, 4, "off", 180, 420, 0.12, 36000, 0.23, 13.0};
        if (scene == "desktop_hearing") return {520, 840, -22.8, true, 1.00, 0, 0, 0, 4, "normal", 300, 280, 0.18, 30000, 0.28, 13.0};
    }
    // Fallback
    return {300, 300, -22.8, false, 1.00, 0, 0, 0, 1, "normal", 300, 200, 0.15, 20000, 0.30, 6.5};
}

// Minimal JSON string escaper for candidate text output
static std::string json_escape_str(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 16);
    for (char c : s) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", (unsigned char)c);
                    out += buf;
                } else {
                    out += c;
                }
        }
    }
    return out;
}

 // V1-style helper: detect Japanese script (CJK + kana)
static bool containsJapaneseScript(const std::string& text) {
    const unsigned char* p = reinterpret_cast<const unsigned char*>(text.data());
    size_t len = text.size();
    for (size_t i = 0; i < len; ) {
        unsigned int cp = 0;
        size_t trail = 0;
        unsigned char c = p[i];
        if (c < 0x80) { cp = c; trail = 0; }
        else if (c < 0xE0) { cp = c & 0x1F; trail = 1; }
        else if (c < 0xF0) { cp = c & 0x0F; trail = 2; }
        else { cp = c & 0x07; trail = 3; }
        for (size_t t = 1; t <= trail && i + t < len; t++) {
            cp = (cp << 6) | (p[i + t] & 0x3F);
        }
        // Hiragana U+3040-U+309F, Katakana U+30A0-U+30FF, CJK U+4E00-U+9FFF
        if ((cp >= 0x3040 && cp <= 0x30FF) || (cp >= 0x4E00 && cp <= 0x9FFF)) {
            return true;
        }
        i += (trail > 0 ? trail + 1 : 1);
    }
    return false;
}

// Count code points in UTF-8 string
static int countCodePoints(const std::string& text) {
    int count = 0;
    const unsigned char* p = reinterpret_cast<const unsigned char*>(text.data());
    size_t len = text.size();
    for (size_t i = 0; i < len; ) {
        unsigned char c = p[i];
        if (c < 0x80) { i += 1; }
        else if (c < 0xE0) { i += 2; }
        else if (c < 0xF0) { i += 3; }
        else { i += 4; }
        count++;
    }
    return count;
}

// Check if text ends with sentence punctuation
static bool endsWithSentencePunctuation(const std::string& text) {
    if (text.empty()) return false;
    const unsigned char* p = reinterpret_cast<const unsigned char*>(text.data());
    size_t len = text.size();
    // Find last non-space char
    size_t i = len;
    while (i > 0 && (p[i-1] == ' ' || p[i-1] == '\t' || p[i-1] == '\n' || p[i-1] == '\r')) i--;
    if (i == 0) return false;
    unsigned char last = p[i-1];
    // ASCII punctuation
    if (last == '.' || last == '!' || last == '?') return true;
    // Japanese full-width punctuation: 。\xE3\x80\x82, ！\xEF\xBC\x81, ？\xEF\xBC\x8F
    if (i >= 3) {
        if (p[i-3] == 0xE3 && p[i-2] == 0x80 && p[i-1] == 0x82) return true; // 。
        if (p[i-3] == 0xEF && p[i-2] == 0xBC && p[i-1] == 0x81) return true; // ！
        if (p[i-3] == 0xEF && p[i-2] == 0xBC && p[i-1] == 0x8F) return true; // ？
    }
    return false;
}

// V1-style base score for a single Moonshine candidate
static int score_moonshine_candidate(const std::string& text) {
    if (text.empty()) return -2147483647; // Integer.MIN_VALUE
    int cp = countCodePoints(text);
    int score = cp * 10;
    if (cp <= 4) score -= 18;
    if (cp <= 2) score -= 30;
    if (endsWithSentencePunctuation(text)) score += 8;
    if (containsJapaneseScript(text)) score += 4;
    // Replacement char or "??": penalty
    if (text.find('\xEF\xBF\xBD') != std::string::npos || text.find("??") != std::string::npos) {
        score -= 20;
    }
    // Short English penalty: no space, not Japanese, cp <= 5
    if (text.find(' ') == std::string::npos && !containsJapaneseScript(text) && cp <= 5) {
        score -= 12;
    }
    return score;
}

// V1-style spam detection: check for repeating patterns
static bool isRepeatingSpam(const std::string& text) {
    if (text.length() < 20) return false;
    // Simple check: look for repeated 7-20 char patterns
    for (int patLen = 7; patLen <= 20; patLen++) {
        for (size_t start = 0; start + (size_t)patLen * 2 <= text.length(); start++) {
            std::string pat = text.substr(start, patLen);
            if (text.find(pat, start + patLen) != std::string::npos) {
                return true;
            }
        }
    }
    return false;
}

// Check if text ends with another string (suffix check)
static bool endsWith(const std::string& str, const std::string& suffix) {
    if (suffix.size() > str.size()) return false;
    return str.compare(str.size() - suffix.size(), suffix.size(), suffix) == 0;
}

 // Process raw int16 samples into float samples for a given Moonshine candidate type.
// Returns empty vector on failure.
static std::vector<float> process_moonshine_candidate(
        const std::vector<int16_t>& samples_raw,
        MoonshineCandidateType type,
        bool do_prefilter, const std::string& prefilter_profile,
        const std::string& language, const std::string& scene,
        bool do_normalize, double normalize_dbfs,
        bool do_slowdown, double slowdown_ratio,
        bool do_rms_norm, double rms_target)
{
    const std::vector<int16_t>* current = &samples_raw;
    std::optional<std::vector<int16_t>> work_samples;

    // --- Silence trimming (TRIMMED and PROCESSED) ---
    // V1 uses threshold=220, pad=120 for Moonshine one-shot candidates
    if (type != MoonshineCandidateType::RAW) {
        std::vector<uint8_t> pcm_bytes(samples_raw.size() * 2);
        for (size_t i = 0; i < samples_raw.size(); i++) {
            int16_t s = samples_raw[i];
            pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
            pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
        }
        auto trimmed = mobmate::trim_silence(pcm_bytes.data(),
            static_cast<int>(pcm_bytes.size()), 16000, 220, 120);
        if (!trimmed.empty()) {
            work_samples.emplace();
            for (size_t i = 0; i < trimmed.size(); i += 2) {
                int lo = trimmed[i] & 0xFF;
                int hi = trimmed[i + 1];
                work_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
            }
            current = &*work_samples;
        }
    }

    // --- Squash internal silence (PROCESSED only, when preset has squash params) ---
    // Note: current presets have squash disabled (threshold=0), so this is a no-op for now

    // --- High-pass filter: disabled for multi-candidate (no --highpass-hz in multi-candidate mode) ---

    // --- Prefilter (PROCESSED only) ---
    if (type == MoonshineCandidateType::PROCESSED && do_prefilter && prefilter_profile != "off") {
        std::vector<uint8_t> pcm_bytes(current->size() * 2);
        for (size_t i = 0; i < current->size(); i++) {
            int16_t s = (*current)[i];
            pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
            pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
        }
        mobmate::AudioPrefilterState prefilterState;
        prefilterState.reset();
        bool use_strong = (prefilter_profile == "strong") ||
            (prefilter_profile == "auto" && (scene == "talk_noisy" || scene == "desktop_hearing"));
        auto filtered = use_strong
            ? mobmate::prefilter_process_strong(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &prefilterState)
            : mobmate::prefilter_process(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &prefilterState);
        if (!filtered.empty()) {
            work_samples.emplace();
            for (size_t i = 0; i < filtered.size(); i += 2) {
                int lo = filtered[i] & 0xFF;
                int hi = filtered[i + 1];
                work_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
            }
            current = &*work_samples;
        }
    }

    // --- Normalize (PROCESSED only) ---
    if (type == MoonshineCandidateType::PROCESSED && do_normalize) {
        std::vector<uint8_t> pcm_bytes(current->size() * 2);
        for (size_t i = 0; i < current->size(); i++) {
            int16_t s = (*current)[i];
            pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
            pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
        }
        auto normalized = mobmate::normalize_final_chunk_for_asr(
            pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), normalize_dbfs);
        if (!normalized.empty() && normalized.size() == pcm_bytes.size()) {
            work_samples.emplace();
            for (size_t i = 0; i < normalized.size(); i += 2) {
                int lo = normalized[i] & 0xFF;
                int hi = normalized[i + 1];
                work_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
            }
            current = &*work_samples;
        }
    }

    // --- Slowdown (PROCESSED only) ---
    if (type == MoonshineCandidateType::PROCESSED && do_slowdown && slowdown_ratio > 1.01) {
        std::vector<uint8_t> pcm_bytes(current->size() * 2);
        for (size_t i = 0; i < current->size(); i++) {
            int16_t s = (*current)[i];
            pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
            pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
        }
        auto slowed = mobmate::slowdown_audio(pcm_bytes.data(),
            static_cast<int>(pcm_bytes.size()), 16000, slowdown_ratio);
        if (!slowed.empty()) {
            work_samples.emplace();
            for (size_t i = 0; i < slowed.size(); i += 2) {
                int lo = slowed[i] & 0xFF;
                int hi = slowed[i + 1];
                work_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
            }
            current = &*work_samples;
        }
    }

    // --- Convert int16 to float32 ---
    std::vector<float> float_samples(current->size());
    for (size_t i = 0; i < current->size(); ++i) {
        float_samples[i] = static_cast<float>((*current)[i]) / 32768.0f;
    }

    // --- RMS normalize ---
    if (do_rms_norm) {
        double rms = 0.0;
        for (float s : float_samples) rms += s * s;
        if (!float_samples.empty()) rms = std::sqrt(rms / float_samples.size());
        if (rms > 1e-6f && rms_target > 0.0) {
            double gain = rms_target / rms;
            for (float& s : float_samples) s *= static_cast<float>(gain);
        }
    }

    return float_samples;
}

static void cmd_bench_moonshine(const std::vector<std::string>& args) {
    std::string manifest_path;
    std::string out_path;
    std::string model_path;
    std::string dll_dir;
    int arch = 0; // ARCH_TINY default
    bool use_rms_norm = false;
    double rms_target = 0.1; // default -20 dBFS (Phase 5A baseline)
    std::string slowdown_lang; // empty = disabled; e.g. "zh"
    double slowdown_factor = 1.06;
    double highpass_hz = 0.0; // 0 = disabled; >0 = apply high-pass filter at this cutoff
    bool use_prefilter = true; // V1-style prefilter (hum notch -> HPF -> pre-emphasis -> LPF -> AGC -> soft limit) default enabled
    std::string prefilter_profile = "auto";
    bool use_trim = false; // V1-style silence trimming
    int trim_abs_threshold = 260; // matching V1 ja_talk_clean default
    int trim_pad_ms = 120; // matching V1 ja_talk_clean default
    int leading_padding_ms = 300; // V1-style leading padding for Moonshine
    int tail_padding_ms = 300; // V1-style tail padding for Moonshine
    std::string padding_lang;
    int padding_lang_leading_ms = 300;
    int padding_lang_tail_ms = 300;
    bool use_normalize_dbfs = false;
    double normalize_dbfs = -22.8; // default matching V1 preset normalizeDbfs
    bool without_streaming = false; // force oneshot API even for streaming models
    bool use_per_lang_preset = false; // use V1-matching per-language preset parameters
    bool use_moonshine_policy = false; // auto language-specific policy: ZH slowdown 1.06, KO pad 900/1200
    bool use_moonshine_dynamic_tuning = false; // V1-style dynamic tuning: auto-select trim/norm/slowdown params
    bool use_moonshine_multi_candidate = false; // V1-style multi-candidate: generate raw/trimmed/processed, select best
    // Phase 5D: debug-only processed-audio dump
    std::string dump_processed_audio_dir; // if non-empty, dump pre-ASR float32 as int16 WAV
    std::vector<int> dump_rows;           // comma-separated row indices to dump (empty = all)

    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "--manifest" && i + 1 < args.size()) { manifest_path = args[++i]; }
        else if (args[i] == "--out" && i + 1 < args.size()) { out_path = args[++i]; }
        else if (args[i] == "--model" && i + 1 < args.size()) { model_path = args[++i]; }
        else if (args[i] == "--moonshine-dll-dir" && i + 1 < args.size()) { dll_dir = args[++i]; }
        else if (args[i] == "--arch" && i + 1 < args.size()) { arch = std::stoi(args[++i]); }
        else if (args[i] == "--rms-norm") { use_rms_norm = true; }
        else if (args[i] == "--rms-target" && i + 1 < args.size()) { rms_target = std::stod(args[++i]); }
        else if (args[i] == "--slowdown-lang" && i + 1 < args.size()) { slowdown_lang = args[++i]; }
        else if (args[i] == "--slowdown-factor" && i + 1 < args.size()) { slowdown_factor = std::stod(args[++i]); }
        else if (args[i] == "--highpass-hz" && i + 1 < args.size()) { highpass_hz = std::stod(args[++i]); }
      else if (args[i] == "--no-prefilter") { use_prefilter = false; }
        else if (args[i] == "--prefilter-profile" && i + 1 < args.size()) { prefilter_profile = args[++i]; }
        else if (args[i] == "--trim") { use_trim = true; }
        else if (args[i] == "--trim-threshold" && i + 1 < args.size()) { trim_abs_threshold = std::stoi(args[++i]); }
        else if (args[i] == "--trim-pad-ms" && i + 1 < args.size()) { trim_pad_ms = std::stoi(args[++i]); }
        else if (args[i] == "--leading-padding-ms" && i + 1 < args.size()) { leading_padding_ms = std::stoi(args[++i]); }
        else if (args[i] == "--tail-padding-ms" && i + 1 < args.size()) { tail_padding_ms = std::stoi(args[++i]); }
        else if (args[i] == "--padding-lang" && i + 1 < args.size()) { padding_lang = args[++i]; }
        else if (args[i] == "--padding-lang-leading-ms" && i + 1 < args.size()) { padding_lang_leading_ms = std::stoi(args[++i]); }
        else if (args[i] == "--padding-lang-tail-ms" && i + 1 < args.size()) { padding_lang_tail_ms = std::stoi(args[++i]); }
        else if (args[i] == "--normalize-dbfs") { use_normalize_dbfs = true; }
        else if (args[i] == "--normalize-dbfs-value" && i + 1 < args.size()) { normalize_dbfs = std::stod(args[++i]); use_normalize_dbfs = true; }
        else if (args[i] == "--without-streaming") { without_streaming = true; }
        else if (args[i] == "--per-lang-preset") { use_per_lang_preset = true; }
        else if (args[i] == "--moonshine-policy") { use_moonshine_policy = true; }
        else if (args[i] == "--moonshine-dynamic-tuning") { use_moonshine_dynamic_tuning = true; }
        else if (args[i] == "--moonshine-multi-candidate") { use_moonshine_multi_candidate = true; }
        // Phase 5D: processed-audio dump flags
        else if (args[i] == "--dump-processed-audio-dir" && i + 1 < args.size()) { dump_processed_audio_dir = args[++i]; }
        else if (args[i] == "--dump-rows" && i + 1 < args.size()) {
            // Parse comma-separated row indices
            std::string rows_str = args[++i];
            std::istringstream rss(rows_str);
            std::string token;
            while (std::getline(rss, token, ',')) {
                if (!token.empty()) dump_rows.push_back(std::stoi(token));
            }
        }
    }

    if (manifest_path.empty() || out_path.empty() || model_path.empty() || dll_dir.empty()) {
        std::cerr << "bench-moonshine: --manifest, --out, --model, --moonshine-dll-dir are required" << std::endl;
        std::exit(1);
    }
    if (prefilter_profile != "auto" && prefilter_profile != "normal" && prefilter_profile != "strong") {
        std::cerr << "bench-moonshine: --prefilter-profile must be auto, normal, or strong" << std::endl;
        std::exit(1);
    }

    // Read manifest
    std::string err_msg;
    auto rows = mobmate::read_manifest(manifest_path, err_msg);
    if (!rows) {
        std::cerr << "bench-moonshine: failed to read manifest: " << err_msg << std::endl;
        std::exit(1);
    }

    std::cout << "bench-moonshine: loaded " << rows->size() << " rows from " << manifest_path << std::endl;

    // Phase 5D: dump helpers
    auto should_dump_row = [&](int row_idx) -> bool {
        if (dump_processed_audio_dir.empty()) return false;
        if (dump_rows.empty()) return true; // no filter = dump all
        for (int r : dump_rows) {
            if (r == row_idx) return true;
        }
        return false;
    };
    auto dump_float_as_wav = [&](const float* samples, int num_samples,
                                  const std::string& dir, const std::string& filename) {
        std::string full_path = dir + "/" + filename;
        if (mobmate::write_wav_float32(full_path, samples, num_samples, 16000)) {
            std::cout << "bench-moonshine: dumped processed audio: " << full_path
                      << " (" << num_samples << " samples, "
                      << (num_samples / 16000.0 * 1000.0) << " ms)" << std::endl;
        } else {
            std::cerr << "bench-moonshine: failed to dump processed audio: " << full_path << std::endl;
        }
    };

    auto resolve_moonshine_model = [](const std::string& root_or_model,
                                      const std::string& language,
                                      int default_arch) -> std::pair<std::string, int> {
        namespace fs = std::filesystem;
        fs::path root(root_or_model);
        if (!fs::exists(root / "en") && !fs::exists(root / "ja") &&
            !fs::exists(root / "zh") && !fs::exists(root / "ko")) {
            return {root_or_model, default_arch};
        }
        if (language == "ja") return {(root / "ja" / "quantized" / "base-ja").string(), mobmate::MoonshineBridge::ARCH_BASE};
        if (language == "zh") return {(root / "zh" / "quantized" / "base-zh").string(), mobmate::MoonshineBridge::ARCH_BASE};
        if (language == "ko") return {(root / "ko" / "quantized" / "tiny-ko").string(), mobmate::MoonshineBridge::ARCH_TINY};
        return {(root / "en" / "quantized").string(), mobmate::MoonshineBridge::ARCH_BASE_STREAMING};
    };

    // Load Moonshine engine
    mobmate::MoonshineBridge bridge;
    if (!bridge.init(dll_dir)) {
        std::cerr << "bench-moonshine: failed to load moonshine.dll" << std::endl;
        std::exit(1);
    }

    std::string loaded_model_path;
    int loaded_arch = -1;
    std::string loaded_options_key;

    // Run benchmark
    std::vector<mobmate::BenchResult> results;
    for (const auto& row : *rows) {
        mobmate::BenchResult result;
        result.index = row.index;
        result.wav_path = row.wav_path;
        result.expected = row.expected;
        auto score_moonshine_result = [&]() {
            result.is_exact_match = mobmate::exact_match_metric(row.expected, result.transcript, row.language, row.aliases, true);
            result.cer = mobmate::compute_metric_cer(row.expected, result.transcript, row.language, row.aliases, true);
        };

       std::cout << "bench-moonshine: processing index=" << row.index
                   << " wav=" << row.wav_path << std::endl;
        std::cout.flush();

        // Per-language+scene preset parameters (V1 matching)
        MoonshineLangPreset preset = get_moonshine_lang_preset(row.language, row.scene);
        int item_leading_pad = use_per_lang_preset ? preset.leading_padding_ms : leading_padding_ms;
        int item_tail_pad = use_per_lang_preset ? preset.tail_padding_ms : tail_padding_ms;
        if (!padding_lang.empty() && row.language == padding_lang) {
            item_leading_pad = padding_lang_leading_ms;
            item_tail_pad = padding_lang_tail_ms;
        }
        bool item_without_streaming = use_per_lang_preset ? preset.without_streaming : without_streaming;
        double item_normalize_dbfs = use_per_lang_preset ? preset.normalize_dbfs : normalize_dbfs;
        double item_slowdown_ratio = use_per_lang_preset ? preset.slowdown_ratio : 1.0;
        bool item_use_normalize = use_per_lang_preset || use_normalize_dbfs;

        // Moonshine auto policy: ZH slowdown 1.06, KO padding 900/1200
        // Applied last so it overrides preset/CLI flags when --moonshine-policy is set.
        if (use_moonshine_policy) {
            if (row.language == "zh") {
                item_slowdown_ratio = 1.06;
            } else if (row.language == "ko") {
                item_leading_pad = 900;
                item_tail_pad = 1200;
            }
        }

        auto model_choice = resolve_moonshine_model(model_path, row.language, arch);
        mobmate::MoonshineBridge::LoadOptions load_options;
        if (use_per_lang_preset) {
            load_options.vad_window_duration = preset.vad_window_sec;
            load_options.vad_look_behind_sample_count = preset.vad_look_behind_samples;
            load_options.vad_threshold = preset.vad_threshold;
            load_options.max_tokens_per_second = preset.max_tokens_per_second;
        }
        std::string options_key = std::to_string(load_options.vad_window_duration)
            + ":" + std::to_string(load_options.vad_look_behind_sample_count)
            + ":" + std::to_string(load_options.vad_threshold)
            + ":" + std::to_string(load_options.max_tokens_per_second);
        if (model_choice.first != loaded_model_path || model_choice.second != loaded_arch || options_key != loaded_options_key) {
            if (!bridge.load_model(model_choice.first, model_choice.second, load_options)) {
                result.transcript = "";
                score_moonshine_result();
                result.latency_ms = -1;
                result.error = "Moonshine model load failed: " + model_choice.first;
                results.push_back(result);
                continue;
            }
            loaded_model_path = model_choice.first;
            loaded_arch = model_choice.second;
            loaded_options_key = options_key;
            std::cout << "bench-moonshine: Moonshine engine loaded (lang=" << row.language
                      << ", model=" << loaded_model_path << ", arch=" << loaded_arch
                      << ", max_tokens=" << load_options.max_tokens_per_second << ")" << std::endl;
        }

        // Read WAV metadata
        std::string wav_err;
        std::cout << "bench-moonshine: reading WAV metadata..." << std::endl;
        std::cout.flush();
        auto wav_meta = mobmate::read_wav_metadata(row.wav_path, wav_err);
        std::cout << "bench-moonshine: WAV metadata read done, result=" << (wav_meta ? "OK" : "FAIL") << std::endl;
        std::cout.flush();
        if (!wav_meta) {
            result.transcript = "";
            score_moonshine_result();
            result.latency_ms = -1;
            result.error = "WAV metadata failed: " + wav_err;
            results.push_back(result);
            continue;
        }

        // Read WAV samples
        std::cout << "bench-moonshine: reading WAV samples..." << std::endl;
        std::cout.flush();
        auto samples = mobmate::read_wav_samples(row.wav_path, wav_err);
        std::cout << "bench-moonshine: WAV samples read done, count=" << (samples ? samples->size() : 0) << std::endl;
        std::cout.flush();
        if (!samples) {
            result.transcript = "";
            score_moonshine_result();
            result.latency_ms = -1;
            result.error = "WAV read failed: " + wav_err;
            results.push_back(result);
            continue;
        }

        // Resample to 16kHz if needed (Moonshine requires 16kHz)
        const std::vector<int16_t>* use_samples_raw = &*samples;
        std::optional<std::vector<int16_t>> resampled_samples;
        if (wav_meta->sample_rate != 16000) {
            std::vector<uint8_t> pcm_bytes(samples->size() * 2);
            for (size_t i = 0; i < samples->size(); i++) {
                int16_t s = (*samples)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto resampled = mobmate::resample_audio(pcm_bytes.data(),
                                                      static_cast<int>(pcm_bytes.size()),
                                                      static_cast<int>(wav_meta->sample_rate),
                                                      16000);
            if (!resampled.empty()) {
                resampled_samples.emplace();
                for (size_t i = 0; i < resampled.size(); i += 2) {
                    int lo = resampled[i] & 0xFF;
                    int hi = resampled[i + 1];
                    resampled_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_raw = &*resampled_samples;
            }
        }

        // === Multi-candidate mode: generate raw/trimmed/processed, transcribe each, select best ===
        if (use_moonshine_multi_candidate) {
            // Compute effective slowdown for PROCESSED candidate (same logic as single-candidate path)
            double mc_effective_slowdown = item_slowdown_ratio;
            bool mc_use_slowdown = mc_effective_slowdown > 1.01;
            if (use_moonshine_policy && row.language == "zh") {
                mc_effective_slowdown = item_slowdown_ratio; // 1.06 set by policy
                mc_use_slowdown = mc_effective_slowdown > 1.01;
            }

            struct MoonshineCandidateResult {
                MoonshineCandidateType type;
                std::string text;
                int64_t latency_ms;
                bool success;
            };
            std::vector<MoonshineCandidateResult> mc_results;

            // Generate and transcribe each candidate
            for (int t = 0; t <= 2; t++) {
                MoonshineCandidateType type = static_cast<MoonshineCandidateType>(t);
                const char* typeNames[] = {"raw", "trimmed", "processed"};
                // Use preset prefilter for multi-candidate path too
                std::string mc_prefilter = use_per_lang_preset ? preset.prefilter : prefilter_profile;

                auto mc_floats = process_moonshine_candidate(*use_samples_raw, type,
                    use_prefilter, mc_prefilter,
                    row.language, row.scene,
                    item_use_normalize, item_normalize_dbfs,
                    mc_use_slowdown, mc_effective_slowdown,
                    use_rms_norm, rms_target);

                MoonshineCandidateResult mc_res;
                mc_res.type = type;
                mc_res.latency_ms = 0;
                mc_res.success = false;

                if (!mc_floats.empty()) {
                    // Phase 5D: dump pre-ASR processed audio (multi-candidate path)
                    if (should_dump_row(row.index)) {
                        namespace fs = std::filesystem;
                        fs::path mc_dump_dir(dump_processed_audio_dir);
                        if (!fs::exists(mc_dump_dir)) fs::create_directories(mc_dump_dir);
                        const char* typeNames[] = {"raw", "trimmed", "processed"};
                        std::string mc_dump_name = "row" + std::to_string(row.index)
                            + "_" + row.language + "_" + row.scene
                            + "_" + typeNames[t] + ".wav";
                        dump_float_as_wav(mc_floats.data(), static_cast<int>(mc_floats.size()),
                                          dump_processed_audio_dir, mc_dump_name);
                    }

                    auto start = std::chrono::steady_clock::now();
                    auto ms_res = bridge.transcribe(mc_floats.data(),
                        static_cast<int>(mc_floats.size()),
                        16000,
                        item_leading_pad,
                        item_tail_pad,
                        item_without_streaming);
                    auto end = std::chrono::steady_clock::now();
                    mc_res.latency_ms = static_cast<int64_t>(
                        std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

                    if (!ms_res.empty() && ms_res[0].success) {
                        mc_res.text = ms_res[0].text;
                        mc_res.success = true;
                    } else {
                        mc_res.text = "";
                    }
                }
                mc_results.push_back(mc_res);
            }


           // V1-style cross-candidate similarity scoring with penalty terms
            int n_candidates = static_cast<int>(mc_results.size());
            int best_idx = 0;
            int best_score = -2147483647; // Integer.MIN_VALUE
            int second_best_score = -2147483647;
            int best_support_count = 0;
            double best_support_strength = 0.0;

            for (int i = 0; i < n_candidates; i++) {
                if (mc_results[i].text.empty()) continue;
                // V1: skip repeating-spam candidates (hallucination filter)
                if (isRepeatingSpam(mc_results[i].text)) continue;
                // V1-style base score from scoreMoonshineOneShotResult
                int score = score_moonshine_candidate(mc_results[i].text);
                int supportCount = 0;
                double supportStrength = 0.0;
                for (int j = 0; j < n_candidates; j++) {
                    if (i == j || mc_results[j].text.empty()) continue;
                    if (isRepeatingSpam(mc_results[j].text)) continue;
                    int dist = mobmate::levenshtein_distance(mc_results[i].text, mc_results[j].text);
          int maxLen = static_cast<int>(mc_results[i].text.length()) > static_cast<int>(mc_results[j].text.length())
                                           ? static_cast<int>(mc_results[i].text.length())
                                           : static_cast<int>(mc_results[j].text.length());
                    double similarity = maxLen > 0 ? 1.0 - (static_cast<double>(dist) / maxLen) : 1.0;
                    if (similarity >= 0.72) {
                        supportCount++;
                        supportStrength += (similarity > 0.82 ? similarity : 0.82);
                    } else if (similarity >= 0.58) {
                        supportStrength += similarity * 0.35;
                    }
                }
                score += supportCount * 18;
                score += static_cast<int>(std::round(supportStrength * 8.0));

                // V1 cross-candidate penalty terms (from decideMoonshineOneShotResult)
                int cp = countCodePoints(mc_results[i].text);
                bool isJapanese = containsJapaneseScript(mc_results[i].text);
                bool unsupportedOrWeakSupport = (supportCount == 0)
                    || (supportCount <= 1 && supportStrength < 1.20);

                // Penalty: 3+ candidates, zero support
                if (n_candidates >= 3 && supportCount == 0) score -= 24;
                // Penalty: 3+ candidates, zero support, Japanese, cp >= 22
                if (n_candidates >= 3 && supportCount == 0 && isJapanese && cp >= 22) {
                    int pen = 18 + ((cp - 21) * 7);
                    score -= (pen < 80 ? pen : 80);
                }
                // Penalty: 2+ candidates, zero support, non-Japanese, cp <= 18
                if (n_candidates >= 2 && supportCount == 0 && !isJapanese && cp <= 18) {
                    score -= 12;
                }
                // Penalty: unsupported/weak, long text (cp >= 14)
                if (unsupportedOrWeakSupport && cp >= 14) {
                    int pen = 10 + ((cp - 13) * 3);
                    score -= (pen < 64 ? pen : 64);
                }
                // Penalty: unsupported/weak, non-Japanese, cp >= 10
                if (!isJapanese && unsupportedOrWeakSupport && cp >= 10) {
                    int pen = 8 + ((cp - 9) * 2);
                    score -= (pen < 22 ? pen : 22);
                }
                // Penalty: replacement char or "??": -26
                if (mc_results[i].text.find('\xEF\xBF\xBD') != std::string::npos
                    || mc_results[i].text.find("??") != std::string::npos) {
                    score -= 26;
                }

                // Prefer processed > trimmed > raw on tie
                score += static_cast<int>(mc_results[i].type);
                if (score > best_score) {
                    second_best_score = best_score;
                    best_score = score;
                    best_idx = i;
                    best_support_count = supportCount;
                    best_support_strength = supportStrength;
                } else if (score > second_best_score) {
                    second_best_score = score;
                }
            }

            // V1: choosePrefixPreferredResult - prefer a longer candidate that has best as suffix
            {
                int bestCp = countCodePoints(mc_results[best_idx].text);
                if (bestCp >= 4 && !mc_results[best_idx].text.empty()
                    && !isRepeatingSpam(mc_results[best_idx].text)) {
                    int prefixIdx = -1;
                    int prefixScore = -2147483647;
                    for (int i = 0; i < n_candidates; i++) {
                        if (i == best_idx) continue;
                        if (mc_results[i].text.empty()) continue;
                        if (isRepeatingSpam(mc_results[i].text)) continue;
                        int candCp = countCodePoints(mc_results[i].text);
                        if (candCp <= bestCp) continue;
                        // Check if candidate ends with best text (best is suffix of candidate)
                        if (!endsWith(mc_results[i].text, mc_results[best_idx].text)) continue;
                        // Similarity check
                        int dist = mobmate::levenshtein_distance(mc_results[i].text, mc_results[best_idx].text);
                        int maxLen = static_cast<int>(mc_results[i].text.length()) > static_cast<int>(mc_results[best_idx].text.length())
                                                ? static_cast<int>(mc_results[i].text.length())
                                                : static_cast<int>(mc_results[best_idx].text.length());
                        double sim = maxLen > 0 ? 1.0 - (static_cast<double>(dist) / maxLen) : 1.0;
                        if (sim < 0.66) continue;
                        // Score gap check (V1: allowedGap = KO&&bestCp<=8?18:12)
                        int allowedGap = 12;
                        // Re-score this candidate
                        int candScore = score_moonshine_candidate(mc_results[i].text);
                        if (candScore < best_score - allowedGap) continue;
                        if (candScore > prefixScore) {
                            prefixScore = candScore;
                            prefixIdx = i;
                        }
                    }
                    if (prefixIdx >= 0) {
                        best_idx = prefixIdx;
                        best_score = prefixScore;
                    }
                }
            }

            // V1-style 2-pass retry: if pass1 result is uncertain, retry with accurate mode
            // V1: shouldRetryAccurate = (supportCount==0 || weakLowScoreAgreement || lowSupportWeakStrength)
            //    && (holdForNoAgreement || narrowMargin || lowConfidence || weakLowScoreAgreement || longLowSupport)
            bool should_retry_accurate = false;
            if (n_candidates >= 2) {
                bool low_support = best_support_count <= 1;
                bool low_support_weak_strength = low_support && best_support_strength < 1.35;
                bool narrow_margin = second_best_score > 0 && (best_score - second_best_score) <= 18;
                bool zero_support = best_support_count == 0;
                // Count long alternatives (>=8 chars)
                int conflicting_long_alternatives = 0;
                for (int i = 0; i < n_candidates; i++) {
                    if (!mc_results[i].text.empty() && mc_results[i].text.length() >= 8
                        && !isRepeatingSpam(mc_results[i].text)) {
                        conflicting_long_alternatives++;
                    }
                }
                bool weak_low_score_agreement = low_support && narrow_margin && best_score < 180 && conflicting_long_alternatives >= 2;
                int best_cp = mc_results[best_idx].text.length();
                bool long_low_support = low_support_weak_strength && best_cp >= 10;
                // holdForNoAgreement: non-Japanese, zero support, >=2 long alternatives (simplified, no lang check yet)
                bool hold_for_no_agreement = zero_support && conflicting_long_alternatives >= 2;
                // V2: confidence unavailable, so lowConfidence is skipped
                should_retry_accurate = (zero_support || weak_low_score_agreement || low_support_weak_strength)
                    && (hold_for_no_agreement || narrow_margin || weak_low_score_agreement || long_low_support);
            }

            // TODO: 2-pass retry shelved - accurate path degrades accuracy in V2 context
            // if (should_retry_accurate && !mc_results[best_idx].text.empty()) {
            if (false) {
                // Retry top 2 candidates with accurate mode (V1: without_streaming=true, longer tail)
                std::vector<int> retry_indices;
                retry_indices.push_back(best_idx);
                if (second_best_score > 0) {
                    for (int i = 0; i < n_candidates; i++) {
                        if (i == best_idx) continue;
                        // Find second best
                        int score = 10 + static_cast<int>(mc_results[i].type);
                        if (score == second_best_score) {
                            retry_indices.push_back(i);
                            break;
                        }
                    }
                }

                for (int ri : retry_indices) {
                    if (ri < 0 || ri >= n_candidates) continue;
                    // Get the float samples for this candidate
                    auto retry_floats = process_moonshine_candidate(*use_samples_raw, mc_results[ri].type,
                        use_prefilter, prefilter_profile,
                        row.language, row.scene,
                        item_use_normalize, item_normalize_dbfs,
                        mc_use_slowdown, mc_effective_slowdown,
                        use_rms_norm, rms_target);

                    if (retry_floats.empty()) continue;

                    auto start = std::chrono::steady_clock::now();
                    auto ms_res = bridge.transcribeAccurate(retry_floats.data(),
                        static_cast<int>(retry_floats.size()),
                        16000,
                        item_leading_pad,
                        (item_tail_pad > 450 ? item_tail_pad : 450)); // V1 accurate: tail>=450ms
                    auto end = std::chrono::steady_clock::now();
                    int64_t retry_latency = static_cast<int64_t>(
                        std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

                    if (!ms_res.empty() && ms_res[0].success && !ms_res[0].text.empty()) {
                        MoonshineCandidateResult retry_res;
                        retry_res.type = mc_results[ri].type; // same type
                        retry_res.latency_ms = retry_latency;
                        retry_res.success = true;
                        retry_res.text = ms_res[0].text;
                        mc_results.push_back(retry_res);
                        n_candidates++;
                    }
                }

                // Re-score with retry candidates
                best_idx = 0;
                best_score = -1;
                for (int i = 0; i < n_candidates; i++) {
                    if (mc_results[i].text.empty()) continue;
                    // V1: skip repeating-spam candidates
                    if (isRepeatingSpam(mc_results[i].text)) continue;
                    int score = 10;
                    int supportCount = 0;
                    double supportStrength = 0.0;
                    for (int j = 0; j < n_candidates; j++) {
                        if (i == j || mc_results[j].text.empty()) continue;
                        if (isRepeatingSpam(mc_results[j].text)) continue;
                        int dist = mobmate::levenshtein_distance(mc_results[i].text, mc_results[j].text);
                        int maxLen = static_cast<int>(mc_results[i].text.length()) > static_cast<int>(mc_results[j].text.length())
                                                 ? static_cast<int>(mc_results[i].text.length())
                                                 : static_cast<int>(mc_results[j].text.length());
                        double similarity = maxLen > 0 ? 1.0 - (static_cast<double>(dist) / maxLen) : 1.0;
                        if (similarity >= 0.72) {
                            supportCount++;
                            supportStrength += (similarity > 0.82 ? similarity : 0.82);
                        } else if (similarity >= 0.58) {
                            supportStrength += similarity * 0.35;
                        }
                    }
                    score += supportCount * 18;
                    score += static_cast<int>(std::round(supportStrength * 8.0));
                    score += static_cast<int>(mc_results[i].type);
                    if (score > best_score) {
                        best_score = score;
                        best_idx = i;
                    }
                }
            }

            result.transcript = mc_results[best_idx].text;
            result.latency_ms = 0;
            for (const auto& mr : mc_results) result.latency_ms += mr.latency_ms;

            score_moonshine_result();

            // Build candidates JSON array
            std::string candidates_json = "\"candidates\":[";
            for (int i = 0; i < static_cast<int>(mc_results.size()); i++) {
                if (i > 0) candidates_json += ",";
                const char* typeNames[] = {"raw", "trimmed", "processed"};
            candidates_json += "{\"type\":\"" + std::string(typeNames[static_cast<int>(mc_results[i].type)])
                     + "\",\"text\":\"" + json_escape_str(mc_results[i].text)
                     + "\",\"latency_ms\":" + std::to_string(mc_results[i].latency_ms)
                    + ",\"success\":" + (mc_results[i].success ? "true" : "false") + "}";
            }
            candidates_json += "]";

            const char* typeNames[] = {"raw", "trimmed", "processed"};
            result.extra_json_fields = "\"engine\":\"moonshine\",\"selected_engine\":\"moonshine\",\"decision_reason\":\"multi_candidate_select" + std::string(should_retry_accurate ? "+2pass_accurate" : "") + "\",";
            result.extra_json_fields += "\"selected_candidate\":\"" + std::string(typeNames[static_cast<int>(mc_results[best_idx].type)]) + "\",";
            result.extra_json_fields += candidates_json + ",";
            result.extra_json_fields += "\"preset\":{\"leading_padding_ms\":" + std::to_string(item_leading_pad)
                + ",\"tail_padding_ms\":" + std::to_string(item_tail_pad)
                + ",\"normalize_dbfs\":" + std::to_string(item_normalize_dbfs)
                + ",\"without_streaming\":" + (item_without_streaming ? "true" : "false")
                + ",\"slowdown_ratio\":" + std::to_string(mc_effective_slowdown) + "}";
            result.error = "";

            results.push_back(result);
            continue; // skip single-candidate pipeline
        }

        // V1-style dynamic tuning: auto-select trim/norm/slowdown params based on audio characteristics
        mobmate::FinalAsrTuning dynamicTuning;
        int dyn_trim_threshold = trim_abs_threshold;
        int dyn_trim_pad = trim_pad_ms;
        double dyn_normalize_dbfs = item_normalize_dbfs;
        double dyn_slowdown_ratio = item_slowdown_ratio;
        if (use_moonshine_dynamic_tuning || use_per_lang_preset) {
            std::vector<uint8_t> raw_pcm_bytes(use_samples_raw->size() * 2);
            for (size_t i = 0; i < use_samples_raw->size(); i++) {
                int16_t s = (*use_samples_raw)[i];
                raw_pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                raw_pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            dynamicTuning = mobmate::select_final_asr_tuning(
                raw_pcm_bytes.data(),
                static_cast<int>(raw_pcm_bytes.size()),
                16000);
            dyn_trim_threshold = dynamicTuning.trimAbsThreshold;
            dyn_trim_pad = dynamicTuning.trimPadMs;
            // Preserve preset normalize_dbfs when --per-lang-preset is active
            if (!use_per_lang_preset) {
                dyn_normalize_dbfs = dynamicTuning.normalizeDbfs;
            }
            // Only apply dynamic slowdown if not already overridden by policy/preset
            if (!use_moonshine_policy && !use_per_lang_preset) {
                dyn_slowdown_ratio = dynamicTuning.slowDownRatio;
            }
            std::cout << "bench-moonshine: dynamic tuning mode=" << dynamicTuning.id
                      << " trim=" << dyn_trim_threshold << "/" << dyn_trim_pad
                      << " norm=" << dyn_normalize_dbfs
                      << " slowdown=" << dyn_slowdown_ratio << std::endl;
        }

      // V1-style silence trimming (leading/trailing silence removal)
        // V1 bench path (mobMateFinalRoute): uses runtime tuning threshold + (preset.trimPadMs + 80) for pad
        // Adaptive lead pad is handled inside trim_silence()
        const std::vector<int16_t>* use_samples_after_trim = use_samples_raw;
        std::optional<std::vector<int16_t>> trim_samples;
        int effective_trim_threshold;
        int effective_trim_pad;
        if (use_per_lang_preset) {
            // Match V1: runtime tuning threshold + preset pad + 80ms (V1's speechPadMs + 80)
            effective_trim_threshold = dyn_trim_threshold; // from select_final_asr_tuning
            effective_trim_pad = std::max<int>(preset.trim_pad_ms + 80, dynamicTuning.trimPadMs);
        } else {
            effective_trim_threshold = dyn_trim_threshold;
            effective_trim_pad = dyn_trim_pad;
        }
        bool do_trim = use_trim || (use_per_lang_preset && effective_trim_threshold > 0);
        if (do_trim) {
            std::vector<uint8_t> pcm_bytes(use_samples_raw->size() * 2);
            for (size_t i = 0; i < use_samples_raw->size(); i++) {
                int16_t s = (*use_samples_raw)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
          auto trimmed = mobmate::trim_silence(pcm_bytes.data(),
                                                  static_cast<int>(pcm_bytes.size()),
                                                  16000,
                                                  effective_trim_threshold,
                                                  effective_trim_pad);
            if (!trimmed.empty()) {
                trim_samples.emplace();
                for (size_t i = 0; i < trimmed.size(); i += 2) {
                    int lo = trimmed[i] & 0xFF;
                    int hi = trimmed[i + 1];
                    trim_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_after_trim = &*trim_samples;
            }
        }

        // V1-style squash internal silence (compress long silent gaps within audio)
        // V1 pipeline: trim → squash → prefilter → normalize → slowdown → float → transcribe
        const std::vector<int16_t>* use_samples_after_squash = use_samples_after_trim;
        std::optional<std::vector<int16_t>> squash_samples;
        if (use_per_lang_preset && preset.squash_abs_threshold > 0 && preset.squash_min_ms > 0) {
            std::vector<uint8_t> pcm_bytes(use_samples_after_trim->size() * 2);
            for (size_t i = 0; i < use_samples_after_trim->size(); i++) {
                int16_t s = (*use_samples_after_trim)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto squashed = mobmate::squash_internal_silence(pcm_bytes.data(),
                                                              static_cast<int>(pcm_bytes.size()),
                                                              16000,
                                                              preset.squash_abs_threshold,
                                                              preset.squash_min_ms,
                                                              preset.squash_keep_ms);
            if (!squashed.empty()) {
                squash_samples.emplace();
                for (size_t i = 0; i < squashed.size(); i += 2) {
                    int lo = squashed[i] & 0xFF;
                    int hi = squashed[i + 1];
                    squash_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_after_squash = &*squash_samples;
            }
        }

        // Optionally apply high-pass filter
        const std::vector<int16_t>* use_samples_after_hp = use_samples_after_squash;
        std::optional<std::vector<int16_t>> hp_samples;
        if (highpass_hz > 0.0) {
            std::vector<uint8_t> pcm_bytes(use_samples_raw->size() * 2);
            for (size_t i = 0; i < use_samples_raw->size(); i++) {
                int16_t s = (*use_samples_raw)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto hp_filtered = mobmate::apply_highpass(pcm_bytes.data(),
                                                       static_cast<int>(pcm_bytes.size()),
                                                       16000,
                                                       highpass_hz);
            if (!hp_filtered.empty()) {
                hp_samples.emplace();
                for (size_t i = 0; i < hp_filtered.size(); i += 2) {
                    int lo = hp_filtered[i] & 0xFF;
                    int hi = hp_filtered[i + 1];
                    hp_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_after_hp = &*hp_samples;
            }
        }

      // V1-style prefilter (hum notch -> HPF -> pre-emphasis -> LPF -> AGC -> soft limit)
        // Use preset prefilter when --per-lang-preset is set, otherwise fall back to CLI --prefilter-profile
        const std::vector<int16_t>* use_samples_after_prefilter = use_samples_after_hp;
        std::optional<std::vector<int16_t>> prefilter_samples;
        std::string item_prefilter = use_per_lang_preset ? preset.prefilter : prefilter_profile;
        if (use_prefilter && item_prefilter != "off") {
            std::vector<uint8_t> pcm_bytes(use_samples_after_hp->size() * 2);
            for (size_t i = 0; i < use_samples_after_hp->size(); i++) {
                int16_t s = (*use_samples_after_hp)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            mobmate::AudioPrefilterState prefilterState;
            prefilterState.reset();
            // Resolve prefilter profile: preset overrides CLI auto logic
            bool use_strong = (item_prefilter == "strong") ||
                (item_prefilter == "auto" && (row.scene == "talk_noisy" || row.scene == "desktop_hearing"));
            if (use_strong) {
                std::cout << "bench-moonshine: applying STRONG prefilter for scene=" << row.scene << std::endl;
                std::cout.flush();
            } else if (item_prefilter == "normal") {
                std::cout << "bench-moonshine: applying NORMAL prefilter for scene=" << row.scene << std::endl;
                std::cout.flush();
            }
            auto filtered = use_strong
                ? mobmate::prefilter_process_strong(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &prefilterState)
                : mobmate::prefilter_process(pcm_bytes.data(), static_cast<int>(pcm_bytes.size()), 16000, &prefilterState);
            if (!filtered.empty()) {
                prefilter_samples.emplace();
                for (size_t i = 0; i < filtered.size(); i += 2) {
                    int lo = filtered[i] & 0xFF;
                    int hi = filtered[i + 1];
                    prefilter_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_after_prefilter = &*prefilter_samples;
            }
        }

       // V1-style normalize final chunk for ASR (RMS normalization to target dBFS)
        // V1 pipeline: trim → squash → prefilter → normalize → slowdown → float → transcribe
        // V1 bench path adds extraGainDb (default 20dB) capped at -7dBFS for final accuracy
        const std::vector<int16_t>* use_samples_after_normalize = use_samples_after_prefilter;
        std::optional<std::vector<int16_t>> normalize_samples;
        // Compute effective normalize target: V1 adds extra gain for per-lang-preset mode
        double effective_normalize_dbfs = dyn_normalize_dbfs;
        if (use_per_lang_preset) {
            // Match V1: targetDbfs + extraGainDb, capped at -7.0 dBFS
            double extraGainDb = 20.0;
            effective_normalize_dbfs = std::min<double>(-7.0, item_normalize_dbfs + extraGainDb);
        }
        if (item_use_normalize) {
            std::vector<uint8_t> pcm_bytes(use_samples_after_prefilter->size() * 2);
            for (size_t i = 0; i < use_samples_after_prefilter->size(); i++) {
                int16_t s = (*use_samples_after_prefilter)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto normalized = mobmate::normalize_final_chunk_for_asr(
                pcm_bytes.data(),
                static_cast<int>(pcm_bytes.size()),
                effective_normalize_dbfs);
            if (!normalized.empty() && normalized.size() == pcm_bytes.size()) {
                normalize_samples.emplace();
                for (size_t i = 0; i < normalized.size(); i += 2) {
                    int lo = normalized[i] & 0xFF;
                    int hi = normalized[i + 1];
                    normalize_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_after_normalize = &*normalize_samples;
            }
        }

        // Language-specific slowdown (e.g. ZH only at 1.06x)
        // Applied after normalize, matching V1 pipeline order
        const std::vector<int16_t>* use_samples_for_float = use_samples_after_normalize;
        std::optional<std::vector<int16_t>> slowed_samples;
        double effective_slowdown = slowdown_factor;
        bool use_slowdown = !slowdown_lang.empty() && row.language == slowdown_lang && slowdown_factor > 1.0;
        if (use_per_lang_preset) {
            effective_slowdown = item_slowdown_ratio;
            use_slowdown = effective_slowdown > 1.01;
        }
        if (use_moonshine_policy && row.language == "zh") {
            effective_slowdown = item_slowdown_ratio; // 1.06 set by policy above
            use_slowdown = effective_slowdown > 1.01;
        }
        if (use_moonshine_dynamic_tuning) {
            effective_slowdown = dyn_slowdown_ratio;
            use_slowdown = effective_slowdown > 1.01;
        }
        if (use_slowdown && effective_slowdown > 1.0) {
            std::vector<uint8_t> pcm_bytes(use_samples_after_normalize->size() * 2);
            for (size_t i = 0; i < use_samples_after_normalize->size(); i++) {
                int16_t s = (*use_samples_after_normalize)[i];
                pcm_bytes[i * 2] = static_cast<uint8_t>(s & 0xFF);
                pcm_bytes[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
            }
            auto slowed = mobmate::slowdown_audio(pcm_bytes.data(),
                                                   static_cast<int>(pcm_bytes.size()),
                                                   16000,
                                                   effective_slowdown);
            if (!slowed.empty()) {
                slowed_samples.emplace();
                for (size_t i = 0; i < slowed.size(); i += 2) {
                    int lo = slowed[i] & 0xFF;
                    int hi = slowed[i + 1];
                    slowed_samples->push_back(static_cast<int16_t>(lo | (hi << 8)));
                }
                use_samples_for_float = &*slowed_samples;
            }
        }

        // Convert int16 to float32
        std::vector<float> float_samples(use_samples_for_float->size());
        for (size_t i = 0; i < use_samples_for_float->size(); ++i) {
            float_samples[i] = static_cast<float>((*use_samples_for_float)[i]) / 32768.0f;
        }

        // RMS normalize (matching Phase 5A baseline)
        if (use_rms_norm) {
            double rms_sum = 0.0;
            for (size_t i = 0; i < float_samples.size(); ++i) {
                rms_sum += float_samples[i] * float_samples[i];
            }
            double rms = std::sqrt(rms_sum / float_samples.size());
            if (rms > 1e-6) {
                float target = static_cast<float>(rms_target);
                float scale = target / static_cast<float>(rms);
                for (size_t i = 0; i < float_samples.size(); ++i) {
                    float_samples[i] *= scale;
                }
            }
        }

        // Phase 5D: dump pre-ASR processed audio (single-candidate path)
        if (should_dump_row(row.index)) {
            namespace fs = std::filesystem;
            fs::path dump_dir(dump_processed_audio_dir);
            if (!fs::exists(dump_dir)) fs::create_directories(dump_dir);
            std::string dump_name = "row" + std::to_string(row.index)
                + "_" + row.language + "_" + row.scene
                + "_single.wav";
            dump_float_as_wav(float_samples.data(), static_cast<int>(float_samples.size()),
                              dump_processed_audio_dir, dump_name);
        }

        // V1-style multi-pass transcription
        int num_passes = use_per_lang_preset ? preset.passes : 1;
        if (num_passes < 1) num_passes = 1;
        if (num_passes > 6) num_passes = 6;

        struct PassResult {
            int pass_idx;
            std::string text;
            bool success;
            int latency_ms;
            int line_count;
            int word_count;
        };
        std::vector<PassResult> pass_results;

        std::cout << "bench-moonshine: transcribing " << float_samples.size()
                  << " samples (passes=" << num_passes << ")..." << std::endl;
        std::cout.flush();

        for (int p = 0; p < num_passes; p++) {
            auto start = std::chrono::steady_clock::now();
            // Use transcribeAccurate for per-lang-preset mode (V1 matching)
            auto ms_res = use_per_lang_preset
                ? bridge.transcribeAccurate(float_samples.data(),
                    static_cast<int>(float_samples.size()),
                    16000,
                    item_leading_pad,
                    item_tail_pad)
                : bridge.transcribe(float_samples.data(),
                    static_cast<int>(float_samples.size()),
                    16000,
                    item_leading_pad,
                    item_tail_pad,
                    item_without_streaming);
            auto end = std::chrono::steady_clock::now();
            int64_t p_latency = static_cast<int64_t>(
                std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());

            PassResult pr;
            pr.pass_idx = p;
            pr.success = false;
            pr.latency_ms = static_cast<int>(p_latency);
            pr.line_count = 0;
            pr.word_count = 0;

            if (!ms_res.empty() && ms_res[0].success && !ms_res[0].text.empty()) {
                pr.success = true;
                pr.text = ms_res[0].text;
                pr.line_count = ms_res[0].line_count;
                pr.word_count = ms_res[0].word_count;
            }
            pass_results.push_back(pr);
            std::cout << "bench-moonshine: pass " << (p + 1) << "/" << num_passes
                      << " latency=" << p_latency << "ms success=" << pr.success << std::endl;
            std::cout.flush();
        }

        // Select best pass: first successful pass (V1-style)
        int best_pass_idx = 0;
        for (size_t i = 0; i < pass_results.size(); i++) {
            if (pass_results[i].success) {
                best_pass_idx = static_cast<int>(i);
                break;
            }
        }

        const PassResult& best_pass = pass_results[best_pass_idx];
        result.latency_ms = 0;
        for (const auto& pr : pass_results) {
            result.latency_ms += pr.latency_ms;
        }

        if (!best_pass.success) {
            result.transcript = "";
            score_moonshine_result();
            result.extra_json_fields = "\"engine\":\"moonshine\",\"selected_engine\":\"none\",\"decision_reason\":\"transcription_failed\",\"confidence_score\":null,\"candidates\":\",";
            result.extra_json_fields += "\"preset\":{\"leading_padding_ms\":" + std::to_string(preset.leading_padding_ms)
                + ",\"tail_padding_ms\":" + std::to_string(preset.tail_padding_ms)
                + ",\"normalize_dbfs\":" + std::to_string(preset.normalize_dbfs)
                + ",\"without_streaming\":" + (preset.without_streaming ? "true" : "false")
                + ",\"slowdown_ratio\":" + std::to_string(preset.slowdown_ratio)
                + ",\"passes\":" + std::to_string(num_passes) + "}";
        } else {
            result.transcript = best_pass.text;
            // Build a synthetic TranscribeResult for JSON output
            mobmate::MoonshineBridge::TranscribeResult synth;
            synth.text = best_pass.text;
            synth.success = true;
            synth.latency_ms = best_pass.latency_ms;
            synth.line_count = best_pass.line_count;
            synth.word_count = best_pass.word_count;
            synth.avg_word_confidence = -1.0f;
            synth.min_word_confidence = -1.0f;
            synth.line_confidence = -1.0f;
            synth.confidence_score = -1.0f;
            result.extra_json_fields = moonshine_candidate_json(synth,
                preset.leading_padding_ms, preset.tail_padding_ms,
                preset.normalize_dbfs, preset.without_streaming, preset.slowdown_ratio);
            score_moonshine_result();
            result.error = "";
        }

        results.push_back(result);
    }

    // Write JSONL output
    std::ofstream ofs(out_path);
    if (!ofs) {
        std::cerr << "bench-moonshine: cannot open output file: " << out_path << std::endl;
        std::exit(1);
    }

    for (const auto& r : results) {
        ofs << mobmate::bench_result_to_jsonl(r) << "\n";
    }
    ofs.close();

    // Compute and print summary
    auto summary = mobmate::compute_summary(results);
    std::cout << "bench-moonshine: wrote " << results.size() << " results to " << out_path << std::endl;
    std::cout << "bench-moonshine: summary:" << std::endl;
    std::cout << mobmate::summary_to_json(summary) << std::endl;
}

static void cmd_transcribe(const std::vector<std::string>& args) {
    std::string input_path;
    std::string model_path;
    std::string dll_dir;
    std::string lang;
    int n_threads = 4;
    bool use_gpu = false;

    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "--input" && i + 1 < args.size()) { input_path = args[++i]; }
        else if (args[i] == "--model" && i + 1 < args.size()) { model_path = args[++i]; }
        else if (args[i] == "--whisper-dll-dir" && i + 1 < args.size()) { dll_dir = args[++i]; }
        else if (args[i] == "--lang" && i + 1 < args.size()) { lang = args[++i]; }
        else if (args[i] == "--threads" && i + 1 < args.size()) { n_threads = std::stoi(args[++i]); }
        else if (args[i] == "--gpu") { use_gpu = true; }
    }

    if (input_path.empty() || model_path.empty() || dll_dir.empty()) {
        std::cerr << "transcribe: --input, --model, --whisper-dll-dir are required" << std::endl;
        std::exit(1);
    }

    // Load Whisper engine
    std::string err_msg;
    auto engine = mobmate::WhisperEngine::load(dll_dir, model_path, err_msg, use_gpu);
    if (!engine) {
        std::cerr << "transcribe: failed to load engine: " << err_msg << std::endl;
        std::exit(1);
    }

    std::cout << "transcribe: engine loaded" << std::endl;

    // Read WAV samples
    std::string wav_err;
    auto samples = mobmate::read_wav_samples(input_path, wav_err);
    if (!samples) {
        std::cerr << "transcribe: failed to read WAV: " << wav_err << std::endl;
        std::exit(1);
    }

    std::cout << "transcribe: loaded " << samples->size() << " samples from " << input_path << std::endl;

    // Convert int16 to float32 (normalize to [-1, 1])
    std::vector<float> float_samples(samples->size());
    for (size_t i = 0; i < samples->size(); ++i) {
        float_samples[i] = static_cast<float>((*samples)[i]) / 32768.0f;
    }

    // Transcribe
    auto result = engine->transcribe(float_samples.data(),
                                     static_cast<int>(float_samples.size()),
                                     lang, n_threads);
    if (!result) {
        std::cerr << "transcribe: transcription failed" << std::endl;
        std::exit(1);
    }

    std::cout << *result << std::endl;
}

static void cmd_wav_info(const std::vector<std::string>& args) {
    if (args.empty()) {
        std::cerr << "wav-info: <wav-file> is required" << std::endl;
        std::exit(1);
    }

    std::string path = args[0];
    std::string err_msg;
    auto meta = mobmate::read_wav_metadata(path, err_msg);

    if (!meta) {
        std::cerr << "wav-info error: " << err_msg << std::endl;
        std::exit(1);
    }

    std::cout << mobmate::wav_metadata_to_json(*meta) << std::endl;
}

static void cmd_vad_test(const std::vector<std::string>& args) {
    std::string wav_path;
    std::string model_path = "models/silero_vad/silero_vad_int8.onnx";
    std::string ort_dir = "libs/onnxruntime/bin";
    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "--wav" && i + 1 < args.size()) wav_path = args[++i];
        else if (args[i] == "--model" && i + 1 < args.size()) model_path = args[++i];
        else if (args[i] == "--ort-dir" && i + 1 < args.size()) ort_dir = args[++i];
        else if (wav_path.empty()) wav_path = args[i];
    }

    if (wav_path.empty()) {
        std::cerr << "vad-test: --wav <wav-file> is required" << std::endl;
        std::exit(1);
    }

    std::string err_msg;
    auto meta = mobmate::read_wav_metadata(wav_path, err_msg);
    if (!meta) {
        std::cerr << "vad-test: failed to read WAV metadata: " << err_msg << std::endl;
        std::exit(1);
    }
    if (!meta->is_pcm || !meta->is_16bit || meta->sample_rate != 16000) {
        std::cerr << "vad-test: WAV must be 16-bit PCM 16kHz" << std::endl;
        std::exit(1);
    }

    auto samples = mobmate::read_wav_samples(wav_path, err_msg);
    if (!samples) {
        std::cerr << "vad-test: failed to read WAV samples: " << err_msg << std::endl;
        std::exit(1);
    }

    std::vector<int16_t> mono;
    if (meta->channels <= 1) {
        mono = *samples;
    } else {
        mono.reserve(samples->size() / meta->channels);
        for (size_t i = 0; i + meta->channels <= samples->size(); i += meta->channels) {
            int sum = 0;
            for (uint16_t ch = 0; ch < meta->channels; ++ch) sum += (*samples)[i + ch];
            mono.push_back(static_cast<int16_t>(sum / meta->channels));
        }
    }

    char errbuf[512] = {};
    silero_vad_handle* vad = silero_vad_init(model_path.c_str(), ort_dir.c_str(), errbuf, sizeof(errbuf));
    if (!vad) {
        std::cerr << "vad-test: SileroVAD init failed: " << errbuf << std::endl;
        std::exit(1);
    }

    int frames = 0;
    int speech_frames = 0;
    double max_prob = 0.0;
    double sum_prob = 0.0;
    for (size_t pos = 0; pos + 512 <= mono.size(); pos += 512) {
        double prob = silero_vad_score(vad, mono.data() + pos, 512, 16000);
        if (prob < 0.0) continue;
        frames++;
        sum_prob += prob;
        if (prob > max_prob) max_prob = prob;
        if (prob >= 0.42) speech_frames++;
    }
    silero_vad_free(vad);

    std::cout << "{\"ok\":true"
              << ",\"wav\":\"" << wav_path << "\""
              << ",\"model\":\"" << model_path << "\""
              << ",\"ortDir\":\"" << ort_dir << "\""
              << ",\"frames\":" << frames
              << ",\"speechFrames\":" << speech_frames
              << ",\"maxProbability\":" << max_prob
              << ",\"avgProbability\":" << (frames > 0 ? sum_prob / frames : 0.0)
              << "}" << std::endl;
}

static void cmd_manifest_info(const std::vector<std::string>& args) {
    if (args.empty()) {
        std::cerr << "manifest-info: <manifest-file> is required" << std::endl;
        std::exit(1);
    }

    std::string path = args[0];
    std::string err_msg;
    auto rows = mobmate::read_manifest(path, err_msg);

    if (!rows) {
        std::cerr << "manifest-info error: " << err_msg << std::endl;
        std::exit(1);
    }

    std::cout << "Loaded " << rows->size() << " rows" << std::endl;
    for (const auto& row : *rows) {
        std::cout << "[" << row.index << "] " << row.wav_path << " | "
                  << row.expected << " | " << row.language << " | "
                  << row.scene;
        if (!row.aliases.empty()) {
            std::cout << " | aliases: ";
            for (size_t i = 0; i < row.aliases.size(); ++i) {
                if (i > 0) std::cout << ", ";
                std::cout << row.aliases[i];
            }
        }
        std::cout << std::endl;
    }
}

#ifdef _WIN32
int wmain(int argc, wchar_t* argv[]) {
    std::vector<std::string> utf8_argv;
    utf8_argv.reserve(static_cast<size_t>(argc));
    for (int i = 0; i < argc; ++i) {
        utf8_argv.push_back(wide_to_utf8(argv[i]));
    }
    return run_main(utf8_argv);
}
#else
int main(int argc, char* argv[]) {
    return run_main(std::vector<std::string>(argv, argv + argc));
}
#endif
