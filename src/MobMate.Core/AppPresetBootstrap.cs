using System.Globalization;
using System.Text;

namespace MobMate.Core;

public static class AppPresetBootstrap
{
    public sealed record IgnoreBootstrapResult(string Action, string TargetPath, string? SourcePath, string Language);

    public static string ResolveInitialLanguage(string? configuredLanguage = null)
    {
        var configured = NormalizeLanguage(configuredLanguage);
        if (!string.IsNullOrEmpty(configured)) return configured;

        var name = CultureInfo.CurrentUICulture.Name;
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("ja")) return "ja";
        if (lower.StartsWith("ko")) return "ko";
        if (lower is "zh-tw" or "zh-hk" or "zh-mo" || lower.Contains("hant")) return "zh_tw";
        if (lower.StartsWith("zh")) return "zh_cn";
        return "en";
    }

    public static string NormalizeLanguage(string? language)
    {
        var v = (language ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return v switch
        {
            "ja" or "ja_jp" => "ja",
            "en" or "en_us" or "en_gb" => "en",
            "ko" or "ko_kr" => "ko",
            "zh" or "zh_cn" or "zh_hans" => "zh_cn",
            "zh_tw" or "zh_hk" or "zh_mo" or "zh_hant" => "zh_tw",
            _ => string.Empty,
        };
    }

    public static IgnoreBootstrapResult EnsureRuntimeIgnoreFile(string appRoot, string configDir, string language)
    {
        Directory.CreateDirectory(configDir);
        var resolvedLanguage = ResolveInitialLanguage(language);
        var target = Path.Combine(configDir, "_ignore.txt");
        if (HasNonEmptyLine(target))
        {
            return new IgnoreBootstrapResult("kept", target, null, resolvedLanguage);
        }

        var source = ResolveIgnorePresetPath(appRoot, resolvedLanguage);
        if (!string.IsNullOrEmpty(source) && File.Exists(source))
        {
            File.Copy(source, target, overwrite: true);
            return new IgnoreBootstrapResult("created", target, source, resolvedLanguage);
        }

        File.WriteAllText(target, string.Empty, new UTF8Encoding(false));
        return new IgnoreBootstrapResult("missing", target, null, resolvedLanguage);
    }

    public static string? ResolveIgnorePresetPath(string appRoot, string language)
    {
        var names = new List<string>();
        var normalized = ResolveInitialLanguage(language);
        names.Add($"_ignore_{normalized}.txt");
        if (normalized == "zh_cn") names.Add("_ignore_zh_tw.txt");
        if (normalized == "zh_tw") names.Add("_ignore_zh_cn.txt");
        names.Add("_ignore_en.txt");
        names.Add("_ignore_ja.txt");

        var presetDir = Path.Combine(appRoot, "libs", "preset");
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(presetDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool HasNonEmptyLine(string path)
    {
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!string.IsNullOrWhiteSpace(line)) return true;
        }
        return false;
    }
}
