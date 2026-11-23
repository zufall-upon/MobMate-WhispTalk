#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace mobmate {

/// State for streaming audio prefilter (matches Java AudioPrefilter.State).
struct AudioPrefilterState {
    // High-pass filter state
    double hpPrevX = 0.0;
    double hpPrevY = 0.0;
    // Pre-emphasis state
    double prePrevX = 0.0;
    // Low-pass filter state
    double lpPrevY = 0.0;
    // AGC state
    double agcGain = 1.0;
    // Notch filter state (6 notch frequencies)
    int notchSampleRateHz = 0;
    double notchX1[6] = {0};
    double notchX2[6] = {0};
    double notchY1[6] = {0};
    double notchY2[6] = {0};
    // Spectral enhancement state (STRONG profile)
    int spectralSampleRateHz = 0;
    std::vector<double> spectralNoiseFloor;
    std::vector<double> spectralLateReverb;

    void reset();
};

/// Prefilter profile (matches Java AudioPrefilter.Profile).
enum class PrefilterProfile {
    NORMAL,
    STRONG,
};

/// Applies the NORMAL-profile audio prefilter pipeline to PCM16 LE samples.
/// Pipeline: hum notch -> high-pass -> pre-emphasis -> low-pass -> AGC -> soft limit.
/// Input and output are the same buffer size (in-place style, but a new vector is returned).
///
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param state    Mutable filter state (create fresh per audio chunk, or reuse for streaming)
/// @return Filtered PCM16 LE byte buffer, or empty vector on error
std::vector<uint8_t> prefilter_process(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    AudioPrefilterState* state);

/// Applies the STRONG-profile audio prefilter pipeline to PCM16 LE samples.
/// Pipeline: hum notch -> spectral subtraction + dereverb -> high-pass -> pre-emphasis -> low-pass -> AGC -> soft limit.
/// Uses DFT-based spectral enhancement with overlap-add for noise suppression and reverberation reduction.
///
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param state    Mutable filter state (must have been reset before first use)
/// @return Filtered PCM16 LE byte buffer, or empty vector on error
std::vector<uint8_t> prefilter_process_strong(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    AudioPrefilterState* state);

/// Trims leading/trailing silence from PCM16 LE audio.
/// Uses absolute threshold to find speech boundaries, then applies adaptive lead pad and tail pad.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param absThreshold  Absolute sample value threshold for silence detection (default 300)
/// @param tailPadMs     Tail padding in milliseconds (default 120)
/// @return Trimmed PCM16 LE byte buffer, or copy of input if trimming not applicable
std::vector<uint8_t> trim_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int absThreshold,
    int tailPadMs);

/// Appends silence to PCM16 LE audio.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param silenceMs     Silence duration in milliseconds to append
/// @return Padded PCM16 LE byte buffer
std::vector<uint8_t> append_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int silenceMs);

/// Applies a first-order RC high-pass filter to PCM16 LE audio.
/// @param pcm16le     PCM16 little-endian byte buffer
/// @param bytes       Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param cutoffHz    High-pass cutoff frequency in Hz (default 80)
/// @return Filtered PCM16 LE byte buffer, or copy of input on error
std::vector<uint8_t> apply_highpass(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    double cutoffHz);

/// Slows down PCM16 LE audio by a given factor using linear interpolation.
/// A factor > 1.0 slows down (e.g., 1.06 = 6% slower, stretching audio by 6%).
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param factor     Slowdown factor (e.g., 1.06 for 6% slowdown)
/// @return Slowed-down PCM16 LE byte buffer
std::vector<uint8_t> slowdown_audio(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    double factor);

/// Resamples PCM16 LE audio from one sample rate to another using linear interpolation.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param srcSampleRateHz  Source sample rate in Hz
/// @param dstSampleRateHz  Destination sample rate in Hz
/// @return Resampled PCM16 LE byte buffer
std::vector<uint8_t> resample_audio(
    const uint8_t* pcm16le,
    int bytes,
    int srcSampleRateHz,
    int dstSampleRateHz);

