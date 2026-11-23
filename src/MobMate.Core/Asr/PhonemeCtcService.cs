using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MobMate.Core.Asr;

/// <summary>
/// wav2vec2 (facebook/wav2vec2-xlsr-53-espeak-cv-ft) の ONNX を Microsoft.ML.OnnxRuntime で直接推論し、
/// 16kHz mono PCM から IPA 音素列 (space 区切り) を得る CTC 音素 ASR。
///
/// 用途: PhoneticAssist の補助レーン。
///   - レーンA: 新語/スラング補正の音素照合スコアリング。
///   - レーンB: Whisper が落とすフィラー (あー/なー等) を音素→かな化して捕捉。
///
/// モデルが無ければ <see cref="IsAvailable"/> = false。呼び出し元は既存 ASR にそのままフォールバックする。
/// スレッドセーフ: <see cref="Transcribe"/> は lock で保護 (InferenceSession 自体は再入可だが入出力整合のため直列化)。
/// 依存追加なし (Microsoft.ML.OnnxRuntime は既存)。
/// </summary>
public sealed class PhonemeCtcService : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly InferenceSession? _session;
    private readonly Dictionary<int, string> _id2token = new();
    private readonly int _blankId;
    private readonly int _sampleRate;
    private readonly bool _doNormalize;
    private readonly string _inputName;
    private bool _disposed;

    public bool IsAvailable => _session != null && !_disposed;

    /// <summary>モデル入力が要求するサンプリングレート (通常 16000)。</summary>
    public int SampleRate => _sampleRate;

    public PhonemeCtcService(string modelDir, ILogger logger)
    {
        _logger = logger;
        _blankId = 0;
        _sampleRate = 16000;
        _doNormalize = true;
        _inputName = "input_values";

        var modelPath = Path.Combine(modelDir, "model.int8.onnx");
        var tokenPath = Path.Combine(modelDir, "id2token.json");
        var metaPath = Path.Combine(modelDir, "meta.json");

        if (!File.Exists(modelPath) || !File.Exists(tokenPath))
        {
            _logger.LogWarning("[PhonemeCtc] モデルファイルが見つかりません: {Dir}", modelDir);
            return;
        }

        try
        {
            LoadTokens(tokenPath);
            LoadMeta(metaPath, ref _blankId, ref _sampleRate, ref _doNormalize);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 2,
                InterOpNumThreads = 1,
            };
            _session = new InferenceSession(modelPath, options);
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "input_values";
            _logger.LogInformation("[PhonemeCtc] 初期化完了: {Dir} (tokens={N}, sr={Sr})", modelDir, _id2token.Count, _sampleRate);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            _logger.LogWarning(ex, "[PhonemeCtc] 初期化失敗 (フォールバック動作)");
        }
    }

    private void LoadTokens(string path)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (map == null) throw new InvalidOperationException("id2token.json parse failed");
        foreach (var kv in map)
            if (int.TryParse(kv.Key, out var id))
                _id2token[id] = kv.Value;
    }

    private static void LoadMeta(string path, ref int blankId, ref int sampleRate, ref bool doNormalize)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("pad_id", out var p) && p.TryGetInt32(out var pid)) blankId = pid;
        if (root.TryGetProperty("sampling_rate", out var s) && s.TryGetInt32(out var sr)) sampleRate = sr;
        if (root.TryGetProperty("do_normalize", out var n) && (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False)) doNormalize = n.GetBoolean();
    }

    /// <summary>
    /// float32 PCM (16kHz mono, [-1,1]) を渡して IPA 音素列を space 区切りで返す。
    /// 無音/空入力や失敗時は null、無発声 (CTC が全 blank) は空文字を返す。
    /// </summary>
    public string? Transcribe(float[] samples)
    {
        if (!IsAvailable || samples.Length == 0) return null;

        lock (_lock)
        {
            if (_disposed || _session == null) return null;
            try
            {
                var input = Normalize(samples);
                var tensor = new DenseTensor<float>(input, [1, input.Length]);
                using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
                var logits = outputs.First().AsTensor<float>();
                return CtcGreedyDecode(logits);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PhonemeCtc] 推論エラー");
                return null;
            }
        }
    }

    private float[] Normalize(float[] samples)
    {
        if (!_doNormalize) return samples;
        double mean = 0;
        for (var i = 0; i < samples.Length; i++) mean += samples[i];
        mean /= samples.Length;
        double var = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var d = samples[i] - mean;
            var += d * d;
        }
        var std = Math.Sqrt(var / samples.Length);
        var inv = 1.0 / (std + 1e-7);
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            result[i] = (float)((samples[i] - mean) * inv);
        return result;
    }

    /// <summary>logits [1, T, V] を argmax → CTC collapse (blank除去 + 連続重複除去) → IPA token を space 連結。</summary>
    private string CtcGreedyDecode(Tensor<float> logits)
    {
        var t = logits.Dimensions[1];
        var v = logits.Dimensions[2];
        var flat = logits.ToArray(); // length = T * V
        var tokens = new List<string>();
        var prev = -1;
        for (var ti = 0; ti < t; ti++)
        {
            var off = ti * v;
            var best = 0;
            var bestVal = flat[off];
            for (var vi = 1; vi < v; vi++)
            {
                var val = flat[off + vi];
                if (val > bestVal)
                {
                    bestVal = val;
                    best = vi;
                }
            }
            if (best == _blankId)
            {
                prev = -1;
                continue;
            }
            if (best == prev) continue;
            prev = best;
            if (_id2token.TryGetValue(best, out var tok) && !IsSpecial(tok))
                tokens.Add(tok);
        }
        return string.Join(' ', tokens);
    }

    private static bool IsSpecial(string tok)
        => tok is "<pad>" or "<s>" or "</s>" or "<unk>";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _session?.Dispose();
        }
    }
}
