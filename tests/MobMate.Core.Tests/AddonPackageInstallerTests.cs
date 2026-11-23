extern alias SteamCore;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SteamCore::MobMate.Core.Asr;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class AddonPackageInstallerTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine("temp", "haas-addon", "tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void CatalogUsesPinnedHaasVariantsAndCompatibleRuntime()
    {
        var engine = Assert.Single(AddonCatalog.Engines, e => e.Id == AddonCatalog.HaasId);
        Assert.Equal("windows-x64", engine.Platform);
        Assert.Equal("0.10.1", engine.Runtime.Version);
        Assert.Equal(29548632, engine.Runtime.Artifact.Bytes);
        Assert.Equal(5, engine.Models.Count);
        Assert.Equal(5, engine.Models.Select(x => x.Id).Distinct().Count());
        foreach (var model in engine.Models)
        {
            Assert.Equal("6b965d5007ba780585de51549d57a8c1c0d67c7e", model.Revision);
            Assert.Contains(model.Revision, model.Artifact.Url);
            Assert.Equal("Apache-2.0", model.License);
            Assert.Equal("ja", Assert.Single(model.Languages));
            Assert.Equal(64, model.Artifact.Sha256.Length);
        }
    }

    [Fact]
    public async Task PrepareUsesSelectedModelWithoutChangingActiveConfiguration()
    {
        var fixture = Fixture();
        var active = SaveActive();
        using var http = new HttpClient(fixture.Handler);
        var installer = new AddonPackageInstaller(http, _ => long.MaxValue);
        var package = await installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[1]);

        Assert.Equal("old-ready", File.ReadAllText(active));
        Assert.Equal("worker", File.ReadAllText(package.RuntimeExecutable));
        Assert.Equal("second model", File.ReadAllText(package.ModelFile));
        Assert.Equal("second.gguf", Path.GetFileName(package.ModelFile));
        Assert.Equal("notice", File.ReadAllText(Path.Combine(package.Directory, "NOTICE-test.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, ".staging")));
        await installer.VerifyAsync(_root, fixture.Engine, fixture.Engine.Models[1], package);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("runtime")]
    [InlineData("extra")]
    [InlineData("archive")]
    [InlineData("notice")]
    public async Task ReverifyRejectsChangedPackageBeforeExecution(string changed)
    {
        var fixture = Fixture();
        using var http = new HttpClient(fixture.Handler);
        var installer = new AddonPackageInstaller(http, _ => long.MaxValue);
        var package = await installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[0]);
        await installer.VerifyAsync(_root, fixture.Engine, fixture.Engine.Models[0], package);
        switch (changed)
        {
            case "model": File.WriteAllText(package.ModelFile, "other model"); break;
            case "runtime": File.WriteAllText(package.RuntimeExecutable, "other!"); break;
            case "extra": File.WriteAllText(Path.Combine(Path.GetDirectoryName(package.RuntimeExecutable)!, "extra.dll"), "bad"); break;
            case "archive": File.WriteAllText(Path.Combine(package.Directory, "runtime.zip"), "bad"); break;
            case "notice": File.WriteAllText(Path.Combine(package.Directory, "NOTICE-test.txt"), "bad"); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.VerifyAsync(_root, fixture.Engine, fixture.Engine.Models[0], package));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("unsafe")]
    [InlineData("space")]
    [InlineData("cancel")]
    public async Task PrepareFailureDoesNotPublishOrReplaceActiveConfiguration(string failure)
    {
        var fixture = Fixture(failure == "unsafe");
        if (failure == "hash") fixture.Handler.Content["/model1"] = Encoding.UTF8.GetBytes("wrong model");
        var active = SaveActive();
        using var http = new HttpClient(fixture.Handler);
        var installer = new AddonPackageInstaller(http, _ => failure == "space" ? 0 : long.MaxValue);
        using var cts = new CancellationTokenSource();
        if (failure == "cancel") cts.Cancel();

        var work = () => installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[0], cts.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(work);
        else if (failure == "space") await Assert.ThrowsAsync<IOException>(work);
        else await Assert.ThrowsAsync<InvalidDataException>(work);
        Assert.Equal("old-ready", File.ReadAllText(active));
        var packages = Path.Combine(_root, "packages");
        if (Directory.Exists(packages)) Assert.Empty(Directory.GetDirectories(packages));
        var staging = Path.Combine(_root, ".staging");
        if (Directory.Exists(staging)) Assert.Empty(Directory.GetDirectories(staging));
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
        if (failure is "space" or "cancel") Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ConcurrentPreparationsAreSerializedAndBothRemainSeparate()
    {
        var fixture = Fixture();
        fixture.Handler.HoldFirst = true;
        using var http = new HttpClient(fixture.Handler);
        var installer = new AddonPackageInstaller(http, _ => long.MaxValue);
        var first = installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[0]);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[1]);
        try { Assert.Equal(1, fixture.Handler.Calls); }
        finally { fixture.Handler.Release.TrySetResult(); }
        var packages = await Task.WhenAll(first, second);
        Assert.NotEqual(packages[0].Directory, packages[1].Directory);
        Assert.Equal("first model", File.ReadAllText(packages[0].ModelFile));
        Assert.Equal("second model", File.ReadAllText(packages[1].ModelFile));
    }

    [Fact]
    public async Task PinnedHierarchicalRuntimeIsReusedAndUnexpectedMembersAreRejected()
    {
        var fixture = Fixture();
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            Write(zip, "release/bin/speech.exe", "worker");
            Write(zip, "release/bin/speech.dll", "library");
            Write(zip, "release/licenses/LICENSE", "notice");
        }
        var bytes = archive.ToArray(); fixture.Handler.Content["/runtime"] = bytes;
        var engine = fixture.Engine with { Runtime = fixture.Engine.Runtime with {
            Artifact = new AddonArtifact("https://fixture.invalid/runtime", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))),
            Executable = "release/bin/speech.exe", Files = new[] { "release/bin/speech.exe", "release/bin/speech.dll", "release/licenses/LICENSE" } } };
        using var http = new HttpClient(fixture.Handler);
        var installer = new AddonPackageInstaller(http, _ => long.MaxValue);
        var package = await installer.PrepareAsync(_root, engine, engine.Models[0]);
        await installer.VerifyAsync(_root, engine, engine.Models[0], package);
        var calls = fixture.Handler.Calls;
        var reused = await installer.PrepareAsync(_root, engine, engine.Models[0]);
        Assert.Equal(package, reused); Assert.Equal(calls, fixture.Handler.Calls);
        File.WriteAllText(Path.Combine(package.Directory, "runtime", "release", "extra.txt"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.VerifyAsync(_root, engine, engine.Models[0], package));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PrepareAsync(_root, engine, engine.Models[0]));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(package.Directory, "runtime", "release", "extra.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("release/../escape")]
    [InlineData("release//file")]
    [InlineData("release\\file")]
    [InlineData("C:/file")]
    public void HierarchicalRuntimePathsCannotEscapeTheirRoot(string path)
        => Assert.Throws<InvalidDataException>(() => AddonPackageInstaller.ValidateRuntimePath(path));

    [Fact]
    public async Task CancellationDuringDownloadCleansStagingAndKeepsActiveConfiguration()
    {
        var fixture = Fixture();
        fixture.Handler.HoldFirst = true;
        var active = SaveActive();
        using var http = new HttpClient(fixture.Handler);
        using var cancellation = new CancellationTokenSource();
        var installer = new AddonPackageInstaller(http, _ => long.MaxValue);
        var work = installer.PrepareAsync(_root, fixture.Engine, fixture.Engine.Models[0], cancellation.Token);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal("old-ready", File.ReadAllText(active));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, ".staging")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "packages")));
    }

    private string SaveActive()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "active.json");
        File.WriteAllText(path, "old-ready");
        return path;
    }

    private static (AddonEngine Engine, FixtureHandler Handler) Fixture(bool unsafeArchive = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(zip, "speech.exe", "worker");
            Write(zip, "speech.dll", "library");
            if (unsafeArchive) Write(zip, "../escape", "bad");
        }
        var runtime = output.ToArray();
        var one = Encoding.UTF8.GetBytes("first model");
        var two = Encoding.UTF8.GetBytes("second model");
        var notice = Encoding.UTF8.GetBytes("notice");
        var handler = new FixtureHandler(new Dictionary<string, byte[]>
        {
            ["/runtime"] = runtime, ["/model1"] = one, ["/model2"] = two, ["/notice"] = notice
        });
        AddonArtifact Artifact(string path, byte[] bytes) => new("https://fixture.invalid" + path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        AddonModel Model(string id, byte[] bytes, string url) => new(id, id, id + ".gguf", "fixture", "revision", "test", new[] { "ja" }, Artifact(url, bytes));
        var models = new[] { Model("first", one, "/model1"), Model("second", two, "/model2") };
        var engine = new AddonEngine("fixture", "fixture", "windows-x64", new AddonRuntime("worker", "1", "test",
            Artifact("/runtime", runtime), "speech.exe", new[] { "speech.exe", "speech.dll" }, 1024,
            Notices: new[] { new AddonNotice("NOTICE-test.txt", Artifact("/notice", notice)) }), models);
        return (engine, handler);
    }

    private static void Write(ZipArchive zip, string name, string value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixtureHandler(Dictionary<string, byte[]> content) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content { get; } = content;
        private int _calls;
        public int Calls => _calls;
        public bool HoldFirst { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _calls);
            if (HoldFirst && number == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content[request.RequestUri!.AbsolutePath]) };
        }
    }
}
