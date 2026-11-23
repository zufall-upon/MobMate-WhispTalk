namespace MobMate.Core.Asr;

/// <summary>
/// 音素 CTC の IPA 列を使う補助レーン。
/// まだ UI/API へ露出しない純粋関数群。
/// 語句リテラルではなく、音素カテゴリと転写表だけで発声補助文字列へ変換する。
/// </summary>
public static class PhoneticAssist
{
    private static readonly HashSet<string> Vowels = ["a", "i", "u", "e", "o", "N"];

    public static IReadOnlyList<PhoneticAssistCandidate> RankHotwords(string observedIpa, IEnumerable<PhoneticHotword>? hotwords = null)
    {
        var observed = NormalizeIpaTokens(SplitPhonemes(observedIpa));
        if (hotwords == null) return [];
        return hotwords
            .Where(h => h.Enabled)
            .Select(h => ScoreCandidate(observed, h))
            .OrderByDescending(c => c.Score)
            .ToArray();
    }

    public static PhoneticAssistCandidate ScoreCandidate(string observedIpa, PhoneticHotword hotword)
        => ScoreCandidate(NormalizeIpaTokens(SplitPhonemes(observedIpa)), hotword);

    private static PhoneticAssistCandidate ScoreCandidate(IReadOnlyList<string> observed, PhoneticHotword hotword)
    {
        var expected = SplitPhonemes(hotword.Phonemes);
        if (observed.Count == 0 || expected.Count == 0)
            return PhoneticAssistCandidate.Reject(hotword, 0, "empty_phonemes", null, null);

        var distance = EditDistance(observed, expected);
        var normalized = distance / (double)Math.Max(observed.Count, expected.Count);
        var score = 1.0 - normalized;

        var obsVowels = VowelSequence(observed);
        var expVowels = VowelSequence(expected);
        var vowelDistance = EditDistance(obsVowels, expVowels);
        var vowelNorm = vowelDistance / (double)Math.Max(1, Math.Max(obsVowels.Count, expVowels.Count));
        score += (1.0 - vowelNorm) * 0.20;

        var firstObs = FirstConsonant(observed);
        if (firstObs != null && firstObs == FirstConsonant(expected)) score += 0.12;

        if (observed.Count < 3) score -= 0.35;
        if (observed.Count == 1) score -= 0.35;
        if (observed.LastOrDefault() == "N" && expected.LastOrDefault() != "N") score -= 0.08;
        if (observed.LastOrDefault() != "N" && expected.LastOrDefault() == "N") score -= 0.08;

        score = Math.Clamp(score, 0.0, 1.0);
        var accepted = normalized <= 0.35 && score >= 0.72 && observed.Count >= 3;
        var reason = accepted ? "accepted"
            : observed.Count < 3 ? "too_short"
            : normalized > 0.35 ? "distance_too_large"
            : "score_too_low";

        return new PhoneticAssistCandidate(
            hotword.Text,
            hotword.ReadingKana,
            hotword.Phonemes,
            Math.Round(score, 4),
            accepted,
            reason,
            distance,
            Math.Round(normalized, 4));
    }

    /// <summary>
    /// Returns a raw IPA similarity score (0.0–1.0) based on normalised edit distance.
    /// Used for re-speak detection: values ≥ 0.4 suggest the same phrase was repeated.
    /// </summary>
    public static double IpaSimilarity(string ipa1, string ipa2)
    {
        var a = NormalizeIpaTokens(SplitPhonemes(ipa1));
        var b = NormalizeIpaTokens(SplitPhonemes(ipa2));
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var dist = EditDistance(a, b);
        return 1.0 - dist / (double)Math.Max(a.Count, b.Count);
    }

