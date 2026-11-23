using System.Text.Json;
using MobMate.Core.Asr;

// Linked into both WebHosts. Management is shared; Windows runtime availability
// is separate from catalog/profile editing on Linux.
internal sealed class AddonAsrEndpoints : IDisposable
{
    private readonly AddonLanguageManager _manager;
    private readonly Task _load;
    private readonly AddonOperationGate _workGate;
    private readonly CancellationToken _stopping;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _operation;
    private string? _error;

    public AddonAsrEndpoints(WebApplication app, AddonLanguageManager manager, Task load, AddonOperationGate workGate)
    {
        _manager = manager;
        _load = load;
        _workGate = workGate;
        _stopping = app.Lifetime.ApplicationStopping;
        app.MapGet("/api/addon-asr", (string? language) =>
        {
            try
            {
                var description = manager.Describe(language);
                var installed = manager.Installed;
                return Results.Ok(new
                {
                    supported = true,
                    runtimeSupported = manager.RuntimeSupported,
                    setupAvailable = manager.RuntimeSupported,
                    loading = !load.IsCompleted,
                    operationRunning = _operation is { IsCompleted: false },
                    operationError = _error ?? (load.IsFaulted ? "load_failed" : null),
                    routeAvailable = description.State.Ready && description.State.Enabled,
                    jointAvailable = description.State.Ready && description.State.Enabled,
                    state = description.State,
                    languageState = description,
                    profiles = manager.Profiles,
                    engines = manager.Catalog.Select(e => new
                    {
                        e.Id, e.Name, e.Platform,
                        runtime = new { e.Runtime.Id, e.Runtime.Version, e.Runtime.License, e.Runtime.SourcePageUrl, bytes = e.Runtime.Artifact.Bytes },
                        models = e.Models.Select(m => new
                        {
                            m.Id, m.Name, m.License, m.Languages, m.SourcePageUrl, bytes = m.Artifact.Bytes,
                            installed = installed.Any(p => p.EngineId == e.Id && p.ModelId == m.Id)
                        })
                    })
                });
            }
            catch (ArgumentException) { return Results.BadRequest(new { code = "addon.invalid_language" }); }
        });

        app.MapPost("/api/addon-asr/install", (JsonElement body) => ModelOperation(body, installOnly: true));
        app.MapPost("/api/addon-asr/setup", (JsonElement body) => ModelOperation(body, installOnly: false));
        app.MapPost("/api/addon-asr/profile", (JsonElement body) =>
        {
            if (!TryModel(body, out var engine, out var model) || !TryLanguage(body, out var language))
                return Results.BadRequest(new { code = "addon.invalid_model" });
            var mode = Text(body, "languageMode") ?? "requested";
            return Begin(async token =>
            {
                await manager.ConfigureAsync(language, engine, model, mode, token);
                if (language == manager.Language) await manager.ApplyLanguageAsync(language, token);
            });
        });
        app.MapPost("/api/addon-asr/language", (JsonElement body) =>
        {
            if (!TryLanguage(body, out var language)) return Results.BadRequest(new { code = "addon.invalid_language" });
            return Begin(token => manager.ApplyLanguageAsync(language, token), waitForDrain: true);
        });
        app.MapPost("/api/addon-asr/suspend", () => Begin(token => manager.StopAsync(token), waitForDrain: true));
        app.MapPost("/api/addon-asr/probe", (JsonElement? body) =>
        {
            if (!manager.RuntimeSupported) return Unsupported();
            var language = manager.Language;
            if (body is { } value && !TryLanguage(value, out language)) return Results.BadRequest(new { code = "addon.invalid_language" });
            if (!manager.Describe(language).RuntimeSupported) return Unsupported();
            return Begin(token => manager.ApplyLanguageAsync(language, token, probe: true));
        });
        app.MapPost("/api/addon-asr/enabled", (JsonElement body) =>
        {
            if (!TryLanguage(body, out var language) || !TryBool(body, "enabled", out var enabled))
                return Results.BadRequest(new { code = "addon.invalid_state" });
            return Begin(async token =>
            {
                var old = manager.Describe(language).Desired;
                await manager.SetConsentAsync(language, enabled, enabled && old?.JointRequested == true, token);
            });
        });
        app.MapPost("/api/addon-asr/joint", (JsonElement body) =>
        {
            if (!TryLanguage(body, out var language) || !TryBool(body, "jointRequested", out var requested))
                return Results.BadRequest(new { code = "addon.invalid_state" });
            return Begin(token => manager.SetConsentAsync(language, manager.Describe(language).Desired?.Enabled == true, requested, token));
        });
        app.MapPost("/api/addon-asr/cancel", () =>
        {
            lock (_sync)
            {
                if (_cancellation is null) return Results.Conflict(new { code = "addon.not_running" });
                _cancellation.Cancel();
                return Results.Ok(new { cancelled = true });
            }
        });
    }

    private IResult ModelOperation(JsonElement body, bool installOnly)
    {
        if (!_manager.RuntimeSupported) return Unsupported();
        if (!TryModel(body, out var engine, out var model)) return Results.BadRequest(new { code = "addon.invalid_model" });
        if (!OperatingSystem.IsWindows() && engine != AddonCatalog.QwenId) return Unsupported();
        return Begin(token => installOnly ? _manager.InstallAsync(engine, model, token) : _manager.SetupAsync(engine, model, token));
    }

    private IResult Begin(Func<CancellationToken, Task> work, bool waitForDrain = false)
    {
        lock (_sync)
        {
            if (_cancellation is not null) return Results.Conflict(new { code = "addon.busy" });
            var lease = _workGate.TryEnterAddonOperation();
            if (lease is null && !waitForDrain) return Results.Conflict(new { code = "addon.busy" });
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            _cancellation = cancellation;
            _error = null;
            _operation = Task.Run(async () =>
            {
                try
                {
                    if (lease is null)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        try { lease = await _workGate.EnterAddonOperationAsync(deadline.Token); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                        { throw new TimeoutException("addon_work_drain_timeout"); }
                    }
                    await _load;
                    await work(cancellation.Token);
                }
                catch (OperationCanceledException) { _error = "cancelled"; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _error = ex.GetType().Name; }
                finally
                {
                    lock (_sync)
                    {
                        lease?.Dispose();
                        _cancellation = null;
                    }
                    cancellation.Dispose();
                }
            });
            return Results.Accepted();
        }
    }

    private bool TryModel(JsonElement body, out string engine, out string model)
    {
        engine = Text(body, "engineId") ?? "";
        model = Text(body, "modelId") ?? "";
        var engineId = engine;
        var id = model;
        return _manager.Catalog.FirstOrDefault(e => e.Id == engineId)?.Models.Any(m => m.Id == id) == true;
    }
    private bool TryLanguage(JsonElement body, out string language)
    {
        try { language = AddonLanguageManager.NormalizeLanguage(Text(body, "language") ?? _manager.Language); return true; }
        catch (ArgumentException) { language = ""; return false; }
    }
    private static string? Text(JsonElement body, string name)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryBool(JsonElement body, string name, out bool result)
    {
        result = false;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = value.GetBoolean();
        return true;
    }
    private static IResult Unsupported() => Results.BadRequest(new { code = "addon.unsupported", message = "Windows runtime; language profiles remain editable" });
    public void Dispose() { lock (_sync) _cancellation?.Cancel(); }
}
