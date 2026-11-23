namespace MobMate.Core.Tts;

/// <summary>
/// Analyzes text to detect emotional prosody profile.
/// Ported from V1 TtsProsodyAnalyzer.analyzeText().
/// </summary>
public static class TtsProsodyAnalyzer
{
    private const int PcmFrameMs = 20;
    private const int MinPcmMs = 180;
    private static readonly string[] TokSingSongRaw =
    { "♪", "〜", "～", "ちゃんちゃか", "ちゃかぽこ", "ララ", "랄라", "啦啦" };
    private static readonly string[] TokSingSongLower =
    { "la la", "lalala", "humming", "chant", "sing-song", "sing song" };
    private static readonly string[] TokWhisper =
    { "ひそひそ", "内緒", "小声", "そっと", "secret", "whisper", "悄悄", "小聲", "속삭" };
    private static readonly string[] TokExcitedRaw =
    { "!", "！", "笑", "草", "哈哈", "ㅋㅋ" };
    private static readonly string[] TokExcitedLower =
    { "lol", "haha", "yay", "wow", "omg" };
    private static readonly string[] TokQuestionRaw =
    { "かな", "ですか", "？", "吗", "嗎", "니", "냐" };
    private static readonly string[] TokQuestionLower =
    { "right?", "okay?", "really?", "is it", "do you", "are you" };
    private static readonly string[] TokCalm =
    { "ゆっくり", "落ち着", "calm", "slowly", "quietly", "静か", "천천히", "慢慢" };

    /// <summary>
    /// Analyzes text and returns a prosody profile.
    /// </summary>
    public static TtsProsodyProfile AnalyzeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TtsProsodyProfile.Neutral;

        string raw = text.Trim();
        string lower = raw.ToLowerInvariant();

        if (LooksLikeSingSong(raw, lower))
            return new TtsProsodyProfile("sing_song_lite", 0.09f, 1.30f, 1.00f, 1.03f, 0.84f);

        if (ContainsAny(raw, TokWhisper) || raw.Contains("…") || lower.Contains("..."))
            return new TtsProsodyProfile("whisper", -0.03f, 0.86f, 0.94f, 0.90f, 0.72f);

        if (IsQuestion(raw, lower))
            return new TtsProsodyProfile("question", 0.02f, 1.08f, 1.00f, 1.00f, 0.66f);

        if (ContainsAny(lower, TokExcitedLower) || ContainsAny(raw, TokExcitedRaw))
            return new TtsProsodyProfile("excited", 0.06f, 1.18f, 1.06f, 1.04f, 0.70f);

        if (ContainsAny(raw, TokCalm) || raw.Contains("…") || lower.Contains("..."))
            return new TtsProsodyProfile("calm", -0.01f, 0.92f, 0.97f, 0.98f, 0.58f);

