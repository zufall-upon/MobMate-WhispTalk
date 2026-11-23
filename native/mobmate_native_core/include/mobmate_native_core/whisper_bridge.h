#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <optional>
#include <memory>
#include <cstddef>

#ifdef _WIN32
#include <windows.h>
#endif

namespace mobmate {

// Replicate whisper_context_params from whisper.cpp v1.9.5 include/whisper.h.
// Must match C layout exactly for ABI compatibility.
struct WhisperContextParams {
    bool  use_gpu;
    bool  flash_attn;
    int32_t gpu_device;
    bool  dtw_token_timestamps;
    int32_t dtw_aheads_preset;  // enum whisper_alignment_heads_preset
    int32_t dtw_n_top;
    struct {
        size_t n_heads;
        const void* heads;
    } dtw_aheads;
    size_t dtw_mem_size;
};

struct WhisperVadParamsRaw {
    float threshold;
    int   min_speech_duration_ms;
    int   min_silence_duration_ms;
    float max_speech_duration_s;
    int   speech_pad_ms;
    float samples_overlap;
};

// Replicate whisper_full_params from whisper.cpp v1.9.5 include/whisper.h.
// The stock C API passes this struct by value.
struct WhisperFullParamsRaw {
    int    strategy;
    int    n_threads;
    int    n_max_text_ctx;
    int    offset_ms;
    int    duration_ms;
    bool   translate;
    bool   no_context;
    bool   no_timestamps;
    bool   single_segment;
    bool   print_special;
    bool   print_progress;
    bool   print_realtime;
    bool   print_timestamps;
    bool   token_timestamps;
    float  thold_pt;
    float  thold_ptsum;
    int    max_len;
    bool   split_on_word;
    int    max_tokens;
    bool   debug_mode;
    int    audio_ctx;
    bool   tdrz_enable;
    const char* suppress_regex;
    const char* initial_prompt;
    bool   carry_initial_prompt;
    const int32_t* prompt_tokens;
    int    prompt_n_tokens;
    const char* language;
    bool   detect_language;
    bool   suppress_blank;
    bool   suppress_nst;
    float  temperature;
    float  max_initial_ts;
    float  length_penalty;
    float  temperature_inc;
    float  entropy_thold;
    float  logprob_thold;
    float  no_speech_thold;
    struct { int best_of; } greedy;
    struct { int beam_size; float patience; } beam_search;
    void*  new_segment_callback;
    void*  new_segment_callback_user_data;
    void*  progress_callback;
    void*  progress_callback_user_data;
    void*  encoder_begin_callback;
    void*  encoder_begin_callback_user_data;
    void*  abort_callback;
    void*  abort_callback_user_data;
    void*  logits_filter_callback;
    void*  logits_filter_callback_user_data;
    void*  grammar_rules;
    size_t n_grammar_rules;
    size_t i_start_rule;
    float  grammar_penalty;
    bool   vad;
    const char* vad_model_path;
    WhisperVadParamsRaw vad_params;
};


/// Dynamically loads whisper.dll and wraps the whisper.cpp C API.
/// RAII releases engine state/context and its loader references on destruction.
/// Windows pins the loaded module until process exit; DLL replacement requires restart.
/// Linux retains the existing dlopen/dlclose lifetime.
class WhisperEngine {
public:
    /// Load whisper.dll from the given directory, then load the model.
    /// Returns nullptr on failure with error in err_msg.
    /// If use_gpu is false, forces CPU-only inference.
    static std::unique_ptr<WhisperEngine> load(
        const std::string& dll_dir,
        const std::string& model_path,
        std::string& err_msg,
        bool use_gpu = true,
        int gpu_device = 0);

    /// Transcribe options for parameter sweep experiments.
    struct TranscribeOpts {
        std::string language;
        int n_threads = 4;
        bool no_context = true;
        float temperature = 0.0f;
        std::string initial_prompt;
        bool suppress_blank = false;
        bool suppress_nst = false;
        int max_len = 0;
        float entropy_thold = -1.0f;
        float logprob_thold = -1.0f;
        float length_penalty = -1.0f;
        /// Apply 80Hz high-pass filter to input PCM before transcription.
        bool apply_highpass = false;
    };

    struct DetailedTranscribeResult {
        std::string text;
        int segment_count = 0;
        int token_count = 0;
        float avg_token_p = -1.0f;
        float min_token_p = -1.0f;
        float avg_no_speech_prob = -1.0f;
        float max_no_speech_prob = -1.0f;
        float confidence_score = -1.0f;
    };

    /// Transcribe PCM float32 samples (16kHz mono).
    /// Returns transcript string, or nullopt on error.
    std::optional<std::string> transcribe(
        const float* samples,
        int n_samples,
        const std::string& language,  // empty = auto-detect
        int n_threads);

    /// Transcribe with full experimental options.
    std::optional<std::string> transcribe(
        const float* samples,
        int n_samples,
        const TranscribeOpts& opts);

    std::optional<DetailedTranscribeResult> transcribe_detailed(
        const float* samples,
        int n_samples,
        const TranscribeOpts& opts);

    /// Returns true if the engine is initialized.
    bool is_valid() const { return m_ctx != nullptr; }

