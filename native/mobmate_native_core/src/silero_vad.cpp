#include "mobmate_native_core/silero_vad.h"

#include <mutex>
#include <vector>
#include <string>
#include <cstring>
#include <algorithm>
#include <filesystem>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

// ---- ONNX Runtime C API v19 ----
#define ORT_DLL_IMPORT
#include "onnxruntime_c_api.h"

namespace fs = std::filesystem;

namespace {

static constexpr int SILERO_SAMPLE_RATE = 16000;
static constexpr int SILERO_FRAME_SAMPLES = 512;  // 32 ms

// LSTM hidden state: [num_layers=2][batch=1][hidden=128]
static constexpr int STATE_DIM0 = 2;
static constexpr int STATE_DIM1 = 1;
static constexpr int STATE_DIM2 = 128;
static constexpr int STATE_FLAT = STATE_DIM0 * STATE_DIM1 * STATE_DIM2; // 256

struct SileroVadState {
    // ONNX Runtime handles
    const OrtApi* ort = nullptr;
    OrtEnv* ort_env = nullptr;
    OrtSession* ort_session = nullptr;
    OrtSessionOptions* ort_opts = nullptr;
    OrtMemoryInfo* mem_info = nullptr;
    OrtAllocator* allocator = nullptr;

    // IO names (resolved dynamically like V1 Java)
    std::string input_name;
    std::string state_name;
    std::string sr_name;
    std::string output_name;
    std::string state_output_name;

    // LSTM state (persistent across frames)
    float state[STATE_FLAT] = {};

    // Pending samples (float32) for frame alignment
    std::vector<float> pending;

    bool available = false;
    std::string last_error;

#ifdef _WIN32
    HMODULE ort_hmodule = nullptr;
#else
    void* ort_hmodule = nullptr;
#endif
};

/* ---- Minimal UTF-8 -> UTF-16 converter for Windows ---- */
#ifdef _WIN32
static std::wstring utf8_to_wide(const char* utf8) {
    if (!utf8) return L"";
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, nullptr, 0);
    if (len <= 1) return L"";
    std::wstring ws(static_cast<size_t>(len), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, ws.data(), len) <= 0) return L"";
    ws.resize(static_cast<size_t>(len - 1));
    return ws;
}
#endif

static void fill_err(char* err, int err_size, const std::string& msg) {
    if (err && err_size > 0) {
        int n = std::min(static_cast<int>(msg.size()), err_size - 1);
        std::memcpy(err, msg.data(), static_cast<size_t>(n));
        err[n] = '\0';
    }
}

static void set_last_error(SileroVadState* st, const std::string& msg) {
    if (st) st->last_error = msg;
}

static void set_last_ort_error(SileroVadState* st, const char* prefix, OrtStatus* status) {
    if (!st) return;
    std::string msg = prefix ? prefix : "ORT error";
    if (status && st->ort) {
        const char* detail = st->ort->GetErrorMessage(status);
        if (detail && detail[0] != '\0') {
            msg += ": ";
            msg += detail;
        }
    }
    st->last_error = msg;
}

} // namespace

/* ---- C ABI implementation ---- */