        return TtsProsodyProfile.Neutral;
    }

    public static TtsProsodyProfile AnalyzeTextAndPcm(string text, byte[]? pcm16le, int sampleRate)
    {
        var textProfile = AnalyzeText(text);
        var pcmProfile = AnalyzePcm(pcm16le, sampleRate);
        if (pcmProfile.HasPitchContour()) return pcmProfile.BlendWith(textProfile);
        if (!textProfile.IsConfident()) return pcmProfile;
        if (!pcmProfile.IsConfident()) return textProfile;
        if (textProfile.Mood == "question") return textProfile.BlendWith(pcmProfile);
        if (pcmProfile.Mood == "sing_song_lite") return pcmProfile.BlendWith(textProfile);
        return textProfile.BlendWith(pcmProfile);
    }

    public static TtsProsodyProfile AnalyzePcm(byte[]? pcm16le, int sampleRate)
    {
        if (pcm16le == null || pcm16le.Length < 4 || sampleRate <= 0) return TtsProsodyProfile.Neutral;
        int totalSamples = pcm16le.Length / 2;
        if (totalSamples < sampleRate * MinPcmMs / 1000) return TtsProsodyProfile.Neutral;

        short[] samples = DecodePcm16Le(pcm16le);
        int frameSamples = Math.Max(80, sampleRate * PcmFrameMs / 1000);
        int frameCount = totalSamples / frameSamples;
        if (frameCount < 4) return TtsProsodyProfile.Neutral;

        float[] rmsFrames = new float[frameCount];
        float[] zcrFrames = new float[frameCount];
        float peakAbs = 0f;
        for (int f = 0; f < frameCount; f++)
        {
            int start = f * frameSamples;
            double sumSq = 0;
            int zeroCross = 0;
            int prev = 0;
            for (int i = 0; i < frameSamples; i++)
            {
                int sample = samples[start + i];
                if (i > 0 && ((sample >= 0 && prev < 0) || (sample < 0 && prev >= 0))) zeroCross++;
                prev = sample;
                int abs = Math.Abs(sample);
                if (abs > peakAbs) peakAbs = abs;
                sumSq += sample * sample;
            }
            rmsFrames[f] = (float)Math.Sqrt(sumSq / frameSamples);
            zcrFrames[f] = zeroCross / (float)frameSamples;
        }

        float voicedThreshold = Math.Max(140f, peakAbs * 0.10f);
        int voicedStart = -1, voicedEnd = -1;
        for (int i = 0; i < frameCount; i++)
        {
            if (rmsFrames[i] >= voicedThreshold)
            {
                if (voicedStart < 0) voicedStart = i;
                voicedEnd = i;
            }
        }
        if (voicedStart < 0 || voicedEnd - voicedStart + 1 < 3) return TtsProsodyProfile.Neutral;

        float[] pitchContour = EstimatePitchContour(samples, sampleRate, frameSamples, rmsFrames, voicedStart, voicedEnd, voicedThreshold);
        float[] activityContour = EstimateActivityContour(rmsFrames, MeanOrZero(rmsFrames));
        float[] voicingContour = EstimateVoicingContour(rmsFrames, voicedThreshold);
        int voicedFrames = voicedEnd - voicedStart + 1;
        int headFrames = Math.Max(2, voicedFrames / 3);
        int tailFrames = Math.Max(2, voicedFrames / 4);
        float rmsSum = 0, zcrSum = 0, maxRms = 0, headSum = 0, tailSum = 0;
        int oscillation = 0;
        float prevDelta = 0;
        for (int i = voicedStart; i <= voicedEnd; i++)
        {
            float rms = rmsFrames[i];
            rmsSum += rms;
            zcrSum += zcrFrames[i];
            if (rms > maxRms) maxRms = rms;
            int rel = i - voicedStart;
            if (rel < headFrames) headSum += rms;
            if (rel >= voicedFrames - tailFrames) tailSum += rms;
            if (i > voicedStart)
            {
                float delta = rmsFrames[i] - rmsFrames[i - 1];
                if (Math.Abs(delta) > 120f && Math.Sign(prevDelta) != 0 && Math.Sign(prevDelta) != Math.Sign(delta)) oscillation++;
                if (Math.Abs(delta) > 50f) prevDelta = delta;
            }
        }

        float meanRms = rmsSum / voicedFrames;
        float rmsVar = 0;
        for (int i = voicedStart; i <= voicedEnd; i++)
        {
            float delta = rmsFrames[i] - meanRms;
            rmsVar += delta * delta;
        }
        float rmsStd = (float)Math.Sqrt(rmsVar / voicedFrames);
        float cv = meanRms <= 1f ? 0f : rmsStd / meanRms;
        float tailRise = (tailSum / tailFrames) / Math.Max(1f, headSum / headFrames);
        float contourRise = ClampSigned((tailRise - 1.0f) / 0.35f);
        float melodyDepth = Clamp01((((cv - 0.10f) / 0.45f) * 0.55f) + (oscillation / (float)Math.Max(3, voicedFrames)) * 0.45f);
        if (pitchContour.Length >= 3)
        {
            contourRise = BlendContourRise(contourRise, ContourRiseFromPitchContour(pitchContour));
            melodyDepth = Math.Max(melodyDepth, ContourMelodyDepth(pitchContour));
        }
        if (contourRise <= -0.40f) melodyDepth *= 0.72f;
        float contourFallScore = Clamp01(((-contourRise) - 0.12f) / 0.70f);
        float meanZcr = zcrSum / voicedFrames;
        float darkScore = Clamp01(((0.82f - Math.Min(0.82f, tailRise)) / 0.42f) * 0.45f + ((0.070f - Math.Min(0.070f, meanZcr)) / 0.070f) * 0.25f + contourFallScore * 0.55f);
        float energyScore = Clamp01(((maxRms - 900f) / 2800f) * 0.62f + ((meanRms - 520f) / 2000f) * 0.28f + Math.Max(0.0f, contourRise) * 0.12f);
        float pitchScale = PitchScaleFromPitchContour(pitchContour, contourRise, darkScore, energyScore);

        TtsProsodyProfile profile;
        if (melodyDepth >= 0.58f && energyScore >= 0.38f)
            profile = new TtsProsodyProfile("sing_song_lite", Math.Max(0.05f, pitchScale), 1.14f, 1.00f, 1.02f, 0.72f, contourRise, Math.Max(0.58f, melodyDepth), darkScore * 0.35f, energyScore);
        else if (darkScore >= 0.62f && energyScore <= 0.62f && (meanZcr >= 0.060f || tailRise < 0.88f || contourRise <= -0.22f))
            profile = new TtsProsodyProfile("whisper", Math.Min(-0.04f, pitchScale - 0.02f), 0.82f, 0.94f, 0.99f, 0.62f, Math.Min(contourRise, 0.0f), melodyDepth * 0.25f, Math.Max(0.62f, darkScore), energyScore * 0.45f);
        else if (darkScore >= 0.46f && energyScore <= 0.48f && contourRise <= 0.10f)
            profile = new TtsProsodyProfile("calm", Math.Min(-0.02f, pitchScale), 0.88f, 0.96f, 1.00f, 0.60f, Math.Min(contourRise, 0.0f), melodyDepth * 0.20f, Math.Max(0.48f, darkScore), energyScore * 0.55f);
        else if (contourRise >= 0.18f && energyScore <= 0.75f)
            profile = new TtsProsodyProfile("question", Math.Max(0.01f, pitchScale), 1.04f, 1.00f, 1.00f, 0.54f, contourRise, melodyDepth * 0.30f, darkScore * 0.20f, energyScore);
        else if (energyScore >= 0.48f && melodyDepth >= 0.22f && contourRise > -0.18f)
            profile = new TtsProsodyProfile("excited", Math.Max(0.02f, pitchScale + 0.01f), 1.03f, 1.03f, 1.02f, 0.52f, contourRise, melodyDepth, darkScore * 0.18f, Math.Max(0.55f, energyScore));
        else if (melodyDepth >= 0.52f && energyScore >= 0.36f && cv >= 0.16f)
            profile = new TtsProsodyProfile("sing_song_lite", pitchScale, 1.03f, 1.00f, 1.01f, 0.44f, contourRise, melodyDepth, darkScore * 0.20f, energyScore);
        else
        {
            float neutralConf = Math.Abs(contourRise) >= 0.20f || melodyDepth >= 0.20f || darkScore >= 0.24f || energyScore >= 0.24f ? 0.34f : 0.0f;
            profile = new TtsProsodyProfile("neutral", pitchScale * 0.85f, 1.0f, 1.0f, 1.0f, neutralConf, contourRise, melodyDepth, darkScore, energyScore);
        }

        return new TtsProsodyProfile(profile.Mood, profile.PitchScale, profile.IntonationScale, profile.SpeedScale, profile.VolumeScale, profile.Confidence,
            profile.ContourRise, profile.MelodyDepth, profile.DarkScore, profile.EnergyScore, pitchContour, activityContour, voicingContour, PcmFrameMs);
    }

    private static bool LooksLikeSingSong(string raw, string lower)
    {
        return ContainsAny(raw, TokSingSongRaw) || ContainsAny(lower, TokSingSongLower);
    }

    private static bool IsQuestion(string raw, string lower)
    {
        return ContainsAny(raw, TokQuestionRaw) || ContainsAny(lower, TokQuestionLower);
    }

    private static bool ContainsAny(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (!string.IsNullOrEmpty(token) && text.Contains(token))
                return true;
        }
        return false;
    }

    private static short[] DecodePcm16Le(byte[] pcm16le)
    {
        short[] samples = new short[pcm16le.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm16le[i * 2] | (pcm16le[i * 2 + 1] << 8));
        return samples;
    }

    private static float[] EstimateActivityContour(float[] rmsFrames, float meanRms)
    {
        float denom = Math.Max(120.0f, meanRms * 1.15f);
        float[] contour = new float[rmsFrames.Length];
        for (int i = 0; i < rmsFrames.Length; i++) contour[i] = Clamp01(rmsFrames[i] / denom);
        SmoothContour(contour);
        return contour;
    }

    private static float[] EstimateVoicingContour(float[] rmsFrames, float voicedThreshold)
    {
        float lo = voicedThreshold * 0.70f, hi = voicedThreshold * 1.05f, span = Math.Max(1.0f, hi - lo);
        float[] contour = new float[rmsFrames.Length];
        for (int i = 0; i < rmsFrames.Length; i++) contour[i] = Clamp01((rmsFrames[i] - lo) / span);
        SmoothContour(contour);
        return contour;
    }

    private static float[] EstimatePitchContour(short[] samples, int sampleRate, int frameSamples, float[] rmsFrames, int voicedStart, int voicedEnd, float voicedThreshold)
    {
        int voicedFrames = voicedEnd - voicedStart + 1;
        if (voicedFrames < 3) return Array.Empty<float>();
        int analysisSamples = Math.Max(frameSamples * 2, sampleRate / 40);
        int minLag = Math.Max(18, sampleRate / 360);
        int maxLag = Math.Min(Math.Max(minLag + 4, analysisSamples / 2), sampleRate / 85);
        float[] hzFrames = Enumerable.Repeat(float.NaN, voicedFrames).ToArray();
        int accepted = 0;
        for (int i = voicedStart; i <= voicedEnd; i++)
        {
            int rel = i - voicedStart;
            if (rmsFrames[i] < voicedThreshold * 0.85f) continue;
            int start = i * frameSamples;
            int len = Math.Min(analysisSamples, samples.Length - start);
            if (len <= maxLag + 8) continue;
            float f0 = EstimateFramePitchHz(samples, start, len, sampleRate, minLag, maxLag);
            if (f0 > 0f) { hzFrames[rel] = f0; accepted++; }
        }
        if (accepted < 3) return Array.Empty<float>();
        float[] voicedHz = hzFrames.Where(v => !float.IsNaN(v)).ToArray();
        float medianHz = Median(voicedHz);
        if (!(medianHz > 0f)) return Array.Empty<float>();
        float[] contour = Enumerable.Repeat(float.NaN, hzFrames.Length).ToArray();
        for (int i = 0; i < hzFrames.Length; i++)
        {
            if (!float.IsNaN(hzFrames[i]))
            {
                float octaveDelta = (float)(Math.Log(hzFrames[i] / medianHz) / Math.Log(2.0d));
                contour[i] = Math.Clamp(octaveDelta * 12.0f, -4.0f, 4.0f);
            }
        }
        InterpolateContourGaps(contour);
        SmoothContour(contour);
        return contour;
    }

    private static float EstimateFramePitchHz(short[] samples, int start, int len, int sampleRate, int minLag, int maxLag)
    {
        double mean = 0;
        for (int i = 0; i < len; i++) mean += samples[start + i];
        mean /= Math.Max(1, len);
        double bestScore = 0;
        int bestLag = -1;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double num = 0, denA = 1e-9, denB = 1e-9;
            for (int i = 0; i < len - lag; i++)
            {
                double a = samples[start + i] - mean;
                double b = samples[start + i + lag] - mean;
                num += a * b; denA += a * a; denB += b * b;
            }
            double score = num / Math.Sqrt(denA * denB);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        return bestLag <= 0 || bestScore < 0.58d ? -1.0f : sampleRate / (float)bestLag;
    }

    private static void InterpolateContourGaps(float[] contour)
    {
        int first = Array.FindIndex(contour, v => !float.IsNaN(v));
        if (first < 0) return;
        for (int i = 0; i < first; i++) contour[i] = contour[first];
        int prev = first;
        for (int i = first + 1; i < contour.Length; i++)
        {
            if (!float.IsNaN(contour[i]))
            {
                if (i - prev > 1)
                {
                    float a = contour[prev], b = contour[i]; int gap = i - prev;
                    for (int j = 1; j < gap; j++) contour[prev + j] = a + ((b - a) * (j / (float)gap));
                }
                prev = i;
            }
        }
        for (int i = prev + 1; i < contour.Length; i++) contour[i] = contour[prev];
    }

    private static void SmoothContour(float[] contour)
    {
        if (contour.Length < 3) return;
        float[] copy = (float[])contour.Clone();
        for (int i = 1; i < contour.Length - 1; i++) contour[i] = (copy[i - 1] * 0.25f) + (copy[i] * 0.50f) + (copy[i + 1] * 0.25f);
    }

    private static float ContourRiseFromPitchContour(float[] contour)
    {
        if (contour.Length < 3) return 0;
        int span = Math.Max(1, contour.Length / 3);
        float head = 0, tail = 0;
        for (int i = 0; i < span; i++) head += contour[i];
        for (int i = contour.Length - span; i < contour.Length; i++) tail += contour[i];
        return ClampSigned(((tail / span) - (head / span)) / 1.6f);
    }

    private static float ContourMelodyDepth(float[] contour)
    {
        if (contour.Length < 3) return 0;
        float mean = contour.Average();
        float var = 0, maxAbs = 0;
        foreach (float v in contour) { float d = v - mean; var += d * d; maxAbs = Math.Max(maxAbs, Math.Abs(v)); }
        float std = (float)Math.Sqrt(var / contour.Length);
        return Clamp01((std / 1.05f) * 0.78f + (maxAbs / 2.8f) * 0.22f);
    }

    private static float PitchScaleFromPitchContour(float[] contour, float contourRise, float darkScore, float energyScore)
    {
        float contourMean = 0, contourSpan = 0;
        if (contour.Length >= 3) { contourMean = contour.Average(); contourSpan = contour.Max() - contour.Min(); }
        float meanBias = ClampSigned(contourMean / 2.4f) * 0.12f;
        float spanBias = ClampSigned((contourSpan - 0.55f) / 1.9f) * 0.05f;
        float riseBias = ClampSigned(contourRise) * 0.02f;
        float toneBias = ClampSigned((energyScore - darkScore) * 1.9f) * 0.08f;
        float darkBias = ClampSigned((0.24f - darkScore) * 2.1f) * 0.03f;
        return Math.Clamp(meanBias + spanBias + riseBias + toneBias + darkBias, -0.18f, 0.18f);
    }

    private static float BlendContourRise(float fallbackRise, float pitchRise) => Math.Abs(pitchRise) < 0.05f ? fallbackRise : ClampSigned((fallbackRise * 0.30f) + (pitchRise * 0.70f));
    private static float MeanOrZero(float[] values) => values.Length == 0 ? 0 : values.Average();
    private static float ClampSigned(float v) => Math.Clamp(v, -1.0f, 1.0f);
    private static float Clamp01(float v) => Math.Clamp(v, 0.0f, 1.0f);
    private static float Median(float[] values) { Array.Sort(values); int mid = values.Length / 2; return (values.Length & 1) == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5f; }
}

