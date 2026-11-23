using System.Runtime.InteropServices;
using System.Text;
using MobMate.Core.Native;

namespace MobMate.Core.Asr;

/// <summary>Adapts the same native PCM callback for realtime and manifest inference.</summary>
public sealed class AddonNativeTranscriber
{
    private readonly AddonManager? _manager;
    private readonly AddonLanguageManager? _languageManager;
    private readonly CancellationToken _stopping;
    private readonly AddonOperationGate? _operationGate;
    // Fixed before measurement. The existing Joint path treats Whisper >1500ms
    // as stalled; allow two seconds of additional waiting for this candidate.
    internal static readonly TimeSpan JointWaitLimit = TimeSpan.FromSeconds(2);
    public NativeLibraryBridge.AddonTranscriber Callback { get; }
    public NativeLibraryBridge.AddonTranscriber JointCallback { get; }
    public NativeLibraryBridge.AddonTranscriberV2 CallbackV2 { get; }
    public NativeLibraryBridge.AddonTranscriberV2 JointCallbackV2 { get; }

    public AddonNativeTranscriber(AddonManager manager, CancellationToken stopping, AddonOperationGate? operationGate = null)
        : this(manager, null, stopping, operationGate) { }

    public AddonNativeTranscriber(AddonLanguageManager manager, CancellationToken stopping, AddonOperationGate? operationGate = null)
        : this(null, manager, stopping, operationGate) { }

    private AddonNativeTranscriber(AddonManager? manager, AddonLanguageManager? languageManager,
        CancellationToken stopping, AddonOperationGate? operationGate)
    {
        _manager = manager;
        _languageManager = languageManager;
        _stopping = stopping;
        _operationGate = operationGate;
        Callback = Transcribe;
        JointCallback = TranscribeJoint;
        CallbackV2 = TranscribeV2;
        JointCallbackV2 = TranscribeJointV2;
    }

    private AddonState Snapshot() => _languageManager?.Snapshot() ?? _manager!.Snapshot();

    private Task<AddonRecognitionResult> Recognize(string? engine, string? language, string mode,
        byte[] bytes, int sampleRate, AddonAsrCandidate existing, TimeSpan timeout, CancellationToken token, bool joint)
        => _languageManager is not null
            ? engine is null ? _languageManager.RecognizeAsync(bytes, sampleRate, existing, timeout, token, joint)
                : _languageManager.RecognizeContextAsync(engine, language ?? "", mode, bytes, sampleRate, existing, timeout, token, joint)
            : engine is null ? _manager!.RecognizeAsync(bytes, sampleRate, existing, timeout, token)
                : _manager!.RecognizeContextAsync(engine, language ?? "", mode, bytes, sampleRate, existing, timeout, token);

    internal int Transcribe(IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int capacity, out long inferenceMs)
        => TranscribeCore(pcm16, count, sampleRate, textUtf8, capacity, out inferenceMs, null, null, "requested");

    private int TranscribeV2(IntPtr engine, IntPtr language, IntPtr mode, IntPtr pcm16, int count, int sampleRate,
        IntPtr textUtf8, int capacity, out long inferenceMs)
    {
        inferenceMs = -1;
        try {
            var id = Marshal.PtrToStringUTF8(engine); var lang = Marshal.PtrToStringUTF8(language);
            if (!AddonCatalog.IsAddonEngine(id) || string.IsNullOrWhiteSpace(lang)) return -1;
            return TranscribeCore(pcm16, count, sampleRate, textUtf8, capacity, out inferenceMs,
                id, lang, Marshal.PtrToStringUTF8(mode) ?? "requested"); }
        catch { return -1; }
    }

