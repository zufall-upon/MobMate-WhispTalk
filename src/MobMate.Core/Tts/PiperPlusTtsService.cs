namespace MobMate.Core.Tts;

/// <summary>
/// Piper-Plus TTS engine via external process execution.
/// Runs piper.exe with model file to generate WAV output.
/// Supports emotion reflection via prosody parameters.
/// </summary>
public sealed class PiperPlusTtsService : IDisposable, IAsyncDisposable
{
    private string _exePath;
    private string? _modelPath;
    private bool _reflectEmotion;
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private readonly object _configLock = new();
    private CancellationTokenSource _configGeneration = new();

    private PiperPlusPersistentSessionManager? _sessionManager;
    private bool _usePersistent;
    private string? _configPath;
    private readonly Func<string, CancellationToken, Task<PiperResult>>? _fixtureSynthesis;

    public PiperPlusTtsService(string exePath, string? modelPath = null)
    {
        _exePath = exePath;
        _modelPath = modelPath;
        _reflectEmotion = true;
    }

    internal PiperPlusTtsService(string exePath, string modelPath,
        Func<string, CancellationToken, Task<PiperResult>> fixtureSynthesis) : this(exePath, modelPath)
        => _fixtureSynthesis = fixtureSynthesis;

    public void SetConfig(
        string? exePath = null,
        string? modelPath = null,
        bool? reflectEmotion = null,
        bool? usePersistent = null,
        string? configPath = null)
    {
        PiperPlusPersistentSessionManager? oldManager = null;
        lock (_configLock)
        {
            bool changed = (exePath != null && exePath != _exePath)
                || (modelPath != null && modelPath != _modelPath)
                || (configPath != null && configPath != _configPath)
                || (reflectEmotion.HasValue && reflectEmotion.Value != _reflectEmotion)
                || (usePersistent.HasValue && usePersistent.Value != _usePersistent);
            if (changed)
            {
                var oldGeneration = _configGeneration;
                _configGeneration = new CancellationTokenSource();
                oldGeneration.Cancel();
                oldGeneration.Dispose();
                oldManager = _sessionManager;
                _sessionManager = null;
            }
            if (exePath != null) _exePath = exePath;
            if (modelPath != null) _modelPath = modelPath;
            if (reflectEmotion.HasValue) _reflectEmotion = reflectEmotion.Value;
            if (configPath != null) _configPath = configPath;
            if (usePersistent.HasValue) _usePersistent = usePersistent.Value;
        }
        if (oldManager != null) _ = DisposeManagerCoreAsync(oldManager);
    }

    public string? ModelPath => _modelPath;

    /// <summary>
    /// Checks if Piper runtime is available.
    /// </summary>
    public bool IsRuntimeAvailable()
    {
        return File.Exists(_exePath);
    }

    /// <summary>
    /// Checks if the configured model is installed.
    /// </summary>
    public bool IsModelInstalled()
    {
        return !string.IsNullOrEmpty(_modelPath) && File.Exists(_modelPath);
    }

