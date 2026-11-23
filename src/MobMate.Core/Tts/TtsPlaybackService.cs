using Microsoft.Extensions.Logging;

namespace MobMate.Core.Tts;

/// <summary>
/// Plays PCM16 WAV audio via native waveOut bridge.
/// Handles WAV RIFF/WAVE parsing, PCM16 extraction, and singleton playback.
/// </summary>
public sealed class TtsPlaybackService : IDisposable
{
    private readonly IPcmPlaybackBackend _backend;
    private readonly ILogger? _logger;
    private readonly Func<System.Diagnostics.ProcessStartInfo>? _pulseTestProcess;
    private readonly object _lock = new();
    private PlaybackLease? _active;
    private bool _disposed;
    private long _lastPlaybackFinalDetectedAtMs;
    private long _lastFinalToPlaybackMs = -1;

    public TtsPlaybackService(string nativeExePath, ILogger? logger = null)
        : this(new NativePcmPlaybackBackend(nativeExePath), logger)
    {
    }

    internal TtsPlaybackService(IPcmPlaybackBackend backend, ILogger? logger = null,
        Func<System.Diagnostics.ProcessStartInfo>? pulseTestProcess = null)
    {
        _backend = backend;
        _logger = logger;
        _pulseTestProcess = pulseTestProcess;
    }

    private sealed class PlaybackLease(bool filler, ulong requestId, bool pulse)
    {
        internal readonly bool Filler = filler;
        internal readonly ulong RequestId = requestId;
        internal readonly bool Pulse = pulse;
        internal bool Started;
        internal bool Entered;
        internal bool StopRequested;
        internal string? PulseSink;
        internal System.Diagnostics.Process? Process;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PlaybackResult>? Stopping;
    }

    private PlaybackLease? Reserve(bool filler, ulong requestId = 0, bool pulse = false)
    {
        lock (_lock)
        {
            if (_disposed || _active != null) return null;
            return _active = new PlaybackLease(filler, requestId, pulse);
        }
    }

    private void Finish(PlaybackLease lease)
    {
        lock (_lock)
        {
            lease.Finished.TrySetResult();
            if (ReferenceEquals(_active, lease) && lease.Stopping == null) _active = null;
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _active is { } active && (!active.Filler || active.Started) && !_disposed;
        }
    }

    // Realtime echo/cooldown guards should ignore filler, while IsPlaying remains
    // the physical output status for existing status clients.
    internal bool IsBodyPlaying
    {
        get { lock (_lock) return _active is { Filler: false } && !_disposed; }
    }

    // Reserve before dispatching work so an OFF/body transition can cancel even
    // if the worker has not entered native Play. Zero means silent/unavailable.
    internal ulong ReserveFiller(string? pulseSinkForTest = null)
    {
        try
        {
            if (!SupportsFiller) return 0;
            var sink = pulseSinkForTest ?? (OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("PULSE_SINK") : null);
            var id = _backend.NextFillerId();
            var lease = Reserve(true, id, !string.IsNullOrWhiteSpace(sink));
            if (lease == null) return 0;
            lease.PulseSink = sink;
            return id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger?.LogDebug(ex, "[TTS] identified filler unavailable");
            return 0;
        }
    }

    internal bool SupportsFiller
    {
        get
        {
            try
            {
                if (!_backend.SupportsFiller()) return false;
                if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PULSE_SINK"))) return true;
                var player = ResolvePulsePlayerExe();
                return Path.IsPathRooted(player) && File.Exists(player) && Path.GetFileName(player) == "paplay";
            }
            catch (Exception) { return false; } // An optional capability failure cannot stop ASR/body.
        }
    }

    internal void LogFillerState(string state) => _logger?.LogDebug("[TTS] filler state={State}", state);

    internal static bool CanPrepareFillerWav(byte[] wav) => wav.Length <= 1024 * 1024 && ParseWavPcm16(wav, out _) is not null;

