namespace MobMate.Core.Realtime;

public readonly record struct RealtimeOutputTicket(long Generation, CancellationToken Cancellation);

/// <summary>Small final-output boundary shared by both hosts, not a job framework.</summary>
public sealed class RealtimeOutputEpoch : IDisposable
{
    private readonly object _sync = new();
    private long _generation;
    private bool _active;
    private bool _disposed;
    private CancellationTokenSource _cancellation = new();

    public RealtimeOutputTicket Capture()
    { lock (_sync) return new(_generation, _disposed ? new CancellationToken(true) : _cancellation.Token); }

    public RealtimeOutputTicket Begin() => Change(true);
    public RealtimeOutputTicket Invalidate() => Change(false);

    public bool Activate(RealtimeOutputTicket ticket)
    {
        lock (_sync)
        {
            if (_disposed || ticket.Generation != _generation || ticket.Cancellation.IsCancellationRequested) return false;
            _active = true;
            return true;
        }
    }

    private RealtimeOutputTicket Change(bool active)
    {
        CancellationTokenSource previous;
        RealtimeOutputTicket ticket;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _cancellation;
            _cancellation = new();
            _active = active;
            ticket = new(++_generation, _cancellation.Token);
        }
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
        return ticket;
    }

    public T Commit<T>(RealtimeOutputTicket ticket, Func<T> effect, Func<T> obsolete)
    {
        lock (_sync)
        {
            return _active && ticket.Generation == _generation && !ticket.Cancellation.IsCancellationRequested
                ? effect() : obsolete();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            ++_generation;
            previous = _cancellation;
        }
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
    }
}
