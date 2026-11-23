namespace MobMate.Core.Asr;

// File layout only: actual model/runtime compatibility is established by native load.
internal static class MoonshineModelCatalog
{
    internal static string[] Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Array.Empty<string>();

        bool HasFiles(string relative, params string[] names)
        {
            var directory = Path.Combine(root, relative);
            return names.All(name => File.Exists(Path.Combine(directory, name)));
        }

        var found = new List<string>();
        const string english = "en/quantized";
        if (HasFiles(english, "tokenizer.bin", "streaming_config.json", "encoder.ort",
                "adapter.ort", "cross_kv.ort", "decoder_kv.ort") &&
            (HasFiles(english, "frontend.ort") || HasFiles(english, "frontend.model.ort", "frontend.weights.ort")))
            found.Add("en");
        foreach (var (code, model) in new[] { ("ja", "base-ja"), ("zh", "base-zh"), ("ko", "tiny-ko"), ("vi", "base-vi") })
        {
            if (HasFiles(Path.Combine(code, "quantized", model), "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort"))
                found.Add(code);
        }
        return found.OrderBy(WhisperLanguageCatalog.PreferredOrder).ThenBy(code => code, StringComparer.Ordinal).ToArray();
    }
}
