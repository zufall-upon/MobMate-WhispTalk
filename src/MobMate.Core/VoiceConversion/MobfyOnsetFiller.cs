using Microsoft.Extensions.Logging;
using MobMate.Core.Native;
using MobMate.Core.Tts;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MobMate.Core.Native.NativeLibraryBridge;

namespace MobMate.Core.VoiceConversion;

/// <summary>
/// Owns one bounded, device-free RVC preparation worker and a session-local bank.
/// All lifecycle state and live-handle access use the host's reentrant session
/// lock. Temporary native create/load/process/destroy never hold that lock.
/// </summary>
public sealed class MobfyOnsetFiller : IDisposable
{
    private readonly TtsEngineRouter _router;
    private readonly string _nativeExe;
    private readonly object _sessionLock;
    private readonly Func<IntPtr> _currentHandle;
    private readonly ILogger _logger;
    private bool _disposed, _enabled, _stopped, _starting, _attached, _idle;
    private IntPtr _attachedHandle;
    private string _language = "", _options = "", _voiceKey = "", _configurationKey = "";
    private string _bankVoiceKey = "", _bankSourceKey = "", _liveVoiceKey = "";
    private string _liveRouteKey = "";
    private VcFillerPcm[] _bank = [];
    private long _revision;
    private long _failedRevision = -1;
    // Notification only, not lifecycle state. The router may raise this while
    // holding its own config lock: it must never wait for sessionLock.
    private int _sourceInvalidationPending;
    private ulong _nativeGeneration, _lastStarted, _startedSerial;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private TaskCompletionSource _idleChanged = NewSignal();
    private string _reason = "unprepared", _playbackReason = "off";

    // Existing friend-smoke boundary: no native library, models or devices.
    internal Func<string, IntPtr>? CreateForTest { get; set; }
    internal Func<IntPtr, bool>? LoadForTest { get; set; }
    internal Func<IntPtr, short[], short[], NativeLibraryResult>? ProcessForTest { get; set; }
    internal Action<IntPtr>? DestroyForTest { get; set; }
    internal VcFillerExchangeCall? ExchangeForTest { get; set; }
    internal Action? WaitingForIdleForTest { get; set; }
    internal Task PreparationCompletion { get { lock (_sessionLock) return _worker ?? Task.CompletedTask; } }
    internal (ulong Id, int Samples, bool Nonzero)[] PreparationEvidence
    {
        get { lock (_sessionLock) return _bank.Select(c => (c.Id, c.Samples.Length, c.Samples.Any(s => s != 0))).ToArray(); }
    }

    public MobfyOnsetFiller(TtsEngineRouter router, string nativeExe, object sessionLock,
        Func<IntPtr> currentHandle, ILogger logger)
    {
        _router = router;
        _nativeExe = nativeExe;
        _sessionLock = sessionLock;
        _currentHandle = currentHandle;
        _logger = logger;
        _router.OnsetSourceChanged += SourceChanged;
    }

