using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

internal sealed class PendingSpeechService : IDisposable
{
    private readonly object _lock = new();
    private readonly string _configPath;
    private readonly ILogger _logger;
    private readonly List<PendingSpeechItem> _queue = [];
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private PendingSpeechConfig _config;
    private long _nextId = 1;
    private System.Threading.Timer? _timer;

    public PendingSpeechService(string appRoot, ILogger logger)
    {
        _logger = logger;
        _configPath = Path.Combine(appRoot, "pending_speech.json");
        _config = LoadOrDefault();
    }

    public PendingSpeechConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void ApplyConfig(PendingSpeechConfig config)
    {
        var normalized = Normalize(config);
        List<PendingSpeechItem> toFlush;
        lock (_lock)
        {
            _config = normalized;
            SaveConfigUnsafe();
            if (_config.Mode == "instant" && _queue.Count > 0)
            {
                toFlush = [.. _queue];
                _queue.Clear();
                StopTimerUnsafe();
            }
            else
            {
                toFlush = [];
                RescheduleUnsafe();
            }
        }

        if (toFlush.Count > 0)
        {
            _logger.LogInformation("[PendingSpeech] mode=instant のため保留{Count}件を破棄しました", toFlush.Count);
        }
    }

    public PendingSpeechStatus GetStatus()
    {
        lock (_lock)
        {
            PruneCancelledUnsafe();
            var head = _queue.Count > 0 ? _queue[0] : null;
            var remaining = head == null ? 0 : Math.Max(0, (int)Math.Ceiling((head.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds));
            return new PendingSpeechStatus
            {
                Mode = _config.Mode,
                WaitSeconds = _config.WaitSeconds,
                MaxQueue = _config.MaxQueue,
                PendingCount = _queue.Count,
                RemainingSeconds = remaining,
                Items = _queue.Select((x, idx) => new PendingSpeechItemDto
                {
                    Id = x.Id,
                    Slot = idx + 1,
                    Text = x.Text,
                    RemainingSeconds = Math.Max(0, (int)Math.Ceiling((x.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)),
                }).ToList()
            };
        }
    }

    public async Task EnqueueOrSpeakAsync(string text, Func<string, CancellationToken, Task> speakAsync, CancellationToken ct,
        CancellationToken originCancellation = default)
    {
        if (string.IsNullOrWhiteSpace(text) || originCancellation.IsCancellationRequested) return;
        PendingSpeechItem? dropped = null;
        lock (_lock)
        {
            if (_config.Mode == "instant")
            {
                dropped = null;
            }
            else
            {
                if (_queue.Count >= _config.MaxQueue)
                {
                    dropped = _queue[0];
                    _queue.RemoveAt(0);
                }
                _queue.Add(new PendingSpeechItem
                {
                    Id = _nextId++,
                    Text = text,
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(_config.WaitSeconds),
                    OriginCancellation = originCancellation
                });
                RescheduleUnsafe();
                return;
            }
        }

        if (dropped != null)
            _logger.LogWarning("[PendingSpeech] queue overflow で先頭を破棄 id={Id}", dropped.Id);
        await speakAsync(text, ct);
    }

    public Task ApproveFirstAsync(Func<string, CancellationToken, Task> speakAsync, CancellationToken ct)
        => ApproveBySlotAsync(1, speakAsync, ct);

    public Task CancelFirstAsync(CancellationToken ct)
        => CancelBySlotAsync(1, ct);

    public async Task ApproveBySlotAsync(int slot, Func<string, CancellationToken, Task> speakAsync, CancellationToken ct)
    {
        PendingSpeechItem? item = null;
        lock (_lock)
        {
            PruneCancelledUnsafe();
            if (slot < 1 || slot > _queue.Count) return;
            item = _queue[slot - 1];
            _queue.RemoveAt(slot - 1);
            RescheduleUnsafe();
        }
        if (item is not null && !string.IsNullOrWhiteSpace(item.Text))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, item.OriginCancellation);
            try { await SpeakSerializedAsync(item.Text, speakAsync, linked.Token); }
            catch (OperationCanceledException) when (item.OriginCancellation.IsCancellationRequested) { }
        }
    }

