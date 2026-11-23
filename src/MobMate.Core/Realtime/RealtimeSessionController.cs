using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MobMate.Core.Native;

namespace MobMate.Core.Realtime;

/// <summary>
/// Poll result from a realtime session.
/// </summary>
public sealed class RealtimePollResult
{
    public string State { get; init; } = string.Empty;
    public string Partial { get; init; } = string.Empty;
    public string Final { get; init; } = string.Empty;
    public string SelectedText { get; init; } = string.Empty;
    public string SelectedEngine { get; init; } = string.Empty;
    public string DecisionReason { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string ResolvedScene { get; init; } = string.Empty;
    public bool ConfidenceAvailable { get; init; }
    public double? ConfidenceScore { get; init; }
    public string ConfidenceUnavailableReason { get; init; } = string.Empty;
    public int LatencyMs { get; init; }
    public JsonElement? AddonCandidate { get; init; }
    public long? AddonWallMs { get; init; }
    public string? JointExistingStatus { get; init; }
    public string? JointAddonStatus { get; init; }
    public string? JointSelectionReason { get; init; }
    public long? ExistingJointWallMs { get; init; }
    public long? JointTotalWallMs { get; init; }
    public long? AddonAddedWaitMs { get; init; }
    public long FinalDetectedAtMs { get; init; }
    public string? Error { get; init; }
    public long CaptureSamples { get; init; }
    public double CaptureRms { get; init; }
    public double CaptureAgcGain { get; init; } = 1.0;
    public string VadState { get; init; } = string.Empty;
    public int VadSpeechCount { get; init; }
    public string? RawJson { get; init; }
    public byte[] FinalPcm16Le { get; init; } = Array.Empty<byte>();
    public int FinalPcmSampleRate { get; init; }
}

/// <summary>
/// Manages a native realtime session lifecycle.
/// </summary>
public sealed class RealtimeSessionController : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;
    private ILogger? _logger;
    private long _pollCount;
    private string _lastState = string.Empty;
    private readonly object _nativeGate = new();
    // Rooted until unregister + the serialized poll have completed.
    private NativeLibraryBridge.VadStartCallback? _vadStartCallback;

    /// <summary>Optional enqueue-only handler. Old native libraries remain usable without fillers.</summary>
    public bool SetVadStartHandler(Action<ulong>? handler)
    {
        lock (_nativeGate)
        {
            CheckHandle();
            NativeLibraryBridge.VadStartCallback? callback = handler is null ? null : (sequence, _) =>
            {
                try { handler(sequence); }
                // Never perform logging or other I/O on the native poll thread.
                catch { }
            };
            try
            {
                if (NativeLibraryBridge.mmv2_realtime_set_vad_start_callback(_handle, callback, IntPtr.Zero) != 0)
                    return false;
                _vadStartCallback = callback;
                return true;
            }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>
    /// Creates a new realtime session by loading the native DLL.
    /// </summary>
    public static RealtimeSessionController Create(string nativeExePath, string? optionsJson = null, ILogger? logger = null)
    {
        var dllPath = NativeLibraryBridge.ResolveDllPath(nativeExePath);
        logger?.LogInformation("[Realtime/Native] action=resolve_dll path={DllPath} exists={Exists}", dllPath, File.Exists(dllPath));
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"DLL not found: {dllPath}");
        }

        logger?.LogInformation("[Realtime/Native] action=load_dll begin");
        NativeLibrary.Load(dllPath);
        logger?.LogInformation("[Realtime/Native] action=load_dll ok");

        logger?.LogInformation("[Realtime/Native] action=create begin optionsLength={OptionsLength}", optionsJson?.Length ?? 2);
        var ret = NativeLibraryBridge.mmv2_realtime_create(optionsJson ?? "{}", out IntPtr handle);
        logger?.LogInformation("[Realtime/Native] action=create returned ret={ReturnCode} handleValid={HandleValid}", ret, handle != IntPtr.Zero);
        if (handle == IntPtr.Zero) {
            throw new InvalidOperationException($"mmv2_realtime_create returned null handle (ret={ret})");
        }

        return new RealtimeSessionController { _handle = handle, _logger = logger };
    }

