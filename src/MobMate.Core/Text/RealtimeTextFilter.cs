using System.Text.RegularExpressions;

namespace MobMate.Core.Text;

public sealed class RealtimeTextFilterConfig
{
    public string UiLanguage { get; set; } = "en";
    public string NgMode { get; set; } = "simple";
    public bool LaughReplacementEnabled { get; set; } = true;
    public string LaughPreset { get; set; } = "auto";
    public string ManualLaughTokens { get; set; } = string.Empty;
    public string AutoLaughTokens { get; set; } = string.Empty;
    public string LaughReplacementWords { get; set; } = string.Empty;
    public string NgWords { get; set; } = string.Empty;

    public static RealtimeTextFilterConfig CreateDefault(string language)
    {
        var lang = NormalizeLaughLanguage(language);
        var profile = GetLaughProfile(lang);
        return new RealtimeTextFilterConfig
        {
            UiLanguage = lang,
            LaughPreset = "auto",
            ManualLaughTokens = profile.ManualTokens,
            AutoLaughTokens = profile.AutoTokens,
            LaughReplacementWords = profile.ReplacementWords,
        };
    }

    public void ApplyLaughPreset(string preset)
    {
        var normalized = NormalizeLaughLanguage(preset);
        if (string.IsNullOrEmpty(normalized)) return;
        var profile = GetLaughProfile(normalized);
        LaughPreset = preset == "auto" ? "auto" : normalized;
        ManualLaughTokens = profile.ManualTokens;
        AutoLaughTokens = profile.AutoTokens;
        LaughReplacementWords = profile.ReplacementWords;
    }

    public static string NormalizeLaughLanguage(string? language)
    {
        var v = (language ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        if (v == "auto") return "auto";
        if (v.StartsWith("ja")) return "ja";
        if (v.StartsWith("ko")) return "ko";
        if (v is "zh_tw" or "zh_hk" or "zh_hant") return "zh";
        if (v.StartsWith("zh")) return "zh";
        if (v.StartsWith("en")) return "en";
        return string.Empty;
    }

    public static LaughProfile GetLaughProfile(string? language)
    {
        return NormalizeLaughLanguage(language) switch
        {
            "ja" => new LaughProfile(
                "（笑）,(笑),笑,草,ｗ,www,lol,lolol,lmao,rofl,hehe,haha,laugh,フフフ,ハハハ,ワハハ,へへへ",
                "（笑）,笑,草,ｗ,www,lol,lmao,rofl,ㅋㅋ,ㅎㅎ,哈哈,呵呵,ハハハ,フフフ",
                "ワハハハハハ,おほほほほ,わらわら"),
            "ko" => new LaughProfile(
                "ㅋㅋ,ㅎㅎ,웃음,www,lol,lmao,rofl,hehe,haha,ハハハ,笑",
                "ㅋㅋ,ㅎㅎ,웃음,ｗ,www,lol,lmao,rofl,哈哈,呵呵,笑",
                "하하하하"),
            "zh" => new LaughProfile(
                "哈哈,呵呵,笑,www,lol,lmao,rofl,hehe,haha,ㅋㅋ,ㅎㅎ,ハハハ",
                "哈哈,呵呵,笑,ｗ,www,lol,lmao,rofl,ㅋㅋ,ㅎㅎ",
                "哈哈哈哈"),
            _ => new LaughProfile(
                "(laugh),laugh,lol,lmao,rofl,hehe,haha,hahaha,www,笑,草,ハハハ",
                "(laugh),laugh,lol,lmao,rofl,hehe,haha,ㅋㅋ,ㅎㅎ,哈哈,呵呵,笑,草",
                "haha,hahaha,hehe"),
        };
    }
}

public sealed record LaughProfile(string ManualTokens, string AutoTokens, string ReplacementWords);

public sealed record RealtimeTextFilterResult(
    string RawText,
    string Text,
    bool Dropped,
    bool LaughReplaced,
    bool NgMatched,
    string? Reason);

public static class RealtimeTextFilter
{
    private static readonly Regex s_simpleNormalizeRegex = new("[\\s\\p{P}、。！？「」『』（）()\\[\\]{}【】…・~〜ー]+", RegexOptions.Compiled);

    public static RealtimeTextFilterResult Apply(string text, RealtimeTextFilterConfig config)
    {
        var raw = text ?? string.Empty;
        if (IsNgMatched(raw, config))
        {
            return new RealtimeTextFilterResult(raw, string.Empty, true, false, true, "ng_matched");
        }

        var replaced = ApplyLaughReplacement(raw, config, out var laughReplaced);
        return new RealtimeTextFilterResult(raw, replaced, false, laughReplaced, false, laughReplaced ? "laugh_replaced" : null);
    }

    public static IReadOnlyList<string> SplitCsv(string? value)
    {
        return (value ?? string.Empty)
            .Split(new[] { ',', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public static IReadOnlyList<string> SplitLines(string? value)
    {
        return (value ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
    }

    public static void ValidateRegexRules(string ngWords)
    {
        foreach (var rule in SplitLines(ngWords))
            _ = new Regex(rule, RegexOptions.CultureInvariant);
    }

    public static string NormalizeSimple(string? text)
    {
        var s = (text ?? string.Empty).Trim().ToLowerInvariant();
        s = s_simpleNormalizeRegex.Replace(s, string.Empty);
        return s;
    }

    private static bool IsNgMatched(string text, RealtimeTextFilterConfig config)
    {
        var rules = SplitLines(config.NgWords);
        if (rules.Count == 0) return false;

        if (string.Equals(config.NgMode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var rule in rules)
            {
                if (Regex.IsMatch(text, rule, RegexOptions.CultureInvariant)) return true;
            }
            return false;
        }

        var normalizedText = NormalizeSimple(text);
        foreach (var rule in rules)
        {
            if (text.Contains(rule, StringComparison.OrdinalIgnoreCase)) return true;
            var normalizedRule = NormalizeSimple(rule);
            if (normalizedRule.Length >= 2 && normalizedText.Contains(normalizedRule, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ApplyLaughReplacement(string text, RealtimeTextFilterConfig config, out bool replaced)
    {
        replaced = false;
        if (!config.LaughReplacementEnabled) return text;

        var tokens = SplitCsv(config.ManualLaughTokens).Concat(SplitCsv(config.AutoLaughTokens))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .ToArray();
        var replacements = SplitCsv(config.LaughReplacementWords).ToArray();
        if (tokens.Length == 0 || replacements.Length == 0) return text;

        var pattern = string.Join("|", tokens.Select(Regex.Escape));
        var pick = replacements[Random.Shared.Next(replacements.Length)];
        var didReplace = false;
        var output = Regex.Replace(text, pattern, _ => { didReplace = true; return pick; }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        replaced = didReplace;
        if (!replaced) return text;

        output = Regex.Replace(output, Regex.Escape(pick) + "[\\u3040-\\u30ffー]*", pick, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return output;
    }
}
