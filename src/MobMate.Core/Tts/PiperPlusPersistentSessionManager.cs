using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MobMate.Core.Tts;

/// <summary>
/// Piper-Plus CLI の --json-input モードによる永続セッション管理。
/// LRU pool、プロセス起動、JSONL 書き込み、WAV ファイル待機、クリーンアップを担当。
/// </summary>
public sealed class PiperPlusPersistentSessionManager : IAsyncDisposable
{
    private const int MaxActiveSessions = 3;
    private const int FileReadyTimeoutMs = 35_000;
    private const int FileReadyStableThreshold = 2;
    private const long FileReadyMinSize = 128;
    private const int PollIntervalMs = 25;

    private readonly Dictionary<SessionKey, LinkedListNode<ActiveSession>> _nodeMap = new();
    private readonly LinkedList<ActiveSession> _lru = new();
    private readonly object _poolLock = new();

    private bool _disposed;
    private Task? _disposeTask;

    public record SynthesisOptions(
        string LanguageTag,
        string LengthScaleArg,
        string SentenceSilenceArg,
        string NoiseScaleArg,
        string NoiseWArg,
        string SpeakerId = "0"
    );

    public record SynthesisResult(
        string WavPath,
        long WavBytes,
        bool Reused,
        long BootMs,
        long SynthWaitMs,
        string SessionKeyLabel
    );