    public void Start()
    {
        lock (_nativeGate)
        {
            CheckHandle();
            _logger?.LogInformation("[Realtime/Native] action=start begin");
            var ret = NativeLibraryBridge.mmv2_realtime_start(_handle);
            _logger?.LogInformation("[Realtime/Native] action=start returned ret={ReturnCode}", ret);
            if (ret != 0)
            {
                throw new InvalidOperationException($"mmv2_realtime_start failed (ret={ret})");
            }
        }
    }

    public void Stop()
    {
        lock (_nativeGate)
        {
            CheckHandle();
            _logger?.LogInformation("[Realtime/Native] action=stop begin");
            var ret = NativeLibraryBridge.mmv2_realtime_stop(_handle);
            _logger?.LogInformation("[Realtime/Native] action=stop returned ret={ReturnCode}", ret);
            if (ret != 0)
            {
                throw new InvalidOperationException($"mmv2_realtime_stop failed (ret={ret})");
            }
        }
    }

    public RealtimePollResult Poll()
    {
        lock (_nativeGate)
        {
            CheckHandle();
            var pollNumber = Interlocked.Increment(ref _pollCount);
            if (pollNumber == 1 || pollNumber % 50 == 0)
                _logger?.LogInformation("[Realtime/Native] action=poll begin count={PollCount}", pollNumber);
            var ret = NativeLibraryBridge.mmv2_realtime_poll(_handle, out IntPtr outJson);
            if (ret != 0 || outJson == IntPtr.Zero)
            {
                _logger?.LogWarning("[Realtime/Native] action=poll failed count={PollCount} ret={ReturnCode} jsonValid={JsonValid}", pollNumber, ret, outJson != IntPtr.Zero);
                return new RealtimePollResult { State = "failed", Error = "poll failed" };
            }

            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
            NativeLibraryBridge.mmv2_free_string(outJson);

            var result = ParseNativePoll(rawJson, _logger);
            if (pollNumber == 1 || pollNumber % 50 == 0 || !string.Equals(_lastState, result.State, StringComparison.Ordinal))
            {
                _lastState = result.State;
                _logger?.LogInformation("[Realtime/Native] action=poll ok count={PollCount} state={State} samples={Samples} rms={Rms} error={Error}", pollNumber, result.State, result.CaptureSamples, result.CaptureRms, result.Error);
            }
            return result;
        }
    }

