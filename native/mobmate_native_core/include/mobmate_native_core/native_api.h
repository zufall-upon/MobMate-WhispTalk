#pragma once

#include <stdint.h>

#ifdef _WIN32
  #define MMV2_API __declspec(dllexport)
#else
  #define MMV2_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Returns API version integer, e.g. 1. */
MMV2_API int mmv2_api_version(void);

/* Returns static UTF-8 version string. Caller must not free. */
MMV2_API const char* mmv2_native_version(void);

/* Frees strings allocated by native API (e.g. mmv2_transcribe_wav output). */
MMV2_API void mmv2_free_string(const char* p);

/* Returns allocated UTF-8 JSON with active capture devices. Caller frees via mmv2_free_string. */
MMV2_API int mmv2_audio_input_devices(const char** out_json);

/*
 * Transcribe a WAV file using JSON options.
 * Returns 0 on success. On success, *out_json receives allocated UTF-8 JSON.
 * On failure, *out_json receives allocated UTF-8 JSON containing error details.
 * Caller must free *out_json via mmv2_free_string when non-null.
 */
MMV2_API int mmv2_transcribe_wav(const char* wav_path_utf8,
                                   const char* options_json_utf8,
                                   const char** out_json);

/*
 * Transcribe raw int16 PCM samples (16-bit little-endian, mono) without a WAV file.
 * sample_rate_hz: typically 16000.
 * options_json_utf8: same format as mmv2_transcribe_wav (engine, modelPath, etc.)
 * Returns 0 on success. *out_json is allocated; caller frees via mmv2_free_string.
 */
MMV2_API int mmv2_transcribe_pcm16(const int16_t* pcm16,
                                    int count,
                                    int sample_rate_hz,
                                    const char* options_json_utf8,
                                    const char** out_json);

/*
 * Realtime session lifecycle.
 *
 * mmv2_realtime_create: creates a session handle. Returns 0 on success.
 *   Caller must free *out_json (error message) via mmv2_free_string on failure.
 * mmv2_realtime_start: starts recording. Returns 0 on success.
 * mmv2_realtime_stop: stops recording. Returns 0 on success.
 * mmv2_realtime_poll: polls session state. Returns 0 on success.
 *   *out_json is allocated; caller must free via mmv2_free_string.
 * mmv2_realtime_destroy: destroys session handle. No return value.
 */
MMV2_API int mmv2_realtime_create(const char* options_json_utf8, void** out_handle);
/* The caller must retain the callback for the lifetime of all sessions and benches.
 * The callback writes a NUL-terminated UTF-8 candidate and returns 0 on inference
 * success, or a nonzero value when the existing engine should be used instead. */
typedef int (*mmv2_addon_transcriber)(const int16_t* pcm16, int count, int sample_rate_hz,
                                     char* text_utf8, int text_capacity, int64_t* inference_ms);
MMV2_API int mmv2_set_addon_transcriber(mmv2_addon_transcriber callback);
/* Joint uses a separately retained callback with its own bounded deadline.
 * Return -2 for timeout, -3 for unavailable, other nonzero values for failure. */
MMV2_API int mmv2_set_addon_joint_transcriber(mmv2_addon_transcriber callback);
/* Additive v2 ABI. App language and model hint mode are separate; v1 remains Haas-only.
 * Null v2 callbacks unregister them. The caller retains delegates as for v1. */
typedef int (*mmv2_addon_transcriber_v2)(const char* engine_id, const char* language, const char* hint_mode,
    const int16_t* pcm16, int count, int sample_rate_hz, char* text_utf8, int text_capacity, int64_t* inference_ms);
