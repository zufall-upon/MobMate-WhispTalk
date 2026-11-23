// Test-only native session for the managed callback and legacy-export boundary.
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <atomic>

using callback_t = void (*)(uint64_t, void*);
struct Session { callback_t callback = nullptr; void* context = nullptr; uint64_t sequence = 0; };

extern "C" {
__declspec(dllexport) int mmv2_realtime_create(const char*, void** result) {
    *result = new Session();
    return 0;
}
__declspec(dllexport) int mmv2_realtime_start(void*) { return 0; }
__declspec(dllexport) int mmv2_realtime_stop(void*) { return 0; }
__declspec(dllexport) int mmv2_realtime_poll(void* handle, const char** result) {
    auto* session = static_cast<Session*>(handle);
    if (session->callback) session->callback(++session->sequence, session->context);
    const char* json = "{\"state\":\"recording\",\"final\":\"\"}";
    auto* copy = static_cast<char*>(std::malloc(std::strlen(json) + 1));
    if (!copy) return -1;
    std::strcpy(copy, json);
    *result = copy;
    return 0;
}
__declspec(dllexport) void mmv2_free_string(const char* value) { std::free(const_cast<char*>(value)); }
__declspec(dllexport) void mmv2_realtime_destroy(void* handle) { delete static_cast<Session*>(handle); }
#ifndef ONSET_MOCK_OLD
static std::atomic<uint64_t> cancelled_through{0};
__declspec(dllexport) int mmv2_tts_play_filler_pcm16(uint64_t id, const int16_t* samples,
    int count, int rate, int channels, int, callback_t started, void* context) {
    if (!id || !samples || count <= 0 || rate <= 0 || channels <= 0) return -2;
    if (id <= cancelled_through.load()) return -6;
    if (started) started(id, context);
    return 0;
}
__declspec(dllexport) int mmv2_tts_cancel_filler_and_wait(uint64_t id) {
    if (!id) return -2;
    auto previous = cancelled_through.load();
    while (previous < id && !cancelled_through.compare_exchange_weak(previous, id)) {}
    return 0;
}
__declspec(dllexport) int mmv2_realtime_set_vad_start_callback(void* handle, callback_t callback, void* context) {
    auto* session = static_cast<Session*>(handle);
    session->callback = callback;
    session->context = context;
    return 0;
}
#endif
}
