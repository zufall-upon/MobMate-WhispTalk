using MobMate.Core.Native;
using MobMate.Core.Realtime;

// Separate process per variant: P/Invoke resolution is cached by the runtime.
internal static class OnsetNativeSmoke
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static async Task<int> Run(string nativeExe, string variant)
    {
        NativeLibraryBridge.SetNativePath(nativeExe);
        Check(NativeLibraryBridge.SupportsIdentifiedFiller(nativeExe) == (variant == "new"),
            "optional identified PCM exports");
        if (variant == "new")
        {
            var id = NativeLibraryBridge.NextFillerRequestId();
            int starts = 0;
            var played = NativeLibraryBridge.PlayFillerPcm16(nativeExe, id, [1, 2], 16000, 1, 0, () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                starts++;
            });
            Check(played.Success && starts == 1, "identified PCM ACK and delegate lifetime");
            var cancelled = NativeLibraryBridge.NextFillerRequestId();
            Check(NativeLibraryBridge.CancelFillerAndWait(nativeExe, cancelled).Success,
                "identified PCM pre-entry cancel");
            var late = NativeLibraryBridge.PlayFillerPcm16(nativeExe, cancelled, [1, 2], 16000, 1, 0, () => starts++);
            Check(!late.Success && starts == 1, "cancelled ID cannot invoke ACK");
        }
        using var session = RealtimeSessionController.Create(nativeExe);
        var sequences = new List<ulong>();
        var supported = session.SetVadStartHandler(sequence => sequences.Add(sequence));
        Check(supported == (variant == "new"), "optional native export availability");
        session.Start();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(session.Poll().State == "recording", "poll survives callback or absent export");
        Check(variant == "old" ? sequences.Count == 0 : sequences.SequenceEqual(new ulong[] { 1 }), "callback retained across GC");
        if (variant == "old")
        {
            session.Stop();
            return 0;
        }

        session.Stop();
        session.Start();
        Check(session.Poll().State == "recording" && sequences.SequenceEqual(new ulong[] { 1, 2 }), "callback remains valid across Stop and Start");
        Check(session.SetVadStartHandler(_ => throw new InvalidOperationException("fixture")), "replace callback");
        Check(session.Poll().State == "recording", "callback exception does not cross native boundary");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        Check(session.SetVadStartHandler(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }), "install drain callback");
        var poll = Task.Run(session.Poll);
        Check(entered.Wait(TimeSpan.FromSeconds(3)), "callback entered");
        var dispose = Task.Run(() => { disposing.Set(); session.Dispose(); });
        try
        {
            Check(disposing.Wait(TimeSpan.FromSeconds(3)), "dispose attempted");
            Check(await Task.WhenAny(dispose, Task.Delay(100)) != dispose, "dispose waits for active poll");
        }
        finally { release.Set(); }
        Check((await poll.WaitAsync(TimeSpan.FromSeconds(3))).State == "recording", "poll returned");
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        var disposed = false;
        try { session.Poll(); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "disposed session rejects poll");
        return 0;
    }
}
