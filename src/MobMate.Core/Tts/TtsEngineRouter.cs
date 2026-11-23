namespace MobMate.Core.Tts;

/// <summary>
/// Routes TTS requests to the configured engine.
/// Supports: windows_sapi, voicevox, piper, piper_plus
/// </summary>
public sealed partial class TtsEngineRouter : IDisposable, IAsyncDisposable
{
    private readonly IPrimaryTtsService _primaryService;
    private readonly TtsPlaybackService _playbackService;
    private readonly VoicevoxTtsService? _voicevoxService;
    private readonly PiperPlusTtsService? _piperService;
    private readonly KokoroTtsService? _kokoroService;
    private readonly MossTtsService? _mossService;
    private readonly TtsWavOutputStore? _wavOutputStore;
    private readonly object _lock = new();
    private static readonly System.Net.Http.HttpClient _checkClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private string _engine = "windows_sapi";
    private volatile bool _voicevoxAvailable;
    private volatile string? _voicevoxError;
    private bool _piperRuntimeAvailable;
    private bool _piperModelInstalled;
    private string? _piperError;
    private bool _kokoroModelInstalled;
    private string? _kokoroError;
    private bool _mossModelInstalled;
    private string? _mossError;
    private string? _lastExternalTtsText;
    private DateTime _lastExternalTtsStartedAt;

    public TtsEngineRouter(
        IPrimaryTtsService primaryService,
        TtsPlaybackService playbackService,
        VoicevoxTtsService? voicevoxService = null,
        PiperPlusTtsService? piperService = null,
        KokoroTtsService? kokoroService = null,
        MossTtsService? mossService = null,
        TtsWavOutputStore? wavOutputStore = null)
    {
        _primaryService = primaryService;
        _playbackService = playbackService;
        _voicevoxService = voicevoxService;
        _piperService = piperService;
        _kokoroService = kokoroService;
        _mossService = mossService;
        if (_mossService != null) _mossService.ReferenceChanging += OnsetMossReferenceChanging;
        _wavOutputStore = wavOutputStore;

        if (_voicevoxService != null)
            _voicevoxAvailable = true;

        if (_piperService != null)
        {
            _piperRuntimeAvailable = _piperService.IsRuntimeAvailable();
            _piperModelInstalled = _piperService.IsModelInstalled();
        }

        if (_kokoroService != null)
        {
            _kokoroModelInstalled = _kokoroService.IsModelInstalled;
            _kokoroError = _kokoroService.LastError;
        }
        if (_mossService != null)
            _mossModelInstalled = _mossService.IsModelInstalled;
    }

    public void SetConfig(TtsConfig config)
    {
        Interlocked.Increment(ref _onsetConfigurationChanging);
        try
        {
        if (!InvalidateOnsetConfiguration(OnsetOnlyMonitorChanged(config))) throw new InvalidOperationException("Filler cancellation is unconfirmed");
        string? voicevoxApiToCheck = null;
        lock (_lock)
        {
            _onsetConfigurationValid = false;
            _engine = config.Engine ?? "windows_sapi";
            CaptureOnsetConfiguration(config); // NullPrimary does not retain Linux config.
            _primaryService.SetConfig(config);

            if (_voicevoxService != null)
            {
                _voicevoxService.SetConfig(
                    config.VoicevoxApi,
                    int.TryParse(config.VoicevoxSpeakerId, out var sid) ? sid : (int?)null,
                    config.VoicevoxAutoEmotion,
                    config.TtsReflectEmotion,
                    config.TtsReflectContourStrength,
                    config.TtsReflectToneEmphasis);

                if (!string.IsNullOrWhiteSpace(config.VoicevoxApi))
                    voicevoxApiToCheck = config.VoicevoxApi;
            }

            if (_piperService != null)
            {
                _piperService.SetConfig(
                    config.PiperPlusExePath,
                    config.PiperPlusModelPath,
                    config.TtsReflectEmotion,
                    config.PiperPlusPersistent,
                    null);
                _piperRuntimeAvailable = _piperService.IsRuntimeAvailable();
                _piperModelInstalled = _piperService.IsModelInstalled();
            }

            if (_kokoroService != null)
            {
                _kokoroService.SetConfig(config.KokoroModelDir, config.KokoroSpeakerId, config.KokoroSpeed);
                _kokoroModelInstalled = _kokoroService.IsModelInstalled;
                _kokoroError = _kokoroService.LastError;
            }

            if (_mossService != null)
            {
                _mossService.SetConfig(config.MossModelDir, config.MossVoiceId, config.MossMaxNewFrames);
                _mossModelInstalled = _mossService.IsModelInstalled;
            }
            _onsetConfigurationValid = true;
        }

        if (!string.IsNullOrWhiteSpace(voicevoxApiToCheck))
            _ = Task.Run(() => CheckVoicevoxAvailability(voicevoxApiToCheck));
        }
        finally { Interlocked.Decrement(ref _onsetConfigurationChanging); }
        PrepareOnsetClips();
    }

