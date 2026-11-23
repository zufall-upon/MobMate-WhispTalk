#include "mobmate_native_core/whisper_bridge.h"

// Test-only implementation of the Whisper ABI used by WhisperEngine.
extern "C" {
__declspec(dllexport) void* whisper_init_from_file(const char*) { return reinterpret_cast<void*>(1); }
__declspec(dllexport) void whisper_free(void*) { }
__declspec(dllexport) void* whisper_full_default_params_by_ref(int) {
    static mobmate::WhisperFullParamsRaw params{};
    return &params;
}
__declspec(dllexport) int whisper_full(void*, mobmate::WhisperFullParamsRaw, const float*, int) { return 0; }
__declspec(dllexport) int whisper_full_n_segments(void*) { return 1; }
__declspec(dllexport) const char* whisper_full_get_segment_text(void*, int) { return "代替語"; }
}
