#include "mobmate_native_core/whisper_bridge.h"
#include "mobmate_native_core/wave.h"
#include <sstream>
#include <filesystem>
#include <cstring>
#include <algorithm>
#include <memory>
#include <cstddef>
#include <type_traits>

#ifdef _WIN32
#include <windows.h>
#elif defined(__linux__)
#include <dlfcn.h>
#endif

namespace mobmate {

static std::filesystem::path utf8_path(const std::string& value) {
#ifdef _WIN32
    return std::filesystem::u8path(value);
#else
    return std::filesystem::path(value);
#endif
}

static std::string path_utf8(const std::filesystem::path& value) {
#ifdef _WIN32
    const auto bytes = value.u8string();
    return std::string(reinterpret_cast<const char*>(bytes.data()), bytes.size());
#else
    return value.string();
#endif
}

namespace {

#ifdef _WIN32
template<typename T>
T resolve(HMODULE hmod, const char* name) {
    FARPROC proc = ::GetProcAddress(hmod, name);
    if (!proc) return nullptr;
    return reinterpret_cast<T>(proc);
}
#elif defined(__linux__)
template<typename T>
T resolve(void* hmod, const char* name) {
    void* proc = ::dlsym(hmod, name);
    if (!proc) return nullptr;
    return reinterpret_cast<T>(proc);
}
#endif

// ABI layout verification
static_assert(sizeof(bool) == 1, "MSVC bool must be 1 byte");
static_assert(sizeof(WhisperContextParams::dtw_aheads) == 16, "dtw_aheads must be 16 bytes (size_t + ptr)");
static_assert(sizeof(WhisperContextParams) == 48, "WhisperContextParams size mismatch");
static_assert(sizeof(WhisperVadParamsRaw) == 24, "WhisperVadParamsRaw size mismatch");
static_assert(sizeof(WhisperFullParamsRaw) == 304, "WhisperFullParamsRaw size mismatch for whisper.cpp v1.8.5");
static_assert(offsetof(WhisperFullParamsRaw, debug_mode) == 52, "WhisperFullParamsRaw debug_mode offset mismatch");
static_assert(offsetof(WhisperFullParamsRaw, carry_initial_prompt) == 80, "WhisperFullParamsRaw carry_initial_prompt offset mismatch");
static_assert(offsetof(WhisperFullParamsRaw, abort_callback) == 208, "WhisperFullParamsRaw abort_callback offset mismatch");
static_assert(offsetof(WhisperFullParamsRaw, vad_model_path) == 272, "WhisperFullParamsRaw vad_model_path offset mismatch");
static_assert(offsetof(WhisperFullParamsRaw, vad_params) == 280, "WhisperFullParamsRaw vad_params offset mismatch");

} // anonymous namespace

WhisperEngine::WhisperEngine() = default;

WhisperEngine::~WhisperEngine() {
    if (m_state && m_fn_free_state) {
        m_fn_free_state(m_state);
        m_state = nullptr;
    }
    if (m_ctx && m_fn_free) {
        m_fn_free(m_ctx);
        m_ctx = nullptr;
    }
#ifdef _WIN32
    if (m_dll) {
        ::FreeLibrary(m_dll);
        m_dll = nullptr;
    }
    if (m_dll_cookie) {
        ::RemoveDllDirectory(m_dll_cookie);
        m_dll_cookie = nullptr;
    }
#elif defined(__linux__)
    if (m_dll) {
        ::dlclose(m_dll);
        m_dll = nullptr;
    }
    for (auto it = m_dependency_handles.rbegin(); it != m_dependency_handles.rend(); ++it) {
        if (*it) ::dlclose(*it);
    }
    m_dependency_handles.clear();
#endif
}

std::unique_ptr<WhisperEngine> WhisperEngine::load(
    const std::string& dll_dir,
    const std::string& model_path,
    std::string& err_msg,
    bool use_gpu,
    int gpu_device)
{
    auto engine = std::unique_ptr<WhisperEngine>(new WhisperEngine());

#ifdef _WIN32
    std::filesystem::path dll_path = std::filesystem::absolute(utf8_path(dll_dir)) / "whisper.dll";

    // Add dll_dir to DLL search path so ggml*.dll dependencies are found
    std::filesystem::path abs_dll_dir = std::filesystem::absolute(utf8_path(dll_dir));
    engine->m_dll_cookie = ::AddDllDirectory(abs_dll_dir.wstring().c_str());

    engine->m_dll = ::LoadLibraryExW(dll_path.wstring().c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
    if (!engine->m_dll) {
        DWORD err = ::GetLastError();
        err_msg = "Cannot load whisper.dll from " + path_utf8(dll_path) + " (error " + std::to_string(err) + ")";
        return nullptr;
    }
    // ggml backend resources can outlive an engine. Keep this exact module
    // resident until process exit while still releasing per-engine resources.
    HMODULE pinned_module = nullptr;
    if (!::GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(engine->m_dll), &pinned_module)) {
        const DWORD err = ::GetLastError();
        err_msg = "Cannot pin loaded whisper.dll (error " + std::to_string(err) + ")";
        return nullptr;
    }
    // Balance the extra reference; PIN prevents unloading until process exit.
    ::FreeLibrary(pinned_module);
    engine->m_fn_init = resolve<fn_whisper_init_from_file>(engine->m_dll, "whisper_init_from_file");
    engine->m_fn_init_with_params = resolve<fn_whisper_init_from_file_with_params>(engine->m_dll, "whisper_init_from_file_with_params");
    engine->m_fn_init_with_params_no_state = resolve<fn_whisper_init_from_file_with_params_no_state>(engine->m_dll, "whisper_init_from_file_with_params_no_state");
    engine->m_fn_init_state = resolve<fn_whisper_init_state>(engine->m_dll, "whisper_init_state");
    engine->m_fn_free_state = resolve<fn_whisper_free_state>(engine->m_dll, "whisper_free_state");
    // Prefer by_ref variant (Java-compatible path)
    engine->m_fn_ctx_default_params_by_ref = resolve<fn_whisper_context_default_params_by_ref>(engine->m_dll, "whisper_context_default_params_by_ref");
    engine->m_fn_free_context_params = resolve<fn_whisper_free_context_params>(engine->m_dll, "whisper_free_context_params");
    engine->m_fn_free = resolve<fn_whisper_free>(engine->m_dll, "whisper_free");
    engine->m_fn_full_default_params = resolve<fn_whisper_full_default_params_by_ref>(engine->m_dll, "whisper_full_default_params_by_ref");
    engine->m_fn_free_params = resolve<fn_whisper_free_params>(engine->m_dll, "whisper_free_params");
    engine->m_fn_full = resolve<fn_whisper_full>(engine->m_dll, "whisper_full");
    engine->m_fn_full_with_state = resolve<fn_whisper_full_with_state>(engine->m_dll, "whisper_full_with_state");
    engine->m_fn_full_n_segments_from_state = resolve<fn_whisper_full_n_segments_from_state>(engine->m_dll, "whisper_full_n_segments_from_state");
    engine->m_fn_full_get_segment_text_from_state = resolve<fn_whisper_full_get_segment_text_from_state>(engine->m_dll, "whisper_full_get_segment_text_from_state");
    engine->m_fn_full_get_segment_no_speech_prob_from_state = resolve<fn_whisper_full_get_segment_no_speech_prob_from_state>(engine->m_dll, "whisper_full_get_segment_no_speech_prob_from_state");
    engine->m_fn_full_n_tokens_from_state = resolve<fn_whisper_full_n_tokens_from_state>(engine->m_dll, "whisper_full_n_tokens_from_state");
    engine->m_fn_full_get_token_p_from_state = resolve<fn_whisper_full_get_token_p_from_state>(engine->m_dll, "whisper_full_get_token_p_from_state");
    engine->m_fn_full_n_segments = resolve<fn_whisper_full_n_segments>(engine->m_dll, "whisper_full_n_segments");
    engine->m_fn_full_get_segment_text = resolve<fn_whisper_full_get_segment_text>(engine->m_dll, "whisper_full_get_segment_text");
    engine->m_fn_full_get_segment_no_speech_prob = resolve<fn_whisper_full_get_segment_no_speech_prob>(engine->m_dll, "whisper_full_get_segment_no_speech_prob");
    engine->m_fn_full_n_tokens = resolve<fn_whisper_full_n_tokens>(engine->m_dll, "whisper_full_n_tokens");
    engine->m_fn_full_get_token_p = resolve<fn_whisper_full_get_token_p>(engine->m_dll, "whisper_full_get_token_p");
    engine->m_fn_full_lang_id_fn = resolve<fn_whisper_full_lang_id>(engine->m_dll, "whisper_full_lang_id");
    engine->m_fn_model_type = resolve<fn_whisper_model_type>(engine->m_dll, "whisper_model_type");

    if (!engine->m_fn_init || !engine->m_fn_free || !engine->m_fn_full_default_params ||
        !engine->m_fn_full || !engine->m_fn_full_n_segments || !engine->m_fn_full_get_segment_text) {
        err_msg = "whisper.dll is missing required symbols";
        return nullptr;
    }
#elif defined(__linux__)
    std::filesystem::path abs_dll_dir = std::filesystem::absolute(dll_dir);
    for (const char* dep : {"libggml-base.so", "libggml-cpu.so", "libggml-vulkan.so", "libggml.so"}) {
        auto dep_path = abs_dll_dir / dep;
        if (std::filesystem::exists(dep_path)) {
            void* handle = ::dlopen(dep_path.string().c_str(), RTLD_NOW | RTLD_GLOBAL);
            if (!handle) {
                const char* error = ::dlerror();
                err_msg = "Cannot load whisper dependency " + dep_path.string() + ": " + (error ? error : "unknown");
                return nullptr;
            }
            engine->m_dependency_handles.push_back(handle);
        }
    }

    std::filesystem::path dll_path = abs_dll_dir / "libwhisper.so";
    engine->m_dll = ::dlopen(dll_path.string().c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (!engine->m_dll) {
        const char* error = ::dlerror();
        err_msg = "Cannot load libwhisper.so from " + dll_path.string() + ": " + (error ? error : "unknown");
        return nullptr;
    }
    engine->m_fn_init = resolve<fn_whisper_init_from_file>(engine->m_dll, "whisper_init_from_file");
    engine->m_fn_init_with_params = resolve<fn_whisper_init_from_file_with_params>(engine->m_dll, "whisper_init_from_file_with_params");
    engine->m_fn_init_with_params_no_state = resolve<fn_whisper_init_from_file_with_params_no_state>(engine->m_dll, "whisper_init_from_file_with_params_no_state");
    engine->m_fn_init_state = resolve<fn_whisper_init_state>(engine->m_dll, "whisper_init_state");
    engine->m_fn_free_state = resolve<fn_whisper_free_state>(engine->m_dll, "whisper_free_state");
    engine->m_fn_ctx_default_params_by_ref = resolve<fn_whisper_context_default_params_by_ref>(engine->m_dll, "whisper_context_default_params_by_ref");
    engine->m_fn_free_context_params = resolve<fn_whisper_free_context_params>(engine->m_dll, "whisper_free_context_params");
    engine->m_fn_free = resolve<fn_whisper_free>(engine->m_dll, "whisper_free");
    engine->m_fn_full_default_params = resolve<fn_whisper_full_default_params_by_ref>(engine->m_dll, "whisper_full_default_params_by_ref");
    engine->m_fn_free_params = resolve<fn_whisper_free_params>(engine->m_dll, "whisper_free_params");
    engine->m_fn_full = resolve<fn_whisper_full>(engine->m_dll, "whisper_full");
    engine->m_fn_full_with_state = resolve<fn_whisper_full_with_state>(engine->m_dll, "whisper_full_with_state");
    engine->m_fn_full_n_segments_from_state = resolve<fn_whisper_full_n_segments_from_state>(engine->m_dll, "whisper_full_n_segments_from_state");
    engine->m_fn_full_get_segment_text_from_state = resolve<fn_whisper_full_get_segment_text_from_state>(engine->m_dll, "whisper_full_get_segment_text_from_state");
    engine->m_fn_full_get_segment_no_speech_prob_from_state = resolve<fn_whisper_full_get_segment_no_speech_prob_from_state>(engine->m_dll, "whisper_full_get_segment_no_speech_prob_from_state");
    engine->m_fn_full_n_tokens_from_state = resolve<fn_whisper_full_n_tokens_from_state>(engine->m_dll, "whisper_full_n_tokens_from_state");
    engine->m_fn_full_get_token_p_from_state = resolve<fn_whisper_full_get_token_p_from_state>(engine->m_dll, "whisper_full_get_token_p_from_state");
    engine->m_fn_full_n_segments = resolve<fn_whisper_full_n_segments>(engine->m_dll, "whisper_full_n_segments");
    engine->m_fn_full_get_segment_text = resolve<fn_whisper_full_get_segment_text>(engine->m_dll, "whisper_full_get_segment_text");
    engine->m_fn_full_get_segment_no_speech_prob = resolve<fn_whisper_full_get_segment_no_speech_prob>(engine->m_dll, "whisper_full_get_segment_no_speech_prob");
    engine->m_fn_full_n_tokens = resolve<fn_whisper_full_n_tokens>(engine->m_dll, "whisper_full_n_tokens");
    engine->m_fn_full_get_token_p = resolve<fn_whisper_full_get_token_p>(engine->m_dll, "whisper_full_get_token_p");
    engine->m_fn_full_lang_id_fn = resolve<fn_whisper_full_lang_id>(engine->m_dll, "whisper_full_lang_id");
    engine->m_fn_model_type = resolve<fn_whisper_model_type>(engine->m_dll, "whisper_model_type");

    if (!engine->m_fn_init || !engine->m_fn_free || !engine->m_fn_full_default_params ||
        !engine->m_fn_full || !engine->m_fn_full_n_segments || !engine->m_fn_full_get_segment_text) {
        err_msg = "libwhisper.so is missing required symbols";
        return nullptr;
    }
#endif

    std::filesystem::path abs_model = std::filesystem::absolute(utf8_path(model_path));
    const std::string abs_model_utf8 = path_utf8(abs_model);

    void* ctx_params_ptr = nullptr;
    if (!use_gpu && engine->m_fn_init_with_params_no_state && engine->m_fn_init_state) {
        // Two-step init: no_state + init_state (avoids stateful wrapper hang)
        if (engine->m_fn_ctx_default_params_by_ref) {
            ctx_params_ptr = engine->m_fn_ctx_default_params_by_ref();
            if (ctx_params_ptr) {
                WhisperContextParams ctx_params = *static_cast<WhisperContextParams*>(ctx_params_ptr);
                ctx_params.use_gpu = use_gpu;
                ctx_params.gpu_device = gpu_device;

                engine->m_ctx = engine->m_fn_init_with_params_no_state(abs_model_utf8.c_str(), ctx_params);

                if (engine->m_ctx) {
                    engine->m_state = engine->m_fn_init_state(engine->m_ctx);
                }
            }
        }
        if (ctx_params_ptr && engine->m_fn_free_context_params) {
            engine->m_fn_free_context_params(ctx_params_ptr);
        }
    }
    if (!engine->m_ctx && engine->m_fn_init_with_params) {
        // Fallback: stateful init
        if (engine->m_fn_ctx_default_params_by_ref) {
            ctx_params_ptr = engine->m_fn_ctx_default_params_by_ref();
            if (ctx_params_ptr) {
                WhisperContextParams ctx_params = *static_cast<WhisperContextParams*>(ctx_params_ptr);
                ctx_params.use_gpu = use_gpu;
                ctx_params.gpu_device = gpu_device;
                engine->m_ctx = engine->m_fn_init_with_params(abs_model_utf8.c_str(), ctx_params);
            }
            if (ctx_params_ptr && engine->m_fn_free_context_params) {
                engine->m_fn_free_context_params(ctx_params_ptr);
            }
        }
    }

    if (!engine->m_ctx && engine->m_fn_init) {
        // Fallback to default init (GPU)
        engine->m_ctx = engine->m_fn_init(abs_model_utf8.c_str());
    }

    if (!engine->m_ctx) {
        err_msg = "whisper_init_from_file failed for: " + abs_model_utf8;
        return nullptr;
    }

    return engine;
}

  std::optional<std::string> WhisperEngine::transcribe(
        const float* samples,
        int n_samples,
        const std::string& language,
        int n_threads)
    {
        TranscribeOpts opts;
        opts.language = language;
        opts.n_threads = n_threads;
        return transcribe(samples, n_samples, opts);
    }

    std::optional<std::string> WhisperEngine::transcribe(
        const float* samples,
        int n_samples,
        const TranscribeOpts& opts)
    {
        auto detailed = transcribe_detailed(samples, n_samples, opts);
        if (!detailed) return std::nullopt;
        return detailed->text;
    }

    std::optional<WhisperEngine::DetailedTranscribeResult> WhisperEngine::transcribe_detailed(
        const float* samples,
        int n_samples,
        const TranscribeOpts& opts)
    {
        if (!m_ctx || !m_fn_full_default_params || !m_fn_full) {
            return std::nullopt;
        }

        // Get default params (strategy GREEDY = 0)
        void* params_ptr = m_fn_full_default_params(0);
        if (!params_ptr) return std::nullopt;

        WhisperFullParamsRaw params = *static_cast<WhisperFullParamsRaw*>(params_ptr);
        params.n_threads = opts.n_threads;
        params.print_progress = false;
        params.print_realtime = false;
        params.print_timestamps = false;
        params.no_timestamps = true;
        params.single_segment = true;

        // Exp 6: experimental params
        params.no_context = opts.no_context;
        params.temperature = opts.temperature;
        params.suppress_blank = opts.suppress_blank;
        params.suppress_nst = opts.suppress_nst;
        if (opts.max_len > 0) {
            params.max_len = opts.max_len;
        }
        if (opts.entropy_thold >= 0.0f) {
            params.entropy_thold = opts.entropy_thold;
        }
        if (opts.logprob_thold >= 0.0f) {
            params.logprob_thold = opts.logprob_thold;
        }
        if (opts.length_penalty >= 0.0f) {
            params.length_penalty = opts.length_penalty;
        }
        if (!opts.initial_prompt.empty()) {
            params.initial_prompt = opts.initial_prompt.c_str();
        }

        if (!opts.language.empty()) {
            params.language = opts.language.c_str();
            params.detect_language = false;
        } else {
            params.language = nullptr;
            params.detect_language = true;
        }

        void* call_state = m_state;
        bool owns_call_state = false;

        int ret;
        if (call_state && m_fn_full_with_state) {
            // Use a fresh state per transcription to avoid cross-case language/result contamination.
            ret = m_fn_full_with_state(m_ctx, call_state, params, samples, n_samples);
        } else {
            // Fallback: stateless API (stateful init path)
            ret = m_fn_full(m_ctx, params, samples, n_samples);
        }
        if (ret != 0) {
            if (owns_call_state && m_fn_free_state) m_fn_free_state(call_state);
            if (m_fn_free_params) m_fn_free_params(params_ptr);
            return std::nullopt;
        }

        DetailedTranscribeResult result;
        std::ostringstream oss;
        int n_segments;
        if (call_state && m_fn_full_n_segments_from_state) {
            n_segments = m_fn_full_n_segments_from_state(call_state);
        } else {
            n_segments = m_fn_full_n_segments(m_ctx);
        }
        result.segment_count = n_segments;
        double token_p_sum = 0.0;
        double no_speech_sum = 0.0;
        int no_speech_count = 0;
        for (int i = 0; i < n_segments; ++i) {
            const char* text;
            if (call_state && m_fn_full_get_segment_text_from_state) {
                text = m_fn_full_get_segment_text_from_state(call_state, i);
            } else {
                text = m_fn_full_get_segment_text(m_ctx, i);
            }
            if (text) {
                oss << text;
            }

            float no_speech = -1.0f;
            if (call_state && m_fn_full_get_segment_no_speech_prob_from_state) {
                no_speech = m_fn_full_get_segment_no_speech_prob_from_state(call_state, i);
            } else if (m_fn_full_get_segment_no_speech_prob) {
                no_speech = m_fn_full_get_segment_no_speech_prob(m_ctx, i);
            }
            if (no_speech >= 0.0f) {
                no_speech_sum += no_speech;
                no_speech_count++;
                result.max_no_speech_prob = result.max_no_speech_prob < 0.0f
                    ? no_speech
                    : (std::max)(result.max_no_speech_prob, no_speech);
            }

            int n_tokens = 0;
            if (call_state && m_fn_full_n_tokens_from_state) {
                n_tokens = m_fn_full_n_tokens_from_state(call_state, i);
            } else if (m_fn_full_n_tokens) {
                n_tokens = m_fn_full_n_tokens(m_ctx, i);
            }
            for (int j = 0; j < n_tokens; ++j) {
                float p = -1.0f;
                if (call_state && m_fn_full_get_token_p_from_state) {
                    p = m_fn_full_get_token_p_from_state(call_state, i, j);
                } else if (m_fn_full_get_token_p) {
                    p = m_fn_full_get_token_p(m_ctx, i, j);
                }
                if (p >= 0.0f) {
                    token_p_sum += p;
                    result.token_count++;
                    result.min_token_p = result.min_token_p < 0.0f ? p : (std::min)(result.min_token_p, p);
                }
            }
        }

        result.text = oss.str();
        if (result.token_count > 0) {
            result.avg_token_p = static_cast<float>(token_p_sum / result.token_count);
        }
        if (no_speech_count > 0) {
            result.avg_no_speech_prob = static_cast<float>(no_speech_sum / no_speech_count);
        }
        if (result.avg_token_p >= 0.0f) {
            float speech_factor = result.max_no_speech_prob >= 0.0f ? (1.0f - result.max_no_speech_prob) : 1.0f;
            result.confidence_score = (std::max)(0.0f, (std::min)(1.0f, result.avg_token_p * speech_factor));
        }

        if (owns_call_state && m_fn_free_state) m_fn_free_state(call_state);
        if (m_fn_free_params) m_fn_free_params(params_ptr);
        return result;
    }

std::string WhisperEngine::model_type() const {
    if (m_ctx && m_fn_model_type) {
        return std::to_string(m_fn_model_type(m_ctx));
    }
    return "none";
}

} // namespace mobmate