    /// <summary>
    /// レーンB: 通常 ASR が空/極短の場合だけ、IPA をかな化して返す。
    /// 無音/情報量不足は reject。
    /// </summary>
    public static PhoneticAssistResult TryFiller(string primaryText, string observedIpa, PhoneticAssistLanguage language = PhoneticAssistLanguage.Japanese)
    {
        if (!IsPrimaryEmptyOrTiny(primaryText))
            return PhoneticAssistResult.Reject("filler", observedIpa, "primary_not_empty");

        var normalized = NormalizeIpaTokens(SplitPhonemes(observedIpa));
        if (normalized.Count == 0)
            return PhoneticAssistResult.Reject("filler", observedIpa, "empty_phonemes");
        if (LooksLikeInitialBurstWordArtifact(normalized, language))
            return PhoneticAssistResult.Reject("filler", observedIpa, "non_filler_shape");

        var filtered = StripLikelyInitialBurst(TrimTrailingBreathAfterLongVowel(normalized, language), language);
        if (filtered.Count == 0 || filtered.Count(t => t != "N") == 0)
            return PhoneticAssistResult.Reject("filler", observedIpa, "noise_only");
        if (LooksLikeShortWordArtifact(filtered, language))
            return PhoneticAssistResult.Reject("filler", observedIpa, "non_filler_shape");
        if (!IsFillerLike(filtered))
            return PhoneticAssistResult.Reject("filler", observedIpa, "not_filler_like");

        var output = IpaToText(filtered, language);
        output = StretchShortFiller(output, filtered, language);
        if (string.IsNullOrWhiteSpace(output))
            return PhoneticAssistResult.Reject("filler", observedIpa, "output_empty");

        if (output.Length == 1 && filtered.Count < 2)
            return PhoneticAssistResult.Reject("filler", observedIpa, "too_short");

        return new PhoneticAssistResult("filler", true, output, null, "accepted", 1.0, observedIpa, null);
    }

    /// <summary>
    /// 通常ASRが空/極短でTTSへ届かない時だけ、音素CTCの表記化結果を補助出力にする。
    /// 語句リテラル補正はせず、IPA→各言語表記の機械変換だけ行う。
    /// </summary>
    public static PhoneticAssistResult TrySurfaceFallback(string primaryText, string observedIpa, PhoneticAssistLanguage language = PhoneticAssistLanguage.Japanese)
    {
        if (!IsPrimaryEmptyOrTiny(primaryText))
            return PhoneticAssistResult.Reject("surface", observedIpa, "primary_not_empty");

        var normalized = NormalizeIpaTokens(SplitPhonemes(observedIpa));
        if (normalized.Count == 0)
            return PhoneticAssistResult.Reject("surface", observedIpa, "empty_phonemes");

        var filtered = StripLikelyInitialBurst(TrimTrailingBreathAfterLongVowel(normalized, language), language);
        if (filtered.Count < 3)
            return PhoneticAssistResult.Reject("surface", observedIpa, "too_short");
        if (filtered.Count > 32)
            return PhoneticAssistResult.Reject("surface", observedIpa, "too_long");

        var vowelCount = filtered.Count(t => Vowels.Contains(t) || IsLongVowel(t, out _));
        if (vowelCount < 2)
            return PhoneticAssistResult.Reject("surface", observedIpa, "low_vowel_info");

        var output = IpaToText(filtered, language);
        if (string.IsNullOrWhiteSpace(output))
            return PhoneticAssistResult.Reject("surface", observedIpa, "output_empty");
        if (output.Length < 2)
            return PhoneticAssistResult.Reject("surface", observedIpa, "output_too_short");
        if (output.Length > 40)
            return PhoneticAssistResult.Reject("surface", observedIpa, "output_too_long");

        return new PhoneticAssistResult("surface", true, output, null, "accepted", 1.0, observedIpa, null);
    }

    /// <summary>
    /// P0 2026-07-14: surface レーン用の音響ゲート。
    /// 飲食音・接触音などのノイズ PCM から CTC がゴミ音節を生成するのを防ぐ。
    /// 母音を2つ以上含む発話なら有声（低ZCR）のアクティブフレームが必ず存在する。
    /// 語句リテラル判定はしない（波形統計のみ）。
    /// </summary>
    public static bool PcmLooksArticulated(byte[] pcm16le, int sampleRate)
    {
        if (pcm16le == null || sampleRate <= 0) return false;
        int totalSamples = pcm16le.Length / 2;
        int frameSamples = Math.Max(1, sampleRate / 50); // 20ms
        if (totalSamples < frameSamples * 4) return false;

        int activeFrames = 0;
        int voicedFrames = 0;
        int activePeak = 0;
        for (int pos = 0; pos + frameSamples <= totalSamples; pos += frameSamples)
        {
            double sumSq = 0;
            int zeroCross = 0;
            int framePeak = 0;
            short prev = 0;
            for (int i = 0; i < frameSamples; i++)
            {
                int byteIdx = (pos + i) * 2;
                short s = (short)(pcm16le[byteIdx] | (pcm16le[byteIdx + 1] << 8));
                sumSq += (double)s * s;
                int abs = Math.Abs((int)s);
                if (abs > framePeak) framePeak = abs;
                if (i > 0 && ((s >= 0) != (prev >= 0))) zeroCross++;
                prev = s;
            }
            double rms = Math.Sqrt(sumSq / frameSamples);
            if (rms < 650.0) continue;
            activeFrames++;
            if (framePeak > activePeak) activePeak = framePeak;
            double zcr = zeroCross / (double)(frameSamples - 1);
            if (zcr < 0.12) voicedFrames++;
        }

        // >=160ms of active audio, peak audible, and >=35% voiced-shaped frames
        if (activeFrames < 8) return false;
        if (activePeak < 2200) return false;
        return voicedFrames / (double)activeFrames >= 0.35;
    }

