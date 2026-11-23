namespace MobMate.Core.Asr;

public sealed class AdaptivePromptResult
{
    public string Prompt { get; init; } = string.Empty;
    public bool Injected { get; init; }
    public int VocabCount { get; init; }
}

public sealed class AdaptivePromptBuilder
{
    private readonly AdaptiveVocabStore _store;
    private readonly AdaptiveVocabOptions _options;

    public AdaptivePromptBuilder(AdaptiveVocabStore store, AdaptiveVocabOptions? options = null)
    {
        _store = store;
        _options = options ?? new AdaptiveVocabOptions();
    }

    public AdaptivePromptResult Build(string? userInitialPrompt, string? language, string speakerProfile = "default")
    {
        var original = userInitialPrompt ?? string.Empty;
        if (!_options.Enabled || !_options.PromptInjection)
        {
            return new AdaptivePromptResult { Prompt = Truncate(original, 2000), Injected = false, VocabCount = 0 };
        }

        var entries = _store.GetTopEntries(language, speakerProfile, 200);
        if (entries.Count == 0)
        {
            return new AdaptivePromptResult { Prompt = Truncate(original, 2000), Injected = false, VocabCount = 0 };
        }

        var selected = new List<string>();
        var maxChars = Math.Clamp(_options.PromptMaxChars, 0, 1500);
        var usedChars = 0;
        foreach (var entry in entries)
        {
            var text = entry.Text.Trim();
            if (text.Length == 0) continue;
            var addLen = text.Length + (selected.Count == 0 ? 0 : 2);
            if (usedChars + addLen > maxChars) break;
            selected.Add(text);
            usedChars += addLen;
        }

        if (selected.Count == 0)
        {
            return new AdaptivePromptResult { Prompt = Truncate(original, 2000), Injected = false, VocabCount = 0 };
        }

        var injected = "[学習語彙] " + string.Join(", ", selected) + " [/学習語彙]";
        var prompt = string.IsNullOrWhiteSpace(original) ? injected : injected + " " + original.Trim();
        return new AdaptivePromptResult { Prompt = Truncate(prompt, 2000), Injected = true, VocabCount = selected.Count };
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars];
}
