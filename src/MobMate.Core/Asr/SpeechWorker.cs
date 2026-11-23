using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MobMate.Core.Asr;

/// <summary>One resident speech.cpp worker. A protocol failure disables this instance.</summary>
public sealed class SpeechWorker : IAddonRecognizer, IContextualAddonRecognizer, IConfirmedAddonStop
{
    private const int MaxResponseChars = 65536;
    private const int MaxAudioBytes = 12 * 1024 * 1024;
    private const int ChunkBytes = 32768;
    private readonly Process _process;
    private readonly SafeFileHandle? _job;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly char[] _readBuffer = new char[4096];
    private int _readOffset;
    private int _readCount;
    private int _closed;
    private bool _stopConfirmed;
    private bool _initialized;
    private bool _inferred;
    private readonly string _engineId;
    private readonly string? _language;
    private readonly object _diagnosticSync = new();
    private readonly StringBuilder _diagnostics = new();
    private bool _diagnosticsTruncated;
    private readonly Task _stderrTask;

    private SpeechWorker(Process process, SafeFileHandle? job, string engineId, string? language)
    {
        _process = process;
        _job = job;
        _engineId = engineId;
        _language = language;
        _stderrTask = DrainStderrAsync(process.StandardError);
    }

    public bool Initialized => _initialized && IsAlive;
    public bool HasSuccessfulInference => _inferred && IsAlive;
    public bool IsAlive => Volatile.Read(ref _closed) == 0 && !_process.HasExited;
    bool IConfirmedAddonStop.StopConfirmed => _stopConfirmed;
    internal int ProcessId => _process.Id;
    internal string? LastResponseJson { get; private set; }
    internal string Diagnostics { get { lock (_diagnosticSync) return _diagnostics.ToString(); } }
    internal bool DiagnosticsTruncated { get { lock (_diagnosticSync) return _diagnosticsTruncated; } }

    /// <summary>Start only an already prepared package, using its explicit model path.</summary>
    public static Task<SpeechWorker> StartAsync(PreparedAddonPackage package, TimeSpan timeout, CancellationToken cancellationToken = default)
        => StartAsync(package, AddonCatalog.HaasId, null, timeout, cancellationToken);

    // Engine context belongs to the verified caller, not a filename guess or a worker-supplied label.
    internal static Task<SpeechWorker> StartAsync(PreparedAddonPackage package, string engineId, string? language,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var linux = SpeechLinuxRuntime.Available && engineId == AddonCatalog.QwenId;
        if (!OperatingSystem.IsWindows() && !linux) throw new PlatformNotSupportedException("The addon runtime is unsupported");
        if (!Path.IsPathFullyQualified(package.RuntimeExecutable) || !File.Exists(package.RuntimeExecutable) ||
            !Path.IsPathFullyQualified(package.ModelFile) || !File.Exists(package.ModelFile) ||
            !package.ModelFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Prepared runtime or model is unavailable");
#if DEBUG && QWEN_PRIVATE_VULKAN
        var privateRoot = Environment.GetEnvironmentVariable(SpeechPrivateVulkanRuntime.RootVariable);
        if (linux && privateRoot is not null)
            return StartPrivateVulkanAsync(package, privateRoot, timeout, cancellationToken, engineId, language);
#endif
        var start = CreateStartInfo(package.RuntimeExecutable, package.ModelFile, linux ? "cpu" : "gpu", linux);
        return StartProcessAsync(start, timeout, cancellationToken, engineId, language);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string model, string device, bool linux)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("worker");
        start.ArgumentList.Add(model);
        start.ArgumentList.Add("--device");
        start.ArgumentList.Add(device);
        if (linux)
        {
            start.ArgumentList.Add("--threads");
            start.ArgumentList.Add("4");
        }
        start.Environment["SPEECH_NO_UPDATE_CHECK"] = "1";
        return start;
    }

#if DEBUG && QWEN_PRIVATE_VULKAN
    private static async Task<SpeechWorker> StartPrivateVulkanAsync(PreparedAddonPackage package, string root, TimeSpan timeout,
        CancellationToken token, string engineId, string? language)
    {
        var executable = await SpeechPrivateVulkanRuntime.VerifyAsync(root, token).ConfigureAwait(false);
        var start = CreateStartInfo(executable, package.ModelFile, "gpu", linux: true);
        return await StartProcessAsync(start, timeout, token, engineId, language, root).ConfigureAwait(false);
    }
#endif

