using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Note: no namespace declaration – matches PendingSpeechService.cs / RadioChatService.cs pattern

// ── 設定モデル ──────────────────────────────────────────────
public sealed class TranslationConfig
{
    [JsonPropertyName("talkTarget")]
    public string TalkTarget { get; set; } = "OFF";
}

// ── M2M100 トークナイザー ───────────────────────────────────
// V1 M2M100Tokenizer.java の C# 移植。
// SentencePiece は Microsoft.ML.Tokenizers.SentencePieceBpe を使用。
// (translatorcore.dll は JNI エクスポートのため C# から直接呼べない)
internal sealed class M2M100Tokenizer : IDisposable
{
    private const long EosId = 2L;

    // tokenizer_config.json 内の "ID": {"content":"__XX__"} パターン
    private static readonly Regex LangTokenRegex =
        new(@"""(\d+)""\s*:\s*\{[^{}]*""content""\s*:\s*""__([a-zA-Z\-]+)__""",
            RegexOptions.Compiled);

    // LlamaTokenizer extends SentencePieceTokenizer and supports BPE .model files
    private readonly LlamaTokenizer _spm;
    private readonly Dictionary<string, long>   _langIds;
    private readonly Dictionary<string, int>    _tokenToId;
    private readonly Dictionary<int,    string> _idToToken;
    private bool _disposed;

    internal M2M100Tokenizer(string modelDir)
    {
        // m2m100 の sentencepiece.bpe.model は "nmt_nfkc" normalization を使うが、
        // LlamaTokenizer は "identity" しか対応しないため、バイナリ上でパッチする。
        // "nmt_nfkc" = 8 bytes, "identity" = 8 bytes → 同バイト長なのでプロトバフ構造に影響なし。
        var rawBytes = File.ReadAllBytes(Path.Combine(modelDir, "sentencepiece.bpe.model"));
        PatchBytes(rawBytes, "nmt_nfkc"u8, "identity"u8);
        using var spmStream = new System.IO.MemoryStream(rawBytes);
        _spm = LlamaTokenizer.Create(spmStream,
            addBeginOfSentence: false,
            addEndOfSentence:   false,
            specialTokens:      null);

        _langIds   = LoadLangIds(Path.Combine(modelDir, "tokenizer_config.json"));
        _tokenToId = LoadTokenToId(Path.Combine(modelDir, "vocab.json"));
        _idToToken = _tokenToId.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    /// <summary>
    /// text をエンコードして [srcLangId, piece_ids..., EOS(2)] を返す。
    /// </summary>
    internal long[] Encode(string text, string srcLang)
    {
        // SentencePieceTokenizer でサブワード piece 文字列を取得
        var tokens = _spm.EncodeToTokens(text, out _,
            addBeginningOfSentence: false,
            addEndOfSentence:       false);

        var ids = new long[tokens.Count + 2];
        ids[0] = GetLangId(srcLang);
        for (int i = 0; i < tokens.Count; i++)
        {
            string piece = tokens[i].Value;
            if (!_tokenToId.TryGetValue(piece, out int vocabId))
                _tokenToId.TryGetValue("<unk>", out vocabId);
            ids[i + 1] = vocabId;
        }
        ids[tokens.Count + 1] = EosId;
        return ids;
    }

    /// <summary>
    /// token ID リストをテキストに復元する。
    /// SentencePiece BPE の piece 文字列を連結し ▁(U+2581) をスペースに置換。
    /// </summary>
    internal string Decode(IList<long> ids)
    {
        if (ids.Count == 0) return "";
        var sb = new StringBuilder(ids.Count * 4);
        foreach (var id in ids)
        {
            if (!_idToToken.TryGetValue((int)id, out var token)) continue;
            if (IsSpecialToken(token)) continue;
            sb.Append(token);
        }
        // ▁ (U+2581) はワード先頭スペースを表す SentencePiece 規約
        return sb.ToString().Replace('\u2581', ' ').Trim();
    }

    internal long GetLangId(string lang)
    {
        var normalized = NormalizeLangCode(lang);
        if (_langIds.TryGetValue(normalized, out long id)) return id;
        if (_langIds.TryGetValue("en",       out long en)) return en;
        throw new InvalidOperationException($"[M2M100Tokenizer] lang not found: {normalized}");
    }

    internal long EosToken => EosId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // LlamaTokenizer/SentencePieceTokenizer does not implement IDisposable in 0.22.0
    }

