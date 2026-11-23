using System.Text.Json;

namespace MobMate.Core.Asr;

public sealed record AddonLanguageProfile(string EngineId, string ModelId, string? PackageId,
    bool Enabled = false, bool JointRequested = false, string LanguageMode = "requested");

public sealed record AddonInstalledModel(string EngineId, string ModelId, string PackageId);

public sealed record AddonResidentState(string Language, string EngineId, string ModelId, string PackageId,
    bool RuntimeLoaded, bool Ready, bool Active);

public sealed record AddonLanguageState(string Language, AddonLanguageProfile? Desired,
    AddonResidentState? Resident, AddonState State, bool RuntimeSupported, bool JointEligible,
    long Generation, string Quality = "not_evaluated", string JointValue = "not_evaluated");

/// <summary>
/// Language-scoped desired configuration and one additional-ASR resident. Existing
/// Joint engines are not managed here. All inference/switches meet at the same
/// utterance gate; requesting a switch immediately invalidates older results.
/// Installation never selects, enables, starts, or grants Joint consent.
/// </summary>
public sealed class AddonLanguageManager : IAsyncDisposable
{
    private sealed record Document(int SchemaVersion, Dictionary<string, AddonLanguageProfile> Profiles,
        List<AddonInstalledModel> Installed);
    private sealed record LegacySettings(string EngineId, string ModelId, string PackageId, bool Enabled, bool JointRequested);

    private readonly string _root;
    private readonly IAddonPackageInstaller _installer;
    private readonly IReadOnlyList<AddonEngine> _catalog;
    private readonly Func<AddonEngine, PreparedAddonPackage, string, CancellationToken, Task<IAddonRecognizer>>? _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Document _document = new(1, new(StringComparer.Ordinal), new());
    private string _language = "ja";
    private long _generation;
    private int _operations;
    private bool _loaded;
    private bool _readOnly;
    private bool _disposed;
    private Task? _disposeTask;
    private bool _failed;
    private string? _error;
    private IAddonRecognizer? _worker;
    private AddonResidentState? _resident;

    public AddonLanguageManager(string root, HttpClient http)
        : this(root, new AddonPackageInstaller(http), AddonCatalog.Engines, null) { }

    internal AddonLanguageManager(string root, IAddonPackageInstaller installer, IReadOnlyList<AddonEngine> catalog,
        Func<AddonEngine, PreparedAddonPackage, string, CancellationToken, Task<IAddonRecognizer>>? start)
    {
        _root = Path.GetFullPath(root);
        _installer = installer;
        _catalog = catalog;
        _start = start;
    }

    public bool RuntimeSupported => _start is not null || OperatingSystem.IsWindows() || SpeechLinuxRuntime.Available;
    private bool SupportsRuntime(string engineId) => _start is not null || OperatingSystem.IsWindows() ||
        (SpeechLinuxRuntime.Available && engineId == AddonCatalog.QwenId &&
         _catalog.Any(engine => engine.Id == engineId && engine.Platform == "linux-x64" && SpeechLinuxRuntime.Matches(engine.Runtime)));
    public IReadOnlyList<AddonEngine> Catalog => _catalog;
    public string Language { get { lock (_sync) return _language; } }
    public string LanguageMode { get { lock (_sync) return Profile(_language)?.LanguageMode ?? "requested"; } }
    public IReadOnlyDictionary<string, AddonLanguageProfile> Profiles
    { get { lock (_sync) return new Dictionary<string, AddonLanguageProfile>(_document.Profiles); } }
    public IReadOnlyList<AddonInstalledModel> Installed
    { get { lock (_sync) return _document.Installed.ToArray(); } }

    public AddonState Snapshot() => Describe().State;

    public string ResolveRecognitionEngine(string existingEngine, bool useLanguageProfile)
    {
        var state = Describe();
        return useLanguageProfile && state.State.Ready && state.State.Enabled && state.Desired is { } profile
            ? profile.JointRequested ? "joint_auto" : profile.EngineId : existingEngine;
    }