MMV2_API int mmv2_set_addon_transcriber_v2(mmv2_addon_transcriber_v2 callback);
MMV2_API int mmv2_set_addon_joint_transcriber_v2(mmv2_addon_transcriber_v2 callback);
MMV2_API int mmv2_realtime_start(void* handle);
MMV2_API int mmv2_realtime_stop(void* handle);
MMV2_API int mmv2_realtime_poll(void* handle, const char** out_json);
/* Optional additive onset notification, invoked synchronously by poll at an
 * existing accepted VAD start, before partial/final recognition. The callback
 * must only enqueue work: no I/O, waiting, exceptions, or session API reentry.
 * Sequence is monotonic for the handle lifetime (not vadSpeechCount).
 * Null unregisters; return waits for any in-flight notification. Retain the
 * callback/context until unregister returns. As with poll/start/stop/destroy,
 * the caller must serialize session lifetime operations with destroy. */
#ifdef _WIN32
typedef void (__cdecl *mmv2_vad_start_callback)(uint64_t sequence, void* user_data);
#else
typedef void (*mmv2_vad_start_callback)(uint64_t sequence, void* user_data);
#endif
MMV2_API int mmv2_realtime_set_vad_start_callback(void* handle,
    mmv2_vad_start_callback callback, void* user_data);
MMV2_API void mmv2_realtime_destroy(void* handle);

/*
 * Bench a manifest by feeding WAV samples through the native RealtimeSession finalizer path.
 * This is for WebHost/UI benchmark parity with realtime ASR; it does not spawn the CLI.
 * Returns allocated UTF-8 JSON summary; caller frees via mmv2_free_string.
 */
MMV2_API int mmv2_realtime_bench_manifest(const char* manifest_path_utf8,
                                          const char* out_jsonl_path_utf8,
                                          const char* options_json_utf8,
                                          const char** out_json);

/* Per-call managed output stage for Windows recorded replay. No expectations or
 * aliases are passed to the callback. Buffers must contain bounded UTF-8 strings;
 * nonzero return is a failed row, not permission to silently use native output. */
typedef int (*mmv2_replay_finalizer)(const char* poll_json, const char* language, const char* scene,
    char* text_utf8, int text_capacity, char* engine_utf8, int engine_capacity,
    char* reason_utf8, int reason_capacity);
MMV2_API int mmv2_realtime_bench_manifest_processed(const char* manifest_path_utf8,
    const char* out_jsonl_path_utf8, const char* options_json_utf8,
    mmv2_replay_finalizer finalizer, const char** out_json);

/*
 * Voice Conversion Phase 2A session skeleton.
 * No audio passthrough or conversion is performed yet.
 * Status JSON truthfully reports created/running state and stored options.
 */
MMV2_API int mmv2_vc_create(const char* options_json_utf8, void** out_handle);
MMV2_API int mmv2_vc_start(void* handle);
MMV2_API int mmv2_vc_stop(void* handle);
MMV2_API int mmv2_vc_poll_status(void* handle, const char** out_json);
typedef struct mmv2_vc_filler_clip {
    uint64_t id;
    const int16_t* samples;
    uint32_t num_samples;
    uint32_t reserved;
} mmv2_vc_filler_clip;
typedef struct mmv2_vc_filler_update {
    uint32_t size;
    uint32_t version;
    uint64_t generation;
    uint32_t enabled;
    uint32_t sample_rate;
    uint32_t clip_count;
    uint32_t reserved;
    const mmv2_vc_filler_clip* clips;
    uint64_t previous_clip_id;
} mmv2_vc_filler_update;
typedef struct mmv2_vc_filler_snapshot {
    uint32_t size;
    uint32_t version;
    uint64_t generation;
    uint64_t utterance;
    uint64_t started_serial;
    uint64_t started_generation;
    uint64_t started_utterance;
    uint64_t started_clip_id;
    uint32_t prepared_count;
    uint32_t active;
} mmv2_vc_filler_snapshot;
/* Version 1, exact sizeof headers. NULL update polls; NULL snapshot is allowed.
 * Replaces 0..5 owned PCM16 mono clips (<=3 seconds, session sample rate).
 * Returns 0, -1 invalid, -2 stale generation, -3 exception/allocation failure.
 * Stop invalidates playback until a newer update; actual-start ACK survives. */
MMV2_API int mmv2_vc_filler_exchange(void* handle,
    const mmv2_vc_filler_update* update, mmv2_vc_filler_snapshot* snapshot);