extern "C" {

silero_vad_handle* silero_vad_init(const char* model_path,
                                    const char* ort_dll_dir,
                                    char* err,
                                    int err_size) {
    auto* st = new SileroVadState();

    if (!model_path || !ort_dll_dir) {
        fill_err(err, err_size, "null model_path or ort_dll_dir");
        delete st;
        return nullptr;
    }

    std::string model_path_str(model_path);
#ifdef _WIN32
    std::wstring dll_path_w = fs::path(utf8_to_wide(ort_dll_dir)) / L"onnxruntime.dll";
    std::string dll_path = std::string(ort_dll_dir) + "/onnxruntime.dll";
#else
    std::string dll_path = (fs::path(ort_dll_dir) / "libonnxruntime.so").string();
#endif

#ifdef _WIN32
    st->ort_hmodule = LoadLibraryW(dll_path_w.c_str());
    if (!st->ort_hmodule) {
        fill_err(err, err_size, "LoadLibrary onnxruntime.dll failed: " + dll_path);
        delete st;
        return nullptr;
    }

    using OrtGetApiBaseFn = const OrtApiBase*(ORT_API_CALL*)();
    auto ort_get_api_base = reinterpret_cast<OrtGetApiBaseFn>(
        GetProcAddress(st->ort_hmodule, "OrtGetApiBase"));
    if (!ort_get_api_base) {
        fill_err(err, err_size, "OrtGetApiBase not found in onnxruntime.dll");
        FreeLibrary(st->ort_hmodule);
        delete st;
        return nullptr;
    }

    const OrtApiBase* api_base = ort_get_api_base();
    if (!api_base || !api_base->GetApi) {
        fill_err(err, err_size, "OrtGetApiBase returned null");
        FreeLibrary(st->ort_hmodule);
        delete st;
        return nullptr;
    }

   st->ort = api_base->GetApi(ORT_API_VERSION);
    if (!st->ort) {
        fill_err(err, err_size, "GetApi returned null for version " + std::to_string(ORT_API_VERSION));
        FreeLibrary(st->ort_hmodule);
        delete st;
        return nullptr;
    }
#else
    st->ort_hmodule = dlopen(dll_path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!st->ort_hmodule) {
        const char* detail = dlerror();
        fill_err(err, err_size, "dlopen libonnxruntime.so failed: " + dll_path + (detail ? std::string("; ") + detail : std::string()));
        delete st;
        return nullptr;
    }

    using OrtGetApiBaseFn = const OrtApiBase*(ORT_API_CALL*)();
    auto ort_get_api_base = reinterpret_cast<OrtGetApiBaseFn>(dlsym(st->ort_hmodule, "OrtGetApiBase"));
    if (!ort_get_api_base) {
        fill_err(err, err_size, "OrtGetApiBase not found in libonnxruntime.so");
        dlclose(st->ort_hmodule);
        delete st;
        return nullptr;
    }

    const OrtApiBase* api_base = ort_get_api_base();
    if (!api_base || !api_base->GetApi) {
        fill_err(err, err_size, "OrtGetApiBase returned null");
        dlclose(st->ort_hmodule);
        delete st;
        return nullptr;
    }

    st->ort = api_base->GetApi(ORT_API_VERSION);
    if (!st->ort) {
        fill_err(err, err_size, "GetApi returned null for version " + std::to_string(ORT_API_VERSION));
        dlclose(st->ort_hmodule);
        delete st;
        return nullptr;
    }
#endif

  // Create OrtEnvironment (ORT_LOGGING_LEVEL_WARN = 1)
    OrtStatus* status = st->ort->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "SileroVAD", &st->ort_env);
    if (status) {
        fill_err(err, err_size, "OrtCreateEnv failed");
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }
    st->ort->ReleaseStatus(status);

    // Create session options
    status = st->ort->CreateSessionOptions(&st->ort_opts);
    if (status) {
        fill_err(err, err_size, "OrtCreateSessionOptions failed");
        st->ort->ReleaseEnv(st->ort_env);
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }
    st->ort->ReleaseStatus(status);

    // V1 uses threads=1; match that.
    status = st->ort->SetIntraOpNumThreads(st->ort_opts, 1);
    st->ort->ReleaseStatus(status);

    // Create CPU memory info (reused for tensor creation)
    status = st->ort->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &st->mem_info);
    if (status) {
        fill_err(err, err_size, "OrtCreateCpuMemoryInfo failed");
        st->ort->ReleaseSessionOptions(st->ort_opts);
        st->ort->ReleaseEnv(st->ort_env);
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }
    st->ort->ReleaseStatus(status);

    // Load model