    private static bool IsFillerLike(IReadOnlyList<string> tokens)
    {
        if (tokens.Count > 4) return false;
        var vowelCount = tokens.Count(t => Vowels.Contains(t) || IsLongVowel(t, out _));
        var consonantCount = tokens.Count - vowelCount;
        if (vowelCount == 0) return false;
        if (consonantCount <= 1) return true;
        if (tokens.Any(t => IsLongVowel(t, out _)) && consonantCount <= 2) return true;
        return false;
    }

    private static bool LooksLikeShortWordArtifact(IReadOnlyList<string> tokens, PhoneticAssistLanguage language)
    {
        if (language != PhoneticAssistLanguage.Japanese || tokens.Count == 0) return false;

        var vowelCount = tokens.Count(t => Vowels.Contains(t) || IsLongVowel(t, out _));
        if (vowelCount < 2) return false;

        if (tokens.Contains("h")) return true;

        // Short multi-vowel shapes without nasal/breath consonants become unstable word-like output (いあ/ちえ/えぱ).
        if (!tokens.Any(t => IsLongVowel(t, out _)) && !tokens.Any(t => t is "n" or "m" or "N" or "h"))
            return true;

        // Multi-vowel shapes starting with a lexical consonant are too word-like for filler fallback.
        if (!IsVowelLike(tokens[0]) && tokens[0] is not "n" and not "m" and not "N" and not "h")
            return true;

        return false;
    }

    private static bool LooksLikeInitialBurstWordArtifact(IReadOnlyList<string> tokens, PhoneticAssistLanguage language)
        => language == PhoneticAssistLanguage.Japanese
           && tokens.Count >= 2
           && (tokens[0] is "p" or "b")
           && (tokens[1] is "i" or "iː" or "e" or "eː" or "o" or "oː");

    private static IReadOnlyList<string> TrimTrailingBreathAfterLongVowel(IReadOnlyList<string> tokens, PhoneticAssistLanguage language)
    {
        if (language != PhoneticAssistLanguage.Japanese || tokens.Count < 4 || tokens[^1] != "h") return tokens;
        for (var i = 0; i < Math.Min(tokens.Count - 1, 2); i++)
        {
            if (!IsLongVowel(tokens[i], out _)) continue;
            var tail = tokens.Skip(i + 1).ToArray();
            if (tail.Length > 0 && tail.All(t => t == "h" || IsVowelLike(t)))
                return tokens.Take(i + 1).ToArray();
        }
        return tokens;
    }

    private static bool IsVowelLike(string token)
        => Vowels.Contains(token) || IsLongVowel(token, out _);

