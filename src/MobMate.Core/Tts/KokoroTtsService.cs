using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace MobMate.Core.Tts;

/// <summary>
/// Kokoro TTS (sherpa-onnx) engine wrapper.
/// Synthesis is sequential: concurrent calls are serialized via _currentTask.
/// Thread-safety: SetConfig/Dispose take _lock; synthesis takes _lock for the call duration.
/// </summary>
public sealed class KokoroTtsService : IDisposable
{
    private OfflineTts? _tts;
    private string? _modelDir;
    private int _speakerId = 3;
    private float _speed = 1.0f;
    private string? _lastError;
    private bool _modelInstalled;
    private bool _supportsJapanese; // true only if model language contains "ja"
    private readonly object _lock = new();
    private Task? _currentTask;

    public bool IsModelInstalled => _modelInstalled;
    public string? ModelDir => _modelDir;
    public string? LastError => _lastError;

    /// <summary>
    /// Reconfigures the service. If modelDir changed, disposes previous OfflineTts and reinitializes.
    /// </summary>
    public void SetConfig(string? modelDir, int speakerId = 3, float speed = 1.0f)
    {
        lock (_lock)
        {
            _speakerId = Math.Clamp(speakerId, 0, 999);
            _speed = Math.Clamp(speed, 0.5f, 2.0f);

            bool dirChanged = !string.Equals(_modelDir, modelDir, StringComparison.OrdinalIgnoreCase);
            if (!dirChanged && _tts != null) return;

            _modelDir = modelDir;
            _tts?.Dispose();
            _tts = null;
            _lastError = null;
            _modelInstalled = false;
            _supportsJapanese = false;

            if (!string.IsNullOrEmpty(modelDir))
            {
                try
                {
                    _lastError = GetModelValidationError(modelDir);
                    if (_lastError != null) return;

                    // Read model language from properties to determine Japanese support
                    var propsPath = Path.Combine(modelDir, "mobmate-model.properties");
                    if (File.Exists(propsPath))
                    {
                        foreach (var line in File.ReadLines(propsPath))
                        {
                            var l = line.Trim();
                            if (l.StartsWith("language=", StringComparison.OrdinalIgnoreCase))
                            {
                                var lang = l["language=".Length..].Trim();
                                _supportsJapanese = lang.Contains("ja", StringComparison.OrdinalIgnoreCase);
                                break;
                            }
                        }
                    }
                    _tts = BuildOfflineTts(modelDir);
                    _modelInstalled = true;
                }
                catch (Exception ex) { _lastError = ex.Message; _modelInstalled = false; }
            }
        }
    }

    /// <summary>Checks known prerequisites for native initialization, not model integrity.</summary>
    public static bool IsModelDirValid(string dir) => GetModelValidationError(dir) == null;