#ifdef _WIN32
    std::wstring model_w = utf8_to_wide(model_path);
    status = st->ort->CreateSession(st->ort_env, model_w.c_str(), st->ort_opts, &st->ort_session);
#else
    status = st->ort->CreateSession(st->ort_env, model_path, st->ort_opts, &st->ort_session);
#endif
    if (status) {
        fill_err(err, err_size, "OrtCreateSession failed for model: " + model_path_str);
        st->ort->ReleaseMemoryInfo(st->mem_info);
        st->ort->ReleaseSessionOptions(st->ort_opts);
        st->ort->ReleaseEnv(st->ort_env);
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }
    st->ort->ReleaseStatus(status);

    // ---- Resolve IO names (same logic as V1 Java) ----
    size_t input_count = 0;
    status = st->ort->SessionGetInputCount(st->ort_session, &input_count);
    st->ort->ReleaseStatus(status);

    size_t output_count = 0;
    status = st->ort->SessionGetOutputCount(st->ort_session, &output_count);
    st->ort->ReleaseStatus(status);

    // Get default allocator for name resolution
    status = st->ort->GetAllocatorWithDefaultOptions(&st->allocator);
    if (status) {
        fill_err(err, err_size, "OrtGetAllocatorWithDefaultOptions failed");
        st->ort->ReleaseSession(st->ort_session);
        st->ort->ReleaseMemoryInfo(st->mem_info);
        st->ort->ReleaseSessionOptions(st->ort_opts);
        st->ort->ReleaseEnv(st->ort_env);
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }
    st->ort->ReleaseStatus(status);

    for (size_t i = 0; i < input_count; ++i) {
        char* name = nullptr;
        status = st->ort->SessionGetInputName(st->ort_session, i, st->allocator, &name);
        st->ort->ReleaseStatus(status);
        if (!name) continue;
        std::string n(name);
        std::string nl = n;
        std::transform(nl.begin(), nl.end(), nl.begin(), [](unsigned char c){ return std::tolower(c); });

        if (st->input_name.empty() && nl == "input") {
            st->input_name = n;
        } else if (st->state_name.empty() && nl.find("state") != std::string::npos) {
            st->state_name = n;
        } else if (st->sr_name.empty() && (nl == "sr" || nl.find("sample") != std::string::npos)) {
            st->sr_name = n;
        }
        st->ort->AllocatorFree(st->allocator, name);
    }
    // Fallback: first input
    if (st->input_name.empty() && input_count > 0) {
        char* name = nullptr;
        st->ort->SessionGetInputName(st->ort_session, 0, st->allocator, &name);
        if (name) { st->input_name = name; st->ort->AllocatorFree(st->allocator, name); }
    }

    for (size_t i = 0; i < output_count; ++i) {
        char* name = nullptr;
        status = st->ort->SessionGetOutputName(st->ort_session, i, st->allocator, &name);
        st->ort->ReleaseStatus(status);
        if (!name) continue;
        std::string n(name);
        std::string nl = n;
        std::transform(nl.begin(), nl.end(), nl.begin(), [](unsigned char c){ return std::tolower(c); });

        if (st->output_name.empty() && (nl == "output" || nl.find("prob") != std::string::npos)) {
            st->output_name = n;
        } else if (st->state_output_name.empty() && nl.find("state") != std::string::npos) {
            st->state_output_name = n;
        }
        st->ort->AllocatorFree(st->allocator, name);
    }
    // Fallback: first output
    if (st->output_name.empty() && output_count > 0) {
        char* name = nullptr;
        st->ort->SessionGetOutputName(st->ort_session, 0, st->allocator, &name);
        if (name) { st->output_name = name; st->ort->AllocatorFree(st->allocator, name); }
    }

    st->available = !st->input_name.empty() && !st->state_name.empty()
                  && !st->sr_name.empty() && !st->output_name.empty()
                  && !st->state_output_name.empty();

    if (!st->available) {
        fill_err(err, err_size, "IO name resolution incomplete: input=" + st->input_name
                    + " state=" + st->state_name + " sr=" + st->sr_name
                    + " output=" + st->output_name + " state_out=" + st->state_output_name);
        st->ort->ReleaseSession(st->ort_session);
        st->ort->ReleaseMemoryInfo(st->mem_info);
        st->ort->ReleaseSessionOptions(st->ort_opts);
        st->ort->ReleaseEnv(st->ort_env);
#ifdef _WIN32
        FreeLibrary(st->ort_hmodule);
#endif
        delete st;
        return nullptr;
    }

    return reinterpret_cast<silero_vad_handle*>(st);
}

