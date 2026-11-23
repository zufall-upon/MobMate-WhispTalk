using System.Globalization;

// Note: no namespace declaration – matches PendingSpeechService.cs / RadioChatService.cs pattern

/// <summary>
/// C# port of V1 LanguageOptions.java (translation-related parts only).
/// </summary>
internal static class LanguageOptionsTranslation
{
    // Full m2m100 supported language list (from V1 LanguageOptions.java TRANSLATION_LANGS)
    internal static readonly string[] TranslationLangs =
    [
        "af","am","ar","ast","az","ba","be","bg","bn","br","bs","ca","ceb","cs","cy","da","de","el","en","es","et","fa","ff","fi","fr","fy","ga","gd",
        "gl","gu","ha","he","hi","hr","ht","hu","hy","id","ig","ilo","is","it","ja","jv","ka","kk","km","kn","ko","lb","lg","ln","lo","lt","lv","mg",
        "mk","ml","mn","mr","ms","my","ne","nl","no","ns","oc","or","pa","pl","ps","pt","ro","ru","sd","si","sk","sl","so","sq","sr","ss","su","sv",
        "sw","ta","th","tl","tn","tr","uk","ur","uz","vi","wo","xh","yi","yo","zh","zu"
    ];

    private static readonly HashSet<string> TranslationLangSet =
        new(TranslationLangs, StringComparer.OrdinalIgnoreCase);

    // Language name overrides (V1 port)
    private static readonly Dictionary<string, string> LanguageNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ast"] = "Asturian",
        ["az"]  = "Azerbaijani",
        ["ba"]  = "Bashkir",
        ["ceb"] = "Cebuano",
        ["ff"]  = "Fulah",
        ["fy"]  = "West Frisian",
        ["ig"]  = "Igbo",
        ["ilo"] = "Ilocano",
        ["jv"]  = "Javanese",
        ["jw"]  = "Javanese",
        ["lg"]  = "Ganda",
        ["ln"]  = "Lingala",
        ["ns"]  = "Northern Sotho",
        ["oc"]  = "Occitan",
        ["or"]  = "Odia",
        ["sd"]  = "Sindhi",
        ["ss"]  = "Swati",
        ["su"]  = "Sundanese",
        ["tl"]  = "Tagalog",
        ["tn"]  = "Tswana",
        ["tt"]  = "Tatar",
        ["wo"]  = "Wolof",
        ["xh"]  = "Xhosa",
        ["yue"] = "Cantonese",
        ["zu"]  = "Zulu",
    };

    /// <summary>
    /// Normalize translation target: "OFF" or lowercase lang code.
    /// Invalid values return "OFF".
    /// </summary>
    internal static string NormalizeTranslationTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "OFF";
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "off") return "OFF";
        return TranslationLangSet.Contains(normalized) ? normalized : "OFF";
    }

    /// <summary>
    /// Returns display label like "English (en)" or "Off (OFF)".
    /// </summary>
    internal static string DisplayTranslationTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Off (OFF)";
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "off") return "Off (OFF)";
        var name = ResolveLanguageName(normalized);
        if (string.IsNullOrEmpty(name) || string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            name = normalized.ToUpperInvariant();
        return $"{name} ({normalized})";
    }

    /// <returns>true if the value is a supported translation language code (not "OFF").</returns>
    internal static bool IsTranslationLangSupported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return TranslationLangSet.Contains(value.Trim().ToLowerInvariant());
    }

    private static string ResolveLanguageName(string normalized)
    {
        if (LanguageNameOverrides.TryGetValue(normalized, out var over)) return over;
        try
        {
            var culture = new CultureInfo(normalized);
            var name = culture.EnglishName;
            // CultureInfo may return "Unknown Language (xx)" - strip that
            if (!string.IsNullOrEmpty(name) && !name.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                return name;
        }
        catch { /* ignore invalid culture codes */ }
        return normalized.ToUpperInvariant();
    }
}
