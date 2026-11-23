using MobMate.Core.Native;
using MobMate.Core.Tts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

// No device, model or real player is used. The fake models the native
// cancellation watermark and explicitly blocks entry/cleanup at barriers.
internal static class OnsetPlaybackSmoke
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static void Wait(ManualResetEventSlim signal, string name)
    {
        if (!signal.Wait(Timeout)) throw new InvalidOperationException("timeout: " + name);
    }

    internal static async Task Run(Action<string, bool> check)
    {
        var old = new FakeBackend { Supported = false };
        using (var service = new TtsPlaybackService(old))
            check("old DLL leaves filler unreserved", service.ReserveFiller() == 0 && !service.IsPlaying);
        var unavailable = new FakeBackend { ThrowSupportProbe = true };
        using (var service = new TtsPlaybackService(unavailable))
            check("capability probe IO failure is silent and preserves body playback", service.ReserveFiller() == 0 && service.PlayPcm16([1, 2], 16000, 1).Success && unavailable.BodyEntry.IsSet);

        var beforeEntry = new FakeBackend { BlockBeforeEntry = true, BlockBody = true };
        using (var service = new TtsPlaybackService(beforeEntry))
        {
            try
            {
                var id = service.ReserveFiller();
                check("pending filler is neither physical speech nor body", id != 0 && !service.IsPlaying && !service.IsBodyPlaying);
                var oldPlay = Task.Run(() => service.PlayReservedFiller(id, Wav(), 0, () => beforeEntry.Starts++));
                Wait(beforeEntry.Entry, "pre-entry");
                check("cancel before native entry succeeds", service.CancelFillerAndWait().Success);
                var body = Task.Run(() => service.PlayPcm16([1, 2], 16000, 1));
                Wait(beforeEntry.BodyEntry, "body entry");
                beforeEntry.AllowEntry.Set();
                check("late native entry is rejected", !(await oldPlay.WaitAsync(Timeout)).Success && beforeEntry.Starts == 0);
                check("late filler finally cannot clear body", service.IsPlaying && service.IsBodyPlaying);
                beforeEntry.AllowBody.Set();
                check("body survives obsolete filler", (await body.WaitAsync(Timeout)).Success);
            }
            finally { beforeEntry.AllowEntry.Set(); beforeEntry.AllowBody.Set(); }
        }

        var inFlight = new FakeBackend();
        using (var service = new TtsPlaybackService(inFlight))
        {
            try
            {
                var id = service.ReserveFiller();
                var play = Task.Run(() => service.PlayReservedFiller(id, Wav(), 0, () => inFlight.Starts++));
                Wait(inFlight.Started, "actual start ACK");
                var cancel = Task.Run(() => service.CancelFillerAndWait());
                Wait(inFlight.Cancelled, "cancel observed");
                check("cancel waits for backend cleanup", !cancel.IsCompleted && service.IsPlaying);
                check("body cannot overlap cleanup", !service.PlayPcm16([1, 2], 16000, 1).Success);
                inFlight.AllowCleanup.Set();
                check("cancel finishes after cleanup", (await cancel.WaitAsync(Timeout)).Success);
                await play.WaitAsync(Timeout);
                check("body can enter after handoff", service.PlayPcm16([1, 2], 16000, 1).Success && inFlight.Starts == 1);
            }
            finally { inFlight.AllowCleanup.Set(); }
        }

        var dispose = new FakeBackend { BlockBeforeEntry = true };
        var disposingService = new TtsPlaybackService(dispose);
        try
        {
            var id = disposingService.ReserveFiller();
            var play = Task.Run(() => disposingService.PlayReservedFiller(id, Wav(), 0, () => dispose.Starts++));
            Wait(dispose.Entry, "dispose pre-entry");
            disposingService.Dispose();
            dispose.AllowEntry.Set();
            check("dispose cancels pending native entry", !(await play.WaitAsync(Timeout)).Success && dispose.Starts == 0);
            check("disposed service cannot reserve", disposingService.ReserveFiller() == 0);
        }
        finally { dispose.AllowEntry.Set(); disposingService.Dispose(); }

        var legacy = new FakeBackend { BlockBody = true, BlockStop = true };
        using (var service = new TtsPlaybackService(legacy))
        {
            try
            {
                var body = Task.Run(() => service.PlayPcm16([1, 2], 16000, 1));
                Wait(legacy.BodyEntry, "legacy entry");
                var stop = Task.Run(service.Stop);
                Wait(legacy.StopEntry, "legacy stop entry");
                legacy.AllowBody.Set();
                await body.WaitAsync(Timeout);
                check("old Stop retains the slot until it returns", !service.PlayPcm16([1, 2], 16000, 1).Success);
                legacy.AllowStop.Set();
                check("old Stop completes", (await stop.WaitAsync(Timeout)).Success);
                check("new body starts only after old Stop", service.PlayPcm16([1, 2], 16000, 1).Success);
            }
            finally { legacy.AllowBody.Set(); legacy.AllowStop.Set(); }
        }
        await PulseOwner(check);
        await PulseFillerAcknowledgement(check);
        await LinuxMonitorOwnership(check);
    }

    private static async Task LinuxMonitorOwnership(Action<string, bool> check)
    {
        var pactl = new MonitorPactl();
        var monitor = new LinuxMonitorLoopback(pactl.Run);
        var first = await monitor.EnsureAsync("fixture-output", "fixture-monitor", 35);
        var second = await monitor.EnsureAsync("fixture-output", "fixture-monitor", 80);
        check("Linux monitor uses the source-output index, not the module or another stream", first == null &&
            pactl.Commands.Contains("set-source-output-volume 19 35%") && !pactl.Commands.Contains("set-source-output-volume 77 35%"));
        check("Linux monitor reapplies current volume on the same sink", second == null &&
            pactl.Commands.Contains("set-source-output-volume 19 80%") &&
            pactl.Commands.Count(c => c.StartsWith("load-module ", StringComparison.Ordinal)) == 1 &&
            pactl.Commands.Count(c => c == "--format=json list source-outputs") == 2);
        check("Linux monitor unloads only its marked module", await monitor.UnloadAsync() == null &&
            pactl.Commands.Contains("unload-module 77") && !pactl.Commands.Contains("unload-module 123"));

        var disappeared = new MonitorPactl();
        var existing = new LinuxMonitorLoopback(disappeared.Run);
        await existing.EnsureAsync("fixture-output", "fixture-monitor", 35);
        disappeared.Mode = "missing";
        check("Linux monitor detects a disappeared stream on same-sink volume change", await existing.EnsureAsync("fixture-output", "fixture-monitor", 80) ==
            "linux_monitor_source_output_not_unique" && !disappeared.Commands.Contains("set-source-output-volume 19 80%") &&
            disappeared.Commands.Contains("unload-module 77"));

        foreach (var mode in new[] { "missing", "ambiguous", "wrong-marker", "malformed", "list-failed" })
        {
            var failed = new MonitorPactl { Mode = mode };
            var candidate = new LinuxMonitorLoopback(failed.Run);
            var result = await candidate.EnsureAsync("fixture-output", "fixture-monitor", 40);
            check("Linux monitor rejects " + mode + " without touching another stream", result ==
                (mode == "list-failed" ? "linux_monitor_source_output_list_failed" : "linux_monitor_source_output_not_unique") &&
                !failed.Commands.Any(c => c.StartsWith("set-source-output-volume ", StringComparison.Ordinal)) &&
                failed.Commands.Contains("unload-module 77") && !failed.Commands.Contains("unload-module 123"));
        }

        var setFailed = new MonitorPactl { FailVolume = true, FailUnload = true };
        var retry = new LinuxMonitorLoopback(setFailed.Run);
        check("Linux monitor reports volume and cleanup failures", await retry.EnsureAsync("fixture-output", "fixture-monitor", 40) == "linux_monitor_volume_set_failed" &&
            retry.CleanupError == "linux_monitor_loopback_unload_failed");
        setFailed.FailUnload = false;
        check("Linux monitor retains ownership after failed unload for a safe retry", await retry.UnloadAsync() == null &&
            setFailed.Commands.Count(c => c == "unload-module 77") == 2);

        var reused = new MonitorPactl();
        var owner = new LinuxMonitorLoopback(reused.Run);
        await owner.EnsureAsync("fixture-output", "fixture-monitor", 40);
        reused.ReusedModule = true;
        check("Linux monitor does not unload an unverified reused module", await owner.EnsureAsync("other-output", "fixture-monitor", 40) ==
            "linux_monitor_loopback_ownership_unverified" && !reused.Commands.Any(c => c.StartsWith("unload-module ", StringComparison.Ordinal)) &&
            reused.Commands.Count(c => c.StartsWith("load-module ", StringComparison.Ordinal)) == 1);
        await LinuxMonitorNormalLifetime(check);
        await LinuxMonitorUnconfirmedRecovery(check);
        await LinuxMonitorShutdownBoundaries(check);
        await LinuxMonitorOwnedChildCancellation(check);
    }

    private static async Task LinuxMonitorUnconfirmedRecovery(Action<string, bool> check)
    {
        var absent = new MonitorLifetimePactl { FailNextLoad = "absent" };
        var absentMonitor = new LinuxMonitorLoopback(absent.Run);
        check("Linux unconfirmed absent load is reported", await absentMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30) == "linux_monitor_loopback_load_failed" && absent.OwnedCount == 0);
        check("Linux unconfirmed absence permits a new load with current settings", await absentMonitor.EnsureAsync("new-output", "fixture-monitor", 80) == null &&
            absent.Loads == 2 && absent.OwnedCount == 1 && absent.LastVolume == 80 && absent.OtherIntact &&
            absent.Commands.Last(c => c.StartsWith("load-module ", StringComparison.Ordinal)).Contains("source=new-output.monitor", StringComparison.Ordinal));
        check("Linux recovered absent load remains removable", await absentMonitor.UnloadAsync() == null && absent.OwnedCount == 0 && absent.OtherIntact);

        var created = new MonitorLifetimePactl { FailNextLoad = "created" };
        var createdMonitor = new LinuxMonitorLoopback(created.Run);
        check("Linux lost load response leaves the created route unconfirmed", await createdMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30) == "linux_monitor_loopback_load_failed" && created.OwnedCount == 1);
        check("Linux unconfirmed unique route is reused at current volume", await createdMonitor.EnsureAsync("fixture-output", "fixture-monitor", 80) == null &&
            created.Loads == 1 && created.OwnedCount == 1 && created.LastVolume == 80 && created.OtherIntact &&
            !created.Commands.Any(c => c.StartsWith("unload-module ", StringComparison.Ordinal)));
        check("Linux OFF removes the recovered route only", await createdMonitor.UnloadAsync() == null && created.OwnedCount == 0 && created.OtherIntact);

        var changed = new MonitorLifetimePactl { FailNextLoad = "created" };
        var changedMonitor = new LinuxMonitorLoopback(changed.Run);
        await changedMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        check("Linux changed route replaces only the original unconfirmed owner", await changedMonitor.EnsureAsync("new-output", "fixture-monitor", 80) == null &&
            changed.Loads == 2 && changed.OwnedCount == 1 && changed.LastVolume == 80 && changed.OtherIntact &&
            changed.Commands.Count(c => c.StartsWith("unload-module ", StringComparison.Ordinal)) == 1 && changed.Commands.Contains("unload-module 77") &&
            changed.Commands.Last(c => c.StartsWith("load-module ", StringComparison.Ordinal)).Contains("source=new-output.monitor", StringComparison.Ordinal));

        foreach (var fault in new[] { "failed", "malformed", "duplicate" })
        {
            var server = new MonitorLifetimePactl { FailNextLoad = "created" };
            var monitor = new LinuxMonitorLoopback(server.Run);
            await monitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
            server.ModuleListFault = fault;
            var start = server.Commands.Count;
            var retry = await monitor.EnsureAsync("fixture-output", "fixture-monitor", 80);
            var off = await monitor.UnloadAsync();
            check("Linux unconfirmed " + fault + " listing leaves retry and OFF untouched", retry == "linux_monitor_loopback_load_unconfirmed" && off == retry &&
                server.Commands.Skip(start).SequenceEqual(new[] { "list short modules", "list short modules" }) && server.Loads == 1 && server.OwnedCount == 1 && server.OtherIntact);
            server.ModuleListFault = null;
            check("Linux unconfirmed " + fault + " listing retains ownership for OFF retry", await monitor.UnloadAsync() == null && server.OwnedCount == 0 && server.OtherIntact);
        }

        var offAbsent = new MonitorLifetimePactl { FailNextLoad = "absent" };
        var offMonitor = new LinuxMonitorLoopback(offAbsent.Run);
        await offMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        check("Linux OFF clears a verified absent unconfirmed route", await offMonitor.UnloadAsync() == null && offAbsent.Loads == 1 && offAbsent.OwnedCount == 0 && offAbsent.OtherIntact);
        check("Linux ON after absent OFF can load normally", await offMonitor.EnsureAsync("fixture-output", "fixture-monitor", 80) == null && offAbsent.Loads == 2 && offAbsent.OtherIntact);

        var failedOff = new MonitorLifetimePactl { FailNextLoad = "created", FailUnload = true };
        var failedOffMonitor = new LinuxMonitorLoopback(failedOff.Run);
        await failedOffMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        check("Linux unconfirmed OFF reports failed owned unload", await failedOffMonitor.UnloadAsync() == "linux_monitor_loopback_unload_failed" && failedOff.OwnedCount == 1 && failedOff.OtherIntact);
        failedOff.FailUnload = false;
        check("Linux failed unconfirmed OFF retains ownership for retry", await failedOffMonitor.UnloadAsync() == null && failedOff.OwnedCount == 0 && failedOff.OtherIntact);

        var stopped = new MonitorLifetimePactl { FailNextLoad = "created" };
        var stoppedMonitor = new LinuxMonitorLoopback(stopped.Run);
        await stoppedMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        using (var gate = new SemaphoreSlim(1, 1))
            check("Linux shutdown resolves and unloads a unique unconfirmed route", await stoppedMonitor.StopWithinAsync(gate, TimeSpan.FromSeconds(2)) == null &&
                stopped.OwnedCount == 0 && stopped.OtherIntact && await stoppedMonitor.EnsureAsync("fixture-output", "fixture-monitor", 80) == "linux_monitor_stopped");

        var cancelled = new MonitorLifetimePactl { FailNextLoad = "created" };
        var configEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockConfig = false;
        async Task<string?> DelayedConfig(string command, CancellationToken ct)
        {
            if (blockConfig && command == "list short modules")
            {
                configEntered.TrySetResult();
                await configRelease.Task;
            }
            return await cancelled.Run(command, ct);
        }
        var cancelledMonitor = new LinuxMonitorLoopback(DelayedConfig);
        await cancelledMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        blockConfig = true;
        using (var cancel = new CancellationTokenSource())
        {
            var pending = cancelledMonitor.EnsureAsync("new-output", "fixture-monitor", 80, cancel.Token);
            await configEntered.Task.WaitAsync(Timeout);
            cancel.Cancel();
            configRelease.TrySetResult();
            var wasCancelled = false;
            try { await pending.WaitAsync(Timeout); } catch (OperationCanceledException) { wasCancelled = true; }
            check("Linux cancelled unconfirmed configuration performs no late changes", wasCancelled && cancelled.Loads == 1 && cancelled.OwnedCount == 1 && cancelled.OtherIntact &&
                !cancelled.Commands.Any(c => c.StartsWith("unload-module ", StringComparison.Ordinal) || c.StartsWith("set-source-output-volume ", StringComparison.Ordinal)));
        }
        blockConfig = false;
        check("Linux cancelled unconfirmed configuration retains the creation route for a current retry", await cancelledMonitor.EnsureAsync("new-output", "fixture-monitor", 80) == null &&
            cancelled.Loads == 2 && cancelled.OwnedCount == 1 && cancelled.OtherIntact && cancelled.Commands.Contains("unload-module 77") && cancelled.LastVolume == 80);

        var delayed = new MonitorLifetimePactl { FailNextLoad = "created" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockList = false;
        async Task<string?> Delayed(string command, CancellationToken ct)
        {
            if (blockList && command == "list short modules")
            {
                entered.TrySetResult();
                await release.Task; // Model a service that returns after cancellation.
            }
            return await delayed.Run(command, ct);
        }
        var delayedMonitor = new LinuxMonitorLoopback(Delayed);
        await delayedMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        blockList = true;
        using (var gate = new SemaphoreSlim(1, 1))
        {
            var shutdown = delayedMonitor.StopWithinAsync(gate, TimeSpan.FromMilliseconds(150));
            await entered.Task.WaitAsync(Timeout);
            check("Linux unconfirmed shutdown bounds a stalled listing", await shutdown.WaitAsync(Timeout) == "linux_monitor_shutdown_timed_out" && delayed.OwnedCount == 1 && delayed.OtherIntact);
            release.TrySetResult();
            if (!await gate.WaitAsync(Timeout)) throw new InvalidOperationException("timeout: unconfirmed monitor cleanup");
            gate.Release();
            check("Linux cancelled unconfirmed listing performs no late unload", delayed.OwnedCount == 1 && delayed.OtherIntact && !delayed.Commands.Any(c => c.StartsWith("unload-module ", StringComparison.Ordinal)));
            blockList = false;
            check("Linux cancelled unconfirmed cleanup retains state for retry", await delayedMonitor.StopWithinAsync(gate, TimeSpan.FromSeconds(2)) == null && delayed.OwnedCount == 0 && delayed.OtherIntact);
        }
    }

    private static async Task LinuxMonitorNormalLifetime(Action<string, bool> check)
    {
        var server = new MonitorLifetimePactl();
        var first = new LinuxMonitorLoopback(server.Run);
        check("Linux monitor first lifetime enables only its own route", await first.EnsureAsync("fixture-output", "fixture-monitor", 30) == null && server.OwnedCount == 1);
        check("Linux monitor normal shutdown leaves no owned server module", await first.StopAsync() == null && server.OwnedCount == 0 && server.OtherIntact);
        check("Linux monitor late configuration cannot recreate output after shutdown", await first.EnsureAsync("fixture-output", "fixture-monitor", 80) == "linux_monitor_stopped" && server.Loads == 1);
        var next = new LinuxMonitorLoopback(server.Run);
        check("Linux monitor next lifetime does not duplicate the old route", await next.EnsureAsync("fixture-output", "fixture-monitor", 80) == null && server.OwnedCount == 1 && server.LastVolume == 80);
        check("Linux monitor OFF in the next lifetime removes all its routes, not another owner", await next.UnloadAsync() == null && server.OwnedCount == 0 && server.OtherIntact);
    }

    private static async Task LinuxMonitorShutdownBoundaries(Action<string, bool> check)
    {
        var server = new MonitorLifetimePactl();
        using var heldGate = new SemaphoreSlim(1, 1);
        var queued = new LinuxMonitorLoopback(server.Run);
        await heldGate.WaitAsync();
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await queued.StopWithinAsync(heldGate, TimeSpan.FromMilliseconds(150));
            check("Linux monitor shutdown bounds an unresponsive settings gate", result == "linux_monitor_shutdown_timed_out" && watch.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally { heldGate.Release(); }
        check("Linux monitor rejects settings delayed past shutdown", await queued.EnsureAsync("fixture-output", "fixture-monitor", 30) == "linux_monitor_stopped" && server.Loads == 0 && server.OtherIntact);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayList = false;
        async Task<string?> Delayed(string command, CancellationToken ct)
        {
            if (delayList && command == "list short modules")
            {
                entered.TrySetResult();
                await release.Task; // Intentionally ignore cancellation to model a broken service.
            }
            return await server.Run(command, ct);
        }
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new LinuxMonitorLoopback(Delayed);
        check("Linux shutdown fixture owns one module", await owner.EnsureAsync("fixture-output", "fixture-monitor", 30) == null && server.OwnedCount == 1);
        delayList = true;
        var shutdown = owner.StopWithinAsync(gate, TimeSpan.FromMilliseconds(150));
        await entered.Task.WaitAsync(Timeout);
        check("Linux monitor bounds a stalled pactl cleanup", await shutdown.WaitAsync(Timeout) == "linux_monitor_shutdown_timed_out" && server.OwnedCount == 1 && server.OtherIntact);
        release.TrySetResult();
        if (!await gate.WaitAsync(Timeout)) throw new InvalidOperationException("timeout: late monitor cleanup");
        gate.Release();
        check("late pactl listing cannot unload after timeout", server.OwnedCount == 1 && server.OtherIntact);
        delayList = false;
        check("timed-out monitor retains ownership for a verified retry", await owner.StopWithinAsync(gate, TimeSpan.FromSeconds(2)) == null && server.OwnedCount == 0 && server.OtherIntact);

        var configServer = new MonitorLifetimePactl();
        var configEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockConfig = false;
        async Task<string?> DelayedConfig(string command, CancellationToken ct)
        {
            if (blockConfig && command == "list short modules")
            {
                configEntered.TrySetResult();
                await configRelease.Task;
            }
            return await configServer.Run(command, ct);
        }
        using var configGate = new SemaphoreSlim(1, 1);
        using var stopping = new CancellationTokenSource();
        var configMonitor = new LinuxMonitorLoopback(DelayedConfig);
        await configMonitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        blockConfig = true;
        async Task Configure()
        {
            await configGate.WaitAsync();
            try { await configMonitor.EnsureAsync("new-output", "fixture-monitor", 80, stopping.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            finally { configGate.Release(); }
        }
        var pendingConfig = Configure();
        await configEntered.Task.WaitAsync(Timeout);
        configMonitor.RequestStop();
        stopping.Cancel();
        check("shutdown advances while an old configuration holds the gate", await configMonitor.StopWithinAsync(configGate, TimeSpan.FromMilliseconds(150)) == "linux_monitor_shutdown_timed_out");
        configRelease.TrySetResult();
        await pendingConfig.WaitAsync(Timeout);
        check("late settings cannot recreate a monitor or touch another owner", configServer.Loads == 1 && configServer.OwnedCount == 1 && configServer.OtherIntact);
        blockConfig = false;
        check("late settings leave owned cleanup retryable", await configMonitor.StopWithinAsync(configGate, TimeSpan.FromSeconds(2)) == null && configServer.OwnedCount == 0 && configServer.OtherIntact);

        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> UnconfirmedLoad(string command, CancellationToken ct)
        {
            if (command.StartsWith("load-module ", StringComparison.Ordinal))
            {
                loadEntered.TrySetResult();
                await loadRelease.Task;
                throw new OperationCanceledException(ct);
            }
            if (command == "list short modules") return null;
            throw new InvalidOperationException("unconfirmed load must not change another module");
        }
        using var loadCancel = new CancellationTokenSource();
        var unconfirmed = new LinuxMonitorLoopback(UnconfirmedLoad);
        var loading = unconfirmed.EnsureAsync("fixture-output", "fixture-monitor", 30, loadCancel.Token);
        await loadEntered.Task.WaitAsync(Timeout);
        unconfirmed.RequestStop();
        loadCancel.Cancel();
        loadRelease.TrySetResult();
        try { await loading.WaitAsync(Timeout); } catch (OperationCanceledException) { }
        check("cancelled load with unknown module ID is not reported as cleaned up", await unconfirmed.StopAsync() == "linux_monitor_loopback_load_unconfirmed");
    }

    private static async Task LinuxMonitorOwnedChildCancellation(Action<string, bool> check)
    {
        var ownerName = "mobmate-pactl-" + Guid.NewGuid().ToString("N");
        var otherName = "mobmate-pactl-other-" + Guid.NewGuid().ToString("N");
        using var ownerPipe = new NamedPipeServerStream(ownerName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var otherPipe = new NamedPipeServerStream(otherName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var other = Process.Start(Child(otherName)) ?? throw new InvalidOperationException("other child did not start");
        var info = Child(ownerName);
        info.RedirectStandardOutput = true;
        var server = new MonitorLifetimePactl();
        var stall = false;
        async Task<string?> Run(string command, CancellationToken ct)
        {
            if (stall && command == "list short modules")
            {
                var result = await LinuxMonitorLoopback.RunChildAsync(info, ct);
                return result.ExitCode == 0 ? result.Stdout : null;
            }
            return await server.Run(command, ct);
        }
        using var gate = new SemaphoreSlim(1, 1);
        var monitor = new LinuxMonitorLoopback(Run);
        await monitor.EnsureAsync("fixture-output", "fixture-monitor", 30);
        stall = true;
        var shutdown = monitor.StopWithinAsync(gate, TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(ownerPipe.WaitForConnectionAsync(), otherPipe.WaitForConnectionAsync()).WaitAsync(Timeout);
            var result = await shutdown.WaitAsync(Timeout);
            var eof = await ownerPipe.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout);
            check("timed-out pactl shutdown terminates only its own child", result == "linux_monitor_shutdown_timed_out" && eof == 0 && !other.HasExited && server.OwnedCount == 1 && server.OtherIntact);
            stall = false;
            check("cancelled pactl leaves owned module available for verified cleanup", await monitor.StopWithinAsync(gate, TimeSpan.FromSeconds(2)) == null && server.OwnedCount == 0 && server.OtherIntact && !other.HasExited);
        }
        finally
        {
            ownerPipe.Dispose();
            otherPipe.Dispose();
            if (!other.HasExited) await other.WaitForExitAsync().WaitAsync(Timeout);
        }
    }

    // A server outlives both application lifetimes. Only module/stream state is
    // modeled; no Pulse server, process, device or microphone is used.
    private sealed class MonitorLifetimePactl
    {
        private readonly Dictionary<uint, string> _modules = new() { [123] = "other-owner" };
        private uint _next = 77;
        internal int Loads, LastVolume;
        internal string? FailNextLoad, ModuleListFault;
        internal bool FailUnload;
        internal readonly List<string> Commands = new();
        internal int OwnedCount => _modules.Count - 1;
        internal bool OtherIntact => _modules.TryGetValue(123, out var marker) && marker == "other-owner";

        internal Task<string?> Run(string command, CancellationToken ct)
        {
            Commands.Add(command);
            string? result = "";
            if (command.StartsWith("load-module ", StringComparison.Ordinal))
            {
                Loads++;
                if (FailNextLoad == "absent")
                {
                    FailNextLoad = null;
                    return Task.FromResult<string?>(null);
                }
                var id = _next++;
                _modules.Add(id, command.Split("source_output_properties=media.name=")[1]);
                result = FailNextLoad == "created" ? null : id.ToString();
                FailNextLoad = null;
            }
            else if (command == "--format=json list source-outputs")
                result = JsonSerializer.Serialize(_modules.Select(pair => new { index = pair.Key + 1000, owner_module = pair.Key,
                    properties = new Dictionary<string, string> { ["media.name"] = pair.Value } }));
            else if (command == "list short modules")
            {
                result = string.Join('\n', _modules.Select(pair => $"{pair.Key}\tmodule-loopback\tsource_output_properties=media.name={pair.Value}\t"));
                if (ModuleListFault == "failed") result = null;
                else if (ModuleListFault == "malformed") result += "\ninvalid\tmodule-loopback\tbad";
                else if (ModuleListFault == "duplicate") result += $"\n999\tmodule-loopback\tsource_output_properties=media.name={_modules.First(pair => pair.Key != 123).Value}\t";
            }
            else if (command.StartsWith("unload-module ", StringComparison.Ordinal))
                result = !FailUnload && _modules.Remove(uint.Parse(command.Split(' ')[1])) ? "" : null;
            else if (command.StartsWith("set-source-output-volume ", StringComparison.Ordinal))
            {
                var parts = command.Split(' ');
                result = _modules.ContainsKey(uint.Parse(parts[1]) - 1000) ? "" : null;
                LastVolume = int.Parse(parts[2].TrimEnd('%'));
            }
            return Task.FromResult(result);
        }
    }

    private sealed class MonitorPactl
    {
        internal readonly List<string> Commands = new();
        internal string Mode = "normal";
        internal bool FailVolume, FailUnload, ReusedModule;
        private string _marker = "";

        internal Task<string?> Run(string command, CancellationToken ct)
        {
            Commands.Add(command);
            string? result = "";
            if (command.StartsWith("load-module ", StringComparison.Ordinal))
            {
                _marker = command.Split("source_output_properties=media.name=")[1];
                result = "77";
            }
            else if (command == "--format=json list source-outputs")
            {
                var other = new { index = 77, owner_module = "123", properties = new Dictionary<string, string> { ["media.name"] = "other" } };
                var owned = new { index = 19, owner_module = "77", properties = new Dictionary<string, string> { ["media.name"] = Mode == "wrong-marker" ? "other" : _marker } };
                var unowned = new { index = 24, owner_module = (string?)null, properties = new Dictionary<string, string>() };
                result = Mode switch
                {
                    "missing" => JsonSerializer.Serialize(new[] { other }),
                    "ambiguous" => JsonSerializer.Serialize(new[] { other, owned, owned }),
                    "malformed" => "[",
                    "list-failed" => null,
                    _ => JsonSerializer.Serialize(new object[] { other, unowned, owned })
                };
            }
            else if (command == "list short modules")
                result = "123\tmodule-loopback\tsource_output_properties=media.name=other\t\n" +
                    "77\tmodule-loopback\tsource_output_properties=media.name=" + (ReusedModule ? "other" : _marker) + "\t\n";
            else if (command.StartsWith("set-source-output-volume ", StringComparison.Ordinal) && FailVolume ||
                     command.StartsWith("unload-module ", StringComparison.Ordinal) && FailUnload)
                result = null;
            return Task.FromResult(result);
        }
    }

    private static ProcessStartInfo Child(string pipeName, string mode = "wait")
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("test executable unavailable");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--pulse-owner-child");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(mode);
        return info;
    }

    internal static async Task<int> PulseChild(string name, string mode = "wait")
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        if (mode != "wait")
        {
            using var protocol = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            await protocol.WriteLineAsync("Stream successfully created.");
            await protocol.WriteLineAsync("Connected to device fixture-sink (index: 42, suspended: no).");
            if (mode == "no-ack") return 0;
            await protocol.WriteLineAsync("Stream started.\u001b[K");
        }
        await pipe.ReadAsync(new byte[1]); // Wait for release or parent EOF, without audio I/O.
        return 0;
    }

    private static async Task PulseFillerAcknowledgement(Action<string, bool> check)
    {
        var scratch = Path.Combine("temp", "voice-filler", "pulse-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var launches = 0;
            using (var pending = new TtsPlaybackService(new FakeBackend(), pulseTestProcess: () => { launches++; throw new InvalidOperationException("cancelled reservation must not start"); }))
            {
                var id = pending.ReserveFiller("fixture-sink");
                check("Pulse cancellation before worker entry drains without launching", pending.CancelFillerAndWait().Success &&
                    !pending.PlayReservedFiller(id, Wav(), 0, () => throw new InvalidOperationException("unexpected ACK"), scratch).Success && launches == 0);
            }
            foreach (var mode in new[] { "ack", "no-ack" })
            {
                var name = "mobmate-filler-" + Guid.NewGuid().ToString("N");
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var backend = new FakeBackend();
                using var service = new TtsPlaybackService(backend, pulseTestProcess: () => Child(name, mode));
                var id = service.ReserveFiller("fixture-sink");
                var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var play = Task.Run(() => service.PlayReservedFiller(id, Wav(), 0, () => ack.TrySetResult(), scratch));
                await pipe.WaitForConnectionAsync().WaitAsync(Timeout);
                if (mode == "ack")
                {
                    await ack.Task.WaitAsync(Timeout);
                    check("Pulse real-start protocol, not spawn/READY, reports filler ownership", service.IsPlaying && !service.IsBodyPlaying);
                    check("Pulse filler cancels only its owned child and waits for exit", (await Task.Run(service.CancelFillerAndWait).WaitAsync(Timeout)).Success);
                }
                else check("spawn and READY without stream-start ACK stay unconfirmed", !(await play.WaitAsync(Timeout)).Success && !ack.Task.IsCompleted);
                await play.WaitAsync(Timeout);
                check("Pulse filler never falls back through native playback or Stop", !backend.BodyEntry.IsSet && !backend.StopEntry.IsSet && !backend.Cancelled.IsSet);
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static async Task PulseOwner(Action<string, bool> check)
    {
        var ownerName = "mobmate-owner-" + Guid.NewGuid().ToString("N");
        var otherName = "mobmate-other-" + Guid.NewGuid().ToString("N");
        using var ownerPipe = new NamedPipeServerStream(ownerName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var otherPipe = new NamedPipeServerStream(otherName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var other = Process.Start(Child(otherName)) ?? throw new InvalidOperationException("other child did not start");
        using var service = new TtsPlaybackService(new FakeBackend(), pulseTestProcess: () => Child(ownerName));
        var play = Task.Run(() => service.PlayWavViaPulse(Wav(), "fixture", testTempDirectory: Path.GetFullPath("temp/voice-filler")));
        try
        {
            await Task.WhenAll(ownerPipe.WaitForConnectionAsync(), otherPipe.WaitForConnectionAsync()).WaitAsync(Timeout);
            check("pulse child is a body owner", service.IsPlaying && service.IsBodyPlaying);
            check("pulse Stop waits for owned child exit", (await Task.Run(service.Stop).WaitAsync(Timeout)).Success);
            check("intentional pulse Stop does not request fallback", (await play.WaitAsync(Timeout)).Success);
            check("unrelated child remains alive", !other.HasExited);
        }
        finally
        {
            ownerPipe.Dispose();
            otherPipe.Dispose();
            if (!other.HasExited) await other.WaitForExitAsync().WaitAsync(Timeout);
        }
    }

    internal static byte[] Wav()
    {
        byte[] data = new byte[48];
        "RIFF"u8.CopyTo(data); BitConverter.GetBytes(40).CopyTo(data, 4); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(data, 16); BitConverter.GetBytes((short)1).CopyTo(data, 20);
        BitConverter.GetBytes((short)1).CopyTo(data, 22); BitConverter.GetBytes(16000).CopyTo(data, 24);
        BitConverter.GetBytes(32000).CopyTo(data, 28); BitConverter.GetBytes((short)2).CopyTo(data, 32);
        BitConverter.GetBytes((short)16).CopyTo(data, 34); "data"u8.CopyTo(data.AsSpan(36));
        BitConverter.GetBytes(4).CopyTo(data, 40); data[44] = 1;
        return data;
    }

    private sealed class FakeBackend : IPcmPlaybackBackend
    {
        private readonly object _gate = new();
        private ulong _next, _cancelledThrough, _active;
        internal bool Supported = true, BlockBeforeEntry, BlockBody, BlockStop, ThrowSupportProbe;
        internal int Starts;
        internal readonly ManualResetEventSlim Entry = new(), AllowEntry = new(), Started = new(), Cancelled = new(),
            AllowCleanup = new(), FillerExited = new(), BodyEntry = new(), AllowBody = new(),
            StopEntry = new(), AllowStop = new();

        public bool SupportsFiller() => ThrowSupportProbe ? throw new IOException("fixture probe unavailable") : Supported;
        public ulong NextFillerId() { lock (_gate) return ++_next; }
        public NativeLibraryResult Play(short[] samples, int sampleRate, int channels, int monitorVolumePercent)
        {
            BodyEntry.Set();
            if (BlockBody) Wait(AllowBody, "body finish");
            return Ok();
        }
        public NativeLibraryResult Stop()
        {
            StopEntry.Set();
            if (BlockStop) Wait(AllowStop, "legacy stop return");
            return Ok();
        }
        public NativeLibraryResult PlayFiller(ulong id, short[] samples, int sampleRate, int channels, int monitorVolumePercent, Action started)
        {
            Entry.Set();
            if (BlockBeforeEntry) Wait(AllowEntry, "allow entry");
            lock (_gate)
            {
                if (id <= _cancelledThrough) return Fail();
                _active = id;
            }
            started();
            Started.Set();
            Wait(AllowCleanup, "filler cleanup");
            lock (_gate) _active = 0;
            FillerExited.Set();
            return Ok();
        }
        public NativeLibraryResult CancelFiller(ulong id)
        {
            bool active;
            lock (_gate) { _cancelledThrough = Math.Max(_cancelledThrough, id); active = _active == id; }
            Cancelled.Set();
            if (active) Wait(FillerExited, "filler exit");
            return Ok();
        }
        private static NativeLibraryResult Ok() => new() { Success = true };
        private static NativeLibraryResult Fail() => new() { Success = false, Message = "cancelled" };
    }
}
