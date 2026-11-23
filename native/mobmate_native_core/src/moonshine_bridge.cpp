#include "mobmate_native_core/moonshine_bridge.h"
#include "mobmate_native_core/version.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#include <algorithm>
#include <map>
#include <chrono>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

namespace mobmate {

// ---- Moonshine C API function pointer types ----
typedef int(*fn_moonshine_get_version)();
typedef const char*(*fn_moonshine_error_to_string)(int);
typedef const char*(*fn_moonshine_transcript_to_string)(void*);
typedef int(*fn_moonshine_load_transcriber_from_files)(
    const char*, int, void*, long long, int);
typedef void(*fn_moonshine_free_transcriber)(int);
typedef int(*fn_moonshine_transcribe_without_streaming)(
    int, const float*, long long, int, int, void**);
typedef void(*fn_moonshine_free_transcript)(void*);

// Streaming API function pointers
typedef int(*fn_moonshine_create_stream)(int, int);
typedef int(*fn_moonshine_start_stream)(int, int);
typedef int(*fn_moonshine_transcribe_add_audio_to_stream)(
    int, int, const float*, long long, int, int);
typedef int(*fn_moonshine_transcribe_stream)(int, int, int, void**);
typedef int(*fn_moonshine_stop_stream)(int, int);
typedef int32_t(*fn_moonshine_free_stream)(int, int);

// Moonshine option struct layout (name: char*, value: char*)
// On 64-bit Windows: 16 bytes (8+8)
struct alignas(8) MoonshineOption {
    const char* name;
    const char* value;
};

static_assert(sizeof(MoonshineOption) == 16, "MoonshineOption size mismatch");

static std::string normalize_transcript_text(const char* raw) {
    if (!raw) return "";
    std::string input(raw);
    std::string out;
    size_t pos = 0;
    while (pos <= input.size()) {
        size_t end = input.find('\n', pos);
        std::string line = input.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
        size_t first = line.find_first_not_of(" \t\r");
        size_t last = line.find_last_not_of(" \t\r");
        line = (first == std::string::npos) ? "" : line.substr(first, last - first + 1);
        if (!line.empty() && line.find(" lines") == std::string::npos && line.find(" line") == std::string::npos) {
            size_t marker = line.find("s:");
            if (marker != std::string::npos) {
                line = line.substr(marker + 2);
                size_t text_first = line.find_first_not_of(" \t\r");
                line = (text_first == std::string::npos) ? "" : line.substr(text_first);
            }
            if (!line.empty()) {
                if (!out.empty()) out.push_back(' ');
                out += line;
            }
        }
        if (end == std::string::npos) break;
        pos = end + 1;
    }
    return out.empty() ? input : out;
}

static const void* read_ptr_field(const uint8_t* base, size_t offset) {
    const void* value = nullptr;
    std::memcpy(&value, base + offset, sizeof(value));
    return value;
}

static uint64_t read_u64_field(const uint8_t* base, size_t offset) {
    uint64_t value = 0;
    std::memcpy(&value, base + offset, sizeof(value));
    return value;
}

static uint32_t read_u32_field(const uint8_t* base, size_t offset) {
    uint32_t value = 0;
    std::memcpy(&value, base + offset, sizeof(value));
    return value;
}

static int8_t read_i8_field(const uint8_t* base, size_t offset) {
    int8_t value = 0;
    std::memcpy(&value, base + offset, sizeof(value));
    return value;
}

static float read_float_field(const uint8_t* base, size_t offset) {
    float value = 0.0f;
    std::memcpy(&value, base + offset, sizeof(value));
    return value;
}

static std::string bounded_c_string(const char* raw, size_t max_len) {
    if (!raw) return "";
    size_t len = 0;
    while (len < max_len && raw[len] != '\0') ++len;
    return std::string(raw, len);
}

struct TranscriptLineLayout {
    size_t size;
    size_t latency;
    size_t words;
    size_t word_count;
};

static bool transcript_line_layout(int runtime_version, TranscriptLineLayout& layout) {
    if (runtime_version == 20000) {
        layout = {80, 60, 64, 72};
        return true;
    }
    if (runtime_version == 30000) {
        layout = {88, 64, 72, 80};
        return true;
    }
    return false;
}

class MoonshineBridge::Impl {
public:
    // DLL handle
#ifdef _WIN32
    HMODULE handle_ = nullptr;
    DLL_DIRECTORY_COOKIE dll_dir_cookie_ = nullptr;
#else
    void* handle_ = nullptr;
#endif

