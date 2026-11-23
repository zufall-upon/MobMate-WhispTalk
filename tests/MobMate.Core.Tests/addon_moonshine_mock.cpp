#include <cstdint>
#include <cstring>

// Test-only implementation of the existing Moonshine ABI; no model is loaded.
struct MockLine { const char* text; unsigned char rest[72]; };
struct MockTranscript { MockLine* lines; uint64_t count; };
static MockLine line{"代替文", {}};
static MockTranscript transcript{&line, 1};
static bool fail_transcription = false;

extern "C" {
__declspec(dllexport) int moonshine_get_version() { return 20000; }
__declspec(dllexport) const char* moonshine_error_to_string(int) { return "mock failure"; }
__declspec(dllexport) int moonshine_load_transcriber_from_files(const char* path, int, void*, long long, int) {
    return path && std::strstr(path, "fail-load") ? -1 : 1;
}
__declspec(dllexport) void moonshine_free_transcriber(int) { }
__declspec(dllexport) void mobmate_mock_set_moon_fail(int fail) { fail_transcription = fail != 0; }
__declspec(dllexport) int moonshine_transcribe_without_streaming(int, const float*, long long, int, int, void** out) {
    if (fail_transcription) return -1;
    *out = &transcript; return 0;
}
__declspec(dllexport) void moonshine_free_transcript(void*) { }
__declspec(dllexport) int moonshine_create_stream(int, int) { return 1; }
__declspec(dllexport) int moonshine_start_stream(int, int) { return 0; }
__declspec(dllexport) int moonshine_transcribe_add_audio_to_stream(int, int, const float*, long long, int, int) { return 0; }
__declspec(dllexport) int moonshine_transcribe_stream(int, int, int, void** out) { *out = &transcript; return 0; }
__declspec(dllexport) int moonshine_stop_stream(int, int) { return 0; }
__declspec(dllexport) int moonshine_free_stream(int, int) { return 0; }
}
