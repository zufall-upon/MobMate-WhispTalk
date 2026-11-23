#pragma once

#include <cstdint>

/// Minimal SileroVAD wrapper using ONNX Runtime C API (LoadLibrary dynamic load).
///
/// Port of V1 Java SileroVadOnnx:
///   SAMPLE_RATE = 16000
///   FRAME_SAMPLES = 512  (32 ms at 16 kHz)
///   LSTM state: float[2][1][128]
///
/// Input : PCM16LE mono @ 16 kHz
/// Output: speech probability in [0, 1] for the chunk (max over frames).
///
/// Thread-safe via internal mutex.  C ABI for easy integration.

#ifdef __cplusplus
extern "C" {
#endif

/* Opaque handle returned by silero_vad_init. */
typedef struct silero_vad_handle silero_vad_handle;

/*
 * Initialize SileroVAD.
 *   model_path  : path to silero_vad_int8.onnx (UTF-8).
 *   ort_dll_dir : directory containing onnxruntime.dll (UTF-8).
 *   err         : error buffer (filled on failure).
 *   err_size    : size of err buffer (0 to skip).
 *
 * Returns non-null handle on success, NULL on failure.
 */
silero_vad_handle* silero_vad_init(const char* model_path,
                                    const char* ort_dll_dir,
                                    char* err,
                                    int err_size);

/*
 * Score a PCM16LE chunk.
 *   pcm16        : PCM16LE mono samples.
 *   sample_count : number of int16 samples.
 *   sample_rate  : must be 16000.
 *
 * Returns speech probability in [0, 1] (max over 512-sample frames).
 * Returns -1.0 on error or if sample_rate != 16000.
 */
double silero_vad_score(silero_vad_handle* handle,
                         const int16_t* pcm16,
                         int sample_count,
                         int sample_rate);

/* Reset LSTM state and pending buffer (start new utterance). */
void silero_vad_reset(silero_vad_handle* handle);

/* Release all resources. Safe to call with NULL. */
void silero_vad_free(silero_vad_handle* handle);

/* Returns true if the model is loaded and ready. */
int silero_vad_is_available(silero_vad_handle* handle);

/* Returns last error message (owned by handle; may be empty). */
const char* silero_vad_last_error(silero_vad_handle* handle);

#ifdef __cplusplus
}
#endif
