namespace MobMate.Core.Asr;

/// <summary>Prevents add-on setup/probes from overlapping application ASR work.</summary>
public sealed class AddonOperationGate
{
    private readonly object _sync = new();
    private int _workCount;
    private bool _addonOperation;
    private int _waitingAddon;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? TryEnterWork()
    {
        lock (_sync)
        {
            if (_addonOperation || _waitingAddon != 0) return null;
            ++_workCount;
            return new Lease(this, false);
        }
    }

    public IDisposable? TryEnterAddonOperation()
    {
        lock (_sync)
        {
            if (_addonOperation || _workCount != 0 || _waitingAddon != 0) return null;
            _addonOperation = true;
            return new Lease(this, true);
        }
    }

    public async Task<IDisposable> EnterAddonOperationAsync(CancellationToken token)
    {
        lock (_sync) ++_waitingAddon;
        try
        {
            for (;;)
            {
                Task changed;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_addonOperation && _workCount == 0) { _addonOperation = true; return new Lease(this, true); }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { lock (_sync) --_waitingAddon; }
    }

    private void Exit(bool addon)
    {
        lock (_sync)
        {
            if (addon) _addonOperation = false;
            else --_workCount;
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private sealed class Lease(AddonOperationGate owner, bool addon) : IDisposable
    {
        private AddonOperationGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(addon);
    }
}