    // ── helpers ──────────────────────────────────────────────
    private static string NormalizeLangCode(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "en";
        return lang.Trim().ToLowerInvariant() switch
        {
            "zh-cn" or "zh-tw" => "zh",
            "jp"               => "ja",
            var s              => s
        };
    }

    private static Dictionary<string, long> LoadLangIds(string configPath)
    {
        var json   = File.ReadAllText(configPath, Encoding.UTF8);
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LangTokenRegex.Matches(json))
        {
            long tokenId = long.Parse(m.Groups[1].Value);
            string lang  = NormalizeLangCode(m.Groups[2].Value);
            result[lang] = tokenId;
        }
        if (result.Count == 0)
            throw new InvalidOperationException($"[M2M100Tokenizer] no lang tokens: {configPath}");
        return result;
    }

    private static Dictionary<string, int> LoadTokenToId(string vocabPath)
    {
        using var stream = File.OpenRead(vocabPath);
        using var doc    = JsonDocument.Parse(stream);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.GetInt32();
        return result;
    }

    private static bool IsSpecialToken(string? token) =>
        string.IsNullOrEmpty(token)
        || token.StartsWith('<')
        || (token.StartsWith("__") && token.EndsWith("__"));

    /// <summary>
    /// data 中の全 search バイト列を replace で上書きする (同バイト長限定 in-place patch)。
    /// </summary>
    private static void PatchBytes(byte[] data, ReadOnlySpan<byte> search, ReadOnlySpan<byte> replace)
    {
        int pos = 0;
        while (pos <= data.Length - search.Length)
        {
            int idx = data.AsSpan(pos).IndexOf(search);
            if (idx < 0) break;
            int absIdx = pos + idx;
            replace.CopyTo(data.AsSpan(absIdx, replace.Length));
            pos = absIdx + replace.Length;
        }
    }
}

// ── ONNX m2m100 推論 ────────────────────────────────────────
// V1 OrtTranslator.translateM2M100() の C# 移植。
internal sealed class OrtM2M100 : IDisposable
{
    private const int MaxNewTokens         = 32;
    private const int MaxRepeatTokenStreak = 8;
    private const int LayerCount           = 12;

    private readonly M2M100Tokenizer  _tokenizer;
    private readonly InferenceSession _encoderSession;
    private readonly InferenceSession _decoderSession;
    private readonly InferenceSession? _decoderWithPastSession;
    private readonly object _runLock = new();
    private bool _disposed;

    internal OrtM2M100(string modelDir, int numThreads, ILogger? logger = null)
    {
        _tokenizer = new M2M100Tokenizer(modelDir);

        var opts = new Microsoft.ML.OnnxRuntime.SessionOptions();
        if (numThreads > 0) opts.IntraOpNumThreads = numThreads;

        string encFile          = PickOnnxFile(modelDir, "encoder_model");
        string decFile          = PickOnnxFile(modelDir, "decoder_model");
        string? decWithPastFile = PickOptionalOnnxFile(modelDir, "decoder_with_past_model");

        _encoderSession = new InferenceSession(Path.Combine(modelDir, encFile), opts);
        _decoderSession = new InferenceSession(Path.Combine(modelDir, decFile), opts);
        if (decWithPastFile != null)
        {
            _decoderWithPastSession = new InferenceSession(
                Path.Combine(modelDir, decWithPastFile), opts);
        }
        logger?.LogInformation("[OrtM2M100] loaded enc={Enc} dec={Dec} withPast={WP}",
            encFile, decFile, decWithPastFile ?? "none");
    }

    internal string Translate(string text, string srcLang, string tgtLang)
    {
        lock (_runLock)
            return TranslateCore(text, srcLang, tgtLang);
    }