    public TtsConfig GetConfig()
    {
        return _primaryService.GetConfig();
    }

    public Task<bool> SetMossReferencePcmAsync(byte[] pcm16LeMono, int sampleRate, CancellationToken ct = default)
    {
        if (_mossService == null || !_mossModelInstalled) return Task.FromResult(false);
        return _mossService.SetReferencePcmAsync(pcm16LeMono, sampleRate, ct);
    }

    public TtsStatus GetStatus()
    {
        lock (_lock)
        {
            var status = _primaryService.GetStatus();
            var externalPlaying = _playbackService.IsPlaying;
            // Update engine status
            return new TtsStatus
            {
                Available = status.Available,
                IsSpeaking = status.IsSpeaking || externalPlaying,
                Error = status.Error,
                OutputDeviceId = status.OutputDeviceId,
                OutputDeviceName = status.OutputDeviceName,
                OutputDeviceMode = status.OutputDeviceMode,
                WirelessLikely = status.WirelessLikely,
                PreSilenceMs = status.PreSilenceMs,
                FallbackReason = status.FallbackReason,
                PendingCount = status.PendingCount,
                DroppedCount = status.DroppedCount,
                LastTtsText = _lastExternalTtsText ?? status.LastTtsText,
                LastTtsStartedAt = _lastExternalTtsStartedAt != default ? _lastExternalTtsStartedAt.ToString("o") : status.LastTtsStartedAt,
                MonitorEnabled = status.MonitorEnabled,
                MonitorVolumePercent = status.MonitorVolumePercent,
                MonitorFallbackReason = status.MonitorFallbackReason,
                Engine = _engine,
                EffectiveEngine = ResolveEffectiveEngine(),
                EngineAvailable = status.EngineAvailable,
                EngineError = status.EngineError,
                VoicevoxApi = status.VoicevoxApi,
                VoicevoxSpeakerId = status.VoicevoxSpeakerId,
                VoicevoxAutoEmotion = status.VoicevoxAutoEmotion,
                TtsReflectEmotion = status.TtsReflectEmotion,
                TtsReflectContourStrength = status.TtsReflectContourStrength,
                TtsReflectToneEmphasis = status.TtsReflectToneEmphasis,
                PiperPlusModelId = status.PiperPlusModelId,
                PiperPlusModelPath = _piperService?.ModelPath,
                PiperPlusModelInstalled = _piperModelInstalled,
                PiperPlusRuntimeAvailable = _piperRuntimeAvailable,
                VoicevoxAvailable = _voicevoxAvailable,
                VoicevoxError = _voicevoxError,
                PiperPlusError = _piperError,
                KokoroModelId = _kokoroService?.ModelDir is string md ? System.IO.Path.GetFileName(md) : string.Empty,
                KokoroModelInstalled = _kokoroModelInstalled,
                KokoroError = _kokoroError,
                MossModelId = _mossService?.ModelDir is string mmd ? System.IO.Path.GetFileName(mmd) : string.Empty,
                MossVoiceId = status.MossVoiceId,
                MossModelInstalled = _mossModelInstalled,
                MossError = _mossError
            };
        }
    }