    private int TranscribeCore(IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int capacity, out long inferenceMs,
        string? engine, string? language, string mode)
    {
        inferenceMs = -1;
        try
        {
            if (engine is null && Snapshot().EngineId != AddonCatalog.HaasId) return -1;
            if (pcm16 == IntPtr.Zero || textUtf8 == IntPtr.Zero || count <= 0 || count > 6 * 1024 * 1024 || capacity < 1)
                return -1;
            var bytes = CopySamples(pcm16, count);
            var existing = new AddonAsrCandidate("none", "", false, -1, "unavailable");
            var work = Recognize(engine, language, mode, bytes, sampleRate, existing, TimeSpan.FromMinutes(1), _stopping, false);
            var result = work
                .GetAwaiter().GetResult();
            if (result.Candidate is not { Succeeded: true } candidate || candidate.InferenceMilliseconds < 0 || candidate.Text.Contains('\0')) return -1;
            if (candidate.Text.Any(c => c < ' ' && c is not ('\n' or '\r' or '\t'))) return -1;
            var text = Encoding.UTF8.GetBytes(candidate.Text);
            if (text.Length >= capacity) return -1;
            Marshal.Copy(text, 0, textUtf8, text.Length);
            Marshal.WriteByte(textUtf8, text.Length, 0);
            inferenceMs = candidate.InferenceMilliseconds;
            return 0;
        }
        catch (Exception)
        {
            // Exceptions must never cross the unmanaged callback boundary.
            return -1;
        }
    }

    internal int TranscribeJoint(IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int capacity, out long inferenceMs)
        => TranscribeJointCore(pcm16, count, sampleRate, textUtf8, capacity, out inferenceMs, null, null, "requested");

    private int TranscribeJointV2(IntPtr engine, IntPtr language, IntPtr mode, IntPtr pcm16, int count, int sampleRate,
        IntPtr textUtf8, int capacity, out long inferenceMs)
    {
        inferenceMs = -1;
        try {
            var id = Marshal.PtrToStringUTF8(engine); var lang = Marshal.PtrToStringUTF8(language);
            if (!AddonCatalog.IsAddonEngine(id) || string.IsNullOrWhiteSpace(lang)) return -1;
            return TranscribeJointCore(pcm16, count, sampleRate, textUtf8, capacity, out inferenceMs,
                id, lang, Marshal.PtrToStringUTF8(mode) ?? "requested"); }
        catch { return -1; }
    }

    private int TranscribeJointCore(IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int capacity, out long inferenceMs,
        string? engine, string? language, string mode)
    {
        inferenceMs = -1;
        try
        {
            if (engine is null && Snapshot().EngineId != AddonCatalog.HaasId) return -1;
            if (pcm16 == IntPtr.Zero || textUtf8 == IntPtr.Zero || count <= 0 || count > 6 * 1024 * 1024 || capacity < 1) return -1;
            var bytes = CopySamples(pcm16, count);
            var lease = _operationGate?.TryEnterWork();
            if (_operationGate is not null && lease is null) return -1;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            var existing = new AddonAsrCandidate("none", "", false, -1, "unavailable");
            var task = Task.Run(async () =>
            {
                try { return await Recognize(engine, language, mode, bytes, sampleRate, existing, JointWaitLimit, cancellation.Token, true).ConfigureAwait(false); }
                finally { lease?.Dispose(); }
            });
            bool completed;
            try { completed = task.Wait(JointWaitLimit); }
            catch { cancellation.Dispose(); throw; }
            if (!completed)
            {
                try { cancellation.Cancel(); }
                catch (AggregateException) { /* Cleanup still owns the work lease. */ }
                _ = task.ContinueWith(t => { _ = t.Exception; cancellation.Dispose(); }, TaskScheduler.Default);
                return -2;
            }
            cancellation.Dispose();
            var result = task.GetAwaiter().GetResult();
            if (result.Error is "OperationCanceledException" or "TaskCanceledException" or "TimeoutException") return -2;
            if (result.Error == "addon_unavailable") return -3;
            if (result.Candidate is not { Succeeded: true } candidate || candidate.InferenceMilliseconds < 0 || candidate.Text.Contains('\0') ||
                candidate.Text.Any(c => c < ' ' && c is not ('\n' or '\r' or '\t'))) return -1;
            var text = Encoding.UTF8.GetBytes(candidate.Text);
            if (text.Length >= capacity) return -1;
            Marshal.Copy(text, 0, textUtf8, text.Length);
            Marshal.WriteByte(textUtf8, text.Length, 0);
            inferenceMs = candidate.InferenceMilliseconds;
            return 0;
        }
        catch (Exception) { return -1; }
    }

    private static byte[] CopySamples(IntPtr pcm16, int count)
    {
        var samples = new short[count];
        Marshal.Copy(pcm16, samples, 0, count);
        var bytes = new byte[count * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