    public Task CancelBySlotAsync(int slot, CancellationToken ct)
    {
        lock (_lock)
        {
            if (slot < 1 || slot > _queue.Count) return Task.CompletedTask;
            _queue.RemoveAt(slot - 1);
            RescheduleUnsafe();
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _queue.Clear();
            StopTimerUnsafe();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopTimerUnsafe();
        }
    }

    private PendingSpeechConfig LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaults = Normalize(new PendingSpeechConfig());
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
                File.WriteAllText(_configPath, JsonSerializer.Serialize(defaults, JsonOptions), new UTF8Encoding(false));
                return defaults;
            }
            var parsed = JsonSerializer.Deserialize<PendingSpeechConfig>(File.ReadAllText(_configPath, Encoding.UTF8), JsonOptions);
            return Normalize(parsed ?? new PendingSpeechConfig());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PendingSpeech] config load失敗。defaultを使用します");
            return Normalize(new PendingSpeechConfig());
        }
    }

    private void SaveConfigUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions), new UTF8Encoding(false));
    }

    private void OnTimer()
    {
        PendingSpeechItem? head = null;
        lock (_lock)
        {
            PruneCancelledUnsafe();
            if (_queue.Count == 0 || _config.Mode != "wait")
            {
                StopTimerUnsafe();
                return;
            }
            if (_queue[0].ExpiresAtUtc > DateTime.UtcNow)
            {
                RescheduleUnsafe();
                return;
            }
            head = _queue[0];
            _queue.RemoveAt(0);
            RescheduleUnsafe();
        }

        if (head != null)
        {
            _logger.LogInformation("[PendingSpeech] timeout auto-speak id={Id}", head.Id);
            _ = Task.Run(() => TimeoutSpeakAsync(head));
        }
    }

    private async Task TimeoutSpeakAsync(PendingSpeechItem item)
    {
        try
        {
            if (OnTimeoutSpeakAsync != null)
                await SpeakSerializedAsync(item.Text, OnTimeoutSpeakAsync, item.OriginCancellation);
        }
        catch (OperationCanceledException) when (item.OriginCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PendingSpeech] timeout speak failed");
        }
    }

    public Func<string, CancellationToken, Task>? OnTimeoutSpeakAsync { get; set; }

    private async Task SpeakSerializedAsync(string text, Func<string, CancellationToken, Task> speakAsync, CancellationToken ct)
    {
        await _speechGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await speakAsync(text, ct);
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private void RescheduleUnsafe()
    {
        PruneCancelledUnsafe();
        if (_queue.Count == 0 || _config.Mode != "wait")
        {
            StopTimerUnsafe();
            return;
        }
        var due = _queue[0].ExpiresAtUtc - DateTime.UtcNow;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        _timer ??= new System.Threading.Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void PruneCancelledUnsafe() => _queue.RemoveAll(item => item.OriginCancellation.IsCancellationRequested);

    private void StopTimerUnsafe()
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private static PendingSpeechConfig Normalize(PendingSpeechConfig src)
    {
        var mode = string.Equals(src.Mode, "wait", StringComparison.OrdinalIgnoreCase) ? "wait" : "instant";
        var waitSeconds = src.WaitSeconds < 1 ? 1 : (src.WaitSeconds > 10 ? 10 : src.WaitSeconds);
        var maxQueue = src.MaxQueue < 1 ? 1 : (src.MaxQueue > 9 ? 9 : src.MaxQueue);
        return new PendingSpeechConfig { Mode = mode, WaitSeconds = waitSeconds, MaxQueue = maxQueue };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class PendingSpeechItem
    {
        public long Id { get; init; }
        public string Text { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public CancellationToken OriginCancellation { get; init; }
    }
}

internal sealed class PendingSpeechConfig
{
    public string Mode { get; set; } = "instant";
    public int WaitSeconds { get; set; } = 3;
    public int MaxQueue { get; set; } = 4;
}

internal sealed class PendingSpeechItemDto
{
    public long Id { get; set; }
    public int Slot { get; set; }
    public string Text { get; set; } = string.Empty;
    public int RemainingSeconds { get; set; }
}

internal sealed class PendingSpeechStatus
{
    public string Mode { get; set; } = "instant";
    public int WaitSeconds { get; set; }
    public int MaxQueue { get; set; }
    public int PendingCount { get; set; }
    public int RemainingSeconds { get; set; }
    public List<PendingSpeechItemDto> Items { get; set; } = [];
}