/// Final ASR tuning parameters (matches Java AudioPrefilter.FinalAsrTuning).
/// Selected dynamically based on audio characteristics (RMS, peak, voiceBandRatio, duration).
struct FinalAsrTuning {
    std::string id;              // Mode identifier: "default", "weak_voice_guard", "low_muffled_guard", "short_command_guard"
    int trimAbsThreshold = 300;  // Absolute sample threshold for silence trimming
    int trimPadMs = 120;         // Tail padding in ms after trimming
    double normalizeDbfs = -22.0; // Target RMS level in dBFS for normalization
    int leadingPaddingMs = 300;  // Leading silence padding in ms (for V1-style prepend)
    int tailPaddingMs = 300;     // Tail silence padding in ms (for V1-style append)
    double slowDownRatio = 1.0;  // Slowdown ratio (>1.0 slows down audio)
};

/// Voice metrics from audio analysis (matches Java AudioPrefilter.VoiceMetrics).
struct VoiceMetrics {
    double rms = 0.0;
    double zcr = 0.0;
    double voiceBandRatio = 0.0;
    int peak = 0;
};

/// Low-frequency periodic noise metrics (matches Java AudioPrefilter.LowFrequencyPeriodicMetrics).
struct LowFrequencyPeriodicMetrics {
    double lowToVoiceRatio = 0.0;
    double dominantLowHz = 0.0;
    double zcr = 0.0;
    double rms = 0.0;
    int peak = 0;

    /// Returns true when characteristics match mechanical/periodic noise (fan, AC, etc.)
    bool likelyMechanicalNoise() const {
        return peak >= 900
                && rms >= 220.0
                && zcr <= 0.0145
                && dominantLowHz >= 45.0
                && dominantLowHz <= 180.0
                && lowToVoiceRatio >= 120.0;
    }
};

/// Analyzes PCM16 LE audio for voice-like characteristics.
/// Computes RMS, zero-crossing rate, voice band ratio (150-3800 Hz band energy / total energy), and peak.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @return VoiceMetrics struct with analysis results
VoiceMetrics analyze_voice_like(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz);

/// Analyzes PCM16 LE audio for low-frequency periodic noise (matches Java AudioPrefilter.analyzeLowFrequencyPeriodicNoise).
/// Uses Goertzel algorithm to detect energy at specific low frequencies (45-180 Hz) vs voice frequencies (220-3000 Hz).
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @return LowFrequencyPeriodicMetrics struct with analysis results
LowFrequencyPeriodicMetrics analyze_low_frequency_periodic(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz);

/// Selects dynamic ASR tuning parameters based on audio characteristics.
/// Matches Java AudioPrefilter.selectFinalAsrTuning.
/// Analyzes raw PCM audio to determine the best trim threshold, padding,
/// normalization target, and slowdown ratio for ASR transcription.
/// @param pcm16le  PCM16 little-endian byte buffer (raw, unfiltered audio)
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @return FinalAsrTuning struct with mode-specific parameters
FinalAsrTuning select_final_asr_tuning(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz);

/// Squashes internal silence in PCM16 LE audio (matching Java squashInternalSilence16le).
/// Compresses silent regions (abs < threshold) that are >= minSilenceMs down to keepMs.
/// Short silent regions below minSilenceMs are kept as-is.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param sampleRateHz  Sample rate of the audio
/// @param absThreshold  Absolute sample value threshold for silence detection
/// @param minSilenceMs  Minimum silence duration in ms to trigger squashing
/// @param keepMs        How many ms of silence to keep when squashing
/// @return Squashed PCM16 LE byte buffer, or copy of input if squashing not applicable
std::vector<uint8_t> squash_internal_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int absThreshold,
    int minSilenceMs,
    int keepMs);

/// Normalizes the RMS level of PCM16 LE audio to a target dBFS.
/// Matches Java AudioPrefilter.normalizeFinalChunkForAsr.
/// Applies gain only if the computed gain is >= 1.03 (to avoid unnecessary processing).
/// Respects peak ceiling to prevent clipping.
/// @param pcm16le  PCM16 little-endian byte buffer
/// @param bytes    Number of valid bytes in pcm16le (must be even)
/// @param targetDbfs  Target RMS level in dBFS (default -21.0, matching V1 TARGET_FINAL_RMS_DBFS)
/// @return Normalized PCM16 LE byte buffer, or copy of input if normalization not applicable
std::vector<uint8_t> normalize_final_chunk_for_asr(
    const uint8_t* pcm16le,
    int bytes,
    double targetDbfs);

} // namespace mobmate
