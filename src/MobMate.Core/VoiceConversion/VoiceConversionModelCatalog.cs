using System.Text;
using System.Text.Json;

namespace MobMate.Core.VoiceConversion;

public sealed record VoiceConversionModelCatalogItem(
    string Id,
    string DisplayName,
    string Engine,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    string License,
    string LicenseUrl,
    string SourcePageUrl,
    string RiskNotice,
    bool RequiresUserAgreement);

public static class VoiceConversionModelCatalog
{
    public static VoiceConversionModelCatalogItem[] Load(string catalogJsonPath)
    {
        if (!File.Exists(catalogJsonPath)) return [];

        try
        {
            var json = File.ReadAllText(catalogJsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                    ? models
                    : default;
            if (items.ValueKind != JsonValueKind.Array) return [];

            var list = new List<VoiceConversionModelCatalogItem>();
            foreach (var item in items.EnumerateArray())
            {
                string G(string k) => item.TryGetProperty(k, out var p) ? p.GetString() ?? string.Empty : string.Empty;
                long GL(string k) => item.TryGetProperty(k, out var p) && p.TryGetInt64(out var v) ? v : 0L;
                bool GB(string k) => item.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.True;
                var entry = new VoiceConversionModelCatalogItem(
                    Id: G("id"),
                    DisplayName: G("displayName"),
                    Engine: G("engine"),
                    DownloadUrl: G("downloadUrl"),
                    SizeBytes: GL("sizeBytes"),
                    Sha256: G("sha256"),
                    License: G("license"),
                    LicenseUrl: G("licenseUrl"),
                    SourcePageUrl: G("sourcePageUrl"),
                    RiskNotice: G("riskNotice"),
                    RequiresUserAgreement: GB("requiresUserAgreement"));
                if (!string.IsNullOrWhiteSpace(entry.Id)) list.Add(entry);
            }
            return [.. list];
        }
        catch
        {
            return [];
        }
    }
}