    public AddonLanguageState Describe(string? language = null)
    {
        lock (_sync)
        {
            language = NormalizeLanguage(language ?? _language);
            var desired = Profile(language);
            var installed = desired is null ? null : FindInstalled(desired);
            var matches = desired is not null && _resident is not null && language == _language &&
                _resident.Language == language && _resident.EngineId == desired.EngineId &&
                _resident.ModelId == desired.ModelId && _resident.PackageId == installed?.PackageId;
            var loaded = !_disposed && !_failed && _worker?.Initialized == true && _worker.IsAlive;
            var ready = loaded && _worker?.HasSuccessfulInference == true;
            var activeDesired = Profile(_language);
            var activePackage = activeDesired is null ? null : FindInstalled(activeDesired);
            var actualMatches = _resident is not null && activeDesired is not null &&
                _resident.Language == _language && _resident.EngineId == activeDesired.EngineId &&
                _resident.ModelId == activeDesired.ModelId && _resident.PackageId == activePackage?.PackageId;
            var resident = _resident is null ? null : _resident with
            { RuntimeLoaded = loaded, Ready = ready, Active = actualMatches && ready && activeDesired?.Enabled == true };
            var state = new AddonState(desired?.EngineId, desired?.ModelId, desired is not null || _readOnly,
                installed is not null && Directory.Exists(Package(installed).Directory), matches && loaded,
                matches && ready, desired?.Enabled == true, desired?.JointRequested == true,
                Volatile.Read(ref _operations) > 0, _error);
            return new(language, desired, resident, state, desired is null ? RuntimeSupported : SupportsRuntime(desired.EngineId),
                matches && ready && desired?.Enabled == true && desired.JointRequested && Supports(desired, language),
                Interlocked.Read(ref _generation));
        }
    }

