#pragma once

#include <string>
#include <vector>
#include <memory>

namespace mobmate {

/// Moonshine C API wrapper for one-shot transcription.
/// Uses moonshine.dll (official Moonshine C API) dynamically loaded at runtime.
class MoonshineBridge {
public:
    // Model architecture constants (from moonshine-c-api.h)
    enum ModelArch : int {
        ARCH_TINY          = 0,
        ARCH_BASE          = 1,
        ARCH_TINY_STREAMING  = 2,
        ARCH_BASE_STREAMING  = 3,
        ARCH_SMALL_STREAMING = 4,
        ARCH_MEDIUM_STREAMING = 5,
    };

    struct TranscribeResult {
        std::string text;
        int latency_ms;      // -1 if unknown
        bool success;
        std::string error;
        int line_count = 0;
        int word_count = 0;
        float avg_word_confidence = -1.0f;
        float min_word_confidence = -1.0f;
        float line_confidence = -1.0f;
        float confidence_score = -1.0f;
    };

    struct StreamResult {
        std::string text;
        int latency_ms = -1;
        bool success = true;
        std::string error;
        bool is_complete = false;
        bool is_partial = false;
        bool is_new = false;
        bool is_updated = false;
        bool has_text_changed = false;
        int line_index = -1;
    };

    struct LoadOptions {
        double vad_window_duration = 0.150;
        int vad_look_behind_sample_count = 20000;
        double vad_threshold = 0.300;
        double max_tokens_per_second = 6.500;
        // Force word_timestamps=true even for non-streaming archs so word
        // confidences are populated. Default false keeps legacy behavior
        // (word_timestamps only for streaming archs).
        bool force_word_timestamps = false;
    };

    // Non-copyable
    MoonshineBridge();
    MoonshineBridge(const MoonshineBridge&) = delete;
    MoonshineBridge& operator=(const MoonshineBridge&) = delete;
    MoonshineBridge(MoonshineBridge&&) = default;
    MoonshineBridge& operator=(MoonshineBridge&&) = default;

    ~MoonshineBridge();

    /// Initialize: load moonshine.dll from the given directory.
    /// Returns true on success.
    bool init(const std::string& dll_dir);

    /// Load a Moonshine model from the given model directory.
    /// model_path should point to a directory containing .ort/.onnx/.bin files.
    /// Returns true on success.
    bool load_model(const std::string& model_path, int arch);
    bool load_model(const std::string& model_path, int arch, const LoadOptions& options);

    /// One-shot transcription of 16kHz mono float32 PCM data.
    std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate);

   /// One-shot transcription with configurable padding (matching V1 transcribeOneShotDetailed).
    std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate,
                                              int leading_padding_ms, int tail_padding_ms);

   /// One-shot transcription with configurable padding and without_streaming flag.
    /// When without_streaming is true, forces the oneshot API even for streaming models.
    std::vector<TranscribeResult> transcribe(const float* pcm, int num_samples, int sample_rate,
                                               int leading_padding_ms, int tail_padding_ms,
                                               bool without_streaming);

    /// Accurate transcription: V1-style multi-pass with longer tail padding and without_streaming.
    /// Uses passes=3, settle_sleep=45ms, tail_padding=450ms, without_streaming=true.
   std::vector<TranscribeResult> transcribeAccurate(const float* pcm, int num_samples, int sample_rate,
                                                       int leading_padding_ms, int tail_padding_ms);

    /// V1 multi-pass: streaming API with multiple poll passes and settle sleep.
    std::vector<TranscribeResult> transcribeMultiPass(const float* pcm, int num_samples, int sample_rate,
                                                       int leading_padding_ms, int tail_padding_ms,
                                                       int passes, int settle_ms);

    bool start_stream(int leading_silence_ms = 300);
    bool reset_stream(int leading_silence_ms = 300, bool flush_before_reset = true);
    bool add_audio(const float* pcm, int num_samples, int sample_rate);
    std::vector<StreamResult> poll_stream(bool force_update = true);
    std::vector<StreamResult> flush_stream(int tail_silence_ms = 300, int passes = 2, int settle_ms = 35);
    void stop_stream();

    /// Get Moonshine library version string, or empty if not loaded.
    std::string version_string() const;

    /// Check if moonshine.dll is loaded.
    bool is_loaded() const;

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace mobmate