    private void MarkExternalTtsStarted(string text)
    {
        _lastExternalTtsText = text;
        _lastExternalTtsStartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Speaks text using the configured engine.
    /// Falls back to SAPI if external engine fails.
    /// </summary>
    public async Task<TtsResult> SpeakAsync(string text, bool interrupt = true, CancellationToken ct = default)
    {
        return await SpeakAsync(text, null, null, interrupt, ct);
    }

    /// <summary>
    /// Speaks text using the specified engine (overrides config).
    /// </summary>
    public async Task<TtsResult> SpeakAsync(string text, string? overrideEngine, int? voicevoxSpeakerId, bool interrupt = true, CancellationToken ct = default)
    {
        return await SpeakAsync(text, overrideEngine, voicevoxSpeakerId, interrupt, ct, null);
    }

    public async Task<TtsResult> SpeakAsync(string text, string? overrideEngine, int? voicevoxSpeakerId, bool interrupt, CancellationToken ct, TtsProsodyProfile? prosodyProfile, long finalDetectedAtMs = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TtsResult.Fail("Empty text");
        using var body = await EnterOnsetBodyAsync().ConfigureAwait(false);

        // Determine effective engine
        string engine;
        if (!string.IsNullOrWhiteSpace(overrideEngine) && overrideEngine != "auto")
        {
            engine = overrideEngine;
        }
        else
        {
            lock (_lock)
            {
                engine = ResolveEffectiveEngine();
            }
        }

        if (engine == "voicevox" && _voicevoxService != null)
        {
            return await TryVoicevoxAsync(text, interrupt, ct, voicevoxSpeakerId, prosodyProfile, finalDetectedAtMs);
        }
        else if ((engine == "piper" || engine == "piper_plus") && _piperService != null)
        {
            return await TryPiperAsync(text, interrupt, ct, prosodyProfile, finalDetectedAtMs);
        }
        else if (engine == "kokoro" && _kokoroService != null)
        {
            // Kokoro intonation reproduction is still unstable; keep it disabled for v2.1.0.
            return await TryKokoroAsync(text, interrupt, ct, null, finalDetectedAtMs);
        }
        else if (engine == "moss" && _mossService != null)
        {
            return await TryMossAsync(text, interrupt, ct, prosodyProfile, finalDetectedAtMs);
        }
        else if (engine == "windows_sapi" || engine == "auto")
        {
            // For auto: prefer external engine if available, else SAPI
            if (engine == "auto")
            {
                // Try external engines first
                if (_voicevoxService != null && _voicevoxAvailable)
                {
                    return await TryVoicevoxAsync(text, interrupt, ct, voicevoxSpeakerId, prosodyProfile, finalDetectedAtMs);
                }
                if (_kokoroService != null && _kokoroModelInstalled)
                {
                    // Kokoro intonation reproduction is still unstable; keep it disabled for v2.1.0.
                    return await TryKokoroAsync(text, interrupt, ct, null, finalDetectedAtMs);
                }
                if (_mossService != null && _mossModelInstalled)
                {
                    return await TryMossAsync(text, interrupt, ct, prosodyProfile, finalDetectedAtMs);
                }
                if (_piperService != null && _piperRuntimeAvailable && _piperModelInstalled)
                {
                    return await TryPiperAsync(text, interrupt, ct, prosodyProfile, finalDetectedAtMs);
                }
            }
            // Fall through to SAPI
            return TrySapiAsync(text, interrupt);
        }
        else
        {
            // Unknown engine, fall back to SAPI
            return TrySapiAsync(text, interrupt);
        }
    }

    /// <summary>
    /// Stops ongoing playback.
    /// </summary>
    public TtsResult StopAsync()
    {
        StopOnset();
        // Stop both SAPI and playback
        var sapiResult = _primaryService.StopAsync();
        var playbackResult = _playbackService.Stop();

        return new TtsResult
        {
            Success = sapiResult.Success || playbackResult.Success,
            Error = sapiResult.Error ?? (playbackResult.Success ? null : "Stop failed")
        };
    }

    private async Task<TtsResult> TryVoicevoxAsync(string text, bool interrupt, CancellationToken ct, int? speakerId = null, TtsProsodyProfile? prosodyProfile = null, long finalDetectedAtMs = 0)
    {
        if (_voicevoxService == null)
            return TtsResult.Fail("VOICEVOX not configured");

        try
        {
            if (interrupt && _playbackService.IsPlaying)
            {
                _playbackService.Stop();
            }

            var result = await _voicevoxService.SynthesizeAsync(text, speakerId, ct, prosodyProfile);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                string err = result.Error;
                lock (_lock)
                {
                    ct.ThrowIfCancellationRequested();
                    _voicevoxError = err;
                }
                // Fall back to SAPI
                ct.ThrowIfCancellationRequested();
                return TrySapiAsync(text, interrupt);
            }

            SaveExternalWav("voicevox", result.WavBytes!);
            if (!HandoffOnsetToBody()) return TtsResult.Fail("Filler output has not drained");
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                MarkExternalTtsStarted(text);
            }
            var monitorVolumePercent = Math.Clamp(_primaryService.GetConfig().MonitorVolumePercent, 0, 100);
            ct.ThrowIfCancellationRequested();
            // Play WAV via native bridge. On Windows this also mirrors to default output for monitor playback.
            var playbackResult = _playbackService.PlayWav(result.WavBytes!, monitorVolumePercent, finalDetectedAtMs);
            if (!playbackResult.Success)
            {
                return TtsResult.Fail($"Playback failed: {playbackResult.Message}");
            }

            return TtsResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string err = ex.Message;
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                _voicevoxError = err;
            }
            ct.ThrowIfCancellationRequested();
            return TrySapiAsync(text, interrupt);
        }
    }

    private async Task<TtsResult> TryKokoroAsync(string text, bool interrupt, CancellationToken ct, TtsProsodyProfile? prosodyProfile = null, long finalDetectedAtMs = 0)
    {
        if (_kokoroService == null)
            return TtsResult.Fail("Kokoro not configured");
        // A rejected initialization is not permission to speak with another engine.
        // Keep the existing synthesis/language fallback for an initialized model below.
        if (!_kokoroService.IsModelInstalled)
            return TtsResult.Fail(_kokoroService.LastError ?? "Kokoro not initialized or model not installed");
        try
        {
            if (interrupt && _playbackService.IsPlaying)
                _playbackService.Stop();

            var result = await _kokoroService.SynthesizeAsync(text, ct, prosodyProfile);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                lock (_lock)
                {
                    ct.ThrowIfCancellationRequested();
                    _kokoroError = result.Error;
                }
                ct.ThrowIfCancellationRequested();
                return TrySapiAsync(text, interrupt, result.Error);
            }

            SaveExternalWav("kokoro", result.WavBytes!);
            if (!HandoffOnsetToBody()) return TtsResult.Fail("Filler output has not drained");
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                MarkExternalTtsStarted(text);
            }
            var monitorVolumePercent = Math.Clamp(_primaryService.GetConfig().MonitorVolumePercent, 0, 100);
            ct.ThrowIfCancellationRequested();
            var playbackResult = _playbackService.PlayWav(result.WavBytes!, monitorVolumePercent, finalDetectedAtMs);
            if (!playbackResult.Success)
                return TtsResult.Fail($"Playback failed: {playbackResult.Message}");
            return TtsResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                _kokoroError = ex.Message;
            }
            ct.ThrowIfCancellationRequested();
            return TrySapiAsync(text, interrupt, ex.Message);
        }
    }

    private async Task<TtsResult> TryPiperAsync(string text, bool interrupt, CancellationToken ct, TtsProsodyProfile? prosodyProfile = null, long finalDetectedAtMs = 0)
    {
        if (_piperService == null)
            return TtsResult.Fail("Piper-Plus not configured");

        using var resultCancellation = _piperService.CreateConfigurationCancellation(ct);
        ct = resultCancellation.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (interrupt && _playbackService.IsPlaying)
            {
                _playbackService.Stop();
            }

            var result = await _piperService.SynthesizeAsync(text, ct, prosodyProfile);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                lock (_lock)
                {
                    ct.ThrowIfCancellationRequested();
                    _piperError = result.Error;
                }
                ct.ThrowIfCancellationRequested();
                return TrySapiAsync(text, interrupt);
            }

            SaveExternalWav("piper", result.WavBytes!);
            if (!HandoffOnsetToBody()) return TtsResult.Fail("Filler output has not drained");
            var monitorVolumePercent = Math.Clamp(_primaryService.GetConfig().MonitorVolumePercent, 0, 100);
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                MarkExternalTtsStarted(text);
            }
            ct.ThrowIfCancellationRequested();
            // Play WAV via native bridge. On Windows this also mirrors to default output for monitor playback.
            var playbackResult = _playbackService.PlayWav(result.WavBytes!, monitorVolumePercent, finalDetectedAtMs);
            if (!playbackResult.Success)
            {
                return TtsResult.Fail($"Playback failed: {playbackResult.Message}");
            }

            return TtsResult.Ok();
        }
        catch (OperationCanceledException)
        {
            // Cancellation (including a Piper configuration generation change)
            // is not an engine failure and must not speak via SAPI.
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                _piperError = ex.Message;
            }
            ct.ThrowIfCancellationRequested();
            return TrySapiAsync(text, interrupt);
        }
    }

    private async Task<TtsResult> TryMossAsync(string text, bool interrupt, CancellationToken ct, TtsProsodyProfile? prosodyProfile = null, long finalDetectedAtMs = 0)
    {
        if (_mossService == null)
            return TtsResult.Fail("MOSS not configured");
        try
        {
            if (interrupt && _playbackService.IsPlaying)
                _playbackService.Stop();

            var result = await _mossService.SynthesizeAsync(text, ct, prosodyProfile);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                lock (_lock) _mossError = result.Error;
                ct.ThrowIfCancellationRequested();
                return TrySapiAsync(text, interrupt, result.Error);
            }

            SaveExternalWav("moss", result.WavBytes!);
            if (!HandoffOnsetToBody()) return TtsResult.Fail("Filler output has not drained");
            ct.ThrowIfCancellationRequested();
            lock (_lock) MarkExternalTtsStarted(text);
            var monitorVolumePercent = Math.Clamp(_primaryService.GetConfig().MonitorVolumePercent, 0, 100);
            var playbackResult = _playbackService.PlayWav(result.WavBytes!, monitorVolumePercent, finalDetectedAtMs);
            if (!playbackResult.Success)
                return TtsResult.Fail($"Playback failed: {playbackResult.Message}");
            return TtsResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) _mossError = ex.Message;
            ct.ThrowIfCancellationRequested();
            return TrySapiAsync(text, interrupt, ex.Message);
        }
    }

    private TtsResult TrySapiAsync(string text, bool interrupt, string? fallbackError = null)
    {
        if (!HandoffOnsetToBody()) return TtsResult.Fail("Filler output has not drained");
        // On Linux, NullPrimaryTtsService.IsAvailable == false.
        // Don't silently swallow the original error; return it directly.
        if (!_primaryService.IsAvailable)
            return fallbackError != null ? TtsResult.Fail(fallbackError) : TtsResult.Fail("TTS unavailable");
        return _primaryService.SpeakAsync(text, interrupt);
    }

    private void SaveExternalWav(string engine, byte[] wavBytes)
    {
        if (_wavOutputStore == null || !_primaryService.GetConfig().WavOutputEnabled) return;
        _ = _wavOutputStore.SaveWavAsync(engine, wavBytes);
    }

    /// <summary>
    /// Checks VOICEVOX availability via HTTP request.
    /// Retries up to 3 times with 5000ms delay to handle VoiceVox startup delay.
    /// </summary>
    private void CheckVoicevoxAvailability(string? apiUrl)
    {
        if (string.IsNullOrEmpty(apiUrl))
        {
            _voicevoxAvailable = false;
            _voicevoxError = "VOICEVOX API URL not configured";
            return;
        }

        const int maxRetries = 3;
        const int retryDelayMs = 5000;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (attempt > 0)
                System.Threading.Thread.Sleep(retryDelayMs);

            try
            {
                var resp = _checkClient.GetAsync($"{apiUrl}/speakers").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                {
                    _voicevoxAvailable = true;
                    _voicevoxError = null;
                    return;
                }
                else
                {
                    _voicevoxAvailable = false;
                    _voicevoxError = $"HTTP {(int)resp.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                _voicevoxAvailable = false;
                _voicevoxError = ex.InnerException?.Message ?? ex.GetType().Name;
            }
        }
    }

    private string ResolveEffectiveEngine()
    {
        if (_engine == "voicevox" && _voicevoxService != null && _voicevoxAvailable)
            return "voicevox";
        if ((_engine == "piper" || _engine == "piper_plus") && _piperService != null && _piperRuntimeAvailable && _piperModelInstalled)
            return _engine;
        if (_engine == "kokoro" && _kokoroService != null)
            return "kokoro";
        if (_engine == "moss" && _mossService != null && _mossModelInstalled)
            return "moss";
        // Linux: primary service is NullPrimaryTtsService → fall through to piper_plus
        if (_primaryService.IsAvailable)
            return _primaryService.EngineName;
        if (_piperService != null && _piperRuntimeAvailable && _piperModelInstalled)
            return "piper_plus";
        return _primaryService.EngineName; // "none" on Linux without Piper
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        StopOnset();
        _playbackService.Dispose();
        if (_piperService != null)
            await _piperService.DisposeAsync();
        _kokoroService?.Dispose();
        if (_mossService != null) _mossService.ReferenceChanging -= OnsetMossReferenceChanging;
        _mossService?.Dispose();
    }
}