    public async Task LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var path = Path.Combine(_root, "language-profiles.json");
            try
            {
                if (File.Exists(path))
                {
                    var document = await ReadAsync<Document>(path, token).ConfigureAwait(false);
                    Validate(document);
                    lock (_sync) _document = document;
                }
                else if (File.Exists(Path.Combine(_root, "active.json")))
                {
                    // The legacy file remains byte-for-byte intact, including after migration.
                    var old = await ReadAsync<LegacySettings>(Path.Combine(_root, "active.json"), token).ConfigureAwait(false);
                    var profile = new AddonLanguageProfile(old.EngineId, old.ModelId, old.PackageId, old.Enabled, old.JointRequested);
                    ValidateProfile("ja", profile);
                    lock (_sync)
                    {
                        _document.Profiles["ja"] = profile;
                        _document.Installed.Add(new(old.EngineId, old.ModelId, old.PackageId));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                lock (_sync) { _readOnly = true; _error = "settings_invalid:" + ex.GetType().Name; }
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task InstallAsync(string engineId, string modelId, CancellationToken token = default)
    {
        if (!SupportsRuntime(engineId)) throw new PlatformNotSupportedException("addon_runtime_unsupported");
        var (engine, model) = Resolve(engineId, modelId);
        Interlocked.Increment(ref _operations);
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureWritable();
                var existing = _document.Installed.FirstOrDefault(x => x.EngineId == engineId && x.ModelId == modelId);
                if (existing is not null)
                {
                    await _installer.VerifyAsync(_root, engine, model, Package(existing), token).ConfigureAwait(false);
                    return;
                }
                var package = await _installer.PrepareAsync(_root, engine, model, token).ConfigureAwait(false);
                await _installer.VerifyAsync(_root, engine, model, package, token).ConfigureAwait(false);
                var id = Path.GetFileName(package.Directory);
                if (!Guid.TryParseExact(id, "N", out _) || Path.GetFullPath(package.Directory) != Path.Combine(_root, "packages", id))
                    throw new InvalidDataException("Invalid package identity");
                var next = CopyDocument();
                next.Installed.Add(new(engineId, modelId, id));
                await CommitAsync(next, token).ConfigureAwait(false);
                // No cleanup here: another language or the legacy file can still reference an older package.
            }
            finally { _gate.Release(); }
        }
        finally { Interlocked.Decrement(ref _operations); }
    }

    public async Task ConfigureAsync(string language, string engineId, string modelId,
        string languageMode = "requested", CancellationToken token = default)
    {
        language = NormalizeLanguage(language);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            var old = Profile(language);
            var package = _document.Installed.FirstOrDefault(x => x.EngineId == engineId && x.ModelId == modelId);
            var sameEngine = old?.EngineId == engineId;
            var profile = new AddonLanguageProfile(engineId, modelId, package?.PackageId,
                sameEngine && old!.Enabled, sameEngine && old!.JointRequested, languageMode);
            ValidateProfile(language, profile);
            var next = CopyDocument();
            next.Profiles[language] = profile;
            await CommitAsync(next, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled, bool jointRequested, CancellationToken token = default)
        => await SetConsentAsync(Language, enabled, jointRequested, token).ConfigureAwait(false);

    public async Task SetConsentAsync(string language, bool enabled, bool jointRequested, CancellationToken token = default)
    {
        language = NormalizeLanguage(language);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            var old = Profile(language) ?? throw new InvalidOperationException("addon_not_configured");
            if (jointRequested && !enabled) throw new InvalidOperationException("joint_requires_enabled");
            var revokingOnly = (!enabled || old.Enabled) && (!jointRequested || old.JointRequested);
            if (!revokingOnly && !Describe(language).State.Ready) throw new InvalidOperationException("addon_not_ready");
            var next = CopyDocument();
            next.Profiles[language] = old with { Enabled = enabled, JointRequested = jointRequested };
            await CommitAsync(next, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Compatibility entry for old explicit setup clients. The new UI uses install/profile/probe separately.
    public async Task SetupAsync(string engineId, string modelId, CancellationToken token = default)
    {
        await InstallAsync(engineId, modelId, token).ConfigureAwait(false);
        await ConfigureAsync(Language, engineId, modelId, token: token).ConfigureAwait(false);
        await ProbeAsync(token).ConfigureAwait(false);
    }

    public Task ProbeAsync(CancellationToken token = default) => ApplyLanguageAsync(Language, token, probe: true);

    public async Task ApplyLanguageAsync(string language, CancellationToken token = default, bool probe = false)
    {
        language = NormalizeLanguage(language);
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = Interlocked.Increment(ref _generation);
            _language = language;
        }
        Interlocked.Increment(ref _operations);
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureLoaded();
                if (generation != Interlocked.Read(ref _generation)) return;
                var profile = Profile(language);
                var supported = profile is null ? RuntimeSupported : SupportsRuntime(profile.EngineId);
                if (_readOnly || profile is null || (!profile.Enabled && !probe) || !supported)
                {
                    await StopWorkerAsync().ConfigureAwait(false);
                    lock (_sync) _error = _readOnly ? _error : !supported && profile is not null
                        ? "addon_runtime_unsupported" : profile is null ? "language_not_configured" : null;
                    return;
                }
                var installed = FindInstalled(profile);
                if (installed is null) throw new FileNotFoundException("addon_package_not_installed");
                var (engine, model) = Resolve(profile.EngineId, profile.ModelId);
                var package = Package(installed);
                var reuse = !_failed && _worker?.IsAlive == true && _resident is not null &&
                    _resident.EngineId == profile.EngineId && _resident.ModelId == profile.ModelId &&
                    _resident.PackageId == installed.PackageId &&
                    (_resident.Language == language || _worker is IContextualAddonRecognizer);
                if (!reuse)
                {
                    await StopWorkerAsync().ConfigureAwait(false);
                    await _installer.VerifyAsync(_root, engine, model, package, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (generation != Interlocked.Read(ref _generation)) return;
                    var worker = _start is not null ? await _start(engine, package, language, token).ConfigureAwait(false) :
                        engine.Id == AddonCatalog.NemotronId ? await NemoSpeechWorker.StartAsync(package, TimeSpan.FromMinutes(2), token).ConfigureAwait(false) :
                        await SpeechWorker.StartAsync(package, engine.Id, engine.Id == AddonCatalog.QwenId ? language : null,
                            TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                    lock (_sync) { _worker = worker; _failed = false; }
                }
                if (_worker?.Initialized != true) throw new InvalidDataException("addon_runtime_not_initialized");
                if (!reuse || _resident?.Language != language || !_worker.HasSuccessfulInference)
                {
                    var result = await InferAsync(_worker, new byte[32000], 16000, language, profile.LanguageMode,
                        TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    if (!result.Succeeded || result.Engine != profile.EngineId || !_worker.HasSuccessfulInference)
                        throw new InvalidDataException("addon_probe_failed");
                }
                token.ThrowIfCancellationRequested();
                if (generation != Interlocked.Read(ref _generation))
                {
                    await StopWorkerAsync().ConfigureAwait(false);
                    return;
                }
                lock (_sync)
                {
                    _resident = new(language, profile.EngineId, profile.ModelId, installed.PackageId, true, true, profile.Enabled);
                    _failed = false;
                    _error = null;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_sync) { _failed = true; _error = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name; }
                try { await StopWorkerAsync().ConfigureAwait(false); }
                catch (Exception stopError) when (stopError is not OutOfMemoryException)
                { lock (_sync) _error = "stop_failed:" + stopError.GetType().Name; }
                if (ex is OperationCanceledException) throw;
                // Keep desired configuration. The caller can use its existing language-specific route.
            }
            finally { _gate.Release(); }
        }
        finally { Interlocked.Decrement(ref _operations); }
    }

    public Task<AddonRecognitionResult> RecognizeAsync(byte[] pcm16, int sampleRate, AddonAsrCandidate existing,
        TimeSpan timeout, CancellationToken token = default, bool joint = false)
        => RecognizeContextAsync(Snapshot().EngineId ?? "", Language, LanguageMode, pcm16, sampleRate, existing, timeout, token, joint);

    internal async Task<AddonRecognitionResult> RecognizeContextAsync(string engineId, string language, string hintMode,
        byte[] pcm16, int sampleRate, AddonAsrCandidate existing, TimeSpan timeout, CancellationToken token, bool joint = false)
    {
        var generation = Interlocked.Read(ref _generation);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = Describe();
            var profile = state.Desired;
            if (generation != state.Generation || language != state.Language || profile?.EngineId != engineId ||
                !profile.Enabled || !state.State.Ready || (joint && !state.JointEligible) ||
                hintMode != profile.LanguageMode || !Supports(profile, language))
                return new(null, existing, "addon_context_unavailable");
            try
            {
                var result = await InferAsync(_worker!, pcm16, sampleRate, language, hintMode, timeout, token).ConfigureAwait(false);
                if (generation != Interlocked.Read(ref _generation)) return new(null, existing, "obsolete_language_result");
                if (!result.Succeeded || result.Engine != engineId) throw new InvalidDataException("addon_candidate_identity_invalid");
                return new(result, result, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_sync) { _failed = true; _error = ex.GetType().Name; }
                try { await StopWorkerAsync().ConfigureAwait(false); }
                catch (Exception stopError) when (stopError is not OutOfMemoryException)
                { lock (_sync) _error = "stop_failed:" + stopError.GetType().Name; }
                if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
                return new(null, existing, _error);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        long generation;
        lock (_sync) generation = Interlocked.Increment(ref _generation);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (generation == Interlocked.Read(ref _generation))
                await StopWorkerAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task StopWorkerAsync()
    {
        var worker = _worker;
        if (worker is not null)
        {
            // Retain ownership on an uncertain stop; never overlap another resident.
            lock (_sync) _failed = true;
            await worker.DisposeAsync().ConfigureAwait(false);
            if (worker.IsAlive || worker is IConfirmedAddonStop { StopConfirmed: false })
                throw new IOException("addon_worker_stop_unconfirmed");
        }
        lock (_sync) { _worker = null; _resident = null; _failed = false; }
    }

    private static Task<AddonAsrCandidate> InferAsync(IAddonRecognizer worker, byte[] pcm16, int rate,
        string language, string mode, TimeSpan timeout, CancellationToken token)
        => worker is IContextualAddonRecognizer contextual
            ? contextual.TranscribeContextAsync(pcm16, rate, language, mode, timeout, token)
            : worker.TranscribeAsync(pcm16, rate, timeout, token);

    private AddonLanguageProfile? Profile(string language) => _document.Profiles.GetValueOrDefault(language);
    private bool Supports(AddonLanguageProfile profile, string language) => Resolve(profile.EngineId, profile.ModelId).Model.Languages.Contains(language);
    private AddonInstalledModel? FindInstalled(AddonLanguageProfile profile) => _document.Installed.FirstOrDefault(x =>
        x.EngineId == profile.EngineId && x.ModelId == profile.ModelId && (profile.PackageId is null || x.PackageId == profile.PackageId));
    private PreparedAddonPackage Package(AddonInstalledModel installed)
    {
        var (engine, model) = Resolve(installed.EngineId, installed.ModelId);
        var directory = Path.Combine(_root, "packages", installed.PackageId);
        return new(directory, Path.Combine(directory, "runtime", engine.Runtime.Executable), Path.Combine(directory, model.FileName));
    }
    private (AddonEngine Engine, AddonModel Model) Resolve(string engineId, string modelId)
    {
        var engine = _catalog.SingleOrDefault(x => x.Id == engineId) ?? throw new ArgumentException("Unknown addon engine");
        var model = engine.Models.SingleOrDefault(x => x.Id == modelId) ?? throw new ArgumentException("Unknown addon model");
        return (engine, model);
    }
    public static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 20) throw new ArgumentException("Invalid ASR language");
        language = language.Trim().ToLowerInvariant();
        var parts = language.Split('-');
        if (language == "auto" || parts[0].Length is < 2 or > 3 || parts.Any(part => part.Length is < 1 or > 8) ||
            language.Any(c => c is not (>= 'a' and <= 'z') and not '-'))
            throw new ArgumentException("An explicit ASR language is required");
        return language;
    }
    private void ValidateProfile(string language, AddonLanguageProfile profile)
    {
        if (NormalizeLanguage(language) != language || !Supports(profile, language) ||
            (profile.PackageId is not null && !Guid.TryParseExact(profile.PackageId, "N", out _)) ||
            (profile.JointRequested && !profile.Enabled) || profile.LanguageMode is not ("requested" or "auto") ||
            (profile.LanguageMode == "auto" && profile.EngineId != AddonCatalog.QwenId))
            throw new InvalidDataException("Invalid language profile");
    }
    private void Validate(Document document)
    {
        if (document.SchemaVersion != 1 || document.Profiles is null || document.Installed is null ||
            document.Profiles.Count > 100 || document.Installed.Count > 200) throw new InvalidDataException("Unknown addon profile schema");
        foreach (var (language, profile) in document.Profiles) ValidateProfile(language, profile);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var installed in document.Installed)
        {
            _ = Resolve(installed.EngineId, installed.ModelId);
            if (!Guid.TryParseExact(installed.PackageId, "N", out _) || !keys.Add(installed.EngineId + "/" + installed.ModelId))
                throw new InvalidDataException("Invalid installed package registry");
        }
    }
    private Document CopyDocument()
    { lock (_sync) return new(1, new(_document.Profiles, StringComparer.Ordinal), new(_document.Installed)); }
    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded) throw new InvalidOperationException("addon_settings_not_loaded");
    }
    private void EnsureWritable()
    {
        EnsureLoaded();
        if (_readOnly) throw new InvalidOperationException("addon_settings_invalid_preserved");
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        AddonPackageInstaller.EnsureNoReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > 256 * 1024) throw new InvalidDataException("Addon settings exceed limit");
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty addon settings");
    }
    private async Task CommitAsync(Document document, CancellationToken token)
    {
        Validate(document);
        AddonPackageInstaller.EnsureNoReparsePoints(_root);
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, $".language-profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, document, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, Path.Combine(_root, "language-profiles.json"), overwrite: true);
            lock (_sync) _document = document;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            // Shutdown is terminal, unlike Stop: a later language request must
            // never supersede it while we drain an existing utterance.
            _disposed = true;
            Interlocked.Increment(ref _generation);
            _disposeTask = DisposeWorkerAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeWorkerAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopWorkerAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
