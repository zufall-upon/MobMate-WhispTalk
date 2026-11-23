using MobMate.Core.Realtime;
using System.Text.Json;

namespace MobMate.Core.Tts;

public sealed partial class TtsEngineRouter
{
    private readonly OnsetFillerState _onset = new();
    private readonly object _onsetPreparationGate = new();
    // Local output handoff only, not a general job scheduler. Never held while
    // playing audio, synthesizing, or inside the native onset callback.
    private readonly SemaphoreSlim _onsetHandoff = new(1, 1);
    // Only preparation owns this gate. Body synthesis never waits for it.
    private readonly SemaphoreSlim _onsetSynthesis = new(1, 1);
    private CancellationTokenSource? _onsetPreparation;
    private Task _onsetPreparationTask = Task.CompletedTask;
    private long _onsetSession;
    private long _onsetDispatch;
    private long _onsetSettingsRevision;
    private long _onsetPreparationRevision;
    private int _onsetConfigurationChanging;
    private volatile bool _onsetConfigurationValid;
    private Func<bool> _onsetPermissionCheck = () => false;
    private int _onsetBodyCount;
    private int _onsetPrepared;
    private volatile bool _onsetEnabled;
    private volatile bool _onsetPermitted;
    private volatile bool _onsetIdle = true;
    private volatile bool _onsetPreparationRequested;
    private string _onsetLanguage = "";
    private string _onsetKey = "";
    private readonly List<FillerClip> _onsetClips = new(5);
    private string _onsetReason = "off";
    private volatile string _onsetPlaybackState = "off";
    private volatile string? _onsetGateReason;
    private TtsConfig _onsetConfiguration = new();
    private volatile string _onsetSourceVoiceKey = "";
    private string _onsetSourcePoolKey = "";
    private long _onsetSourceVoiceGeneration;
    private long _onsetMossReferenceRevision;
    // Source changes only. ASR Stop/OFF and output invalidation must not retire a
    // Mobfy bank made from the same selected voice. Never raised under the
    // preparation gate; subscribers must not call back into SetConfig.
    internal event Action? OnsetSourceChanged;
    private void CaptureOnsetConfiguration(TtsConfig config)
    {
        _onsetConfiguration = JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(config))!;
        var key = config.Engine switch
        {
            "voicevox" => JsonSerializer.Serialize(new { config.Engine, config.VoicevoxApi, config.VoicevoxSpeakerId,
                config.VoicevoxAutoEmotion, config.TtsReflectEmotion, config.TtsReflectContourStrength, config.TtsReflectToneEmphasis }),
            "kokoro" => JsonSerializer.Serialize(new { config.Engine, config.KokoroModelId, config.KokoroModelDir,
                config.KokoroSpeakerId, config.KokoroSpeed }),
            "moss" => JsonSerializer.Serialize(new { config.Engine, config.MossModelId, config.MossModelDir,
                config.MossVoiceId, config.MossMaxNewFrames }),
            _ => ""
        };
        if (_onsetSourceVoiceKey == key) return;
        _onsetSourceVoiceKey = key;
        Interlocked.Increment(ref _onsetSourceVoiceGeneration);
        OnsetSourceChanged?.Invoke();
    }

    internal string? GetOnsetSourceKey(string language)
    {
        var normalized = NormalizeOnsetLanguage(language);
        var voice = _onsetSourceVoiceKey;
        if (voice.Length == 0 || _onsetLanguage != normalized) return null;
        return normalized + "/dynamic-v1/" + voice + "/" + Volatile.Read(ref _onsetSourceVoiceGeneration) +
            "/" + Volatile.Read(ref _onsetMossReferenceRevision);
    }

    internal sealed record OnsetSourceSnapshot(string Key, long Generation, byte[][] Wavs);

    // Copies the raw, same-voice pool, including prewarm while playback is OFF.
    // The independent source key remains available after ASR Stop clears PCM.
    internal bool TryGetOnsetSourceSnapshot(string language, out OnsetSourceSnapshot? snapshot)
    {
        lock (_onsetPreparationGate)
        {
            snapshot = null;
            var key = GetOnsetSourceKey(language);
            if (!_onsetConfigurationValid || OnsetRouteChanging || key == null || key != _onsetSourcePoolKey || _onsetPrepared != 5 ||
                _onsetClips.Count != 5 || _onsetClips.Any(c => c.Wav.IsEmpty) ||
                _onsetClips.Select(c => c.Id).Distinct().Count() != 5) return false;
            snapshot = new(key, Volatile.Read(ref _onsetPreparationRevision), _onsetClips.Select(c => c.Wav.ToArray()).ToArray());
            return true;
        }
    }
    private static string OnsetConfigurationWithoutMonitor(TtsConfig config)
    {
        var copy = JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(config))!;
        copy.MonitorVolumePercent = 0;
        return JsonSerializer.Serialize(copy);
    }
    private bool OnsetOnlyMonitorChanged(TtsConfig config)
        => _onsetConfigurationValid && OnsetConfigurationWithoutMonitor(_onsetConfiguration) == OnsetConfigurationWithoutMonitor(config);
    internal Task OnsetPreparationCompletion { get { lock (_onsetPreparationGate) return _onsetPreparationTask; } }

    // The existing friend smoke exercises configuration and ownership without
    // opening devices, loading a model, or sending requests to a live engine.
    internal Func<TtsConfig, string, CancellationToken, Task<byte[]?>>? OnsetSynthesizeForTest { get; set; }

    internal static bool OnsetMatchesVoicevoxMetadata(string speakersJson, int speakerId)
    {
        try
        {
            using var speakers = JsonDocument.Parse(speakersJson);
            var matches = new List<bool>();
            foreach (var speaker in speakers.RootElement.EnumerateArray())
            {
                foreach (var style in speaker.GetProperty("styles").EnumerateArray())
                {
                    if (style.GetProperty("id").GetInt32() != speakerId) continue;
                    matches.Add(style.TryGetProperty("type", out var type) && type.GetString() == "talk");
                }
            }
            return matches.Count == 1 && matches[0];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return false; }
    }

    internal static bool OnsetKokoroSupportsLanguage(TtsConfig config, string language)
    {
        try
        {
            // The installed metadata is written from RichVoiceCatalog, the same
            // registry used by the normal voice selector. Never substitute a SID.
            var properties = File.ReadLines(Path.Combine(config.KokoroModelDir, "mobmate-model.properties"))
                .Where(line => !line.StartsWith('#') && line.Contains('='))
                .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
            if (!properties.TryGetValue("id", out var id) || id != config.KokoroModelId ||
                !properties.TryGetValue("speakers", out var speakers)) return false;
            var selected = speakers.Split(',').Where(s => s.StartsWith(config.KokoroSpeakerId + ":", StringComparison.Ordinal)).ToArray();
            if (selected.Length != 1) return false;
            var name = selected[0].Split(':', 2)[1];
            // Catalog names use a/b for English, z for Chinese. Current installed
            // models do not provide a supported Japanese phonemizer.
            // The English v0.19 catalog also includes the short blend name "af".
            return (name.Length > 2 || name == "af") && (language == "en" && name[0] is 'a' or 'b' || language == "zh" && name[0] == 'z');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    internal static bool OnsetMatchesMossVoice(TtsConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.MossVoiceId) ||
            config.MossModelId is not ("moss-tts-nano-100m-onnx" or "moss-tts-nano-100m-onnx-int8")) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.MossModelDir, "browser_poc_manifest.json")));
            var matches = manifest.RootElement.GetProperty("builtin_voices").EnumerateArray()
                .Where(voice => voice.GetProperty("voice").GetString() == config.MossVoiceId.Trim()).ToArray();
            return matches.Length == 1 && matches[0].GetProperty("prompt_audio_codes").GetArrayLength() > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    private void OnsetMossReferenceChanging()
    {
        if (_onsetConfiguration.Engine == "moss")
        {
            Interlocked.Increment(ref _onsetMossReferenceRevision);
            OnsetSourceChanged?.Invoke();
            InvalidateOnsetConfiguration();
        }
    }

    // Physical status stays truthful; only realtime BODY guards exclude filler.
    public bool IsSpeakingForRealtimeBody => _primaryService.GetStatus().IsSpeaking || _playbackService.IsBodyPlaying;

    public bool OnsetRouteChanging => Volatile.Read(ref _onsetConfigurationChanging) != 0;

    // Host output changes include native/Pulse routing after SetConfig returns.
    // Keep the gate closed across that whole operation, not just the config copy.
    public IDisposable BeginOnsetRouteChange() => BeginOnsetRouteChangeCore(false);

    // A monitor-only update still invalidates the output generation, but the
    // selected voice and its prepared PCM do not change.
    internal IDisposable BeginOnsetRouteChange(TtsConfig config) => BeginOnsetRouteChangeCore(OnsetOnlyMonitorChanged(config));

    private IDisposable BeginOnsetRouteChangeCore(bool preservePreparation)
    {
        Interlocked.Increment(ref _onsetConfigurationChanging);
        try
        {
            if (!InvalidateOnsetConfiguration(preservePreparation)) throw new InvalidOperationException("Filler cancellation is unconfirmed");
            return new OnsetRouteChangeLease(this);
        }
        catch { Interlocked.Decrement(ref _onsetConfigurationChanging); throw; }
    }

    private sealed class OnsetRouteChangeLease(TtsEngineRouter owner) : IDisposable
    {
        private TtsEngineRouter? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current == null) return;
            Interlocked.Decrement(ref current._onsetConfigurationChanging);
        }
    }
    public object GetOnsetStatus() => GetOnsetStatus(null);
    public object GetOnsetStatus(string? blockReason) => new
    {
        enabled = _onsetEnabled, prepared = Volatile.Read(ref _onsetPrepared), reason = _onsetReason,
        playbackReason = !_onsetEnabled ? "off" : blockReason ?? _onsetGateReason ?? _onsetPlaybackState
    };

    // Prewarm is not permission to play. UI startup/selection supplies its already
    // selected language; OFF stays OFF and no microphone or output is opened.
    public void PrepareOnset(string language, bool idle = true)
    {
        var normalized = NormalizeOnsetLanguage(language);
        if (_onsetLanguage != normalized)
        {
            if (!InvalidateOnsetConfiguration()) return;
            _onsetLanguage = normalized;
            OnsetSourceChanged?.Invoke();
        }
        _onsetIdle = idle;
        _onsetPreparationRequested = true;
        if (_onsetEnabled) RefreshOnset(_onsetPermissionCheck, idle);
        else PrepareOnsetClips();
    }

    public Action<ulong>? ConfigureOnset(bool enabled, string language, Func<bool> permitted, bool newSession = false, bool idle = true)
    {
        Interlocked.Increment(ref _onsetConfigurationChanging);
        try
        {
            if (!enabled)
            {
                // Starting with playback OFF must not discard a valid prewarm.
                if (!newSession || _onsetEnabled) StopOnset();
                return null;
            }
            if (_onsetLanguage != NormalizeOnsetLanguage(language) && !InvalidateOnsetConfiguration())
            { _onsetEnabled = false; return null; }
            if (newSession)
            {
                Interlocked.Increment(ref _onsetSession);
                Interlocked.Increment(ref _onsetDispatch);
                _onset.BeginSession();
            }
            _onsetEnabled = enabled;
            _onsetPlaybackState = "waiting-onset";
            var normalized = NormalizeOnsetLanguage(language);
            var languageChanged = _onsetLanguage != normalized;
            _onsetLanguage = normalized;
            if (languageChanged) OnsetSourceChanged?.Invoke();
            _onsetPermissionCheck = permitted;
            _onsetPreparationRequested = true;
        }
        finally { Interlocked.Decrement(ref _onsetConfigurationChanging); }
        RefreshOnset(permitted, idle);
        var session = Volatile.Read(ref _onsetSession);
        return !enabled ? null : sequence => NotifyOnset(session, sequence);
    }

    public void DisableOnset(string reason)
    {
        StopOnset();
        _onsetReason = reason;
        _onsetPlaybackState = reason;
    }

    public void StopOnset()
    {
        _onsetEnabled = false;
        _onsetPlaybackState = "off";
        _onsetPreparationRequested = false;
        Interlocked.Increment(ref _onsetSession);
        if (InvalidateOnsetConfiguration()) _onsetReason = "off";
    }

    public bool InvalidateOnsetConfiguration() => InvalidateOnsetConfiguration(false);

    private bool InvalidateOnsetConfiguration(bool preservePreparation)
    {
        Interlocked.Increment(ref _onsetDispatch);
        Interlocked.Increment(ref _onsetSettingsRevision);
        if (!preservePreparation)
        {
            Interlocked.Increment(ref _onsetPreparationRevision);
            StopOnsetPreparation();
        }
        lock (_onsetPreparationGate)
        {
            _onsetPermitted = false;
            _onset.Invalidate();
            if (!preservePreparation)
            {
                Volatile.Write(ref _onsetPrepared, 0);
                _onsetKey = "";
                _onsetSourcePoolKey = "";
                _onsetClips.Clear();
            }
        }
        _onsetHandoff.Wait();
        try
        {
            var stopped = _playbackService.CancelFillerAndWait();
            if (!stopped.Success) _onsetReason = "cancel-unconfirmed";
            return stopped.Success;
        }
        finally { _onsetHandoff.Release(); }
    }

    public void RefreshOnset(Func<bool> permissionCheck, bool idle)
    {
        if (!_onsetConfigurationValid) { _onsetReason = "unsupported-configuration"; return; }
        var revision = Volatile.Read(ref _onsetSettingsRevision);
        var config = _onsetConfiguration;
        var hasOutput = !string.IsNullOrWhiteSpace(config.OutputDeviceId) || !string.IsNullOrWhiteSpace(config.OutputDeviceName);
        var supported = _playbackService.SupportsFiller;
        var permitted = permissionCheck() && hasOutput && supported;
        lock (_onsetPreparationGate)
        {
            if (revision != Volatile.Read(ref _onsetSettingsRevision) || Volatile.Read(ref _onsetConfigurationChanging) != 0) return;
            _onsetPermitted = permitted;
            _onsetGateReason = !hasOutput ? "output-required" :
                !supported ? "native-unavailable" : !permitted ? "permission-blocked" : null;
        }
        _onsetIdle = idle;
        lock (_onsetPreparationGate)
        {
            if (revision != Volatile.Read(ref _onsetSettingsRevision)) return;
            var preparation = _onset.Configure(_onsetEnabled && permitted, _onsetKey);
            if (_onsetClips.Count == 5) _onset.Publish(preparation, _onsetClips);
        }
        PrepareOnsetClips();
    }

    private void PrepareOnsetClips()
    {
        if (!_onsetPreparationRequested || !_onsetConfigurationValid || Volatile.Read(ref _onsetConfigurationChanging) != 0) return;
        var revision = Volatile.Read(ref _onsetSettingsRevision);
        var preparationRevision = Volatile.Read(ref _onsetPreparationRevision);
        var idle = _onsetIdle;
        if (!idle || Volatile.Read(ref _onsetBodyCount) != 0) return;
        lock (_onsetPreparationGate)
            if (_onsetKey.Length > 0) return;
        TtsConfig config;
        lock (_lock) config = _onsetConfiguration;
        var language = _onsetLanguage;
        var texts = language switch
        {
            "ja" => new[] { "えっと、", "あの、", "ああ", "うーん、", "ええと、" },
            "en" => new[] { "Um,", "Well,", "Hmm,", "Let me see,", "Uh," },
            "zh" => new[] { "嗯，", "那个，", "我想想，", "怎么说呢，", "这个，" },
            "es" => new[] { "Eh,", "Mmm,", "A ver,", "Bueno,", "Este," },
            "fr" => new[] { "Euh,", "Hmm,", "Alors,", "Voyons,", "Eh bien," },
            "de" => new[] { "Ähm,", "Hm,", "Also,", "Mal sehen,", "Nun," },
            "it" => new[] { "Ehm,", "Allora,", "Dunque,", "Vediamo,", "Be'," },
            "hu" => new[] { "Ööö,", "Hát,", "Nos,", "Lássuk,", "Hm," },
            "ko" => new[] { "음,", "어,", "그,", "저기,", "글쎄요," },
            "ru" => new[] { "Эм,", "Ну,", "Так,", "Хм,", "Дайте подумать," },
            "fa" => new[] { "اِم،", "خب،", "هوم،", "ببینم،", "اِه،" },
            "ar" => new[] { "أمم،", "حسنًا،", "همم،", "دعني أفكر،", "آه،" },
            "pl" => new[] { "Yyy,", "No,", "Hmm,", "Zaraz,", "Zobaczmy," },
            "pt" => new[] { "Hum,", "Bem,", "Hmm,", "Deixa eu ver,", "Então," },
            "cs" => new[] { "Ehm,", "No,", "Hmm,", "Tak,", "Uvidíme," },
            "da" => new[] { "Øh,", "Tja,", "Hmm,", "Altså,", "Lad mig se," },
            "sv" => new[] { "Eh,", "Tja,", "Hmm,", "Alltså,", "Få se," },
            "el" => new[] { "Εεε,", "Λοιπόν,", "Χμ,", "Για να δω,", "Ας δω," },
            "tr" => new[] { "Şey,", "Yani,", "Hmm,", "Bir bakayım,", "Peki," },
            _ => Array.Empty<string>()
        };
        var voicevoxApi = config.VoicevoxApi?.TrimEnd('/') ?? "";
        var voicevox = config.Engine == "voicevox" && language == "ja" && int.TryParse(config.VoicevoxSpeakerId, out var speakerId) && speakerId >= 0 &&
            _voicevoxService != null && Uri.TryCreate(voicevoxApi, UriKind.Absolute, out var voicevoxUri) &&
            voicevoxUri.Scheme == Uri.UriSchemeHttp && voicevoxUri.Port > 0 &&
            (voicevoxUri.Host == "127.0.0.1" || voicevoxUri.Host == "localhost") &&
            voicevoxUri.UserInfo.Length == 0 && voicevoxUri.AbsolutePath == "/" &&
            voicevoxUri.Query.Length == 0 && voicevoxUri.Fragment.Length == 0;
        // A separate preparation instance is not evidence that the body can use
        // Kokoro. If the selected body service is absent or failed to initialize,
        // preparing its voice could precede a different engine (or no body).
        var kokoro = config.Engine == "kokoro" && _kokoroService != null &&
            (_kokoroService.IsModelInstalled || OnsetSynthesizeForTest != null) && OnsetKokoroSupportsLanguage(config, language);
        var moss = config.Engine == "moss" && _mossService != null && _mossModelInstalled &&
            _mossService.HasOnlyBuiltInReference && OnsetMatchesMossVoice(config);
        if ((!voicevox && !kokoro && !moss) || texts.Length != 5)
        {
            _onsetReason = "unsupported-voice-language-or-route";
            return;
        }
        var key = language + "/dynamic-v1/" + JsonSerializer.Serialize(config);
        var sourceKey = GetOnsetSourceKey(language);
        lock (_onsetPreparationGate)
        {
            if (revision != Volatile.Read(ref _onsetSettingsRevision) || Volatile.Read(ref _onsetConfigurationChanging) != 0) return;
            if (_onsetKey == key) return;
            _onsetClips.Clear();
            _onsetPreparation?.Dispose();
            var cancellation = _onsetPreparation = new();
            var token = cancellation.Token;
            _onsetKey = key;
            _onsetReason = "preparing";
            _onsetPreparationTask = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await _onsetSynthesis.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    token.ThrowIfCancellationRequested();
                    VoicevoxTtsService? voice = null;
                    using var kokoroVoice = kokoro && OnsetSynthesizeForTest == null ? new KokoroTtsService() : null;
                    using var mossVoice = moss && OnsetSynthesizeForTest == null ? new MossTtsService() : null;
                    if (OnsetSynthesizeForTest == null)
                    {
                        if (voicevox)
                        {
                            var speakers = await _checkClient.GetStringAsync(voicevoxApi + "/speakers", token).ConfigureAwait(false);
                            if (!OnsetMatchesVoicevoxMetadata(speakers, int.Parse(config.VoicevoxSpeakerId)))
                                throw new InvalidOperationException("Selected VOICEVOX talk style is unavailable");
                            voice = new VoicevoxTtsService(voicevoxApi, int.Parse(config.VoicevoxSpeakerId), config.VoicevoxAutoEmotion);
                            voice.SetConfig(reflectEmotion: config.TtsReflectEmotion,
                                contourStrength: config.TtsReflectContourStrength, toneEmphasis: config.TtsReflectToneEmphasis);
                        }
                        else if (kokoro)
                        {
                            // A bounded production preparation instance uses the
                            // already installed model. Its native lock/task is
                            // never shared with the body or awaited by the body.
                            kokoroVoice!.SetConfig(config.KokoroModelDir, config.KokoroSpeakerId, config.KokoroSpeed);
                        }
                        else mossVoice!.SetConfig(config.MossModelDir, config.MossVoiceId, config.MossMaxNewFrames);
                    }
                    var clips = new List<FillerClip>(5);
                    foreach (var text in texts)
                    {
                        token.ThrowIfCancellationRequested();
                        var wav = OnsetSynthesizeForTest != null
                            ? await OnsetSynthesizeForTest(config, text, token).ConfigureAwait(false)
                            : voice != null
                                ? (await voice.SynthesizeAsync(text, ct: token).ConfigureAwait(false)).WavBytes
                                : kokoroVoice != null
                                    ? (await kokoroVoice.SynthesizeAsync(text, token).ConfigureAwait(false)).WavBytes
                                    : (await mossVoice!.SynthesizeAsync(text, token).ConfigureAwait(false)).WavBytes;
                        token.ThrowIfCancellationRequested();
                        if (wav == null || !TtsPlaybackService.CanPrepareFillerWav(wav)) break;
                        clips.Add(new FillerClip(wav));
                    }
                    token.ThrowIfCancellationRequested();
                    lock (_onsetPreparationGate)
                    {
                        if (preparationRevision != Volatile.Read(ref _onsetPreparationRevision)) return;
                        if (token.IsCancellationRequested || !_onsetIdle || Volatile.Read(ref _onsetBodyCount) != 0 ||
                            (moss && !_mossService!.HasOnlyBuiltInReference))
                        { _onsetKey = ""; return; }
                        if (clips.Count == 5 && clips.Select(c => c.Id).Distinct().Count() == 5)
                        {
                            _onsetClips.AddRange(clips);
                            _onsetSourcePoolKey = sourceKey ?? "";
                            var preparation = _onset.Configure(_onsetEnabled && _onsetPermitted, key);
                            _onset.Publish(preparation, clips);
                            Volatile.Write(ref _onsetPrepared, clips.Count);
                        }
                        if (Volatile.Read(ref _onsetPrepared) == 0) _onsetKey = "";
                        _onsetReason = Volatile.Read(ref _onsetPrepared) == 0 ? "unprepared" : "ready";
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_onsetPreparationGate)
                        if (preparationRevision == Volatile.Read(ref _onsetPreparationRevision) && _onsetKey == key && _onsetPrepared == 0)
                            _onsetKey = "";
                }
                catch (Exception)
                {
                    lock (_onsetPreparationGate)
                    {
                        if (preparationRevision == Volatile.Read(ref _onsetPreparationRevision) && _onsetKey == key)
                        {
                            _onsetKey = "";
                            _onsetReason = "preparation-failed";
                        }
                    }
                } // No alternate voice/engine.
                finally { if (acquired) _onsetSynthesis.Release(); }
            });
        }
    }

    private void NotifyOnset(long session, ulong sequence)
    {
        if (!_onsetEnabled || !_onsetConfigurationValid || session != Volatile.Read(ref _onsetSession) || Volatile.Read(ref _onsetConfigurationChanging) != 0) return;
        var onset = _onset.AcceptUtterance(sequence, _onsetPermitted && Volatile.Read(ref _onsetBodyCount) == 0, out var consumed);
        if (!consumed) return;
        _onsetPlaybackState = onset == null ? "onset-unavailable" : "onset-received";
        _onsetIdle = false;
        var dispatch = Interlocked.Increment(ref _onsetDispatch);
        var preparation = Volatile.Read(ref _onsetPreparation);
        _ = Task.Run(async () =>
        {
            try { preparation?.Cancel(); } catch (ObjectDisposedException) { }
            catch (AggregateException) { _onsetReason = "preparation-failed"; }
            // This live safety check is on a worker, never on the native callback.
            bool permitted;
            var revision = Volatile.Read(ref _onsetSettingsRevision);
            var config = _onsetConfiguration;
            try
            {
                permitted = _onsetPermitted && _onsetPermissionCheck() &&
                    (!string.IsNullOrWhiteSpace(config.OutputDeviceId) || !string.IsNullOrWhiteSpace(config.OutputDeviceName));
            }
            catch { permitted = false; }
            await _onsetHandoff.WaitAsync().ConfigureAwait(false);
            try
            {
                if (dispatch != Volatile.Read(ref _onsetDispatch) || session != Volatile.Read(ref _onsetSession) ||
                    revision != Volatile.Read(ref _onsetSettingsRevision)) return;
                if (!_playbackService.CancelFillerAndWait().Success) { _onsetPlaybackState = "cancel-unconfirmed"; return; }
                if (!permitted || onset == null) return;
                var attempt = _onset.Choose(onset);
                if (attempt == null || !_onset.IsCurrent(attempt)) { _onsetPlaybackState = "selection-rejected"; return; }
                var id = _playbackService.ReserveFiller();
                if (id == 0) { _onsetPlaybackState = "reservation-rejected"; _playbackService.LogFillerState(_onsetPlaybackState); return; }
                _onsetPlaybackState = "waiting-start";
                // Reserve before releasing the handoff. A concurrent body/OFF
                // can cancel this ID before the Play task enters native.
                _ = Task.Run(() =>
                {
                    var result = _playbackService.PlayReservedFiller(id, attempt.Clip.Wav.ToArray(), Math.Clamp(config.MonitorVolumePercent, 0, 100), () =>
                    {
                        _onset.RecordActualStart(attempt);
                        if (dispatch == Volatile.Read(ref _onsetDispatch)) _onsetPlaybackState = "started";
                        _playbackService.LogFillerState("started");
                    });
                    if (!result.Success && dispatch == Volatile.Read(ref _onsetDispatch))
                    { _onsetPlaybackState = "playback-failed"; _playbackService.LogFillerState(_onsetPlaybackState); }
                });
            }
            finally { _onsetHandoff.Release(); }
        });
    }

    private void StopOnsetPreparation()
    {
        lock (_onsetPreparationGate)
        {
            try { _onsetPreparation?.Cancel(); } catch (ObjectDisposedException) { }
            catch (AggregateException) { _onsetReason = "preparation-failed"; }
        }
    }

    private Task<IDisposable> EnterOnsetBodyAsync()
    {
        Interlocked.Increment(ref _onsetBodyCount);
        StopOnsetPreparation();
        // Do not await a preparation task/native Generate/HTTP cancellation.
        // Server-side resource contention is not a cancellation guarantee.
        return Task.FromResult<IDisposable>(new OnsetBodyLease(this));
    }

    private sealed class OnsetBodyLease(TtsEngineRouter owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._onsetBodyCount);
    }

    private bool HandoffOnsetToBody()
    {
        Interlocked.Increment(ref _onsetDispatch);
        if (_onsetEnabled) _onsetPlaybackState = "body-priority";
        _onset.BodyReady();
        _onsetHandoff.Wait();
        try { return _playbackService.CancelFillerAndWait().Success; }
        finally { _onsetHandoff.Release(); }
    }

    private static string NormalizeOnsetLanguage(string language) => (language ?? "").Trim().ToLowerInvariant().Split(['-', '_'])[0];
}
