#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

struct mmv2_vc_filler_update;
struct mmv2_vc_filler_snapshot;

namespace mobmate::vc {

struct VoiceConversionOptions {
    std::string raw_json;
    std::string model_id;
    std::string model_path;  // directory containing model files
    std::string engine;      // "rvc_onnx" etc.
    std::string input_device_id;
    std::string output_device_id;
    int sample_rate = 0;
    int frame_ms = 0;
    int hop_ms = 0;

    // Prosody Neutralizer / DSP parameters (0.0–1.0)
    bool prosody_neutralizer_enabled = true;
    bool pressure_estimator_enabled = true;
    bool adaptive_strength = true;
    std::string neutralizer_preset = "flat";
    float neutralizer_strength = 1.00f;
    std::string noise_cleanup = "dtln";
    float pressure_flatten = -1.0f; // <0 = derive from neutralizer_strength
    float pressure_suppressor_strength = 0.80f;
    float breath_suppress = -1.0f;
    float f0_flatten = 0.50f;       // reserved for WORLD/F0 PoC; no-op in DSP v1
    float world_wet = 2.0f;
    bool experimental_voice_model = false;
    float energy_compression  = 0.95f;
    float shout_suppression   = 0.75f;
    float sharpness_suppression = 0.75f;
    float noise_gate_threshold = 0.35f;
    float output_limiter_ceiling = 0.85f;
    float output_volume_percent = 140.0f;
    float monitor_volume_percent = 0.0f;
    bool wav_output_enabled = false;
    std::string wav_output_directory;

    // RVC ONNX model file overrides (relative to model_path or absolute)
    std::string content_encoder_file;  // default: "contentvec.onnx"
    std::string f0_model_file;         // default: "f0.onnx" (optional; YIN fallback)
    std::string vc_model_file;         // default: "model.onnx"
    int speaker_id = 0;
    float f0_shift_semitones = 1.0f;   // pitch shift in semitones
    bool use_gpu = false;

    // SileroVAD gating (optional; empty paths = disabled)
    std::string silero_vad_model_path;   // e.g. "models/silero_vad/silero_vad_int8.onnx"
    std::string onnx_runtime_dll_dir;    // e.g. "libs/onnxruntime"
    float vad_start_threshold    = 0.45f;
    float vad_continue_threshold = 0.30f;

    // Optional Moonshine speech guard for VAD laugh detection.
    bool laugh_asr_assist_enabled = true;
    std::string moonshine_dll_dir;        // e.g. "libs/moonshine"
    std::string moonshine_model_root;     // e.g. "models/moonshine"
    std::string laugh_asr_language = "ja";
    std::string laugh_asr_tokens;         // CSV, mirrors STT->TTS laugh replacement tokens

    // Speech Enhancement (DTLN) — directory containing model_1.onnx & model_2.onnx
    // Empty = SE disabled.
    std::string se_model_dir;  // e.g. "models/dtln"
};

struct VcAudioState;       // Forward declaration (defined in voice_conversion.cpp)
struct VcOnnxPipeline;     // Forward declaration (ONNX RVC runtime)
struct SeedVcNcnnPipeline; // Forward declaration (Seed-VC ncnn runtime)
struct VcOnsetFiller;

class VoiceConversionSession {
public:
    explicit VoiceConversionSession(const char* options_json_utf8);
    ~VoiceConversionSession();

    VoiceConversionSession(const VoiceConversionSession&) = delete;
    VoiceConversionSession& operator=(const VoiceConversionSession&) = delete;

    int start();
    int stop();
    std::string status_json() const;
    int filler_exchange(const mmv2_vc_filler_update* update, mmv2_vc_filler_snapshot* snapshot);

    // Offline buffer processing: run DSP + optional VC on raw PCM.
    int process_buffer(const int16_t* input, size_t num_samples,
                       int16_t* output, char** out_pressure_json);

    // Live-path inject: feed PCM through the running session's VAD+DSP pipeline
    // (same path as on_capture). Requires start() first. Returns processed samples.
    int inject_and_capture(const int16_t* input, size_t num_samples,
                           int16_t* output, char** out_pressure_json);

    // Load RVC ONNX models. Returns 0 on success.
    int load_models();

    // Unload models and free ONNX sessions.
    void unload_models();

    // Whether VC model is loaded and ready for inference.
    bool models_loaded() const;

private:
    mutable std::mutex mutex_;
    VoiceConversionOptions options_;
    std::unique_ptr<VcOnsetFiller> filler_;
    bool running_ = false;
    VcAudioState* audio_ = nullptr;
    VcOnnxPipeline* onnx_ = nullptr;
    SeedVcNcnnPipeline* seedvc_ = nullptr;
    std::string last_load_error_;
};

} // namespace mobmate::vc