    // Function pointers
    fn_moonshine_get_version get_version = nullptr;
    fn_moonshine_error_to_string error_to_string = nullptr;
    fn_moonshine_transcript_to_string transcript_to_string = nullptr;
    fn_moonshine_load_transcriber_from_files load_transcriber = nullptr;
    fn_moonshine_free_transcriber free_transcriber = nullptr;
    fn_moonshine_transcribe_without_streaming transcribe_oneshot = nullptr;
    fn_moonshine_free_transcript free_transcript = nullptr;
    fn_moonshine_create_stream create_stream = nullptr;
    fn_moonshine_start_stream start_stream = nullptr;
    fn_moonshine_transcribe_add_audio_to_stream add_audio_to_stream = nullptr;
    fn_moonshine_transcribe_stream transcribe_stream = nullptr;
    fn_moonshine_stop_stream stop_stream = nullptr;
    fn_moonshine_free_stream free_stream = nullptr;

    // Loaded transcriber
    int transcriber_handle_ = -1;
    int arch_ = MoonshineBridge::ARCH_TINY;
    int stream_handle_ = -1;
    int stream_sample_rate_ = 16000;
    uint64_t last_completed_count_ = 0;
    int runtime_version_ = 0;
    TranscriptLineLayout line_layout_{};

    bool load_dll(const std::string& dll_dir) {
#ifdef _WIN32
        fs::path abs_dir = fs::absolute(dll_dir);
        fs::path dll_path = abs_dir / "moonshine.dll";
        dll_dir_cookie_ = AddDllDirectory(abs_dir.wstring().c_str());
        handle_ = LoadLibraryExW(dll_path.wstring().c_str(), nullptr,
                                 LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
#else
        std::string dll_path = dll_dir + "/libmoonshine.so";
        handle_ = dlopen(dll_path.c_str(), RTLD_NOW);
#endif
        if (!handle_) {
            std::fprintf(stderr, "[MoonshineBridge] Failed to load moonshine runtime from %s\n", dll_dir.c_str());
            return false;
        }

        // Resolve symbols
#ifdef _WIN32
        get_version           = (fn_moonshine_get_version)GetProcAddress((HMODULE)handle_, "moonshine_get_version");
        error_to_string       = (fn_moonshine_error_to_string)GetProcAddress((HMODULE)handle_, "moonshine_error_to_string");
        transcript_to_string  = (fn_moonshine_transcript_to_string)GetProcAddress((HMODULE)handle_, "moonshine_transcript_to_string");
        load_transcriber      = (fn_moonshine_load_transcriber_from_files)GetProcAddress((HMODULE)handle_, "moonshine_load_transcriber_from_files");
        free_transcriber      = (fn_moonshine_free_transcriber)GetProcAddress((HMODULE)handle_, "moonshine_free_transcriber");
        transcribe_oneshot    = (fn_moonshine_transcribe_without_streaming)GetProcAddress((HMODULE)handle_, "moonshine_transcribe_without_streaming");
        free_transcript       = (fn_moonshine_free_transcript)GetProcAddress((HMODULE)handle_, "moonshine_free_transcript");
        create_stream         = (fn_moonshine_create_stream)GetProcAddress((HMODULE)handle_, "moonshine_create_stream");
        start_stream          = (fn_moonshine_start_stream)GetProcAddress((HMODULE)handle_, "moonshine_start_stream");
        add_audio_to_stream   = (fn_moonshine_transcribe_add_audio_to_stream)GetProcAddress((HMODULE)handle_, "moonshine_transcribe_add_audio_to_stream");
        transcribe_stream     = (fn_moonshine_transcribe_stream)GetProcAddress((HMODULE)handle_, "moonshine_transcribe_stream");
        stop_stream           = (fn_moonshine_stop_stream)GetProcAddress((HMODULE)handle_, "moonshine_stop_stream");
        free_stream           = (fn_moonshine_free_stream)GetProcAddress((HMODULE)handle_, "moonshine_free_stream");
#else
        get_version           = (fn_moonshine_get_version)dlsym(handle_, "moonshine_get_version");
        error_to_string       = (fn_moonshine_error_to_string)dlsym(handle_, "moonshine_error_to_string");
        transcript_to_string  = (fn_moonshine_transcript_to_string)dlsym(handle_, "moonshine_transcript_to_string");
        load_transcriber      = (fn_moonshine_load_transcriber_from_files)dlsym(handle_, "moonshine_load_transcriber_from_files");
        free_transcriber      = (fn_moonshine_free_transcriber)dlsym(handle_, "moonshine_free_transcriber");
        transcribe_oneshot    = (fn_moonshine_transcribe_without_streaming)dlsym(handle_, "moonshine_transcribe_without_streaming");
        free_transcript       = (fn_moonshine_free_transcript)dlsym(handle_, "moonshine_free_transcript");
        create_stream         = (fn_moonshine_create_stream)dlsym(handle_, "moonshine_create_stream");
        start_stream          = (fn_moonshine_start_stream)dlsym(handle_, "moonshine_start_stream");
        add_audio_to_stream   = (fn_moonshine_transcribe_add_audio_to_stream)dlsym(handle_, "moonshine_transcribe_add_audio_to_stream");
        transcribe_stream     = (fn_moonshine_transcribe_stream)dlsym(handle_, "moonshine_transcribe_stream");
        stop_stream           = (fn_moonshine_stop_stream)dlsym(handle_, "moonshine_stop_stream");
        free_stream           = (fn_moonshine_free_stream)dlsym(handle_, "moonshine_free_stream");
#endif

        if (!get_version || !load_transcriber || !transcribe_oneshot || !create_stream || !start_stream ||
            !add_audio_to_stream || !transcribe_stream || !stop_stream || !free_stream) {
            std::fprintf(stderr, "[MoonshineBridge] Failed to resolve Moonshine symbols\n");
            std::fprintf(stderr, "[MoonshineBridge] get_version=%p load_transcriber=%p create_stream=%p start_stream=%p add_audio=%p transcribe_stream=%p stop_stream=%p free_stream=%p\n",
                          (void*)get_version, (void*)load_transcriber, (void*)create_stream, (void*)start_stream,
                          (void*)add_audio_to_stream, (void*)transcribe_stream, (void*)stop_stream, (void*)free_stream);
            unload_dll();
            return false;
        }
        std::fprintf(stderr, "[MoonshineBridge] Symbols resolved: get_version=%p load_transcriber=%p transcribe_stream=%p\n",
                     (void*)get_version, (void*)load_transcriber, (void*)transcribe_stream);

        runtime_version_ = get_version();
        if (!transcript_line_layout(runtime_version_, line_layout_)) {
            std::fprintf(stderr, "[MoonshineBridge] Unsupported Moonshine ABI version=%d\n", runtime_version_);
            unload_dll();
            return false;
        }
        std::fprintf(stdout, "[MoonshineBridge] Loaded Moonshine runtime, version=%d line_size=%zu\n",
                     runtime_version_, line_layout_.size);
        return true;
    }

    void unload_dll() {
        if (handle_) {
#ifdef _WIN32
            FreeLibrary((HMODULE)handle_);
            if (dll_dir_cookie_) {
                RemoveDllDirectory(dll_dir_cookie_);
                dll_dir_cookie_ = nullptr;
            }
#else
            dlclose(handle_);
#endif
            handle_ = nullptr;
        }
        get_version = nullptr;
        error_to_string = nullptr;
        transcript_to_string = nullptr;
        load_transcriber = nullptr;
        free_transcriber = nullptr;
        transcribe_oneshot = nullptr;
        free_transcript = nullptr;
        create_stream = nullptr;
        start_stream = nullptr;
        add_audio_to_stream = nullptr;
        transcribe_stream = nullptr;
        stop_stream = nullptr;
        free_stream = nullptr;
        runtime_version_ = 0;
        line_layout_ = {};
    }

    void close_transcriber() {
        stop_streaming();
        if (transcriber_handle_ >= 0 && free_transcriber) {
            free_transcriber(transcriber_handle_);
            transcriber_handle_ = -1;
        }
    }

    bool load_model(const std::string& model_path, int arch, const MoonshineBridge::LoadOptions& load_options) {
        if (!handle_ || !load_transcriber) return false;
        close_transcriber();

        // Build options
        std::vector<MoonshineOption> opts;
        std::string vad_window = std::to_string(load_options.vad_window_duration);
        std::string vad_lookbehind = std::to_string(load_options.vad_look_behind_sample_count);
        std::string vad_threshold = std::to_string(load_options.vad_threshold);
        std::string max_tokens = std::to_string(load_options.max_tokens_per_second);
        opts.push_back({"vad_window_duration", vad_window.c_str()});
        opts.push_back({"vad_look_behind_sample_count", vad_lookbehind.c_str()});
        opts.push_back({"vad_threshold", vad_threshold.c_str()});
        bool streaming = (arch >= MoonshineBridge::ARCH_TINY_STREAMING);
        bool word_timestamps = streaming || load_options.force_word_timestamps;
        opts.push_back({"word_timestamps", word_timestamps ? "true" : "false"});
        opts.push_back({"max_tokens_per_second", max_tokens.c_str()});

        // MOONSHINE_HEADER_VERSION = 20000
        int header_version = 20000;

        transcriber_handle_ = load_transcriber(
            model_path.c_str(),
            arch,
            opts.data(),
            (long long)opts.size(),
            header_version
        );

        if (transcriber_handle_ < 0) {
            const char* err = error_to_string ? error_to_string(transcriber_handle_) : "unknown";
            std::fprintf(stderr, "[MoonshineBridge] load_transcriber failed: %s (path=%s arch=%d)\n",
                         err, model_path.c_str(), arch);
            return false;
        }

        std::fprintf(stdout, "[MoonshineBridge] Model loaded. handle=%d\n", transcriber_handle_);
        arch_ = arch;
        return true;
    }

    bool load_model(const std::string& model_path, int arch) {
        return load_model(model_path, arch, MoonshineBridge::LoadOptions{});
    }

    void read_transcript(void* transcript_ptr, TranscribeResult& result) {
        if (!transcript_ptr) return;
            // Read transcript struct directly (same layout as Java LocalMoonshineSTT)
            // TX_OFF_LINES=0 (ptr), TX_OFF_LINE_COUNT=8 (uint64_t)
            // LINE_OFF_TEXT=0 (const char*), LINE_SIZE=80
            std::string best_text;
            int best_word_count = 0;
            double best_conf_sum = 0.0;
            float best_min_conf = -1.0f;
            {
                const uint8_t* tx = static_cast<const uint8_t*>(transcript_ptr);
                const void* pLines = read_ptr_field(tx, 0);
                uint64_t lineCount = read_u64_field(tx, 8);
                result.line_count = lineCount <= 4096 ? static_cast<int>(lineCount) : 0;

                if (pLines && lineCount > 0 && lineCount <= 4096) {
                    const uint8_t* lines = static_cast<const uint8_t*>(pLines);
                    for (uint64_t i = 0; i < lineCount; i++) {
                        const uint8_t* line_base = lines + i * line_layout_.size;
                        const void* pText = read_ptr_field(line_base, 0);
                        if (pText) {
                            const char* raw = static_cast<const char*>(pText);
                            std::string line = bounded_c_string(raw, 4096);
                            // Trim whitespace
                            size_t first = line.find_first_not_of(" \t\r\n");
                            if (first != std::string::npos) {
                                line = line.substr(first);
                                size_t last = line.find_last_not_of(" \t\r\n");
                                if (last != std::string::npos) {
                                    line = line.substr(0, last + 1);
                                }
                            }
                            if (!line.empty()) {
                                best_text = line;
                                best_word_count = 0;
                                best_conf_sum = 0.0;
                                best_min_conf = -1.0f;
                                const void* pWords = read_ptr_field(line_base, line_layout_.words);
                                uint64_t wordCount = read_u64_field(line_base, line_layout_.word_count);
                                if (pWords && wordCount > 0 && wordCount <= 4096) {
                                    const uint8_t* words = static_cast<const uint8_t*>(pWords);
                                    for (uint64_t w = 0; w < wordCount; ++w) {
                                        float conf = read_float_field(words + w * 24, 16);
                                        if (conf >= 0.0f && conf <= 1.0f) {
                                            best_conf_sum += conf;
                                            best_word_count++;
                                            best_min_conf = best_min_conf < 0.0f
                                                ? conf
                                                : (std::min)(best_min_conf, conf);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        result.text = best_text;
        result.word_count = best_word_count;
        if (best_word_count > 0) {
            result.avg_word_confidence = static_cast<float>(best_conf_sum / best_word_count);
            result.min_word_confidence = best_min_conf;
            result.line_confidence = result.avg_word_confidence;
            result.confidence_score = result.avg_word_confidence;
        }
    }

    std::vector<TranscribeResult> finish_transcript(int ret, void* transcript_ptr) {
        std::vector<TranscribeResult> results;
        if (ret != 0 || !transcript_ptr) {
            if (transcript_ptr && free_transcript) {
                free_transcript(transcript_ptr);
            }
            TranscribeResult r;
            r.success = false;
            r.error = error_to_string ? error_to_string(ret) : "transcription failed";
            results.push_back(r);
            return results;
        }

        TranscribeResult r;
        r.success = true;
        read_transcript(transcript_ptr, r);
        r.latency_ms = -1;
        results.push_back(r);

        if (free_transcript) {
            free_transcript(transcript_ptr);
        }
        return results;
    }

    std::vector<MoonshineBridge::StreamResult> finish_stream_transcript(int ret, void* transcript_ptr) {
        std::vector<MoonshineBridge::StreamResult> results;
        if (ret != 0 || !transcript_ptr) {
            if (transcript_ptr && free_transcript) {
                free_transcript(transcript_ptr);
            }
            MoonshineBridge::StreamResult r;
            r.success = false;
            r.error = error_to_string ? error_to_string(ret) : "stream transcription failed";
            results.push_back(r);
            return results;
        }

        const uint8_t* tx = static_cast<const uint8_t*>(transcript_ptr);
        const void* pLines = read_ptr_field(tx, 0);
        uint64_t lineCount = read_u64_field(tx, 8);
        uint64_t completed = 0;
        if (pLines && lineCount > 0 && lineCount <= 4096) {
            const uint8_t* lines = static_cast<const uint8_t*>(pLines);
            for (uint64_t i = 0; i < lineCount; ++i) {
                const uint8_t* line_base = lines + i * line_layout_.size;
                const void* pText = read_ptr_field(line_base, 0);
                if (!pText) continue;
                std::string text = bounded_c_string(static_cast<const char*>(pText), 4096);
                size_t first = text.find_first_not_of(" \t\r\n");
                if (first == std::string::npos) continue;
                text = text.substr(first);
                size_t last = text.find_last_not_of(" \t\r\n");
                if (last != std::string::npos) text = text.substr(0, last + 1);
                if (text.empty()) continue;

                MoonshineBridge::StreamResult r;
                r.text = text;
                r.latency_ms = static_cast<int>(read_u32_field(line_base, line_layout_.latency));
                r.is_complete = read_i8_field(line_base, 40) != 0;
                r.is_updated = read_i8_field(line_base, 41) != 0;
                r.is_new = read_i8_field(line_base, 42) != 0;
                r.has_text_changed = read_i8_field(line_base, 43) != 0;
                r.is_partial = !r.is_complete;
                r.line_index = static_cast<int>(i);
                results.push_back(r);

                if (r.is_complete) {
                    completed = i + 1;
                } else {
                    break;
                }
            }
        }
        if (completed > last_completed_count_) {
            last_completed_count_ = completed;
        }

        if (free_transcript) {
            free_transcript(transcript_ptr);
        }
        return results;
    }

    bool start_streaming(int leading_silence_ms) {
        if (transcriber_handle_ < 0 || arch_ < MoonshineBridge::ARCH_TINY_STREAMING) return false;
        if (!create_stream || !start_stream || !add_audio_to_stream || !transcribe_stream || !stop_stream || !free_stream) return false;
        stop_streaming();
        int stream = create_stream(transcriber_handle_, 0);
        if (stream < 0) return false;
        int err = start_stream(transcriber_handle_, stream);
        if (err != 0) {
            free_stream(transcriber_handle_, stream);
            return false;
        }
        stream_handle_ = stream;
        last_completed_count_ = 0;
        if (leading_silence_ms > 0) {
            int samples = (stream_sample_rate_ * leading_silence_ms) / 1000;
            if (samples < 160) samples = 160;
            std::vector<float> pad(samples, 0.0f);
            add_audio(pad.data(), static_cast<int>(pad.size()), stream_sample_rate_);
        }
        return true;
    }

    bool reset_streaming(int leading_silence_ms, bool flush_before_reset) {
        if (flush_before_reset) {
            (void)flush_streaming(300, 2, 35);
        }
        stop_streaming();
        return start_streaming(leading_silence_ms);
    }

    bool add_audio(const float* pcm, int num_samples, int sample_rate) {
        if (stream_handle_ < 0 || !pcm || num_samples <= 0 || !add_audio_to_stream) return false;
        stream_sample_rate_ = sample_rate > 0 ? sample_rate : stream_sample_rate_;
        int err = add_audio_to_stream(transcriber_handle_, stream_handle_, pcm, (long long)num_samples, stream_sample_rate_, 0);
        return err == 0;
    }

    std::vector<MoonshineBridge::StreamResult> poll_streaming(bool force_update) {
        if (stream_handle_ < 0 || !transcribe_stream) return {};
        void* transcript_ptr = nullptr;
        int ret = transcribe_stream(transcriber_handle_, stream_handle_, force_update ? 1 : 0, &transcript_ptr);
        return finish_stream_transcript(ret, transcript_ptr);
    }

    std::vector<MoonshineBridge::StreamResult> flush_streaming(int tail_silence_ms, int passes, int settle_ms) {
        std::vector<MoonshineBridge::StreamResult> out;
        if (stream_handle_ < 0) return out;
        if (tail_silence_ms > 0) {
            int samples = (stream_sample_rate_ * tail_silence_ms) / 1000;
            if (samples < 160) samples = 160;
            std::vector<float> pad(samples, 0.0f);
            (void)add_audio(pad.data(), static_cast<int>(pad.size()), stream_sample_rate_);
        }
        int count = passes > 0 ? passes : 1;
        for (int i = 0; i < count; ++i) {
            auto batch = poll_streaming(true);
            out.insert(out.end(), batch.begin(), batch.end());
            if (settle_ms > 0 && i + 1 < count) {
                std::this_thread::sleep_for(std::chrono::milliseconds(settle_ms));
            }
        }
        return out;
    }

    void stop_streaming() {
        if (stream_handle_ >= 0) {
            if (stop_stream) stop_stream(transcriber_handle_, stream_handle_);
            if (free_stream) free_stream(transcriber_handle_, stream_handle_);
            stream_handle_ = -1;
        }
    }

  std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate) {
        return transcribe(pcm, num_samples, sample_rate, 300, 300, false);
    }

    std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate,
                                              int leading_padding_ms, int tail_padding_ms) {
        return transcribe(pcm, num_samples, sample_rate, leading_padding_ms, tail_padding_ms, false);
    }

   std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate,
                                               int leading_padding_ms, int tail_padding_ms,
                                               bool without_streaming) {
        return transcribeInternal(pcm, num_samples, sample_rate,
                                  leading_padding_ms, tail_padding_ms, without_streaming, false);
    }

    std::vector<TranscribeResult> transcribeAccurate(const float* pcm, int num_samples, int sample_rate,
                                                      int leading_padding_ms, int tail_padding_ms) {
        // V1 accurate: without_streaming=true, longer tail padding, multi-pass streaming
        return transcribeInternal(pcm, num_samples, sample_rate,
                                  leading_padding_ms, tail_padding_ms, true, true);
    }

    std::vector<TranscribeResult> transcribeMultiPass(const float* pcm, int num_samples, int sample_rate,
                                                       int leading_padding_ms, int tail_padding_ms,
                                                       int passes, int settle_ms) {
        std::vector<TranscribeResult> results;
        if (transcriber_handle_ < 0) {
            TranscribeResult r;
            r.success = false;
            r.error = "transcriber not loaded";
            results.push_back(r);
            return results;
        }
        if (!create_stream || !start_stream || !add_audio_to_stream ||
            !transcribe_stream || !stop_stream || !free_stream) {
            TranscribeResult r;
            r.success = false;
            r.error = "streaming API not loaded";
            results.push_back(r);
            return results;
        }

        int leading_samples = (std::max)(160, (sample_rate * leading_padding_ms) / 1000);
        int tail_samples = (std::max)(160, (sample_rate * tail_padding_ms) / 1000);

        int stream = create_stream(transcriber_handle_, 0);
        if (stream < 0) {
            TranscribeResult r;
            r.success = false;
            r.error = error_to_string ? error_to_string(stream) : "create_stream failed";
            results.push_back(r);
            return results;
        }

        int err = start_stream(transcriber_handle_, stream);
        if (err != 0) {
            free_stream(transcriber_handle_, stream);
            TranscribeResult r;
            r.success = false;
            r.error = error_to_string ? error_to_string(err) : "start_stream failed";
            results.push_back(r);
            return results;
        }

        // Add leading pad + audio + tail pad
        {
            std::vector<float> leading_pad(leading_samples, 0.0f);
            err = add_audio_to_stream(transcriber_handle_, stream, leading_pad.data(),
                                      (long long)leading_pad.size(), sample_rate, 0);
        }
        if (err == 0) {
            err = add_audio_to_stream(transcriber_handle_, stream, pcm, (long long)num_samples, sample_rate, 0);
        }
        if (err == 0) {
            std::vector<float> tail_pad(tail_samples, 0.0f);
            err = add_audio_to_stream(transcriber_handle_, stream, tail_pad.data(),
                                      (long long)tail_pad.size(), sample_rate, 0);
        }

        if (err != 0) {
            stop_stream(transcriber_handle_, stream);
            free_stream(transcriber_handle_, stream);
            TranscribeResult r;
            r.success = false;
            r.error = error_to_string ? error_to_string(err) : "add_audio_to_stream failed";
            results.push_back(r);
            return results;
        }

        // Multi-pass: poll multiple times with settle sleep
        auto _mp_t0 = std::chrono::steady_clock::now();
        std::map<std::string, int> text_counts;
        std::string best_text;
        int best_count = 0;
        for (int i = 0; i < passes; ++i) {
            void* transcript_ptr = nullptr;
            int ret = transcribe_stream(transcriber_handle_, stream, 1, &transcript_ptr);
            auto pass_results = finish_transcript(ret, transcript_ptr);
            if (!pass_results.empty() && pass_results[0].success && !pass_results[0].text.empty()) {
                std::string text = pass_results[0].text;
                // Normalize: strip trailing punctuation for comparison
                std::string norm_text = text;
                while (!norm_text.empty() && (norm_text.back() == ',' || norm_text.back() == '\u3001' ||
                       norm_text.back() == '.' || norm_text.back() == '\u3002' ||
                       norm_text.back() == ' ' || norm_text.back() == '\u3000')) {
                    norm_text.pop_back();
                }
                int cnt = ++text_counts[norm_text];
                if (cnt > best_count) {
                    best_count = cnt;
                    best_text = text; // use original (with punctuation)
                }
            }
            if (i + 1 < passes && settle_ms > 0) {
                std::this_thread::sleep_for(std::chrono::milliseconds(settle_ms));
            }
        }

        stop_stream(transcriber_handle_, stream);
        free_stream(transcriber_handle_, stream);

        int _mp_lat = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - _mp_t0).count());
        if (!best_text.empty()) {
            TranscribeResult r;
            r.success = true;
            r.text = best_text;
            r.latency_ms = _mp_lat;
            results.push_back(r);
        } else {
            TranscribeResult r;
            r.success = false;
            r.error = "all passes returned empty";
            r.latency_ms = _mp_lat;
            results.push_back(r);
        }
        return results;
    }

    std::vector<TranscribeResult> transcribeInternal(const float* pcm, int num_samples, int sample_rate,
                                                      int leading_padding_ms, int tail_padding_ms,
                                                      bool without_streaming, bool accurate) {
        std::vector<TranscribeResult> results;

        if (transcriber_handle_ < 0) {
            TranscribeResult r;
            r.success = false;
            r.error = "transcriber not loaded";
            results.push_back(r);
            return results;
        }

        // V1 matching: max(160, (16000 * paddingMs) / 1000)
        int leading_samples;
        {
            int ls = (sample_rate * leading_padding_ms) / 1000;
            leading_samples = ls > 160 ? ls : 160;
        }
        int tail_samples;
        {
            int ts = (sample_rate * tail_padding_ms) / 1000;
            tail_samples = ts > 160 ? ts : 160;
        }

        try {
            // without_streaming=true forces oneshot path even for streaming models
            if (without_streaming || arch_ < MoonshineBridge::ARCH_TINY_STREAMING) {
                if (!transcribe_oneshot) {
                    TranscribeResult r;
                    r.success = false;
                    r.error = "one-shot API not loaded";
                    results.push_back(r);
                    return results;
                }
                std::vector<float> padded;
                padded.reserve(static_cast<size_t>(num_samples) + leading_samples + tail_samples);
                padded.insert(padded.end(), leading_samples, 0.0f);
                padded.insert(padded.end(), pcm, pcm + num_samples);
                padded.insert(padded.end(), tail_samples, 0.0f);
                void* transcript_ptr = nullptr;
                auto _os_t0 = std::chrono::steady_clock::now();
                int ret = transcribe_oneshot(transcriber_handle_, padded.data(),
                                             (long long)padded.size(), sample_rate, 0, &transcript_ptr);
                auto _os_results = finish_transcript(ret, transcript_ptr);
                if (!_os_results.empty() && _os_results[0].latency_ms < 0) {
                    _os_results[0].latency_ms = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - _os_t0).count());
                }
                return _os_results;
            }

            if (!create_stream || !start_stream || !add_audio_to_stream ||
                !transcribe_stream || !stop_stream || !free_stream) {
                TranscribeResult r;
                r.success = false;
                r.error = "streaming API not loaded";
                results.push_back(r);
                return results;
            }

            int stream = create_stream(transcriber_handle_, 0);

            if (stream < 0) {
                TranscribeResult r;
                r.success = false;
                r.error = error_to_string ? error_to_string(stream) : "create_stream failed";
                results.push_back(r);
                return results;
            }

            int err = start_stream(transcriber_handle_, stream);

            if (err != 0) {
                free_stream(transcriber_handle_, stream);
                TranscribeResult r;
                r.success = false;
                r.error = error_to_string ? error_to_string(err) : "start_stream failed";
                results.push_back(r);
                return results;
            }

            std::vector<float> leading_pad(leading_samples, 0.0f);
            err = add_audio_to_stream(transcriber_handle_, stream, leading_pad.data(),
                                      (long long)leading_pad.size(), sample_rate, 0);
            if (err == 0) {
                err = add_audio_to_stream(transcriber_handle_, stream, pcm, (long long)num_samples, sample_rate, 0);
            }
            if (err == 0) {
                std::vector<float> tail_pad(tail_samples, 0.0f);
                err = add_audio_to_stream(transcriber_handle_, stream, tail_pad.data(),
                                          (long long)tail_pad.size(), sample_rate, 0);
            }

            if (err != 0) {
                stop_stream(transcriber_handle_, stream);
                free_stream(transcriber_handle_, stream);
                TranscribeResult r;
                r.success = false;
                r.error = error_to_string ? error_to_string(err) : "add_audio_to_stream failed";
                results.push_back(r);
                return results;
            }

            void* transcript_ptr = nullptr;
            auto _ss_t0 = std::chrono::steady_clock::now();
            int ret = transcribe_stream(transcriber_handle_, stream, 1, &transcript_ptr);
            auto _ss_results = finish_transcript(ret, transcript_ptr);
            if (!_ss_results.empty() && _ss_results[0].latency_ms < 0) {
                _ss_results[0].latency_ms = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - _ss_t0).count());
            }
            stop_stream(transcriber_handle_, stream);
            free_stream(transcriber_handle_, stream);

            return _ss_results;
        } catch (const std::exception& e) {
            TranscribeResult r;
            r.success = false;
            r.error = std::string("exception: ") + e.what();
            results.push_back(r);
        } catch (...) {
            TranscribeResult r;
            r.success = false;
            r.error = "unknown exception during transcription";
            results.push_back(r);
        }

        return results;
    }
};

// ---- MoonshineBridge public API ----

MoonshineBridge::MoonshineBridge() : impl_(std::make_unique<Impl>()) {}

MoonshineBridge::~MoonshineBridge() = default;

bool MoonshineBridge::init(const std::string& dll_dir) {
    return impl_->load_dll(dll_dir);
}

bool MoonshineBridge::load_model(const std::string& model_path, int arch) {
    return impl_->load_model(model_path, arch);
}

bool MoonshineBridge::load_model(const std::string& model_path, int arch, const LoadOptions& options) {
    return impl_->load_model(model_path, arch, options);
}

std::vector<MoonshineBridge::TranscribeResult>
MoonshineBridge::transcribe(const float* pcm, int num_samples, int sample_rate) {
    return impl_->transcribe(pcm, num_samples, sample_rate, 300, 300);
}

std::vector<MoonshineBridge::TranscribeResult>
MoonshineBridge::transcribe(const float* pcm, int num_samples, int sample_rate,
                            int leading_padding_ms, int tail_padding_ms) {
    return impl_->transcribe(pcm, num_samples, sample_rate, leading_padding_ms, tail_padding_ms, false);
}

std::vector<MoonshineBridge::TranscribeResult>
MoonshineBridge::transcribe(const float* pcm, int num_samples, int sample_rate,
                             int leading_padding_ms, int tail_padding_ms,
                             bool without_streaming) {
    return impl_->transcribe(pcm, num_samples, sample_rate, leading_padding_ms, tail_padding_ms, without_streaming);
}

