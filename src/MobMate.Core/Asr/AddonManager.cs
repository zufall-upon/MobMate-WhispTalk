using System.Text.Json;

namespace MobMate.Core.Asr;

public sealed record AddonState(string? EngineId, string? ModelId, bool Present, bool Installed, bool RuntimeLoaded,
    bool Ready, bool Enabled, bool JointRequested, bool Downloading, string? Error);

/// <summary>Coordinates fixed-catalog packages and one resident adapter without changing existing ASR settings.</summary>
public sealed class AddonManager : IAsyncDisposable
{
    private sealed record Settings(string EngineId, string ModelId, string PackageId, bool Enabled, bool JointRequested);
    private readonly string _root;
    private readonly IAddonPackageInstaller _installer;
    private readonly IReadOnlyList<AddonEngine> _catalog;
    private readonly Func<PreparedAddonPackage, CancellationToken, Task<IAddonRecognizer>>? _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Settings? _settings;
    private IAddonRecognizer? _worker;
    private bool _present;
    private bool _installed;
    private bool _busy;
    private bool _sessionFailed;
    private string? _error;

    public AddonManager(string root, HttpClient http) : this(root, new AddonPackageInstaller(http), AddonCatalog.Engines, null) { }

    internal AddonManager(string root, IAddonPackageInstaller installer, IReadOnlyList<AddonEngine> catalog,
        Func<PreparedAddonPackage, CancellationToken, Task<IAddonRecognizer>>? start)
    {
        _root = Path.GetFullPath(root);
        _installer = installer;
        _catalog = catalog;
        _start = start;
    }

    public AddonState Snapshot()
    {
        lock (_sync)
        {
            if (_worker is not null && !_worker.IsAlive)
            {
                _sessionFailed = true;
                _error ??= "worker_exited";
            }
            var loaded = !_sessionFailed && _worker?.Initialized == true;
            var ready = loaded && _worker?.HasSuccessfulInference == true;
            return new AddonState(_settings?.EngineId, _settings?.ModelId, _present, _installed, loaded, ready,
                _settings?.Enabled == true, _settings?.JointRequested == true, _busy, _error);
        }
    }

    /// <summary>Load settings and reverify files. Loading alone never marks an engine ready.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_worker is not null) throw new InvalidOperationException("Cannot reload while a worker is active");
            var path = Path.Combine(_root, "active.json");
            if (!File.Exists(path)) return;
            lock (_sync) { _present = true; }
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var settings = await JsonSerializer.DeserializeAsync<Settings>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Addon settings are empty");
                var (engine, model, package) = Resolve(settings);
                await _installer.VerifyAsync(_root, engine, model, package, cancellationToken).ConfigureAwait(false);
                lock (_sync) { _settings = settings; _installed = true; _sessionFailed = false; _error = null; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                lock (_sync) { _settings = null; _installed = false; _error = ex.GetType().Name; }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Prepare and probe the chosen catalog model; commit only after a successful inference.</summary>
    public async Task SetupAsync(string engineId, string modelId, CancellationToken cancellationToken = default)
    {
        var engine = _catalog.SingleOrDefault(x => x.Id == engineId) ?? throw new ArgumentException("Unknown addon engine", nameof(engineId));
        var model = engine.Models.SingleOrDefault(x => x.Id == modelId) ?? throw new ArgumentException("Unknown addon model", nameof(modelId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PreparedAddonPackage? prepared = null;
        IAddonRecognizer? candidate = null;
        var oldStopped = false;
        var started = false;
        Settings? previous = null;
        var previousPackages = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(Path.Combine(_root, "packages")))
                previousPackages.UnionWith(Directory.GetDirectories(Path.Combine(_root, "packages")).Select(Path.GetFullPath));
            _ = Snapshot();
            if (_sessionFailed)
                throw new InvalidOperationException("Addon failed during this session");
            if (_installed && _settings?.EngineId == engineId && _settings.ModelId == modelId)
                throw new InvalidOperationException("Selected addon model is already installed");
            lock (_sync) { _busy = true; _error = null; }
            started = true;
            previous = _settings;
            prepared = await _installer.PrepareAsync(_root, engine, model, cancellationToken).ConfigureAwait(false);
            await _installer.VerifyAsync(_root, engine, model, prepared, cancellationToken).ConfigureAwait(false);
            var packageId = Path.GetFileName(prepared.Directory);
            if (!Guid.TryParseExact(packageId, "N", out _)) throw new InvalidDataException("Invalid package identity");
            var sameEngine = previous?.EngineId == engine.Id;
            var next = new Settings(engine.Id, model.Id, packageId,
                sameEngine && previous?.Enabled == true, sameEngine && previous?.JointRequested == true);
            _ = Resolve(next); // Reconstruct paths from the catalog, not persisted executable paths.
            if (_worker is not null)
            {
                var old = _worker;
                lock (_sync) { _worker = null; }
                oldStopped = true;
                await old.DisposeAsync().ConfigureAwait(false);
            }
            candidate = await StartAndProbeAsync(engine, prepared, cancellationToken).ConfigureAwait(false);
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync) { _settings = next; _worker = candidate; _present = true; _installed = true; _sessionFailed = false; _error = null; }
            candidate = null;
            prepared = null;
            // A new engine is not consent to enable it, join Joint, or remove the previous engine's files.
            if (previous is not null && sameEngine)
            {
                try
                {
                    var (oldEngine, oldModel, oldPackage) = Resolve(previous);
                    await _installer.VerifyAsync(_root, oldEngine, oldModel, oldPackage, CancellationToken.None).ConfigureAwait(false);
                    DiscardPreparedPackage(oldPackage, oldEngine, oldModel);
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    // The new settings are already committed. Never roll them back because cleanup failed.
                    lock (_sync) { _error = "cleanup_pending:" + cleanupError.GetType().Name; }
                }
            }
        }
        catch (Exception ex)
        {
            if (!started) throw;
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            if (oldStopped && _settings is not null)
            {
                try
                {
                    var (oldEngine, oldModel, oldPackage) = Resolve(_settings);
                    await _installer.VerifyAsync(_root, oldEngine, oldModel, oldPackage, CancellationToken.None).ConfigureAwait(false);
                    var restored = await StartAndProbeAsync(oldEngine, oldPackage, CancellationToken.None).ConfigureAwait(false);
                    lock (_sync) { _worker = restored; _sessionFailed = false; }
                }
                catch (Exception restoreError) when (restoreError is not OutOfMemoryException)
                {
                    lock (_sync) { _worker = null; _sessionFailed = true; }
                }
            }
            var cleanupFailed = false;
            if (prepared is not null && !previousPackages.Contains(Path.GetFullPath(prepared.Directory)))
            {
                try { DiscardPreparedPackage(prepared, engine, model); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidDataException)
                { cleanupFailed = true; }
            }
            lock (_sync) { _error = ex.GetType().Name + (cleanupFailed ? ":cleanup_pending" : ""); }
            throw;
        }
        finally
        {
            lock (_sync) { _busy = false; }
            _gate.Release();
        }
    }