    internal static async Task<SpeechWorker> StartProcessAsync(ProcessStartInfo start, TimeSpan timeout,
        CancellationToken cancellationToken = default, string engineId = AddonCatalog.HaasId, string? language = null
#if DEBUG && QWEN_PRIVATE_VULKAN
        , string? privateVulkanRoot = null
#endif
        )
    {
        CheckTimeout(timeout);
        if (engineId is not (AddonCatalog.HaasId or "parakeet_ja" or "qwen3_asr_ja") ||
            (engineId == "qwen3_asr_ja" ? language is not ("ja" or "en" or "auto") : language is not null))
            throw new ArgumentException("Unsupported addon engine/language context");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false, true);
        start.StandardOutputEncoding = new UTF8Encoding(false, true);
        start.StandardErrorEncoding = new UTF8Encoding(false, false);
        var process = new Process { StartInfo = start };
        SafeFileHandle? job = null;
        SpeechWorker? worker = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Worker did not start");
            if (OperatingSystem.IsWindows()) job = CreateKillOnCloseJob(process);
            worker = new SpeechWorker(process, job, engineId, language);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var line = await worker.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (GetString(root, "type") != "ready" ||
                !root.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != 4 ||
                GetString(root, "version") != "0.10.1")
                throw new InvalidDataException("Worker returned an incompatible ready message");
#if DEBUG && QWEN_PRIVATE_VULKAN
            if (privateVulkanRoot is not null)
            {
                await worker.WriteAsync(new { type = "info", id = "private-vulkan-info" }, deadline.Token).ConfigureAwait(false);
                var infoLine = await worker.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                using var info = JsonDocument.Parse(infoLine);
                if (GetString(info.RootElement, "type") != "end" || GetString(info.RootElement, "id") != "private-vulkan-info")
                    throw new InvalidDataException("Private Vulkan worker returned incompatible info");
                await SpeechPrivateVulkanRuntime.RecordAsync(privateVulkanRoot, process.Id, line, infoLine,
                    worker.Diagnostics, worker.DiagnosticsTruncated, deadline.Token).ConfigureAwait(false);
            }
#endif
            worker._initialized = true;
            return worker;
        }
        catch (Exception error)
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                error.Data["workerStderr"] = worker.Diagnostics;
                error.Data["workerStderrTruncated"] = worker.DiagnosticsTruncated;
            }
            else
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                job?.Dispose();
                process.Dispose();
            }
            throw;
        }
    }

    /// <summary>Returns the worker's candidate, not a Joint selection or confidence score.</summary>
    public Task<AddonAsrCandidate> TranscribeAsync(byte[] pcm16, int sampleRate, TimeSpan timeout, CancellationToken cancellationToken = default)
        => TranscribeCoreAsync(pcm16, sampleRate, timeout, cancellationToken, _language);

    Task<AddonAsrCandidate> IContextualAddonRecognizer.TranscribeContextAsync(byte[] pcm16, int sampleRate, string language,
        string hintMode, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (hintMode is not ("requested" or "auto") || (hintMode == "auto" && _engineId != AddonCatalog.QwenId))
            throw new ArgumentException("Unsupported addon hint mode");
        var hint = _engineId == AddonCatalog.QwenId ? (hintMode == "auto" ? "auto" : language) : null;
        return TranscribeCoreAsync(pcm16, sampleRate, timeout, cancellationToken, hint);
    }

    private async Task<AddonAsrCandidate> TranscribeCoreAsync(byte[] pcm16, int sampleRate, TimeSpan timeout,
        CancellationToken cancellationToken, string? language)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        CheckTimeout(timeout);
        if (pcm16.Length == 0 || pcm16.Length > MaxAudioBytes || pcm16.Length % 2 != 0 || sampleRate < 8000 || sampleRate > 192000)
            throw new ArgumentOutOfRangeException(nameof(pcm16), "Expected bounded mono PCM16 and a valid sample rate");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Initialized) throw new InvalidOperationException("Worker is not available");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var id = Guid.NewGuid().ToString("N");
            var watch = Stopwatch.StartNew();
            try
            {
                var seq = 0;
                for (var offset = 0; offset < pcm16.Length; offset += ChunkBytes)
                {
                    var count = Math.Min(ChunkBytes, pcm16.Length - offset);
                    await WriteAsync(new { type = "chunk", id, seq = seq++, pcm = Convert.ToBase64String(pcm16, offset, count) }, deadline.Token).ConfigureAwait(false);
                }
                object request = language is null
                    ? new { type = "transcribe", id, sample_rate = sampleRate }
                    : new { type = "transcribe", id, sample_rate = sampleRate, language };
                await WriteAsync(request, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    var line = await ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (GetString(root, "id") != id) throw new InvalidDataException("Worker response ID mismatch");
                    var type = GetString(root, "type");
                    if (type == "progress") continue;
                    if (type != "end") throw new InvalidDataException($"Worker request failed: {type}");
                    var text = GetString(root, "text") ?? throw new InvalidDataException("Worker response has no text");
                    var stop = GetString(root, "stop");
                    if (stop is not ("complete" or "model_limit")) throw new InvalidDataException("Worker response has invalid stop reason");
                    _inferred = true;
                    LastResponseJson = line;
                    return new AddonAsrCandidate(_engineId, text, true, watch.ElapsedMilliseconds, stop);
                }
            }
            catch (Exception error)
            {
                // Ask the worker to cancel, but do not rely on it responding or reuse
                // stdout after a failed request. A wedged worker is killed below.
                try
                {
                    using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    await WriteAsync(new { type = "cancel", id }, cancelDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
                await DisposeAsync().ConfigureAwait(false);
                error.Data["workerStderr"] = Diagnostics;
                error.Data["workerStderrTruncated"] = DiagnosticsTruncated;
                throw;
            }
        }
        finally { _requestGate.Release(); }
    }

    private static void CheckTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static string? GetString(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private async Task WriteAsync(object message, CancellationToken token)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await _process.StandardOutput.ReadAsync(_readBuffer.AsMemory(), token).ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0) throw new EndOfStreamException("Worker closed stdout");
            }
            var c = _readBuffer[_readOffset++];
            if (c == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaxResponseChars) throw new InvalidDataException("Worker response exceeds size limit");
            line.Append(c);
        }
    }

    private async Task DrainStderrAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                lock (_diagnosticSync)
                {
                    if (count > 32768 - _diagnostics.Length) _diagnosticsTruncated = true;
                    if (_diagnostics.Length < 32768)
                        _diagnostics.Append(buffer, 0, Math.Min(count, 32768 - _diagnostics.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _initialized = false;
        _inferred = false;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            _stopConfirmed = _process.HasExited;
        }
        catch (InvalidOperationException) { }
        finally
        {
            _job?.Dispose();
            // EOF follows process exit; never let diagnostics turn cleanup into an unbounded wait.
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            _process.Dispose();
        }
    }

    internal static SafeFileHandle CreateKillOnCloseJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new System.ComponentModel.Win32Exception(); }
        try
        {
            var info = new JobExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception();
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}

public sealed record AddonAsrCandidate(string Engine, string Text, bool Succeeded, long InferenceMilliseconds, string StopReason);