    // A caller consuming the result must retain the same generation until output.
    internal CancellationTokenSource CreateConfigurationCancellation(CancellationToken ct)
    {
        lock (_configLock)
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _configGeneration.Token);
    }

    /// <summary>
    /// Synthesizes text to WAV bytes via Piper-Plus process.
    /// </summary>
    public async Task<PiperResult> SynthesizeAsync(string text, CancellationToken ct = default, TtsProsodyProfile? sourceProsodyProfile = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PiperResult.Fail("Empty text");

        using (var linked = CreateConfigurationCancellation(ct))
        {
            await _synthesisGate.WaitAsync(linked.Token);
            try
            {
                Task<PiperResult> task;
                lock (_configLock)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!IsRuntimeAvailable()) return PiperResult.Fail($"Piper runtime not found: {_exePath}");
                    if (!IsModelInstalled()) return PiperResult.Fail("Piper model not installed or not configured");
                    // Start with one coherent generation; config changes cancel it.
                    task = _fixtureSynthesis != null ? _fixtureSynthesis(text, linked.Token) : DoSynthesizeAsync(text, linked.Token, sourceProsodyProfile);
                }
                var result = await task;
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                linked.Token.ThrowIfCancellationRequested();
                return PiperResult.Fail($"Piper synthesis error: {ex.Message}");
            }
            finally { _synthesisGate.Release(); }
        }
    }

    private async Task<PiperResult> DoSynthesizeAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsodyProfile)
    {
        ct.ThrowIfCancellationRequested();
        if (_usePersistent)
        {
            var persistentResult = await TrySynthesizePersistentAsync(text, ct, sourceProsodyProfile);
            if (persistentResult.HasValue) return persistentResult.Value;
        }
        Task<PiperResult?> rawTask;
        lock (_configLock)
        {
            ct.ThrowIfCancellationRequested();
            rawTask = TrySynthesizeRawStdoutAsync(text, ct, sourceProsodyProfile);
        }
        var rawResult = await rawTask;
        if (rawResult.HasValue) return rawResult.Value;
        Task<PiperResult> oneShotTask;
        lock (_configLock)
        {
            ct.ThrowIfCancellationRequested();
            oneShotTask = SynthesizeOneShotAsync(text, ct, sourceProsodyProfile);
        }
        return await oneShotTask;
    }

    private async Task<PiperResult?> TrySynthesizePersistentAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsodyProfile)
    {
        string? wavPath = null;
        string? configPath = ResolveConfigPath();

        if (!File.Exists(configPath))
        {
            return PiperResult.Fail($"Piper config not found: {configPath}");
        }

        PiperPlusPersistentSessionManager? manager = null;
        try
        {
            manager = _sessionManager ??= new PiperPlusPersistentSessionManager();

            var language = ResolveLanguageTag();
            float lengthScale = 1.0f;
            float noiseScale = 0.667f;
            float noiseW = 0.8f;

            if (_reflectEmotion)
            {
                var profile = ResolveProsodyProfile(text, sourceProsodyProfile);
                if (profile.Confidence >= 0.3f && profile.Mood != "neutral")
                {
                    lengthScale = 1.0f / Math.Max(0.5f, profile.SpeedScale);
                    noiseScale = 0.66f * profile.IntonationScale;
                    noiseW = 0.8f * profile.IntonationScale;
                }
            }

            var options = new PiperPlusPersistentSessionManager.SynthesisOptions(
                LanguageTag: language,
                LengthScaleArg: lengthScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                SentenceSilenceArg: "0",
                NoiseScaleArg: noiseScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                NoiseWArg: noiseW.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                SpeakerId: "0"
            );

            var result = await manager.SynthesizeAsync(
                _exePath,
                _modelPath!,
                configPath,
                text,
                options,
                ct);

            wavPath = result.WavPath;
            var wavBytes = await File.ReadAllBytesAsync(wavPath, ct);
            return PiperResult.Ok(wavBytes);
        }
        catch
        {
            if (manager != null)
            {
                lock (_configLock)
                {
                    if (ReferenceEquals(_sessionManager, manager)) _sessionManager = null;
                }
                try { await manager.DisposeAsync(); } catch { }
            }
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(wavPath))
            {
                try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            }
        }
    }

    private async Task<PiperResult?> TrySynthesizeRawStdoutAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsodyProfile)
    {
        try
        {
            var sampleRate = ResolveSampleRate();
            if (sampleRate <= 0) return null;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _exePath,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in BuildRawArguments(text, sourceProsodyProfile))
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Piper process");
            using var cancelProcess = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
            var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            var finishedTask = process.WaitForExitAsync(ct);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
            if (await Task.WhenAny(finishedTask, timeoutTask) != finishedTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            await finishedTask;

            var pcmBytes = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || pcmBytes.Length < 128)
            {
                Console.Error.WriteLine($"[PiperPlus] raw stdout failed exit={process.ExitCode} bytes={pcmBytes.Length} err={stderr}");
                return null;
            }

            return PiperResult.Ok(WrapPcm16MonoAsWav(pcmBytes, sampleRate));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PiperPlus] raw stdout fallback: {ex.Message}");
            return null;
        }
    }

    private async Task<PiperResult> SynthesizeOneShotAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsodyProfile)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"piper_{Guid.NewGuid():N}.wav");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _exePath,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in BuildArguments(text, wavPath, sourceProsodyProfile))
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Piper process");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                ct.ThrowIfCancellationRequested();
                return PiperResult.Fail("Piper synthesis timed out");
            }
            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                return PiperResult.Fail($"Piper failed (exit {process.ExitCode}): {stderr}");
            }

            if (!File.Exists(wavPath))
                return PiperResult.Fail("Piper did not produce WAV output");

            var wavBytes = await File.ReadAllBytesAsync(wavPath, ct);
            return PiperResult.Ok(wavBytes);
        }
        finally
        {
            // Cleanup temp WAV
            try
            {
                if (File.Exists(wavPath))
                    File.Delete(wavPath);
            }
            catch { }
        }
    }

    /// <summary>
    /// Builds Piper CLI arguments with optional emotion reflection.
    /// </summary>
    private List<string> BuildArguments(string text, string wavPath, TtsProsodyProfile? sourceProsodyProfile)
    {
        var args = new List<string>
        {
            "--model", _modelPath ?? string.Empty,
            "--output_file", wavPath,
            "--text", text,
            "--quiet"
        };
        var language = ResolveLanguageTag();
        if (!string.IsNullOrWhiteSpace(language))
        {
            args.Add("--language");
            args.Add(language);
        }

        if (!_reflectEmotion)
            return args;

        var profile = ResolveProsodyProfile(text, sourceProsodyProfile);
        if (profile.Confidence < 0.3f || profile.Mood == "neutral")
            return args;

        // Map prosody to Piper CLI options:
        // length-scale: 1.0 = normal, <1.0 = faster, >1.0 = slower
        // Piper's length-scale is inverse of speedScale
        float lengthScale = 1.0f / Math.Max(0.5f, profile.SpeedScale);
        args.Add("--length-scale");
        args.Add(lengthScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        // noise-scale: controls voice variation (higher = more variation)
        // Map from intonationScale: 1.0 = normal, >1.0 = more variation
        float noiseScale = 0.66f * profile.IntonationScale;
        args.Add("--noise-scale");
        args.Add(noiseScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        // noise-w: controls phoneme variation
        float noiseW = 0.8f * profile.IntonationScale;
        args.Add("--noise-w");
        args.Add(noiseW.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        return args;
    }

    private List<string> BuildRawArguments(string text, TtsProsodyProfile? sourceProsodyProfile)
    {
        var args = new List<string>
        {
            "--model", _modelPath ?? string.Empty,
            "--output_raw",
            "--text", text,
            "--speaker", "0",
            "--quiet"
        };
        var configPath = ResolveConfigPath();
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            args.InsertRange(2, new[] { "--config", configPath });
        }
        var language = ResolveLanguageTag();
        if (!string.IsNullOrWhiteSpace(language))
        {
            args.Add("--language");
            args.Add(language);
        }

        if (!_reflectEmotion)
            return args;

        var profile = ResolveProsodyProfile(text, sourceProsodyProfile);
        if (profile.Confidence < 0.3f || profile.Mood == "neutral")
            return args;

        float lengthScale = 1.0f / Math.Max(0.5f, profile.SpeedScale);
        args.Add("--length-scale");
        args.Add(lengthScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        float noiseScale = 0.66f * profile.IntonationScale;
        args.Add("--noise-scale");
        args.Add(noiseScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        float noiseW = 0.8f * profile.IntonationScale;
        args.Add("--noise-w");
        args.Add(noiseW.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        return args;
    }

    private static TtsProsodyProfile ResolveProsodyProfile(string text, TtsProsodyProfile? sourceProsodyProfile)
    {
        var textProfile = TtsProsodyAnalyzer.AnalyzeText(text);
        if (sourceProsodyProfile == null) return textProfile;
        return sourceProsodyProfile.Value.BlendWith(textProfile);
    }

    private string? ResolveConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(_configPath)) return _configPath;
        if (string.IsNullOrWhiteSpace(_modelPath)) return null;
        var dir = Path.GetDirectoryName(_modelPath) ?? string.Empty;
        var configJson = Path.Combine(dir, "config.json");
        if (File.Exists(configJson)) return configJson;
        var modelJson = _modelPath + ".json";
        if (File.Exists(modelJson)) return modelJson;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.onnx.json", SearchOption.TopDirectoryOnly).OrderBy(x => x).FirstOrDefault()
            : null;
    }

    private int ResolveSampleRate()
    {
        var configPath = ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("audio", out var audio)
                && audio.TryGetProperty("sample_rate", out var sampleRate)
                && sampleRate.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
            }
        }
        catch { }
        return 0;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static byte[] WrapPcm16MonoAsWav(byte[] pcmBytes, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataSize = pcmBytes.Length;
        var wav = new byte[44 + dataSize];
        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + dataSize);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16);
        WriteInt16(wav, 20, 1);
        WriteInt16(wav, 22, channels);
        WriteInt32(wav, 24, sampleRate);
        WriteInt32(wav, 28, byteRate);
        WriteInt16(wav, 32, blockAlign);
        WriteInt16(wav, 34, bitsPerSample);
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, dataSize);
        Buffer.BlockCopy(pcmBytes, 0, wav, 44, dataSize);
        return wav;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++) buffer[offset + i] = (byte)value[i];
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)((value >> 16) & 0xff);
        buffer[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    // The existing text-mode tag is not the complete voice capability contract:
    // ordinary monolingual espeak voices expose language.code in their JSON.
    internal bool SupportsFillerLanguage(string language)
    {
        lock (_configLock)
        {
            try
            {
                var tag = ResolveLanguageTag().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(tag))
                    return tag.Split(new[] { '-', '_', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains(language);
                var path = ResolveConfigPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("language", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !value.TryGetProperty("code", out var code) || code.ValueKind != System.Text.Json.JsonValueKind.String) return false;
                return (code.GetString() ?? "").ToLowerInvariant().Split(['-', '_'])[0] == language;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return false; }
        }
    }

    private string ResolveLanguageTag()
    {
        if (string.IsNullOrWhiteSpace(_modelPath)) return string.Empty;
        var propsPath = Path.Combine(Path.GetDirectoryName(_modelPath) ?? string.Empty, "mobmate-model.properties");
        if (File.Exists(propsPath))
        {
            string? language = null;
            foreach (var rawLine in File.ReadLines(propsPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var sep = line.IndexOf('=');
                if (sep <= 0) continue;
                var key = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();
                if (key.Equals("text_mode_language_tag", StringComparison.OrdinalIgnoreCase)) return value;
                if (key.Equals("language", StringComparison.OrdinalIgnoreCase)) language = value;
            }
            if (language != null) return language;
        }

        // A multilingual voice can require a matching text-mode phonemizer even
        // when no MobMate-specific properties file accompanies the model.
        try
        {
            var configPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return string.Empty;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("phoneme_type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String ||
                type.GetString() != "multilingual" || !root.TryGetProperty("language", out var lang) ||
                lang.ValueKind != System.Text.Json.JsonValueKind.Object || !lang.TryGetProperty("code", out var code) ||
                code.ValueKind != System.Text.Json.JsonValueKind.String)
                return string.Empty;
            var tag = code.GetString() ?? string.Empty;
            var parts = tag.Split('-');
            if (parts.Length < 2 || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length ||
                parts.Any(part => part is not ("ja" or "en" or "zh" or "ko" or "es" or "fr" or "pt" or "sv")))
                return string.Empty;
            return tag;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        PiperPlusPersistentSessionManager? manager;
        lock (_configLock)
        {
            var oldGeneration = _configGeneration;
            _configGeneration = new CancellationTokenSource();
            oldGeneration.Cancel();
            oldGeneration.Dispose();
            manager = _sessionManager;
            _sessionManager = null;
        }
        if (manager != null)
        {
            await manager.DisposeAsync();
        }
    }

    private static async Task DisposeManagerCoreAsync(PiperPlusPersistentSessionManager manager)
    {
        try { await manager.DisposeAsync(); } catch { }
    }
}

/// <summary>
/// Result of Piper-Plus synthesis.
/// </summary>
public readonly struct PiperResult
{
    public bool Success { get; }
    public byte[]? WavBytes { get; }
    public string? Error { get; }

    private PiperResult(bool success, byte[]? wavBytes, string? error)
    {
        Success = success;
        WavBytes = wavBytes;
        Error = error;
    }

    public static PiperResult Ok(byte[] wavBytes) => new(true, wavBytes, null);
    public static PiperResult Fail(string error) => new(false, null, error);
}
