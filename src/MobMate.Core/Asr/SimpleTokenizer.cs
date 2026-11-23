using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MobMate.Core.Asr;

public static partial class SimpleTokenizer
{
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "has", "have", "i", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "was", "were", "will", "with", "you", "your"
    };

    private static readonly char[] KoreanParticles = ['은', '는', '이', '가', '을', '를', '에', '서', '도', '만', '로', '과', '와'];
    private static readonly char[] JapaneseParticles = ['は', 'が', 'の', 'を', 'に', 'で', 'と', 'も', 'へ', 'や'];

    public static IReadOnlyList<string> Tokenize(string? text, string? lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0) return Array.Empty<string>();

        var language = (lang ?? string.Empty).Trim().ToLowerInvariant();
        List<string> raw = language switch
        {
            "ko" or "kr" or "kor" or "ko-kr" => TokenizeKorean(normalized),
            "zh" or "zho" or "cmn" or "zh-cn" or "zh-tw" => TokenizeChinese(normalized),
            "ja" or "jp" or "jpn" or "ja-jp" => TokenizeJapanese(normalized),
            _ => ContainsCjk(normalized) ? TokenizeMixedCjk(normalized) : TokenizeEnglish(normalized),
        };

        return raw.Select(CleanToken)
            .Where(static t => IsUsefulToken(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
    }

    private static List<string> TokenizeEnglish(string text)
    {
        var tokens = SplitWordsRegex().Split(text)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !EnglishStopWords.Contains(s))
            .ToList();
        AddPhrases(tokens, tokens);
        return tokens;
    }

    private static List<string> TokenizeKorean(string text)
    {
        var tokens = SplitWordsRegex().Split(text)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(TrimKoreanParticle)
            .ToList();
        AddPhrases(tokens, tokens);
        return tokens;
    }

    private static List<string> TokenizeChinese(string text)
    {
        var parts = SplitCjkPunctuationRegex().Split(text).Where(static s => !string.IsNullOrWhiteSpace(s));
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            var compact = WhitespaceRegex().Replace(part, string.Empty);
            AddCjkNgrams(compact, tokens);
        }
        return tokens;
    }

    private static List<string> TokenizeJapanese(string text)
    {
        var tokens = new List<string>();
        foreach (var part in SplitCjkPunctuationRegex().Split(text).Where(static s => !string.IsNullOrWhiteSpace(s)))
        {
            var segments = SplitByChars(part, JapaneseParticles);
            foreach (var segment in segments)
            {
                if (!string.IsNullOrWhiteSpace(segment)) tokens.Add(segment);
            }
        }
        tokens.AddRange(KatakanaRegex().Matches(text).Select(m => m.Value));
        return tokens;
    }

    private static List<string> TokenizeMixedCjk(string text)
    {
        var tokens = new List<string>();
        tokens.AddRange(TokenizeEnglish(text));
        tokens.AddRange(TokenizeChinese(text));
        tokens.AddRange(TokenizeJapanese(text));
        tokens.AddRange(TokenizeKorean(text));
        return tokens;
    }

    private static void AddPhrases(IReadOnlyList<string> words, List<string> tokens)
    {
        if (words.Count < 3) return;
        for (var i = 0; i <= words.Count - 3; i++)
        {
            var phrase = string.Join(' ', words.Skip(i).Take(3));
            if (phrase.Length <= 30) tokens.Add(phrase);
        }
    }

    private static void AddCjkNgrams(string text, List<string> tokens)
    {
        var chars = text.Where(IsCjkLetter).ToArray();
        for (var n = 2; n <= 4; n++)
        {
            if (chars.Length < n) continue;
            for (var i = 0; i <= chars.Length - n; i++)
            {
                tokens.Add(new string(chars, i, n));
            }
        }
    }

    private static IEnumerable<string> SplitByChars(string text, IReadOnlyCollection<char> separators)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (separators.Contains(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
                continue;
            }
            builder.Append(c);
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    private static string TrimKoreanParticle(string token)
    {
        if (token.Length <= 2) return token;
        return KoreanParticles.Contains(token[^1]) ? token[..^1] : token;
    }

    private static string CleanToken(string token)
        => token.Trim().Trim(',', '.', '!', '?', ';', ':', '"', '\'', '“', '”', '‘', '’', '。', '、', '，', '！', '？', '；', '：');

    private static bool IsUsefulToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.All(char.IsDigit)) return false;
        if (token.Any(IsCjkLetter)) return token.Length >= 2;
        return token.Length >= 2;
    }

    private static bool ContainsCjk(string text) => text.Any(IsCjkLetter);

    private static bool IsCjkLetter(char c)
        => (c >= '\u3040' && c <= '\u30ff')
        || (c >= '\u3400' && c <= '\u9fff')
        || (c >= '\uf900' && c <= '\ufaff')
        || (c >= '\uac00' && c <= '\ud7af');

    [GeneratedRegex("[\\s\\p{P}\\p{S}]+", RegexOptions.Compiled)]
    private static partial Regex SplitWordsRegex();

    [GeneratedRegex("[\\s、。,.!?！？;；:：\\-ー]+", RegexOptions.Compiled)]
    private static partial Regex SplitCjkPunctuationRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[ァ-ヴー]{2,}", RegexOptions.Compiled)]
    private static partial Regex KatakanaRegex();
}