    public async Task<SynthesisResult> SynthesizeAsync(
        string exePath,
        string modelPath,
        string? configPath,
        string text,
        SynthesisOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = new SessionKey(
            exePath,
            modelPath,
            configPath,
            options.LanguageTag,
            options.LengthScaleArg,
            options.SentenceSilenceArg,
            options.NoiseScaleArg,
            options.NoiseWArg,
            options.SpeakerId);

        ActiveSession session;
        lock (_poolLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PiperPlusPersistentSessionManager));
            session = EnsureSession(key);
        }

        long requestId = Interlocked.Increment(ref session.RequestCounter);
        string fileName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "utt_{0:D8}.wav", requestId);
        string outFile = Path.Combine(session.OutputDir, fileName);

        try { File.Delete(outFile); } catch { }

        try
        {
            ct.ThrowIfCancellationRequested();
            long waitStart = Stopwatch.GetTimestamp();
            SubmitJsonSynthesis(session, text, fileName);
            await WaitForFileReadyAsync(outFile, session, ct);
            ct.ThrowIfCancellationRequested();
            long synthWaitMs = (long)Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;

            long wavBytes = new FileInfo(outFile).Length;
            bool reused = session.ReuseCount > 0;
            session.ReuseCount++;

            return new SynthesisResult(outFile, wavBytes, reused, session.BootMs, synthWaitMs, key.LogLabel());
        }
        catch
        {
            // The child can still be writing; delete only after this session exits.
            lock (session.AbandonedFiles)
            {
                if (session.Stopped) { try { File.Delete(outFile); } catch { } }
                else session.AbandonedFiles.Add(outFile);
            }
            throw;
        }
    }

    private ActiveSession EnsureSession(SessionKey key)
    {
        if (_nodeMap.TryGetValue(key, out var node))
        {
            var session = node.Value;
            if (!session.Process.HasExited)
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return session;
            }

            _lru.Remove(node);
            _nodeMap.Remove(key);
            StopSessionBlocking(session);
        }

        var started = StartSession(key);
        var newNode = _lru.AddLast(started);
        _nodeMap[key] = newNode;
        TrimPool();
        return started;
    }

    private ActiveSession StartSession(SessionKey key)
    {
        // A key's display name is not unique, and different managers may use the
        // same key. Never reset a directory owned by another live process.
        string outputDir = Path.Combine(Path.GetTempPath(), "mobmate_piper_plus_session", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        string logFile = Path.Combine(outputDir, "_session.log");

        var psi = new ProcessStartInfo
        {
            FileName = key.ExePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(key.ExePath) ?? string.Empty,
            StandardInputEncoding = ResolveStdinEncoding()
        };

        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(key.ModelPath);
        if (!string.IsNullOrEmpty(key.ConfigPath))
        {
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(key.ConfigPath);
        }
        psi.ArgumentList.Add("--json-input");
        psi.ArgumentList.Add("--output-dir");
        psi.ArgumentList.Add(outputDir);
        if (!string.IsNullOrWhiteSpace(key.LanguageTag))
        {
            psi.ArgumentList.Add("--language");
            psi.ArgumentList.Add(key.LanguageTag);
        }
        psi.ArgumentList.Add("--speaker");
        psi.ArgumentList.Add(key.SpeakerId);
        psi.ArgumentList.Add("--length-scale");
        psi.ArgumentList.Add(key.LengthScaleArg);
        psi.ArgumentList.Add("--sentence-silence");
        psi.ArgumentList.Add(key.SentenceSilenceArg);
        psi.ArgumentList.Add("--noise-scale");
        psi.ArgumentList.Add(key.NoiseScaleArg);
        psi.ArgumentList.Add("--noise-w");
        psi.ArgumentList.Add(key.NoiseWArg);
        psi.ArgumentList.Add("--quiet");

        long bootStart = Stopwatch.GetTimestamp();
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Piper process");
            long bootMs = (long)Stopwatch.GetElapsedTime(bootStart).TotalMilliseconds;

            var logCts = new CancellationTokenSource();
            var logWriter = new StreamWriter(logFile, append: true, encoding: Encoding.UTF8) { AutoFlush = true };
            var session = new ActiveSession(key, process, outputDir, logFile, bootMs, logCts) { LogWriter = logWriter };
            _ = DrainStreamToFileAsync(process.StandardOutput, session, logCts.Token);
            _ = DrainStreamToFileAsync(process.StandardError, session, logCts.Token);
            return session;
        }
        catch
        {
            try { if (process != null && !process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } } catch { }
            process?.Dispose();
            try { File.Delete(logFile); Directory.Delete(outputDir); } catch { }
            throw;
        }
    }

    private static async Task DrainStreamToFileAsync(StreamReader reader, ActiveSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                lock (session.LogLock)
                {
                    session.LogWriter?.WriteLine(line);
                }
            }
        }
        catch
        {
            // ignore drain errors
        }
    }

    private static Encoding ResolveStdinEncoding()
    {
        try
        {
            var enc = Console.OutputEncoding;
            if (enc != null && enc.CodePage != 0)
                return enc;
        }
        catch { }
        return new UTF8Encoding(false);
    }

    private void SubmitJsonSynthesis(ActiveSession session, string text, string fileName)
    {
        var payload = new Dictionary<string, object>
        {
            ["text"] = text ?? "",
            ["output_file"] = fileName
        };
        string json = JsonSerializer.Serialize(payload);
        session.Process.StandardInput.WriteLine(json);
        session.Process.StandardInput.Flush();
    }

    private async Task WaitForFileReadyAsync(string outFile, ActiveSession session, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(FileReadyTimeoutMs);
        long lastSize = -1;
        int stableCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (session.Process.HasExited)
            {
                throw new IOException($"PiperPlus persistent session exited early: {TryReadSessionLogTail(session)}");
            }

            if (File.Exists(outFile))
            {
                long size = new FileInfo(outFile).Length;
                if (size > FileReadyMinSize)
                {
                    if (size == lastSize)
                    {
                        stableCount++;
                        if (stableCount >= FileReadyStableThreshold) return;
                    }
                    else
                    {
                        lastSize = size;
                        stableCount = 0;
                    }
                }
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        throw new IOException($"Timed out waiting persistent wav: {outFile} log={TryReadSessionLogTail(session)}");
    }

    private static string? TryReadSessionLogTail(ActiveSession session, int maxChars = 1000)
    {
        try
        {
            if (!File.Exists(session.LogFile)) return "(no session log)";
            string text = File.ReadAllText(session.LogFile).Trim();
            if (string.IsNullOrWhiteSpace(text)) return "(empty session log)";
            if (text.Length > maxChars) return text.Substring(text.Length - maxChars);
            return text;
        }
        catch (Exception ex)
        {
            return $"(failed to read session log: {ex.Message})";
        }
    }

    private void TrimPool()
    {
        while (_lru.Count > MaxActiveSessions)
        {
            var eldest = _lru.First;
            if (eldest == null) break;
            _lru.RemoveFirst();
            _nodeMap.Remove(eldest.Value.Key);
            StopSessionBlocking(eldest.Value);
        }
    }

    private void StopSessionBlocking(ActiveSession session)
    {
        StopSessionAsync(session).GetAwaiter().GetResult();
    }

    private async Task StopSessionAsync(ActiveSession session)
    {
        if (session == null) return;

        try { session.LogCts.Cancel(); } catch { }
        try
        {
            lock (session.LogLock)
            {
                session.LogWriter?.Close();
                session.LogWriter = null;
            }
        }
        catch { }
        try { session.Process.StandardInput.Close(); } catch { }

        try
        {
            if (!session.Process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try { await session.Process.WaitForExitAsync(cts.Token); } catch { }
            }
        }
        catch { }

        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try
        {
            if (!session.Process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                try { await session.Process.WaitForExitAsync(cts.Token); } catch { }
            }
        }
        catch { }
        bool exited = false;
        try { exited = session.Process.HasExited; } catch { }
        try { session.LogCts.Dispose(); } catch { }
        try { session.Process.Dispose(); } catch { }
        if (exited)
        {
            lock (session.AbandonedFiles)
            {
                session.Stopped = true;
                foreach (var file in session.AbandonedFiles) { try { File.Delete(file); } catch { } }
            }
            try { File.Delete(session.LogFile); } catch { }
            // Completed WAV paths belong to the caller and remain valid.
            try { Directory.Delete(session.OutputDir); } catch { }
        }
    }

    private async Task StopAllSessionsAsync()
    {
        List<ActiveSession> snapshot;
        lock (_poolLock)
        {
            snapshot = _lru.ToList();
            _lru.Clear();
            _nodeMap.Clear();
        }

        foreach (var session in snapshot)
        {
            await StopSessionAsync(session);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_poolLock)
        {
            if (_disposeTask == null)
            {
                _disposed = true;
                _disposeTask = StopAllSessionsAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private readonly record struct SessionKey(
        string ExePath,
        string ModelPath,
        string? ConfigPath,
        string LanguageTag,
        string LengthScaleArg,
        string SentenceSilenceArg,
        string NoiseScaleArg,
        string NoiseWArg,
        string SpeakerId
    )
    {
        public string LogLabel()
        {
            return $"{Path.GetFileNameWithoutExtension(ModelPath)}/{LanguageTag} len={LengthScaleArg} sil={SentenceSilenceArg} noise={NoiseScaleArg} noiseW={NoiseWArg}";
        }
    }

    private sealed class ActiveSession
    {
        public SessionKey Key { get; }
        public Process Process { get; }
        public string OutputDir { get; }
        public string LogFile { get; }
        public long BootMs { get; }
        public long RequestCounter;
        public int ReuseCount;
        public CancellationTokenSource LogCts { get; }
        public StreamWriter? LogWriter { get; set; }
        public object LogLock { get; } = new();
        public List<string> AbandonedFiles { get; } = new();
        public bool Stopped;

        public ActiveSession(SessionKey key, Process process, string outputDir, string logFile, long bootMs, CancellationTokenSource logCts)
        {
            Key = key;
            Process = process;
            OutputDir = outputDir;
            LogFile = logFile;
            BootMs = bootMs;
            LogCts = logCts;
        }
    }
}