MMV2_API void mmv2_vc_destroy(void* handle);
MMV2_API int mmv2_vc_inject_and_capture(void* handle,
    const int16_t* input, int num_samples, int16_t* output, const char** out_json);

/*
 * Set UTF-8 text to Windows clipboard.
 * Returns 0 on success, -1 on failure.
 */
MMV2_API int mmv2_clipboard_set_text(const char* utf8_text);

/*
 * Phase 7.4-2: TTS PCM16 playback (Windows: WASAPI, Linux: miniaudio).
 * Windows mirrors external TTS to default output when monitor_volume_percent > 0.
 * Returns 0 on success, negative on error.
 * -1: already playing (use mmv2_tts_stop first)
 * -2: invalid arguments
 * -3: output device open/initialize failed
 * -4: output device write/start failed
 */
MMV2_API int mmv2_tts_play_pcm16(
    const int16_t* samples,
    int sample_count,
    int sample_rate,
    int channels,
    int monitor_volume_percent);

/* Stops ongoing TTS playback. Returns 0 on success, -1 if not playing. */
MMV2_API int mmv2_tts_stop(void);

/* Optional additive identified filler operations. IDs must be positive,
 * monotonically increasing and never reused across ALL callers of this native
 * singleton. A cancelled ID and older delayed IDs cannot enter playback.
 * Play is synchronous: retain PCM, callback and context until it returns.
 * started is an enqueue-only acknowledgement after the backend first publishes
 * audio, not a queue/selection acknowledgement. It must not reenter any TTS API.
 * Cancel records cancellation even before Play enters; it returns only after
 * the matching active backend and its cleanup exit. Other owners are untouched.
 * -6 means stale/cancelled; other Play errors follow legacy PCM. Old exports
 * and signatures remain unchanged. */
typedef void (*mmv2_tts_started_callback)(uint64_t request_id, void* user_data);
MMV2_API int mmv2_tts_play_filler_pcm16(uint64_t request_id,
    const int16_t* samples, int sample_count, int sample_rate, int channels,
    int monitor_volume_percent, mmv2_tts_started_callback started, void* user_data);
MMV2_API int mmv2_tts_cancel_filler_and_wait(uint64_t request_id);

/*
 * Set TTS output target.
 * Windows: MMDevice GUID/endpoint id fragment. Linux: PipeWire/PulseAudio output device name.
 * Pass NULL or empty string to use the default output device.
 */
MMV2_API int mmv2_tts_set_output_device(const char* device_name_or_id);

/*
 * Phase 7A: Speaker identification API.
 * mmv2_speaker_reset: Resets all speaker profiles. Returns 0 on success.
 * mmv2_speaker_set_enabled: Enables/disables speaker ID globally. Returns 0 on success.
 *   enabled: 1 to enable, 0 to disable.
 */
MMV2_API int mmv2_speaker_reset(void);
MMV2_API int mmv2_speaker_set_enabled(int enabled);

/*
 * SileroVAD standalone API for HearingService (C# P/Invoke).
 * mmv2_vad_create : Initialize SileroVAD. Returns opaque handle (NULL on failure).
 *   model_path_utf8   : Path to silero_vad_int8.onnx.
 *   ort_dll_dir_utf8  : Directory containing onnxruntime.dll.
 * mmv2_vad_score  : Score PCM16LE chunk (16kHz mono). Returns [0,1] or -1 on error.
 * mmv2_vad_reset  : Reset LSTM state (call at utterance boundary).
 * mmv2_vad_free   : Free handle. Safe with NULL.
 */
MMV2_API void* mmv2_vad_create(const char* model_path_utf8, const char* ort_dll_dir_utf8);
MMV2_API double mmv2_vad_score(void* handle, const int16_t* pcm16, int sample_count);
MMV2_API void mmv2_vad_reset(void* handle);
MMV2_API void mmv2_vad_free(void* handle);

#ifdef __cplusplus
}
#endif
