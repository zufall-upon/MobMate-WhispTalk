using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace MobMate.Core.Asr;

/// <summary>
/// SenseVoice (sherpa-onnx) を使って ZH/JA/KO/EN/YUE の音声を認識する。
/// 言語ごとに recognizer をキャッシュし、言語パラメータを明示して精度を確保する。
/// モデルが存在しない場合は <see cref="IsAvailable"/> = false になり呼び出し元は既存 ASR にフォールバックする。
/// スレッドセーフ: Transcribe は lock で保護済み（OfflineRecognizer は非スレッドセーフ）。
/// </summary>
public sealed class SenseVoiceAsrService : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _modelPath;
    private readonly string _tokensPath;
    private readonly Dictionary<string, OfflineRecognizer> _cache = new();
    private readonly object _lock = new();
    private bool _disposed;
    private bool _modelsExist;

    public bool IsAvailable => _modelsExist && !_disposed;

    // sherpa-onnx SenseVoice の言語コードマッピング
    // セッション言語コード → SenseVoice Language フィールド
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"]  = "ja",
        ["zh"]  = "zh",
        ["ko"]  = "ko",
        ["en"]  = "en",
        ["yue"] = "yue",
    };

    public SenseVoiceAsrService(string modelDir, ILogger logger)
    {
        _logger = logger;
        _modelPath  = Path.Combine(modelDir, "model.int8.onnx");
        _tokensPath = Path.Combine(modelDir, "tokens.txt");

        if (!File.Exists(_modelPath) || !File.Exists(_tokensPath))
        {
            _logger.LogWarning("[SenseVoice] モデルファイルが見つかりません: {Dir}", modelDir);
            return;
        }

        // 動作確認: ja で1つ作成しておく
        try
        {
            var r = CreateRecognizer("ja");
            _cache["ja"] = r;
            _modelsExist = true;
            _logger.LogInformation("[SenseVoice] 初期化完了: {Dir}", modelDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SenseVoice] 初期化失敗 (フォールバック動作)");
        }
    }

    private OfflineRecognizer CreateRecognizer(string svLang)
    {
        var config = new OfflineRecognizerConfig
        {
            ModelConfig = new OfflineModelConfig
            {
                SenseVoice = new OfflineSenseVoiceModelConfig
                {
                    Model    = _modelPath,
                    Language = svLang,   // ← 言語を明示
                    UseInverseTextNormalization = 1,
                },
                Tokens     = _tokensPath,
                NumThreads = 1,
                Debug      = 0,
                Provider   = "cpu",
            },
        };
        return new OfflineRecognizer(config);
    }

    private OfflineRecognizer? GetOrCreate(string sessionLanguage)
    {
        // セッション言語 → SenseVoice 言語コード (未知なら "auto")
        var svLang = LangMap.TryGetValue(sessionLanguage, out var mapped) ? mapped : "auto";
        if (_cache.TryGetValue(svLang, out var cached)) return cached;
        try
        {
            var r = CreateRecognizer(svLang);
            _cache[svLang] = r;
            _logger.LogDebug("[SenseVoice] 認識器作成: lang={Lang}", svLang);
            return r;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SenseVoice] 認識器作成失敗: lang={Lang}", svLang);
            return null;
        }
    }

    /// <summary>
    /// float32 PCM サンプル (16 kHz, mono) を渡してテキストを返す。
    /// <paramref name="sessionLanguage"/> はセッション設定言語 (ja/zh/ko/en/yue)。
    /// 失敗時は null を返す。
    /// </summary>
    public string? Transcribe(float[] samples, int sampleRate = 16000, string sessionLanguage = "auto")
    {
        if (!_modelsExist || _disposed || samples.Length == 0)
            return null;

        lock (_lock)
        {
            if (_disposed) return null;
            try
            {
                var recognizer = GetOrCreate(sessionLanguage);
                if (recognizer == null) return null;

                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(sampleRate, samples);
                recognizer.Decode(stream);
                return stream.Result.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SenseVoice] 推論エラー (lang={Lang})", sessionLanguage);
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var r in _cache.Values) r.Dispose();
            _cache.Clear();
        }
    }
}

/// <summary>
/// Paraformer-zh (sherpa-onnx) を使って中国語音声を認識する Linux/Windows 共通の軽量ラッパー。
/// </summary>
public sealed class ParaformerZhAsrService : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _modelPath;
    private readonly string _tokensPath;
    private readonly object _lock = new();
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public bool IsAvailable => _recognizer != null && !_disposed;

    public ParaformerZhAsrService(string modelDir, ILogger logger)
    {
        _logger = logger;
        _modelPath = Path.Combine(modelDir, "model.int8.onnx");
        _tokensPath = Path.Combine(modelDir, "tokens.txt");

        if (!File.Exists(_modelPath) || !File.Exists(_tokensPath))
        {
            _logger.LogWarning("[ParaformerZh] モデルファイルが見つかりません: {Dir}", modelDir);
            return;
        }

        try
        {
            _recognizer = new OfflineRecognizer(new OfflineRecognizerConfig
            {
                ModelConfig = new OfflineModelConfig
                {
                    Paraformer = new OfflineParaformerModelConfig { Model = _modelPath },
                    Tokens = _tokensPath,
                    NumThreads = 1,
                    Debug = 0,
                    Provider = "cpu",
                },
            });
            _logger.LogInformation("[ParaformerZh] 初期化完了: {Dir}", modelDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ParaformerZh] 初期化失敗 (フォールバック動作)");
        }
    }

    public string? Transcribe(float[] samples, int sampleRate = 16000)
    {
        if (_recognizer == null || _disposed || samples.Length == 0)
            return null;

        lock (_lock)
        {
            if (_recognizer == null || _disposed) return null;
            try
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(sampleRate, samples);
                _recognizer.Decode(stream);
                return CleanZhText(stream.Result.Text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ParaformerZh] 推論エラー");
                return null;
            }
        }
    }

    private static string? CleanZhText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Replace(" ", string.Empty).Replace("\t", string.Empty).Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
