extern alias SteamCore;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using SteamCore::MobMate.Core.Tts;

namespace MobMate.Steam.Tests;

public sealed class KokoroDownloadTests : IDisposable
{
    private readonly List<string> _roots = new();
    private const string Archive = "QlpoOTFBWSZTWeFlqpwAAHP9gMqAAgBAAf9AACBnJ55ACAggAHUNTKaBo0NGIaD1PUEkoaGQAAAH3sCJCDORCEWdNMXQnlQIYGNzF9bhOYRI2A457RLKR2oQL0aigfPfoQC0v8RBcisavwgFa8wSQfi7kinChIcLLVTg";
    private const string CannotExec = "tar (child): bzip2: Cannot exec: No such file or directory\ntar (child): Error is not recoverable: exiting now\ntar: Child returned status 2\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalTarSuccessNeverCallsFallback(bool linux)
    {
        int calls = 0;
        await LinuxBzip2Fallback.ExtractAsync("archive", "root", default, linux,
            (a, d, compressed, ct) => { calls++; Assert.True(compressed); return Task.FromResult(new LinuxBzip2Fallback.TarResult(0, "")); },
            (a, output, ct) => throw new Exception("fallback should not run"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, 2, CannotExec)]
    [InlineData(true, 1, CannotExec)]
    [InlineData(true, 2, "tar: Error opening archive: Unrecognized archive format")]
    [InlineData(true, 2, "bzip2: Data integrity error when decompressing")]
    [InlineData(true, 2, "tar: archive contains tar (child): bzip2: Cannot exec: fake")]
    public async Task OnlyLinuxTarChildExecFailureCanFallback(bool linux, int exit, string error)
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LinuxBzip2Fallback.ExtractAsync("archive", "root", default, linux,
            (a, d, compressed, ct) => { calls++; return Task.FromResult(new LinuxBzip2Fallback.TarResult(exit, error)); },
            (a, output, ct) => throw new Exception("fallback should not run")));
        Assert.Equal($"tar exited {exit}: {error}", ex.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TarStartFailureDoesNotFallback()
    {
        var ex = await Assert.ThrowsAsync<Win32Exception>(() => LinuxBzip2Fallback.ExtractAsync("archive", "root", default, true,
            (a, d, compressed, ct) => throw new Win32Exception("tar missing"),
            (a, output, ct) => throw new Exception("fallback should not run")));
        Assert.Contains("tar missing", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackPassesTemporaryTarToTarAndCleansItAfterSuccessOrFailure(bool failExtract)
    {
        string? temporary = null;
        int calls = 0;
        async Task Extract() => await LinuxBzip2Fallback.ExtractAsync("archive", "root", default, true,
            (a, d, compressed, ct) =>
            {
                calls++;
                if (compressed) return Task.FromResult(new LinuxBzip2Fallback.TarResult(2, CannotExec));
                Assert.Equal(temporary, a);
                Assert.True(File.Exists(a));
                Assert.Equal("tar contents", File.ReadAllText(a));
                return Task.FromResult(new LinuxBzip2Fallback.TarResult(failExtract ? 2 : 0, "extract failed"));
            },
            (a, output, ct) =>
            {
                temporary = ((FileStream)output).Name;
                output.Write(Encoding.UTF8.GetBytes("tar contents"));
            });
        if (failExtract)
            Assert.Contains("extract failed", (await Assert.ThrowsAsync<InvalidOperationException>(Extract)).Message);
        else
            await Extract();
        Assert.Equal(2, calls);
        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackDecodeFailureOrCancellationCleansTemporaryTarAndSkipsExtraction(bool cancel)
    {
        string? temporary = null;
        int calls = 0;
        async Task Extract() => await LinuxBzip2Fallback.ExtractAsync("archive", "root", default, true,
            (a, d, compressed, ct) => { calls++; return Task.FromResult(new LinuxBzip2Fallback.TarResult(2, CannotExec)); },
            (a, output, ct) =>
            {
                temporary = ((FileStream)output).Name;
                output.WriteByte(1);
                if (cancel) throw new OperationCanceledException();
                throw new InvalidDataException("decode failed");
            });
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(Extract);
        else await Assert.ThrowsAsync<InvalidDataException>(Extract);
        Assert.Equal(1, calls);
        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void NativeLibraryMissingExportsAndVersionAreRejected()
    {
        using var output = new MemoryStream();
        Assert.Throws<DllNotFoundException>(() => LinuxBzip2Fallback.Decompress("unused", output, default, "libbz2-mobmate-does-not-exist"));
        var otherLibrary = OperatingSystem.IsWindows() ? "kernel32.dll" : "libc.so.6";
        Assert.Throws<EntryPointNotFoundException>(() => LinuxBzip2Fallback.Decompress("unused", output, default, otherLibrary));
        Assert.False(LinuxBzip2Fallback.IsCompatibleVersion(null));
        Assert.False(LinuxBzip2Fallback.IsCompatibleVersion("2.0.0"));
        Assert.True(LinuxBzip2Fallback.IsCompatibleVersion("1.0.8, 13-Jul-2019"));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task PublicManagerSuccessPreservesInstalledAndMetadataContract()
    {
        var root = NewRoot();
        using var server = new ArchiveServer(Convert.FromBase64String(Archive));
        using var manager = new KokoroModelDownloadManager();
        var entry = Entry(server.Url);
        Assert.Equal(RichDownloadStatus.Idle, manager.GetState(entry.Id).Status);
        Assert.False(KokoroModelDownloadManager.IsInstalled(entry.Id, root));
        manager.StartDownload(entry, root);
        var state = await Terminal(manager, entry.Id);
        Assert.Equal(new RichDownloadState(RichDownloadStatus.Done, 1, null), state);
        Assert.True(KokoroModelDownloadManager.IsInstalled(entry.Id, root));
        var model = Path.Combine(root, entry.Id);
        Assert.Equal("model fixture", File.ReadAllText(Path.Combine(model, "model.int8.onnx")));
        var properties = File.ReadAllText(Path.Combine(model, "mobmate-model.properties"));
        Assert.Contains("id=fixture", properties);
        Assert.Contains("language=multi", properties);
        Assert.Contains("speaker_count=1", properties);
        await AssertTarballCleaned(entry.Id);
    }

    [Fact]
    public async Task PublicManagerCorruptArchiveReportsErrorWithoutMetadataOrDamageToExistingFiles()
    {
        var root = NewRoot();
        var model = Path.Combine(root, "fixture");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "user.txt"), "keep");
        using var server = new ArchiveServer(Encoding.ASCII.GetBytes("invalid-bzip2"));
        using var manager = new KokoroModelDownloadManager();
        var entry = Entry(server.Url);
        manager.StartDownload(entry, root);
        var state = await Terminal(manager, entry.Id);
        Assert.Equal(RichDownloadStatus.Error, state.Status);
        Assert.Equal(0, state.Progress);
        Assert.Contains("tar exited", state.Error);
        Assert.False(KokoroModelDownloadManager.IsInstalled(entry.Id, root));
        Assert.False(File.Exists(Path.Combine(model, "mobmate-model.properties")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(model, "user.txt")));
        await AssertTarballCleaned(entry.Id);
    }

    [Theory]
    [InlineData("missing-library")]
    [InlineData("incompatible-library")]
    [InlineData("decode")]
    [InlineData("extract")]
    public async Task PublicManagerFallbackFailuresKeepExistingModelAndMetadata(string failure)
    {
        var root = NewRoot();
        var model = Path.Combine(root, "fixture");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "model.onnx"), "existing model");
        File.WriteAllText(Path.Combine(model, "mobmate-model.properties"), "existing metadata");
        string? temporary = null;
        using var server = new ArchiveServer(Convert.FromBase64String(Archive));
        using var manager = new KokoroModelDownloadManager((archive, destination, token) =>
            LinuxBzip2Fallback.ExtractAsync(archive, destination, token, true,
                (a, d, compressed, ct) => Task.FromResult(compressed
                    ? new LinuxBzip2Fallback.TarResult(2, CannotExec)
                    : new LinuxBzip2Fallback.TarResult(2, "simulated extract failure")),
                (a, output, ct) =>
                {
                    temporary = ((FileStream)output).Name;
                    if (failure == "missing-library") LinuxBzip2Fallback.Decompress(a, output, ct, "libbz2-mobmate-does-not-exist");
                    if (failure == "incompatible-library") throw new InvalidOperationException("Incompatible libbz2 version");
                    if (failure == "decode") throw new InvalidDataException("simulated decode failure");
                    output.WriteByte(1);
                }));
        var entry = Entry(server.Url);
        manager.StartDownload(entry, root);
        var state = await Terminal(manager, entry.Id);
        Assert.Equal(RichDownloadStatus.Error, state.Status);
        Assert.Equal(0, state.Progress);
        Assert.NotNull(state.Error);
        Assert.True(KokoroModelDownloadManager.IsInstalled(entry.Id, root));
        Assert.Equal("existing model", File.ReadAllText(Path.Combine(model, "model.onnx")));
        Assert.Equal("existing metadata", File.ReadAllText(Path.Combine(model, "mobmate-model.properties")));
        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
        await AssertTarballCleaned(entry.Id);
    }

    [Fact]
    public async Task PublicManagerCancellationAfterExtractionNeverWritesSuccessMetadata()
    {
        var root = NewRoot();
        using var server = new ArchiveServer(Convert.FromBase64String(Archive));
        using var cancellation = new CancellationTokenSource();
        using var manager = new KokoroModelDownloadManager((archive, destination, ct) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var entry = Entry(server.Url);
        manager.StartDownload(entry, root, cancellation.Token);
        var state = await Terminal(manager, entry.Id);
        Assert.Equal(RichDownloadStatus.Error, state.Status);
        Assert.NotNull(state.Error);
        Assert.False(KokoroModelDownloadManager.IsInstalled(entry.Id, root));
        Assert.False(File.Exists(Path.Combine(root, entry.Id, "mobmate-model.properties")));
        await AssertTarballCleaned(entry.Id);
    }

    [Theory]
    [InlineData("missing-voices")]
    [InlineData("empty-voices")]
    [InlineData("truncated-voices")]
    [InlineData("missing-tokens")]
    [InlineData("missing-model")]
    [InlineData("empty-phondata")]
    [InlineData("truncated-model")]
    [InlineData("locked-voices")]
    public async Task IncompleteKokoroIsRejectedBeforeNativeAndRemainsRemovable(string fault)
    {
        var root = NewRoot();
        var model = Path.Combine(root, "fixture");
        Directory.CreateDirectory(Path.Combine(model, "espeak-ng-data"));
        static void Sized(string path, long bytes) { using var f = File.Create(path); f.SetLength(bytes); }
        var onnx = Path.Combine(model, "model.int8.onnx");
        var voices = Path.Combine(model, "voices.bin");
        var tokens = Path.Combine(model, "tokens.txt");
        Sized(onnx, 1024 * 1024);
        Sized(voices, 510 * 256 * sizeof(float));
        File.WriteAllText(tokens, "token");
        foreach (var name in new[] { "phondata", "phonindex", "phontab" })
            File.WriteAllText(Path.Combine(model, "espeak-ng-data", name), "data");
        Assert.True(KokoroTtsService.IsModelDirValid(model));
        switch (fault)
        {
            case "missing-voices": File.Delete(voices); break;
            case "empty-voices": Sized(voices, 0); break;
            case "truncated-voices": Sized(voices, 510 * 256 * sizeof(float) - 4); break;
            case "missing-tokens": File.Delete(tokens); break;
            case "missing-model": File.Delete(onnx); break;
            case "empty-phondata": Sized(Path.Combine(model, "espeak-ng-data", "phondata"), 0); break;
            case "truncated-model": Sized(onnx, 128); break;
        }
        using (var locked = fault == "locked-voices" ? new FileStream(voices, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            Assert.False(KokoroTtsService.IsModelDirValid(model));
            Assert.True(KokoroModelDownloadManager.IsInstalled("fixture", root));
            using var service = new KokoroTtsService();
            service.SetConfig(model);
            Assert.False(service.IsModelInstalled);
            Assert.Contains("incomplete or unreadable", service.LastError);
            var result = await service.SynthesizeAsync("hello");
            Assert.False(result.Success);
            Assert.Equal(service.LastError, result.Error);
        }
        using var manager = new KokoroModelDownloadManager();
        manager.Delete("fixture", root);
        Assert.False(Directory.Exists(model));
        Assert.False(KokoroModelDownloadManager.IsInstalled("fixture", root));
    }

    [Fact]
    public void KnownV11LengthsDetectTruncationWithoutLoadingNative()
    {
        var model = Path.Combine(NewRoot(), "kokoro-int8-multi-lang-v1_1");
        Directory.CreateDirectory(Path.Combine(model, "espeak-ng-data"));
        static void Sized(string path, long bytes) { using var f = File.Create(path); f.SetLength(bytes); }
        var onnx = Path.Combine(model, "model.int8.onnx");
        var voices = Path.Combine(model, "voices.bin");
        Sized(onnx, 114203914);
        Sized(voices, 53790720);
        foreach (var name in new[] { "tokens.txt", "lexicon-gb-en.txt", "lexicon-us-en.txt", "lexicon-zh.txt",
                                      "espeak-ng-data/phondata", "espeak-ng-data/phonindex", "espeak-ng-data/phontab" })
            File.WriteAllText(Path.Combine(model, name), "data");
        Assert.True(KokoroTtsService.IsModelDirValid(model));
        Sized(voices, 53790716);
        Assert.False(KokoroTtsService.IsModelDirValid(model));
        Sized(voices, 53790720);
        Sized(onnx, 114203913);
        Assert.False(KokoroTtsService.IsModelDirValid(model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitIncompleteKokoroDoesNotFallBackToPrimary(bool primaryAvailable)
    {
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "model.onnx"), "incomplete");
        var primary = new KokoroFallbackRecordingPrimary(primaryAvailable);
        using var router = new TtsEngineRouter(primary, new TtsPlaybackService("unused"),
            kokoroService: new KokoroTtsService());
        router.SetConfig(new TtsConfig { Engine = "kokoro", KokoroModelId = "fixture", KokoroModelDir = root });
        var status = router.GetStatus();
        Assert.Equal("kokoro", status.EffectiveEngine);
        Assert.False(status.KokoroModelInstalled);
        Assert.Contains("incomplete or unreadable", status.KokoroError);
        var result = await router.SpeakAsync("hello");
        Assert.False(result.Success);
        Assert.Contains("incomplete or unreadable", result.Error);
        Assert.Equal(0, primary.SpeakCount);
    }

    private sealed class KokoroFallbackRecordingPrimary(bool available) : IPrimaryTtsService
    {
        public int SpeakCount { get; private set; }
        public bool IsAvailable => available;
        public string EngineName => "windows_sapi";
        public TtsResult SpeakAsync(string text, bool interrupt = true) { SpeakCount++; return TtsResult.Ok(); }
        public TtsResult StopAsync() => TtsResult.Ok();
        public void SetConfig(TtsConfig config) { }
        public TtsConfig GetConfig() => new();
        public TtsStatus GetStatus() => new() { Available = available };
        public void Dispose() { }
    }

    [Fact]
    public void WindowsConfigCopyPreservesKokoroSelectionAndOtherSettings()
    {
        using var service = new WindowsSapiTtsService();
        service.SetConfig(new TtsConfig { KokoroModelId = "fixture", KokoroModelDir = "model-dir", KokoroSpeakerId = 8,
            KokoroSpeed = 1.2f, MonitorVolumePercent = 30, Engine = "kokoro" });
        var config = service.GetConfig();
        Assert.Equal("fixture", config.KokoroModelId);
        Assert.Equal("model-dir", config.KokoroModelDir);
        Assert.Equal(8, config.KokoroSpeakerId);
        Assert.Equal(1.2f, config.KokoroSpeed);
        Assert.Equal(30, config.MonitorVolumePercent);
        Assert.Equal("kokoro", config.Engine);
    }

    private static RichVoiceEntry Entry(string url) => new("fixture", "Fixture", "kokoro", "multi", "multi", "high", "sherpa_onnx", "all", "test", "https://example.invalid", url, 1, 0, 1, [new(0, "speaker")]);
    private string NewRoot()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../temp/kokoro-download-regression", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }
    public void Dispose()
    {
        foreach (var root in _roots)
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    private static async Task<RichDownloadState> Terminal(KokoroModelDownloadManager manager, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var state = manager.GetState(id);
            if (state.Status is RichDownloadStatus.Done or RichDownloadStatus.Error) return state;
            await Task.Delay(25, timeout.Token);
        }
    }
    private static async Task AssertTarballCleaned(string id)
    {
        for (int i = 0; i < 100 && Directory.GetFiles(Path.GetTempPath(), $"kokoro-{id}-*.tar.bz2").Length != 0; i++)
            await Task.Delay(25);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"kokoro-{id}-*.tar.bz2"));
    }
    private sealed class ArchiveServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        internal string Url { get; }
        internal ArchiveServer(byte[] data)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/archive";
            _serve = Serve(data);
        }
        private async Task Serve(byte[] data)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[4096];
            int count = await stream.ReadAsync(buffer);
            Assert.Contains("GET /archive ", Encoding.ASCII.GetString(buffer, 0, count));
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(data);
        }
        public void Dispose() { _listener.Stop(); _serve.GetAwaiter().GetResult(); }
    }
}
