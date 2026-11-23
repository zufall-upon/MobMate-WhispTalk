using MobMate.Core.Asr;

internal sealed record RealtimeLanguageOption(string Code, string Label);

// The same file-layout observations feed the Linux catalog and start guard.
// File presence alone does not claim runtime/model compatibility.
internal static class RealtimeLanguagePolicy
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["ja"] = "Japanese", ["en"] = "English", ["zh"] = "Chinese", ["ko"] = "Korean", ["yue"] = "Cantonese"
    };
    private static readonly Dictionary<string, string[]> Codes = new(StringComparer.Ordinal)
    {
        ["whisper"] = WhisperLanguageCatalog.OrderedCodes(),
        ["moonshine"] = Array.Empty<string>(),
        ["joint_auto"] = Array.Empty<string>(),
        ["safe_auto"] = Array.Empty<string>(),
        ["sense_voice"] = new[] { "ja", "en", "zh", "ko", "yue" },
        ["paraformer_zh"] = new[] { "zh" }
    };

    public static IReadOnlyDictionary<string, RealtimeLanguageOption[]> Catalog(bool whisperAvailable, string? moonshineRoot, bool jointAvailable = true)
        => Codes.Keys.ToDictionary(engine => engine, engine => GetCodes(engine, whisperAvailable, moonshineRoot, jointAvailable)
            .Select(code => new RealtimeLanguageOption(code, engine == "whisper" || !Labels.ContainsKey(code) ? WhisperLanguageCatalog.Labels[code] : Labels[code])).ToArray(), StringComparer.Ordinal);

    public static bool RejectsExplicit(string? engine, string? language, bool whisperAvailable, string? moonshineRoot, bool jointAvailable = true)
        => engine == "joint_auto" && !jointAvailable ||
            engine is not null && Codes.ContainsKey(engine) && !string.IsNullOrWhiteSpace(language) && language != "auto" &&
            !GetCodes(engine, whisperAvailable, moonshineRoot, jointAvailable).Contains(language, StringComparer.OrdinalIgnoreCase);

    private static string[] GetCodes(string engine, bool whisperAvailable, string? moonshineRoot, bool jointAvailable)
        => engine == "joint_auto" && !jointAvailable || engine == "whisper" && !whisperAvailable ? Array.Empty<string>()
            : engine is "moonshine" or "joint_auto" or "safe_auto" ? MoonshineModelCatalog.Detect(moonshineRoot)
            : Codes[engine];
}
