extern alias SteamCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SteamCore::MobMate.Core.Asr;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class SpeechWorkerTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine("temp", "haas-addon", "worker-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task ResidentWorkerChecksProtocolAndUsesOneProcessForMultipleRequests()
    {
        await using var worker = await Start("ok");
        Assert.True(worker.Initialized);
        Assert.False(worker.HasSuccessfulInference);
        var pid = worker.ProcessId;
        var first = await worker.TranscribeAsync(new byte[40000], 16000, TimeSpan.FromSeconds(5));
        var second = await worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5));
        Assert.Equal("2 chunks", first.Text);
        Assert.Equal("1 chunks", second.Text);
        Assert.Equal("haas_ja", first.Engine);
        Assert.True(first.Succeeded);
        Assert.True(worker.HasSuccessfulInference);
        Assert.Equal(pid, worker.ProcessId);
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("oversize")]
    [InlineData("error")]
    [InlineData("malformed")]
    public async Task ProtocolFailuresDisableAndReapTheWorker(string mode)
    {
        await using var worker = await Start(mode);
        var process = Process.GetProcessById(worker.ProcessId);
        var work = () => worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5));
        if (mode == "malformed") await Assert.ThrowsAnyAsync<JsonException>(work);
        else await Assert.ThrowsAsync<InvalidDataException>(work);
        Assert.False(worker.Initialized);
        Assert.False(worker.HasSuccessfulInference);
        Assert.True(process.HasExited);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5)));
        process.Dispose();
    }

    [Fact]
    public async Task TimedOutOrCancelledRequestDisablesAndReapsTheWorker()
    {
        foreach (var cancel in new[] { false, true })
        {
            await using var worker = await Start("hang");
            using var process = Process.GetProcessById(worker.ProcessId);
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.CancelAfter(250);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.TranscribeAsync(
                new byte[640], 16000, TimeSpan.FromMilliseconds(cancel ? 5000 : 250), cancellation.Token));
            Assert.False(worker.IsAlive);
            Assert.True(process.HasExited);
        }
    }

    [Fact]
    public async Task StartupRejectsWrongProtocolAndTimesOut()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Start("bad-ready"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start("startup-hang", TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task InvalidAudioDoesNotDisableAnOtherwiseHealthyWorker()
    {
        await using var worker = await Start("ok");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.TranscribeAsync(new byte[3], 16000, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.TranscribeAsync(new byte[640], 0, TimeSpan.FromSeconds(5)));
        var result = await worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5));
        Assert.Equal("1 chunks", result.Text);
    }

    [Theory]
    [InlineData("parakeet_ja", null, "absent")]
    [InlineData("qwen3_asr_ja", "ja", "ja")]
    [InlineData("qwen3_asr_ja", "en", "en")]
    [InlineData("qwen3_asr_ja", "auto", "auto")]
    public async Task EngineIdentityAndLanguageReachTheSameResidentRequest(string engine, string? language, string expected)
    {
        await using var worker = await Start("options", engine: engine, language: language);
        var pid = worker.ProcessId;
        var result = await worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5));
        Assert.Equal(engine, result.Engine);
        Assert.Equal(expected, result.Text);
        Assert.Contains("\"text\"", worker.LastResponseJson);
        await worker.TranscribeAsync(new byte[640], 16000, TimeSpan.FromSeconds(5));
        Assert.Equal(pid, worker.ProcessId);
    }

    [Fact]
    public async Task RowLanguageAndAutoHintStaySeparateOnOneResidentQwenWorker()
    {
        await using var worker = await Start("options", engine: AddonCatalog.QwenId, language: "ja");
        var context = (IContextualAddonRecognizer)worker;
        var pid = worker.ProcessId;
        foreach (var (language, mode, expected) in new[] { ("en", "requested", "en"), ("ja", "auto", "auto"), ("ja", "requested", "ja") })
        {
            var result = await context.TranscribeContextAsync(new byte[640], 16000, language, mode, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(expected, result.Text);
            Assert.Equal(AddonCatalog.QwenId, result.Engine);
            Assert.Equal(pid, worker.ProcessId);
        }
    }

    [Fact]
    public async Task FailureRetainsBoundedRuntimeDiagnosticsWithoutChangingTheExceptionContract()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Start("bad-ready"));
        var stderr = Assert.IsType<string>(error.Data["workerStderr"]);
        Assert.Contains("fixture stderr", stderr);
        Assert.InRange(stderr.Length, 1, 32768);
        Assert.True(Assert.IsType<bool>(error.Data["workerStderrTruncated"]));
    }

    [Theory]
    [InlineData("haas_ja", "ja")]
    [InlineData("parakeet_ja", "auto")]
    [InlineData("qwen3_asr_ja", "zh")]
    public async Task UnsupportedLanguageContextIsRejectedBeforeStartingAProcess(string engine, string language)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SpeechWorker.StartProcessAsync(
            new ProcessStartInfo("fixture-must-not-start"), TimeSpan.FromSeconds(1), engineId: engine, language: language));
    }

    private Task<SpeechWorker> Start(string mode, TimeSpan? timeout = null, string engine = AddonCatalog.HaasId, string? language = null)
    {
        Directory.CreateDirectory(_root);
        var scriptPath = Path.Combine(_root, "mock-worker.ps1");
        File.WriteAllText(scriptPath, Script, new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.Environment["MOCK_WORKER_MODE"] = mode;
        return SpeechWorker.StartProcessAsync(start, timeout ?? TimeSpan.FromSeconds(10), engineId: engine, language: language);
    }

    private const string Script = """
        $mode = $env:MOCK_WORKER_MODE
        [Console]::Error.WriteLine('fixture stderr')
        if ($mode -eq 'startup-hang') { Start-Sleep -Seconds 30; exit }
        if ($mode -eq 'bad-ready') { [Console]::Error.WriteLine(('d' * 40000)); [Console]::Out.WriteLine('{"type":"ready","protocol":3,"version":"0.10.1"}'); exit }
        [Console]::Out.WriteLine('{"type":"ready","protocol":4,"version":"0.10.1"}')
        $chunks = 0
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $request = $line | ConvertFrom-Json
            if ($request.type -eq 'chunk') {
                if ($request.seq -ne $chunks -or [Convert]::FromBase64String($request.pcm).Length -gt 32768) { exit 2 }
                $chunks++
            }
            if ($request.type -eq 'transcribe') {
                $id = $request.id
                if ($mode -eq 'hang') { Start-Sleep -Seconds 30 }
                elseif ($mode -eq 'bad-id') { [Console]::Out.WriteLine('{"type":"end","id":"wrong","text":"x","stop":"complete"}') }
                elseif ($mode -eq 'oversize') { [Console]::Out.WriteLine(('x' * 70000)) }
                elseif ($mode -eq 'error') { [Console]::Out.WriteLine('{"type":"error","id":"' + $id + '","error":{"code":"internal"}}') }
                elseif ($mode -eq 'malformed') { [Console]::Out.WriteLine('{oops') }
                elseif ($mode -eq 'options') {
                    $value = if ($request.PSObject.Properties.Name -contains 'language') { $request.language } else { 'absent' }
                    [Console]::Out.WriteLine('{"type":"end","id":"' + $id + '","text":"' + $value + '","stop":"complete"}')
                }
                else { [Console]::Out.WriteLine('{"type":"end","id":"' + $id + '","text":"' + $chunks + ' chunks","stop":"complete"}') }
                $chunks = 0
            }
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