    /// <summary>Parse the same native final and PCM contract for live polling and recorded replay.</summary>
    public static RealtimePollResult ParseNativePoll(string rawJson, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            byte[] finalPcm = Array.Empty<byte>();
            if (root.TryGetProperty("finalPcm16Base64", out var pcmProp)
                && pcmProp.ValueKind == JsonValueKind.String)
            {
                try { finalPcm = Convert.FromBase64String(pcmProp.GetString() ?? string.Empty); }
                catch (FormatException) { finalPcm = Array.Empty<byte>(); }
            }

            var finalText = root.TryGetProperty("final", out var fp) ? fp.GetString() ?? "" : "";
            var selectedText = root.TryGetProperty("selected_text", out var stp) ? stp.GetString() ?? "" : "";
            JsonElement? addonCandidate = null;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                    if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("engine", out var candidateEngine) &&
                        candidateEngine.ValueKind == JsonValueKind.String && Asr.AddonCatalog.IsAddonEngine(candidateEngine.GetString()) &&
                        candidate.TryGetProperty("success", out _)) addonCandidate = candidate.Clone();
            }
            finalText = RemoveCjkIntraSpaces(finalText);
            selectedText = RemoveCjkIntraSpaces(selectedText);
            var result = new RealtimePollResult
            {
                State = root.TryGetProperty("state", out var sp) ? sp.GetString() ?? "" : "",
                Partial = root.TryGetProperty("partial", out var pp) ? pp.GetString() ?? "" : "",
                Final = finalText,
                SelectedText = selectedText,
                SelectedEngine = root.TryGetProperty("selected_engine", out var sep) ? sep.GetString() ?? "" : "",
                DecisionReason = root.TryGetProperty("decision_reason", out var drp) ? drp.GetString() ?? "" : "",
                Language = root.TryGetProperty("language", out var langp) ? langp.GetString() ?? "" : "",
                ResolvedScene = root.TryGetProperty("resolvedScene", out var rsp) ? rsp.GetString() ?? "" : "",
                ConfidenceAvailable = root.TryGetProperty("confidence_available", out var cap) && cap.ValueKind == JsonValueKind.True,
                ConfidenceScore = root.TryGetProperty("confidence_score", out var csp) && csp.ValueKind == JsonValueKind.Number ? csp.GetDouble() : null,
                ConfidenceUnavailableReason = root.TryGetProperty("confidence_unavailable_reason", out var curp) ? curp.GetString() ?? "" : "",
                LatencyMs = root.TryGetProperty("latencyMs", out var lp) ? lp.GetInt32() : 0,
                AddonCandidate = addonCandidate,
                AddonWallMs = root.TryGetProperty("addonWallMs", out var awp) && awp.ValueKind == JsonValueKind.Number && awp.TryGetInt64(out var addonWall) ? addonWall : null,
                JointExistingStatus = root.TryGetProperty("jointExistingStatus", out var jes) && jes.ValueKind == JsonValueKind.String ? jes.GetString() : null,
                JointAddonStatus = root.TryGetProperty("jointAddonStatus", out var jas) && jas.ValueKind == JsonValueKind.String ? jas.GetString() : null,
                JointSelectionReason = root.TryGetProperty("jointSelectionReason", out var jsr) && jsr.ValueKind == JsonValueKind.String ? jsr.GetString() : null,
                ExistingJointWallMs = root.TryGetProperty("existingJointWallMs", out var ejw) && ejw.ValueKind == JsonValueKind.Number && ejw.TryGetInt64(out var existingWall) ? existingWall : null,
                JointTotalWallMs = root.TryGetProperty("jointTotalWallMs", out var jtw) && jtw.ValueKind == JsonValueKind.Number && jtw.TryGetInt64(out var totalWall) ? totalWall : null,
                AddonAddedWaitMs = root.TryGetProperty("addonAddedWaitMs", out var aaw) && aaw.ValueKind == JsonValueKind.Number && aaw.TryGetInt64(out var addedWait) ? addedWait : null,
                FinalDetectedAtMs = root.TryGetProperty("finalDetectedAtMs", out var fdp) ? fdp.GetInt64() : 0,
                Error = root.TryGetProperty("error", out var ep) && ep.ValueKind != JsonValueKind.Null
                    ? ep.GetString()
                    : null,
                CaptureSamples = root.TryGetProperty("captureSamples", out var cps) ? cps.GetInt64() : 0,
                CaptureRms = root.TryGetProperty("captureRms", out var cr) ? cr.GetDouble() : 0,
                CaptureAgcGain = root.TryGetProperty("captureAgcGain", out var cag) ? cag.GetDouble() : 1.0,
                VadState = root.TryGetProperty("vadState", out var vs) ? vs.GetString() ?? "" : "",
                VadSpeechCount = root.TryGetProperty("vadSpeechCount", out var vsc) ? vsc.GetInt32() : 0,
                RawJson = rawJson,
                FinalPcm16Le = finalPcm,
                FinalPcmSampleRate = root.TryGetProperty("finalPcmSampleRate", out var srp) ? srp.GetInt32() : 0,
            };
            return result;
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "[Realtime/Native] action=poll_json_parse failed rawLength={RawLength}", rawJson.Length);
            return new RealtimePollResult { State = "failed", Error = $"JSON parse error: {ex.Message}", RawJson = rawJson };
        }
    }

    public void Dispose()
    {
        lock (_nativeGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                // The gate has drained poll before unregister/destroy. A callback
                // cannot reenter this controller; it must only enqueue work.
                if (_vadStartCallback is not null)
                    NativeLibraryBridge.mmv2_realtime_set_vad_start_callback(_handle, null, IntPtr.Zero);
                _logger?.LogInformation("[Realtime/Native] action=destroy begin polls={PollCount}", _pollCount);
                NativeLibraryBridge.mmv2_realtime_destroy(_handle);
                _handle = IntPtr.Zero;
                _vadStartCallback = null;
                _logger?.LogInformation("[Realtime/Native] action=destroy ok");
            }
        }
    }

    private void CheckHandle()
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(RealtimeSessionController));
        }
    }

    /// <summary>
    /// CJK文字を含む認識テキストの文中スペースを除去する（日本語・中国語・韓国語）。
    /// </summary>
    private static string RemoveCjkIntraSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (char c in text)
        {
            if ((c >= 0x3000 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7FF))
                return text.Replace(" ", "");
        }
        return text;
    }
}