double silero_vad_score(silero_vad_handle* handle,
                         const int16_t* pcm16,
                         int sample_count,
                         int sample_rate) {
   if (!handle || !pcm16 || sample_count <= 0) return -1.0;
    auto* st = reinterpret_cast<SileroVadState*>(handle);
    if (!st->available || !st->ort) {
        set_last_error(st, "SileroVAD unavailable");
        return -1.0;
    }
    if (sample_rate != SILERO_SAMPLE_RATE) {
        set_last_error(st, "SileroVAD unsupported sample_rate=" + std::to_string(sample_rate));
        return -1.0;
    }

    // Convert PCM16LE to float32 [-1, 1] and prepend pending
    std::vector<float> samples(st->pending.size() + static_cast<size_t>(sample_count));
    size_t pend_off = st->pending.size();
    if (!st->pending.empty()) {
        std::memcpy(samples.data(), st->pending.data(), st->pending.size() * sizeof(float));
    }
    for (int i = 0; i < sample_count; ++i) {
        samples[pend_off + static_cast<size_t>(i)] =
            std::max(-1.0f, std::min(1.0f, static_cast<float>(pcm16[i]) / 32768.0f));
    }

    double max_prob = -1.0;
    size_t pos = 0;
    while (pos + static_cast<size_t>(SILERO_FRAME_SAMPLES) <= samples.size()) {
        // Build input tensor: [1, 512] float32
        int64_t input_shape[2] = {1, SILERO_FRAME_SAMPLES};
        OrtValue* input_tensor = nullptr;
        OrtStatus* status = st->ort->CreateTensorWithDataAsOrtValue(
            st->mem_info,
            const_cast<float*>(samples.data() + pos),
            static_cast<size_t>(SILERO_FRAME_SAMPLES) * sizeof(float),
            input_shape, 2,
            ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
            &input_tensor);
        if (status) set_last_ort_error(st, "Create input tensor failed", status);
        st->ort->ReleaseStatus(status);
        if (!input_tensor) { pos += SILERO_FRAME_SAMPLES; continue; }

        // Build state tensor: [2, 1, 128] float32
        int64_t state_shape[3] = {STATE_DIM0, STATE_DIM1, STATE_DIM2};
        OrtValue* state_tensor = nullptr;
        status = st->ort->CreateTensorWithDataAsOrtValue(
            st->mem_info, st->state, STATE_FLAT * sizeof(float),
            state_shape, 3,
            ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
            &state_tensor);
        if (status) set_last_ort_error(st, "Create state tensor failed", status);
        st->ort->ReleaseStatus(status);
        if (!state_tensor) {
            st->ort->ReleaseValue(input_tensor);
            pos += SILERO_FRAME_SAMPLES;
            continue;
        }

        // Build sample rate tensor: [1] int64
        int64_t sr_val = SILERO_SAMPLE_RATE;
        int64_t sr_shape[1] = {1};
        OrtValue* sr_tensor = nullptr;
        status = st->ort->CreateTensorWithDataAsOrtValue(
            st->mem_info, &sr_val, sizeof(int64_t),
            sr_shape, 1,
            ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
            &sr_tensor);
        if (status) set_last_ort_error(st, "Create sample-rate tensor failed", status);
        st->ort->ReleaseStatus(status);
        if (!sr_tensor) {
            st->ort->ReleaseValue(input_tensor);
            st->ort->ReleaseValue(state_tensor);
            pos += SILERO_FRAME_SAMPLES;
            continue;
        }

        const char* input_names[3] = {st->input_name.c_str(),
                                       st->state_name.c_str(),
                                       st->sr_name.c_str()};
        OrtValue* input_values[3] = {input_tensor, state_tensor, sr_tensor};

        const char* output_names[2] = {st->output_name.c_str(),
                                        st->state_output_name.c_str()};
        OrtValue* output_ptrs[2] = {nullptr, nullptr};
        status = st->ort->Run(st->ort_session, nullptr,
                           input_names, input_values, 3,
                           output_names, 2,
                           output_ptrs);
        if (status) set_last_ort_error(st, "Run failed", status);
        st->ort->ReleaseStatus(status);

        st->ort->ReleaseValue(input_tensor);
        st->ort->ReleaseValue(state_tensor);
        st->ort->ReleaseValue(sr_tensor);

        if (output_ptrs[0]) {
            // Extract probability from output[0]
            float* prob_ptr = nullptr;
            st->ort->GetTensorMutableData(output_ptrs[0], reinterpret_cast<void**>(&prob_ptr));
            if (prob_ptr) {
                double p = static_cast<double>(prob_ptr[0]);
                if (max_prob < 0.0) max_prob = p;
                else max_prob = std::max(max_prob, p);
            }
            st->ort->ReleaseValue(output_ptrs[0]);
        }

        if (output_ptrs[1]) {
            // Update LSTM state from output[1]
            float* next_state_ptr = nullptr;
            st->ort->GetTensorMutableData(output_ptrs[1], reinterpret_cast<void**>(&next_state_ptr));
            if (next_state_ptr) {
                std::memcpy(st->state, next_state_ptr, STATE_FLAT * sizeof(float));
            }
            st->ort->ReleaseValue(output_ptrs[1]);
        }

        pos += SILERO_FRAME_SAMPLES;
    }

    // Store remaining samples as pending
    int remain = static_cast<int>(samples.size() - pos);
    if (remain > 0) {
        st->pending.resize(static_cast<size_t>(remain));
        std::memcpy(st->pending.data(), samples.data() + pos, remain * sizeof(float));
    } else {
        st->pending.clear();
    }

    return max_prob;
}

