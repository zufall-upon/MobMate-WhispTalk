extern alias SteamCore;
using SteamCore::MobMate.Core.Asr;
using System.Runtime.InteropServices;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class AddonManagerTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine("temp", "haas-addon", "manager-tests", Guid.NewGuid().ToString("N")));
    private readonly AddonEngine _engine;
    private readonly FakeInstaller _installer = new();
    private readonly List<FakeRecognizer> _workers = new();
    private string? _failModel;

    public AddonManagerTests()
    {
        var artifact = new AddonArtifact("https://fixture.invalid/file", 1, new string('a', 64));
        var runtime = new AddonRuntime("worker", "1", "test", artifact, "worker.exe", new[] { "worker.exe" }, 1024);
        AddonModel Model(string name) => new(name, name, name + ".gguf", "test", "1", "test", new[] { "ja" }, artifact);
        _engine = new AddonEngine("fixture", "fixture", "windows-x64", runtime, new[] { Model("first"), Model("second") });
    }

    [Fact]
    public async Task SetupUsesSelectedModelAndReloadDoesNotPretendInferenceIsReady()
    {
        await using (var manager = NewManager())
        {
            await manager.SetupAsync("fixture", "second");
            var state = manager.Snapshot();
            Assert.True(state.Installed);
            Assert.True(state.RuntimeLoaded);
            Assert.True(state.Ready);
            Assert.False(state.Enabled);
            Assert.False(state.JointRequested);
            Assert.EndsWith("second.gguf", _workers.Single().ModelPath);
            Assert.Equal("second", _installer.LastPreparedModel);
            await manager.SetEnabledAsync(true, true);
        }
        await using (var reloaded = NewManager())
        {
            await reloaded.LoadAsync();
            var state = reloaded.Snapshot();
            Assert.True(state.Installed);
            Assert.False(state.RuntimeLoaded);
            Assert.False(state.Ready);
            Assert.True(state.Enabled);
            Assert.True(state.JointRequested);
            await reloaded.ProbeAsync();
            Assert.True(reloaded.Snapshot().Ready);
            Assert.EndsWith("second.gguf", _workers.Last().ModelPath);
        }
    }

    [Fact]
    public async Task FailedModelSwitchPreservesSettingsAndRestoresOldWorker()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await manager.SetEnabledAsync(true, false);
        var settingsPath = Path.Combine(_root, "active.json");
        var before = File.ReadAllText(settingsPath);
        _failModel = "second.gguf";
        await Assert.ThrowsAsync<IOException>(() => manager.SetupAsync("fixture", "second"));
        var state = manager.Snapshot();
        Assert.Equal(before, File.ReadAllText(settingsPath));
        Assert.Equal("first", state.ModelId);
        Assert.True(state.Installed);
        Assert.True(state.Ready);
        Assert.True(state.Enabled);
        Assert.Equal("IOException", state.Error);
        Assert.True(_workers[0].Disposed);
        Assert.EndsWith("first.gguf", _workers.Last().ModelPath);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
    }

    [Fact]
    public async Task WorkerFailureKeepsExistingCandidateAndDoesNotRestartInSession()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await manager.SetEnabledAsync(true, false);
        _workers.Last().Fail = true;
        var existing = new AddonAsrCandidate("moonshine", "existing", true, 5, "complete");
        var result = await manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1));
        Assert.Same(existing, result.Selected);
        Assert.Null(result.Candidate);
        Assert.Equal("IOException", result.Error);
        Assert.False(manager.Snapshot().Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ProbeAsync());
        Assert.Single(_workers);
    }

    [Fact]
    public async Task WorkerDisposeFailureStillReturnsTheExistingCandidate()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await manager.SetEnabledAsync(true, false);
        _workers.Last().Fail = true;
        _workers.Last().DisposeFailure = true;
        var existing = new AddonAsrCandidate("moonshine", "existing", true, 5, "complete");
        var result = await manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1));
        Assert.Same(existing, result.Selected);
        Assert.Null(result.Candidate);
        Assert.False(manager.Snapshot().Ready);
        Assert.Contains("dispose_IOException", manager.Snapshot().Error);
    }

    [Fact]
    public async Task UnexpectedWorkerExitIsNotSilentlyRestarted()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await _workers.Single().DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ProbeAsync());
        Assert.False(manager.Snapshot().Ready);
        Assert.Single(_workers);
    }

    [Fact]
    public async Task InvalidExistingConfigurationIsNotReportedAsMissingOrModified()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "active.json");
        File.WriteAllText(path, "{invalid");
        await using var manager = NewManager();
        await manager.LoadAsync();
        var state = manager.Snapshot();
        Assert.True(state.Present);
        Assert.False(state.Installed);
        Assert.False(state.Ready);
        Assert.NotNull(state.Error);
        Assert.Equal("{invalid", File.ReadAllText(path));
    }

    [Fact]
    public async Task UnknownModelsAndCancelledPreparationDoNotReplaceConfiguration()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        var before = File.ReadAllText(Path.Combine(_root, "active.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SetupAsync("fixture", "missing"));
        _installer.CancelNext = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SetupAsync("fixture", "second"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "active.json")));
        Assert.Equal("first", manager.Snapshot().ModelId);
        Assert.True(manager.Snapshot().Ready);
        Assert.False(_workers.Single().Disposed);
    }

    [Fact]
    public async Task InstalledModelIsNotDownloadedAgain()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        var before = File.ReadAllText(Path.Combine(_root, "active.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SetupAsync("fixture", "first"));
        Assert.Single(_workers);
        Assert.False(_workers[0].Disposed);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "active.json")));
        Assert.True(manager.Snapshot().Ready);
    }

    [Fact]
    public async Task SuccessfulSwitchRemovesOnlyTheVerifiedPreviousPackage()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await manager.SetEnabledAsync(true, true);
        var old = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        await manager.SetupAsync("fixture", "second");
        Assert.False(Directory.Exists(old));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        Assert.Equal("second", manager.Snapshot().ModelId);
        Assert.True(manager.Snapshot().Ready);
        Assert.True(manager.Snapshot().Enabled);
        Assert.True(manager.Snapshot().JointRequested);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DifferentEngineDoesNotInheritOptInsOrRemoveThePreviousPackage(bool enabled, bool jointRequested)
    {
        string old;
        await using (var manager = NewManager())
        {
            await manager.SetupAsync("fixture", "first");
            await manager.SetEnabledAsync(enabled, jointRequested);
            old = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
            File.WriteAllText(Path.Combine(old, "first.gguf"), "keep previous model");

            // Identical model IDs in different engines must not transfer engine-specific consent.
            await manager.SetupAsync("another", "first");
            var state = manager.Snapshot();
            Assert.Equal("another", state.EngineId);
            Assert.Equal("first", state.ModelId);
            Assert.True(state.Installed);
            Assert.True(state.Ready);
            Assert.False(state.Enabled);
            Assert.False(state.JointRequested);
            Assert.Null(state.Error);
            Assert.True(_workers[0].Disposed);
            Assert.Equal("keep previous model", File.ReadAllText(Path.Combine(old, "first.gguf")));
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "packages")).Length);
            var existing = new AddonAsrCandidate("moonshine", "existing", true, 5, "complete");
            var result = await manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1));
            Assert.Same(existing, result.Selected);
            Assert.Null(result.Candidate);
        }
        await using (var reloaded = NewManager())
        {
            await reloaded.LoadAsync();
            var state = reloaded.Snapshot();
            Assert.Equal("another", state.EngineId);
            Assert.True(state.Installed);
            Assert.False(state.Ready);
            Assert.False(state.Enabled);
            Assert.False(state.JointRequested);
            Assert.Equal("keep previous model", File.ReadAllText(Path.Combine(old, "first.gguf")));
            await reloaded.ProbeAsync();
            Assert.True(reloaded.Snapshot().Ready);
            Assert.False(reloaded.Snapshot().Enabled);
            Assert.False(reloaded.Snapshot().JointRequested);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentEngineFailureOrCancellationPreservesPreviousConsentAndPackage(bool cancelPreparation)
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        await manager.SetEnabledAsync(true, true);
        var before = File.ReadAllText(Path.Combine(_root, "active.json"));
        var old = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        File.WriteAllText(Path.Combine(old, "first.gguf"), "keep previous model");
        if (cancelPreparation)
        {
            _installer.CancelNext = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SetupAsync("another", "second"));
        }
        else
        {
            _failModel = "second.gguf";
            await Assert.ThrowsAsync<IOException>(() => manager.SetupAsync("another", "second"));
        }
        var state = manager.Snapshot();
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "active.json")));
        Assert.Equal("fixture", state.EngineId);
        Assert.Equal("first", state.ModelId);
        Assert.True(state.Ready);
        Assert.True(state.Enabled);
        Assert.True(state.JointRequested);
        Assert.Equal(!cancelPreparation, _workers[0].Disposed);
        Assert.Equal(old, Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages"))));
        Assert.Equal("keep previous model", File.ReadAllText(Path.Combine(old, "first.gguf")));
    }

    [Fact]
    public async Task SuccessfulSwitchPreservesUnexpectedOldFilesAndReportsCleanupPending()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        var old = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        var unrelated = Path.Combine(old, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        await manager.SetupAsync("fixture", "second");
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal("second", manager.Snapshot().ModelId);
        Assert.True(manager.Snapshot().Ready);
        Assert.StartsWith("cleanup_pending:", manager.Snapshot().Error);
    }

    [Fact]
    public async Task SuccessfulSwitchDoesNotFollowOrDeleteAnUnexpectedLink()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        var old = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        var target = Path.Combine(_root, "unrelated.txt");
        var link = Path.Combine(old, "unexpected-link");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(link, target);
        await manager.SetupAsync("fixture", "second");
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Assert.True(manager.Snapshot().Ready);
        Assert.StartsWith("cleanup_pending:", manager.Snapshot().Error);
    }

    [Fact]
    public async Task NativeCallbackUsesEnabledWorkerAndFailsClosedAfterWorkerFailure()
    {
        await using var manager = NewManager(AddonCatalog.HaasId);
        await manager.SetupAsync(AddonCatalog.HaasId, "first");
        var callback = new AddonNativeTranscriber(manager, CancellationToken.None).Callback;
        var audio = Marshal.AllocHGlobal(320);
        var output = Marshal.AllocHGlobal(128);
        try
        {
            Marshal.Copy(new byte[320], 0, audio, 320);
            Assert.NotEqual(0, callback(audio, 160, 16000, output, 128, out _));
            await manager.SetEnabledAsync(true, false);
            Assert.Equal(0, callback(audio, 160, 16000, output, 128, out var elapsed));
            Assert.Equal("candidate", Marshal.PtrToStringUTF8(output));
            Assert.Equal(1, elapsed);
            _workers.Last().Fail = true;
            Assert.NotEqual(0, callback(audio, 160, 16000, output, 128, out _));
            Assert.False(manager.Snapshot().Ready);
            Assert.NotEqual(0, callback(audio, 160, 16000, output, 128, out _));
            Assert.Single(_workers);
        }
        finally { Marshal.FreeHGlobal(audio); Marshal.FreeHGlobal(output); }
    }

    [Fact]
    public async Task FailedReturnToARetainedEngineNeverDeletesTheReusedPackage()
    {
        await using var manager = NewManager();
        await manager.SetupAsync("fixture", "first");
        var retained = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
        File.WriteAllText(Path.Combine(retained, "first.gguf"), "keep");
        await manager.SetupAsync("another", "second");
        _installer.ReuseNext = retained; _failModel = "first.gguf";
        await Assert.ThrowsAsync<IOException>(() => manager.SetupAsync("fixture", "first"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(retained, "first.gguf")));
        Assert.Equal("another", manager.Snapshot().EngineId);
        Assert.True(manager.Snapshot().Ready);
    }

    [Fact]
    public async Task LegacyNativeCallbacksNeverAliasANewActiveEngineAsHaas()
    {
        await using var manager = NewManager(AddonCatalog.QwenId);
        await manager.SetupAsync(AddonCatalog.QwenId, "first");
        await manager.SetEnabledAsync(true, true);
        var bridge = new AddonNativeTranscriber(manager, CancellationToken.None);
        var audio = Marshal.AllocHGlobal(320); var output = Marshal.AllocHGlobal(128);
        try
        {
            Assert.NotEqual(0, bridge.Callback(audio, 160, 16000, output, 128, out _));
            Assert.NotEqual(0, bridge.JointCallback(audio, 160, 16000, output, 128, out _));
            Assert.True(manager.Snapshot().Ready);
        }
        finally { Marshal.FreeHGlobal(audio); Marshal.FreeHGlobal(output); }
    }

    [Fact]
    public async Task JointCallbackUsesBoundedDeadlineAndRetainsLeaseUntilWorkerActuallyStops()
    {
        await using var manager = NewManager(AddonCatalog.HaasId);
        await manager.SetupAsync(AddonCatalog.HaasId, "first");
        await manager.SetEnabledAsync(true, true);
        var operationGate = new AddonOperationGate();
        var callbacks = new AddonNativeTranscriber(manager, CancellationToken.None, operationGate);
        var audio = Marshal.AllocHGlobal(320);
        var output = Marshal.AllocHGlobal(128);
        try
        {
            Marshal.Copy(new byte[320], 0, audio, 320);
            Assert.Equal(0, callbacks.JointCallback(audio, 160, 16000, output, 128, out _));
            Assert.Equal(AddonNativeTranscriber.JointWaitLimit, _workers.Last().LastTimeout);
            Assert.Equal(0, callbacks.Callback(audio, 160, 16000, output, 128, out _));
            Assert.Equal(TimeSpan.FromMinutes(1), _workers.Last().LastTimeout);

            var pending = new TaskCompletionSource<AddonAsrCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
            _workers.Last().Pending = pending;
            var timed = Task.Run(() => callbacks.JointCallback(audio, 160, 16000, output, 128, out _));
            Assert.Equal(-2, await timed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(operationGate.TryEnterAddonOperation());
            pending.SetResult(new AddonAsrCandidate("fixture", "candidate", true, 1, "complete"));
            IDisposable? lease = null;
            for (var i = 0; i < 100 && lease is null; i++)
            {
                lease = operationGate.TryEnterAddonOperation();
                if (lease is null) await Task.Delay(10);
            }
            Assert.NotNull(lease);
            lease.Dispose();
        }
        finally { Marshal.FreeHGlobal(audio); Marshal.FreeHGlobal(output); }
    }

    [Fact]
    public void OperationGateReleasesReservationsOnDisposeAndExcludesSetup()
    {
        var gate = new AddonOperationGate();
        var work = Assert.IsAssignableFrom<IDisposable>(gate.TryEnterWork());
        var secondWork = Assert.IsAssignableFrom<IDisposable>(gate.TryEnterWork());
        Assert.Null(gate.TryEnterAddonOperation());
        work.Dispose();
        work.Dispose();
        Assert.Null(gate.TryEnterAddonOperation());
        secondWork.Dispose();
        using (Assert.IsAssignableFrom<IDisposable>(gate.TryEnterAddonOperation()))
        {
            Assert.Null(gate.TryEnterWork());
            Assert.Null(gate.TryEnterAddonOperation());
        }
        using var after = gate.TryEnterWork();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task OperationGateKeepsSetupBlockedUntilAsyncFailureFinishes()
    {
        var gate = new AddonOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(async () =>
        {
            using var lease = gate.TryEnterWork();
            Assert.NotNull(lease);
            entered.SetResult();
            await release.Task;
            throw new IOException("injected inference failure");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(gate.TryEnterAddonOperation());
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => work);
        using var operation = gate.TryEnterAddonOperation();
        Assert.NotNull(operation);
        Assert.Null(gate.TryEnterWork());
    }

    [Fact]
    public void OperationGateKeepsDispatchedWorkReservedAfterSessionStops()
    {
        var gate = new AddonOperationGate();
        var session = gate.TryEnterWork();
        var inFlight = gate.TryEnterWork();
        Assert.NotNull(session);
        Assert.NotNull(inFlight);
        session.Dispose();
        Assert.Null(gate.TryEnterAddonOperation());
        inFlight.Dispose();
        using var setup = gate.TryEnterAddonOperation();
        Assert.NotNull(setup);
    }

    private AddonManager NewManager(string primaryEngineId = "fixture") => new(_root, _installer, new[] { _engine with { Id = primaryEngineId }, _engine with { Id = "another" } }, (package, token) =>
    {
        if (Path.GetFileName(package.ModelFile) == _failModel) throw new IOException("probe failed");
        var worker = new FakeRecognizer(package.ModelFile);
        _workers.Add(worker);
        return Task.FromResult<IAddonRecognizer>(worker);
    });

    private sealed class FakeInstaller : IAddonPackageInstaller
    {
        public string? LastPreparedModel { get; private set; }
        public bool CancelNext { get; set; }
        public string? ReuseNext { get; set; }
        public Task<PreparedAddonPackage> PrepareAsync(string root, AddonEngine engine, AddonModel model, CancellationToken cancellationToken = default)
        {
            if (CancelNext) { CancelNext = false; throw new OperationCanceledException(); }
            LastPreparedModel = model.Id;
            var directory = ReuseNext ?? Path.Combine(root, "packages", Guid.NewGuid().ToString("N"));
            ReuseNext = null;
            Directory.CreateDirectory(directory);
            return Task.FromResult(new PreparedAddonPackage(directory, Path.Combine(directory, "runtime", engine.Runtime.Executable), Path.Combine(directory, model.FileName)));
        }
        public Task VerifyAsync(string root, AddonEngine engine, AddonModel model, PreparedAddonPackage package, CancellationToken cancellationToken = default)
        {
            Assert.Equal(model.FileName, Path.GetFileName(package.ModelFile));
            Assert.True(Directory.Exists(package.Directory));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecognizer(string modelPath) : IAddonRecognizer
    {
        public string ModelPath { get; } = modelPath;
        public bool Initialized => !Disposed;
        public bool HasSuccessfulInference { get; private set; }
        public bool IsAlive => !Disposed;
        public bool Disposed { get; private set; }
        public bool Fail { get; set; }
        public bool DisposeFailure { get; set; }
        public TimeSpan LastTimeout { get; private set; }
        public TaskCompletionSource<AddonAsrCandidate>? Pending { get; set; }
        public Task<AddonAsrCandidate> TranscribeAsync(byte[] pcm16, int sampleRate, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastTimeout = timeout;
            if (Pending is not null) return Pending.Task;
            if (Fail) throw new IOException("inference failed");
            HasSuccessfulInference = true;
            return Task.FromResult(new AddonAsrCandidate("fixture", "candidate", true, 1, "complete"));
        }
        public ValueTask DisposeAsync() { Disposed = true; return DisposeFailure ? ValueTask.FromException(new IOException("dispose failed")) : ValueTask.CompletedTask; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
