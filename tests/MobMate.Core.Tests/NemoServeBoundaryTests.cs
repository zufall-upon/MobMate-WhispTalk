extern alias SteamCore;
using System.Diagnostics;
using System.Text;
using SteamCore::MobMate.Core.Asr;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class NemoServeBoundaryTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine("temp", "asr-candidates", "continuation", "boundary-tests", Guid.NewGuid().ToString("N")));

    private ProcessStartInfo StartInfo(string mode, int port)
    {
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "server.py");
        File.WriteAllText(script, Script, new UTF8Encoding(false));
        var python = Environment.GetEnvironmentVariable("MOBMATE_TEST_PYTHON");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            throw new InvalidOperationException("Set MOBMATE_TEST_PYTHON to an existing Python executable.");
        var start = new ProcessStartInfo(python);
        foreach (var arg in new[] { "-I", script, port.ToString(), mode }) start.ArgumentList.Add(arg);
        return start;
    }

    private Task<NemoSpeechWorker> Start(string mode = "ok")
    {
        var port = NemoSpeechWorker.ChoosePort();
        return NemoSpeechWorker.StartProcessAsync(StartInfo(mode, port), port, new string('a', 64), TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Fact]
    public async Task OneOwnedAuthenticatedProcessPreservesIdentityAndBoundsInvalidInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var worker = await Start();
        var pid = worker.ProcessId;
        Assert.Equal(pid, NemoSpeechWorker.ListenerOwner(worker.Port));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.TranscribeAsync(new byte[3], 16000, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.TranscribeAsync(new byte[NemoSpeechWorker.MaximumPcmBytes + 2], 16000, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ArgumentException>(() => worker.TranscribeContextAsync(new byte[640], 16000, "zh", "requested", TimeSpan.FromSeconds(2), CancellationToken.None));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => worker.TranscribeContextAsync(new byte[640], 16000, "ko", "requested", TimeSpan.FromSeconds(3), CancellationToken.None)));
        Assert.All(results, result => { Assert.Equal(AddonCatalog.NemotronId, result.Engine); Assert.Equal("ko", result.Text); });
        Assert.Equal(pid, worker.ProcessId); Assert.True(worker.HasSuccessfulInference);
        using var process = Process.GetProcessById(pid);
        await worker.DisposeAsync();
        Assert.True(process.HasExited); Assert.Equal(0, NemoSpeechWorker.ListenerOwner(worker.Port));
    }

    [Theory]
    [InlineData("unprotected")]
    [InlineData("ui")]
    public async Task StartupRejectsMissingAuthenticationOrExposedPlayground(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<InvalidDataException>(() => Start(mode));
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("malformed")]
    [InlineData("hang")]
    [InlineData("cancel")]
    public async Task ResponseFailureTimeoutAndCancellationReapOnlyTheOwnedChild(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var worker = await Start(mode == "cancel" ? "hang" : mode);
        using var child = Process.GetProcessById(worker.ProcessId);
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancel") cancellation.CancelAfter(150);
        var work = () => worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromMilliseconds(300), cancellation.Token);
        if (mode is "cancel" or "hang") await Assert.ThrowsAnyAsync<OperationCanceledException>(work);
        else if (mode == "malformed") await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(work);
        else await Assert.ThrowsAsync<InvalidDataException>(work);
        Assert.False(worker.Initialized); Assert.True(child.HasExited);
    }

    [Fact]
    public async Task ForeignListenerIsNeverReusedOrStopped()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var foreign = await Start();
        using var child = Process.GetProcessById(foreign.ProcessId);
        var port = foreign.Port;
        await Assert.ThrowsAsync<InvalidDataException>(() => NemoSpeechWorker.StartProcessAsync(StartInfo("sleep", port), port,
            new string('b', 64), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(child.HasExited); Assert.Equal(child.Id, NemoSpeechWorker.ListenerOwner(port));
        var result = await foreign.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(2));
        Assert.Equal("ja", result.Text);
    }

    private const string Script = """
        import http.server, json, os, sys, time
        port, mode = int(sys.argv[1]), sys.argv[2]
        if mode == 'sleep':
            time.sleep(30)
            sys.exit()
        key = os.environ['NEMO_SPEECH_HTTP_API_KEY']
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def send(self, code, body, content='application/json'):
                self.send_response(code)
                self.send_header('Content-Type',content)
                self.send_header('Content-Length',str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def auth(self):
                if mode == 'unprotected' or self.headers.get('Authorization') == 'Bearer '+key: return True
                self.send(401,b'{}')
                return False
            def do_GET(self):
                if self.path == '/ready': self.send(200,b'{"ready":true}')
                elif self.path == '/': self.send(200,b'<html>ui</html>') if mode == 'ui' else self.send(404,b'{}')
                elif self.auth(): self.send(200,b'{"data":[]}')
            def do_POST(self):
                if not self.auth(): return
                data = self.rfile.read(int(self.headers['Content-Length']))
                if mode == 'hang': time.sleep(30)
                elif mode == 'oversize': self.send(200,b'x'*70000)
                elif mode == 'malformed': self.send(200,b'{invalid')
                else:
                    assert b'RIFF' in data and b'WAVEfmt ' in data and b'response_format' in data
                    language = 'ko' if b'\r\nko\r\n' in data else 'ja'
                    self.send(200,json.dumps({'text':language}).encode())
        http.server.HTTPServer(('127.0.0.1',port),Handler).serve_forever()
        """;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