const char* silero_vad_last_error(silero_vad_handle* handle) {
    if (!handle) return "null handle";
    auto* st = reinterpret_cast<SileroVadState*>(handle);
    return st->last_error.c_str();
}

void silero_vad_reset(silero_vad_handle* handle) {
    if (!handle) return;
    auto* st = reinterpret_cast<SileroVadState*>(handle);
    std::memset(st->state, 0, STATE_FLAT * sizeof(float));
    st->pending.clear();
}

void silero_vad_free(silero_vad_handle* handle) {
    if (!handle) return;
    auto* st = reinterpret_cast<SileroVadState*>(handle);
    if (st->ort) {
        if (st->ort_session) st->ort->ReleaseSession(st->ort_session);
        if (st->mem_info) st->ort->ReleaseMemoryInfo(st->mem_info);
        if (st->ort_opts) st->ort->ReleaseSessionOptions(st->ort_opts);
        if (st->ort_env) st->ort->ReleaseEnv(st->ort_env);
    }
#ifdef _WIN32
    if (st->ort_hmodule) FreeLibrary(st->ort_hmodule);
#else
    if (st->ort_hmodule) dlclose(st->ort_hmodule);
#endif
    delete st;
}

int silero_vad_is_available(silero_vad_handle* handle) {
    if (!handle) return 0;
    auto* st = reinterpret_cast<SileroVadState*>(handle);
    return st->available ? 1 : 0;
}

} // extern "C"
