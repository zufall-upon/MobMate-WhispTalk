using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobMate.Core.Asr;

public sealed class AdaptiveVocabOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxVocabSize { get; set; } = 500;
    public bool PromptInjection { get; set; } = true;
    public int PromptMaxChars { get; set; } = 500;
    public int TtsPriority { get; set; } = 40;
    public double EmphaticRmsMultiplier { get; set; } = 1.5;
    public int RetentionDays { get; set; } = 30;
    public double PhoneticHotwordScoreThreshold { get; set; } = 0.72;
    public int PhoneticHotwordMaxCount { get; set; } = 100;
    public double RespeakWindowSeconds { get; set; } = 15;
    public double RespeakSamePollToleranceSeconds { get; set; } = 0.5;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdaptiveVocabDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, AdaptiveSpeakerProfile> SpeakerProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdaptiveSpeakerProfile
{
    public List<AdaptiveVocabEntry> Vocab { get; set; } = new();
    public int MaxVocabSize { get; set; } = 500;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AdaptiveVocabEntry
{
    public string Text { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string Source { get; set; } = AdaptiveVocabSources.Asr;
    public int Count { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public int Priority { get; set; }
    public string? Phonemes { get; set; }
}

public static class AdaptiveVocabSources
{
    public const string Asr = "asr";
    public const string Tts = "tts";
    public const string AsrEmphatic = "asr_emphatic";
}

public sealed class AdaptiveVocabStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly AdaptiveVocabOptions _options;

    public AdaptiveVocabStore(string path, AdaptiveVocabOptions? options = null)
    {
        _path = path;
        _options = options ?? new AdaptiveVocabOptions();
    }

    public string Path => _path;

    public int AddText(string? text, string? lang, string source, string speakerProfile = "default")
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(text)) return 0;
        var tokens = SimpleTokenizer.Tokenize(text, lang);
        return AddTokens(tokens, lang, source, speakerProfile);
    }

    public int AddTokens(IEnumerable<string> tokens, string? lang, string source, string speakerProfile = "default")
    {
        if (!_options.Enabled) return 0;
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        lock (_sync)
        {
            var doc = LoadNoLock();
            var profile = GetOrCreateProfile(doc, speakerProfile);
            profile.MaxVocabSize = Math.Max(1, _options.MaxVocabSize);
            var byText = profile.Vocab.ToDictionary(e => e.Text, StringComparer.OrdinalIgnoreCase);
            foreach (var rawToken in tokens)
            {
                var token = rawToken.Trim();
                if (token.Length == 0) continue;
                if (!byText.TryGetValue(token, out var entry))
                {
                    entry = new AdaptiveVocabEntry
                    {
                        Text = token,
                        Lang = NormalizeLanguage(lang),
                        Source = source,
                        Count = 0,
                        LastSeen = now,
                    };
                    profile.Vocab.Add(entry);
                    byText[token] = entry;
                    added++;
                }

                entry.Count++;
                entry.LastSeen = now;
                entry.Lang = string.IsNullOrWhiteSpace(entry.Lang) ? NormalizeLanguage(lang) : entry.Lang;
                if (BasePriority(source) >= BasePriority(entry.Source)) entry.Source = source;
                entry.Priority = CalculatePriority(entry, now);
            }

            GarbageCollect(profile, now);
            SaveNoLock(doc);
        }
        return added;
    }

    public bool SetPhonemesForText(string? text, string? lang, string? phonemes, string source = AdaptiveVocabSources.Asr, string speakerProfile = "default")
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phonemes)) return false;
        var normalizedText = text.Trim();
        if (normalizedText.Length == 0) return false;
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            var doc = LoadNoLock();
            var profile = GetOrCreateProfile(doc, speakerProfile);
            profile.MaxVocabSize = Math.Max(1, _options.MaxVocabSize);
            var entry = profile.Vocab.FirstOrDefault(e => string.Equals(e.Text, normalizedText, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new AdaptiveVocabEntry
                {
                    Text = normalizedText,
                    Lang = NormalizeLanguage(lang),
                    Source = source,
                    Count = 0,
                    LastSeen = now,
                };
                profile.Vocab.Add(entry);
            }

            entry.Phonemes = phonemes.Trim();
            entry.Count = Math.Max(1, entry.Count);
            entry.LastSeen = now;
            entry.Lang = string.IsNullOrWhiteSpace(entry.Lang) ? NormalizeLanguage(lang) : entry.Lang;
            if (BasePriority(source) >= BasePriority(entry.Source)) entry.Source = source;
            entry.Priority = CalculatePriority(entry, now);
            GarbageCollect(profile, now);
            SaveNoLock(doc);
        }
        return true;
    }

    public IReadOnlyList<AdaptiveVocabEntry> GetTopEntries(string? lang, string speakerProfile = "default", int maxCount = 100)
    {
        lock (_sync)
        {
            var doc = LoadNoLock();
            if (!doc.SpeakerProfiles.TryGetValue(NormalizeSpeaker(speakerProfile), out var profile)) return Array.Empty<AdaptiveVocabEntry>();
            var normalizedLang = NormalizeLanguage(lang);
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in profile.Vocab)
            {
                entry.Priority = CalculatePriority(entry, now);
            }

            return profile.Vocab
                .Where(e => string.IsNullOrWhiteSpace(normalizedLang) || string.Equals(normalizedLang, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Lang, normalizedLang, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static e => e.Priority)
                .ThenByDescending(static e => e.Count)
                .ThenByDescending(static e => e.LastSeen)
                .Take(maxCount)
                .Select(CloneEntry)
                .ToArray();
        }
    }

    public int Count(string speakerProfile = "default")
    {
        lock (_sync)
        {
            var doc = LoadNoLock();
            return doc.SpeakerProfiles.TryGetValue(NormalizeSpeaker(speakerProfile), out var profile) ? profile.Vocab.Count : 0;
        }
    }

    /// <summary>
    /// Adjusts the base priority of matching entries by <paramref name="delta"/>.
    /// Used to penalise superseded recognition results during re-speak detection.
    /// </summary>
    public void AdjustPriority(string? text, int delta, string speakerProfile = "default")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var normalizedText = text.Trim();
        lock (_sync)
        {
            var doc = LoadNoLock();
            if (!doc.SpeakerProfiles.TryGetValue(NormalizeSpeaker(speakerProfile), out var profile)) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in profile.Vocab)
            {
                if (string.Equals(entry.Text, normalizedText, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Priority = Math.Clamp(CalculatePriority(entry, now) + delta, 0, 100);
                }
            }
            SaveNoLock(doc);
        }
    }

    public bool Reset(string? speakerProfile = null)
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return false;
            if (string.IsNullOrWhiteSpace(speakerProfile))
            {
                File.Delete(_path);
                return true;
            }

            var doc = LoadNoLock();
            var removed = doc.SpeakerProfiles.Remove(NormalizeSpeaker(speakerProfile));
            SaveNoLock(doc);
            return removed;
        }
    }

    private AdaptiveVocabDocument LoadNoLock()
    {
        try
        {
            if (!File.Exists(_path)) return new AdaptiveVocabDocument();
            var json = File.ReadAllText(_path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AdaptiveVocabDocument>(json, JsonOptions) ?? new AdaptiveVocabDocument();
        }
        catch
        {
            return new AdaptiveVocabDocument();
        }
    }

    private void SaveNoLock(AdaptiveVocabDocument doc)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(doc, JsonOptions), new UTF8Encoding(false));
    }

    private AdaptiveSpeakerProfile GetOrCreateProfile(AdaptiveVocabDocument doc, string speakerProfile)
    {
        var key = NormalizeSpeaker(speakerProfile);
        if (!doc.SpeakerProfiles.TryGetValue(key, out var profile))
        {
            profile = new AdaptiveSpeakerProfile { MaxVocabSize = _options.MaxVocabSize };
            doc.SpeakerProfiles[key] = profile;
        }
        return profile;
    }

    private void GarbageCollect(AdaptiveSpeakerProfile profile, DateTimeOffset now)
    {
        var retentionDays = Math.Max(1, _options.RetentionDays);
        profile.Vocab.RemoveAll(e => now - e.LastSeen > TimeSpan.FromDays(retentionDays));
        foreach (var entry in profile.Vocab) entry.Priority = CalculatePriority(entry, now);
        var maxSize = Math.Max(1, profile.MaxVocabSize);
        if (profile.Vocab.Count <= maxSize) return;
        profile.Vocab = profile.Vocab
            .OrderByDescending(static e => e.Priority)
            .ThenByDescending(static e => e.Count)
            .ThenByDescending(static e => e.LastSeen)
            .Take(maxSize)
            .ToList();
    }

    internal static int CalculatePriority(AdaptiveVocabEntry entry, DateTimeOffset now)
    {
        var countBonus = entry.Count <= 1 ? 0 : (int)Math.Round(Math.Log2(entry.Count) * 5.0);
        var age = now - entry.LastSeen;
        var recency = age < TimeSpan.FromHours(1) ? 10 : age < TimeSpan.FromDays(1) ? 5 : age > TimeSpan.FromDays(7) ? -10 : 0;
        return Math.Clamp(BasePriority(entry.Source) + countBonus + recency, 0, 100);
    }

    internal static int BasePriority(string? source)
        => source switch
        {
            AdaptiveVocabSources.Tts => 40,
            AdaptiveVocabSources.AsrEmphatic => 30,
            _ => 10,
        };

    private static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return string.Empty;
        var lower = lang.Trim().ToLowerInvariant();
        if (lower.StartsWith("en", StringComparison.Ordinal)) return "en";
        if (lower.StartsWith("ko", StringComparison.Ordinal)) return "ko";
        if (lower.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        if (lower.StartsWith("ja", StringComparison.Ordinal)) return "ja";
        return lower;
    }

    private static string NormalizeSpeaker(string? speakerProfile)
        => string.IsNullOrWhiteSpace(speakerProfile) ? "default" : speakerProfile.Trim();

    private static AdaptiveVocabEntry CloneEntry(AdaptiveVocabEntry e)
        => new()
        {
            Text = e.Text,
            Lang = e.Lang,
            Source = e.Source,
            Count = e.Count,
            LastSeen = e.LastSeen,
            Priority = e.Priority,
            Phonemes = e.Phonemes,
        };
}