/// <summary>
/// Prosody profile derived from text or audio analysis.
/// </summary>
public readonly struct TtsProsodyProfile
{
    public static readonly TtsProsodyProfile Neutral = new("neutral", 0.0f, 1.0f, 1.0f, 1.0f, 0.0f);

    public string Mood { get; }
    public float PitchScale { get; }
    public float IntonationScale { get; }
    public float SpeedScale { get; }
    public float VolumeScale { get; }
    public float Confidence { get; }
    public float ContourRise { get; }
    public float MelodyDepth { get; }
    public float DarkScore { get; }
    public float EnergyScore { get; }
    public float[] PitchContour { get; }
    public float[] ActivityContour { get; }
    public float[] VoicingContour { get; }
    public int ContourStepMs { get; }

    public TtsProsodyProfile(string mood, float pitchScale, float intonationScale, float speedScale, float volumeScale, float confidence)
        : this(mood, pitchScale, intonationScale, speedScale, volumeScale, confidence,
            0.0f, 0.0f, 0.0f, 0.0f, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), 0)
    {
    }

    public TtsProsodyProfile(string mood, float pitchScale, float intonationScale, float speedScale, float volumeScale, float confidence, float contourRise, float melodyDepth)
        : this(mood, pitchScale, intonationScale, speedScale, volumeScale, confidence,
            contourRise, melodyDepth, 0.0f, 0.0f, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), 0)
    {
    }

    public TtsProsodyProfile(string mood, float pitchScale, float intonationScale, float speedScale, float volumeScale, float confidence,
        float contourRise, float melodyDepth, float darkScore, float energyScore)
        : this(mood, pitchScale, intonationScale, speedScale, volumeScale, confidence,
            contourRise, melodyDepth, darkScore, energyScore, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), 0)
    {
    }

    public TtsProsodyProfile(string mood, float pitchScale, float intonationScale, float speedScale, float volumeScale, float confidence,
        float contourRise, float melodyDepth, float darkScore, float energyScore,
        float[]? pitchContour, float[]? activityContour, float[]? voicingContour, int contourStepMs)
    {
        Mood = mood;
        PitchScale = pitchScale;
        IntonationScale = intonationScale;
        SpeedScale = speedScale;
        VolumeScale = volumeScale;
        Confidence = confidence;
        ContourRise = contourRise;
        MelodyDepth = melodyDepth;
        DarkScore = darkScore;
        EnergyScore = energyScore;
        PitchContour = pitchContour == null || pitchContour.Length == 0 ? Array.Empty<float>() : (float[])pitchContour.Clone();
        ActivityContour = activityContour == null || activityContour.Length == 0 ? Array.Empty<float>() : (float[])activityContour.Clone();
        VoicingContour = voicingContour == null || voicingContour.Length == 0 ? Array.Empty<float>() : (float[])voicingContour.Clone();
        ContourStepMs = Math.Max(0, contourStepMs);
    }

    public bool IsUsable() => Confidence >= 0.25f || HasContinuousHint();
    public bool IsConfident() => Confidence >= 0.55f;
    public bool HasPitchContour() => PitchContour.Length >= 3;
    public bool HasTimingContour() => ActivityContour.Length >= 3 || VoicingContour.Length >= 3;
    public bool IsDarkLike() => Mood == "whisper" || Mood == "calm" || DarkScore >= 0.38f;
    public bool IsMelodicLike() => Mood == "sing_song_lite" || MelodyDepth >= 0.45f || HasPitchContour();

    public bool HasContinuousHint()
    {
        return Math.Abs(ContourRise) >= 0.08f
            || MelodyDepth >= 0.12f
            || DarkScore >= 0.12f
            || EnergyScore >= 0.12f
            || HasPitchContour()
            || HasTimingContour();
    }

    public TtsProsodyProfile BlendWith(TtsProsodyProfile other)
    {
        if (!IsUsable()) return other;
        if (!other.IsUsable()) return this;
        string mergedMood = other.Confidence > Confidence + 0.08f ? other.Mood : Mood;
        float total = Math.Max(0.001f, Confidence + other.Confidence);
        float wThis = Confidence / total;
        float wOther = other.Confidence / total;
        return new TtsProsodyProfile(
            mergedMood,
            (PitchScale * wThis) + (other.PitchScale * wOther),
            (IntonationScale * wThis) + (other.IntonationScale * wOther),
            (SpeedScale * wThis) + (other.SpeedScale * wOther),
            (VolumeScale * wThis) + (other.VolumeScale * wOther),
            Math.Max(Confidence, other.Confidence),
            (ContourRise * wThis) + (other.ContourRise * wOther),
            (MelodyDepth * wThis) + (other.MelodyDepth * wOther),
            (DarkScore * wThis) + (other.DarkScore * wOther),
            (EnergyScore * wThis) + (other.EnergyScore * wOther),
            SelectContour(this, other),
            SelectTimingContour(ActivityContour, other.ActivityContour),
            SelectTimingContour(VoicingContour, other.VoicingContour),
            Math.Max(ContourStepMs, other.ContourStepMs));
    }

    public TtsProsodyProfile AsContourDriven()
    {
        if (!HasPitchContour()) return this;
        return new TtsProsodyProfile("contour_transfer", 0.0f, 1.0f, 1.0f, 1.0f,
            Math.Max(0.72f, Confidence), ContourRise, MelodyDepth, DarkScore, EnergyScore,
            PitchContour, ActivityContour, VoicingContour, ContourStepMs);
    }

    private static float[] SelectContour(TtsProsodyProfile a, TtsProsodyProfile b)
    {
        bool aHas = a.HasPitchContour();
        bool bHas = b.HasPitchContour();
        if (aHas && bHas)
        {
            float aScore = (a.PitchContour.Length * 0.5f) + (a.Confidence * 2.0f) + a.MelodyDepth;
            float bScore = (b.PitchContour.Length * 0.5f) + (b.Confidence * 2.0f) + b.MelodyDepth;
            return aScore >= bScore ? a.PitchContour : b.PitchContour;
        }
        if (aHas) return a.PitchContour;
        if (bHas) return b.PitchContour;
        return Array.Empty<float>();
    }

    private static float[] SelectTimingContour(float[] a, float[] b)
    {
        bool aHas = a.Length >= 3;
        bool bHas = b.Length >= 3;
        if (aHas && bHas) return a.Length >= b.Length ? a : b;
        if (aHas) return a;
        if (bHas) return b;
        return Array.Empty<float>();
    }
}