    // ── Core inference ───────────────────────────────────────
    private string TranslateCore(string text, string srcLang, string tgtLang)
    {
        var original = text.Trim();
        if (original.Length == 0) return "";

        long[] inputIds = _tokenizer.Encode(original, srcLang);
        long[] attnMask = Ones(inputIds.Length);

        // ── Encoder ──────────────────────────────────────────
        float[] encoderHiddenFlat;
        int[] encoderHiddenDims;
        {
            var inT   = MakeLong2D(inputIds, 1, inputIds.Length);
            var attT  = MakeLong2D(attnMask, 1, attnMask.Length);
            var encInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids",      inT),
                NamedOnnxValue.CreateFromTensor("attention_mask", attT),
            };
            using var encOut = _encoderSession.Run(encInputs);
            var encHidden    = encOut.First(o => o.Name == "last_hidden_state").AsTensor<float>();
            encoderHiddenDims = encHidden.Dimensions.ToArray();
            encoderHiddenFlat = encHidden.ToArray();
        }

        // ── Decoder loop ─────────────────────────────────────
        long tgtLangId    = _tokenizer.GetLangId(tgtLang);
        var generated     = new List<long> { _tokenizer.EosToken }; // decoder_start = EOS(2)
        long prevToken    = long.MinValue;
        int repeatStreak  = 0;
        M2M100Past? past  = null;

        // step 0: run full decoder with [EOS], extract past, push forced_bos(tgtLangId)
        {
            var (nextId0, past0) = RunDecoder(
                [.. generated],
                attnMask, encoderHiddenFlat, encoderHiddenDims,
                includeEncoderPast: true, existingPast: null);
            _ = nextId0; // first step result ignored (forced_bos overrides)
            past = past0;
            generated.Add(tgtLangId);
        }

        // step 1 .. MAX_NEW_TOKENS
        for (int step = 1; step < MaxNewTokens; step++)
        {
            long nextId;
            M2M100Past? nextPast;

            if (_decoderWithPastSession != null && past != null)
                (nextId, nextPast) = RunDecoderWithPast(
                    generated[^1], attnMask, past);
            else
                (nextId, nextPast) = RunDecoder(
                    [.. generated], attnMask, encoderHiddenFlat, encoderHiddenDims,
                    includeEncoderPast: false, existingPast: past);

            past?.Dispose();
            past = nextPast;

            if (nextId == _tokenizer.EosToken) break;
            if (nextId == prevToken) repeatStreak++;
            else { repeatStreak = 1; prevToken = nextId; }
            generated.Add(nextId);
            if (repeatStreak >= MaxRepeatTokenStreak) break;
        }
        past?.Dispose();

