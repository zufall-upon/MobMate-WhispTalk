using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MobMate.Core.Asr;

/// <summary>One owned, authenticated loopback server. No model downloads, UI or remote listeners.</summary>
internal sealed class NemoSpeechWorker : IAddonRecognizer, IContextualAddonRecognizer, IConfirmedAddonStop
{
    internal const int MaximumPcmBytes = 16000 * 2 * 60;
    private const int MaximumResponseBytes = 65536;
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly HttpClient _http;
    private readonly SocketsHttpHandler _handler;
    private readonly int _port;
    private readonly string _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrTask;
    private int _closed;
    private bool _stopConfirmed;
    private bool _ready;
    private bool _inferred;
    private bool _keyLeaked;

    private NemoSpeechWorker(Process process, SafeFileHandle job, int port, string key)
    {
        _process = process; _job = job; _port = port; _key = key;
        _handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != port ||
                    process.HasExited || ListenerOwner(port) != process.Id)
                    throw new InvalidDataException("Refused non-owned Nemotron endpoint");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                    // Check the established connection before any HTTP header (including the key) is sent.
                    if (process.HasExited || ListenerOwner(port) != process.Id) throw new InvalidDataException("Nemotron endpoint ownership changed during connect");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            } };
        _http = new HttpClient(_handler)
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _stderrTask = DrainAsync();
    }

    public bool IsAlive => Volatile.Read(ref _closed) == 0 && !_process.HasExited;
    public bool Initialized => _ready && IsAlive;
    public bool HasSuccessfulInference => _inferred && Initialized;
    bool IConfirmedAddonStop.StopConfirmed => _stopConfirmed;
    internal int ProcessId => _process.Id;
    internal int Port => _port;

    public static Task<NemoSpeechWorker> StartAsync(PreparedAddonPackage package, TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Nemotron addon is Windows-only");
        if (!Path.IsPathFullyQualified(package.RuntimeExecutable) || !File.Exists(package.RuntimeExecutable) ||
            !Path.IsPathFullyQualified(package.ModelFile) || !File.Exists(package.ModelFile))
            throw new FileNotFoundException("Prepared Nemotron package is unavailable");
        var port = ChoosePort();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(package.RuntimeExecutable) { WorkingDirectory = Path.GetDirectoryName(package.RuntimeExecutable)! };
        foreach (var arg in new[] { "serve", "--asr-model", package.ModelFile, "--device", "vulkan:0", "--host", "127.0.0.1",
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-ui", "--threads", "1",
            "--max-upload-mb", "2", "--read-timeout", "30", "--write-timeout", "30", "--no-warmup" }) start.ArgumentList.Add(arg);
        // v0.3.0 serve's synthetic warmup asserts for these fixed multilingual weights.
        // This never establishes readiness by itself: AddonManager still requires a real inference probe.
        return StartProcessAsync(start, port, key, timeout, token);
    }

    internal static int ChoosePort()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        try { return ((IPEndPoint)reservation.LocalEndpoint).Port; }
        finally { reservation.Stop(); }
    }

    internal static async Task<NemoSpeechWorker> StartProcessAsync(ProcessStartInfo start, int port, string key,
        TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue || port is < 1 or > 65535 || key.Length < 32)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardError = true; start.RedirectStandardOutput = true;
        start.StandardErrorEncoding = new UTF8Encoding(false, false);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("NEMO_SPEECH_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["NEMO_SPEECH_HTTP_API_KEY"] = key;
        var process = new Process { StartInfo = start };
        NemoSpeechWorker? worker = null;
        SafeFileHandle? job = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Nemotron server did not start");
            job = SpeechWorker.CreateKillOnCloseJob(process);
            worker = new NemoSpeechWorker(process, job, port, key);
            // Native serve logs to stderr. Drain stdout too; it is not a protocol transport here.
            _ = DrainOutputAsync(process.StandardOutput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!worker.IsAlive) throw new InvalidDataException("Owned Nemotron server exited during startup");
                var owner = ListenerOwner(port);
                if (owner != 0 && owner != process.Id) throw new InvalidDataException("Loopback port belongs to another process; refused to connect");
                if (owner == process.Id)
                {
                    try
                    {
                        using var readyRequest = new HttpRequestMessage(HttpMethod.Get, "ready");
                        using var readyResponse = await worker._http.SendAsync(readyRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                        var json = await ReadBoundedAsync(readyResponse, deadline.Token).ConfigureAwait(false);
                        if (readyResponse.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True) break;
                        }
                    }
                    catch (HttpRequestException) { /* Owned listener can still be starting. */ }
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            worker.EnsureOwned();
            using (var anonymousClient = new HttpClient(worker._handler, disposeHandler: false)
                { BaseAddress = worker._http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan })
            {
                using var denied = await anonymousClient.GetAsync("v1/models", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                _ = await ReadBoundedAsync(denied, deadline.Token).ConfigureAwait(false);
                if (denied.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidDataException("Fixed Nemotron server did not enforce authentication");
            }
            using (var noUi = await worker._http.GetAsync("", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
            {
                var body = await ReadBoundedAsync(noUi, deadline.Token).ConfigureAwait(false);
                if (noUi.IsSuccessStatusCode && body.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Nemotron playground remains exposed");
            }
            using (var authenticated = await worker._http.GetAsync("v1/models", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
            {
                _ = await ReadBoundedAsync(authenticated, deadline.Token).ConfigureAwait(false);
                if (!authenticated.IsSuccessStatusCode) throw new InvalidDataException("Authenticated Nemotron readiness failed");
            }
            if (worker._keyLeaked) throw new InvalidDataException("Nemotron wrote authentication material to diagnostics");
            worker._ready = true;
            return worker;
        }
        catch (Exception error)
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                lock (worker._stderr) error.Data["workerStderr"] = worker._stderr.ToString().Replace(key, "[redacted]", StringComparison.Ordinal);
            }
            else
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                job?.Dispose(); process.Dispose();
            }
            throw;
        }
    }

    public Task<AddonAsrCandidate> TranscribeAsync(byte[] pcm16, int sampleRate, TimeSpan timeout, CancellationToken cancellationToken = default)
        => TranscribeContextAsync(pcm16, sampleRate, "ja", "requested", timeout, cancellationToken);

    public async Task<AddonAsrCandidate> TranscribeContextAsync(byte[] pcm16, int sampleRate, string language,
        string hintMode, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (pcm16.Length == 0 || pcm16.Length > MaximumPcmBytes || pcm16.Length % 2 != 0 || sampleRate != 16000)
            throw new ArgumentOutOfRangeException(nameof(pcm16), "Expected at most 60 seconds of mono 16kHz PCM16");
        if (language is not ("ja" or "en" or "ko") || hintMode != "requested") throw new ArgumentException("Unsupported Nemotron language context");
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Initialized) throw new InvalidOperationException("Nemotron server is unavailable");
            EnsureOwned();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var watch = Stopwatch.StartNew();
            try
            {
                using var body = new MultipartFormDataContent();
                var audio = new ByteArrayContent(Wave(pcm16));
                audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                body.Add(audio, "file", "input.wav");
                body.Add(new StringContent(language), "language");
                body.Add(new StringContent("json"), "response_format");
                using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = body };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var json = await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidDataException("Nemotron transcription HTTP failure: " + (int)response.StatusCode);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Nemotron returned no candidate text");
                EnsureOwned();
                _inferred = true;
                return new AddonAsrCandidate(AddonCatalog.NemotronId, text.GetString()!, true, watch.ElapsedMilliseconds, "complete");
            }
            catch { await DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }

    private void EnsureOwned()
    {
        if (!IsAlive || ListenerOwner(_port) != _process.Id) throw new InvalidDataException("Nemotron listener ownership changed");
    }

    private static byte[] Wave(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Nemotron response exceeded its bound");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var target = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (target.Length + count > MaximumResponseBytes) throw new InvalidDataException("Nemotron response exceeded its bound");
            target.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(target.ToArray());
    }

    private async Task DrainAsync()
    {
        var buffer = new char[4096]; int count;
        try
        {
            while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                lock (_stderr)
                {
                    if (_stderr.Length < 32768) _stderr.Append(buffer, 0, Math.Min(count, 32768 - _stderr.Length));
                    if (_stderr.ToString().Contains(_key, StringComparison.Ordinal)) _keyLeaked = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task DrainOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _http.Dispose();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            _stopConfirmed = _process.HasExited;
        }
        finally
        {
            _job.Dispose();
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
            _process.Dispose();
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    internal static int ListenerOwner(int port)
    {
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0) != 122 || size < 4) throw new IOException("Cannot observe loopback listener ownership");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) throw new IOException("Cannot observe loopback listener ownership");
            var rows = Marshal.ReadInt32(buffer);
            if (rows < 0 || rows > (size - 4) / 24) throw new InvalidDataException("Invalid TCP ownership table");
            for (var i = 0; i < rows; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row, 8) & 0xffff));
                // TCP_TABLE_OWNER_PID_LISTENER already filters listeners. Some Windows builds
                // leave dwState zero in this table; do not treat that as an absent listener.
                if (localPort == port)
                {
                    if ((uint)Marshal.ReadInt32(row, 4) != 0x0100007f) throw new InvalidDataException("Listener is not exclusively loopback");
                    return Marshal.ReadInt32(row, 20);
                }
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