 std::vector<MoonshineBridge::TranscribeResult>
MoonshineBridge::transcribeAccurate(const float* pcm, int num_samples, int sample_rate,
                                     int leading_padding_ms, int tail_padding_ms) {
    return impl_->transcribeAccurate(pcm, num_samples, sample_rate, leading_padding_ms, tail_padding_ms);
}

std::vector<MoonshineBridge::TranscribeResult>
MoonshineBridge::transcribeMultiPass(const float* pcm, int num_samples, int sample_rate,
                                      int leading_padding_ms, int tail_padding_ms,
                                      int passes, int settle_ms) {
    return impl_->transcribeMultiPass(pcm, num_samples, sample_rate, leading_padding_ms, tail_padding_ms, passes, settle_ms);
}

bool MoonshineBridge::start_stream(int leading_silence_ms) {
    return impl_->start_streaming(leading_silence_ms);
}

bool MoonshineBridge::reset_stream(int leading_silence_ms, bool flush_before_reset) {
    return impl_->reset_streaming(leading_silence_ms, flush_before_reset);
}

bool MoonshineBridge::add_audio(const float* pcm, int num_samples, int sample_rate) {
    return impl_->add_audio(pcm, num_samples, sample_rate);
}

std::vector<MoonshineBridge::StreamResult>
MoonshineBridge::poll_stream(bool force_update) {
    return impl_->poll_streaming(force_update);
}

std::vector<MoonshineBridge::StreamResult>
MoonshineBridge::flush_stream(int tail_silence_ms, int passes, int settle_ms) {
    return impl_->flush_streaming(tail_silence_ms, passes, settle_ms);
}

void MoonshineBridge::stop_stream() {
    impl_->stop_streaming();
}

std::string MoonshineBridge::version_string() const {
    if (!impl_->get_version || impl_->runtime_version_ == 0) return "";
    return std::to_string(impl_->runtime_version_);
}

bool MoonshineBridge::is_loaded() const {
    return impl_->handle_ != nullptr;
}

} // namespace mobmate