        // generated = [EOS, tgtLangId, ...tokens...] → skip first 2
        var decoded = generated.Count > 2
            ? _tokenizer.Decode(generated.GetRange(2, generated.Count - 2))
            : "";
        return SanitizeOutput(original, decoded);
    }

    // ── Full decoder ─────────────────────────────────────────
    private (long nextId, M2M100Past? past) RunDecoder(
        long[] inputIds, long[] attnMask,
        float[] encHidFlat, int[] encHidDims,
        bool includeEncoderPast, M2M100Past? existingPast)
    {
        var decT    = MakeLong2D(inputIds, 1, inputIds.Length);
        var encHidT = MakeFloat(encHidFlat, encHidDims);
        var encAttT = MakeLong2D(attnMask,  1, attnMask.Length);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",              decT),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states",  encHidT),
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encAttT),
        };

        using var decOut = _decoderSession.Run(inputs);
        var logits  = decOut.First(o => o.Name == "logits").AsTensor<float>();
        long nextId = Argmax(logits, inputIds.Length - 1);
        var past    = ExtractPast(decOut, includeEncoderPast, existingPast);
        return (nextId, past);
    }

    // ── Decoder with past ────────────────────────────────────
    private (long nextId, M2M100Past? past) RunDecoderWithPast(
        long inputId, long[] attnMask, M2M100Past past)
    {
        var decT    = MakeLong2D([inputId], 1, 1);
        var encAttT = MakeLong2D(attnMask,  1, attnMask.Length);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",              decT),
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encAttT),
        };

        for (int i = 0; i < LayerCount; i++)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.decoder.key",   past.DecoderKeys[i]));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.decoder.value", past.DecoderValues[i]));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.encoder.key",   past.EncoderKeys[i]));
            inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{i}.encoder.value", past.EncoderValues[i]));
        }

        using var decOut = _decoderWithPastSession!.Run(inputs);
        var logits  = decOut.First(o => o.Name == "logits").AsTensor<float>();
        long nextId = Argmax(logits, 0);
        var nextPast = ExtractPast(decOut, includeEncoderPast: false, existingPast: past);
        return (nextId, nextPast);
    }

    // ── Past KV extraction ───────────────────────────────────
    private static M2M100Past ExtractPast(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result,
        bool includeEncoderPast, M2M100Past? existingPast)
    {
        var resultMap = result.ToDictionary(o => o.Name, o => o);

        var decKeys   = new DenseTensor<float>[LayerCount];
        var decValues = new DenseTensor<float>[LayerCount];
        DenseTensor<float>[] encKeys, encValues;

        if (includeEncoderPast)
        {
            encKeys   = new DenseTensor<float>[LayerCount];
            encValues = new DenseTensor<float>[LayerCount];
        }
        else
        {
            // Reuse encoder KV from previous step (they don't change after first decode)
            encKeys   = existingPast?.EncoderKeys   ?? new DenseTensor<float>[LayerCount];
            encValues = existingPast?.EncoderValues ?? new DenseTensor<float>[LayerCount];
        }

        for (int i = 0; i < LayerCount; i++)
        {
            decKeys[i]   = CopyTensor(resultMap[$"present.{i}.decoder.key"]);
            decValues[i] = CopyTensor(resultMap[$"present.{i}.decoder.value"]);
            if (includeEncoderPast)
            {
                encKeys[i]   = CopyTensor(resultMap[$"present.{i}.encoder.key"]);
                encValues[i] = CopyTensor(resultMap[$"present.{i}.encoder.value"]);
            }
        }
        return new M2M100Past(encKeys, encValues, decKeys, decValues);
    }

    // ── Tensor helpers ───────────────────────────────────────
    private static DenseTensor<long> MakeLong2D(long[] data, int d0, int d1)
    {
        var t = new DenseTensor<long>(new[] { d0, d1 });
        for (int i = 0; i < data.Length && i < d0 * d1; i++)
            t.Buffer.Span[i] = data[i];
        return t;
    }

    private static DenseTensor<float> MakeFloat(float[] data, int[] dims)
    {
        var t = new DenseTensor<float>(dims);
        data.AsSpan().CopyTo(t.Buffer.Span);
        return t;
    }

    private static DenseTensor<float> CopyTensor(DisposableNamedOnnxValue src)
    {
        var tensor = src.AsTensor<float>();
        var dims   = tensor.Dimensions.ToArray();
        var data   = tensor.ToArray();  // managed copy before native memory is freed
        return new DenseTensor<float>(data, dims);
    }

    private static long[] Ones(int len)
    {
        var a = new long[len]; Array.Fill(a, 1L); return a;
    }

    private static long Argmax(Tensor<float> logits, int seqPos)
    {
        int vocab = logits.Dimensions[2];
        long best = 0; float bestVal = logits[0, seqPos, 0];
        for (int i = 1; i < vocab; i++)
        {
            float v = logits[0, seqPos, i];
            if (v > bestVal) { bestVal = v; best = i; }
        }
        return best;
    }

    private static string PickOnnxFile(string dir, string baseName)
    {
        var q = baseName + "_quantized.onnx";
        return File.Exists(Path.Combine(dir, q)) ? q : baseName + ".onnx";
    }

    private static string? PickOptionalOnnxFile(string dir, string baseName)
    {
        var q = baseName + "_quantized.onnx";
        if (File.Exists(Path.Combine(dir, q))) return q;
        var plain = baseName + ".onnx";
        return File.Exists(Path.Combine(dir, plain)) ? plain : null;
    }

    // ── Output sanitize (V1 port) ────────────────────────────
    private static string SanitizeOutput(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return original;
        var norm = Regex.Replace(translated.Trim(), @"\s+", " ");
        if (norm.Length == 0) return original;
        if (IsClearlyBroken(norm)) return original;
        return norm;
    }

    private static bool IsClearlyBroken(string text)
    {
        var compact = Regex.Replace(text, @"\s+", "");
        if (compact.Length >= 12 && Regex.IsMatch(compact, @"^(.{1,4})\1{4,}$")) return true;
        if (compact.Length >= 24 && HasVeryLowDiversity(compact)) return true;
        return false;
    }

    private static bool HasVeryLowDiversity(string text)
    {
        var set = new HashSet<char>();
        foreach (char c in text) { set.Add(c); if (set.Count > 6) return false; }
        return set.Count <= 4;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenizer.Dispose();
        _encoderSession.Dispose();
        _decoderSession.Dispose();
        _decoderWithPastSession?.Dispose();
    }

    // ── Past KV container ────────────────────────────────────
    private sealed class M2M100Past(
        DenseTensor<float>[] encoderKeys,
        DenseTensor<float>[] encoderValues,
        DenseTensor<float>[] decoderKeys,
        DenseTensor<float>[] decoderValues) : IDisposable
    {
        internal readonly DenseTensor<float>[] EncoderKeys   = encoderKeys;
        internal readonly DenseTensor<float>[] EncoderValues = encoderValues;
        internal readonly DenseTensor<float>[] DecoderKeys   = decoderKeys;
        internal readonly DenseTensor<float>[] DecoderValues = decoderValues;

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // DenseTensor<float> は managed memory のため GC 任せで OK
        }
    }
}