    /// Returns model type string (e.g. "small").
    std::string model_type() const;

private:
    WhisperEngine();
public:
    ~WhisperEngine();  // public for unique_ptr

    // No copy
    WhisperEngine(const WhisperEngine&) = delete;
    WhisperEngine& operator=(const WhisperEngine&) = delete;

#ifdef _WIN32
    HMODULE m_dll = nullptr;
    DLL_DIRECTORY_COOKIE m_dll_cookie = nullptr;
#elif defined(__linux__)
    void* m_dll = nullptr;
    std::vector<void*> m_dependency_handles;
#endif

    void* m_ctx = nullptr;  // whisper context
    void* m_state = nullptr; // whisper state (from whisper_init_state)

    // Function pointers
    typedef void* (*fn_whisper_init_from_file)(const char* path_model);
    typedef void* (*fn_whisper_init_from_file_with_params)(const char* path_model, WhisperContextParams params);
    // Two-step init: no_state + init_state
    typedef void* (*fn_whisper_init_from_file_with_params_no_state)(const char* path_model, WhisperContextParams params);
    typedef void* (*fn_whisper_init_state)(void* ctx);
    typedef void (*fn_whisper_free_state)(void* state);
    // whisper_context_default_params_by_ref returns pointer (Java-compatible path)
    typedef void* (*fn_whisper_context_default_params_by_ref)();
    typedef void (*fn_whisper_free_context_params)(void* params);
    typedef void (*fn_whisper_free)(void* ctx);

    typedef void* (*fn_whisper_full_default_params_by_ref)(int strategy);
    typedef void (*fn_whisper_free_params)(void* params);

    typedef int (*fn_whisper_full)(void* ctx, WhisperFullParamsRaw params, const float* samples, int n_samples);
    // whisper_full_with_state uses explicit state (for no_state init path)
    typedef int (*fn_whisper_full_with_state)(void* ctx, void* state, WhisperFullParamsRaw params, const float* samples, int n_samples);
    // state-aware segment/text accessors (no ctx param per whisper.h v1.8.2)
    typedef int (*fn_whisper_full_n_segments_from_state)(void* state);
    typedef const char* (*fn_whisper_full_get_segment_text_from_state)(void* state, int i_segment);
    typedef float (*fn_whisper_full_get_segment_no_speech_prob_from_state)(void* state, int i_segment);
    typedef int (*fn_whisper_full_n_tokens_from_state)(void* state, int i_segment);
    typedef float (*fn_whisper_full_get_token_p_from_state)(void* state, int i_segment, int i_token);

    typedef int (*fn_whisper_full_n_segments)(void* ctx);
    typedef const char* (*fn_whisper_full_get_segment_text)(void* ctx, int i_segment);
    typedef float (*fn_whisper_full_get_segment_no_speech_prob)(void* ctx, int i_segment);
    typedef int (*fn_whisper_full_n_tokens)(void* ctx, int i_segment);
    typedef float (*fn_whisper_full_get_token_p)(void* ctx, int i_segment, int i_token);
  typedef int (*fn_whisper_full_lang_id)(void* ctx);
   typedef int (*fn_whisper_model_type)(void* ctx);

  fn_whisper_init_from_file m_fn_init = nullptr;
    fn_whisper_init_from_file_with_params m_fn_init_with_params = nullptr;
    fn_whisper_init_from_file_with_params_no_state m_fn_init_with_params_no_state = nullptr;
    fn_whisper_init_state m_fn_init_state = nullptr;
    fn_whisper_free_state m_fn_free_state = nullptr;
    fn_whisper_context_default_params_by_ref m_fn_ctx_default_params_by_ref = nullptr;
    fn_whisper_free_context_params m_fn_free_context_params = nullptr;
    fn_whisper_free m_fn_free = nullptr;
   fn_whisper_full_default_params_by_ref m_fn_full_default_params = nullptr;
   fn_whisper_free_params m_fn_free_params = nullptr;
   fn_whisper_full m_fn_full = nullptr;
   fn_whisper_full_with_state m_fn_full_with_state = nullptr;
    fn_whisper_full_n_segments_from_state m_fn_full_n_segments_from_state = nullptr;
    fn_whisper_full_get_segment_text_from_state m_fn_full_get_segment_text_from_state = nullptr;
    fn_whisper_full_get_segment_no_speech_prob_from_state m_fn_full_get_segment_no_speech_prob_from_state = nullptr;
    fn_whisper_full_n_tokens_from_state m_fn_full_n_tokens_from_state = nullptr;
    fn_whisper_full_get_token_p_from_state m_fn_full_get_token_p_from_state = nullptr;
    fn_whisper_full_n_segments m_fn_full_n_segments = nullptr;
    fn_whisper_full_get_segment_text m_fn_full_get_segment_text = nullptr;
    fn_whisper_full_get_segment_no_speech_prob m_fn_full_get_segment_no_speech_prob = nullptr;
    fn_whisper_full_n_tokens m_fn_full_n_tokens = nullptr;
    fn_whisper_full_get_token_p m_fn_full_get_token_p = nullptr;
   fn_whisper_full_lang_id m_fn_full_lang_id_fn = nullptr;
   fn_whisper_model_type m_fn_model_type = nullptr;
};

} // namespace mobmate