    private static string? GetModelValidationError(string dir)
    {
        const long voiceBytesPerSpeaker = 510L * 256 * sizeof(float);
        const string error = "Kokoro model is incomplete or unreadable. Remove it and download it again.";
        try
        {
            if (!Directory.Exists(dir)) return error;
            var model = File.Exists(Path.Combine(dir, "model.int8.onnx")) ? "model.int8.onnx" : "model.onnx";
            bool Readable(string name, long minimum, long? exact = null)
            {
                using var stream = new FileStream(Path.Combine(dir, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                return stream.Length >= minimum && (!exact.HasValue || stream.Length == exact.Value) && stream.ReadByte() >= 0;
            }

            var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            int speakers = id switch
            {
                "kokoro-int8-multi-lang-v1_1" => 103,
                "kokoro-int8-multi-lang-v1_0" => 53,
                "kokoro-int8-en-v0_19" => 11,
                _ => 0
            };
            // The v1.1 int8 length is from the retained downloaded fixture, not an official hash.
            long? modelLength = id == "kokoro-int8-multi-lang-v1_1" && model == "model.int8.onnx" ? 114203914L : null;
            if (!Readable(model, 1024 * 1024, modelLength) || !Readable("tokens.txt", 1)) return error;
            using (var voices = new FileStream(Path.Combine(dir, "voices.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (voices.Length < voiceBytesPerSpeaker || voices.Length % voiceBytesPerSpeaker != 0 ||
                    (speakers > 0 && voices.Length != speakers * voiceBytesPerSpeaker) || voices.ReadByte() < 0) return error;
            }
            foreach (var name in new[] { "phondata", "phonindex", "phontab" })
                if (!Readable(Path.Combine("espeak-ng-data", name), 1)) return error;
            var lexicons = Directory.GetFiles(dir, "lexicon-*.txt");
            if (id == "kokoro-int8-multi-lang-v1_1" &&
                new[] { "lexicon-gb-en.txt", "lexicon-us-en.txt", "lexicon-zh.txt" }.Any(name => !lexicons.Any(path => Path.GetFileName(path) == name))) return error;
            foreach (var lexicon in lexicons)
                if (!Readable(Path.GetFileName(lexicon), 1)) return error;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return error;
        }
    }

    /// <summary>Synthesizes text to PCM-16 WAV bytes.</summary>
    public async Task<KokoroResult> SynthesizeAsync(string text, CancellationToken ct = default, TtsProsodyProfile? prosodyProfile = null)
    {
        // Sequential execution: wait for previous synthesis.
        if (_currentTask != null)
        {
            try { await _currentTask.ConfigureAwait(false); }
            catch { /* ignore previous failure */ }
        }

        var task = Task.Run(() =>
        {
            lock (_lock)
            {
                if (_tts == null) return KokoroResult.Fail(_lastError ?? "Kokoro not initialized or model not installed");
                if (string.IsNullOrWhiteSpace(text)) return KokoroResult.Fail("Empty text");
                // Skip Japanese text if the model does not support it (Kokoro en/zh models).
                // espeak-ng would output "JapaneseJapanese..." for hiragana/katakana input.
                if (!_supportsJapanese && ContainsJapaneseSyllabary(text))
                    return KokoroResult.Fail("skip: Japanese text not supported by this Kokoro model");
                try
                {
                    var genConfig = new OfflineTtsGenerationConfig
                    {
                        Sid = _speakerId,
                        Speed = ApplyProsodySpeed(_speed, prosodyProfile),
                        SilenceScale = 0.2f,
                    };
                    var audio = _tts.GenerateWithConfig(text, genConfig, null);
                    return KokoroResult.Ok(FloatSamplesToWav(audio.Samples, audio.SampleRate));
                }
                catch (Exception ex)
                {
                    return KokoroResult.Fail($"Kokoro synthesis error: {ex.Message}");
                }
            }
        }, ct);

        _currentTask = task;
        try { return await task.ConfigureAwait(false); }
        catch (Exception ex) { return KokoroResult.Fail($"Kokoro task error: {ex.Message}"); }
    }

    // ---- helpers ----

    private static float ApplyProsodySpeed(float baseSpeed, TtsProsodyProfile? profile)
    {
        if (profile is not { } p || !p.IsUsable()) return baseSpeed;
        float weight = Math.Clamp(p.Confidence <= 0 ? 0.55f : p.Confidence, 0.25f, 1.0f);
        float moodBias = p.IsDarkLike() ? -0.05f : p.EnergyScore >= 0.25f ? 0.04f : 0.0f;
        float target = 1.0f + ((p.SpeedScale - 1.0f) * weight) + moodBias;
        return Math.Clamp(baseSpeed * target, 0.65f, 1.35f);
    }

    private static OfflineTts BuildOfflineTts(string modelDir)
    {
        var modelFile = File.Exists(Path.Combine(modelDir, "model.int8.onnx"))
            ? Path.Combine(modelDir, "model.int8.onnx")
            : Path.Combine(modelDir, "model.onnx");

        var lexicons = Directory.GetFiles(modelDir, "lexicon-*.txt", SearchOption.TopDirectoryOnly)
                                .OrderBy(x => x).ToArray();

        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model   = modelFile;
        config.Model.Kokoro.Voices  = Path.Combine(modelDir, "voices.bin");
        config.Model.Kokoro.Tokens  = Path.Combine(modelDir, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(modelDir, "espeak-ng-data");
        config.Model.Kokoro.Lexicon = string.Join(",", lexicons);
        config.Model.Kokoro.LengthScale = 1.0f;
        config.Model.NumThreads = 2;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return new OfflineTts(config);
    }

    /// <summary>Returns true if text contains hiragana or katakana characters.</summary>
    private static bool ContainsJapaneseSyllabary(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') ||  // Hiragana
                (c >= '\u30A0' && c <= '\u30FF'))     // Katakana
                return true;
        }
        return false;
    }

    private static byte[] FloatSamplesToWav(float[] samples, int sampleRate)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int dataSize = samples.Length * 2;
        var wav = new byte[44 + dataSize];
        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + dataSize);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16);
        WriteInt16(wav, 20, 1);       // PCM
        WriteInt16(wav, 22, (short)channels);
        WriteInt32(wav, 24, sampleRate);
        WriteInt32(wav, 28, byteRate);
        WriteInt16(wav, 32, (short)(channels * bitsPerSample / 8));
        WriteInt16(wav, 34, (short)bitsPerSample);
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, dataSize);
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(samples[i] * 32767f, (float)short.MinValue, (float)short.MaxValue);
            wav[44 + i * 2]     = (byte)(s & 0xFF);
            wav[44 + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return wav;
    }

    private static void WriteAscii(byte[] buf, int offset, string val)
    { for (int i = 0; i < val.Length; i++) buf[offset + i] = (byte)val[i]; }
    private static void WriteInt16(byte[] buf, int offset, short val)
    { buf[offset] = (byte)(val & 0xFF); buf[offset + 1] = (byte)((val >> 8) & 0xFF); }
    private static void WriteInt32(byte[] buf, int offset, int val)
    { buf[offset] = (byte)(val & 0xFF); buf[offset + 1] = (byte)((val >> 8) & 0xFF);
      buf[offset + 2] = (byte)((val >> 16) & 0xFF); buf[offset + 3] = (byte)((val >> 24) & 0xFF); }

    public void Dispose()
    {
        lock (_lock) { _tts?.Dispose(); _tts = null; }
    }
}

/// <summary>Result of Kokoro synthesis.</summary>
public readonly struct KokoroResult
{
    public bool Success { get; }
    public byte[]? WavBytes { get; }
    public string? Error { get; }
    private KokoroResult(bool ok, byte[]? wav, string? err) { Success = ok; WavBytes = wav; Error = err; }
    public static KokoroResult Ok(byte[] wav)    => new(true, wav, null);
    public static KokoroResult Fail(string err) => new(false, null, err);
}