// ── TranslationService (公開エントリーポイント) ─────────────
public sealed class TranslationService : IDisposable
{
    private OrtM2M100?    _engine;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsLoaded { get { lock (_lock) return _engine != null; } }

    /// <summary>
    /// appRoot/models/m2m100_onnx_int8 を検出してロードする。
    /// 失敗時は false を返す（例外を投げない）。
    /// </summary>
    public bool Load(string appRoot, ILogger? logger = null)
    {
        var modelDir = Path.Combine(appRoot, "models", "m2m100_onnx_int8");
        if (!IsModelDirComplete(modelDir))
        {
            logger?.LogWarning("[Translation] model dir incomplete or missing: {Dir}", modelDir);
            return false;
        }
        try
        {
            var engine = new OrtM2M100(modelDir, numThreads: 2, logger);
            lock (_lock)
            {
                _engine?.Dispose();
                _engine = engine;
            }
            logger?.LogInformation("[Translation] loaded OK: {Dir}", modelDir);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Translation] load failed");
            return false;
        }
    }

    /// <summary>
    /// text を srcLang → tgtLang に翻訳して返す。
    /// tgtLang が "OFF" / 空 / ロード前の場合は null を返す（例外なし）。
    /// 翻訳結果が原文と同じ場合も null を返す。
    /// </summary>
    public string? Translate(string text, string srcLang, string tgtLang)
    {
        if (string.IsNullOrWhiteSpace(tgtLang) || tgtLang.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        OrtM2M100? engine;
        lock (_lock) { engine = _engine; }
        if (engine == null) return null;

        try
        {
            var result = engine.Translate(text.Trim(),
                srcLang.Trim().ToLowerInvariant(),
                tgtLang.Trim().ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(result) || result == text.Trim()) return null;
            return result.Trim();
        }
        catch (Exception ex)
        {
            _ = ex; // 呼び出し元でロギング
            return null;
        }
    }

    public void Unload()
    {
        lock (_lock) { _engine?.Dispose(); _engine = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }

    private static bool IsModelDirComplete(string dir) =>
        Directory.Exists(dir)
        && File.Exists(Path.Combine(dir, "sentencepiece.bpe.model"))
        && File.Exists(Path.Combine(dir, "tokenizer_config.json"))
        && File.Exists(Path.Combine(dir, "vocab.json"))
        && (File.Exists(Path.Combine(dir, "encoder_model.onnx"))
            || File.Exists(Path.Combine(dir, "encoder_model_quantized.onnx")))
        && (File.Exists(Path.Combine(dir, "decoder_model.onnx"))
            || File.Exists(Path.Combine(dir, "decoder_model_quantized.onnx")));
}
