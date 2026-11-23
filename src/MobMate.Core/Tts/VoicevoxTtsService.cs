namespace MobMate.Core.Tts;

/// <summary>
/// VOICEVOX TTS engine via localhost HTTP API.
/// Only accepts localhost URLs (http://127.0.0.1:<port> or http://localhost:<port>).
/// Supports emotion reflection via TtsProsodyAnalyzer.
/// </summary>
public sealed class VoicevoxTtsService
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None
    });

    private string _apiUrl;
    private int _speakerId;
    private bool _autoEmotion;
    private bool _reflectEmotion;
    private string _contourStrength = "normal";
    private string _toneEmphasis = "normal";

    public VoicevoxTtsService(string apiUrl, int speakerId = 1, bool autoEmotion = true)
    {
        if (!IsLocalhostUrl(apiUrl))
            throw new ArgumentException("VOICEVOX API must be localhost", nameof(apiUrl));

        _apiUrl = apiUrl.TrimEnd('/');
        _speakerId = speakerId;
        _autoEmotion = autoEmotion;
        _reflectEmotion = true;
    }

    public void SetConfig(
        string? apiUrl = null,
        int? speakerId = null,
        bool? autoEmotion = null,
        bool? reflectEmotion = null,
        string? contourStrength = null,
        string? toneEmphasis = null)
    {
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            if (!IsLocalhostUrl(apiUrl))
                throw new ArgumentException("VOICEVOX API must be localhost");
            _apiUrl = apiUrl.TrimEnd('/');
        }
        if (speakerId.HasValue) _speakerId = speakerId.Value;
        if (autoEmotion.HasValue) _autoEmotion = autoEmotion.Value;
        if (reflectEmotion.HasValue) _reflectEmotion = reflectEmotion.Value;
        if (contourStrength != null && (contourStrength == "mild" || contourStrength == "normal" || contourStrength == "strong"))
            _contourStrength = contourStrength;
        if (toneEmphasis != null && (toneEmphasis == "mild" || toneEmphasis == "normal" || toneEmphasis == "strong"))
            _toneEmphasis = toneEmphasis;
    }

    /// <summary>
    /// Synthesizes text to WAV bytes via VOICEVOX API.
    /// </summary>
    public async Task<VoicevoxResult> SynthesizeAsync(string text, int? speakerId = null, CancellationToken ct = default, TtsProsodyProfile? prosodyProfile = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return VoicevoxResult.Fail("Empty text");

        try
        {
            int effectiveSpeaker = speakerId ?? _speakerId;
            // Step 1: Get audio query (VOICEVOX requires POST for /audio_query)
            var queryUrl = $"{_apiUrl}/audio_query?speaker={effectiveSpeaker}&text={Uri.EscapeDataString(text)}";
            using var emptyContent = new StringContent("", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var queryResponse = await _httpClient.PostAsync(queryUrl, emptyContent, ct);
            if (!queryResponse.IsSuccessStatusCode)
            {
                var errBody = await queryResponse.Content.ReadAsStringAsync(ct);
                return VoicevoxResult.Fail($"Audio query failed: {queryResponse.StatusCode} {errBody}");
            }

            var queryJson = await queryResponse.Content.ReadAsStringAsync(ct);

            // Step 2: Apply emotion reflection if enabled
            if (_reflectEmotion)
            {
                queryJson = ApplyEmotionReflection(text, queryJson, prosodyProfile);
            }

            // Step 3: Synthesize
            var synthesizeUrl = $"{_apiUrl}/synthesis?speaker={effectiveSpeaker}";
            var queryContent = new StringContent(queryJson, System.Text.Encoding.UTF8, "application/json");
            using var synthesizeResponse = await _httpClient.PostAsync(synthesizeUrl, queryContent, ct);
            if (!synthesizeResponse.IsSuccessStatusCode)
            {
                var errBody = await synthesizeResponse.Content.ReadAsStringAsync(ct);
                return VoicevoxResult.Fail($"Synthesis failed: {synthesizeResponse.StatusCode} {errBody}");
            }

            var wavBytes = await synthesizeResponse.Content.ReadAsByteArrayAsync(ct);
            return VoicevoxResult.Ok(wavBytes);
        }
        catch (OperationCanceledException)
        {
            return VoicevoxResult.Fail("Cancelled");
        }
        catch (Exception ex)
        {
            return VoicevoxResult.Fail($"VOICEVOX error: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies emotion reflection to VOICEVOX audio query JSON.
    /// Uses TtsProsodyAnalyzer to detect text emotion, then applies
    /// strength multipliers from contourStrength and toneEmphasis settings.
    /// </summary>
    private string ApplyEmotionReflection(string text, string queryJson, TtsProsodyProfile? sourceProsodyProfile = null)
    {
        var profile = sourceProsodyProfile?.BlendWith(TtsProsodyAnalyzer.AnalyzeText(text)) ?? TtsProsodyAnalyzer.AnalyzeText(text);

        if (profile.HasPitchContour() && profile.Mood != "contour_transfer")
            profile = profile.AsContourDriven();
        profile = SoftenVoiceVoxBorderlineUpbeatProfile(profile);

        if (!profile.IsUsable())
            return queryJson;

        bool contourDriven = profile.HasPitchContour();
        float contourPref = GetVoiceVoxContourReflectionStrength(_contourStrength);
        float tonePref = GetVoiceVoxToneEmphasisStrength(_toneEmphasis);
        float weight = contourDriven ? Math.Max(0.88f, ProsodyWeight(profile.Confidence)) : ProsodyWeight(profile.Confidence);
        float contour = profile.ContourRise * weight;
        float melodyDepth = profile.MelodyDepth * weight;
        float darkDepth = profile.DarkScore * weight;
        float energyDepth = profile.EnergyScore * weight;
        float contourDarkAssist = contour < -0.16f ? Math.Clamp(((-contour) - 0.16f) * 0.55f, 0.0f, 0.35f) : 0.0f;
        float rawDarkTone = Math.Max(darkDepth, contourDarkAssist);
        float rawBrightTone = Math.Max(0.0f, Math.Max(contour, energyDepth - 0.18f));
        float toneBalance = rawDarkTone + rawBrightTone + 0.0001f;
        float darkWeightLocal = rawDarkTone / toneBalance;
        float strongToneBoost = Math.Clamp(tonePref / 1.10f, 0.85f, 1.60f);
        float darkBlendGate = Clamp01((((rawDarkTone - 0.12f) / 0.42f) * 0.62f)
            + (((-Math.Min(contour, 0.0f)) - 0.08f) / 0.55f * 0.38f));
        float brightSuppression = Clamp01(darkBlendGate * (0.30f + (0.18f * strongToneBoost)));
        float brightTone = rawBrightTone * (1.0f - brightSuppression);
        float darkTone = Clamp01(rawDarkTone * (1.0f + ((darkWeightLocal * 0.22f) + (darkBlendGate * 0.16f)) * strongToneBoost));

        float pitchTarget = contourDriven
            ? ((brightTone * 0.0032f) - (darkTone * 0.032f)) * tonePref
            : profile.PitchScale + (Math.Max(0.0f, contour) * 0.015f * tonePref) - (darkTone * 0.032f * tonePref);
        float intonationTarget = contourDriven
            ? 1.0f + ((melodyDepth * 0.026f) + (brightTone * 0.006f) - (darkTone * 0.112f)) * tonePref
            : profile.IntonationScale + (melodyDepth * 0.09f * tonePref) + (Math.Max(0.0f, contour) * 0.03f * tonePref) - (darkTone * 0.062f * tonePref);

        var json = queryJson;
        json = PatchJsonFloat(json, "pitchScale", Math.Clamp(ScaleAroundNeutral(0.0f, pitchTarget, weight), -0.20f, 0.20f));
        json = PatchJsonFloat(json, "intonationScale", Math.Clamp(ScaleAroundNeutral(1.0f, intonationTarget, weight), 0.75f, 1.45f));
        json = PatchJsonFloat(json, "speedScale", Math.Clamp(ScaleAroundNeutral(1.0f, profile.SpeedScale, weight), 0.72f, 1.25f));
        json = PatchJsonFloat(json, "volumeScale", Math.Clamp(ScaleAroundNeutral(1.0f, profile.VolumeScale, weight), 0.78f, 1.25f));
        json = ApplyVoiceVoxMoraMelodyIfNeeded(json, profile, weight, contourPref);

        return json;
    }

    /// <summary>
    /// Applies lightweight mora-level pitch contour to reproduce question rises,
    /// excited/sing-song movement, and calm/whisper flattening. Keeps VOICEVOX
    /// accent data intact and only nudges existing positive mora pitch values.
    /// </summary>
    private static string ApplyVoiceVoxMoraMelodyIfNeeded(string queryJson, TtsProsodyProfile profile, float weight, float contourPref)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(queryJson) as System.Text.Json.Nodes.JsonObject;
            var phrases = root?["accent_phrases"] as System.Text.Json.Nodes.JsonArray;
            if (root == null || phrases == null)
                return queryJson;

            var voicedMoras = new List<System.Text.Json.Nodes.JsonObject>();
            foreach (var phrase in phrases)
            {
                if (phrase?["moras"] is not System.Text.Json.Nodes.JsonArray phraseMoras)
                    continue;
                foreach (var mora in phraseMoras)
                {
                    if (mora is System.Text.Json.Nodes.JsonObject moraObj && moraObj["pitch"] != null)
                    {
                        float pitch = (float)GetJsonDouble(moraObj["pitch"]);
                        if (pitch > 0.0f)
                            voicedMoras.Add(moraObj);
                    }
                }
            }

            int voicedCount = voicedMoras.Count;
            // 2-mora fillers (あー/なー) still carry rise/fall intonation that is the
            // core differentiation point; allow contour transfer down to 2 voiced moras.
            // Single-mora has no span for movement, so keep the >=2 guard.
            if (voicedCount < 2)
                return queryJson;

            if (profile.HasPitchContour())
            {
                var sourceContour = NormalizeContourForVoiceVox(profile.PitchContour);
                if (sourceContour.Length >= 3)
                {
                    float maxAbs = 0.0f;
                    float sumSq = 0.0f;
                    foreach (var v in sourceContour)
                    {
                        maxAbs = Math.Max(maxAbs, Math.Abs(v));
                        sumSq += v * v;
                    }
                    float rms = MathF.Sqrt(sumSq / Math.Max(1, sourceContour.Length));
                    float contourStrength = Clamp01((maxAbs * 0.58f) + (rms * 0.70f) + (Math.Abs(profile.ContourRise) * 0.18f));
                    // V1's 0.10..0.40 range can be too subtle after V2 routing/playback.
                    // For real source-PCM contour transfer, keep the V1 formula shape but
                    // apply a dedicated gain so the transferred intonation is audible.
                    // Overall transfer-strength boost: short fillers and normal speech both
                    // sounded too flat after V2 routing, so raise the base gain and ceiling.
                    float shortUtteranceGain = voicedCount <= 2 ? 1.22f : 1.0f;
                    float contourAmplitude = Math.Clamp((0.16f + (0.26f * Math.Max(0.22f, contourStrength) * (0.40f + (0.60f * weight)))) * contourPref * 2.75f * shortUtteranceGain, 0.28f, 1.35f);
                    double totalDur = voicedMoras.Sum(EstimateVoiceVoxMoraDurationSec);
                    double elapsed = 0.0d;
                    for (int i = 0; i < voicedCount; i++)
                    {
                        var mora = voicedMoras[i];
                        double dur = EstimateVoiceVoxMoraDurationSec(mora);
                        double startPos = totalDur <= 0.0d ? i / (double)Math.Max(1, voicedCount) : elapsed / totalDur;
                        double endPos = totalDur <= 0.0d ? (i + 1.0d) / Math.Max(1, voicedCount) : (elapsed + dur) / totalDur;
                        float contourDelta = SampleContourAverage(sourceContour, startPos, endPos);
                        double onsetRamp = Math.Min(1.0d, (i + 0.35d) / 1.8d);
                        contourDelta *= (float)onsetRamp;
                        if (i == 0 && contourDelta > 0.0f) contourDelta *= 0.58f;
                        else if (i == 1 && contourDelta > 0.0f) contourDelta *= 0.82f;
                        float basePitch = (float)GetJsonDouble(mora["pitch"]);
                        if (basePitch > 0.0f) mora["pitch"] = Math.Clamp(basePitch + (contourAmplitude * contourDelta), 0.01f, 10.0f);
                        elapsed += dur;
                    }
                    ApplyVoiceVoxTimingTransferIfNeeded(root, profile, weight, contourPref);
                    return root.ToJsonString();
                }
            }

            float amplitude;
            string mode;
            float melodyDepth = profile.MelodyDepth * weight;
            float contour = profile.ContourRise * weight;
            float darkDepth = profile.DarkScore * weight;
            if (profile.IsMelodicLike() || melodyDepth >= 0.28f)
            {
                if (profile.Confidence < 0.60f)
                {
                    amplitude = Math.Clamp(0.016f + (0.026f * Math.Max(weight * 0.7f, melodyDepth * 0.6f)), 0.016f, 0.045f);
                    mode = "ripple-lite";
                }
                else
                {
                    amplitude = Math.Clamp(0.03f + (0.05f * Math.Max(weight, melodyDepth)), 0.03f, 0.10f);
                    mode = "arch+ripple";
                }
            }
            else if (profile.Mood == "excited")
            {
                amplitude = Math.Clamp(0.018f + (0.032f * Math.Max(weight * 0.7f, profile.EnergyScore * weight)), 0.018f, 0.065f);
                mode = "lift";
            }
            else if (profile.Mood == "question" || contour >= 0.18f)
            {
                amplitude = Math.Clamp(0.015f + (0.030f * Math.Max(weight * 0.6f, contour)), 0.015f, 0.055f);
                mode = "tail-rise";
            }
            else if (profile.IsDarkLike() || contour <= -0.16f || darkDepth >= 0.18f)
            {
                amplitude = Math.Clamp(0.015f + (0.035f * Math.Max(darkDepth, -contour)), 0.015f, 0.06f);
                mode = "tail-fall";
            }
            else
            {
                return queryJson;
            }

            for (int i = 0; i < voicedCount; i++)
            {
                var mora = voicedMoras[i];
                float basePitch = (float)GetJsonDouble(mora["pitch"]);
                if (basePitch <= 0.0f)
                    continue;

                double pos = voicedCount <= 1 ? 0.0d : i / (double)(voicedCount - 1);
                double delta = mode switch
                {
                    "arch+ripple" => amplitude * ((Math.Sin(Math.PI * pos) * 0.75d) + (Math.Sin(2.0d * Math.PI * pos) * 0.25d)),
                    "ripple-lite" => amplitude * Math.Sin(2.0d * Math.PI * pos) * 0.55d,
                    "lift" => amplitude * Math.Sin(Math.PI * pos),
                    "tail-fall" => -amplitude * Math.Pow(pos, 1.55d),
                    "tail-rise" => amplitude * pos * pos,
                    _ => 0.0d
                };
                mora["pitch"] = Math.Clamp(basePitch + (float)delta, 0.01f, 10.0f);
            }

            ApplyVoiceVoxTimingTransferIfNeeded(root, profile, weight, contourPref);
            return root.ToJsonString();
        }
        catch
        {
            return queryJson;
        }
    }

    private static float[] NormalizeContourForVoiceVox(float[] contour)
    {
        if (contour.Length < 3) return Array.Empty<float>();
        float mean = contour.Average();
        float[] centered = contour.Select(v => v - mean).ToArray();
        if (centered.Length >= 3)
        {
            var smoothed = (float[])centered.Clone();
            for (int i = 1; i < centered.Length - 1; i++)
                smoothed[i] = (centered[i - 1] * 0.20f) + (centered[i] * 0.60f) + (centered[i + 1] * 0.20f);
            centered = smoothed;
        }
        var absValues = centered.Select(Math.Abs).OrderBy(v => v).ToArray();
        float normBase = absValues[Math.Max(0, (int)Math.Floor((absValues.Length - 1) * 0.90d))];
        normBase = Math.Max(0.85f, normBase);
        return centered.Select(v => Math.Clamp(v / normBase, -1.0f, 1.0f)).ToArray();
    }

    private static float SampleContourAverage(float[] contour, double startPos, double endPos)
    {
        if (contour.Length == 0) return 0.0f;
        if (contour.Length == 1) return contour[0];
        double lo = Math.Clamp(startPos, 0.0d, 1.0d);
        double hi = Math.Clamp(endPos, lo, 1.0d);
        if (Math.Abs(hi - lo) < 0.0001d) return SampleContourAt(contour, (lo + hi) * 0.5d);
        double sum = 0.0d;
        const int n = 5;
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5d) / n;
            sum += SampleContourAt(contour, lo + ((hi - lo) * t));
        }
        return (float)(sum / n);
    }

    private static float SampleContourAt(float[] contour, double pos)
    {
        if (contour.Length == 0) return 0.0f;
        if (contour.Length == 1) return contour[0];
        double scaled = Math.Clamp(pos, 0.0d, 1.0d) * (contour.Length - 1);
        int lo = (int)Math.Floor(scaled);
        int hi = Math.Min(contour.Length - 1, lo + 1);
        double t = scaled - lo;
        return (float)(contour[lo] + ((contour[hi] - contour[lo]) * t));
    }

    private static void ApplyVoiceVoxTimingTransferIfNeeded(System.Text.Json.Nodes.JsonObject root, TtsProsodyProfile profile, float weight, float contourPref)
    {
        if (!profile.HasTimingContour()) return;
        if (root["accent_phrases"] is not System.Text.Json.Nodes.JsonArray phrases) return;
        var units = new List<(System.Text.Json.Nodes.JsonObject Mora, bool Pause, bool Voiced)>();
        foreach (var phrase in phrases)
        {
            if (phrase is not System.Text.Json.Nodes.JsonObject phraseObj) continue;
            if (phraseObj["moras"] is System.Text.Json.Nodes.JsonArray moras)
            {
                foreach (var mora in moras)
                {
                    if (mora is not System.Text.Json.Nodes.JsonObject moraObj) continue;
                    bool voiced = GetJsonDouble(moraObj["pitch"]) > 0.0d;
                    units.Add((moraObj, false, voiced));
                }
            }
            if (phraseObj["pause_mora"] is System.Text.Json.Nodes.JsonObject pauseMora)
                units.Add((pauseMora, true, false));
        }
        if (units.Count < 2) return;

        double totalDur = units.Sum(u => EstimateVoiceVoxMoraDurationSec(u.Mora));
        if (totalDur <= 0.05d) return;
        float[] activity = profile.ActivityContour;
        float[] voicing = profile.VoicingContour;
        double elapsed = 0.0d;
        foreach (var unit in units)
        {
            double baseDur = EstimateVoiceVoxMoraDurationSec(unit.Mora);
            double startPos = elapsed / totalDur;
            double endPos = (elapsed + baseDur) / totalDur;
            float activityAvg = SampleGenericContourAverage(activity, startPos, endPos, 0.0f);
            float voicingAvg = SampleGenericContourAverage(voicing, startPos, endPos, unit.Voiced ? 1.0f : 0.0f);
            float silenceAvg = Clamp01(1.0f - Math.Max(activityAvg, voicingAvg));
            float scale;
            if (unit.Pause)
            {
                scale = Math.Clamp(0.92f + (silenceAvg * 0.40f * contourPref) - (activityAvg * 0.10f), 0.78f, 1.55f);
            }
            else if (unit.Voiced)
            {
                float expressiveness = Math.Max(voicingAvg, activityAvg * 0.35f);
                scale = Math.Clamp(0.90f + (expressiveness * 0.30f * contourPref) + (silenceAvg * 0.08f), 0.82f, 1.60f);
            }
            else
            {
                scale = Math.Clamp(0.95f + (silenceAvg * 0.18f), 0.82f, 1.25f);
            }
            ApplyVoiceVoxTimingScale(unit.Mora, scale);
            elapsed += baseDur;
        }
    }

    private static float SampleGenericContourAverage(float[] contour, double startPos, double endPos, float defaultValue)
    {
        return contour.Length == 0 ? defaultValue : SampleContourAverage(contour, startPos, endPos);
    }

    private static void ApplyVoiceVoxTimingScale(System.Text.Json.Nodes.JsonObject mora, float scale)
    {
        double consonant = GetJsonDouble(mora["consonant_length"]);
        double vowel = GetJsonDouble(mora["vowel_length"]);
        if (consonant > 0.0d)
        {
            double consonantScale = 1.0d + ((scale - 1.0d) * 0.45d);
            mora["consonant_length"] = Math.Clamp(consonant * consonantScale, 0.018d, 0.320d);
        }
        if (vowel > 0.0d)
            mora["vowel_length"] = Math.Clamp(vowel * scale, 0.040d, 0.750d);
    }

    private static double EstimateVoiceVoxMoraDurationSec(System.Text.Json.Nodes.JsonObject mora)
    {
        double consonant = GetJsonDouble(mora["consonant_length"]);
        double vowel = GetJsonDouble(mora["vowel_length"]);
        double total = consonant + vowel;
        return total > 0.02d ? total : 0.12d;
    }

    private static TtsProsodyProfile SoftenVoiceVoxBorderlineUpbeatProfile(TtsProsodyProfile profile)
    {
        if (profile.HasPitchContour() || !IsUpbeatTtsMood(profile.Mood)) return profile;
        float conf = profile.Confidence;
        if (conf < 0.60f)
        {
            return new TtsProsodyProfile("neutral", 0.0f,
                1.0f + ((profile.IntonationScale - 1.0f) * 0.35f),
                1.0f + ((profile.SpeedScale - 1.0f) * 0.35f),
                1.0f + ((profile.VolumeScale - 1.0f) * 0.35f),
                Math.Max(0.28f, conf * 0.85f), profile.ContourRise, profile.MelodyDepth, profile.DarkScore, profile.EnergyScore);
        }
        if (conf < 0.78f)
        {
            return new TtsProsodyProfile(profile.Mood, Math.Min(profile.PitchScale, 0.012f),
                1.0f + ((profile.IntonationScale - 1.0f) * 0.65f),
                1.0f + ((profile.SpeedScale - 1.0f) * 0.65f),
                1.0f + ((profile.VolumeScale - 1.0f) * 0.65f),
                profile.Confidence, profile.ContourRise, profile.MelodyDepth, profile.DarkScore, profile.EnergyScore);
        }
        return profile;
    }

    private static bool IsUpbeatTtsMood(string mood) => mood == "sing_song_lite" || mood == "excited" || mood == "question";
    private static double GetJsonDouble(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node == null) return 0.0d;
        if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return value;
        return 0.0d;
    }

    private static float ProsodyWeight(float confidence)
    {
        float c = Math.Clamp(confidence, 0.25f, 1.0f);
        float norm = (c - 0.25f) / 0.75f;
        return 0.20f + (0.80f * MathF.Sqrt(Math.Max(0.0f, norm)));
    }

    private static float ScaleAroundNeutral(float neutral, float target, float weight) => neutral + ((target - neutral) * weight);
    private static float Clamp01(float value) => Math.Clamp(value, 0.0f, 1.0f);

    /// <summary>
    /// Gets the contour strength multiplier.
    /// mild=0.4, normal=0.7, strong=1.0
    /// </summary>
    private static float GetContourMultiplier(string strength)
    {
        return strength switch
        {
            "mild" => 0.4f,
            "strong" => 1.0f,
            _ => 0.7f // normal
        };
    }

    private static float GetVoiceVoxContourReflectionStrength(string strength)
    {
        // Overall boost: reproduced intonation was too subtle after V2 routing/playback.
        return strength switch
        {
            "mild" => 1.00f,
            "strong" => 1.65f,
            _ => 1.35f
        };
    }

    private static float GetVoiceVoxToneEmphasisStrength(string emphasis)
    {
        return emphasis switch
        {
            "mild" => 0.85f,
            "strong" => 1.55f,
            _ => 1.10f
        };
    }

    /// <summary>
    /// Gets the tone emphasis multiplier.
    /// mild=0.3, normal=0.6, strong=1.0
    /// </summary>
    private static float GetToneMultiplier(string emphasis)
    {
        return emphasis switch
        {
            "mild" => 0.3f,
            "strong" => 1.0f,
            _ => 0.6f // normal
        };
    }

    /// <summary>
    /// Blends a value toward a target based on a multiplier.
    /// multiplier=0.0 returns target, multiplier=1.0 returns value.
    /// </summary>
    private static float BlendToward(float target, float value, float multiplier)
    {
        return target + (value - target) * multiplier;
    }

    private static string PatchJsonFloat(string json, string key, float value)
    {
        var escapedKey = System.Text.RegularExpressions.Regex.Escape(key);
        var pattern = "\"" + escapedKey + "\"\\s*:\\s*[-0-9.]+";
        var replacement = "\"" + escapedKey + "\": " + value.ToString("F4");
        return System.Text.RegularExpressions.Regex.Replace(json, pattern, replacement);
    }

    private static bool IsLocalhostUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host == "localhost" || uri.Host == "127.0.0.1";
        }
        return false;
    }

    public void Dispose()
    {
        // HttpClient is shared static; no disposal needed
    }
}

/// <summary>
/// Result of VOICEVOX synthesis.
/// </summary>
public readonly struct VoicevoxResult
{
    public bool Success { get; }
    public byte[]? WavBytes { get; }
    public string? Error { get; }

    private VoicevoxResult(bool success, byte[]? wavBytes, string? error)
    {
        Success = success;
        WavBytes = wavBytes;
        Error = error;
    }

    public static VoicevoxResult Ok(byte[] wavBytes) => new(true, wavBytes, null);
    public static VoicevoxResult Fail(string error) => new(false, null, error);
}
