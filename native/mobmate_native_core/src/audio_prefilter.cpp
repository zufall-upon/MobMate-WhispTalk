#define _USE_MATH_DEFINES
#include "mobmate_native_core/audio_prefilter.h"
#include <cmath>
#include <cstring>
#include <algorithm>

namespace mobmate {

/* ------------------------------------------------------------------ */
/*  Constants (matching Java AudioPrefilter NORMAL profile)            */
/* ------------------------------------------------------------------ */

static constexpr double HUM_NOTCH_Q = 18.0;
static constexpr int NOTCH_FREQS_HZ[] = {50, 60, 100, 120, 150, 180};
static constexpr int NOTCH_COUNT = 6;

// NORMAL profile filter parameters (base defaults)
static constexpr double DEFAULT_HP_CUTOFF_HZ = 80.0;
static constexpr double DEFAULT_LP_CUTOFF_HZ = 6500.0;
static constexpr double DEFAULT_PRE_EMPHASIS_ALPHA = 0.0;

// TALK+NORMAL adaptive HP cutoff and pre-emphasis (matching Java AudioPrefilter)
static constexpr double TALK_NORMAL_HP_CUTOFF_HZ = 115.0;
static constexpr double TALK_NORMAL_NOISY_HP_CUTOFF_HZ = 220.0;
static constexpr double TALK_NORMAL_PRE_EMPHASIS_ALPHA = 0.08;
static constexpr double TALK_NORMAL_NOISY_PRE_EMPHASIS_ALPHA = 0.18;

// Goertzel low-frequency periodic noise detection frequencies
static constexpr double GOERTZEL_LOW_FREQS_HZ[] = {45, 50, 55, 60, 75, 90, 100, 105, 120, 150, 180};
static constexpr int GOERTZEL_LOW_FREQS_COUNT = 11;
static constexpr double GOERTZEL_VOICE_FREQS_HZ[] = {220, 300, 400, 600, 800, 1000, 1400, 1800, 2400, 3000};
static constexpr int GOERTZEL_VOICE_FREQS_COUNT = 10;

// AGC parameters (NORMAL profile)
static constexpr double STREAMING_AGC_TARGET_RMS_DBFS = -22.5;
static constexpr double STREAMING_AGC_MIN_RMS = 140.0;
static constexpr double STREAMING_AGC_GAIN_UP_ALPHA = 0.10;
static constexpr double STREAMING_AGC_GAIN_DOWN_ALPHA = 0.28;
static constexpr double STREAMING_AGC_IDLE_ALPHA = 0.04;
static constexpr double STREAMING_AGC_MIN_GAIN = 0.90;  /* Phase 6B: match V1 TALK mode */
static constexpr double NORMAL_STREAMING_AGC_MAX_GAIN = 2.4;

// Soft limit
static constexpr double NORMAL_LIMIT_DRIVE = 0.92;

// Peak ceiling
static constexpr double PEAK_CEILING_DBFS = -3.0;

/* ------------------------------------------------------------------ */
/*  Helper: read/write PCM16 LE                                        */
/* ------------------------------------------------------------------ */

static inline int16_t readPcm16le(const uint8_t* buf, int offset) {
    int lo = buf[offset] & 0xFF;
    int hi = buf[offset + 1];
    return static_cast<int16_t>(lo | (hi << 8));
}

static inline void writePcm16le(uint8_t* buf, int offset, int16_t value) {
    buf[offset] = static_cast<uint8_t>(value & 0xFF);
    buf[offset + 1] = static_cast<uint8_t>((value >> 8) & 0xFF);
}

static inline double dbfsToLinear(double dbfs) {
    return std::pow(10.0, dbfs / 20.0);
}

/* ------------------------------------------------------------------ */
/*  State::reset                                                       */
/* ------------------------------------------------------------------ */

void AudioPrefilterState::reset() {
    hpPrevX = 0.0;
    hpPrevY = 0.0;
    prePrevX = 0.0;
    lpPrevY = 0.0;
    agcGain = 1.0;
    notchSampleRateHz = 0;
    spectralSampleRateHz = 0;
    std::memset(notchX1, 0, sizeof(notchX1));
    std::memset(notchX2, 0, sizeof(notchX2));
    std::memset(notchY1, 0, sizeof(notchY1));
    std::memset(notchY2, 0, sizeof(notchY2));
    spectralNoiseFloor.clear();
    spectralLateReverb.clear();
}

/* ------------------------------------------------------------------ */
/*  Hum notch filters (biquad, one sample at a time)                   */
/* ------------------------------------------------------------------ */

static double applyHumNotches(double x, int sampleRateHz, AudioPrefilterState* state) {
    double y = x;
    for (int i = 0; i < NOTCH_COUNT; i++) {
        int hz = NOTCH_FREQS_HZ[i];
        if (hz >= sampleRateHz / 2) continue;

        double w0 = 2.0 * M_PI * hz / sampleRateHz;
        double alpha = std::sin(w0) / (2.0 * HUM_NOTCH_Q);
        double b0 = 1.0;
        double b1 = -2.0 * std::cos(w0);
        double b2 = 1.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * std::cos(w0);
        double a2 = 1.0 - alpha;

        double out = (b0 / a0) * y
                   + (b1 / a0) * state->notchX1[i]
                   + (b2 / a0) * state->notchX2[i]
                   - (a1 / a0) * state->notchY1[i]
                   - (a2 / a0) * state->notchY2[i];

        state->notchX2[i] = state->notchX1[i];
        state->notchX1[i] = y;
        state->notchY2[i] = state->notchY1[i];
        state->notchY1[i] = out;
        y = out;
    }
    return y;
}

/* ------------------------------------------------------------------ */
/*  Soft limit (tanh-based)                                            */
/* ------------------------------------------------------------------ */

static double softLimit(double sample, double drive) {
    double normalized = sample / static_cast<double>(INT16_MAX);
    double driven = normalized / std::max(0.1, drive);
    double limited = std::tanh(driven) * drive;
    return limited * static_cast<double>(INT16_MAX);
}

/* ------------------------------------------------------------------ */
/*  Goertzel power (single-frequency DFT, matching Java goertzelPower) */
/* ------------------------------------------------------------------ */

static double goertzelPower(const std::vector<double>& frame, int sampleRateHz, double targetHz) {
    int n = static_cast<int>(frame.size());
    if (n <= 0 || sampleRateHz <= 0 || targetHz <= 0.0) return 0.0;
    double k = 0.5 + ((n * targetHz) / sampleRateHz);
    double omega = (2.0 * M_PI * k) / n;
    double coeff = 2.0 * std::cos(omega);
    double s0 = 0.0, s1 = 0.0, s2 = 0.0;
    for (double sample : frame) {
        s0 = sample + (coeff * s1) - s2;
        s2 = s1;
        s1 = s0;
    }
    return (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
}

/* ------------------------------------------------------------------ */
/*  Low-frequency periodic noise analysis (matching Java analyzeLowFrequencyPeriodicNoise) */
/* ------------------------------------------------------------------ */

LowFrequencyPeriodicMetrics analyze_low_frequency_periodic(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz)
{
    LowFrequencyPeriodicMetrics result;
    if (!pcm16le || bytes < 512 || sampleRateHz <= 0) {
        VoiceMetrics vm = analyze_voice_like(pcm16le, bytes, sampleRateHz);
        result.zcr = vm.zcr;
        result.rms = vm.rms;
        result.peak = vm.peak;
        return result;
    }

    int samples = std::min(bytes / 2, std::min(sampleRateHz, 4096));
    if (samples < 256) {
        VoiceMetrics vm = analyze_voice_like(pcm16le, bytes, sampleRateHz);
        result.zcr = vm.zcr;
        result.rms = vm.rms;
        result.peak = vm.peak;
        return result;
    }

    // Apply Hann window
    std::vector<double> frame(samples);
    for (int i = 0; i < samples; i++) {
        double hann = 0.5 - (0.5 * std::cos((2.0 * M_PI * i) / std::max(1, samples - 1)));
        frame[i] = static_cast<double>(readPcm16le(pcm16le, i * 2)) * hann;
    }

    // Goertzel on low frequencies
    double lowPower = 0.0;
    double dominantLowHz = 0.0;
    double dominantLowPower = 0.0;
    for (int i = 0; i < GOERTZEL_LOW_FREQS_COUNT; i++) {
        double power = goertzelPower(frame, sampleRateHz, GOERTZEL_LOW_FREQS_HZ[i]);
        lowPower += power;
        if (power > dominantLowPower) {
            dominantLowPower = power;
            dominantLowHz = GOERTZEL_LOW_FREQS_HZ[i];
        }
    }

    // Goertzel on voice frequencies
    double voicePower = 0.0;
    for (int i = 0; i < GOERTZEL_VOICE_FREQS_COUNT; i++) {
        voicePower += goertzelPower(frame, sampleRateHz, GOERTZEL_VOICE_FREQS_HZ[i]);
    }

    VoiceMetrics vm = analyze_voice_like(pcm16le, bytes, sampleRateHz);
    result.lowToVoiceRatio = lowPower / std::max(1.0, voicePower);
    result.dominantLowHz = dominantLowHz;
    result.zcr = vm.zcr;
    result.rms = vm.rms;
    result.peak = vm.peak;
    return result;
}

/* ------------------------------------------------------------------ */
/*  Streaming AGC                                                      */
/* ------------------------------------------------------------------ */

static void applyStreamingAgc(std::vector<double>& samples, AudioPrefilterState* state) {
    if (samples.empty()) return;

    double sumSq = 0.0;
    double peak = 0.0;
    for (double sample : samples) {
        double abs = std::abs(sample);
        if (abs > peak) peak = abs;
        sumSq += sample * sample;
    }
    double rms = std::sqrt(sumSq / samples.size());

    double maxGain = NORMAL_STREAMING_AGC_MAX_GAIN;
    double minGain = STREAMING_AGC_MIN_GAIN;
    double targetLinear = dbfsToLinear(STREAMING_AGC_TARGET_RMS_DBFS) * static_cast<double>(INT16_MAX);
    double targetGain = 1.0;

    if (rms >= STREAMING_AGC_MIN_RMS) {
        targetGain = targetLinear / std::max(1.0, rms);
        targetGain = std::max(minGain, std::min(maxGain, targetGain));
    }

    double currentGain = (state->agcGain > 0.0) ? state->agcGain : 1.0;
    double alpha;
    if (rms < STREAMING_AGC_MIN_RMS) {
        alpha = STREAMING_AGC_IDLE_ALPHA;
        targetGain = 1.0;
    } else {
        alpha = (targetGain > currentGain)
                ? STREAMING_AGC_GAIN_UP_ALPHA
                : STREAMING_AGC_GAIN_DOWN_ALPHA;
    }
    currentGain = currentGain + ((targetGain - currentGain) * alpha);

    double peakCeiling = dbfsToLinear(PEAK_CEILING_DBFS) * static_cast<double>(INT16_MAX);
    if (peak > 1.0) {
        currentGain = std::min(currentGain, peakCeiling / peak);
    }
    currentGain = std::max(minGain, std::min(maxGain, currentGain));
    state->agcGain = currentGain;

    for (double& s : samples) {
        s *= currentGain;
    }
}

/* ------------------------------------------------------------------ */
/*  Main pipeline                                                      */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> prefilter_process(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    AudioPrefilterState* state)
{
    if (!pcm16le || bytes <= 0 || !state) {
        return {};
    }
    if (bytes < 4) {
        // Too small, return copy
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    std::vector<uint8_t> out(static_cast<size_t>(bytes));
    std::vector<double> working(samples);

    // Initialize notch state if sample rate changed
    if (state->notchSampleRateHz != sampleRateHz) {
        state->notchSampleRateHz = sampleRateHz;
        std::memset(state->notchX1, 0, sizeof(state->notchX1));
        std::memset(state->notchX2, 0, sizeof(state->notchX2));
        std::memset(state->notchY1, 0, sizeof(state->notchY1));
        std::memset(state->notchY2, 0, sizeof(state->notchY2));
    }

    // Stage 1: Hum notch filtering
    for (int i = 0; i < samples; i++) {
        double x = static_cast<double>(readPcm16le(pcm16le, i * 2));
        working[i] = applyHumNotches(x, sampleRateHz, state);
    }

    // Stage 2-5: HP -> pre-emphasis -> LP -> soft limit
    // Adaptive HP cutoff and pre-emphasis (matching Java filterVoiceBand for TALK+NORMAL)
    double hpCutoff = TALK_NORMAL_HP_CUTOFF_HZ;
    double preAlpha = TALK_NORMAL_PRE_EMPHASIS_ALPHA;

    // Analyze low-frequency periodic noise for adaptive filtering
    LowFrequencyPeriodicMetrics periodic = analyze_low_frequency_periodic(pcm16le, bytes, sampleRateHz);
    bool lowDominant = periodic.likelyMechanicalNoise() || periodic.lowToVoiceRatio >= 24.0;
    if (lowDominant) {
        hpCutoff = TALK_NORMAL_NOISY_HP_CUTOFF_HZ;
        preAlpha = TALK_NORMAL_NOISY_PRE_EMPHASIS_ALPHA;
    }

    double dt = 1.0 / sampleRateHz;

    // High-pass RC
    double hpRc = 1.0 / (2.0 * M_PI * hpCutoff);
    double hpAlpha = hpRc / (hpRc + dt);

    // Low-pass RC
    double lpRc = 1.0 / (2.0 * M_PI * DEFAULT_LP_CUTOFF_HZ);
    double lpAlpha = dt / (lpRc + dt);

    double hpPrevX = state->hpPrevX;
    double hpPrevY = state->hpPrevY;
    double prePrevX = state->prePrevX;
    double lpPrevY = state->lpPrevY;

    std::vector<double> shaped(samples);
    for (int i = 0; i < samples; i++) {
        double x = working[i];

        // High-pass (DC blocker)
        double hp = hpAlpha * (hpPrevY + x - hpPrevX);
        hpPrevX = x;
        hpPrevY = hp;

        // Pre-emphasis
        double voice = hp;
        if (preAlpha > 0.0) {
            double preInput = voice;
            voice = preInput - (preAlpha * prePrevX);
            prePrevX = preInput;
        }

        // Low-pass
        double y = lpPrevY + lpAlpha * (voice - lpPrevY);
        lpPrevY = y;

        shaped[i] = y;
    }

    // Stage 6: AGC
    applyStreamingAgc(shaped, state);

    // Stage 7: Soft limit + write output
    for (int i = 0; i < samples; i++) {
        int16_t v = static_cast<int16_t>(std::round(softLimit(shaped[i], NORMAL_LIMIT_DRIVE)));
        v = std::max(static_cast<int16_t>(INT16_MIN), std::min(static_cast<int16_t>(INT16_MAX), v));
        writePcm16le(out.data(), i * 2, v);
    }

    // Save state
    state->hpPrevX = hpPrevX;
    state->hpPrevY = hpPrevY;
    state->prePrevX = prePrevX;
    state->lpPrevY = lpPrevY;

    return out;
}

/* ------------------------------------------------------------------ */
/*  Standalone high-pass (RC filter, first-order)                      */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> apply_highpass(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    double cutoffHz)
{
    if (!pcm16le || bytes < 4 || sampleRateHz <= 0 || cutoffHz <= 0.0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    std::vector<uint8_t> out(static_cast<size_t>(bytes));

    double dt = 1.0 / sampleRateHz;
    double hpRc = 1.0 / (2.0 * M_PI * cutoffHz);
    double hpAlpha = hpRc / (hpRc + dt);

    double hpPrevX = 0.0;
    double hpPrevY = 0.0;

    for (int i = 0; i < samples; i++) {
        double x = static_cast<double>(readPcm16le(pcm16le, i * 2));
        double hp = hpAlpha * (hpPrevY + x - hpPrevX);
        hpPrevX = x;
        hpPrevY = hp;

        int16_t v = static_cast<int16_t>(std::clamp(hp, -32768.0, 32767.0));
        writePcm16le(out.data(), i * 2, v);
    }

    return out;
}

/* ------------------------------------------------------------------ */
/*  Slowdown (linear interpolation, matching Java slowdown)            */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> slowdown_audio(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    double factor)
{
    if (!pcm16le || bytes < 4 || sampleRateHz <= 0 || factor <= 1.0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    if (samples < 2) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // Read input samples
    std::vector<double> input(samples);
    for (int i = 0; i < samples; i++) {
        input[i] = static_cast<double>(readPcm16le(pcm16le, i * 2));
    }

    // Output has more samples (stretched by factor)
    int outSamples = static_cast<int>(std::round(samples * factor));
    std::vector<uint8_t> out(static_cast<size_t>(outSamples * 2));

    for (int i = 0; i < outSamples; i++) {
        // Map output sample to input position
        double pos = static_cast<double>(i) / factor;
        int idx = static_cast<int>(std::floor(pos));
        double frac = pos - idx;

        // Linear interpolation
        double val;
        if (idx >= samples - 1) {
            val = input[samples - 1];
        } else {
            val = input[idx] * (1.0 - frac) + input[idx + 1] * frac;
        }

        // Clamp to int16 range
        int16_t s = static_cast<int16_t>(std::clamp(val, -32768.0, 32767.0));
        out[i * 2] = static_cast<uint8_t>(s & 0xFF);
        out[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
    }

    return out;
}

/* ------------------------------------------------------------------ */
/*  Resample (linear interpolation, arbitrary ratio)                   */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> resample_audio(
    const uint8_t* pcm16le,
    int bytes,
    int srcSampleRateHz,
    int dstSampleRateHz)
{
    if (!pcm16le || bytes < 4 || srcSampleRateHz <= 0 || dstSampleRateHz <= 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // No resampling needed if rates match
    if (srcSampleRateHz == dstSampleRateHz) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    if (samples < 2) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // Read input samples
    std::vector<double> input(samples);
    for (int i = 0; i < samples; i++) {
        input[i] = static_cast<double>(readPcm16le(pcm16le, i * 2));
    }

    // Calculate output sample count
    double ratio = static_cast<double>(dstSampleRateHz) / static_cast<double>(srcSampleRateHz);
    int outSamples = static_cast<int>(std::round(samples * ratio));
    if (outSamples < 1) outSamples = 1;

    std::vector<uint8_t> out(static_cast<size_t>(outSamples * 2));

    for (int i = 0; i < outSamples; i++) {
        // Map output sample to input position
        double pos = static_cast<double>(i) / ratio;
        int idx = static_cast<int>(std::floor(pos));
        double frac = pos - idx;

        // Linear interpolation
        double val;
        if (idx >= samples - 1) {
            val = input[samples - 1];
        } else {
            val = input[idx] * (1.0 - frac) + input[idx + 1] * frac;
        }

        // Clamp to int16 range
        int16_t s = static_cast<int16_t>(std::clamp(val, -32768.0, 32767.0));
        out[i * 2] = static_cast<uint8_t>(s & 0xFF);
        out[i * 2 + 1] = static_cast<uint8_t>((s >> 8) & 0xFF);
    }

    return out;
}

/* ------------------------------------------------------------------ */
/*  Silence trimming (matching Java trimPcmSilence16le)               */
/* ------------------------------------------------------------------ */

static constexpr int TRIM_SHORT_MAX_MS = 900;
static constexpr int TRIM_MEDIUM_MAX_MS = 1800;
static constexpr int TRIM_SHORT_LEAD_PAD_MS = 160;
static constexpr int TRIM_MEDIUM_LEAD_PAD_MS = 280;
static constexpr int TRIM_LONG_LEAD_PAD_MS = 420;

std::vector<uint8_t> trim_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int absThreshold,
    int padMs)
{
    if (!pcm16le || bytes < 4 || sampleRateHz <= 0) {
        return {};
    }

    int samples = bytes / 2;
    if (samples < 2) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // Find first speech sample (matching V1 trimPcmSilence16le)
    int first = 0;
    while (first < samples) {
        int16_t s = readPcm16le(pcm16le, first * 2);
        if (std::abs(s) >= absThreshold) break;
        first++;
    }

    // All silence-ish
    if (first >= samples) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // Find last speech sample
    int last = samples - 1;
    while (last > first) {
        int16_t s = readPcm16le(pcm16le, last * 2);
        if (std::abs(s) >= absThreshold) break;
        last--;
    }

    if (last <= first) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // No trimming needed
    if (first == 0 && last == samples - 1) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // V1-style adaptive lead pad based on speech duration
    // MOONSHINE_TRIM_SHORT_MAX_MS = 900, LEAD = 160
    // MOONSHINE_TRIM_MEDIUM_MAX_MS = 1800, LEAD = 280
    // LONG: LEAD = 420
    long long speechMs = (static_cast<long long>(last - first + 1) * 1000LL) / std::max(1, sampleRateHz);
    int adaptiveLeadPadMs;
    if (speechMs <= 900LL) {
        adaptiveLeadPadMs = 160;
    } else if (speechMs <= 1800LL) {
        adaptiveLeadPadMs = 280;
    } else {
        adaptiveLeadPadMs = 420;
    }

    int tailPadSamples = std::max(0, (sampleRateHz * padMs) / 1000);
    int leadPadSamples = std::max(tailPadSamples, (sampleRateHz * adaptiveLeadPadMs) / 1000);

    int start = std::max(0, first - leadPadSamples);
    int end = std::min(samples - 1, last + tailPadSamples);

    int bStart = start * 2;
    int bEndExcl = (end + 1) * 2;

    // V1 safety: don't shrink to less than 4 bytes
    if (bEndExcl <= bStart + 4) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    // V1 safety: don't shrink too aggressively (keep at least ~120ms)
    int minBytes = (sampleRateHz * 120 / 1000) * 2;
    if ((bEndExcl - bStart) < minBytes && bytes >= minBytes) {
        std::vector<uint8_t> out(bytes);
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int outBytes = bEndExcl - bStart;
    std::vector<uint8_t> out(static_cast<size_t>(outBytes));
    std::memcpy(out.data(), pcm16le + bStart, static_cast<size_t>(outBytes));
    return out;
}

/* ------------------------------------------------------------------ */
/*  Append silence (matching Java appendSilence16le)                   */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> append_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int silenceMs)
{
    if (!pcm16le || bytes < 0 || sampleRateHz <= 0) {
        return {};
    }

    int ms = std::max(0, silenceMs);
    if (ms == 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int padSamples = static_cast<int>(std::round(sampleRateHz * (ms / 1000.0)));
    int padBytes = std::max(0, padSamples * 2);
    if (padBytes <= 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int totalBytes = bytes + padBytes;
    std::vector<uint8_t> out(static_cast<size_t>(totalBytes));
    std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
    std::memset(out.data() + bytes, 0, static_cast<size_t>(padBytes));
    return out;
}

/* ------------------------------------------------------------------ */
/*  Voice-like analysis (matching Java AudioPrefilter.analyzeVoiceLike) */
/* ------------------------------------------------------------------ */

VoiceMetrics analyze_voice_like(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz)
{
    VoiceMetrics metrics;

    if (!pcm16le || bytes < 4) {
        return metrics;
    }

    int sampleRate = std::max(8000, sampleRateHz);
    int samples = bytes / 2;
    if (samples <= 0) {
        return metrics;
    }

    long long sumSq = 0LL;
    long long bandSq = 0LL;
    int peak = 0;
    int zeroCrossings = 0;
    int16_t prev = 0;
    bool hasPrev = false;

    // Band-pass filter: HP 150Hz + LP 3800Hz (RC first-order)
    double hpPrevX = 0.0;
    double hpPrevY = 0.0;
    double lpPrevY = 0.0;
    double dt = 1.0 / static_cast<double>(sampleRate);
    double hpRc = 1.0 / (2.0 * M_PI * 150.0);
    double hpAlpha = hpRc / (hpRc + dt);
    double lpRc = 1.0 / (2.0 * M_PI * 3800.0);
    double lpAlpha = dt / (lpRc + dt);

    for (int i = 0; i + 1 < bytes; i += 2) {
        int16_t s = readPcm16le(pcm16le, i);
        int absVal = std::abs(static_cast<int>(s));
        if (absVal > peak) peak = absVal;
        sumSq += static_cast<long long>(s) * static_cast<long long>(s);

        if (hasPrev && ((s >= 0) != (prev >= 0))) {
            zeroCrossings++;
        }
        prev = s;
        hasPrev = true;

        // HP filter (150Hz)
        double x = static_cast<double>(s);
        double hp = hpAlpha * (hpPrevY + x - hpPrevX);
        hpPrevX = x;
        hpPrevY = hp;

        // LP filter (3800Hz) on HP output
        double band = lpPrevY + lpAlpha * (hp - lpPrevY);
        lpPrevY = band;
        int bandInt = static_cast<int>(std::round(band));
        bandSq += static_cast<long long>(bandInt) * static_cast<long long>(bandInt);
    }

    metrics.rms = std::sqrt(static_cast<double>(sumSq) / static_cast<double>(samples));
    metrics.zcr = (samples <= 1) ? 0.0 : static_cast<double>(zeroCrossings) / static_cast<double>(samples - 1);
    metrics.voiceBandRatio = (sumSq <= 0LL) ? 0.0 : std::sqrt(static_cast<double>(bandSq) / static_cast<double>(sumSq));
    metrics.peak = peak;

    return metrics;
}

/* ------------------------------------------------------------------ */
/*  Squash internal silence (matching Java squashInternalSilence16le) */
/* ------------------------------------------------------------------ */

std::vector<uint8_t> squash_internal_silence(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz,
    int absThreshold,
    int minSilenceMs,
    int keepMs)
{
    if (!pcm16le || bytes < 4 || absThreshold <= 0 || minSilenceMs <= 0 || keepMs < 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    if (samples < 2) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int minSamples = std::max(1, (sampleRateHz * minSilenceMs) / 1000);
    int keepSamples = std::max(0, (sampleRateHz * keepMs) / 1000);

    // Output buffer (worst case: same size as input)
    std::vector<uint8_t> out(static_cast<size_t>(bytes));
    int outPos = 0;

    int i = 0;
    while (i < samples) {
        // Find silent region
        int start = i;
        while (i < samples && std::abs(static_cast<int>(readPcm16le(pcm16le, i * 2))) < absThreshold) {
            i++;
        }
        int silent = i - start;
        if (silent >= minSamples) {
            // Compress this silence to keepSamples
            int copySamples = std::min(silent, keepSamples);
            int copyBytes = copySamples * 2;
            std::memcpy(out.data() + outPos, pcm16le + start * 2, static_cast<size_t>(copyBytes));
            outPos += copyBytes;
        } else if (silent > 0) {
            // Short silence, keep as-is
            int copyBytes = silent * 2;
            std::memcpy(out.data() + outPos, pcm16le + start * 2, static_cast<size_t>(copyBytes));
            outPos += copyBytes;
        }

        // Find speech region
        start = i;
        while (i < samples && std::abs(static_cast<int>(readPcm16le(pcm16le, i * 2))) >= absThreshold) {
            i++;
        }
        if (i > start) {
            int copyBytes = (i - start) * 2;
            std::memcpy(out.data() + outPos, pcm16le + start * 2, static_cast<size_t>(copyBytes));
            outPos += copyBytes;
        }
    }

    if (outPos >= 4) {
        out.resize(static_cast<size_t>(outPos));
        return out;
    }

    // Too short after squashing, return original
    std::vector<uint8_t> fallback(static_cast<size_t>(bytes));
    std::memcpy(fallback.data(), pcm16le, static_cast<size_t>(bytes));
    return fallback;
}

/* ------------------------------------------------------------------ */
/*  Normalize final chunk for ASR (matching Java normalizeFinalChunkForAsr) */
/* ------------------------------------------------------------------ */

static constexpr double NORMALIZE_TARGET_FINAL_RMS_DBFS = -21.0;
static constexpr double NORMALIZE_PEAK_CEILING_DBFS = -3.0;
static constexpr double NORMALIZE_MIN_RMS = 220.0;

std::vector<uint8_t> normalize_final_chunk_for_asr(
    const uint8_t* pcm16le,
    int bytes,
    double targetDbfs)
{
    if (!pcm16le || bytes < 4) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    int samples = bytes / 2;
    if (samples <= 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    long long sumSq = 0LL;
    int peak = 0;

    for (int i = 0; i + 1 < bytes; i += 2) {
        int16_t s = readPcm16le(pcm16le, i);
        int absVal = std::abs(static_cast<int>(s));
        if (absVal > peak) peak = absVal;
        sumSq += static_cast<long long>(s) * static_cast<long long>(s);
    }

    double rms = std::sqrt(static_cast<double>(sumSq) / static_cast<double>(samples));

    // Skip if RMS too low or no signal
    if (rms < NORMALIZE_MIN_RMS || peak <= 0) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    double targetLinear = dbfsToLinear(targetDbfs) * static_cast<double>(INT16_MAX);
    double ceilingLinear = dbfsToLinear(NORMALIZE_PEAK_CEILING_DBFS) * static_cast<double>(INT16_MAX);

    double gain = targetLinear / std::max(1.0, rms);
    gain = std::min(gain, ceilingLinear / std::max(1.0, static_cast<double>(peak)));

    // Skip if gain is too small (< 1.03) or not finite
    if (gain < 1.03 || !std::isfinite(gain)) {
        std::vector<uint8_t> out(static_cast<size_t>(bytes));
        std::memcpy(out.data(), pcm16le, static_cast<size_t>(bytes));
        return out;
    }

    std::vector<uint8_t> out(static_cast<size_t>(bytes));
    for (int i = 0; i + 1 < bytes; i += 2) {
        int16_t s = readPcm16le(pcm16le, i);
        int v = static_cast<int>(std::round(static_cast<double>(s) * gain));
        v = std::max(static_cast<int>(INT16_MIN), std::min(static_cast<int>(INT16_MAX), v));
        writePcm16le(out.data(), i, static_cast<int16_t>(v));
    }

    return out;
}

/* --- select_final_asr_tuning (matches Java AudioPrefilter.selectFinalAsrTuning) --- */

FinalAsrTuning select_final_asr_tuning(
    const uint8_t* pcm16le,
    int bytes,
    int sampleRateHz)
{
    int sampleRate = std::max(8000, sampleRateHz);
    int samples = (pcm16le == nullptr || bytes < 4) ? 0 : (bytes / 2);
    long long durationMs = (samples <= 0) ? 0LL
        : (static_cast<long long>(samples) * 1000LL) / sampleRate;

    VoiceMetrics metrics = analyze_voice_like(pcm16le, bytes, sampleRate);

    // Four tuning modes (priority order: verySoftVoice > lowMuffled/lowReadable > shortCommand > default)
    bool verySoftVoice = (metrics.peak > 0)
        && (metrics.peak <= 3200 || metrics.rms < 320.0);

    bool lowMuffledVoice = (durationMs >= 2000LL)
        && (metrics.rms >= 700.0 && metrics.rms <= 2100.0)
        && (metrics.peak >= 6000 && metrics.peak <= 24000)
        && (metrics.voiceBandRatio >= 0.55 && metrics.voiceBandRatio <= 0.82);

    bool lowReadableVoice = (durationMs >= 1800LL)
        && (metrics.rms >= 320.0 && metrics.rms < 700.0)
        && (metrics.peak <= 9000)
        && (metrics.voiceBandRatio >= 0.55 && metrics.voiceBandRatio <= 0.82);

    bool shortCommandLike = (durationMs > 0LL && durationMs <= 2200LL)
        && (metrics.peak >= 6000)
        && (metrics.rms >= 1200.0);

    if (verySoftVoice) {
        FinalAsrTuning t;
        t.id = "weak_voice_guard";
        t.trimAbsThreshold = 300; t.trimPadMs = 120;
        t.normalizeDbfs = -26.0;
        t.leadingPaddingMs = 300; t.tailPaddingMs = 300;
        t.slowDownRatio = 1.00;
        return t;
    }
    if (lowMuffledVoice || lowReadableVoice) {
        FinalAsrTuning t;
        t.id = "low_muffled_guard";
        t.trimAbsThreshold = 90; t.trimPadMs = 300;
        t.normalizeDbfs = -26.0;
        t.leadingPaddingMs = 520; t.tailPaddingMs = 820;
        t.slowDownRatio = 1.00;
        return t;
    }
    if (shortCommandLike) {
        FinalAsrTuning t;
        t.id = "short_command_guard";
        t.trimAbsThreshold = 180; t.trimPadMs = 420;
        t.normalizeDbfs = -23.4;
        t.leadingPaddingMs = 420; t.tailPaddingMs = 520;
        t.slowDownRatio = 1.00;
        return t;
    }
    FinalAsrTuning t;
    t.id = "default";
    t.trimAbsThreshold = 300; t.trimPadMs = 120;
    t.normalizeDbfs = -22.8;
    t.leadingPaddingMs = 300; t.tailPaddingMs = 300;
    t.slowDownRatio = 1.00;
    return t;
}

} // namespace mobmate
