using MobMate.Core.Asr;
using MobMate.Core.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Diagnostics;

// Dependency-free offline fixture. The runner uses the existing friend
// assembly name MobMate.Core.Tests; no xunit/SDK/runtime/model download is needed.
internal static class AddonLanguageSmoke
{
    private static readonly List<string> Passed = new();
    private static string Root = "";
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        Passed.Add(name);
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is 2 or 3 && args[0] == "--pulse-owner-child")
            return await OnsetPlaybackSmoke.PulseChild(args[1], args.Length == 3 ? args[2] : "wait");
        if (args.Length == 1 && args[0] == "--filler-selection")
        {
            OnsetFillerSelectionSmoke.Run(Check);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--filler-playback")
        {
            await OnsetPlaybackSmoke.Run(Check);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--filler-router")
        {
            var scratch = Path.Combine("temp", "voice-filler", "router-" + Guid.NewGuid().ToString("N"));
            try { await OnsetRouterSmoke.Run(Check, scratch); }
            finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            return 0;
        }
        if (args.Length == 1 && args[0] == "--realtime-language-policy")
        {
            LanguageCapabilityBoundary();
            return 0;
        }
        if (args.Length == 3 && args[0] == "--filler-real-model")
        {
            await OnsetRouterSmoke.RunInstalledModel(Check, args[1], args[2]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--filler-moss-real")
        {
            await OnsetRouterSmoke.RunMossModel(args[1]);
            return 0;
        }
        if (args.Length is 3 or 4 && args[0] == "--piperplus-real")
        {
            await OnsetRouterSmoke.RunPiperPlusModel(Check, args[1], args[2], args.Length == 4 ? args[3] : null);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--onset-native")
            return await OnsetNativeSmoke.Run(args[1], args[2]);
#if DEBUG && QWEN_PRIVATE_VULKAN
        if (args.Length == 3 && args[0] == "--qwen-private-vulkan")
            return await QwenPrivateVulkanSmoke.Run(args[1], args[2]);
#endif
        if (args.Length == 3 && args[0] == "--qwen-real")
            return await QwenRealSmoke.Run(args[1], args[2]);
        if (args.Length != 1 || !Path.GetFullPath(args[0]).StartsWith(Path.GetFullPath("temp") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Exactly one fixture directory under repository temp/ is required");
        var output = Path.GetFullPath(args[0]);
        // Every invocation owns a new child; never delete/reuse a previous run
        // or let persisted opt-ins turn a fresh-install assertion into a flake.
        Root = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string? error = null;
        try
        {
            OnsetFillerSelectionSmoke.Run(Check);
            await OnsetPlaybackSmoke.Run(Check);
            await OnsetRouterSmoke.Run(Check, Path.Combine(Root, "onset-router"));
            await ConfigurationAndReuse();
            await LegacyMigration();
            await ObsoleteResultsAndStop();
            await Failures();
            await InvalidSettings();
            await RealProcessStopBoundary();
            await OperationDrain();
            await OutputBoundary();
            await ShutdownBoundary();
            LanguageCapabilityBoundary();
        }
        catch (Exception ex) { error = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(
            new { passed = error is null, checks = Passed, error }, new JsonSerializerOptions { WriteIndented = true }));
        if (error is null) Directory.Delete(Root, recursive: true); // Only this invocation's created child.
        return error is null ? 0 : 1;
    }

    private static async Task ConfigurationAndReuse()
    {
        await using var fixture = new Fixture("profiles");
        var manager = fixture.Manager;
        await manager.LoadAsync();
        await manager.InstallAsync("fixture", "first");
        Check("install does not configure, enable, grant consent or start", manager.Profiles.Count == 0 && fixture.Workers.Count == 0);
        await manager.ConfigureAsync("ja", "fixture", "first");
        Check("new language defaults OFF", !manager.Snapshot().Enabled && !manager.Snapshot().JointRequested);
        await manager.ProbeAsync();
        Check("probe establishes Ready, not Enabled/quality/Joint value", manager.Snapshot().Ready && !manager.Snapshot().Enabled &&
            manager.Describe().Quality == "not_evaluated" && manager.Describe().JointValue == "not_evaluated");
        await manager.SetEnabledAsync(true, true);
        await manager.ConfigureAsync("en", "fixture", "first");
        Check("JA consent never spreads to EN", !manager.Profiles["en"].Enabled && !manager.Profiles["en"].JointRequested && manager.Profiles["ja"].JointRequested);
        await manager.ApplyLanguageAsync("en", probe: true);
        Check("same model reuses contextual resident and sends EN hint", fixture.Workers.Count == 1 && fixture.Workers[0].Languages.Last() == "en");
        await manager.SetEnabledAsync(true, false);
        Check("saved enabled profile automatically selects individual engine", manager.ResolveRecognitionEngine("moonshine", true) == "fixture");
        Check("legacy explicit-route clients keep their existing engine", manager.ResolveRecognitionEngine("moonshine", false) == "moonshine");
        Check("editing JA does not falsify actual EN resident status", manager.Describe("ja").Resident is { Language: "en", Active: true } && !manager.Describe("ja").State.Ready);
        var existing = new AddonAsrCandidate("moonshine", "existing", true, 1, "complete");
        var noJoint = await manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1), joint: true);
        Check("EN without consent never executes Joint", noJoint.Candidate is null);
        await manager.SetEnabledAsync(true, true);
        Check("saved Joint preference automatically selects Joint without changing language consent", manager.ResolveRecognitionEngine("moonshine", true) == "joint_auto");
        var joint = await manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1), joint: true);
        Check("explicit EN consent permits EN candidate", joint.Candidate?.Engine == "fixture");
        await manager.ApplyLanguageAsync("ja");
        Check("JA to EN to JA applies saved opt-ins without manual engine switch", manager.Describe().JointEligible && fixture.Workers.Count == 1 && manager.Language == "ja");
        await manager.InstallAsync("fixture", "second");
        await manager.ConfigureAsync("ja", "fixture", "second");
        Check("same-engine variant retains only that language's opt-ins", manager.Profiles["ja"].Enabled && manager.Profiles["ja"].JointRequested && manager.Profiles["en"].ModelId == "first");
        await manager.ApplyLanguageAsync("ja");
        Check("variant switch retains packages referenced by other languages", fixture.Installer.Preparations == 2 && Directory.GetDirectories(Path.Combine(fixture.Path, "packages")).Length == 2);
        await manager.ConfigureAsync("ja", "another", "first");
        Check("different engine never inherits consent", !manager.Profiles["ja"].Enabled && !manager.Profiles["ja"].JointRequested);
        var snapshot = await File.ReadAllTextAsync(Path.Combine(fixture.Path, "language-profiles.json"));
        await manager.ApplyLanguageAsync("ko");
        Check("unconfigured language stops addon, keeps desired file unchanged", manager.Describe().Resident is null &&
            snapshot == await File.ReadAllTextAsync(Path.Combine(fixture.Path, "language-profiles.json")));
        await using var reload = fixture.NewManager();
        await reload.LoadAsync();
        Check("reload restores desired settings but never fakes Ready", reload.Profiles["en"].JointRequested && !reload.Snapshot().Ready);
    }

    private static async Task LegacyMigration()
    {
        await using var fixture = new Fixture("legacy");
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "packages", id));
        var legacy = "{\"EngineId\":\"fixture\",\"ModelId\":\"first\",\"PackageId\":\"" + id + "\",\"Enabled\":true,\"JointRequested\":true}";
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "active.json"), legacy);
        await fixture.Manager.LoadAsync();
        Check("legacy consent imports to JA only", fixture.Manager.Profiles.Count == 1 && fixture.Manager.Profiles["ja"].JointRequested);
        await fixture.Manager.ConfigureAsync("en", "fixture", "first");
        Check("legacy file preserved byte-for-byte after new save", legacy == await File.ReadAllTextAsync(Path.Combine(fixture.Path, "active.json")));
    }

    private static async Task ObsoleteResultsAndStop()
    {
        await using var fixture = new Fixture("generations");
        var manager = fixture.Manager;
        await manager.LoadAsync();
        await manager.InstallAsync("fixture", "first");
        foreach (var language in new[] { "ja", "en" })
        {
            await manager.ConfigureAsync(language, "fixture", "first");
            await manager.ApplyLanguageAsync(language, probe: true);
            await manager.SetEnabledAsync(true, true);
        }
        await manager.ApplyLanguageAsync("ja");
        var worker = fixture.Workers.Single();
        worker.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = new AddonAsrCandidate("none", "", false, -1, "unavailable");
        var recognition = manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1));
        await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var en = manager.ApplyLanguageAsync("en");
        var stop = manager.StopAsync();
        var ja = manager.ApplyLanguageAsync("ja");
        Check("switch waits for current utterance before touching worker", !worker.Disposed && !en.IsCompleted && fixture.Workers.Count == 1);
        worker.Pending.SetResult(new("fixture", "obsolete", true, 1, "complete"));
        var old = await recognition.WaitAsync(TimeSpan.FromSeconds(3));
        worker.Pending = null;
        await Task.WhenAll(en, stop, ja).WaitAsync(TimeSpan.FromSeconds(3));
        Check("old result invalidated before language becomes active", old.Candidate is null && old.Error == "obsolete_language_result");
        Check("latest language wins over earlier switch and Stop", manager.Language == "ja" && manager.Snapshot().Ready && !worker.Disposed);
    }

    private static async Task Failures()
    {
        await using var fixture = new Fixture("failures");
        var manager = fixture.Manager;
        await manager.LoadAsync();
        await manager.InstallAsync("fixture", "first");
        await manager.InstallAsync("another", "first");
        await manager.ConfigureAsync("ja", "fixture", "first");
        await manager.ProbeAsync();
        await manager.SetEnabledAsync(true, true);
        var first = fixture.Workers.Single();
        await manager.ConfigureAsync("en", "another", "first");
        await manager.ApplyLanguageAsync("en", probe: true);
        await manager.SetEnabledAsync(true, true);
        await manager.ApplyLanguageAsync("ja");
        fixture.FailEngine = "another";
        await manager.ApplyLanguageAsync("en");
        Check("load failure preserves desired EN configuration, not old JA active", manager.Profiles["en"].Enabled &&
            manager.Profiles["en"].EngineId == "another" && !manager.Snapshot().Ready && manager.Describe().Resident is null);
        Check("old worker stopped before another starts", first.Disposed && fixture.MaximumLive == 1);
        fixture.FailEngine = null;
        await manager.ApplyLanguageAsync("ja");
        var stuck = fixture.Workers.Last();
        stuck.ThrowOnDispose = true;
        var starts = fixture.Workers.Count;
        await manager.ApplyLanguageAsync("en");
        Check("uncertain stop never overlaps another worker", fixture.Workers.Count == starts && !manager.Snapshot().Ready && manager.Snapshot().Error?.StartsWith("stop_failed:") == true);
        stuck.ThrowOnDispose = false;
        stuck.UnconfirmedStop = true;
        await manager.ApplyLanguageAsync("en");
        Check("closed adapter flag is not proof of OS process exit", fixture.Workers.Count == starts && !manager.Snapshot().Ready);
        stuck.UnconfirmedStop = false;
        await manager.ApplyLanguageAsync("en");
        Check("explicit retry after confirmed stop can recover", manager.Snapshot().Ready && fixture.MaximumLive == 1);
    }

    private static async Task InvalidSettings()
    {
        foreach (var invalidLanguage in new[] { "auto", "-ja", "ja--jp", "j", "" })
        {
            try { AddonLanguageManager.NormalizeLanguage(invalidLanguage); throw new Exception("invalid language accepted"); }
            catch (ArgumentException) { }
        }
        Check("language commands require a well-formed explicit language", AddonLanguageManager.NormalizeLanguage("EN") == "en");
        await using var fixture = new Fixture("invalid");
        var path = Path.Combine(fixture.Path, "language-profiles.json");
        const string invalid = "{\"SchemaVersion\":99,\"Profiles\":{},\"Installed\":[]}";
        await File.WriteAllTextAsync(path, invalid);
        await fixture.Manager.LoadAsync();
        try
        {
            await fixture.Manager.ConfigureAsync("ja", "fixture", "first");
            throw new Exception("invalid schema was overwritten");
        }
        catch (InvalidOperationException) { }
        Check("unknown schema is preserved and never overwritten", invalid == await File.ReadAllTextAsync(path));
    }

    private static async Task RealProcessStopBoundary()
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = new ProcessStartInfo("/usr/bin/python3");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; print('{\"type\":\"ready\",\"protocol\":4,\"version\":\"0.10.1\"}',flush=True); sys.stdin.read()");
        await using var worker = await SpeechWorker.StartProcessAsync(start, TimeSpan.FromSeconds(3));
        var pid = worker.ProcessId;
        Check("owned protocol fixture starts without model inference", worker.Initialized && !worker.HasSuccessfulInference);
        await worker.DisposeAsync();
        var exited = false;
        try { using var process = Process.GetProcessById(pid); exited = process.HasExited; }
        catch (ArgumentException) { exited = true; }
        Check("SpeechWorker confirms actual child process exit", ((IConfirmedAddonStop)worker).StopConfirmed && exited);
    }

    private static async Task OperationDrain()
    {
        var gate = new AddonOperationGate();
        var work = gate.TryEnterWork()!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pending = gate.EnterAddonOperationAsync(deadline.Token);
        Check("automatic switch waits for dispatched inference lease", !pending.IsCompleted);
        Check("new ASR work cannot starve a pending language switch", gate.TryEnterWork() is null);
        work.Dispose();
        using (await pending) Check("drained operation still excludes new ASR work", gate.TryEnterWork() is null);
        using var second = gate.TryEnterWork();
        using var cancelled = new CancellationTokenSource();
        var cancellation = gate.EnterAddonOperationAsync(cancelled.Token);
        cancelled.Cancel();
        try { await cancellation; throw new Exception("cancelled drain acquired a reservation"); }
        catch (OperationCanceledException) { }
        second!.Dispose();
        using var next = gate.TryEnterAddonOperation();
        Check("cancelled drain never leaks an addon reservation", next is not null);
    }

    private static async Task OutputBoundary()
    {
        using var epoch = new RealtimeOutputEpoch();
        var old = epoch.Begin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        var late = Task.Run(async () =>
        {
            entered.SetResult();
            await release.Task;
            return epoch.Commit(old, () => ++effects, () => -1);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopped = epoch.Invalidate();
        var current = epoch.Begin();
        release.SetResult();
        Check("late final cannot learn, display or enqueue TTS after language switch", await late.WaitAsync(TimeSpan.FromSeconds(3)) == -1 && effects == 0 && old.Cancellation.IsCancellationRequested);
        Check("old Stop cannot activate output over a newer session", !epoch.Activate(stopped));
        Check("current final still reaches output boundary", epoch.Commit(current, () => ++effects, () => -1) == 1);

        var path = Path.Combine(Root, "pending-output");
        Directory.CreateDirectory(path);
        using var pending = new PendingSpeechService(path, NullLogger.Instance);
        pending.ApplyConfig(new PendingSpeechConfig { Mode = "wait", WaitSeconds = 10 });
        await pending.EnqueueOrSpeakAsync("old recognition", (_, _) => Task.CompletedTask, current.Cancellation, current.Cancellation);
        await pending.EnqueueOrSpeakAsync("manual text", (_, _) => Task.CompletedTask, CancellationToken.None);
        epoch.Invalidate();
        Check("switch prunes only recognition-origin pending speech, not manual text", pending.GetStatus().Items.Select(i => i.Text).SequenceEqual(new[] { "manual text" }));
        await pending.ApproveFirstAsync((_, _) => { effects++; return Task.CompletedTask; }, CancellationToken.None);
        Check("unrelated manual pending speech remains usable", effects == 2);

        using var origin = new CancellationTokenSource();
        await pending.EnqueueOrSpeakAsync("first", (_, _) => Task.CompletedTask, CancellationToken.None);
        await pending.EnqueueOrSpeakAsync("obsolete waiting", (_, _) => Task.CompletedTask, origin.Token, origin.Token);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = pending.ApproveFirstAsync(async (_, _) => { firstEntered.SetResult(); await finishFirst.Task; }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = pending.ApproveFirstAsync((_, _) => { effects += 100; return Task.CompletedTask; }, CancellationToken.None);
        origin.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        finishFirst.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Check("approved old speech waiting for serialization never dispatches after cancellation", effects == 2);
        await pending.EnqueueOrSpeakAsync("new", (_, _) => Task.CompletedTask, CancellationToken.None);
        await pending.ApproveFirstAsync((_, _) => { effects++; return Task.CompletedTask; }, CancellationToken.None);
        Check("canceled old speech does not leak the speech gate", effects == 3);
    }

    private static async Task ShutdownBoundary()
    {
        await using var fixture = new Fixture("shutdown-boundary");
        var manager = fixture.Manager;
        await manager.LoadAsync();
        await manager.InstallAsync("fixture", "first");
        await manager.ConfigureAsync("ja", "fixture", "first");
        await manager.ProbeAsync();
        await manager.SetEnabledAsync(true, true);
        var worker = fixture.Workers.Single();
        worker.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = new AddonAsrCandidate("none", "", false, -1, "unavailable");
        var recognition = manager.RecognizeAsync(new byte[320], 16000, existing, TimeSpan.FromSeconds(1));
        await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queuedSwitch = manager.ApplyLanguageAsync("ja");
        var shutdown = manager.DisposeAsync().AsTask();
        var concurrentShutdown = manager.DisposeAsync().AsTask();
        var joinedDrain = !concurrentShutdown.IsCompleted;
        var advertisedReady = manager.Snapshot().Ready;
        var lateSwitch = manager.ApplyLanguageAsync("en");
        worker.Pending.SetResult(new("fixture", "obsolete", true, 1, "complete"));
        var old = await recognition.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(queuedSwitch, shutdown, concurrentShutdown).WaitAsync(TimeSpan.FromSeconds(3));
        var rejected = false;
        try { await lateSwitch.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (ObjectDisposedException) { rejected = true; }
        Check("shutdown invalidates Ready before draining an in-flight utterance", !advertisedReady);
        Check("concurrent disposal joins the same unfinished worker drain", joinedDrain);
        Check("shutdown rejects a later language switch instead of losing the Stop generation", rejected);
        Check("shutdown owns and stops the resident despite queued switches", old.Candidate is null && fixture.Workers.Count == 1 && worker.Disposed && manager.Describe().Resident is null);
    }

    private static void LanguageCapabilityBoundary()
    {
        var root = Path.Combine("temp", "jointauto-language-policy", Guid.NewGuid().ToString("N"));
        var ja = Path.Combine(root, "ja", "quantized", "base-ja");
        var en = Path.Combine(root, "en", "quantized");
        Directory.CreateDirectory(ja); Directory.CreateDirectory(en);
        foreach (var name in new[] { "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort" }) File.WriteAllText(Path.Combine(ja, name), "");
        foreach (var name in new[] { "tokenizer.bin", "streaming_config.json", "encoder.ort", "adapter.ort", "cross_kv.ort", "decoder_kv.ort", "frontend.ort" }) File.WriteAllText(Path.Combine(en, name), "");
        var catalog = RealtimeLanguagePolicy.Catalog(whisperAvailable: true, root);
        Check("Linux catalog keeps its existing engine keys and uses detected model layouts",
            catalog.Keys.SequenceEqual(new[] { "whisper", "moonshine", "joint_auto", "safe_auto", "sense_voice", "paraformer_zh" }) &&
            catalog["moonshine"].Select(x => x.Code).SequenceEqual(new[] { "ja", "en" }) &&
            catalog["joint_auto"].Select(x => x.Code).SequenceEqual(new[] { "ja", "en" }));
        Check("Linux Whisper catalog matches the Windows recognition catalog and start guard",
            catalog["whisper"].Select(x => x.Code).SequenceEqual(WhisperLanguageCatalog.OrderedCodes()) &&
            new[] { "es", "fr", "de" }.All(code => !RealtimeLanguagePolicy.RejectsExplicit("whisper", code, true, root)) &&
            RealtimeLanguagePolicy.RejectsExplicit("whisper", "xx", true, root));
        Check("explicit KO on Moonshine is rejected while EN and JA retain their existing gate",
            RealtimeLanguagePolicy.RejectsExplicit("moonshine", "ko", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("moonshine", "en", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("moonshine", "ja", true, root));
        Check("Linux start guard and published catalog use the same capability matrix",
            catalog.All(pair => pair.Value.All(option => !RealtimeLanguagePolicy.RejectsExplicit(pair.Key, option.Code, true, root))) &&
            RealtimeLanguagePolicy.RejectsExplicit("paraformer_zh", "en", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("paraformer_zh", "zh", true, root));
        Check("unknown engine, omitted language and auto retain existing default delegation",
            !RealtimeLanguagePolicy.RejectsExplicit("legacy_unknown", "ko", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit(null, "ko", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("moonshine", null, true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("moonshine", "", true, root) &&
            !RealtimeLanguagePolicy.RejectsExplicit("moonshine", "auto", true, root));
        Check("Whisper capability absence is not manufactured into readiness",
            RealtimeLanguagePolicy.Catalog(false, root)["whisper"].Length == 0 &&
            RealtimeLanguagePolicy.RejectsExplicit("whisper", "ko", false, root));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Path { get; }
        public FakeInstaller Installer { get; } = new();
        public List<Worker> Workers { get; } = new();
        public AddonLanguageManager Manager { get; }
        public string? FailEngine { get; set; }
        public int MaximumLive { get; private set; }
        private readonly AddonEngine[] _catalog;
        public Fixture(string name)
        {
            Path = System.IO.Path.Combine(Root, name);
            Directory.CreateDirectory(Path);
            var artifact = new AddonArtifact("https://fixture.invalid/model", 1, new string('a', 64));
            var runtime = new AddonRuntime("fixture", "1", "test", artifact, "worker.exe", new[] { "worker.exe" }, 1024);
            AddonModel Model(string id) => new(id, id, id + ".gguf", "test", "1", "test", new[] { "ja", "en" }, artifact);
            _catalog = new[] { new AddonEngine("fixture", "fixture", "windows-x64", runtime, new[] { Model("first"), Model("second") }),
                new AddonEngine("another", "another", "windows-x64", runtime, new[] { Model("first") }) };
            Manager = NewManager();
        }
        public AddonLanguageManager NewManager() => new(Path, Installer, _catalog, (engine, package, language, token) =>
        {
            if (engine.Id == FailEngine) throw new IOException("injected load failure");
            var worker = new Worker(engine.Id);
            Workers.Add(worker);
            MaximumLive = Math.Max(MaximumLive, Workers.Count(w => w.IsAlive));
            return Task.FromResult<IAddonRecognizer>(worker);
        });
        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private sealed class FakeInstaller : IAddonPackageInstaller
    {
        public int Preparations { get; private set; }
        public Task<PreparedAddonPackage> PrepareAsync(string root, AddonEngine engine, AddonModel model, CancellationToken token = default)
        {
            Preparations++;
            var directory = Path.Combine(root, "packages", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Task.FromResult(new PreparedAddonPackage(directory, Path.Combine(directory, "runtime", "worker.exe"), Path.Combine(directory, model.FileName)));
        }
        public Task VerifyAsync(string root, AddonEngine engine, AddonModel model, PreparedAddonPackage package, CancellationToken token = default)
            => Directory.Exists(package.Directory) ? Task.CompletedTask : Task.FromException(new IOException("fixture package missing"));
    }

    private sealed class Worker(string engine) : IAddonRecognizer, IContextualAddonRecognizer, IConfirmedAddonStop
    {
        public bool Initialized => !Disposed;
        public bool HasSuccessfulInference { get; private set; }
        public bool IsAlive => !Disposed;
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; set; }
        public bool UnconfirmedStop { get; set; }
        public bool StopConfirmed => Disposed && !UnconfirmedStop;
        public List<string> Languages { get; } = new();
        public TaskCompletionSource<AddonAsrCandidate>? Pending { get; set; }
        public TaskCompletionSource? Entered { get; set; }
        public Task<AddonAsrCandidate> TranscribeAsync(byte[] pcm, int rate, TimeSpan timeout, CancellationToken token = default)
            => TranscribeContextAsync(pcm, rate, "ja", "requested", timeout, token);
        public Task<AddonAsrCandidate> TranscribeContextAsync(byte[] pcm, int rate, string language, string hint,
            TimeSpan timeout, CancellationToken token)
        {
            Languages.Add(language);
            HasSuccessfulInference = true;
            if (Pending is not null) { Entered?.TrySetResult(); return Pending.Task; }
            return Task.FromResult(new AddonAsrCandidate(engine, language, true, 1, "complete"));
        }
        public ValueTask DisposeAsync()
        {
            if (ThrowOnDispose) return ValueTask.FromException(new IOException("injected stop failure"));
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