    internal PlaybackResult PlayReservedFiller(ulong requestId, byte[] wavBytes, int monitorVolumePercent, Action started, string? testTempDirectory = null)
    {
        PlaybackLease? lease;
        lock (_lock)
        {
            lease = _active is { Filler: true } active && active.RequestId == requestId ? active : null;
            if (lease != null) lease.Entered = true;
        }
        if (lease == null) return PlaybackResult.Error("Filler reservation expired");
        try
        {
            var pcm = ParseWavPcm16(wavBytes, out var error);
            if (pcm == null) return PlaybackResult.Error(error ?? "WAV parse failed");
            if (lease.Pulse) return PlayFillerViaPulse(lease, wavBytes, started, testTempDirectory);
            var result = _backend.PlayFiller(requestId, pcm.Value.samples, pcm.Value.sampleRate,
                pcm.Value.channels, monitorVolumePercent, () =>
                {
                    lock (_lock) lease.Started = true;
                    started();
                });
            return result.Success ? PlaybackResult.Ok() : PlaybackResult.Error(result.Message ?? "Filler playback failed");
        }
        catch (Exception ex) { return PlaybackResult.Error($"Filler playback exception: {ex.Message}"); }
        finally { Finish(lease); }
    }

    private PlaybackResult PlayFillerViaPulse(PlaybackLease lease, byte[] wavBytes, Action started, string? testTempDirectory)
    {
        var tempPath = Path.Combine(testTempDirectory ?? Path.GetTempPath(), $"mobmate-filler-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(tempPath, wavBytes);
            var player = ResolvePulsePlayerExe();
            if (Path.GetFileName(player) != "paplay" || string.IsNullOrWhiteSpace(lease.PulseSink))
                return PlaybackResult.Error("Pulse filler start acknowledgement unavailable");
            var psi = _pulseTestProcess?.Invoke() ?? new System.Diagnostics.ProcessStartInfo(player)
            {
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
            };
            if (_pulseTestProcess == null)
            {
                psi.ArgumentList.Add("--verbose");
                psi.ArgumentList.Add("--device=" + lease.PulseSink);
                psi.ArgumentList.Add(tempPath);
            }
            ApplyPulseToolEnvironment(psi);
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["PULSE_LOG_TARGET"] = "stderr";
            System.Diagnostics.Process? process;
            lock (_lock)
            {
                if (lease.StopRequested) return PlaybackResult.Error("Filler cancelled before Pulse start");
                process = System.Diagnostics.Process.Start(psi);
                lease.Process = process;
            }
            using (process)
            {
                if (process == null) return PlaybackResult.Error("paplay start failed");
                var connected = false;
                while (process.StandardError.ReadLine() is { } line)
                {
                    if (line.Contains($"Connected to device {lease.PulseSink} (", StringComparison.Ordinal)) connected = true;
                    if (connected && line.Contains("Stream started.", StringComparison.Ordinal) && !lease.Started)
                    {
                        lock (_lock) lease.Started = true;
                        started(); // Server stream-start ACK, never Process.Start/READY alone.
                    }
                    if (line.Contains("Stream moved to device ", StringComparison.Ordinal) &&
                        !line.Contains($"Stream moved to device {lease.PulseSink} (", StringComparison.Ordinal))
                    {
                        lock (_lock) lease.StopRequested = true;
                        if (!process.HasExited) process.Kill(); // Only our owned single child.
                    }
                }
                process.WaitForExit();
                return process.ExitCode == 0 && lease.Started
                    ? PlaybackResult.Ok() : PlaybackResult.Error("Pulse filler cancelled, failed, or missing start ACK");
            }
        }
        finally { try { File.Delete(tempPath); } catch (IOException) { } }
    }

    internal PlaybackResult CancelFillerAndWait()
    {
        PlaybackLease? lease;
        lock (_lock) lease = _active is { Filler: true } active ? active : null;
        return lease == null ? PlaybackResult.Ok() : StopLease(lease);
    }

    /// <summary>
    /// Parses a WAV RIFF/WAVE byte array and plays PCM16 audio via native waveOut.
    /// Only supports PCM format (WAVE_FORMAT_PCM, 16-bit).
    /// </summary>
    /// <param name="wavBytes">Complete WAV file bytes (RIFF/WAVE container)</param>
    /// <returns>Result with playback status</returns>
    public PlaybackResult PlayWav(byte[] wavBytes, int monitorVolumePercent = 0, long finalDetectedAtMs = 0)
    {
        if (_disposed)
            return PlaybackResult.Error("Service disposed");

        if (wavBytes == null || wavBytes.Length < 44)
            return PlaybackResult.Error("WAV too small");

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            var pulseSink = System.Environment.GetEnvironmentVariable("PULSE_SINK");
            if (!string.IsNullOrWhiteSpace(pulseSink))
            {
                var pulseResult = PlayWavViaPulse(wavBytes, pulseSink!, finalDetectedAtMs);
                if (pulseResult.Success)
                    return pulseResult;
            }
        }

