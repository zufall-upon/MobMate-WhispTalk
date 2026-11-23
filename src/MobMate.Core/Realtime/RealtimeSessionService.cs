namespace MobMate.Core.Realtime;

/// <summary>Serializes native realtime session ownership and prevents partially-started sessions.</summary>
public sealed class RealtimeSessionService : IDisposable
{
    private readonly object _gate = new();
    private RealtimeSessionController? _active;
    private readonly Action? _onStopping;

    public RealtimeSessionService() { }
    public RealtimeSessionService(Action onStopping) => _onStopping = onStopping;

    public RealtimeSessionController? Current { get { lock (_gate) return _active; } }

    public void Create(Func<RealtimeSessionController> create)
    {
        lock (_gate)
        {
            DisposeActive();
            RealtimeSessionController? created = null;
            try { created = create(); _active = created; }
            catch { created?.Dispose(); throw; }
        }
    }

    public void Start(Func<RealtimeSessionController> create)
    {
        lock (_gate)
        {
            DisposeActive();
            RealtimeSessionController? created = null;
            try
            {
                created = create();
                created.Start();
                _active = created;
            }
            catch
            {
                created?.Dispose();
                _active = null;
                throw;
            }
        }
    }

    public RealtimePollResult? Stop()
    {
        lock (_gate)
        {
            if (_active == null) return null;
            _onStopping?.Invoke();
            try { _active.Stop(); return _active.Poll(); }
            finally { DisposeActive(notify: false); }
        }
    }

    public void Dispose() { lock (_gate) DisposeActive(); }

    private void DisposeActive(bool notify = true)
    {
        var active = _active;
        _active = null;
        if (active != null && notify) _onStopping?.Invoke();
        active?.Dispose();
    }
}