    public void Configure(string resolvedOptionsJson, string language, bool enabled)
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            DrainSourceChange();
            ApplyConfiguration(resolvedOptionsJson, language, enabled, false);
            _failedRevision = -1; // An explicit request may retry; status polls may not spin on failure.
            EnsurePreparation();
        }
    }

    public void BeforeStart(string resolvedOptionsJson, string language, bool enabled)
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            DrainSourceChange();
            // Never wait for a cancelled HTTP/synthesis/model/inference call.
            // Start reloads the live model even at unchanged paths. Retire its
            // converted PCM too; loaded/idle status permits fresh preparation.
            ClearBank();
            ApplyConfiguration(resolvedOptionsJson, language, enabled, true);
            _liveVoiceKey = _voiceKey;
            _liveRouteKey = RouteKey(resolvedOptionsJson);
            _starting = true;
            _attached = false;
            _attachedHandle = IntPtr.Zero;
            _startedSerial = 0;
            _idle = false;
            _playbackReason = enabled ? "waiting-start" : "off";
        }
    }

    public void Attach()
    {
        lock (_sessionLock)
        {
            if (_disposed || _stopped) return;
            DrainSourceChange();
            _starting = true;
            _attachedHandle = _currentHandle();
            _attached = _attachedHandle != IntPtr.Zero;
            _startedSerial = 0;
            _idle = false; // The next existing status poll must prove live idle.
            ValidateBank();
            SendBank(true);
        }
    }

    public void ObserveStatus(string nativeStatusRawJson)
    {
        lock (_sessionLock)
        {
            if (_disposed || !_attached || _attachedHandle != _currentHandle()) return;
            DrainSourceChange();
            ReadSnapshot();
            ValidateBank();
            if (_starting && IsRunningLoaded(nativeStatusRawJson))
            {
                _starting = false;
                SendBank(true); // Latest Configure/OFF intent, after model load.
            }
            _idle = IsIdleStatus(nativeStatusRawJson);
            var signal = _idleChanged;
            _idleChanged = NewSignal();
            signal.TrySetResult();
            if (!_idle && _enabled && _playbackReason is not ("native-unavailable" or "selection-mismatch"))
                _playbackReason = "body-priority";
            EnsurePreparation();
        }
    }

    public void Stop()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            DrainSourceChange();
            CancelPreparation();
            ClearBank();
            SendBank(false);
            _attached = false;
            _attachedHandle = IntPtr.Zero;
            _startedSerial = 0;
            _stopped = true;
            _starting = false;
            _idle = false;
            _playbackReason = _enabled ? "stopped" : "off";
        }
    }

    public void Invalidate()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            DrainSourceChange();
            CancelPreparation();
            if (!BankMatches()) ClearBank();
            SendBank(false);
            _stopped = true; // Explicit Configure/BeforeStart supplies new truth.
            _reason = "invalidated";
        }
    }

    public object GetStatus()
    {
        lock (_sessionLock)
        {
            if (!_disposed)
            {
                DrainSourceChange();
                ReadSnapshot();
                ValidateBank();
            }
            return new { enabled = _enabled, prepared = _bank.Length, reason = _reason,
                playbackReason = !_enabled ? "off" : !_attached || _stopped ? "start-required" :
                    _starting ? "waiting-start" : _playbackReason };
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _router.OnsetSourceChanged -= SourceChanged;
            if (_worker == null) { _cancellation?.Dispose(); _cancellation = null; }
            // In-flight native calls own their temporary handle until their
            // finally block. Dispose/Start/body never join that worker.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ApplyConfiguration(string json, string language, bool enabled, bool force)
    {
        var normalized = (language ?? "").Trim().ToLowerInvariant().Split(['-', '_'])[0];
        var valid = TryPreparationOptions(json, normalized, out var options, out var voiceKey, out var configurationKey);
        var changed = force || _configurationKey != configurationKey || _language != normalized || _enabled != enabled || _stopped;
        if (changed)
        {
            CancelPreparation();
            // Clear native active/bank before replacing any configuration.
            SendBank(false);
        }
        _language = normalized;
        _options = options;
        _voiceKey = voiceKey;
        _configurationKey = configurationKey;
        _enabled = enabled; // Preparation never changes the user's OFF intent.
        _stopped = false;
        if (!valid || !BankMatches()) ClearBank();
        _reason = !valid ? "unsupported-options" : _bank.Length == 5 ? "ready" : "unprepared";
        _playbackReason = enabled ? "waiting-onset" : "off";
        if (!force && changed && _attached) SendBank(true);
    }

    private void SourceChanged()
    {
        Interlocked.Exchange(ref _sourceInvalidationPending, 1);
        if (!Monitor.TryEnter(_sessionLock)) return;
        try
        {
            if (_disposed) return;
            DrainSourceChange();
        }
        finally { Monitor.Exit(_sessionLock); }
    }

    private void DrainSourceChange()
    {
        if (Interlocked.Exchange(ref _sourceInvalidationPending, 0) == 0) return;
        CancelPreparation();
        ClearBank();
        SendBank(false);
        _reason = "source-changed";
        // No preparation here: SetConfig/source preparation may still be
        // changing. Configure or the existing host poll retries when ready.
    }

    private bool BankMatches() => _bank.Length == 5 && _bankVoiceKey == _voiceKey &&
        _bankSourceKey == _router.GetOnsetSourceKey(_language);

    private void ValidateBank()
    {
        if (_bank.Length == 0 || BankMatches()) return;
        CancelPreparation();
        ClearBank();
        SendBank(false);
        _reason = "source-mismatch";
    }

    private void ClearBank()
    {
        _bank = [];
        _bankVoiceKey = _bankSourceKey = "";
        _reason = "unprepared";
    }

    private void CancelPreparation()
    {
        ++_revision;
        _cancellation?.Cancel();
        var signal = _idleChanged;
        _idleChanged = NewSignal();
        signal.TrySetResult();
    }

    private void EnsurePreparation()
    {
        if (_disposed || _stopped || _starting || _failedRevision == _revision || _voiceKey.Length == 0 || _bank.Length == 5 ||
            (_worker != null && !_worker.IsCompleted) || (_attached && !_idle)) return;
        // One task only, not a queue of cancelled tasks waiting on a semaphore.
        var revision = _revision;
        var language = _language;
        var voiceKey = _voiceKey;
        var options = _options;
        var sourceKey = _router.GetOnsetSourceKey(language);
        if (sourceKey == null) { _reason = "source-unavailable"; return; }
        _cancellation?.Dispose();
        var cancellation = _cancellation = new();
        _reason = "preparing";
        _worker = Task.Run(() => PrepareAsync(revision, language, voiceKey, sourceKey, options, cancellation));
    }

    private bool Current(long revision, string sourceKey, CancellationToken token) =>
        !_disposed && !_stopped && !_starting && !token.IsCancellationRequested &&
        _revision == revision && sourceKey == _router.GetOnsetSourceKey(_language);

    private async Task WaitForIdleAsync(long revision, string sourceKey, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_sessionLock)
            {
                if (!Current(revision, sourceKey, token)) throw new OperationCanceledException(token);
                if (!_attached || _idle) return;
                _reason = "waiting-idle";
                WaitingForIdleForTest?.Invoke();
                changed = _idleChanged.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private async Task PrepareAsync(long revision, string language, string voiceKey, string sourceKey,
        string options, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var handle = IntPtr.Zero;
        try
        {
            await _router.OnsetPreparationCompletion.WaitAsync(token).ConfigureAwait(false);
            if (!_router.TryGetOnsetSourceSnapshot(language, out var source) || source!.Key != sourceKey)
                throw new InvalidOperationException("Source preparation is unavailable");
            var inputs = source.Wavs.Select(wav => DecodeSource(wav) ?? throw new InvalidOperationException("Invalid source PCM")).ToArray();
            await WaitForIdleAsync(revision, sourceKey, token).ConfigureAwait(false);
            // These calls are deliberately outside sessionLock. The temporary
            // context has its own model mutex and never starts capture/playback.
            if (CreateForTest != null) handle = CreateForTest(options);
            else
            {
                var created = VoiceConversionCreate(_nativeExe, options, out handle);
                if (!created.Success) throw new InvalidOperationException("Preparation context creation failed: " + created.Message);
            }
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Preparation context is unavailable");
            await WaitForIdleAsync(revision, sourceKey, token).ConfigureAwait(false);
            if (!(LoadForTest?.Invoke(handle) ?? VoiceConversionLoadModels(handle).Success))
                throw new InvalidOperationException("Preparation model load failed");
            var clips = new List<VcFillerPcm>(5);
            foreach (var input in inputs)
            {
                await WaitForIdleAsync(revision, sourceKey, token).ConfigureAwait(false);
                var output = new short[input.Length];
                var converted = ProcessForTest?.Invoke(handle, input, output) ?? ProcessVoiceConversionFillerBuffer(handle, input, output);
                // Success alone includes DSP-only processing. Never publish
                // passthrough, silence, partial pools, or an obsolete result.
                token.ThrowIfCancellationRequested();
                if (!Converted(converted, output)) throw new InvalidOperationException("RVC conversion failed");
                clips.Add(new(PcmIdentity(output), output));
            }
            lock (_sessionLock)
            {
                if (!Current(revision, sourceKey, token) || _voiceKey != voiceKey ||
                    clips.Count != 5 || clips.Select(c => c.Id).Distinct().Count() != 5) return;
                _bank = clips.ToArray();
                _bankVoiceKey = voiceKey;
                _bankSourceKey = sourceKey;
                _reason = "ready";
                SendBank(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_sessionLock)
                if (Current(revision, sourceKey, token))
                {
                    _reason = "preparation-failed";
                    _failedRevision = revision;
                }
            _logger.LogDebug(ex, "[VC filler] preparation unavailable");
        }
        finally
        {
            // Cancellation cannot destroy an in-flight process_buffer handle.
            // Destruction follows its synchronous return, never the live handle.
            if (handle != IntPtr.Zero)
            {
                try
                {
                    if (DestroyForTest != null) DestroyForTest(handle);
                    else VoiceConversionDestroy(handle);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[VC filler] preparation destroy failed"); }
            }
            lock (_sessionLock)
            {
                _worker = null;
                // Restart only a superseding request, never a failed-generation
                // retry loop. An explicit Configure may request another attempt.
                if (_disposed) { _cancellation?.Dispose(); _cancellation = null; }
                else if (revision != _revision) EnsurePreparation();
            }
        }
    }

    private void Remember(VcFillerSnapshot snapshot)
    {
        if (snapshot.StartedSerial <= _startedSerial || snapshot.StartedClipId == 0) return;
        _startedSerial = snapshot.StartedSerial;
        _lastStarted = snapshot.StartedClipId; // Even ACKs from invalidated generations.
        _playbackReason = "started";
    }

    private void ReadSnapshot()
    {
        if (!_attached || _attachedHandle == IntPtr.Zero || _attachedHandle != _currentHandle()) return;
        if (TryVoiceConversionFillerExchange(_attachedHandle, null, out var snapshot, ExchangeForTest)) Remember(snapshot);
        else _playbackReason = "native-unavailable";
    }

    private void SendBank(bool includeBank)
    {
        if (!_attached || _attachedHandle == IntPtr.Zero || _attachedHandle != _currentHandle()) return;
        ReadSnapshot();
        // A UI selection may differ from the still-running session. Selection
        // never restarts capture or publishes the new voice into that old route.
        var selectedLiveVoice = _voiceKey == _liveVoiceKey && _liveRouteKey != "invalid-route" &&
            RouteKey(_options) == _liveRouteKey;
        includeBank = includeBank && selectedLiveVoice;
        var clips = includeBank ? _bank : [];
        // Until this handle actually starts a clip, every update must retain the
        // prior handle's seed. Native ignores this seed after its own start ACK.
        var bank = new VcFillerBank(checked(++_nativeGeneration), includeBank && _enabled && !_stopped && !_starting,
            clips, _lastStarted);
        if (TryVoiceConversionFillerExchange(_attachedHandle, bank, out var snapshot, ExchangeForTest))
        {
            Remember(snapshot);
            if (!selectedLiveVoice) _playbackReason = "selection-mismatch";
            else if (includeBank && _enabled && snapshot.StartedSerial == 0)
                _playbackReason = _starting ? "waiting-start" : "waiting-onset";
        }
        else _playbackReason = "native-unavailable";
    }

    internal static short[]? DecodeSource(byte[] wav)
    {
        if (wav.Length > 1024 * 1024) return null;
        var decoded = TtsPlaybackService.ParseWavPcm16(wav, out _);
        if (decoded is not { } pcm || pcm.sampleRate is < 8000 or > 192000 ||
            pcm.channels is < 1 or > 2 || pcm.samples.Length == 0 || pcm.samples.Length % pcm.channels != 0) return null;
        // The reused body decoder deliberately tolerates a trailing partial
        // sample. A publishable filler must contain whole PCM16 channel frames.
        for (var position = 12; position + 8 <= wav.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(position + 4));
            if (size < 0 || size > wav.Length - position - 8) return null;
            if (wav.AsSpan(position, 4).SequenceEqual("data"u8))
            {
                if (size % (2 * pcm.channels) != 0) return null;
                break;
            }
            position += 8 + size + (size & 1);
        }
        var frames = pcm.samples.Length / pcm.channels;
        if (frames > (long)pcm.sampleRate * 3) return null;
        var mono = new short[frames];
        for (var i = 0; i < frames; ++i)
            mono[i] = pcm.channels == 1 ? pcm.samples[i] : (short)(((int)pcm.samples[2 * i] + pcm.samples[2 * i + 1]) / 2);
        if (pcm.sampleRate == 48000) return mono;
        var count = (int)((long)frames * 48000 / pcm.sampleRate);
        if (count is < 1 or > 144000) return null;
        var result = new short[count];
        for (var i = 0; i < count; ++i)
        {
            var position = (double)i * pcm.sampleRate / 48000;
            var left = Math.Min((int)position, frames - 1);
            var right = Math.Min(left + 1, frames - 1);
            result[i] = (short)Math.Clamp(Math.Round(mono[left] + (mono[right] - mono[left]) * (position - left)), short.MinValue, short.MaxValue);
        }
        return result;
    }

    private static string RouteKey(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            var monitor = root.TryGetProperty("monitorVolumePercent", out var volume) ? volume.GetDouble() : 0;
            if (!double.IsFinite(monitor)) return "invalid-route";
            return JsonSerializer.Serialize(new { input = Text("inputDeviceId"), output = Text("outputDeviceId"), monitor });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { return "invalid-route"; }
    }

    private static bool Converted(NativeLibraryResult result, short[] output)
    {
        if (!result.Success || output.Length is < 1 or > 144000 || !output.Any(s => s != 0)) return false;
        try
        {
            using var json = JsonDocument.Parse(result.RawJson ?? "{}");
            return json.RootElement.TryGetProperty("vcApplied", out var applied) && applied.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static ulong PcmIdentity(short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), pcm[i]);
        var id = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(bytes));
        if (id == 0) throw new InvalidOperationException("Invalid clip identity");
        return id;
    }

    internal static bool IsIdleStatus(string rawJson)
    {
        try
        {
            using var json = JsonDocument.Parse(rawJson);
            var root = json.RootElement;
            static bool False(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
            static bool True(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            static bool Zero(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count == 0;
            if (!True(root, "ok") || !True(root, "running") || !Zero(root, "ringBufferFill") ||
                !root.TryGetProperty("vad", out var vad) || !True(vad, "enabled") ||
                !False(vad, "speechActive") || !False(vad, "laughAsrPending")) return false;
            if (root.TryGetProperty("vadSpeechActive", out var active) && active.ValueKind != JsonValueKind.False) return false;
            // The current RVC status exposes pending ASR and converted output
            // here. Unknown/missing busy-state fields remain fail-closed.
            return root.TryGetProperty("rtRvc", out var rt) && True(rt, "active") &&
                False(rt, "asrGateActive") && Zero(rt, "asrGatePendingSamples") && Zero(rt, "postRingFill");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool IsRunningLoaded(string rawJson)
    {
        try
        {
            using var json = JsonDocument.Parse(rawJson);
            var root = json.RootElement;
            static bool True(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            return True(root, "ok") && True(root, "running") && True(root, "conversionRuntimeAvailable") &&
                root.TryGetProperty("onnx", out var onnx) && True(onnx, "loaded");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static bool TryPreparationOptions(string json, string language, out string options,
        out string voiceKey, out string configurationKey)
    {
        options = voiceKey = "";
        configurationKey = json;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
                document.RootElement.EnumerateObject().Count() || JsonNode.Parse(json) is not JsonObject obj) return false;
            configurationKey = Canonical(obj);
            string Text(string key) => obj[key]?.GetValue<string>() ?? "";
            var engine = Text("engine").ToLowerInvariant();
            if (engine is "" or "onnx") engine = "rvc_onnx"; // Native's RVC aliases/default, not a fallback engine.
            if (engine != "rvc_onnx" || (Text("modelPath").Length == 0 && Text("rvcModelDir").Length == 0) ||
                (obj["sampleRate"]?.GetValue<int>() ?? 0) is not (0 or 48000)) return false;
            // Keep the selected RVC model/speaker/pitch/raw/DSP and hardware
            // backend. Do not silently substitute CPU or another TTS engine.
            obj["sampleRate"] = 48000;
            obj["engine"] = engine;
            obj["wavOutputEnabled"] = false;
            obj["wavOutputDirectory"] = "";
            obj["debugDumpEnabled"] = false;
            obj["debugDumpDir"] = "";
            options = obj.ToJsonString();
            // Effective native defaults, aliases and canonical property order,
            // not raw JSON ordering or output/storage settings, identify voice.
            var voice = JsonNode.Parse("""
                {"mode":"","engine":"rvc_onnx","modelId":"","modelPath":"","sampleRate":48000,"frameMs":0,"hopMs":0,
                 "prosodyNeutralizerEnabled":true,"pressureEstimatorEnabled":true,"adaptiveConversionStrength":true,
                 "neutralizerPreset":"flat","neutralizerStrength":1,"noiseCleanup":"dtln","pressureFlatten":-1,
                 "pressureSuppressorStrength":0.8,"breathSuppress":-1,"f0Flatten":0.5,"worldWet":2,
                 "experimentalVoiceModel":false,"energyCompression":0.95,"shoutSuppression":0.75,"sharpnessSuppression":0.75,
                 "noiseGate":0.35,"outputLimiterCeiling":0.85,"outputVolumePercent":140,
                 "contentEncoderFile":"","f0ModelFile":"","vcModelFile":"","speakerId":0,"f0ShiftSemitones":1,"useGpu":false,
                 "sileroVadModelPath":"","onnxRuntimeDllDir":"","vadStartThreshold":0.54,"vadContinueThreshold":0.36,
                 "laughAsrAssistEnabled":true,"moonshineDllDir":"","moonshineModelRoot":"","laughAsrLanguage":"ja",
                 "laughAsrTokens":"","seModelDir":""}
                """)!.AsObject();
            foreach (var pair in obj)
                if (pair.Value != null) voice[pair.Key] = pair.Value.DeepClone();
            voice["mode"] = Text("mode").ToLowerInvariant();
            voice["modelId"] = Text("modelId").Length == 0 ? Text("id") : Text("modelId");
            voice["modelPath"] = Text("modelPath").Length == 0 ? Text("rvcModelDir") : Text("modelPath");
            foreach (var name in new[] { "inputDeviceId", "outputDeviceId", "inputDeviceName", "outputDeviceName",
                "monitorVolumePercent", "wavOutputEnabled", "wavOutputDirectory", "debugDumpEnabled", "debugDumpDir", "id", "rvcModelDir" })
                voice.Remove(name);
            voiceKey = language + "/" + Canonical(voice);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException) { return false; }
    }

    private static string Canonical(JsonNode node)
    {
        JsonNode? Sort(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Sort(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
            JsonValue scalar when scalar.TryGetValue<double>(out var number) => JsonValue.Create(number),
            _ => value?.DeepClone()
        };
        return Sort(node)!.ToJsonString();
    }
}