        var pcm = ParseWavPcm16(wavBytes, out string? parseError);
        if (pcm == null)
            return PlaybackResult.Error(parseError ?? "WAV parse failed");

        return PlayPcm16(pcm.Value.samples, pcm.Value.sampleRate, pcm.Value.channels, monitorVolumePercent, finalDetectedAtMs);
    }

    public long LastPlaybackFinalDetectedAtMs => System.Threading.Interlocked.Read(ref _lastPlaybackFinalDetectedAtMs);

    public long LastFinalToPlaybackMs => System.Threading.Interlocked.Read(ref _lastFinalToPlaybackMs);

    internal PlaybackResult PlayWavViaPulse(byte[] wavBytes, string pulseSink, long finalDetectedAtMs = 0, string? testTempDirectory = null)
    {
        var lease = Reserve(false, pulse: true);
        if (lease == null) return PlaybackResult.Error("Already playing or disposed");

        var tempPath = System.IO.Path.Combine(testTempDirectory ?? System.IO.Path.GetTempPath(), $"mobmate-tts-{System.Guid.NewGuid():N}.wav");
        try
        {
            System.IO.File.WriteAllBytes(tempPath, wavBytes);
            var pulsePlayer = _pulseTestProcess == null ? ResolvePulsePlayerExe() : "paplay";
            var pulseArgs = IsPwPlay(pulsePlayer)
                ? $"--target={QuoteArg(pulseSink)} {QuoteArg(tempPath)}"
                : $"--device={QuoteArg(pulseSink)} {QuoteArg(tempPath)}";
            var psi = _pulseTestProcess?.Invoke() ?? new System.Diagnostics.ProcessStartInfo(pulsePlayer, pulseArgs)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ApplyPulseToolEnvironment(psi);
            System.Diagnostics.Process? proc;
            lock (_lock)
            {
                if (lease.StopRequested) return PlaybackResult.Ok();
                proc = System.Diagnostics.Process.Start(psi);
                lease.Process = proc;
            }
            using (proc)
            {
                if (proc == null)
                    return PlaybackResult.Error("paplay start failed");
                LogPlaybackStart(finalDetectedAtMs, "pulse");
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                // An intentional Stop is completion, not a Pulse failure that may
                // fall through to a second native playback of the same old WAV.
                lock (_lock) { if (lease.StopRequested) return PlaybackResult.Ok(); }
                if (proc.ExitCode == 0)
                    return PlaybackResult.Ok();
                return PlaybackResult.Error(string.IsNullOrWhiteSpace(stderr) ? $"paplay failed: {proc.ExitCode}" : stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            return PlaybackResult.Error($"Pulse playback exception: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
            Finish(lease);
        }
    }

    private static string ResolvePulsePlayerExe()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, "paplay");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        foreach (var candidate in new[] { "/usr/bin/paplay", "/bin/paplay", "/run/host/usr/bin/paplay" })
        {
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, "pw-play");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        foreach (var candidate in new[] { "/usr/bin/pw-play", "/bin/pw-play", "/run/host/usr/bin/pw-play" })
        {
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return "paplay";
    }

    private static bool IsPwPlay(string exePath)
        => string.Equals(System.IO.Path.GetFileName(exePath), "pw-play", StringComparison.Ordinal)
           || string.Equals(System.IO.Path.GetFileName(exePath), "pw-cat", StringComparison.Ordinal);

    private static void ApplyPulseToolEnvironment(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.Environment.Remove("LD_PRELOAD");
        var existingLdPath = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : string.Empty;
        psi.Environment["LD_LIBRARY_PATH"] = string.Join(':', new[]
        {
            "/run/host/usr/lib/x86_64-linux-gnu/pulseaudio",
            "/run/host/usr/lib/x86_64-linux-gnu",
            "/run/host/usr/lib64/pulseaudio",
            "/run/host/usr/lib64",
            "/run/host/lib64",
            "/usr/lib/x86_64-linux-gnu/pulseaudio",
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib64/pulseaudio",
            "/usr/lib64",
            existingLdPath
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string QuoteArg(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Plays raw PCM16 samples directly (no WAV container).
    /// </summary>
    public PlaybackResult PlayPcm16(short[] samples, int sampleRate, int channels, int monitorVolumePercent = 0, long finalDetectedAtMs = 0)
    {
        if (_disposed)
            return PlaybackResult.Error("Service disposed");

        var lease = Reserve(false);
        if (lease == null) return PlaybackResult.Error("Already playing or disposed");

        try
        {
            LogPlaybackStart(finalDetectedAtMs, "native");
            var result = _backend.Play(samples, sampleRate, channels, monitorVolumePercent);

            if (result.Success)
                return PlaybackResult.Ok();
            return PlaybackResult.Error(result.Message ?? "Playback failed");
        }
        catch (Exception ex)
        {
            return PlaybackResult.Error($"Playback exception: {ex.Message}");
        }
        finally { Finish(lease); }
    }

    private void LogPlaybackStart(long finalDetectedAtMs, string route)
    {
        var playbackStartAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var finalToPlaybackMs = finalDetectedAtMs > 0 ? playbackStartAtMs - finalDetectedAtMs : (long?)null;
        System.Threading.Interlocked.Exchange(ref _lastPlaybackFinalDetectedAtMs, finalDetectedAtMs);
        System.Threading.Interlocked.Exchange(ref _lastFinalToPlaybackMs, finalToPlaybackMs ?? -1);
        _logger?.LogInformation(
            "[TTS] playback start: finalDetectedAtMs={FinalDetectedAtMs} playbackStartAtMs={PlaybackStartAtMs} finalToPlaybackMs={FinalToPlaybackMs} route={Route}",
            finalDetectedAtMs, playbackStartAtMs, finalToPlaybackMs, route);
    }

    /// <summary>
    /// Stops ongoing playback.
    /// </summary>
    public PlaybackResult Stop()
    {
        if (_disposed)
            return PlaybackResult.Error("Service disposed");

        PlaybackLease? lease;
        lock (_lock) lease = _active;
        return lease == null ? PlaybackResult.Error("Not playing") : StopLease(lease);
    }

    private PlaybackResult StopLease(PlaybackLease lease)
    {
        TaskCompletionSource<PlaybackResult> stopping;
        Task<PlaybackResult>? existingStop;
        lock (_lock)
        {
            if (!ReferenceEquals(_active, lease)) return PlaybackResult.Ok();
            existingStop = lease.Stopping?.Task;
            stopping = lease.Stopping ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (existingStop != null) return existingStop.GetAwaiter().GetResult();
        PlaybackResult outcome;
        try
        {
            if (lease.Pulse)
            {
                System.Diagnostics.Process? process;
                lock (_lock)
                {
                    lease.StopRequested = true;
                    process = lease.Process;
                    if (lease.Filler && !lease.Entered) lease.Finished.TrySetResult();
                }
                // Only the child created by this lease is stopped. Never wait
                // for stderr, process exit or cleanup while holding _lock.
                try { if (process != null && !process.HasExited) process.Kill(); }
                catch (InvalidOperationException) when (lease.Finished.Task.IsCompleted) { }
                lease.Finished.Task.GetAwaiter().GetResult();
                outcome = PlaybackResult.Ok();
            }
            else
            {
                var result = lease.Filler ? _backend.CancelFiller(lease.RequestId) : _backend.Stop();
                outcome = result.Success ? PlaybackResult.Ok() : PlaybackResult.Error(result.Message ?? "Stop failed");
            }
        }
        catch (Exception ex) { outcome = PlaybackResult.Error($"Stop exception: {ex.Message}"); }
        lock (_lock)
        {
            // Do not let a concurrent legacy Stop hit a new body after the old
            // Play finishes. The slot remains reserved until Stop itself returns.
            if (ReferenceEquals(_active, lease) && (outcome.Success || lease.Finished.Task.IsCompleted))
                _active = null;
            lease.Stopping = null;
        }
        stopping.TrySetResult(outcome);
        return outcome;
    }

    /// <summary>
    /// Parses RIFF/WAVE container and extracts PCM16 data.
    /// Returns null on failure (error message via out param).
    /// </summary>
    internal static (short[] samples, int sampleRate, int channels)? ParseWavPcm16(
        byte[] data, out string? error)
    {
        error = null;

        // RIFF header: "RIFF" + size + "WAVE"
        if (data.Length < 12)
        {
            error = "Data too short for RIFF header";
            return null;
        }

        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
        {
            error = "Not a RIFF file";
            return null;
        }

        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
        {
            error = "Not a WAVE file";
            return null;
        }

        // Find 'fmt ' and 'data' chunks
        int fmtOffset = -1;
        int fmtSize = -1;
        int dataOffset = -1;
        int dataSize = -1;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            int chunkSize = ReadLittleEndian32(data, pos + 4);
            pos += 8;
            if (chunkSize < 0 || chunkSize > data.Length - pos)
            {
                error = "WAV chunk extends beyond file";
                return null;
            }

            if (chunkId == "fmt ")
            {
                fmtOffset = pos;
                fmtSize = chunkSize;
            }
            else if (chunkId == "data")
            {
                dataOffset = pos;
                dataSize = chunkSize;
                break;
            }

            pos += chunkSize;
            // Align to 4 bytes
            if (chunkSize % 2 != 0)
                pos++;
        }

        if (fmtOffset < 0 || fmtSize < 16)
        {
            error = "WAV fmt chunk not found or too small";
            return null;
        }

        if (dataOffset < 0 || dataSize <= 0)
        {
            error = "WAV data chunk not found or empty";
            return null;
        }

        // Parse fmt chunk
        int audioFormat = ReadLittleEndian16(data, fmtOffset);
        int channels = ReadLittleEndian16(data, fmtOffset + 2);
        int sampleRate = ReadLittleEndian32(data, fmtOffset + 4);
        // Skip avgBytesPerSec (4), blockAlign (2)
        int bitsPerSample = ReadLittleEndian16(data, fmtOffset + 14);

        if (audioFormat != 1)
        {
            error = $"Unsupported WAV format: {audioFormat} (only PCM supported)";
            return null;
        }

        if (bitsPerSample != 16)
        {
            error = $"Unsupported bit depth: {bitsPerSample} (only 16-bit PCM supported)";
            return null;
        }

        if (channels < 1 || channels > 2)
        {
            error = $"Unsupported channel count: {channels}";
            return null;
        }

        if (sampleRate <= 0)
        {
            error = $"Invalid sample rate: {sampleRate}";
            return null;
        }

        // Extract PCM16 samples
        int totalSamples = dataSize / 2;
        if (dataOffset + dataSize > data.Length)
        {
            error = "WAV data chunk extends beyond file";
            return null;
        }

        var samples = new short[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            int offset = dataOffset + (i * 2);
            samples[i] = (short)(data[offset] | (data[offset + 1] << 8));
        }

        return (samples, sampleRate, channels);
    }

    private static int ReadLittleEndian16(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8);
    }

    private static int ReadLittleEndian32(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) |
               (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    public void Dispose()
    {
        PlaybackLease? active;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
        }
        if (active != null) StopLease(active);
    }

    /// <summary>
    /// Result of a playback operation.
    /// </summary>
    public readonly struct PlaybackResult
    {
        public bool Success { get; }
        public string? Message { get; }

        private PlaybackResult(bool success, string? message)
        {
            Success = success;
            Message = message;
        }

        public static PlaybackResult Ok() => new(true, null);
        public static PlaybackResult Error(string message) => new(false, message);
    }
}

// The production adapter keeps the existing native output. The interface also
// permits deterministic no-device ownership tests without a new dependency.
internal interface IPcmPlaybackBackend
{
    bool SupportsFiller();
    ulong NextFillerId();
    Native.NativeLibraryResult Play(short[] samples, int sampleRate, int channels, int monitorVolumePercent);
    Native.NativeLibraryResult Stop();
    Native.NativeLibraryResult PlayFiller(ulong id, short[] samples, int sampleRate, int channels, int monitorVolumePercent, Action started);
    Native.NativeLibraryResult CancelFiller(ulong id);
}

internal sealed class NativePcmPlaybackBackend(string nativeExePath) : IPcmPlaybackBackend
{
    public bool SupportsFiller() => Native.NativeLibraryBridge.SupportsIdentifiedFiller(nativeExePath);
    public ulong NextFillerId() => Native.NativeLibraryBridge.NextFillerRequestId();
    public Native.NativeLibraryResult Play(short[] samples, int sampleRate, int channels, int monitorVolumePercent)
        => Native.NativeLibraryBridge.PlayPcm16(nativeExePath, samples, sampleRate, channels, monitorVolumePercent);
    public Native.NativeLibraryResult Stop() => Native.NativeLibraryBridge.StopPcm16(nativeExePath);
    public Native.NativeLibraryResult PlayFiller(ulong id, short[] samples, int sampleRate, int channels, int monitorVolumePercent, Action started)
        => Native.NativeLibraryBridge.PlayFillerPcm16(nativeExePath, id, samples, sampleRate, channels, monitorVolumePercent, started);
    public Native.NativeLibraryResult CancelFiller(ulong id) => Native.NativeLibraryBridge.CancelFillerAndWait(nativeExePath, id);
}