    private static string StretchShortFiller(string text, IReadOnlyList<string> tokens, PhoneticAssistLanguage language)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (language != PhoneticAssistLanguage.Japanese) return text;
        if (text.EndsWith('ー')) return text;
        if (tokens.Count == 1 && (Vowels.Contains(tokens[0]) || IsLongVowel(tokens[0], out _))) return text + "ー";
        if (tokens.Count == 2 && !Vowels.Contains(tokens[0]) && (Vowels.Contains(tokens[1]) || IsLongVowel(tokens[1], out _))) return text + "ー";
        return text;
    }

    public static string IpaToText(string observedIpa, PhoneticAssistLanguage language)
        => IpaToText(NormalizeIpaTokens(SplitPhonemes(observedIpa)), language);

    public static string IpaToText(IReadOnlyList<string> normalizedTokens, PhoneticAssistLanguage language)
        => language switch
        {
            PhoneticAssistLanguage.Japanese => IpaToKana(normalizedTokens),
            PhoneticAssistLanguage.English => IpaToRomanized(normalizedTokens, RomanizationFlavor.EnglishPronunciation),
            PhoneticAssistLanguage.Chinese => IpaToRomanized(normalizedTokens, RomanizationFlavor.PinyinLike),
            PhoneticAssistLanguage.Korean => IpaToHangul(normalizedTokens),
            _ => IpaToKana(normalizedTokens),
        };

    public static string IpaToKana(string observedIpa)
        => IpaToKana(NormalizeIpaTokens(SplitPhonemes(observedIpa)));

    public static string IpaToKana(IReadOnlyList<string> normalizedTokens)
    {
        var outKana = new List<string>();
        for (var i = 0; i < normalizedTokens.Count; i++)
        {
            var t = normalizedTokens[i];
            if (IsLongVowel(t, out var baseVowel))
            {
                outKana.Add(VowelKana(baseVowel) + "ー");
                continue;
            }
            if (Vowels.Contains(t))
            {
                outKana.Add(t == "N" ? "ん" : VowelKana(t));
                continue;
            }
            if (i + 1 < normalizedTokens.Count && TryCvKana(t, normalizedTokens[i + 1], out var kana))
            {
                outKana.Add(kana);
                i++;
                continue;
            }
            if (t is "n" or "m") outKana.Add("ん");
        }
        return string.Concat(outKana);
    }

    public static IReadOnlyList<string> SplitPhonemes(string value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<string> NormalizeIpaTokens(IEnumerable<string> tokens)
        => tokens.Select(NormalizeIpaToken).Where(t => t.Length > 0).ToArray();

    private static string NormalizeIpaToken(string token) => token switch
    {
        "a" or "ɑ" or "ɐ" or "æ" => "a",
        "aː" or "ɑː" => "aː",
        "i" or "ɪ" or "ᵻ" or "i.5" or "i5" => "i",
        "iː" => "iː",
        "u" or "ʊ" or "ɯ" or "ə" or "ə5" => "u",
        "uː" => "uː",
        "e" or "ɛ" or "ɜ" or "e̞" => "e",
        "eː" or "ɛː" => "eː",
        "o" or "ɔ" or "oʊ" => "o",
        "oː" or "ɔː" => "oː",
        "ŋ" or "ɲ" => "N",
        "ɾ" or "ɹ" or "l" or "ɭ" => "r",
        "ɡ" => "g",
        "β" or "v" => "b",
        "ð" or "z" or "ʑ" => "z",
        "ɕ" or "ʃ" => "s",
        "tɕ" or "tʃ" => "ch",
        "dʑ" or "dʒ" => "j",
        "ɸ" or "f" => "h",
        "j" => "y",
        "w" or "n" or "m" or "p" or "b" or "t" or "d" or "k" or "g" or "s" or "h" or "r" or "y" or "ts" => token,
        "<pad>" or "<s>" or "</s>" or "<unk>" => string.Empty,
        _ => token,
    };

    private static IReadOnlyList<string> StripLikelyInitialBurst(IReadOnlyList<string> tokens, PhoneticAssistLanguage language)
    {
        if (language != PhoneticAssistLanguage.Japanese) return tokens;
        if (tokens.Count < 2) return tokens;
        var first = tokens[0];
        var second = tokens[1];
        if (first is "b" or "p" && (Vowels.Contains(second) || IsLongVowel(second, out _)))
            return tokens.Skip(1).ToArray();
        return tokens;
    }

    private static bool IsPrimaryEmptyOrTiny(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        return trimmed.Length <= 1 || trimmed.All(char.IsPunctuation) || trimmed.All(char.IsSymbol);
    }

    private static bool IsLongVowel(string token, out string baseVowel)
    {
        baseVowel = token switch
        {
            "aː" => "a", "iː" => "i", "uː" => "u", "eː" => "e", "oː" => "o",
            _ => string.Empty,
        };
        return baseVowel.Length > 0;
    }

    private static string VowelKana(string vowel) => vowel switch
    {
        "a" => "あ", "i" => "い", "u" => "う", "e" => "え", "o" => "お", _ => string.Empty,
    };

    private static bool TryCvKana(string c, string v, out string kana)
    {
        if (IsLongVowel(v, out var baseV)) v = baseV;
        var key = c + v;
        kana = key switch
        {
            "ka" => "か", "ki" => "き", "ku" => "く", "ke" => "け", "ko" => "こ",
            "sa" => "さ", "si" => "し", "su" => "す", "se" => "せ", "so" => "そ",
            "ta" => "た", "ti" => "ち", "tu" => "つ", "te" => "て", "to" => "と",
            "na" => "な", "ni" => "に", "nu" => "ぬ", "ne" => "ね", "no" => "の",
            "ha" => "は", "hi" => "ひ", "hu" => "ふ", "he" => "へ", "ho" => "ほ",
            "ma" => "ま", "mi" => "み", "mu" => "む", "me" => "め", "mo" => "も",
            "ra" => "ら", "ri" => "り", "ru" => "る", "re" => "れ", "ro" => "ろ",
            "ya" => "や", "yu" => "ゆ", "yo" => "よ",
            "wa" => "わ", "wo" => "を",
            "pa" => "ぱ", "pi" => "ぴ", "pu" => "ぷ", "pe" => "ぺ", "po" => "ぽ",
            "ba" => "ば", "bi" => "び", "bu" => "ぶ", "be" => "べ", "bo" => "ぼ",
            "ga" => "が", "gi" => "ぎ", "gu" => "ぐ", "ge" => "げ", "go" => "ご",
            "za" => "ざ", "zi" => "じ", "zu" => "ず", "ze" => "ぜ", "zo" => "ぞ",
            "cha" => "ちゃ", "chi" => "ち", "chu" => "ちゅ", "che" => "ちぇ", "cho" => "ちょ",
            "ja" => "じゃ", "ji" => "じ", "ju" => "じゅ", "je" => "じぇ", "jo" => "じょ",
            "tsa" => "さ", "tsi" => "し", "tsu" => "つ", "tse" => "せ", "tso" => "そ",
            _ => string.Empty,
        };
        return kana.Length > 0;
    }

    private static string IpaToRomanized(IReadOnlyList<string> normalizedTokens, RomanizationFlavor flavor)
    {
        var parts = new List<string>();
        for (var i = 0; i < normalizedTokens.Count; i++)
        {
            var t = normalizedTokens[i];
            if (IsLongVowel(t, out var baseVowel))
            {
                parts.Add(RomanVowel(baseVowel, flavor, longVowel: true));
                continue;
            }
            if (Vowels.Contains(t))
            {
                parts.Add(t == "N" ? NasalRoman(flavor) : RomanVowel(t, flavor, longVowel: false));
                continue;
            }
            if (i + 1 < normalizedTokens.Count && TryCvRoman(t, normalizedTokens[i + 1], flavor, out var roman))
            {
                parts.Add(roman);
                i++;
                continue;
            }
            var consonant = RomanConsonant(t, flavor);
            if (consonant.Length > 0) parts.Add(consonant);
        }
        return string.Join(' ', parts);
    }

    private static string IpaToHangul(IReadOnlyList<string> normalizedTokens)
    {
        var outText = new List<string>();
        for (var i = 0; i < normalizedTokens.Count; i++)
        {
            var t = normalizedTokens[i];
            if (IsLongVowel(t, out var baseVowel)) t = baseVowel;
            if (Vowels.Contains(t))
            {
                outText.Add(t == "N" ? "응" : ComposeHangul("", t));
                continue;
            }
            if (i + 1 < normalizedTokens.Count && TryHangulCv(t, normalizedTokens[i + 1], out var hangul))
            {
                outText.Add(hangul);
                i++;
                continue;
            }
            if (t is "n" or "m") outText.Add("응");
        }
        return string.Concat(outText);
    }

    private static bool TryCvRoman(string c, string v, RomanizationFlavor flavor, out string roman)
    {
        if (IsLongVowel(v, out var baseV)) v = baseV;
        var consonant = RomanConsonant(c, flavor);
        var vowel = RomanVowel(v, flavor, longVowel: false);
        roman = consonant.Length == 0 || vowel.Length == 0 ? string.Empty : consonant + vowel;
        return roman.Length > 0;
    }

    private static string RomanVowel(string vowel, RomanizationFlavor flavor, bool longVowel) => flavor switch
    {
        RomanizationFlavor.EnglishPronunciation => vowel switch
        {
            "a" => longVowel ? "aa" : "ah", "i" => longVowel ? "ee" : "ih", "u" => longVowel ? "oo" : "uh", "e" => longVowel ? "ehh" : "eh", "o" => longVowel ? "ohh" : "oh", _ => string.Empty,
        },
        RomanizationFlavor.PinyinLike => vowel switch
        {
            "a" => "a", "i" => "yi", "u" => "wu", "e" => "e", "o" => "o", _ => string.Empty,
        },
        _ => string.Empty,
    };

    private static string RomanConsonant(string consonant, RomanizationFlavor flavor) => flavor switch
    {
        RomanizationFlavor.EnglishPronunciation => consonant switch
        {
            "ch" => "ch", "j" => "j", "ts" => "ts", "y" => "y", "N" => "n", _ => BasicRomanConsonant(consonant),
        },
        RomanizationFlavor.PinyinLike => consonant switch
        {
            "ch" => "q", "j" => "j", "ts" => "c", "y" => "y", "r" => "l", "N" => "n", _ => BasicRomanConsonant(consonant),
        },
        _ => string.Empty,
    };

    private static string BasicRomanConsonant(string consonant) => consonant switch
    {
        "w" or "n" or "m" or "p" or "b" or "t" or "d" or "k" or "g" or "s" or "h" or "r" or "y" or "z" => consonant,
        _ => string.Empty,
    };

    private static string NasalRoman(RomanizationFlavor flavor) => flavor == RomanizationFlavor.PinyinLike ? "en" : "n";

    private static bool TryHangulCv(string c, string v, out string hangul)
    {
        if (IsLongVowel(v, out var baseV)) v = baseV;
        hangul = ComposeHangul(c, v);
        return hangul.Length > 0;
    }

    private static string ComposeHangul(string c, string v)
    {
        var choseong = c switch
        {
            "" => 11, "g" or "k" => 0, "n" => 2, "d" or "t" => 3, "r" => 5, "m" => 6, "b" or "p" => 7, "s" or "z" or "ts" => 9, "ch" or "j" => 12, "h" => 18,
            _ => -1,
        };
        var jungseong = v switch
        {
            "a" => 0, "e" => 5, "o" => 8, "u" => 13, "i" => 20,
            _ => -1,
        };
        if (choseong < 0 || jungseong < 0) return string.Empty;
        return char.ConvertFromUtf32(0xAC00 + ((choseong * 21) + jungseong) * 28);
    }

    private static int EditDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = 0; i <= a.Count; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Count; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Count; i++)
        for (var j = 1; j <= b.Count; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
        }
        return dp[a.Count, b.Count];
    }

    private static IReadOnlyList<string> VowelSequence(IReadOnlyList<string> tokens)
        => tokens.Where(t => Vowels.Contains(t) || IsLongVowel(t, out _)).Select(t => IsLongVowel(t, out var v) ? v : t).ToArray();

    private static string? FirstConsonant(IReadOnlyList<string> tokens)
        => tokens.FirstOrDefault(t => !Vowels.Contains(t) && !IsLongVowel(t, out _));
}

public sealed record PhoneticHotword(string Text, string ReadingKana, string Phonemes, bool Enabled = true, int Priority = 0);

public enum PhoneticAssistLanguage
{
    Japanese,
    English,
    Chinese,
    Korean,
}

internal enum RomanizationFlavor
{
    EnglishPronunciation,
    PinyinLike,
}

public sealed record PhoneticAssistCandidate(
    string Text,
    string ReadingKana,
    string ExpectedPhonemes,
    double Score,
    bool Accepted,
    string Reason,
    int? EditDistance,
    double? NormalizedDistance)
{
    public static PhoneticAssistCandidate Reject(PhoneticHotword hotword, double score, string reason, int? editDistance, double? normalizedDistance)
        => new(hotword.Text, hotword.ReadingKana, hotword.Phonemes, score, false, reason, editDistance, normalizedDistance);
}

public sealed record PhoneticAssistResult(
    string Lane,
    bool Accepted,
    string? OutputText,
    string? CandidateText,
    string Reason,
    double Score,
    string PhonemesObserved,
    string? PhonemesExpected)
{
    public static PhoneticAssistResult Reject(string lane, string observed, string reason)
        => new(lane, false, null, null, reason, 0, observed, null);
}