    /// <summary>Start a previously verified package and run an inference before reporting ready.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_installed || _settings is null) throw new InvalidOperationException("Addon is not installed");
            _ = Snapshot();
            if (_sessionFailed) throw new InvalidOperationException("Addon failed during this session");
            if (_worker?.HasSuccessfulInference == true) return;
            var (engine, model, package) = Resolve(_settings);
            await _installer.VerifyAsync(_root, engine, model, package, cancellationToken).ConfigureAwait(false);
            var worker = await StartAndProbeAsync(engine, package, cancellationToken).ConfigureAwait(false);
            lock (_sync) { _worker = worker; _error = null; }
        }
        catch (Exception ex)
        {
            lock (_sync) { _error = ex.GetType().Name; }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled, bool jointRequested, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings is null) throw new InvalidOperationException("Addon is not ready");
            // Clearing an opt-in before readiness must not activate a disabled addon.
            var clearingJointOnly = !jointRequested && enabled == _settings.Enabled;
            if (enabled && !Snapshot().Ready && !clearingJointOnly) throw new InvalidOperationException("Addon is not ready");
            if (jointRequested && !enabled) throw new InvalidOperationException("Joint participation requires an enabled addon");
            var next = _settings with { Enabled = enabled, JointRequested = jointRequested };
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_sync) { _settings = next; }
        }
        finally { _gate.Release(); }
    }

    public Task<AddonRecognitionResult> RecognizeAsync(byte[] pcm16, int sampleRate, AddonAsrCandidate existing,
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => RecognizeCoreAsync(pcm16, sampleRate, existing, timeout, cancellationToken, null, null, "requested");

    internal Task<AddonRecognitionResult> RecognizeContextAsync(string engineId, string language, string hintMode,
        byte[] pcm16, int sampleRate, AddonAsrCandidate existing, TimeSpan timeout, CancellationToken cancellationToken)
        => RecognizeCoreAsync(pcm16, sampleRate, existing, timeout, cancellationToken, engineId, language, hintMode);

    private async Task<AddonRecognitionResult> RecognizeCoreAsync(byte[] pcm16, int sampleRate, AddonAsrCandidate existing,
        TimeSpan timeout, CancellationToken cancellationToken, string? engineId, string? language, string hintMode)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings?.Enabled != true || _worker is null || !Snapshot().Ready)
                return new AddonRecognitionResult(null, existing, "addon_unavailable");
            if (engineId is not null && (engineId != _settings.EngineId ||
                !Resolve(_settings).Model.Languages.Contains(language) || hintMode is not ("requested" or "auto") ||
                (hintMode == "auto" && engineId != AddonCatalog.QwenId)))
                return new AddonRecognitionResult(null, existing, "addon_context_unavailable");
            try
            {
                var candidate = language is not null && _worker is IContextualAddonRecognizer contextual
                    ? await contextual.TranscribeContextAsync(pcm16, sampleRate, language, hintMode, timeout, cancellationToken).ConfigureAwait(false)
                    : await _worker.TranscribeAsync(pcm16, sampleRate, timeout, cancellationToken).ConfigureAwait(false);
                if (!candidate.Succeeded) throw new InvalidDataException("Addon returned an unsuccessful candidate");
                if (engineId is not null && candidate.Engine != engineId) throw new InvalidDataException("Addon candidate identity mismatch");
                return new AddonRecognitionResult(candidate, candidate, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var failed = _worker;
                lock (_sync) { _worker = null; _sessionFailed = true; _error = ex.GetType().Name; }
                try { await failed.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
                {
                    lock (_sync) { _error += ":dispose_" + disposeError.GetType().Name; }
                }
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                return new AddonRecognitionResult(null, existing, ex.GetType().Name);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IAddonRecognizer> StartAndProbeAsync(AddonEngine engine, PreparedAddonPackage package, CancellationToken token)
    {
        IAddonRecognizer worker = _start is not null ? await _start(package, token).ConfigureAwait(false) :
            engine.Id == AddonCatalog.NemotronId ? await NemoSpeechWorker.StartAsync(package, TimeSpan.FromMinutes(2), token).ConfigureAwait(false) :
            await SpeechWorker.StartAsync(package, engine.Id, engine.Id == AddonCatalog.QwenId ? "ja" : null, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        try
        {
            if (!worker.Initialized) throw new InvalidDataException("Addon runtime did not initialize");
            var probe = await worker.TranscribeAsync(new byte[32000], 16000, TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            if (!probe.Succeeded || !worker.HasSuccessfulInference) throw new InvalidDataException("Addon inference probe failed");
            return worker;
        }
        catch { await worker.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private (AddonEngine Engine, AddonModel Model, PreparedAddonPackage Package) Resolve(Settings settings)
    {
        var engine = _catalog.SingleOrDefault(x => x.Id == settings.EngineId) ?? throw new InvalidDataException("Unknown addon engine");
        var model = engine.Models.SingleOrDefault(x => x.Id == settings.ModelId) ?? throw new InvalidDataException("Unknown addon model");
        if (!Guid.TryParseExact(settings.PackageId, "N", out _)) throw new InvalidDataException("Invalid package identity");
        var directory = Path.Combine(_root, "packages", settings.PackageId);
        return (engine, model, new PreparedAddonPackage(directory, Path.Combine(directory, "runtime", engine.Runtime.Executable), Path.Combine(directory, model.FileName)));
    }

    private async Task SaveAsync(Settings settings, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "active.json");
        var temp = Path.Combine(_root, $".active.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void DiscardPreparedPackage(PreparedAddonPackage package, AddonEngine engine, AddonModel model)
    {
        var directory = Path.GetFullPath(package.Directory);
        AddonPackageInstaller.EnsureNoReparsePoints(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(directory), Path.Combine(_root, "packages"), comparison) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            string.Equals(Path.GetFileName(directory), _settings?.PackageId, comparison))
            throw new InvalidDataException("Refusing to discard an unrelated package");
        if (!Directory.Exists(directory)) return;
        var top = Directory.GetFileSystemEntries(directory);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "runtime", "runtime.zip", model.FileName };
        foreach (var notice in AddonPackageInstaller.GetNotices(engine, model)) allowed.Add(notice.FileName);
        if (top.Any(x => !allowed.Contains(Path.GetFileName(x)) || (File.GetAttributes(x) & FileAttributes.ReparsePoint) != 0) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing to discard a changed package");
        var runtime = Path.Combine(directory, "runtime");
        if (Directory.Exists(runtime))
        {
            AddonPackageInstaller.ValidateRuntimeTree(runtime, engine.Runtime);
            foreach (var relative in engine.Runtime.Files) File.Delete(AddonPackageInstaller.RuntimePath(runtime, relative));
            var parents = engine.Runtime.Files.SelectMany(file => {
                var list = new List<string>();
                for (var slash = file.LastIndexOf('/'); slash > 0; slash = file.LastIndexOf('/', slash - 1)) list.Add(file[..slash]);
                return list;
            }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length);
            foreach (var parent in parents) Directory.Delete(AddonPackageInstaller.RuntimePath(runtime, parent));
            Directory.Delete(runtime);
        }
        foreach (var file in top.Where(x => !string.Equals(x, runtime, comparison))) File.Delete(file);
        Directory.Delete(directory);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var worker = _worker;
            lock (_sync) { _worker = null; }
            if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
