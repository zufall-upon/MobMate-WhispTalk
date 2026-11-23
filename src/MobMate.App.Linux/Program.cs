// MobMate Linux WebHost — Phase 4
// Minimal but functional: TTS, audio devices, virtual mic setup, static web UI.

using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MobMate.Core;
using MobMate.Core.Asr;
using MobMate.Core.Bench;
using MobMate.Core.Native;
using MobMate.Core.Realtime;
using MobMate.Core.Text;
using MobMate.Core.Tts;
using MobMate.Core.VoiceConversion;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// ---- App root / native path ----
var appRoot = AppContext.BaseDirectory;

// ---- Subtitle window process tracking ----
System.Diagnostics.Process? _subtitleProcess = null;
// On Linux the native binary is 'mobmate-native' (no extension)
var nativeExe = Path.Combine(appRoot, "mobmate-native");
// Fallback: look in cmake build output alongside this binary
if (!File.Exists(nativeExe))
    nativeExe = Path.Combine(appRoot, "mobmate_native_core", "mobmate-native");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();
string? selectedBaseUrl = null;
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var realtimeOutputEpoch = new RealtimeOutputEpoch();
app.Lifetime.ApplicationStopping.Register(realtimeOutputEpoch.Dispose);
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var steamAchievements = new SteamAchievementService(appRoot, loggerFactory.CreateLogger("SteamAchievement"));
void UnlockSteamAchievement(string achievementId) => _ = Task.Run(() => steamAchievements.Unlock(achievementId));
app.Lifetime.ApplicationStopping.Register(() => steamAchievements.Dispose());
var adaptiveVocabLogger = loggerFactory.CreateLogger("AdaptiveVocab");
var adaptiveVocabOptions = AppConfigLoader.Load(Path.Combine(appRoot, "mobmate.config.json")).AdaptiveVocab ?? new AdaptiveVocabOptions();
var adaptiveVocabStore = new AdaptiveVocabStore(Path.Combine(AppContext.BaseDirectory, "app", "adaptive_vocab.json"), adaptiveVocabOptions);
var adaptivePromptBuilder = new AdaptivePromptBuilder(adaptiveVocabStore, adaptiveVocabOptions);
object adaptiveRmsLock = new();
double adaptiveRmsAverage = 0.0;
int adaptiveRmsCount = 0;
string lastAdaptiveVocabLearnedTextKey = string.Empty;
DateTimeOffset lastAdaptiveVocabLearnedAt = DateTimeOffset.MinValue;
// Re-speak detection state
string lastAdaptiveFinalText = string.Empty;
string lastAdaptiveFinalIpa = string.Empty;
DateTimeOffset lastAdaptiveFinalAt = DateTimeOffset.MinValue;
string currentAdaptiveIpa = string.Empty;
logger.LogInformation("MobMate Linux Phase 4 starting. appRoot={AppRoot}", appRoot);
logger.LogInformation("[Environment] appVersion={AppVersion} osDescription={OsDescription} osVersion={OsVersion} framework={Framework} osArch={OsArch} processArch={ProcessArch} cpuCount={CpuCount} process64Bit={Process64Bit}",
    MobMateCore.Version, RuntimeInformation.OSDescription, Environment.OSVersion.VersionString,
    RuntimeInformation.FrameworkDescription, RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture,
    Environment.ProcessorCount, Environment.Is64BitProcess);
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    logger.LogCritical(eventArgs.ExceptionObject as Exception, "[Process] unhandled exception terminating={Terminating}", eventArgs.IsTerminating);
TaskScheduler.UnobservedTaskException += (_, eventArgs) => logger.LogError(eventArgs.Exception, "[Process] unobserved task exception");
AppDomain.CurrentDomain.ProcessExit += (_, _) => logger.LogInformation("[Process] ProcessExit received exitCode={ExitCode}", Environment.ExitCode);
app.Lifetime.ApplicationStopping.Register(() => logger.LogInformation("[Process] ApplicationStopping received"));

AppUserPaths.EnsureV2Directories();
var uiSettingsPath = Path.Combine(AppUserPaths.ConfigDir, "ui-settings.json");
var uiSettingsStore = new VersionedJsonStore<UiSettingsDocument>(
    uiSettingsPath,
    UiSettingsDocument.CurrentSchemaVersion,
    () => new UiSettingsDocument());
var uiSettingsLock = new SemaphoreSlim(1, 1);
var uiSettingsLoad = await uiSettingsStore.LoadAsync();
var uiSettingsDocument = UiSettingsDocument.Normalize(uiSettingsLoad.Value);
Action<UiSettingsDocument, UiSettingsDocument>? mobfyOnsetSettingsChanged = null;
var uiSettingsMigrationResult = uiSettingsLoad.FailSoftReason?.ToString()
    ?? (uiSettingsDocument.BrowserMigrationCompleted ? "completed" : "pending");
logger.LogInformation(
    "[UiSettings] schema={Schema} path={Path} wizardReason={WizardReason} migrationResult={MigrationResult} migrationCount={MigrationCount}",
    uiSettingsLoad.SchemaVersion, uiSettingsPath, uiSettingsDocument.ResolveWizardReason(),
    uiSettingsMigrationResult, uiSettingsDocument.Settings.Count);

// ---- Native bridge: register DLL import resolver for .so ----
NativeLibraryBridge.SetNativePath(nativeExe);
var nativeDllPath = NativeLibraryBridge.ResolveDllPath(nativeExe);
if (File.Exists(nativeDllPath))
    logger.LogInformation("Native bridge: {Path}", nativeDllPath);
else
    logger.LogWarning("Native bridge not found: {Path}", nativeDllPath);

// Shared language-scoped management. Windows-only workers stay unavailable on Linux.
var addonWorkGate = new AddonOperationGate();
var addonHttp = new HttpClient();
var addonManager = new AddonLanguageManager(Path.Combine(AppUserPaths.V2Root, "asr-addons"), addonHttp);
var addonTranscriber = new AddonNativeTranscriber(addonManager, app.Lifetime.ApplicationStopping, addonWorkGate);
var addonLoad = Task.Run(() => addonManager.LoadAsync(app.Lifetime.ApplicationStopping));
var addonEndpoints = new AddonAsrEndpoints(app, addonManager, addonLoad, addonWorkGate);
app.Lifetime.ApplicationStopping.Register(() =>
{
    addonEndpoints.Dispose();
    try { addonManager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch (Exception ex) { logger.LogWarning(ex, "[AddonAsr] shutdown failed"); }
    addonHttp.Dispose();
});

// ---- TTS setup ----
// Primary: NullPrimaryTtsService on Linux (SAPI unavailable)
IPrimaryTtsService primaryTts = new NullPrimaryTtsService();

// Piper executable: look in libs/piper_plus/ or PATH
string ResolvePiperExe()
{
    var fromConfig = Environment.GetEnvironmentVariable("MOBMATE_PIPER_EXE");
    if (!string.IsNullOrEmpty(fromConfig) && File.Exists(fromConfig)) return fromConfig;
    // Prefer piper_wrapper.sh alongside piper binary.
    // piper 1.2.0 reads text from stdin only; the wrapper converts --text → stdin
    // and normalizes hyphen-style prosody args (--noise-scale → --noise_scale).
    var wrapper = Path.Combine(appRoot, "libs", "piper_plus", "piper_wrapper.sh");
    if (File.Exists(wrapper)) return wrapper;
    var bundled = Path.Combine(appRoot, "libs", "piper_plus", "piper");
    if (File.Exists(bundled)) return bundled;
    return "piper"; // rely on PATH
}

string ResolvePiperPlusExe()
{
    var fromConfig = Environment.GetEnvironmentVariable("MOBMATE_PIPER_PLUS_EXE");
    if (!string.IsNullOrEmpty(fromConfig) && File.Exists(fromConfig)) return fromConfig;
    var bundled = Path.Combine(appRoot, "libs", "piper_plus_cli", "PiperPlus.Cli");
    if (File.Exists(bundled)) return bundled;
    var legacy = Path.Combine(appRoot, "libs", "piper_plus", "PiperPlus.Cli");
    if (File.Exists(legacy)) return legacy;
    return ResolvePiperExe();
}

string ResolvePiperExeForEngine(string? engine)
    => string.Equals(engine, "piper_plus", StringComparison.OrdinalIgnoreCase) ? ResolvePiperPlusExe() : ResolvePiperExe();

string ResolvePactlExe()
{
    var fromConfig = Environment.GetEnvironmentVariable("MOBMATE_PACTL_EXE");
    if (!string.IsNullOrEmpty(fromConfig) && File.Exists(fromConfig)) return fromConfig;
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(dir, "pactl");
        if (File.Exists(candidate)) return candidate;
    }
    foreach (var candidate in new[] { "/usr/bin/pactl", "/bin/pactl", "/run/host/usr/bin/pactl" })
    {
        if (File.Exists(candidate)) return candidate;
    }
    var bundled = Path.Combine(appRoot, "libs", "pactl", "linux-x64", "pactl");
    if (File.Exists(bundled)) return bundled;
    return "pactl";
}

System.Diagnostics.ProcessStartInfo NewPactlStartInfo(string arguments, bool redirectStandardOutput = false, bool redirectStandardError = false)
{
    var pactlExe = ResolvePactlExe();
    var psi = new System.Diagnostics.ProcessStartInfo(pactlExe, arguments)
    {
        RedirectStandardOutput = redirectStandardOutput,
        RedirectStandardError = redirectStandardError,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    ApplyPulseToolEnvironment(psi);
    return psi;
}

void ApplyPulseToolEnvironment(System.Diagnostics.ProcessStartInfo psi)
{
    psi.Environment.Remove("LD_PRELOAD");
    var existingLdPath = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : string.Empty;
    var bundledPactlLibDir = Path.Combine(appRoot, "libs", "pactl", "linux-x64");
    var useBundledPactlLibs = Path.GetFullPath(psi.FileName).StartsWith(Path.GetFullPath(bundledPactlLibDir), StringComparison.Ordinal);
    psi.Environment["LD_LIBRARY_PATH"] = string.Join(':', new[]
    {
        useBundledPactlLibs ? bundledPactlLibDir : null,
        "/run/host/usr/lib/x86_64-linux-gnu/pulseaudio",
        "/run/host/usr/lib/x86_64-linux-gnu",
        "/run/host/usr/lib64/pulseaudio",
        "/run/host/usr/lib64",
        "/run/host/usr/lib/pulseaudio",
        "/run/host/usr/lib",
        "/run/host/lib64",
        "/usr/lib/x86_64-linux-gnu/pulseaudio",
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib64/pulseaudio",
        "/usr/lib64",
        "/usr/lib/pulseaudio",
        "/usr/lib",
        existingLdPath
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

// Returns candidate1 if it exists, otherwise candidate2 (repo-root fallback pattern).
string ResolveDirWithRepoFallback(string candidate1, string candidate2)
    => Directory.Exists(candidate1) ? candidate1 : candidate2;

// Resolves the piper voice model root with three-tier fallback:
// 1. appRoot/models/piper_plus (tarball or in-place install with bundled models)
// 2. repo root (dev mode: bin/Debug/net8.0 is 5 levels below repo root)
// 3. user config dir (tarball without bundled models; persists across updates)
string ResolveVoiceModelRoot()
{
    var inApp = Path.Combine(appRoot, "models", "piper_plus");
    if (Directory.Exists(inApp)) return inApp;
    var repoRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "piper_plus"));
    if (Directory.Exists(repoRoot)) return repoRoot;
    return Path.Combine(AppUserPaths.ConfigDir, "piper_models");
}

string ResolveSileroVadModelPath()
{
    const string modelRelPath = "silero_vad/silero_vad_int8.onnx";
    var modelsRoot = Environment.GetEnvironmentVariable("MOBMATE_MODELS_ROOT");
    if (!string.IsNullOrEmpty(modelsRoot))
    {
        var fromEnv = Path.Combine(modelsRoot, modelRelPath);
        if (File.Exists(fromEnv)) return fromEnv;
    }
    var inApp = Path.Combine(appRoot, "models", modelRelPath);
    if (File.Exists(inApp)) return inApp;
    var repoRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", modelRelPath));
    return File.Exists(repoRoot) ? repoRoot : string.Empty;
}

string ResolveOnnxRuntimeDllDir()
{
    var dirs = new[]
    {
        Path.Combine(appRoot, "libs", "sherpa-onnx"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "sherpa-onnx")),
    };
    return dirs.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "libonnxruntime.so"))) ?? string.Empty;
}

string ResolvePiperModel()
{
    var fromConfig = Environment.GetEnvironmentVariable("MOBMATE_PIPER_MODEL");
    if (!string.IsNullOrEmpty(fromConfig) && File.Exists(fromConfig)) return fromConfig;
    // Search libs/piper_models/ for *.onnx
    var modelsDir = Path.Combine(appRoot, "libs", "piper_models");
    if (Directory.Exists(modelsDir))
    {
        var first = Directory.EnumerateFiles(modelsDir, "*.onnx", SearchOption.AllDirectories).FirstOrDefault();
        if (first != null) return first;
    }
    return string.Empty;
}

IEnumerable<(string Id, string Name, string ModelPath, string ConfigPath, string Language, string License, string SourcePageUrl, string Runtime)> EnumeratePiperPlusModels()
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 1. Flat *.onnx files in libs/piper_models/ (legacy / bundled)
    var flatRoots = new[]
    {
        Path.Combine(appRoot, "libs", "piper_models"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "piper_models"))
    };
    foreach (var flatRoot in flatRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        foreach (var onnx in Directory.EnumerateFiles(flatRoot, "*.onnx", SearchOption.TopDirectoryOnly).OrderBy(x => x))
        {
            var id = Path.GetFileNameWithoutExtension(onnx);
            if (!seen.Add(id)) continue;
            var configPath = onnx + ".json";
            var language = id.StartsWith("en_", StringComparison.OrdinalIgnoreCase) ? "en" : string.Empty;
            yield return (id, id, onnx, File.Exists(configPath) ? configPath : string.Empty, language, string.Empty, string.Empty, "piper");
        }
    }

    // 2. Subdirectory-style models/piper_plus/<id>/ (downloaded via DL manager, same as WebHost)
    var subdirRoots = new[]
    {
        Path.Combine(appRoot, "models", "piper_plus"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "piper_plus")),
        Path.Combine(AppUserPaths.ConfigDir, "piper_models")
    };
    foreach (var subdirRoot in subdirRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        foreach (var dir in Directory.EnumerateDirectories(subdirRoot).OrderBy(Path.GetFileName))
        {
            var onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Contains(".cpu.opt.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x).FirstOrDefault();
            if (string.IsNullOrEmpty(onnx)) continue;

            var props = ReadProperties(Path.Combine(dir, "mobmate-model.properties"));
            var folder = Path.GetFileName(dir);
            var id = props.TryGetValue("id", out var idVal) && !string.IsNullOrWhiteSpace(idVal) ? idVal : folder;
            if (!seen.Add(id)) continue;
            var display = props.TryGetValue("display_name", out var displayVal) && !string.IsNullOrWhiteSpace(displayVal) ? displayVal : id;
            var cfgPath = Directory.GetFiles(dir, "*.onnx.json", SearchOption.TopDirectoryOnly).OrderBy(x => x).FirstOrDefault()
                ?? Path.Combine(dir, "config.json");
            yield return (
                id, display, onnx,
                File.Exists(cfgPath) ? cfgPath : string.Empty,
                props.TryGetValue("language", out var lang) ? lang : string.Empty,
                props.TryGetValue("license", out var lic) ? lic : string.Empty,
                props.TryGetValue("source_page_url", out var src) ? src : string.Empty,
                InferPiperRuntime(props, id));
        }
    }
}

Dictionary<string, string> ReadProperties(string path)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return values;
    foreach (var rawLine in File.ReadLines(path, System.Text.Encoding.UTF8))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
        var sep = line.IndexOf('=');
        if (sep <= 0) continue;
        values[line[..sep].Trim()] = line[(sep + 1)..].Trim().Replace("\\:", ":");
    }
    return values;
}

string InferPiperRuntime(Dictionary<string, string> props, string id)
{
    if (props.TryGetValue("runtime", out var runtime) && !string.IsNullOrWhiteSpace(runtime)) return runtime;
    var joined = string.Join(" ", new[]
    {
        id,
        props.TryGetValue("source_page_url", out var source) ? source : string.Empty,
        props.TryGetValue("model_url", out var modelUrl) ? modelUrl : string.Empty,
        props.TryGetValue("text_mode_language_tag", out var languageTag) ? languageTag : string.Empty
    });
    return joined.Contains("piper-plus", StringComparison.OrdinalIgnoreCase)
        || joined.Contains("tsukuyomi", StringComparison.OrdinalIgnoreCase)
        || joined.Contains("css10", StringComparison.OrdinalIgnoreCase)
        ? "piper_plus"
        : "piper";
}

string? ResolvePiperPlusModelPathById(string? modelId)
{
    if (string.IsNullOrWhiteSpace(modelId)) return null;
    foreach (var model in EnumeratePiperPlusModels())
    {
        if (string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)) return model.ModelPath;
    }
    return null;
}

string? ResolvePiperRuntimeById(string? modelId)
{
    if (string.IsNullOrWhiteSpace(modelId)) return null;
    foreach (var model in EnumeratePiperPlusModels())
    {
        if (string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)) return model.Runtime;
    }
    return null;
}

var piperExe = ResolvePiperExe();
var piperModel = ResolvePiperModel();
var piperService = new PiperPlusTtsService(piperExe, string.IsNullOrEmpty(piperModel) ? null : piperModel);
logger.LogInformation("Piper exe: {Exe}, model: {Model}", piperExe,
    string.IsNullOrEmpty(piperModel) ? "(none)" : piperModel);

// Piper voice catalog + download manager
var piperVoiceCatalogPath = Path.Combine(appRoot, "libs", "preset", "piper_voice_catalog.json");
var piperVoiceCatalogCachePath = Path.Combine(AppUserPaths.ConfigDir, "piper_voice_catalog.json");
var piperVoiceCatalogRemoteUrl = Environment.GetEnvironmentVariable("MOBMATE_PIPER_CATALOG_URL") ?? string.Empty;
var piperVoiceModelRoot = ResolveVoiceModelRoot();
logger.LogInformation("Piper voice model root: {Root}", piperVoiceModelRoot);
var piperVoiceDlManager = new MobMate.Core.Tts.PiperVoiceDownloadManager();
var piperVoiceCatalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
app.Lifetime.ApplicationStopping.Register(() => piperVoiceDlManager.Dispose());
app.Lifetime.ApplicationStopping.Register(() => piperVoiceCatalogHttp.Dispose());

var richVoiceCatalogPath = Path.Combine(appRoot, "libs", "preset", "rich_tts_catalog.json");
// MOBMATE_MODELS_ROOT allows persistent model storage outside the publish dir (useful in dev).
var _modelsBase = Environment.GetEnvironmentVariable("MOBMATE_MODELS_ROOT");
var modelsBase = string.IsNullOrWhiteSpace(_modelsBase) ? Path.Combine(appRoot, "models") : _modelsBase;
var kokoroModelRoot = Path.Combine(modelsBase, "kokoro");
var mossModelRoot   = Path.Combine(modelsBase, "moss-tts");

// Voice Conversion Phase 1: model-management only. No audio conversion is started here.
var voiceConversionCatalogPath = Path.Combine(appRoot, "libs", "preset", "voiceconv_catalog.json");
var voiceConversionModelRoot = Path.Combine(appRoot, "models", "voiceconv");
var voiceConversionLogger = loggerFactory.CreateLogger("VoiceConversion");
var voiceConversionDlManager = new VoiceConversionModelDownloadManager(voiceConversionLogger);
var voiceConversionAutoDetected = VoiceConversionModelDownloadManager.ScanAndNormalizeInstalledModels(voiceConversionModelRoot, voiceConversionLogger);
voiceConversionLogger.LogInformation("[VC] auto-detected installed models: {Count}", voiceConversionAutoDetected);
app.Lifetime.ApplicationStarted.Register(() =>
{
    var count = VoiceConversionModelDownloadManager.ScanAndNormalizeInstalledModels(voiceConversionModelRoot, voiceConversionLogger);
    voiceConversionLogger.LogInformation("[VC] startup rescan installed models: {Count}", count);
});
app.Lifetime.ApplicationStopping.Register(() => voiceConversionDlManager.Dispose());
object voiceConversionSessionLock = new();
IntPtr voiceConversionSessionHandle = IntPtr.Zero;
long voiceConversionOperationId = 0;
app.Lifetime.ApplicationStopping.Register(() =>
{
    lock (voiceConversionSessionLock)
    {
        NativeLibraryBridge.VoiceConversionDestroy(voiceConversionSessionHandle);
        voiceConversionSessionHandle = IntPtr.Zero;
    }
});

// ── Translation service ──────────────────────────────────────
var translationService = new TranslationService();
var translationLogger  = loggerFactory.CreateLogger("Translation");
string TranslationConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "translation.json");
TranslationConfig LoadTranslationConfig()
{
    var path = TranslationConfigPath();
    if (!File.Exists(path)) return new TranslationConfig();
    try
    {
        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return System.Text.Json.JsonSerializer.Deserialize<TranslationConfig>(json) ?? new TranslationConfig();
    }
    catch { return new TranslationConfig(); }
}
void SaveTranslationConfig(TranslationConfig cfg)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(TranslationConfigPath(), json, System.Text.Encoding.UTF8);
}
_ = Task.Run(() => translationService.Load(appRoot, translationLogger));

// ── VRChat OSC service ───────────────────────────────────────
var vrchatOscLogger  = loggerFactory.CreateLogger("VrchatOsc");
var vrchatOscService = new VrchatOscService(vrchatOscLogger);
string VrchatOscConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "vrchat_osc.json");
VrchatOscConfig LoadVrchatOscConfig()
{
    var path = VrchatOscConfigPath();
    if (!File.Exists(path)) return new VrchatOscConfig();
    try
    {
        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return System.Text.Json.JsonSerializer.Deserialize<VrchatOscConfig>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new VrchatOscConfig();
    }
    catch { return new VrchatOscConfig(); }
}
void SaveVrchatOscConfig(VrchatOscConfig cfg)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(VrchatOscConfigPath(), json, System.Text.Encoding.UTF8);
}
vrchatOscService.SetConfig(LoadVrchatOscConfig());
app.Lifetime.ApplicationStopping.Register(() => vrchatOscService.Dispose());

// ── Hearing API state (Linux capture backend is a later step) ───────────────
var hearingLogger = loggerFactory.CreateLogger("Hearing");
string HearingConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "hearing.json");
LinuxHearingConfig LoadHearingConfig()
{
    var path = HearingConfigPath();
    if (!File.Exists(path)) return new LinuxHearingConfig();
    try
    {
        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return System.Text.Json.JsonSerializer.Deserialize<LinuxHearingConfig>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LinuxHearingConfig();
    }
    catch (Exception ex)
    {
        hearingLogger.LogWarning(ex, "[Hearing] config load failed");
        return new LinuxHearingConfig();
    }
}
void SaveHearingConfig(LinuxHearingConfig cfg)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(HearingConfigPath(), json, System.Text.Encoding.UTF8);
}
var hearingConfig = LoadHearingConfig();
LinuxHearingService? hearingService = null;

// ── Pending Speech service ───────────────────────────────────
var pendingSpeechLogger  = loggerFactory.CreateLogger("PendingSpeech");
var pendingSpeechService = new PendingSpeechService(appRoot, pendingSpeechLogger);
app.Lifetime.ApplicationStopping.Register(pendingSpeechService.Dispose);
// NOTE: OnTimeoutSpeakAsync is set after SpeakWithOutputRoutingAsync is defined (below)
var kokoroService = new MobMate.Core.Tts.KokoroTtsService();
var kokoroDlManager = new MobMate.Core.Tts.KokoroModelDownloadManager();
var mossService = new MobMate.Core.Tts.MossTtsService();
var mossDlManager = new MobMate.Core.Tts.MossModelDownloadManager();
app.Lifetime.ApplicationStopping.Register(() => { kokoroService.Dispose(); kokoroDlManager.Dispose(); });
app.Lifetime.ApplicationStopping.Register(() => { mossService.Dispose(); mossDlManager.Dispose(); });

const string LinuxVoicevoxApi = "http://127.0.0.1:50021";
const string LinuxVoicevoxVersion = "0.25.2";
const string LinuxVoicevoxOfficialSiteUrl = "https://voicevox.hiroshiba.jp/";
const string LinuxVcClientOfficialSiteUrl = "https://github.com/w-okada/voice-changer";
const string LinuxVoicevoxArchiveName = "voicevox-linux-cpu-x64-0.25.2.tar.gz";
const string LinuxVoicevoxDownloadUrl = "https://github.com/VOICEVOX/voicevox/releases/download/0.25.2/voicevox-linux-cpu-x64-0.25.2.tar.gz";
var voicevoxInstallDir = Path.Combine(AppUserPaths.ConfigDir, "voicevox");
var voicevoxArchivePath = Path.Combine(voicevoxInstallDir, LinuxVoicevoxArchiveName);
var voicevoxExtractDir = Path.Combine(voicevoxInstallDir, "engine-" + LinuxVoicevoxVersion);
var voicevoxRunPath = Path.Combine(voicevoxExtractDir, "VOICEVOX", "vv-engine", "run");
var voicevoxDownloadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
object voicevoxInstallLock = new object();
bool voicevoxInstallRunning = false;
long voicevoxInstallBytesReceived = 0;
long? voicevoxInstallTotalBytes = null;
string? voicevoxInstallError = null;
string voicevoxInstallPhase = "idle";
DateTimeOffset? voicevoxInstallStartedAt = null;
DateTimeOffset? voicevoxInstallFinishedAt = null;
System.Diagnostics.Process? voicevoxProcess = null;
app.Lifetime.ApplicationStopping.Register(() => voicevoxDownloadHttp.Dispose());
app.Lifetime.ApplicationStopping.Register(() => { try { if (voicevoxProcess?.HasExited == false) voicevoxProcess.Kill(entireProcessTree: true); } catch { } });

var ttsPlayback = new TtsPlaybackService(nativeExe, loggerFactory.CreateLogger("TtsPlayback"));
var voicevoxService = new VoicevoxTtsService(LinuxVoicevoxApi, 3, true);
var wavOutputStore = new TtsWavOutputStore(Path.Combine(AppUserPaths.V2Root, "wav-output"), loggerFactory.CreateLogger("WavOutput"));

var ttsRouter = new TtsEngineRouter(
    primaryTts,
    ttsPlayback,
    voicevoxService: voicevoxService,
    piperService: piperService,
    kokoroService: kokoroService,
    mossService: mossService,
    wavOutputStore: wavOutputStore);

// Default engine on Linux: piper_plus — keep a local config because NullPrimaryTtsService does not persist it.
var linuxTtsConfig = new TtsConfig
{
    Engine = "piper",
    VoicevoxApi = LinuxVoicevoxApi,
    VoicevoxSpeakerId = "3",
    VoicevoxAutoEmotion = true,
    PiperPlusExePath = piperExe,
    PiperPlusModelId = EnumeratePiperPlusModels().FirstOrDefault(m => string.Equals(m.ModelPath, piperModel, StringComparison.OrdinalIgnoreCase)).Id ?? string.Empty,
    PiperPlusModelPath = string.IsNullOrEmpty(piperModel) ? "" : piperModel
};
ttsRouter.SetConfig(linuxTtsConfig);
logger.LogInformation("TTS engine default: piper");
var mobfyOnset = new MobfyOnsetFiller(ttsRouter, nativeExe, voiceConversionSessionLock,
    () => voiceConversionSessionHandle, voiceConversionLogger);
var mobfyOnsetPreference = MobfyOnsetSelection.Enabled(uiSettingsDocument);
var mobfyOnsetLanguage = MobfyOnsetSelection.Language(uiSettingsDocument);
long mobfyOnsetControlGeneration = 0;
void PrepareMobfyOnset(string? language = null)
{
    if (!MobfyOnsetSelection.IsSelected(uiSettingsDocument)) { mobfyOnset.Invalidate(); return; }
    if (language != null) mobfyOnsetLanguage = language;
    var selectedLanguage = mobfyOnsetLanguage;
    ttsRouter.PrepareOnset(selectedLanguage);
    mobfyOnset.Configure(ResolveVoiceConversionModelOptionsJson(
        MobfyOnsetSelection.Options(uiSettingsDocument), ensureAudibleLiveOutput: true),
        selectedLanguage, Volatile.Read(ref mobfyOnsetPreference));
}
app.Lifetime.ApplicationStopping.Register(mobfyOnset.Dispose);

void WarmUpMossIfSelected(TtsConfig config)
{
    if (!string.Equals(config.Engine, "moss", StringComparison.OrdinalIgnoreCase)) return;
    _ = mossService.WarmUpAsync()
        .ContinueWith(t =>
        {
            if (t.IsFaulted) logger.LogDebug(t.Exception?.GetBaseException(), "[TTS] MOSS warmup failed");
            else logger.LogDebug("[TTS] MOSS warmup result={Ok}", t.Result);
        });
}

// ---- SenseVoice ASR setup (Linux: sherpa-onnx .so) ----
void TryLoadSherpaOnnxLinux()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "libs", "sherpa-onnx"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "sherpa-onnx"))
    };

    foreach (var dir in candidates.Where(Directory.Exists))
    {
        var onnx = Path.Combine(dir, "libonnxruntime.so");
        var sherpa = Path.Combine(dir, "libsherpa-onnx-c-api.so");
        if (!File.Exists(onnx) || !File.Exists(sherpa)) continue;
        try
        {
            NativeLibrary.Load(onnx);
            var sherpaHandle = NativeLibrary.Load(sherpa);
            NativeLibrary.SetDllImportResolver(
                typeof(SherpaOnnx.OfflineRecognizer).Assembly,
                (name, _, _) => name == "sherpa-onnx-c-api" ? sherpaHandle : IntPtr.Zero);
            logger.LogInformation("[SenseVoice] native loaded: {Dir}", dir);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SenseVoice] native load failed: {Dir}", dir);
        }
    }
    logger.LogWarning("[SenseVoice] Linux native runtime not found");
}

static float[] PcmToFloat32(byte[] pcm16le)
{
    var samples = new float[pcm16le.Length / 2];
    for (int i = 0; i < samples.Length; i++)
        samples[i] = BitConverter.ToInt16(pcm16le, i * 2) / 32768.0f;
    return samples;
}

static bool HasSpeakableText(string text) => text.Any(char.IsLetterOrDigit);

static PhoneticAssistLanguage ResolvePhoneticAssistLanguage(string? lang)
{
    if (lang != null && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.Chinese;
    if (lang != null && lang.Equals("ko", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.Korean;
    if (lang != null && lang.Equals("en", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.English;
    return PhoneticAssistLanguage.Japanese;
}

TryLoadSherpaOnnxLinux();
var senseVoiceLogger = loggerFactory.CreateLogger("SenseVoice");
var paraformerLogger = loggerFactory.CreateLogger("ParaformerZh");
string ResolveSenseVoiceModelDir()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "models", "sense_voice"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "sense_voice")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "models", "sense_voice")
    };
    foreach (var dir in candidates)
    {
        if (File.Exists(Path.Combine(dir, "model.int8.onnx")) &&
            File.Exists(Path.Combine(dir, "tokens.txt")))
        {
            logger.LogInformation("[SenseVoice] model dir: {Dir}", dir);
            return dir;
        }
    }
    logger.LogWarning("[SenseVoice] model dir not found. tried: {Dirs}", string.Join(";", candidates));
    return candidates[0];
}

string ResolveParaformerZhModelDir()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "models", "paraformer_zh"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "paraformer_zh")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "models", "paraformer_zh")
    };
    foreach (var dir in candidates)
    {
        if (File.Exists(Path.Combine(dir, "model.int8.onnx")) &&
            File.Exists(Path.Combine(dir, "tokens.txt")))
        {
            logger.LogInformation("[ParaformerZh] model dir: {Dir}", dir);
            return dir;
        }
    }
    logger.LogWarning("[ParaformerZh] model dir not found. tried: {Dirs}", string.Join(";", candidates));
    return candidates[0];
}

var senseVoiceModelDir = ResolveSenseVoiceModelDir();
var paraformerZhModelDir = ResolveParaformerZhModelDir();
SenseVoiceAsrService? senseVoiceService = null;
ParaformerZhAsrService? paraformerZhService = null;
// Phoneme CTC ASR (PhoneticAssist): joint_auto 補助レーン。Windows(App.WebHost) parity。
PhonemeCtcService? phonemeCtcService = null;
ILogger? phonemeCtcLogger = null;

// ---- Moonshine DLL / model path resolution (Phase 14-1) ----
string ResolveMoonshineDllDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_MOONSHINE_DLL_DIR");
    var candidates = new[]
    {
        fromEnv,
        Path.Combine(appRoot, "libs", "moonshine"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "moonshine")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "libs", "moonshine")
    };
    foreach (var dir in candidates.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)))
        if (File.Exists(Path.Combine(dir!, "libmoonshine.so")))
            return dir!;
    return string.Empty;
}

string ResolveMoonshineModelRoot()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "models", "moonshine"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "moonshine")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "models", "moonshine")
    };
    return candidates.FirstOrDefault(d => Directory.Exists(d) && Directory.GetDirectories(d).Length > 0) ?? string.Empty;
}

string ResolveWhisperDllDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_WHISPER_DLL_DIR");
    var steamInstallRoot = AppConfigLoader.GetSteamInstallRootFallback(appRoot);
    var candidates = new[]
    {
        fromEnv,
        Path.Combine(appRoot, "libs", "whisper-linux-vulkan"),
        steamInstallRoot == null ? null : Path.Combine(steamInstallRoot, "libs", "whisper-linux-vulkan"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "whisper-linux-vulkan")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "libs", "whisper-linux-vulkan"),
        Path.Combine(appRoot, "libs", "whisper-linux"),
        steamInstallRoot == null ? null : Path.Combine(steamInstallRoot, "libs", "whisper-linux"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "whisper-linux")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "libs", "whisper-linux")
    };
    return candidates.Where(d => !string.IsNullOrWhiteSpace(d))
        .FirstOrDefault(d => Directory.Exists(d!) && File.Exists(Path.Combine(d!, "libwhisper.so"))) ?? string.Empty;
}

string ResolveWhisperCpuDllDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_WHISPER_CPU_DLL_DIR");
    var steamInstallRoot = AppConfigLoader.GetSteamInstallRootFallback(appRoot);
    var candidates = new[]
    {
        fromEnv,
        Path.Combine(appRoot, "libs", "whisper-linux"),
        steamInstallRoot == null ? null : Path.Combine(steamInstallRoot, "libs", "whisper-linux"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "libs", "whisper-linux")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "libs", "whisper-linux")
    };
    return candidates.Where(d => !string.IsNullOrWhiteSpace(d))
        .FirstOrDefault(d => Directory.Exists(d!) && File.Exists(Path.Combine(d!, "libwhisper.so"))) ?? string.Empty;
}

var whisperModelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
var whisperTurbo = new WhisperTurboDownload(whisperModelHttp, Path.Combine(AppUserPaths.V2Root, "asr-models", "whisper"));
app.Lifetime.ApplicationStopping.Register(() => whisperModelHttp.Dispose());

IReadOnlyList<string> EnumerateWhisperModels()
{
    var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_WHISPER_MODEL");
    var steamInstallRoot = AppConfigLoader.GetSteamInstallRootFallback(appRoot);
    var roots = new[]
    {
        Path.Combine(appRoot, "models", "whisper"),
        whisperTurbo.Root,
        steamInstallRoot == null ? null : Path.Combine(steamInstallRoot, "models", "whisper"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "whisper")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "models", "whisper")
    };
    return roots.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
        .SelectMany(d => Directory.EnumerateFiles(d, "ggml*.bin", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        .Concat(!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) ? new[] { fromEnv } : Array.Empty<string>())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

string ResolveWhisperModelPath()
{
    var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_WHISPER_MODEL");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
    var models = EnumerateWhisperModels();
    return models.FirstOrDefault(p => Path.GetFileName(p).Contains("small", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault()
        ?? string.Empty;
}

var moonshineDllDir = ResolveMoonshineDllDir();
var moonshineModelRoot = ResolveMoonshineModelRoot();
var whisperDllDir = ResolveWhisperDllDir();
var whisperCpuDllDir = ResolveWhisperCpuDllDir();
var whisperModelPath = ResolveWhisperModelPath();
bool JointRuntimeAvailable() =>
    !string.IsNullOrEmpty(moonshineDllDir) && File.Exists(Path.Combine(moonshineDllDir, "libmoonshine.so")) &&
    !string.IsNullOrEmpty(whisperDllDir) && File.Exists(Path.Combine(whisperDllDir, "libwhisper.so")) &&
    !string.IsNullOrEmpty(whisperModelPath) && File.Exists(whisperModelPath);
var whisperUseGpu = !string.IsNullOrEmpty(whisperDllDir) && File.Exists(Path.Combine(whisperDllDir, "libggml-vulkan.so"));
var sileroVadModelPath = ResolveSileroVadModelPath();
var onnxRuntimeDllDir = ResolveOnnxRuntimeDllDir();
var whisperRuntimeReason = string.IsNullOrEmpty(whisperDllDir)
    ? "Linux whisper runtime is not configured."
    : (whisperUseGpu ? "Linux whisper Vulkan runtime available" : "Linux whisper CPU runtime available");
var whisperRuntimeReasonCode = string.IsNullOrEmpty(whisperDllDir)
    ? "linux_whisper_runtime_missing"
    : (whisperUseGpu ? "linux_vulkan_runtime_available" : "linux_cpu_runtime_available");
var steamStatus = AppConfigLoader.SteamworksStatus.Disabled;
var dlcStatus = AppConfigLoader.DlcOwnershipStatus.Disabled;
var skipDlcGate = Environment.GetEnvironmentVariable("MOBMATE_SKIP_DLC_GATE") == "1";
if (whisperUseGpu)
{
    if (skipDlcGate)
    {
        logger.LogWarning("[Whisper] DLC gate skipped (MOBMATE_SKIP_DLC_GATE=1)");
        whisperRuntimeReason = "DLC gate skipped by env";
        whisperRuntimeReasonCode = "skip_dlc_gate";
        steamStatus = AppConfigLoader.SteamworksStatus.Disabled;
        dlcStatus = AppConfigLoader.DlcOwnershipStatus.Disabled;
    }
    else
    {
    var gate = AppConfigLoader.CheckSteamVulkanDlc(new AppConfig().SteamVulkanDlcAppId, appRoot);
    steamStatus = gate.SteamStatus;
    dlcStatus = gate.DlcStatus;
    if (gate.Allowed)
    {
        whisperRuntimeReason = "Linux Whisper Vulkan DLC gate passed";
        whisperRuntimeReasonCode = gate.ReasonCode;
        logger.LogInformation("[Whisper] Vulkan DLC gate passed: {Reason}", gate.Reason);
    }
    else
    {
        logger.LogWarning("[Whisper] Vulkan DLC gate denied, fallback CPU: {Reason}", gate.Reason);
        whisperRuntimeReason = gate.Reason;
        whisperRuntimeReasonCode = gate.ReasonCode;
        if (!string.IsNullOrEmpty(whisperCpuDllDir))
        {
            whisperDllDir = whisperCpuDllDir;
            whisperUseGpu = false;
        }
        else
        {
            whisperDllDir = string.Empty;
            whisperUseGpu = false;
        }
    }
    } // end !skipDlcGate
}
if (!string.IsNullOrEmpty(moonshineDllDir))
    logger.LogInformation("[Moonshine] dll dir: {Dir}", moonshineDllDir);
else
    logger.LogInformation("[Moonshine] libmoonshine.so not found, JointAuto will use SenseVoice only");
if (!string.IsNullOrEmpty(whisperDllDir) && !string.IsNullOrEmpty(whisperModelPath))
    logger.LogInformation("[Whisper] Linux runtime: {Dir}, mode: {Mode}, model: {Model}", whisperDllDir, whisperUseGpu ? "vulkan" : "cpu", whisperModelPath);
else
    logger.LogInformation("[Whisper] Linux runtime/model not found; whisper engine disabled");
try
{
    senseVoiceService = new SenseVoiceAsrService(senseVoiceModelDir, senseVoiceLogger);
    if (senseVoiceService.IsAvailable)
        app.Lifetime.ApplicationStopping.Register(senseVoiceService.Dispose);
}
catch (Exception ex)
{
    senseVoiceLogger.LogWarning(ex, "[SenseVoice] service init failed");
}

// Phoneme CTC ASR (PhoneticAssist): モデル同梱時のみ有効化（Windows parity）。
string ResolvePhonemeCtcModelDir()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "models", "phoneme_ctc"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "models", "phoneme_ctc")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "models", "phoneme_ctc")
    };
    foreach (var dir in candidates)
    {
        if (File.Exists(Path.Combine(dir, "model.int8.onnx")) &&
            File.Exists(Path.Combine(dir, "id2token.json")))
            return dir;
    }
    return candidates[0];
}
phonemeCtcLogger = loggerFactory.CreateLogger("PhonemeCtc");
try
{
    var phonemeCtcModelDir = ResolvePhonemeCtcModelDir();
    if (File.Exists(Path.Combine(phonemeCtcModelDir, "model.int8.onnx")) &&
        File.Exists(Path.Combine(phonemeCtcModelDir, "id2token.json")))
    {
        phonemeCtcService = new PhonemeCtcService(phonemeCtcModelDir, phonemeCtcLogger);
        app.Lifetime.ApplicationStopping.Register(phonemeCtcService.Dispose);
    }
    else
    {
        phonemeCtcLogger.LogDebug("[PhonemeCtc] model not found, PhoneticAssist disabled: {Dir}", phonemeCtcModelDir);
    }
}
catch (Exception ex)
{
    phonemeCtcLogger.LogWarning(ex, "[PhonemeCtc] service init failed");
}

hearingService = new LinuxHearingService(
    hearingConfig,
    SaveHearingConfig,
    nativeExe,
    whisperModelPath,
    whisperDllDir,
    whisperUseGpu,
    senseVoiceService,
    translationService,
    hearingLogger,
    sileroVadModelPath: sileroVadModelPath,
    onnxRuntimeDllDir: onnxRuntimeDllDir);
app.Lifetime.ApplicationStopping.Register(() => hearingService.Dispose());
try
{
    paraformerZhService = new ParaformerZhAsrService(paraformerZhModelDir, paraformerLogger);
    if (paraformerZhService.IsAvailable)
        app.Lifetime.ApplicationStopping.Register(paraformerZhService.Dispose);
}
catch (Exception ex)
{
    paraformerLogger.LogWarning(ex, "[ParaformerZh] service init failed");
}

// ---- UI i18n / minimal text-filter config for shared web UI ----
var textFilterConfig = RealtimeTextFilterConfig.CreateDefault(AppPresetBootstrap.ResolveInitialLanguage());
var textFilterLock = new object();
var ignoreBootstrap = AppPresetBootstrap.EnsureRuntimeIgnoreFile(appRoot, AppUserPaths.ConfigDir, textFilterConfig.UiLanguage);
var ignoreFilePath = ignoreBootstrap.TargetPath;
textFilterConfig.UiLanguage = ignoreBootstrap.Language;

// ── Radio Chat service ────────────────────────────────────────
var radioChatLogger = loggerFactory.CreateLogger("RadioChat");
var radioChatService = new RadioChatService(appRoot, textFilterConfig.UiLanguage, radioChatLogger);
app.Lifetime.ApplicationStopping.Register(radioChatService.Dispose);
radioChatService.SetPendingStatusProvider(() => pendingSpeechService.GetStatus());

string ReadIgnoreWords() => File.Exists(ignoreFilePath) ? File.ReadAllText(ignoreFilePath, Encoding.UTF8) : string.Empty;
int CountIgnoreRules() => RealtimeTextFilter.SplitLines(ReadIgnoreWords()).Count;

(string Manual, string Auto, string Replacement) ResolveTextFilterTokens(RealtimeTextFilterConfig config)
{
    if (!string.Equals(config.LaughPreset, "auto", StringComparison.OrdinalIgnoreCase))
        return (config.ManualLaughTokens, config.AutoLaughTokens, config.LaughReplacementWords);
    var profile = RealtimeTextFilterConfig.GetLaughProfile(config.UiLanguage);
    return (profile.ManualTokens, profile.AutoTokens, profile.ReplacementWords);
}

RealtimeTextFilterConfig SnapshotTextFilterConfig(string? language = null)
{
    RealtimeTextFilterConfig snapshot;
    lock (textFilterLock)
    {
        snapshot = new RealtimeTextFilterConfig
        {
            UiLanguage = textFilterConfig.UiLanguage,
            NgMode = textFilterConfig.NgMode,
            LaughReplacementEnabled = textFilterConfig.LaughReplacementEnabled,
            LaughPreset = textFilterConfig.LaughPreset,
            ManualLaughTokens = textFilterConfig.ManualLaughTokens,
            AutoLaughTokens = textFilterConfig.AutoLaughTokens,
            LaughReplacementWords = textFilterConfig.LaughReplacementWords,
            NgWords = ReadIgnoreWords()
        };
    }

    if (string.Equals(snapshot.LaughPreset, "auto", StringComparison.OrdinalIgnoreCase))
    {
        var lang = AppPresetBootstrap.NormalizeLanguage(language) switch
        {
            "zh_cn" or "zh_tw" => "zh",
            "" => snapshot.UiLanguage,
            var v => v,
        };
        var profile = RealtimeTextFilterConfig.GetLaughProfile(lang);
        snapshot.ManualLaughTokens = profile.ManualTokens;
        snapshot.AutoLaughTokens = profile.AutoTokens;
        snapshot.LaughReplacementWords = profile.ReplacementWords;
    }
    return snapshot;
}

object TextFilterConfigResponse(RealtimeTextFilterConfig config)
{
    var tokens = ResolveTextFilterTokens(config);
    return new
    {
        uiLanguage = config.UiLanguage,
        ngMode = config.NgMode,
        ngSource = "_ignore.txt",
        ngRuleCount = CountIgnoreRules(),
        laughPreset = config.LaughPreset,
        resolvedLaughLanguage = RealtimeTextFilterConfig.NormalizeLaughLanguage(config.LaughPreset == "auto" ? config.UiLanguage : config.LaughPreset),
        laughReplacementEnabled = config.LaughReplacementEnabled,
        manualLaughTokens = tokens.Manual,
        autoLaughTokens = tokens.Auto,
        laughReplacementWords = tokens.Replacement,
        manualTokenCount = RealtimeTextFilter.SplitCsv(tokens.Manual).Count,
        autoTokenCount = RealtimeTextFilter.SplitCsv(tokens.Auto).Count,
        replacementCount = RealtimeTextFilter.SplitCsv(tokens.Replacement).Count,
    };
}

string? ValidateNgWordsError(string text, string mode)
{
    if (text.Length > 20000) return "ngWords length must be <= 20000";
    if (RealtimeTextFilter.SplitLines(text).Count > 500) return "ngWords lines must be <= 500";
    if (mode == "regex")
    {
        try { RealtimeTextFilter.ValidateRegexRules(text); }
        catch (Exception ex) { return $"invalid regex: {ex.Message}"; }
    }
    return null;
}

Dictionary<string, string> ReadUiPreset(string language)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var normalized = AppPresetBootstrap.NormalizeLanguage(language);
    if (string.IsNullOrEmpty(normalized)) normalized = "en";

    var path = Path.Combine(appRoot, "libs", "preset", $"_ui_{normalized}.txt");
    if (!File.Exists(path)) return result;

    foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
        var sep = line.IndexOf('=');
        if (sep <= 0) continue;
        var key = line[..sep].Trim();
        var value = line[(sep + 1)..].Trim().Replace("\\n", "\n");
        if (!string.IsNullOrEmpty(key)) result[key] = value;
    }
    return result;
}

// ---- Static web assets ----
var webRoot = Path.Combine(appRoot, "web");
if (!Directory.Exists(webRoot))
    webRoot = Path.Combine(appRoot, "..", "..", "..", "..", "web"); // dev layout
if (Directory.Exists(webRoot))
{
    var fp = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fp,
        RequestPath = ""
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fp,
        RequestPath = "",
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    });
    logger.LogInformation("Web UI: {WebRoot}", webRoot);
}
else
{
    logger.LogWarning("web/ directory not found. Static files disabled.");
}

// ---- Helpers ----
static object ApiOk(object? data = null) => data ?? new { ok = true };
static object ApiMessage(string code, string message) => new { code, message };
static object ApiError(string msg, string? code = null) =>
    new { ok = false, error = msg, code = code ?? "error" };

string CurrentPiperVoiceCatalogPath() => File.Exists(piperVoiceCatalogCachePath) ? piperVoiceCatalogCachePath : piperVoiceCatalogPath;

MobMate.Core.Tts.PiperVoiceEntry[] LoadPiperVoiceCatalog() =>
    MobMate.Core.Tts.PiperVoiceCatalog.Load(CurrentPiperVoiceCatalogPath());

VoiceConversionModelCatalogItem[] LoadVoiceConversionCatalog()
{
    VoiceConversionModelDownloadManager.ScanAndNormalizeInstalledModels(voiceConversionModelRoot, voiceConversionLogger);
    var items = VoiceConversionModelCatalog.Load(voiceConversionCatalogPath).ToList();
    var known = new HashSet<string>(items.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(voiceConversionModelRoot)) return [.. items];

    foreach (var dir in Directory.EnumerateDirectories(voiceConversionModelRoot))
    {
        var id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(id) || known.Contains(id))
            continue;
        var setupState = VoiceConversionModelDownloadManager.GetSetupStateForDirectory(dir);
        if (setupState.Status is not ("ready" or "needs_vcclient_export"))
            continue;
        try
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            var manifest = File.Exists(manifestPath) ? VoiceConversionModelManifest.Load(manifestPath) : null;
            items.Add(new VoiceConversionModelCatalogItem(
                Id: id,
                DisplayName: string.IsNullOrWhiteSpace(manifest?.DisplayName) ? id : manifest.DisplayName,
                Engine: string.IsNullOrWhiteSpace(manifest?.Engine) ? "rvc_onnx" : manifest.Engine,
                DownloadUrl: string.Empty,
                SizeBytes: 0,
                Sha256: string.Empty,
                License: string.IsNullOrWhiteSpace(manifest?.License) ? "User supplied" : manifest.License,
                LicenseUrl: string.IsNullOrWhiteSpace(manifest?.LicenseUrl) ? string.Empty : manifest.LicenseUrl,
                SourcePageUrl: string.IsNullOrWhiteSpace(manifest?.SourceUrl) ? dir : manifest.SourceUrl,
                RiskNotice: "User supplied voice conversion model. Verify rights and usage terms before use.",
                RequiresUserAgreement: false));
            known.Add(id);
        }
        catch (Exception ex)
        {
            voiceConversionLogger.LogWarning(ex, "Failed to load installed voice conversion manifest: {Dir}", dir);
        }
    }
    return [.. items];
}

string ResolveVoiceConversionModelOptionsJson(string optionsJson, bool ensureAudibleLiveOutput = false)
{
    try
    {
        var obj = JsonNode.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson)?.AsObject();
        if (obj is null) return optionsJson;

        obj["wavOutputEnabled"] = linuxTtsConfig.WavOutputEnabled;
        obj["wavOutputDirectory"] = wavOutputStore.OutputDirectory;

        var rawRvcMode = string.Equals(obj["mode"]?.GetValue<string>(), "rvc_raw", StringComparison.OrdinalIgnoreCase);
        if (rawRvcMode)
        {
            // Minimal RVC route: preserve capture/playback, VAD segmentation, and model options;
            // bypass optional pre-RVC DSP/prosody and Moonshine laugh-ASR.
            obj["engine"] = "rvc_onnx";
            obj["prosodyNeutralizerEnabled"] = false;
            obj["pressureEstimatorEnabled"] = false;
            obj["adaptiveStrength"] = false;
            obj["noiseCleanup"] = "none";
            obj["noiseGate"] = 0.0;
            obj["laughAsrAssistEnabled"] = false;
            obj["seModelDir"] = "";
            if (obj["sileroVadModelPath"] is null)
                obj["sileroVadModelPath"] = Path.Combine(appRoot, "models", "silero_vad", "silero_vad_int8.onnx");
            if (obj["onnxRuntimeDllDir"] is null)
                obj["onnxRuntimeDllDir"] = onnxRuntimeDllDir;
            if (ensureAudibleLiveOutput) EnsureAudibleOutputVolume(obj);
        }
        else
        {
            // Default mode keeps the existing DSP/VAD path unchanged.
            if (obj["sileroVadModelPath"] is null)
                obj["sileroVadModelPath"] = Path.Combine(appRoot, "models", "silero_vad", "silero_vad_int8.onnx");
            if (obj["onnxRuntimeDllDir"] is null)
                obj["onnxRuntimeDllDir"] = onnxRuntimeDllDir;
            // Speech Enhancement (DTLN): inject path if models exist
            var seModelDir = Path.Combine(appRoot, "models", "dtln");
            if (obj["seModelDir"] is null && Directory.Exists(seModelDir))
                obj["seModelDir"] = seModelDir;
            if (obj["moonshineDllDir"] is null)
                obj["moonshineDllDir"] = moonshineDllDir;
            if (obj["moonshineModelRoot"] is null)
                obj["moonshineModelRoot"] = moonshineModelRoot;
            if (obj["laughAsrLanguage"] is null)
                obj["laughAsrLanguage"] = NormalizeMoonshineLang(textFilterConfig.UiLanguage);
            var existingLaughAsrTokens = obj["laughAsrTokens"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(existingLaughAsrTokens))
            {
                var tokens = ResolveTextFilterTokens(textFilterConfig);
                obj["laughAsrTokens"] = string.Join(',', new[] { tokens.Manual, tokens.Auto }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            if (ensureAudibleLiveOutput) EnsureAudibleOutputVolume(obj);
        }
        NormalizeLinuxVoiceConversionOutputDevice(obj);

        var existingPath = obj["modelPath"]?.GetValue<string>();
        var rvcModelDir = obj["rvcModelDir"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(existingPath) && !string.IsNullOrWhiteSpace(rvcModelDir))
        {
            obj["modelPath"] = rvcModelDir;
            existingPath = rvcModelDir;
        }
        if (!string.IsNullOrWhiteSpace(existingPath)) return obj.ToJsonString();

        var modelId = obj["modelId"]?.GetValue<string>() ?? obj["id"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelId)) return obj.ToJsonString();

        var model = LoadVoiceConversionCatalog().FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null || !VoiceConversionModelDownloadManager.IsInstalled(model.Id, voiceConversionModelRoot))
            return obj.ToJsonString();

        obj["modelPath"] = Path.Combine(voiceConversionModelRoot, model.Id);
        if (obj["engine"] is null || string.IsNullOrWhiteSpace(obj["engine"]?.GetValue<string>()))
            obj["engine"] = model.Engine;
        InjectManifestFileOptions(obj, Path.Combine(voiceConversionModelRoot, model.Id));
        return obj.ToJsonString();
    }
    catch
    {
        return optionsJson;
    }

    static void EnsureAudibleOutputVolume(JsonObject obj)
    {
        const double mobfyMinimumAudibleOutputVolumePercent = 140.0;
        var volume = 100.0;
        try { volume = obj["outputVolumePercent"]?.GetValue<double>() ?? 100.0; } catch { }
        if (!double.IsFinite(volume) || volume < mobfyMinimumAudibleOutputVolumePercent)
            obj["outputVolumePercent"] = mobfyMinimumAudibleOutputVolumePercent;
    }

    static void NormalizeLinuxVoiceConversionOutputDevice(JsonObject obj)
    {
        const string linuxVirtualMicSinkName = "MobMateTTSVirtualMic";
        var output = obj["outputDeviceId"]?.GetValue<string>() ?? string.Empty;
        var compact = output.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(compact, linuxVirtualMicSinkName, StringComparison.OrdinalIgnoreCase))
            obj["outputDeviceId"] = linuxVirtualMicSinkName;
    }

    static string NormalizeMoonshineLang(string? lang)
    {
        var s = (lang ?? "ja").ToLowerInvariant().Replace('-', '_');
        if (s.StartsWith("zh")) return "zh";
        if (s.StartsWith("ko")) return "ko";
        if (s.StartsWith("en")) return "en";
        return "ja";
    }

    void InjectManifestFileOptions(JsonObject obj, string modelDir)
    {
        try
        {
            var manifestPath = Path.Combine(modelDir, "manifest.json");
            if (!File.Exists(manifestPath)) return;
            var manifest = VoiceConversionModelManifest.Load(manifestPath);
            if (obj["vcModelFile"] is null && !string.IsNullOrWhiteSpace(manifest.Files.Model)) obj["vcModelFile"] = manifest.Files.Model;
            if (obj["contentEncoderFile"] is null && !string.IsNullOrWhiteSpace(manifest.Files.ContentEncoder)) obj["contentEncoderFile"] = manifest.Files.ContentEncoder;
            if (obj["f0ModelFile"] is null && !string.IsNullOrWhiteSpace(manifest.Files.F0Model)) obj["f0ModelFile"] = manifest.Files.F0Model;
        }
        catch (Exception ex)
        {
            voiceConversionLogger.LogWarning(ex, "Failed to inject VC manifest file options: {ModelDir}", modelDir);
        }
    }
}

bool VoiceConversionRequested(string optionsJson)
{
    try
    {
        using var doc = JsonDocument.Parse(optionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
        // Mirror the native option reader, including its first-key and escape
        // handling, so both sides select the same route for this session.
        var invalidSelection = false;
        string Value(string key)
        {
            var token = "\"" + key + "\"";
            var search = 0;
            int start;
            while (true)
            {
                start = optionsJson.IndexOf(token, search, StringComparison.Ordinal);
                if (start < 0) return string.Empty;
                start += token.Length;
                search = start;
                while (start < optionsJson.Length && (optionsJson[start] == ' ' || optionsJson[start] == '\t' || optionsJson[start] == '\r' || optionsJson[start] == '\n')) start++;
                if (start < optionsJson.Length && optionsJson[start] == ':') break;
            }
            start++;
            while (start < optionsJson.Length && (optionsJson[start] == ' ' || optionsJson[start] == '\t' || optionsJson[start] == '\r' || optionsJson[start] == '\n')) start++;
            if (start >= optionsJson.Length || optionsJson[start] != '"')
            {
                // Null is an absent optional value; other unreadable selections
                // must go through load failure/cleanup, never passthrough.
                invalidSelection |= !optionsJson[start..].StartsWith("null", StringComparison.Ordinal);
                return string.Empty;
            }
            start++;
            var end = start;
            while (end < optionsJson.Length && (optionsJson[end] != '"' || (end > start && optionsJson[end - 1] == '\\'))) end++;
            return end < optionsJson.Length ? optionsJson[start..end] : string.Empty;
        }
        var engine = Value("engine");
        return string.Equals(Value("mode"), "rvc_raw", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Value("modelId")) || !string.IsNullOrEmpty(Value("id"))
            || !string.IsNullOrEmpty(Value("modelPath")) || !string.IsNullOrEmpty(Value("rvcModelDir"))
            || (!string.IsNullOrEmpty(engine) && !string.Equals(engine, "passthrough", StringComparison.OrdinalIgnoreCase))
            || invalidSelection;
    }
    catch (JsonException)
    {
        return true; // Invalid options cannot safely imply passthrough.
    }
}

object VoiceConversionNativeStatusObject(NativeLibraryResult result)
{
    if (!result.Success)
        return new { ok = false, error = result.Message };
    try
    {
        using var doc = JsonDocument.Parse(result.RawJson);
        return doc.RootElement.Clone();
    }
    catch
    {
        return new { ok = false, error = "invalid native status JSON", rawJson = result.RawJson };
    }
}

MobMate.Core.Tts.RichVoiceEntry[] LoadRichVoiceCatalog() =>
    MobMate.Core.Tts.RichVoiceCatalog.Load(richVoiceCatalogPath);

string? ResolveKokoroModelDir(string modelRoot, string? modelId)
{
    if (string.IsNullOrWhiteSpace(modelId)) return null;
    var dir = Path.Combine(modelRoot, modelId);
    return Directory.Exists(dir) ? dir : null;
}

string? ResolveMossModelDir(string modelRoot, string? modelId)
{
    if (string.IsNullOrWhiteSpace(modelId)) return null;
    var outer = Path.Combine(modelRoot, modelId);
    if (Directory.Exists(outer))
    {
        foreach (var sub in Directory.EnumerateDirectories(outer))
            if (MobMate.Core.Tts.MossTtsService.IsModelDirValid(sub)) return sub;
    }
    return MobMate.Core.Tts.MossTtsService.IsModelDirValid(outer) ? outer : null;
}

string[]? ReadMossVoicesFromManifest(string modelDir)
{
    var path = Path.Combine(modelDir, "browser_poc_manifest.json");
    if (!File.Exists(path)) return null;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!doc.RootElement.TryGetProperty("builtin_voices", out var arr)) return null;
        var voices = arr.EnumerateArray()
            .Select(v => v.TryGetProperty("voice", out var vp) ? vp.GetString() : null)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();
        return voices.Length > 0 ? voices : null;
    }
    catch { return null; }
}

IEnumerable<object> EnumerateKokoroModels(string modelRoot)
{
    var catalogById = LoadRichVoiceCatalog().ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(modelRoot)) yield break;
    foreach (var dir in Directory.EnumerateDirectories(modelRoot).OrderBy(Path.GetFileName))
    {
        if (!MobMate.Core.Tts.KokoroTtsService.IsModelDirValid(dir)) continue;
        var props = ReadProperties(Path.Combine(dir, "mobmate-model.properties"));
        var id = props.TryGetValue("id", out var idV) && !string.IsNullOrWhiteSpace(idV) ? idV : Path.GetFileName(dir);
        var name = props.TryGetValue("display_name", out var dV) && !string.IsNullOrWhiteSpace(dV) ? dV : id;
        var lang = props.TryGetValue("language", out var lV) ? lV : string.Empty;
        var defSid = props.TryGetValue("default_speaker_id", out var dsV) && int.TryParse(dsV, out var dsi) ? dsi : 3;
        var spCount = props.TryGetValue("speaker_count", out var scV) && int.TryParse(scV, out var sci) ? sci : 0;
        var speakers = catalogById.TryGetValue(id, out var cat) ? cat.Speakers : [];
        yield return new { id, name, language = lang, path = dir, defaultSpeakerId = defSid, speakerCount = spCount, speakers };
    }
}

IEnumerable<object> EnumerateMossModels(string modelRoot)
{
    if (!Directory.Exists(modelRoot)) yield break;
    foreach (var outerDir in Directory.EnumerateDirectories(modelRoot).OrderBy(Path.GetFileName))
    {
        string? modelDir = null;
        foreach (var sub in Directory.EnumerateDirectories(outerDir))
        {
            if (MobMate.Core.Tts.MossTtsService.IsModelDirValid(sub)) { modelDir = sub; break; }
        }
        if (modelDir == null && MobMate.Core.Tts.MossTtsService.IsModelDirValid(outerDir))
            modelDir = outerDir;
        if (modelDir == null) continue;

        var props = ReadProperties(Path.Combine(outerDir, "mobmate-model.properties"));
        var id = props.TryGetValue("id", out var idV) && !string.IsNullOrWhiteSpace(idV) ? idV : Path.GetFileName(outerDir);
        var name = props.TryGetValue("display_name", out var dV) && !string.IsNullOrWhiteSpace(dV) ? dV : id;
        var voices = ReadMossVoicesFromManifest(modelDir)
                     ?? (props.TryGetValue("voices", out var voicesV) && !string.IsNullOrWhiteSpace(voicesV)
                         ? voicesV.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray()
                         : new[] { "Ava", "Adam", "Junhao", "Soyo" });
        yield return new { id, name, path = modelDir, voices };
    }
}

async Task<(bool Ok, string Code, int Count)> TryUpdatePiperVoiceCatalogAsync(CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(piperVoiceCatalogRemoteUrl))
        return (false, "not_configured", 0);
    if (!Uri.TryCreate(piperVoiceCatalogRemoteUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        return (false, "invalid_url", 0);

    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var tempPath = piperVoiceCatalogCachePath + ".tmp";
    try
    {
        using var response = await piperVoiceCatalogHttp.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
            return (false, $"http_{(int)response.StatusCode}", 0);
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonDocument.Parse(json).Dispose();
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), ct);
        var voices = MobMate.Core.Tts.PiperVoiceCatalog.Load(tempPath);
        if (voices.Length <= 0) return (false, "empty_or_invalid_catalog", 0);
        File.Copy(tempPath, piperVoiceCatalogCachePath, overwrite: true);
        return (true, "updated", voices.Length);
    }
    catch (Exception ex)
    {
        logger.LogWarning("Piper voice catalog update failed: {Error}", ex.Message);
        return (false, "download_failed", 0);
    }
    finally
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }
}

string ttsOutputSinkName = string.Empty;
string? linuxMonitorFallbackReason = null;
var linuxMonitorGate = new SemaphoreSlim(1, 1);
var linuxMonitorLoopback = new LinuxMonitorLoopback(RunPactlAsync);
var linuxMonitorStopping = new CancellationTokenSource();
const string LinuxVirtualMicSinkName = "MobMateTTSVirtualMic";
const string LinuxVirtualMicSourceName = "MobMateTTSVirtualMic";
const string LinuxLegacyVirtualMicSinkName = "MobMateVirtualMic";
const string LinuxLegacyVirtualMicSourceName = "MobMateVirtualMicSource";
const string LinuxVirtualMicDescription = "MobMateTTSVirtualMic";

bool IsMobMateSink(string name)
    => name.StartsWith("MobMate", StringComparison.OrdinalIgnoreCase);

bool IsInternalMobMateSink(string name)
    => IsMobMateSink(name) && !string.Equals(name, LinuxVirtualMicSinkName, StringComparison.Ordinal);

string NormalizeTtsOutputSink(string sinkName)
    => string.Equals(sinkName, LinuxLegacyVirtualMicSinkName, StringComparison.Ordinal) || IsInternalMobMateSink(sinkName) ? LinuxVirtualMicSinkName : sinkName;

bool ShouldUseSinkMasterRemapSource()
    => false;

async Task<string?> RunPactlAsync(string arguments, CancellationToken ct = default)
{
    try
    {
        var psi = NewPactlStartInfo(arguments, redirectStandardOutput: true, redirectStandardError: true);
        var result = await LinuxMonitorLoopback.RunChildAsync(psi, ct);
        if (result.ExitCode != 0)
        {
            logger!.LogWarning("pactl {Args} failed: {Error}", arguments, result.Stderr.Trim());
            return null;
        }
        return result.Stdout.Trim();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger!.LogWarning("pactl {Args} failed: {Error}", arguments, ex.Message);
        return null;
    }
}

async Task<string?> GetDefaultSinkAsync(CancellationToken ct = default)
{
    return await RunPactlAsync("get-default-sink", ct);
}

async Task<string?> GetFirstNonTtsSinkAsync(string sourceSinkName, CancellationToken ct = default)
{
    var sinks = await RunPactlAsync("list short sinks", ct);
    if (string.IsNullOrWhiteSpace(sinks)) return null;
    foreach (var line in sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = line.Split('\t');
        if (parts.Length < 2) continue;
        var name = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(name)) continue;
        if (string.Equals(name, sourceSinkName, StringComparison.Ordinal)) continue;
        if (name.StartsWith("MobMate", StringComparison.OrdinalIgnoreCase)) continue;
        return name;
    }
    return null;
}

async Task EnsureLinuxMonitorPlaybackAsync(string sourceSinkName, int volumePercent, CancellationToken ct = default)
{
    using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, linuxMonitorStopping.Token);
    ct = request.Token;
    await linuxMonitorGate.WaitAsync(ct);
    try
    {
        ct.ThrowIfCancellationRequested();
        linuxMonitorFallbackReason = null;
        if (volumePercent <= 0)
        {
            linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(sourceSinkName))
        {
            linuxMonitorFallbackReason = "linux_monitor_no_output_sink";
            linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(ct) ?? linuxMonitorFallbackReason;
            return;
        }
        if (IsInternalMobMateSink(sourceSinkName))
        {
            linuxMonitorFallbackReason = "linux_monitor_internal_mobmate_sink";
            linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(ct) ?? linuxMonitorFallbackReason;
            return;
        }
        var defaultSink = await GetDefaultSinkAsync(ct);
        if (string.IsNullOrWhiteSpace(defaultSink))
        {
            linuxMonitorFallbackReason = "linux_monitor_default_sink_not_found";
            linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(ct) ?? linuxMonitorFallbackReason;
            return;
        }
        if (string.Equals(defaultSink, sourceSinkName, StringComparison.Ordinal) || IsMobMateSink(defaultSink))
        {
            defaultSink = await GetFirstNonTtsSinkAsync(sourceSinkName, ct);
            if (string.IsNullOrWhiteSpace(defaultSink))
            {
                linuxMonitorFallbackReason = "linux_monitor_default_sink_is_tts_sink";
                linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(ct) ?? linuxMonitorFallbackReason;
                return;
            }
        }

        linuxMonitorFallbackReason = await linuxMonitorLoopback.EnsureAsync(sourceSinkName, defaultSink, volumePercent, ct);
        if (linuxMonitorFallbackReason == null)
            logger.LogInformation("Linux TTS monitor loopback enabled: sourceSink={SourceSink} defaultSink={DefaultSink} volume={Volume}%", sourceSinkName, defaultSink, volumePercent);
        else
            logger.LogWarning("Linux TTS monitor loopback failed: {Reason}; cleanup: {Cleanup}", linuxMonitorFallbackReason, linuxMonitorLoopback.CleanupError);
    }
    finally { linuxMonitorGate.Release(); }
}

async Task MoveTtsSinkInputsAsync(string sinkName, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(sinkName)) return;
    var moved = new HashSet<string>();
    var until = DateTimeOffset.UtcNow.AddSeconds(5);
    while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < until)
    {
        try
        {
            var listPsi = NewPactlStartInfo("list short sink-inputs", redirectStandardOutput: true);
            using var listProc = System.Diagnostics.Process.Start(listPsi);
            if (listProc != null)
            {
                var output = await listProc.StandardOutput.ReadToEndAsync(ct);
                await listProc.WaitForExitAsync(ct);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 5) continue;
                    var id = parts[0].Trim();
                    if (moved.Contains(id)) continue;
                    if (!line.Contains("16000Hz", StringComparison.OrdinalIgnoreCase)) continue;

                    var movePsi = NewPactlStartInfo($"move-sink-input {id} {sinkName}", redirectStandardError: true);
                    using var moveProc = System.Diagnostics.Process.Start(movePsi);
                    if (moveProc == null) continue;
                    var stderr = await moveProc.StandardError.ReadToEndAsync(ct);
                    await moveProc.WaitForExitAsync(ct);
                    if (moveProc.ExitCode == 0)
                    {
                        moved.Add(id);
                        logger!.LogInformation("TTS sink-input moved: id={Id} sink={Sink}", id, sinkName);
                    }
                    else
                    {
                        logger!.LogWarning("TTS sink-input move failed: id={Id} sink={Sink}: {Error}", id, sinkName, stderr.Trim());
                    }
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            logger!.LogWarning("TTS sink-input routing failed: {Sink}: {Error}", sinkName, ex.Message);
        }
        try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
    }
}

long lastRealtimeTtsSpeakFinalDetectedAtMs = 0;
long lastRealtimeFinalToTtsSpeakMs = -1;
long lastRealtimeAsrReadyFinalDetectedAtMs = 0;

long? GetLastFinalToTtsSpeakMs(long finalDetectedAtMs)
{
    if (finalDetectedAtMs <= 0) return null;
    var lastFinal = System.Threading.Interlocked.Read(ref lastRealtimeTtsSpeakFinalDetectedAtMs);
    var lastMs = System.Threading.Interlocked.Read(ref lastRealtimeFinalToTtsSpeakMs);
    return lastFinal == finalDetectedAtMs && lastMs >= 0 ? lastMs : null;
}

long? GetLastFinalToPlaybackMs(long finalDetectedAtMs)
{
    if (finalDetectedAtMs <= 0) return null;
    var lastFinal = ttsPlayback.LastPlaybackFinalDetectedAtMs;
    var lastMs = ttsPlayback.LastFinalToPlaybackMs;
    return lastFinal == finalDetectedAtMs && lastMs >= 0 ? lastMs : null;
}

void LogRealtimeAsrReady(RealtimePollResult poll, string finalText, string? selectedEngine, string? decisionReason, string source)
{
    if (poll.FinalDetectedAtMs <= 0 || string.IsNullOrWhiteSpace(finalText)) return;
    if (System.Threading.Interlocked.Read(ref lastRealtimeAsrReadyFinalDetectedAtMs) == poll.FinalDetectedAtMs) return;
    System.Threading.Interlocked.Exchange(ref lastRealtimeAsrReadyFinalDetectedAtMs, poll.FinalDetectedAtMs);
    var readyAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var finalToOutputMs = readyAtMs - poll.FinalDetectedAtMs;
    logger.LogInformation(
        "[ASR] final ready: source={Source} finalDetectedAtMs={FinalDetectedAtMs} readyAtMs={ReadyAtMs} finalToOutputMs={FinalToOutputMs} latencyMs={LatencyMs} selectedEngine={Engine} decision={Decision} text=[{Text}]",
        source, poll.FinalDetectedAtMs, readyAtMs, finalToOutputMs, poll.LatencyMs, selectedEngine ?? string.Empty, decisionReason ?? string.Empty, finalText);
}

async Task<TtsResult> SpeakWithOutputRoutingAsync(string text, bool interrupt, TtsProsodyProfile? sourceProsody = null,
    long finalDetectedAtMs = 0, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    // NOTE: EnsureLinuxMonitorPlaybackAsync is intentionally NOT called here.
    // Monitor loopback is managed independently by /api/tts/config.
    // Calling pactl unload-module just before miniaudio device init caused a
    // PipeWire graph reconfiguration race, making MobMateVirtualMic temporarily
    // invisible and falling back to the default (speakers) sink.
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var routeTask = string.IsNullOrWhiteSpace(ttsOutputSinkName)
        ? Task.CompletedTask
        : MoveTtsSinkInputsAsync(ttsOutputSinkName, cts.Token);
    if (!string.IsNullOrWhiteSpace(ttsOutputSinkName))
    {
        Environment.SetEnvironmentVariable("PULSE_SINK", ttsOutputSinkName);
        NativeLibraryBridge.SetTtsOutputDevice(nativeExe, ttsOutputSinkName);
    }
    var speakStartAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var finalToSpeakMs = finalDetectedAtMs > 0 ? speakStartAtMs - finalDetectedAtMs : (long?)null;
    System.Threading.Interlocked.Exchange(ref lastRealtimeTtsSpeakFinalDetectedAtMs, finalDetectedAtMs);
    System.Threading.Interlocked.Exchange(ref lastRealtimeFinalToTtsSpeakMs, finalToSpeakMs ?? -1);
    logger!.LogInformation(
        "[TTS] speak start: finalDetectedAtMs={FinalDetectedAtMs} ttsSpeakStartAtMs={TtsSpeakStartAtMs} finalToTtsSpeakMs={FinalToTtsSpeakMs} sink={Sink} text=[{Text}]",
        finalDetectedAtMs, speakStartAtMs, finalToSpeakMs, ttsOutputSinkName ?? string.Empty, text);
    try { return await ttsRouter.SpeakAsync(text, null, null, interrupt, cancellationToken, sourceProsody, finalDetectedAtMs); }
    finally
    {
        cts.Cancel();
        try { await routeTask; } catch (OperationCanceledException) { }
    }
}

// Pending speech timeout callback (requires SpeakWithOutputRoutingAsync defined above)
pendingSpeechService.OnTimeoutSpeakAsync = async (text, ct) => { await SpeakWithOutputRoutingAsync(text, false, null, cancellationToken: ct); };

// Demo mode: 15-minute full-feature session limit (Windows WebHost parity).
// NOTE: Restarting the app resets the timer. This looseness is intentional:
// the goal is to keep the trial from polluting live voice chat, not strict license enforcement.
var demoSessionStartUtc = DateTime.UtcNow;
const int DemoSessionLimitMinutes = 15;
const int DemoSessionWarningMinutes = 13; // = 残り2分
bool demoSessionExpired = false;
object demoSessionLock = new object();
System.Threading.Timer? demoWarningTimer = null;
System.Threading.Timer? demoExpireTimer = null;
var demoLogger = loggerFactory.CreateLogger("Demo");

bool IsDemoSessionExpired()
{
    if (!MobMateCore.IsDemo) return false;
    lock (demoSessionLock) { return demoSessionExpired; }
}

IResult DemoSessionExpiredResult() =>
    Results.Json(new
    {
        reason = "demo_session_expired",
        code = "demo_session_expired",
        message = "demo session expired (restart the app to try again; the full version has no time limit)"
    }, statusCode: 403);

if (MobMateCore.IsDemo)
{
    var demoWarningMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "体験版のセッションは、残り2分で終了します。",
        ["en"] = "This trial session ends in two minutes.",
        ["ko"] = "체험판 세션이 2분 후에 종료됩니다.",
        ["zh_cn"] = "试用会话将在2分钟后结束。",
        ["zh_tw"] = "試用工作階段將在2分鐘後結束。",
    };
    var demoLanguage = textFilterConfig.UiLanguage;
    if (demoLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !demoWarningMessages.ContainsKey(demoLanguage))
        demoLanguage = "zh_cn";
    string demoWarningMsg = demoWarningMessages.TryGetValue(demoLanguage, out var dw) ? dw : demoWarningMessages["en"];
    demoWarningTimer = new System.Threading.Timer(_ =>
    {
        demoLogger.LogInformation("[Demo] session warning: 2 minutes remaining");
        _ = Task.Run(async () =>
        {
            try
            {
                await SpeakWithOutputRoutingAsync(demoWarningMsg, interrupt: true);
            }
            catch (Exception ex)
            {
                demoLogger.LogWarning(ex, "[Demo] session warning TTS failed.");
            }
        });
    }, null, TimeSpan.FromMinutes(DemoSessionWarningMinutes), System.Threading.Timeout.InfiniteTimeSpan);

    demoExpireTimer = new System.Threading.Timer(_ =>
    {
        lock (demoSessionLock) { demoSessionExpired = true; }
        demoLogger.LogInformation("[Demo] session expired: blocking start APIs and stopping running sessions");
        try
        {
            hearingService?.Stop();
            if (_subtitleProcess is { HasExited: false })
            {
                _subtitleProcess.Kill();
                _subtitleProcess = null;
            }
            RadioChatService.TryStopHostUserUnit("mobmate-subtitle-window", logger);
        }
        catch (Exception ex)
        {
            demoLogger.LogWarning(ex, "[Demo] hearing/subtitle stop on expiry failed");
        }
    }, null, TimeSpan.FromMinutes(DemoSessionLimitMinutes), System.Threading.Timeout.InfiniteTimeSpan);

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        demoWarningTimer?.Dispose();
        demoExpireTimer?.Dispose();
    });
}

app.MapGet("/api/demo/status", () =>
{
    if (!MobMateCore.IsDemo)
    {
        return Results.Ok(new { isDemo = false, remainingSeconds = -1, expired = false });
    }
    var elapsed = DateTime.UtcNow - demoSessionStartUtc;
    var remainingSeconds = (int)Math.Max(0, (TimeSpan.FromMinutes(DemoSessionLimitMinutes) - elapsed).TotalSeconds);
    return Results.Ok(new { isDemo = true, remainingSeconds, expired = IsDemoSessionExpired() });
});

async Task<bool> EnsureVirtualMicSourceAsync(string sinkName, string sourceName, string description)
{
    try
    {
        if (!string.Equals(sourceName, LinuxLegacyVirtualMicSourceName, StringComparison.Ordinal))
            await UnloadRemapSourceModulesAsync(LinuxLegacyVirtualMicSourceName);

        if (ShouldUseSinkMasterRemapSource())
            await UnloadRemapSourceModulesAsync(sourceName);

        var listPsi = NewPactlStartInfo("list short sources", redirectStandardOutput: true);
        using var listProc = System.Diagnostics.Process.Start(listPsi);
        if (listProc == null) return false;
        var sources = await listProc.StandardOutput.ReadToEndAsync();
        await listProc.WaitForExitAsync();
        var sourceExists = sources.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t').ElementAtOrDefault(1)?.Trim())
            .Any(name => string.Equals(name, sourceName, StringComparison.Ordinal));
        if (sourceExists) return true;

        var remapMaster = ShouldUseSinkMasterRemapSource() ? sinkName : $"{sinkName}.monitor";
        var args = $"load-module module-remap-source master={remapMaster} source_name={sourceName} source_properties=device.description=\"{description}\"";
        var psi = NewPactlStartInfo(args, redirectStandardOutput: true, redirectStandardError: true);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return false;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode == 0)
        {
            logger!.LogInformation("Virtual mic source created: {Source}", sourceName);
            return true;
        }
        logger!.LogWarning("Virtual mic source create failed: {Source}: {Error}", sourceName, stderr.Trim());
        return false;
    }
    catch (Exception ex)
    {
        logger!.LogWarning("Virtual mic source create failed: {Source}: {Error}", sourceName, ex.Message);
        return false;
    }
}

async Task UnloadRemapSourceModulesAsync(string sourceName, CancellationToken ct = default)
{
    var modules = await RunPactlAsync("list short modules", ct);
    if (string.IsNullOrWhiteSpace(modules)) return;
    foreach (var line in modules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!line.Contains("module-remap-source", StringComparison.Ordinal)) continue;
        if (!line.Contains($"source_name={sourceName}", StringComparison.Ordinal)) continue;
        var id = line.Split('\t').ElementAtOrDefault(0)?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
            await RunPactlAsync($"unload-module {id}", ct);
    }
}

async Task UnloadLegacyVirtualMicModulesAsync(CancellationToken ct = default)
{
    var modules = await RunPactlAsync("list short modules", ct);
    if (string.IsNullOrWhiteSpace(modules)) return;
    foreach (var line in modules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!line.Contains(LinuxLegacyVirtualMicSinkName, StringComparison.Ordinal) && !line.Contains(LinuxLegacyVirtualMicSourceName, StringComparison.Ordinal)) continue;
        var id = line.Split('\t').ElementAtOrDefault(0)?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
            await RunPactlAsync($"unload-module {id}", ct);
    }
}

async Task<(bool ok, bool alreadyExisted, bool sourceOk, string? error)> EnsureVirtualMicAsync(string sinkName, string sourceName, string description)
{
    using var onsetRouteChange = ttsRouter.BeginOnsetRouteChange();
    try
    {
        if (string.Equals(sinkName, LinuxVirtualMicSinkName, StringComparison.Ordinal))
            await UnloadLegacyVirtualMicModulesAsync();

        var checkPsi = NewPactlStartInfo("list short sinks", redirectStandardOutput: true);
        using var checkProc = System.Diagnostics.Process.Start(checkPsi);
        if (checkProc == null) return (false, false, false, "pactl not found");
        var existing = await checkProc.StandardOutput.ReadToEndAsync();
        await checkProc.WaitForExitAsync();

        if (existing.Contains(sinkName, StringComparison.Ordinal))
        {
            NativeLibraryBridge.SetTtsOutputDevice(nativeExe, sinkName);
            var sourceOk = await EnsureVirtualMicSourceAsync(sinkName, sourceName, description);
            logger!.LogInformation("Virtual mic already exists: {Sink}", sinkName);
            return (true, true, sourceOk, null);
        }

        var args = $"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=\"{description}\"";
        var psi = NewPactlStartInfo(args, redirectStandardOutput: true, redirectStandardError: true);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return (false, false, false, "pactl not found");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return (false, false, false, stderr.Trim());

        NativeLibraryBridge.SetTtsOutputDevice(nativeExe, sinkName);
        var createdSourceOk = await EnsureVirtualMicSourceAsync(sinkName, sourceName, description);
        logger!.LogInformation("Virtual mic created: {Sink}", sinkName);
        return (true, false, createdSourceOk, null);
    }
    catch (Exception ex)
    {
        return (false, false, false, ex.Message);
    }
}

async Task RestoreVirtualMicOnStartupAsync()
{
    const string sinkName = LinuxVirtualMicSinkName;
    const string sourceName = LinuxVirtualMicSourceName;
    const string description = LinuxVirtualMicDescription;
    var result = await EnsureVirtualMicAsync(sinkName, sourceName, description);
    if (!result.ok)
    {
        logger!.LogWarning("Virtual mic auto-restore failed: {Error}", result.error);
        return;
    }

    ttsOutputSinkName = sinkName;
    logger!.LogInformation("Virtual mic auto-restored: sink={Sink}, source={Source}, sourceOk={SourceOk}, alreadyExisted={AlreadyExisted}",
        sinkName, sourceName, result.sourceOk, result.alreadyExisted);
}

// ---- Health ----
app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    platform = "linux",
    phase = "4",
    nativeBridgeFound = File.Exists(NativeLibraryBridge.ResolveDllPath(nativeExe)),
    piperExe,
    piperModelFound = !string.IsNullOrEmpty(piperModel) && File.Exists(piperModel)
}));

app.MapGet("/api/ui/i18n", (string? lang) =>
{
    var resolved = AppPresetBootstrap.NormalizeLanguage(lang);
    if (string.IsNullOrEmpty(resolved)) resolved = textFilterConfig.UiLanguage;
    if (string.IsNullOrEmpty(resolved)) resolved = "en";

    var strings = ReadUiPreset("en");
    foreach (var kv in ReadUiPreset(resolved)) strings[kv.Key] = kv.Value;
    return Results.Ok(new { language = resolved, strings });
});

app.MapGet("/api/text-filter/config", () =>
{
    lock (textFilterLock)
    {
        return Results.Ok(TextFilterConfigResponse(textFilterConfig));
    }
});

app.MapPost("/api/text-filter/config", async (HttpRequest req) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        lock (textFilterLock)
        {
            if (doc.RootElement.TryGetProperty("uiLanguage", out var uiLangProp))
            {
                var uiLang = AppPresetBootstrap.NormalizeLanguage(uiLangProp.GetString());
                if (string.IsNullOrEmpty(uiLang)) return Results.BadRequest(ApiError("uiLanguage must be en, ja, ko, zh_cn, or zh_tw", "text_filter.invalid_ui_language"));
                textFilterConfig.UiLanguage = uiLang;
            }
            if (doc.RootElement.TryGetProperty("ngMode", out var ngModeProp))
            {
                var mode = (ngModeProp.GetString() ?? "simple").Trim().ToLowerInvariant();
                if (mode is not ("simple" or "regex")) return Results.BadRequest(ApiError("ngMode must be simple or regex", "text_filter.invalid_ng_mode"));
                if (mode == "regex")
                {
                    var validation = ValidateNgWordsError(ReadIgnoreWords(), mode);
                    if (validation != null) return Results.BadRequest(ApiError(validation, "text_filter.invalid_ng_words"));
                }
                textFilterConfig.NgMode = mode;
            }
            if (doc.RootElement.TryGetProperty("laughReplacementEnabled", out var enabledProp))
                textFilterConfig.LaughReplacementEnabled = enabledProp.GetBoolean();
            if (doc.RootElement.TryGetProperty("laughPreset", out var presetProp))
            {
                var preset = (presetProp.GetString() ?? "auto").Trim().ToLowerInvariant();
                if (preset is not ("auto" or "custom" or "ja" or "en" or "ko" or "zh"))
                    return Results.BadRequest(ApiError("laughPreset must be auto, custom, ja, en, ko, or zh", "text_filter.invalid_laugh_preset"));
                textFilterConfig.LaughPreset = preset;
                if (preset is not ("auto" or "custom")) textFilterConfig.ApplyLaughPreset(preset);
            }
            if (doc.RootElement.TryGetProperty("manualLaughTokens", out var manualProp))
            {
                var value = manualProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiError("manualLaughTokens is too large", "text_filter.manual_laugh_tokens_too_large"));
                textFilterConfig.ManualLaughTokens = value;
                textFilterConfig.LaughPreset = "custom";
            }
            if (doc.RootElement.TryGetProperty("autoLaughTokens", out var autoProp))
            {
                var value = autoProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiError("autoLaughTokens is too large", "text_filter.auto_laugh_tokens_too_large"));
                textFilterConfig.AutoLaughTokens = value;
                textFilterConfig.LaughPreset = "custom";
            }
            if (doc.RootElement.TryGetProperty("laughReplacementWords", out var replProp))
            {
                var value = replProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiError("laughReplacementWords is too large", "text_filter.laugh_replacement_words_too_large"));
                textFilterConfig.LaughReplacementWords = value;
                textFilterConfig.LaughPreset = "custom";
            }
            return Results.Ok(TextFilterConfigResponse(textFilterConfig));
        }
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiError(ex.Message, "request.invalid_json"));
    }
});

app.MapGet("/api/text-filter/ng-words", () =>
{
    var text = ReadIgnoreWords();
    var preset = AppPresetBootstrap.ResolveIgnorePresetPath(appRoot, textFilterConfig.UiLanguage) ?? string.Empty;
    return Results.Ok(new { source = "_ignore.txt", presetFallback = preset, mode = textFilterConfig.NgMode, text, count = RealtimeTextFilter.SplitLines(text).Count });
});

app.MapPost("/api/text-filter/ng-words", async (HttpRequest req) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        string text;
        if (doc.RootElement.TryGetProperty("loadPreset", out var loadPresetProp) && loadPresetProp.GetBoolean())
        {
            var lang = doc.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : textFilterConfig.UiLanguage;
            var preset = AppPresetBootstrap.ResolveIgnorePresetPath(appRoot, lang ?? "en");
            text = !string.IsNullOrEmpty(preset) && File.Exists(preset) ? File.ReadAllText(preset, Encoding.UTF8) : string.Empty;
        }
        else
        {
            text = doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        }
        var validation = ValidateNgWordsError(text, textFilterConfig.NgMode);
        if (validation != null) return Results.BadRequest(ApiError(validation, "text_filter.invalid_ng_words"));
        Directory.CreateDirectory(AppUserPaths.ConfigDir);
        File.WriteAllText(ignoreFilePath, text, new UTF8Encoding(false));
        return Results.Ok(new { code = "text_filter.ng_words_saved", message = "ng words saved", source = "_ignore.txt", count = RealtimeTextFilter.SplitLines(text).Count });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiError(ex.Message, "request.invalid_json"));
    }
});

app.MapPost("/api/text-filter/preview", async (HttpRequest req) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var text = doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        var lang = doc.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
        var result = RealtimeTextFilter.Apply(text, SnapshotTextFilterConfig(lang));
        return Results.Ok(new { rawText = result.RawText, text = result.Text, dropped = result.Dropped, laughReplaced = result.LaughReplaced, ngMatched = result.NgMatched, reason = result.Reason });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiError(ex.Message, "request.invalid_json"));
    }
});

// Shared Windows web UI asks these endpoints during startup. Return Linux-safe stubs for unsupported areas.
string ResolveDefaultRealtimeEngine() => whisperUseGpu ? "joint_auto" : "moonshine";
app.MapGet("/api/config", () => Results.Ok(new { nativeExePath = nativeExe, defaultModelPath = whisperModelPath, whisperRuntimeMode = string.IsNullOrEmpty(whisperDllDir) ? "linux_unavailable" : (whisperUseGpu ? "vulkan" : "cpu"), whisperDllDir, whisperRuntimeReason, whisperRuntimeReasonCode, defaultMoonshineDllDir = moonshineDllDir, defaultMoonshineModelRoot = moonshineModelRoot, voicevoxExePath = File.Exists(voicevoxRunPath) ? voicevoxRunPath : string.Empty, defaultRealtimeEngine = ResolveDefaultRealtimeEngine() }));
app.MapGet("/api/release/status", () => Results.Ok(new { version = MobMateCore.Version, buildChannel = MobMateCore.BuildChannel, isDemo = MobMateCore.IsDemo, commitSha = MobMateCore.CommitSha, buildTimeUtc = MobMateCore.BuildTimeUtc, runtimeMode = string.IsNullOrEmpty(whisperDllDir) ? "linux_unavailable" : (whisperUseGpu ? "vulkan" : "cpu"), runtimeReason = whisperRuntimeReason, runtimeReasonCode = whisperRuntimeReasonCode, steamStatus = steamStatus.ToString().ToLowerInvariant(), dlcStatus = dlcStatus.ToString().ToLowerInvariant(), cloudConfigDir = AppUserPaths.ConfigDir }));
mobfyOnsetSettingsChanged = (before, after) =>
{
    if (!MobfyOnsetSelection.Changed(before, after)) return;
    mobfyOnset.Invalidate();
    PrepareMobfyOnset(before.Settings.GetValueOrDefault("mobmate.realtimeLanguage") != after.Settings.GetValueOrDefault("mobmate.realtimeLanguage")
        ? MobfyOnsetSelection.Language(after) : null);
};
app.MapGet("/api/ui-settings", () => Results.Ok(uiSettingsDocument));
app.MapPut("/api/ui-settings", async (UiSettingsDocument request, CancellationToken cancellationToken) =>
{
    await uiSettingsLock.WaitAsync(cancellationToken);
    try
    {
        var normalized = UiSettingsDocument.PrepareForSave(request, uiSettingsDocument);
        await uiSettingsStore.SaveAsync(normalized, cancellationToken);
        var previous = uiSettingsDocument;
        uiSettingsDocument = normalized;
        mobfyOnsetSettingsChanged?.Invoke(previous, normalized);
        return Results.Ok(uiSettingsDocument);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        logger.LogWarning(ex, "[UiSettings] save failed");
        return Results.Json(UiSettingsApiContract.SaveFailed, statusCode: UiSettingsApiContract.SaveFailedStatusCode);
    }
    finally
    {
        uiSettingsLock.Release();
    }
});
app.MapGet("/api/whisper/models", () => Results.Ok(new { selected = whisperModelPath, autoSelected = ResolveWhisperModelPath(), models = EnumerateWhisperModels().Select(p => new { path = p, name = Path.GetFileName(p), size = new FileInfo(p).Length.ToString() }) }));
app.MapPost("/api/whisper/model", async (HttpRequest request) =>
{
    try
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, app.Lifetime.ApplicationStopping);
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellation.Token);
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return Results.BadRequest(ApiError("path must be a string", "request.invalid_json"));
        var path = await whisperTurbo.ResolveSelectionAsync(pathProp.GetString() ?? string.Empty,
            EnumerateWhisperModels(), cancellation.Token);
        if (path is null) return Results.BadRequest(ApiError("unknown whisper model", "whisper.unknown_model"));
        cancellation.Token.ThrowIfCancellationRequested();
        whisperModelPath = path;
        return Results.Ok(new { code = "whisper.model_selected", message = "whisper model selected", selected = whisperModelPath });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiError(ex.Message, "request.invalid_json"));
    }
    catch (OperationCanceledException)
    {
        return Results.Json(ApiError("Whisper model request cancelled", "whisper.download_cancelled"), statusCode: 408);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
    {
        logger.LogWarning(ex, "[Whisper] model download failed; selection preserved");
        return Results.Json(ApiError("Whisper Turbo download failed; previous model retained", "whisper.download_failed"), statusCode: 502);
    }
});
app.MapPost("/api/speaker/reset", () =>
{
    var ret = NativeLibraryBridge.mmv2_speaker_reset();
    if (ret != 0) return Results.Ok(new { success = false, code = "speaker.reset_failed", adaptiveVocabReset = false });
    var adaptiveVocabReset = adaptiveVocabStore.Reset();
    lock (adaptiveRmsLock)
    {
        adaptiveRmsAverage = 0.0;
        adaptiveRmsCount = 0;
    }
    return Results.Ok(new { success = ret == 0, code = ret == 0 ? (string?)null : "speaker.reset_failed", adaptiveVocabReset });
});

app.MapGet("/api/adaptive-vocab/status", () =>
{
    const string lang = "auto";
    var entries = adaptiveVocabStore.GetTopEntries(lang, maxCount: 50);
    return Results.Ok(new
    {
        enabled = adaptiveVocabOptions.Enabled,
        path = adaptiveVocabStore.Path,
        count = adaptiveVocabStore.Count(),
        language = lang,
        top = entries.Select(e => new { e.Text, e.Lang, e.Source, e.Count, e.Priority, e.LastSeen, hasPhonemes = !string.IsNullOrWhiteSpace(e.Phonemes) }).ToArray()
    });
});

app.MapDelete("/api/adaptive-vocab", () =>
{
    var adaptiveVocabReset = adaptiveVocabStore.Reset();
    lock (adaptiveRmsLock)
    {
        adaptiveRmsAverage = 0.0;
        adaptiveRmsCount = 0;
    }
    return Results.Ok(new { success = true, adaptiveVocabReset });
});

// ---- System soft-restart (Linux audio subsystem reinit) ----
app.MapPost("/api/system/restart", async () =>
{
    var steps = new List<string>();
    using var monitorRequest = CancellationTokenSource.CreateLinkedTokenSource(linuxMonitorStopping.Token);
    try
    {
        // 1. Unload only the loopback owned by this process.
        await linuxMonitorGate.WaitAsync(monitorRequest.Token);
        try
        {
            linuxMonitorFallbackReason = await linuxMonitorLoopback.UnloadAsync(monitorRequest.Token);
        }
        finally { linuxMonitorGate.Release(); }
        monitorRequest.Token.ThrowIfCancellationRequested();
        var monitorOk = linuxMonitorFallbackReason == null;
        steps.Add(monitorOk ? "owned monitor loopback stopped" : $"monitor loopback stop failed: {linuxMonitorFallbackReason}");

        // 2. Restore virtual mic (recreate if missing)
        const string vSink = LinuxVirtualMicSinkName;
        const string vSource = LinuxVirtualMicSourceName;
        const string vDesc = LinuxVirtualMicDescription;
        var vmResult = await EnsureVirtualMicAsync(vSink, vSource, vDesc);
        monitorRequest.Token.ThrowIfCancellationRequested();
        if (vmResult.ok)
        {
            ttsOutputSinkName = vSink;
            NativeLibraryBridge.SetTtsOutputDevice(nativeExe, vSink);
            steps.Add(vmResult.alreadyExisted ? "virtual mic already existed" : "virtual mic restored");
        }
        else
        {
            steps.Add($"virtual mic restore failed: {vmResult.error}");
        }

        // 3. Re-apply monitor loopback if configured
        if (monitorOk && linuxTtsConfig.MonitorVolumePercent > 0)
        {
            await EnsureLinuxMonitorPlaybackAsync(ttsOutputSinkName, linuxTtsConfig.MonitorVolumePercent, monitorRequest.Token);
            monitorOk = linuxMonitorFallbackReason == null;
            steps.Add(monitorOk ? $"monitor loopback re-applied at {linuxTtsConfig.MonitorVolumePercent}%" : $"monitor loopback failed: {linuxMonitorFallbackReason}");
        }

        // 4. Restart VOICEVOX if run path exists and process is dead
        if (File.Exists(voicevoxRunPath))
        {
            if (voicevoxProcess?.HasExited != false)
            {
                _ = Task.Run(() => ExtractAndStartVoicevoxAsync(CancellationToken.None));
                steps.Add("voicevox restart queued");
            }
            else
            {
                steps.Add("voicevox already running");
            }
        }

        return Results.Ok(new { ok = monitorOk, steps });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message, steps });
    }
});

(bool available, string? error, List<object> speakers) ProbeVoicevox()
{
    var speakers = new List<object>();
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        var resp = client.GetAsync($"{LinuxVoicevoxApi}/speakers").GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}", speakers);
        var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        foreach (var speaker in doc.RootElement.EnumerateArray())
        {
            var speakerName = speaker.TryGetProperty("name", out var nameProp) ? nameProp.ToString() : null;
            if (!speaker.TryGetProperty("styles", out var stylesProp)) continue;
            foreach (var style in stylesProp.EnumerateArray())
            {
                var styleId = style.TryGetProperty("id", out var sidProp) ? sidProp.ToString() : null;
                var styleName = style.TryGetProperty("name", out var snameProp) ? snameProp.ToString() : null;
                if (string.IsNullOrWhiteSpace(styleId)) continue;
                speakers.Add(new { id = styleId, name = string.IsNullOrWhiteSpace(styleName) ? speakerName : $"{speakerName} ({styleName})" });
            }
        }
        return (true, null, speakers);
    }
    catch (Exception ex)
    {
        return (false, ex.Message.Split('\n')[0], speakers);
    }
}

object VoicevoxInstallStateResponse()
{
    lock (voicevoxInstallLock)
    {
        var installed = File.Exists(voicevoxArchivePath);
        var progress = voicevoxInstallTotalBytes.HasValue && voicevoxInstallTotalBytes.Value > 0
            ? Math.Clamp((double)voicevoxInstallBytesReceived / voicevoxInstallTotalBytes.Value, 0.0, 1.0)
            : 0.0;
        return new
        {
            ok = true,
            installed,
            extracted = File.Exists(voicevoxRunPath),
            engineRunning = voicevoxProcess?.HasExited == false,
            phase = voicevoxInstallPhase,
            running = voicevoxInstallRunning,
            version = LinuxVoicevoxVersion,
            archivePath = voicevoxArchivePath,
            runPath = voicevoxRunPath,
            downloadUrl = LinuxVoicevoxDownloadUrl,
            bytesReceived = voicevoxInstallBytesReceived,
            totalBytes = voicevoxInstallTotalBytes,
            progress = Math.Round(progress, 4),
            error = voicevoxInstallError,
            startedAt = voicevoxInstallStartedAt?.ToString("O"),
            finishedAt = voicevoxInstallFinishedAt?.ToString("O")
        };
    }
}

async Task DownloadVoicevoxArchiveAsync(CancellationToken ct)
{
    Directory.CreateDirectory(voicevoxInstallDir);
    var tmpPath = voicevoxArchivePath + ".download";
    bool downloadSuccess = false;
    try
    {
        lock (voicevoxInstallLock) voicevoxInstallPhase = "download";
        using var resp = await voicevoxDownloadHttp.GetAsync(LinuxVoicevoxDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        lock (voicevoxInstallLock)
        {
            voicevoxInstallTotalBytes = resp.Content.Headers.ContentLength;
            voicevoxInstallBytesReceived = 0;
        }
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(tmpPath);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            lock (voicevoxInstallLock) voicevoxInstallBytesReceived += read;
        }
        output.Close();
        if (File.Exists(voicevoxArchivePath)) File.Delete(voicevoxArchivePath);
        File.Move(tmpPath, voicevoxArchivePath);
        lock (voicevoxInstallLock) voicevoxInstallError = null;
        downloadSuccess = true;
    }
    catch (Exception ex)
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        lock (voicevoxInstallLock) voicevoxInstallError = ex.Message;
    }
    finally
    {
        // On failure only: reset running state here.
        // On success: ExtractAndStartVoicevoxAsync will reset state after extract+start.
        if (!downloadSuccess)
        {
            lock (voicevoxInstallLock)
            {
                voicevoxInstallPhase = "idle";
                voicevoxInstallRunning = false;
                voicevoxInstallFinishedAt = DateTimeOffset.UtcNow;
            }
        }
    }
    // Chain to extract+start without restart
    if (downloadSuccess)
        await ExtractAndStartVoicevoxAsync(ct);
}

Task DrainVoicevoxOutputAsync(System.IO.TextReader reader, string streamName)
{
    return Task.Run(async () =>
    {
        try
        {
            while (await reader.ReadLineAsync() is not null) { }
        }
        catch (Exception ex)
        {
            logger!.LogDebug(ex, "VOICEVOX {Stream} drain stopped", streamName);
        }
    });
}

async Task ExtractAndStartVoicevoxAsync(CancellationToken ct)
{
    try
    {
        lock (voicevoxInstallLock) voicevoxInstallPhase = "extract";
        if (!File.Exists(voicevoxRunPath))
        {
            if (!File.Exists(voicevoxArchivePath))
                throw new FileNotFoundException("VOICEVOX archive not found", voicevoxArchivePath);
            if (Directory.Exists(voicevoxExtractDir)) Directory.Delete(voicevoxExtractDir, recursive: true);
            Directory.CreateDirectory(voicevoxExtractDir);
            var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xzf \"{voicevoxArchivePath}\" -C \"{voicevoxExtractDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("failed to start tar");
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"tar failed code={proc.ExitCode}: {stderr.Trim()}");
        }

        if (!File.Exists(voicevoxRunPath))
            throw new FileNotFoundException("VOICEVOX run file not found after extraction", voicevoxRunPath);

        lock (voicevoxInstallLock) voicevoxInstallPhase = "start";
        if (voicevoxProcess?.HasExited == false)
        {
            voicevoxInstallError = null;
            return;
        }
        var startInfo = new System.Diagnostics.ProcessStartInfo(voicevoxRunPath, "--host 127.0.0.1 --port 50021")
        {
            WorkingDirectory = Path.GetDirectoryName(voicevoxRunPath) ?? voicevoxExtractDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        voicevoxProcess = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start VOICEVOX engine");
        _ = DrainVoicevoxOutputAsync(voicevoxProcess.StandardOutput, "stdout");
        _ = DrainVoicevoxOutputAsync(voicevoxProcess.StandardError, "stderr");
        await Task.Delay(1500, ct);
        var probe = ProbeVoicevox();
        if (!probe.available)
            voicevoxInstallError = probe.error;
        else
            voicevoxInstallError = null;
    }
    catch (Exception ex)
    {
        lock (voicevoxInstallLock) voicevoxInstallError = ex.Message;
    }
    finally
    {
        lock (voicevoxInstallLock)
        {
            voicevoxInstallPhase = "idle";
            voicevoxInstallRunning = false;
            voicevoxInstallFinishedAt = DateTimeOffset.UtcNow;
        }
    }
}

if (File.Exists(voicevoxRunPath) || File.Exists(voicevoxArchivePath))
    _ = Task.Run(() => ExtractAndStartVoicevoxAsync(CancellationToken.None));

app.MapGet("/api/realtime/languages", () => Results.Ok(new
{
    defaultEngine = ResolveDefaultRealtimeEngine(),
    languagesByEngine = RealtimeLanguagePolicy.Catalog(!string.IsNullOrEmpty(whisperDllDir), moonshineModelRoot, JointRuntimeAvailable()),
    senseVoiceAvailable = senseVoiceService?.IsAvailable == true,
    paraformerZhAvailable = paraformerZhService?.IsAvailable == true
}));
app.MapGet("/api/tts/engines", () =>
{
    var models = EnumeratePiperPlusModels()
        .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .Select(m => new
        {
            id = m.Id,
            name = m.Name,
            path = Path.GetDirectoryName(m.ModelPath) ?? string.Empty,
            modelPath = m.ModelPath,
            configPath = m.ConfigPath,
            language = m.Language,
            license = m.License,
            sourcePageUrl = m.SourcePageUrl,
            runtime = m.Runtime,
            fallbackNote = m.Language.Contains("ko", StringComparison.OrdinalIgnoreCase) ? null : "ko_tts_specific_model_not_bundled_use_default_voice"
        })
        .ToArray();
    var piperModels = models.Where(m => string.Equals(m.runtime, "piper", StringComparison.OrdinalIgnoreCase)).ToArray();
    var piperPlusModels = models.Where(m => string.Equals(m.runtime, "piper_plus", StringComparison.OrdinalIgnoreCase)).ToArray();
    var piperRuntimeAvailable = File.Exists(ResolvePiperExe());
    var piperPlusRuntimeAvailable = File.Exists(ResolvePiperPlusExe());
    var voicevox = ProbeVoicevox();
    var kokoroModelList = EnumerateKokoroModels(kokoroModelRoot).ToArray();
    var mossModelList = EnumerateMossModels(mossModelRoot).ToArray();
    return Results.Ok(new
    {
        engines = new object[]
        {
            new { id = "voicevox", name = "VOICEVOX", available = voicevox.available, error = voicevox.error, errorCode = voicevox.error == null ? null : "tts.voicevox_unavailable" },
            new { id = "piper", name = "Piper", available = piperRuntimeAvailable && piperModels.Length > 0, error = (string?)null, errorCode = (string?)null },
            new { id = "piper_plus", name = "Piper-Plus", available = piperPlusRuntimeAvailable && piperPlusModels.Length > 0, error = (string?)null, errorCode = (string?)null },
            new { id = "kokoro", name = "Kokoro", available = kokoroModelList.Any(), error = kokoroService.LastError, errorCode = kokoroModelList.Any() ? null : "tts.kokoro_unavailable" },
            new { id = "moss", name = "MOSS", available = mossModelList.Any(), error = mossService.LastError, errorCode = mossModelList.Any() ? null : "tts.moss_unavailable" }
        },
        piperModels = piperModels,
        piperPlusModels = piperPlusModels,
        voicevoxSpeakers = voicevox.speakers,
        kokoroModels = kokoroModelList,
        mossModels = mossModelList,
        koFallbackReason = "ko_piper_model_not_bundled"
    });
});

app.MapGet("/api/voicevox/install-status", () => Results.Ok(VoicevoxInstallStateResponse()));

app.MapGet("/api/vcclient/install-status", () => Results.Ok(new
{
    ok = true,
    supported = false,
    installed = false,
    extracted = false,
    engineRunning = false,
    phase = "manual",
    running = false,
    version = "manual",
    downloadUrl = string.Empty,
    officialUrl = LinuxVcClientOfficialSiteUrl,
    bytesReceived = 0,
    totalBytes = (long?)null,
    progress = 0.0,
    error = "VCClient Linux x86-64 RVC binary is not distributed as a ready zip. Open the official repository and follow the Linux clone/setup instructions."
}));

app.MapPost("/api/vcclient/official-site", () =>
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "xdg-open",
            Arguments = LinuxVcClientOfficialSiteUrl,
            UseShellExecute = false
        });
        return Results.Ok(new { ok = true, url = LinuxVcClientOfficialSiteUrl });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to open VCClient official site via xdg-open");
        return Results.Ok(new { ok = false, url = LinuxVcClientOfficialSiteUrl, error = ex.Message });
    }
});

app.MapPost("/api/vcclient/install", () => Results.BadRequest(new
{
    ok = false,
    supported = false,
    officialUrl = LinuxVcClientOfficialSiteUrl,
    error = "Automatic VCClient install is Windows-only. On Linux, open the official repository and follow the clone/setup instructions."
}));

app.MapPost("/api/voicevox/official-site", () =>
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "xdg-open",
            Arguments = LinuxVoicevoxOfficialSiteUrl,
            UseShellExecute = false
        });
        return Results.Ok(new { ok = true, url = LinuxVoicevoxOfficialSiteUrl });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to open VOICEVOX official site via xdg-open");
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/voicevox/install", () =>
{
    lock (voicevoxInstallLock)
    {
        if (voicevoxInstallRunning)
            return Results.Conflict(new { ok = false, error = "VOICEVOX install is already running" });
        voicevoxInstallRunning = true;
        voicevoxInstallBytesReceived = 0;
        voicevoxInstallTotalBytes = null;
        voicevoxInstallError = null;
        voicevoxInstallStartedAt = DateTimeOffset.UtcNow;
        voicevoxInstallFinishedAt = null;
    }
    _ = Task.Run(() => File.Exists(voicevoxArchivePath)
        ? ExtractAndStartVoicevoxAsync(CancellationToken.None)
        : DownloadVoicevoxArchiveAsync(CancellationToken.None));
    return Results.Ok(VoicevoxInstallStateResponse());
});

// ---- Piper Voice DL APIs ----
app.MapGet("/api/tts/voices/catalog", () =>
{
    var catalogPath = CurrentPiperVoiceCatalogPath();
    var voices = LoadPiperVoiceCatalog();
    return Results.Ok(new
    {
        source = string.Equals(catalogPath, piperVoiceCatalogCachePath, StringComparison.OrdinalIgnoreCase) ? "cache" : "bundled",
        remoteConfigured = !string.IsNullOrWhiteSpace(piperVoiceCatalogRemoteUrl),
        voices = voices.Select(v => new
        {
            id = v.Id,
            name = v.Name,
            language = v.Language,
            languageDisplay = v.LanguageDisplay,
            quality = v.Quality,
            runtime = v.Runtime,
            platform = v.Platform,
            license = v.License,
            sourcePageUrl = v.SourcePageUrl,
            sizeMb = v.SizeMb,
            installed = MobMate.Core.Tts.PiperVoiceDownloadManager.IsInstalled(v.Id, piperVoiceModelRoot)
        }).ToArray()
    });
});

app.MapPost("/api/tts/voices/catalog/update", async (CancellationToken ct) =>
{
    var result = await TryUpdatePiperVoiceCatalogAsync(ct);
    return Results.Ok(new
    {
        ok = result.Ok,
        code = result.Code,
        count = result.Count,
        source = File.Exists(piperVoiceCatalogCachePath) ? "cache" : "bundled",
        remoteConfigured = !string.IsNullOrWhiteSpace(piperVoiceCatalogRemoteUrl)
    });
});

app.MapPost("/api/tts/voices/download", async (HttpRequest request, CancellationToken ct) =>
{
    string id = string.Empty;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        if (body.RootElement.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
    }
    catch { return Results.BadRequest(new { error = "invalid JSON body" }); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(new { error = "id is required" });

    var voices = LoadPiperVoiceCatalog();
    var voice = voices.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice is null)
        return Results.NotFound(new { error = $"voice '{id}' not found in catalog" });

    if (string.Equals(voice.Platform, "windows", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = $"voice '{id}' is Windows-only and cannot be downloaded on Linux" });

    if (string.IsNullOrEmpty(voice.OnnxUrl))
        return Results.BadRequest(new { error = "voice has no onnxUrl in catalog" });

    Directory.CreateDirectory(piperVoiceModelRoot);
    piperVoiceDlManager.StartDownload(voice, piperVoiceModelRoot);
    return Results.Ok(new { ok = true, id });
});

app.MapGet("/api/tts/voices/download/{id}/status", (string id) =>
{
    var state = piperVoiceDlManager.GetState(id);
    return Results.Ok(new
    {
        id,
        status = state.Status.ToString().ToLowerInvariant(),
        progress = Math.Round(state.Progress, 3),
        error = state.Error
    });
});

app.MapDelete("/api/tts/voices/{id}", (string id) =>
{
    piperVoiceDlManager.Delete(id, piperVoiceModelRoot);
    return Results.Ok(new { ok = true, id });
});

// ---- Voice Conversion Phase 1 APIs ----
app.MapGet("/api/voice-conversion/status", () =>
{
    var count = VoiceConversionModelDownloadManager.CountInstalled(voiceConversionModelRoot);
    var phase1 = VoiceConversionRuntimeStatus.Phase1(count);
    NativeLibraryResult? nativeResult = null;
    bool sessionCreated;
    lock (voiceConversionSessionLock)
    {
        sessionCreated = voiceConversionSessionHandle != IntPtr.Zero;
        if (sessionCreated)
        {
            nativeResult = NativeLibraryBridge.VoiceConversionPollStatus(voiceConversionSessionHandle);
            mobfyOnset.ObserveStatus(nativeResult.RawJson);
        }
    }
    var nativeStatus = nativeResult is null ? null : VoiceConversionNativeStatusObject(nativeResult);
    var running = nativeResult?.RawJson.Contains("\"running\":true", StringComparison.OrdinalIgnoreCase) == true;
    return Results.Ok(new
    {
        enabled = phase1.Enabled,
        running,
        mode = phase1.Mode,
        message = phase1.Message,
        installedModelCount = phase1.InstalledModelCount,
        nativeSessionCreated = sessionCreated,
        nativeStatus
    });
});

app.MapPost("/api/voice-conversion/start", async (HttpRequest request, CancellationToken ct) =>
{
    var operationId = Interlocked.Increment(ref voiceConversionOperationId);
    string optionsJson;
    using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        optionsJson = await reader.ReadToEndAsync(ct);
    if (string.IsNullOrWhiteSpace(optionsJson)) optionsJson = "{}";
    voiceConversionLogger.LogInformation("[VC] op={OperationId} action=start requested rawOptions={Options}", operationId, optionsJson);
    optionsJson = ResolveVoiceConversionModelOptionsJson(optionsJson, ensureAudibleLiveOutput: true);
    voiceConversionLogger.LogInformation("[VC] op={OperationId} action=options_resolved options={Options}", operationId, optionsJson);
    try
    {
        using var optionsDocument = JsonDocument.Parse(optionsJson);
        if (optionsDocument.RootElement.ValueKind != JsonValueKind.Object)
            return Results.Problem(detail: "invalid voice conversion options", statusCode: StatusCodes.Status400BadRequest);
    }
    catch (JsonException)
    {
        return Results.Problem(detail: "invalid voice conversion options", statusCode: StatusCodes.Status400BadRequest);
    }

    lock (voiceConversionSessionLock)
    {
        if (!MobfyOnsetSelection.Enabled(uiSettingsDocument)) Volatile.Write(ref mobfyOnsetPreference, false);
        mobfyOnset.BeforeStart(optionsJson, mobfyOnsetLanguage, Volatile.Read(ref mobfyOnsetPreference));
        if (voiceConversionSessionHandle != IntPtr.Zero)
        {
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=stop_existing begin", operationId);
            NativeLibraryBridge.VoiceConversionStop(voiceConversionSessionHandle);
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=stop_existing returned; action=destroy_existing begin", operationId);
            NativeLibraryBridge.VoiceConversionDestroy(voiceConversionSessionHandle);
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=destroy_existing returned", operationId);
            voiceConversionSessionHandle = IntPtr.Zero;
        }

        if (voiceConversionSessionHandle == IntPtr.Zero)
        {
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=create begin", operationId);
            var create = NativeLibraryBridge.VoiceConversionCreate(nativeExe, optionsJson, out voiceConversionSessionHandle);
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=create returned success={Success} handleValid={HandleValid} msg={Message}", operationId, create.Success, voiceConversionSessionHandle != IntPtr.Zero, create.Message);
            if (!create.Success)
            {
                mobfyOnset.Stop();
                return Results.Problem(detail: create.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
        mobfyOnset.Attach();
        voiceConversionLogger.LogInformation("[VC] op={OperationId} action=native_start begin", operationId);
        var start = NativeLibraryBridge.VoiceConversionStart(voiceConversionSessionHandle);
        voiceConversionLogger.LogInformation("[VC] op={OperationId} action=native_start returned success={Success} msg={Message}", operationId, start.Success, start.Message);
        if (!start.Success)
        {
            mobfyOnset.Stop();
            return Results.Problem(detail: start.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // A model selection, engine, or raw RVC mode requests conversion;
        // an empty model selection is the existing DSP passthrough.
        if (VoiceConversionRequested(optionsJson))
        {
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=load_models begin", operationId);
            NativeLibraryResult loadResult;
            try { loadResult = NativeLibraryBridge.VoiceConversionLoadModels(voiceConversionSessionHandle); }
            catch (Exception ex)
            {
                voiceConversionLogger.LogWarning(ex, "[VC] op={OperationId} load_models threw", operationId);
                loadResult = new NativeLibraryResult { Success = false, Message = ex.Message };
            }
            voiceConversionLogger.LogInformation("[VC] op={OperationId} action=load_models returned success={Success} msg={Message}", operationId, loadResult.Success, loadResult.Message);
            if (!loadResult.Success)
            {
                mobfyOnset.Stop();
                try
                {
                    var stop = NativeLibraryBridge.VoiceConversionStop(voiceConversionSessionHandle);
                    voiceConversionLogger.LogInformation("[VC] op={OperationId} action=stop_after_load_failure success={Success} msg={Message}", operationId, stop.Success, stop.Message);
                }
                catch (Exception ex) { voiceConversionLogger.LogWarning(ex, "[VC] op={OperationId} stop after load failure threw", operationId); }
                try
                {
                    NativeLibraryBridge.VoiceConversionDestroy(voiceConversionSessionHandle);
                    voiceConversionSessionHandle = IntPtr.Zero;
                }
                catch (Exception ex) { voiceConversionLogger.LogWarning(ex, "[VC] op={OperationId} destroy after load failure threw", operationId); }
                return Results.Problem(detail: loadResult.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        voiceConversionLogger.LogInformation("[VC] op={OperationId} action=first_poll begin", operationId);
        PrepareMobfyOnset();
        var status = NativeLibraryBridge.VoiceConversionPollStatus(voiceConversionSessionHandle);
        mobfyOnset.ObserveStatus(status.RawJson);
        voiceConversionLogger.LogInformation("[VC] op={OperationId} action=first_poll returned success={Success} status={Status}", operationId, status.Success, status.RawJson);
        UnlockSteamAchievement(SteamAchievementService.FirstMobfyStart);
        return Results.Ok(new { ok = true, status = VoiceConversionNativeStatusObject(status) });
    }
});

app.MapPost("/api/voice-conversion/stop", () =>
{
    lock (voiceConversionSessionLock)
    {
        mobfyOnset.Stop();
        if (voiceConversionSessionHandle == IntPtr.Zero)
            return Results.Ok(new { ok = true, status = new { ok = true, mode = "voice_conversion", created = false, running = false } });
        var stop = NativeLibraryBridge.VoiceConversionStop(voiceConversionSessionHandle);
        if (!stop.Success)
            return Results.Problem(detail: stop.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        var status = NativeLibraryBridge.VoiceConversionPollStatus(voiceConversionSessionHandle);
        return Results.Ok(new { ok = true, status = VoiceConversionNativeStatusObject(status) });
    }
});

app.MapPost("/api/voice-conversion/load-models", () =>
{
    lock (voiceConversionSessionLock)
    {
        if (voiceConversionSessionHandle == IntPtr.Zero)
            return Results.BadRequest(ApiError("session not created; call /start first", "voice_conversion.not_created"));
        NativeLibraryResult result;
        try { result = NativeLibraryBridge.VoiceConversionLoadModels(voiceConversionSessionHandle); }
        catch (Exception ex)
        {
            voiceConversionLogger.LogWarning(ex, "[VC] load_models threw");
            result = new NativeLibraryResult { Success = false, Message = ex.Message };
        }
        if (!result.Success)
        {
            mobfyOnset.Stop();
            try
            {
                var stop = NativeLibraryBridge.VoiceConversionStop(voiceConversionSessionHandle);
                voiceConversionLogger.LogInformation("[VC] action=stop_after_load_failure success={Success} msg={Message}", stop.Success, stop.Message);
            }
            catch (Exception ex) { voiceConversionLogger.LogWarning(ex, "[VC] stop after load failure threw"); }
            try
            {
                NativeLibraryBridge.VoiceConversionDestroy(voiceConversionSessionHandle);
                voiceConversionSessionHandle = IntPtr.Zero;
            }
            catch (Exception ex) { voiceConversionLogger.LogWarning(ex, "[VC] destroy after load failure threw"); }
            return Results.Problem(detail: result.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        PrepareMobfyOnset();
        var status = NativeLibraryBridge.VoiceConversionPollStatus(voiceConversionSessionHandle);
        mobfyOnset.ObserveStatus(status.RawJson);
        return Results.Ok(new { ok = true, status = VoiceConversionNativeStatusObject(status) });
    }
});

app.MapPost("/api/voice-conversion/unload-models", () =>
{
    lock (voiceConversionSessionLock)
    {
        mobfyOnset.Invalidate();
        if (voiceConversionSessionHandle == IntPtr.Zero)
            return Results.Ok(new { ok = true, message = "no session" });
        NativeLibraryBridge.VoiceConversionUnloadModels(voiceConversionSessionHandle);
        return Results.Ok(new { ok = true, message = "models unloaded" });
    }
});

app.MapGet("/api/voice-conversion/models/catalog", () =>
{
    var models = LoadVoiceConversionCatalog();
    return Results.Ok(new
    {
        models = models.Select(m => new
        {
            id = m.Id,
            displayName = m.DisplayName,
            engine = m.Engine,
            downloadUrl = m.DownloadUrl,
            sizeBytes = m.SizeBytes,
            sha256 = m.Sha256,
            license = m.License,
            licenseUrl = m.LicenseUrl,
            sourcePageUrl = m.SourcePageUrl,
            riskNotice = m.RiskNotice,
            requiresUserAgreement = m.RequiresUserAgreement,
            installed = VoiceConversionModelDownloadManager.IsInstalled(m.Id, voiceConversionModelRoot),
            setupState = VoiceConversionModelDownloadManager.GetSetupState(m.Id, voiceConversionModelRoot)
        }).ToArray()
    });
});

app.MapGet("/api/voice-conversion/models/status", (string? id) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiError("id is required", "voice_conversion.id_required"));
    var state = voiceConversionDlManager.GetState(id);
    return Results.Ok(new
    {
        id,
        download = new
        {
            status = state.Status.ToString().ToLowerInvariant(),
            progress = Math.Round(state.Progress, 3),
            error = state.Error
        },
        setupState = VoiceConversionModelDownloadManager.GetSetupState(id, voiceConversionModelRoot),
        installed = VoiceConversionModelDownloadManager.IsInstalled(id, voiceConversionModelRoot)
    });
});

app.MapPost("/api/voice-conversion/models/download", async (HttpRequest request, CancellationToken ct) =>
{
    string id = string.Empty;
    bool acceptLicense = false;
    bool force = false;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = body.RootElement;
        if (root.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
        if (root.TryGetProperty("acceptLicense", out var acceptProp)) acceptLicense = acceptProp.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("force", out var forceProp)) force = forceProp.ValueKind == JsonValueKind.True;
    }
    catch { return Results.BadRequest(ApiError("invalid JSON body", "voice_conversion.invalid_body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiError("id is required", "voice_conversion.id_required"));

    var model = LoadVoiceConversionCatalog().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    if (model is null)
        return Results.NotFound(ApiError($"model '{id}' not found in catalog", "voice_conversion.not_found"));

    if (model.RequiresUserAgreement && !acceptLicense)
        return Results.BadRequest(ApiError("license agreement is required", "voice_conversion.license_required"));

    try
    {
        Directory.CreateDirectory(voiceConversionModelRoot);
        var result = await voiceConversionDlManager.DownloadAndInstallAsync(model, voiceConversionModelRoot, force, ct);
        return Results.Ok(new { ok = result.Ok, id = result.Id, installed = result.Installed, message = result.Message });
    }
    catch (Exception ex)
    {
        voiceConversionLogger.LogWarning(ex, "Voice conversion model download failed: {Id}", id);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/voice-conversion/models/import", async (HttpRequest request, CancellationToken ct) =>
{
    string sourcePath = string.Empty;
    string id = string.Empty;
    string displayName = string.Empty;
    string engine = "rvc_onnx";
    bool acceptLicense = false;
    bool force = false;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = body.RootElement;
        if (root.TryGetProperty("sourcePath", out var srcProp)) sourcePath = srcProp.GetString() ?? string.Empty;
        if (root.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
        if (root.TryGetProperty("displayName", out var nameProp)) displayName = nameProp.GetString() ?? string.Empty;
        if (root.TryGetProperty("engine", out var engineProp)) engine = engineProp.GetString() ?? engine;
        if (root.TryGetProperty("acceptLicense", out var acceptProp)) acceptLicense = acceptProp.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("force", out var forceProp)) force = forceProp.ValueKind == JsonValueKind.True;
    }
    catch { return Results.BadRequest(ApiError("invalid JSON body", "voice_conversion.invalid_body")); }

    if (string.IsNullOrWhiteSpace(sourcePath))
        return Results.BadRequest(ApiError("sourcePath is required", "voice_conversion.source_required"));
    if (string.IsNullOrWhiteSpace(id))
        id = Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.IsNullOrWhiteSpace(displayName)) displayName = id;
    if (!string.Equals(engine, "rvc_onnx", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(engine, "seedvc_ncnn", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(ApiError("engine must be rvc_onnx or seedvc_ncnn", "voice_conversion.invalid_engine"));
    if (!acceptLicense)
        return Results.BadRequest(ApiError("confirm rights and usage terms with acceptLicense=true", "voice_conversion.license_required"));

    var model = new VoiceConversionModelCatalogItem(
        Id: id,
        DisplayName: displayName,
        Engine: engine,
        DownloadUrl: string.Empty,
        SizeBytes: 0,
        Sha256: string.Empty,
        License: "User supplied",
        LicenseUrl: string.Empty,
        SourcePageUrl: sourcePath,
        RiskNotice: "User supplied voice conversion model. Verify rights and usage terms before use.",
        RequiresUserAgreement: false);

    try
    {
        Directory.CreateDirectory(voiceConversionModelRoot);
        var result = await voiceConversionDlManager.ImportFromPathAsync(sourcePath, model, voiceConversionModelRoot, force, ct);
        if (result.Ok) UnlockSteamAchievement(SteamAchievementService.FirstVcModelImport);
        return Results.Ok(new { ok = result.Ok, id = result.Id, installed = result.Installed, message = result.Message });
    }
    catch (Exception ex)
    {
        voiceConversionLogger.LogWarning(ex, "Voice conversion model import failed: {Id} from {SourcePath}", id, sourcePath);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/voice-conversion/models/vcclient-export", async (HttpRequest request, CancellationToken ct) =>
{
    string id = string.Empty;
    string vcclientBaseUrl = "http://127.0.0.1:18000";
    int slot = 0;
    bool acceptPthRisk = false;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = body.RootElement;
        if (root.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
        if (root.TryGetProperty("vcclientBaseUrl", out var urlProp)) vcclientBaseUrl = urlProp.GetString() ?? vcclientBaseUrl;
        if (root.TryGetProperty("slot", out var slotProp) && slotProp.TryGetInt32(out var n)) slot = n;
        if (root.TryGetProperty("acceptPthRisk", out var acceptProp)) acceptPthRisk = acceptProp.ValueKind == JsonValueKind.True;
    }
    catch { return Results.BadRequest(ApiError("invalid JSON body", "voice_conversion.invalid_body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiError("id is required", "voice_conversion.id_required"));
    if (!acceptPthRisk)
        return Results.BadRequest(ApiError("acceptPthRisk=true is required", "voice_conversion.pth_risk_required"));
    try
    {
        var result = await voiceConversionDlManager.ExportPthWithVcClientAsync(id, voiceConversionModelRoot, vcclientBaseUrl, slot, acceptPthRisk, ct);
        return Results.Ok(new { ok = result.Ok, id = result.Id, installed = result.Installed, message = result.Message });
    }
    catch (Exception ex)
    {
        voiceConversionLogger.LogWarning(ex, "VCClient model export failed: {Id}", id);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ---- Rich TTS (Kokoro / MOSS) APIs ----
app.MapGet("/api/tts/rich/catalog", () =>
{
    var voices = LoadRichVoiceCatalog();
    return Results.Ok(new
    {
        voices = voices.Select(v => new
        {
            id = v.Id,
            name = v.Name,
            engine = v.Engine,
            language = v.Language,
            languageDisplay = v.LanguageDisplay,
            quality = v.Quality,
            runtime = v.Runtime,
            platform = v.Platform,
            license = v.License,
            sourcePageUrl = v.SourcePageUrl,
            sizeMb = v.SizeMb,
            defaultSpeakerId = v.DefaultSpeakerId,
            speakerCount = v.SpeakerCount,
            speakers = v.Speakers,
            installed = string.Equals(v.Engine, "moss", StringComparison.OrdinalIgnoreCase)
                ? MobMate.Core.Tts.MossModelDownloadManager.IsInstalled(v.Id, mossModelRoot)
                : MobMate.Core.Tts.KokoroModelDownloadManager.IsInstalled(v.Id, kokoroModelRoot)
        }).ToArray()
    });
});

app.MapPost("/api/tts/rich/download", async (HttpRequest request, CancellationToken ct) =>
{
    string id = string.Empty;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        if (body.RootElement.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
    }
    catch { return Results.BadRequest(ApiError("invalid JSON body", "tts.rich.invalid_body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiError("id is required", "tts.rich.id_required"));

    var voice = LoadRichVoiceCatalog().FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice is null)
        return Results.NotFound(ApiError($"voice '{id}' not found in rich catalog", "tts.rich.not_found"));

    if (string.Equals(voice.Engine, "moss", StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(mossModelRoot);
        mossDlManager.StartDownload(voice.Id, mossModelRoot);
        return Results.Ok(new { ok = true, id });
    }

    if (string.IsNullOrEmpty(voice.TarUrl))
        return Results.BadRequest(ApiError("voice has no tarUrl in catalog", "tts.rich.no_url"));

    Directory.CreateDirectory(kokoroModelRoot);
    kokoroDlManager.StartDownload(voice, kokoroModelRoot);
    return Results.Ok(new { ok = true, id });
});

app.MapGet("/api/tts/rich/download/{id}/status", (string id) =>
{
    var mossState = mossDlManager.GetState(id);
    var state = mossState.Status != MobMate.Core.Tts.RichDownloadStatus.Idle
        ? mossState
        : kokoroDlManager.GetState(id);
    return Results.Ok(new
    {
        id,
        status = state.Status.ToString().ToLowerInvariant(),
        progress = Math.Round(state.Progress, 3),
        error = state.Error
    });
});

app.MapDelete("/api/tts/rich/{id}", (string id) =>
{
    var voice = LoadRichVoiceCatalog().FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice != null && string.Equals(voice.Engine, "moss", StringComparison.OrdinalIgnoreCase))
        mossDlManager.Delete(id, mossModelRoot);
    else
        kokoroDlManager.Delete(id, kokoroModelRoot);
    return Results.Ok(new { ok = true, id });
});

app.MapGet("/api/virtual-audio/status", () => Results.Ok(new { ok = true, installed = true, message = "PipeWire/PulseAudio" }));
app.MapGet("/api/virtual-audio/install-status", () => Results.Ok(new { ok = true, running = false, installed = true }));
app.MapPost("/api/virtual-audio/install", () => Results.Ok(new { ok = true, installed = true }));
app.MapPost("/api/virtual-audio/apply", () =>
{
    UnlockSteamAchievement(SteamAchievementService.VirtualAudioReady);
    UnlockSteamAchievement(SteamAchievementService.SetupComplete);
    return Results.Ok(ApiOk());
});
app.MapPost("/api/virtual-audio/open-sound-settings", () => Results.Ok(ApiOk()));
app.MapPost("/api/external/discord-voice-settings", () =>
{
    try
    {
        StartDetached("xdg-open", "https://discord.gg/nrXFheFWqP");
    }
    catch (Exception ex)
    {
        try
        {
            StartDetached("gio", "open https://discord.gg/nrXFheFWqP");
        }
        catch (Exception gioEx)
        {
            return Results.Problem(detail: $"{ex.Message}; {gioEx.Message}", statusCode: 500);
        }
    }
    UnlockSteamAchievement(SteamAchievementService.FirstDiscordGuide);
    return Results.Ok(new { ok = true });
});
// ── Pending Speech API ───────────────────────────────────────
app.MapGet("/api/pending-speech/config", () =>
{
    var cfg = pendingSpeechService.GetConfig();
    return Results.Ok(new { mode = cfg.Mode, waitSeconds = cfg.WaitSeconds, maxQueue = cfg.MaxQueue });
});

app.MapPost("/api/pending-speech/config", async (HttpRequest request) =>
{
    try
    {
        var cfg = pendingSpeechService.GetConfig();
        if (request.ContentLength is > 0)
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("mode", out var modeProp))
                cfg.Mode = modeProp.GetString() ?? cfg.Mode;
            if (body.RootElement.TryGetProperty("waitSeconds", out var waitProp) && waitProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                cfg.WaitSeconds = waitProp.GetInt32();
            if (body.RootElement.TryGetProperty("maxQueue", out var maxProp) && maxProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                cfg.MaxQueue = maxProp.GetInt32();
        }
        ttsRouter.InvalidateOnsetConfiguration();
        pendingSpeechService.ApplyConfig(cfg);
        ttsRouter.InvalidateOnsetConfiguration();
        var applied = pendingSpeechService.GetConfig();
        return Results.Ok(new { ok = true, mode = applied.Mode, waitSeconds = applied.WaitSeconds, maxQueue = applied.MaxQueue });
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapGet("/api/pending-speech/status", () =>
{
    var status = pendingSpeechService.GetStatus();
    return Results.Ok(new
    {
        mode             = status.Mode,
        waitSeconds      = status.WaitSeconds,
        maxQueue         = status.MaxQueue,
        pendingCount     = status.PendingCount,
        remainingSeconds = status.RemainingSeconds,
        items            = status.Items.Select(x => new { id = x.Id, slot = x.Slot, text = x.Text, remainingSeconds = x.RemainingSeconds })
    });
});

app.MapPost("/api/pending-speech/approve", async (HttpRequest request, CancellationToken ct) =>
{
    int slot = 1;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (body.RootElement.TryGetProperty("slot", out var slotProp) && slotProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                slot = slotProp.GetInt32();
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    await pendingSpeechService.ApproveBySlotAsync(slot, async (text, tok) => { await SpeakWithOutputRoutingAsync(text, false, null); }, ct);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/pending-speech/cancel", async (HttpRequest request, CancellationToken ct) =>
{
    int slot = 1;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (body.RootElement.TryGetProperty("slot", out var slotProp) && slotProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                slot = slotProp.GetInt32();
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    await pendingSpeechService.CancelBySlotAsync(slot, ct);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/pending-speech/clear", async (CancellationToken ct) =>
{
    await pendingSpeechService.ClearAsync(ct);
    return Results.Ok(new { ok = true });
});
// ── Radio Chat API ────────────────────────────────────────────
app.MapGet("/api/radio/status", () =>
{
    var s = radioChatService.GetStatus();
    var c = radioChatService.GetConfig();
    var gnomeShortcutReady = EnsureGnomeRadioShortcutCurrent(c.Shortcut.Display, radioChatLogger);
    return Results.Ok(new
    {
        enabled = s.Enabled,
        overlayVisible = s.OverlayVisible,
        commandCount = s.CommandCount,
        commandsPath = s.CommandsPath,
        hotkeyRegistered = s.HotkeyRegistered || IsUserServiceActive("mobmate-radio-hotkey.service") || gnomeShortcutReady,
        platform = s.Platform,
        triggerCmd = s.TriggerCmd
    });
});

app.MapGet("/api/radio/config", () =>
{
    var c = radioChatService.GetConfig();
    return Results.Ok(new
    {
        enabled = c.Enabled,
        commandsPath = c.CommandsPath,
        shortcut = new { display = c.Shortcut.Display, modifier = c.Shortcut.Modifier, key = c.Shortcut.Key },
        overlay = new { position = c.Overlay.Position, fontSize = c.Overlay.FontSize, opacity = c.Overlay.Opacity, margin = c.Overlay.Margin, maxLines = c.Overlay.MaxLines, background = c.Overlay.Background, foreground = c.Overlay.Foreground, pageChangeKey = c.Overlay.PageChangeKey }
    });
});

app.MapPost("/api/radio/config", async (HttpRequest request) =>
{
    var cfg = radioChatService.GetConfig();
    string? requestedShortcutDisplay = null;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("enabled", out var enabledProp) && (enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False))
                cfg.Enabled = enabledProp.GetBoolean();
            if (body.RootElement.TryGetProperty("commandsPath", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
                cfg.CommandsPath = pathProp.GetString() ?? cfg.CommandsPath;
            if (body.RootElement.TryGetProperty("shortcut", out var shProp) && shProp.ValueKind == JsonValueKind.Object)
            {
                if (shProp.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    requestedShortcutDisplay = d.GetString();
                    cfg.Shortcut.Display = requestedShortcutDisplay ?? cfg.Shortcut.Display;
                }
                if (shProp.TryGetProperty("modifier", out var m) && m.ValueKind == JsonValueKind.String) cfg.Shortcut.Modifier = m.GetString() ?? cfg.Shortcut.Modifier;
                if (shProp.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String) cfg.Shortcut.Key = k.GetString() ?? cfg.Shortcut.Key;
            }
            if (body.RootElement.TryGetProperty("overlay", out var ovProp) && ovProp.ValueKind == JsonValueKind.Object)
            {
                if (ovProp.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.String) cfg.Overlay.Position = pos.GetString() ?? cfg.Overlay.Position;
                if (ovProp.TryGetProperty("fontSize", out var fs) && fs.ValueKind == JsonValueKind.Number) cfg.Overlay.FontSize = fs.GetInt32();
                if (ovProp.TryGetProperty("opacity", out var op) && op.ValueKind == JsonValueKind.Number) cfg.Overlay.Opacity = op.GetDouble();
                if (ovProp.TryGetProperty("margin", out var mg) && mg.ValueKind == JsonValueKind.Number) cfg.Overlay.Margin = mg.GetInt32();
                if (ovProp.TryGetProperty("maxLines", out var ml) && ml.ValueKind == JsonValueKind.Number) cfg.Overlay.MaxLines = ml.GetInt32();
                if (ovProp.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.String) cfg.Overlay.Background = bg.GetString() ?? cfg.Overlay.Background;
                if (ovProp.TryGetProperty("foreground", out var fg) && fg.ValueKind == JsonValueKind.String) cfg.Overlay.Foreground = fg.GetString() ?? cfg.Overlay.Foreground;
                if (ovProp.TryGetProperty("pageChangeKey", out var pc) && pc.ValueKind == JsonValueKind.String) cfg.Overlay.PageChangeKey = pc.GetString() ?? cfg.Overlay.PageChangeKey;
            }
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    radioChatService.ApplyConfig(cfg);
    if (!string.IsNullOrWhiteSpace(requestedShortcutDisplay))
    {
        var kdeShortcut = NormalizeKdeShortcutForRadio(requestedShortcutDisplay);
        if (kdeShortcut is null) return Results.BadRequest(ApiMessage("radio.invalid_shortcut", "Shortcut must look like Ctrl+Shift+F4"));
        if (TryInstallGnomeRadioShortcut(kdeShortcut, radioChatLogger) || TryUpdateKdePortalRadioShortcut(kdeShortcut, radioChatLogger))
        {
            cfg.Shortcut.Display = kdeShortcut;
            radioChatService.ApplyConfig(cfg);
            if (!IsGnomeSession()) RestartUserServiceForRadio("mobmate-radio-hotkey.service", radioChatLogger);
        }
    }
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/radio/commands", () =>
{
    var cmds = radioChatService.GetCommands();
    return Results.Ok(new { commands = cmds.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => new { key = x.Key, text = x.Value }) });
});

app.MapPost("/api/radio/commands", async (HttpRequest request) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        var page = body.RootElement.TryGetProperty("page", out var p) ? p.GetInt32() : -1;
        var digit = body.RootElement.TryGetProperty("digit", out var d) ? d.GetInt32() : -1;
        var text = body.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
        radioChatService.UpsertCommand(page, digit, text);
        return Results.Ok(new { ok = true });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapPost("/api/radio/speak", async (HttpRequest request, CancellationToken ct) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var page = body.RootElement.TryGetProperty("page", out var p) ? p.GetInt32() : -1;
        var digit = body.RootElement.TryGetProperty("digit", out var d) ? d.GetInt32() : -1;
        if (page == -1)
        {
            if (digit == 5)
            {
                await pendingSpeechService.CancelFirstAsync(ct);
                return Results.Ok(new { ok = true, action = "pending_cancel" });
            }
            await pendingSpeechService.ApproveBySlotAsync(digit, async (text, tok) => { await SpeakWithOutputRoutingAsync(text, false, null); }, ct);
            return Results.Ok(new { ok = true, action = "pending_approve", slot = digit });
        }

        var ok = await radioChatService.SpeakSlotAsync(page, digit, async (text, token) =>
        {
            await pendingSpeechService.EnqueueOrSpeakAsync(text, async (queuedText, queuedToken) => { await SpeakWithOutputRoutingAsync(queuedText, false, null); }, token);
        }, ct);
        if (ok) UnlockSteamAchievement(SteamAchievementService.FirstRadioChat);
        return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, code = "radio.slot_not_found" });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapPost("/api/radio/preview", async (HttpRequest request) =>
{
    bool show = true;
    int durationMs = 0;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("show", out var showProp))
                show = showProp.ValueKind == JsonValueKind.True || (showProp.ValueKind == JsonValueKind.String && bool.TryParse(showProp.GetString(), out var parsed) && parsed);
            if (body.RootElement.TryGetProperty("visible", out var visibleProp))
                show = visibleProp.ValueKind == JsonValueKind.True || (visibleProp.ValueKind == JsonValueKind.String && bool.TryParse(visibleProp.GetString(), out var parsed) && parsed);
            if (body.RootElement.TryGetProperty("durationMs", out var durationProp) && durationProp.ValueKind == JsonValueKind.Number)
                durationMs = Math.Clamp(durationProp.GetInt32(), 0, 30000);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    if (show && durationMs > 0) radioChatService.ShowPreviewFor(TimeSpan.FromMilliseconds(durationMs), () => pendingSpeechService.GetStatus());
    else if (show) radioChatService.ShowPreview(() => pendingSpeechService.GetStatus());
    else radioChatService.HidePreview();
    return Results.Ok(new { ok = true, show, durationMs });
});

app.MapPost("/api/radio/capture-shortcut", () =>
{
    try
    {
        var cfg = radioChatService.GetConfig();
        cfg.Enabled = true;
        radioChatService.ApplyConfig(cfg);
        if (TryInstallGnomeRadioShortcut(cfg.Shortcut.Display, radioChatLogger))
        {
            return Results.Ok(new { ok = true, backend = "gnome-custom-shortcut" });
        }
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-portal-hotkey.py");
        var triggerPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-trigger.sh");
        if (!File.Exists(scriptPath)) return Results.NotFound(new { ok = false, code = "radio.portal_hotkey_not_found" });
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("python3"), $"\"{scriptPath}\" configure \"{triggerPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplyCurrentUserSessionEnvironment(psi.Environment);
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        var process = System.Diagnostics.Process.Start(psi);
        if (process is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (!string.IsNullOrWhiteSpace(output)) radioChatLogger.LogInformation("[RadioChat] portal capture: {Output}", output.Trim());
                    if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) radioChatLogger.LogWarning("[RadioChat] portal capture exit={ExitCode}: {Error}", process.ExitCode, error.Trim());
                    else RestartUserServiceForRadio("mobmate-radio-hotkey.service", radioChatLogger);
                }
                catch (Exception logEx)
                {
                    radioChatLogger.LogDebug(logEx, "[RadioChat] portal capture log read failed");
                }
            });
        }
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        radioChatLogger.LogWarning(ex, "[RadioChat] portal shortcut capture failed");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

static void ApplyCurrentUserSessionEnvironment(System.Collections.Generic.IDictionary<string, string?> env)
{
    var systemdEnv = ReadSystemdUserEnvironmentForRadio();
    string? EnvValue(string name) => systemdEnv.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : Environment.GetEnvironmentVariable(name);

    var runtimeDir = EnvValue("XDG_RUNTIME_DIR") ?? $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";
    env["XDG_RUNTIME_DIR"] = runtimeDir;
    env["DBUS_SESSION_BUS_ADDRESS"] = EnvValue("DBUS_SESSION_BUS_ADDRESS") ?? $"unix:path={runtimeDir}/bus";
    foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY" })
    {
        var value = EnvValue(name);
        if (!string.IsNullOrWhiteSpace(value)) env[name] = value;
    }
}

static string? NormalizeKdeShortcutForRadio(string? display)
{
    var raw = (display ?? string.Empty).Trim().Replace('＋', '+').Replace(" ", string.Empty);
    if (string.IsNullOrWhiteSpace(raw) || raw.Equals("disabled", StringComparison.OrdinalIgnoreCase) || raw.Equals("無効", StringComparison.OrdinalIgnoreCase)) return null;
    var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) return null;
    var mods = new List<string>();
    string? key = null;
    foreach (var part in parts)
    {
        var lower = part.ToLowerInvariant();
        if (lower is "ctrl" or "control") { if (!mods.Contains("Ctrl")) mods.Add("Ctrl"); continue; }
        if (lower is "shift") { if (!mods.Contains("Shift")) mods.Add("Shift"); continue; }
        if (lower is "alt") { if (!mods.Contains("Alt")) mods.Add("Alt"); continue; }
        if (lower is "meta" or "super" or "win") { if (!mods.Contains("Meta")) mods.Add("Meta"); continue; }
        key = part.Length == 1 ? part.ToUpperInvariant() : part.ToUpperInvariant().StartsWith("F", StringComparison.Ordinal) ? part.ToUpperInvariant() : part;
    }
    if (string.IsNullOrWhiteSpace(key)) return null;
    mods.Add(key);
    return string.Join('+', mods);
}

static bool TryUpdateKdePortalRadioShortcut(string kdeShortcut, ILogger logger)
{
    try
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".config", "kglobalshortcutsrc");
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path);
        var inSection = false;
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) inSection = line.Equals("[token_mobmate_radio]", StringComparison.OrdinalIgnoreCase);
            if (inSection && line.StartsWith("toggle_radio_chat=", StringComparison.Ordinal))
            {
                lines[i] = $"toggle_radio_chat={kdeShortcut},Ctrl+Shift+F4,Toggle MobMate Radio Chat";
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            Array.Resize(ref lines, lines.Length + 3);
            lines[^3] = "[token_mobmate_radio]";
            lines[^2] = "_k_friendly_name=xdg-desktop-portal-kde";
            lines[^1] = $"toggle_radio_chat={kdeShortcut},Ctrl+Shift+F4,Toggle MobMate Radio Chat";
        }
        File.WriteAllLines(path, lines, Encoding.UTF8);
        logger.LogInformation("[RadioChat] updated KDE portal shortcut: {Shortcut}", kdeShortcut);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[RadioChat] failed to update KDE portal shortcut");
        return false;
    }
}

static bool IsGnomeSession()
{
    var env = ReadSystemdUserEnvironmentForRadio();
    var desktop = env.TryGetValue("XDG_CURRENT_DESKTOP", out var v) ? v : Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
    return (desktop ?? string.Empty).Contains("GNOME", StringComparison.OrdinalIgnoreCase);
}

static string ToGnomeBinding(string display)
{
    var normalized = NormalizeKdeShortcutForRadio(display) ?? display;
    var parts = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var sb = new StringBuilder();
    foreach (var part in parts)
    {
        if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) { sb.Append("<Control>"); continue; }
        if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { sb.Append("<Shift>"); continue; }
        if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { sb.Append("<Alt>"); continue; }
        if (part.Equals("Meta", StringComparison.OrdinalIgnoreCase) || part.Equals("Super", StringComparison.OrdinalIgnoreCase) || part.Equals("Win", StringComparison.OrdinalIgnoreCase)) { sb.Append("<Super>"); continue; }
        sb.Append(part);
    }
    return sb.Length == 0 ? "<Control><Shift>F4" : sb.ToString();
}

static bool TryInstallGnomeRadioShortcut(string? display, ILogger logger)
{
    if (!IsGnomeSession()) return false;
    try
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-shortcut-helper.sh");
        if (!File.Exists(helperPath)) return false;
        var binding = ToGnomeBinding(display ?? "Ctrl+Shift+F4");
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("bash"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(helperPath);
        psi.ArgumentList.Add("install-gnome");
        psi.ArgumentList.Add(binding);
        ApplyCurrentUserSessionEnvironment(psi.Environment);
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return false;
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("[RadioChat] GNOME shortcut install timed out");
            return false;
        }
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode == 0)
        {
            logger.LogInformation("[RadioChat] GNOME shortcut installed: {Output}", output);
            return true;
        }
        logger.LogWarning("[RadioChat] GNOME shortcut install exit={ExitCode}: {Error}", process.ExitCode, error);
        return false;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[RadioChat] failed to install GNOME shortcut");
        return false;
    }
}

static Dictionary<string, string> ReadSystemdUserEnvironmentForRadio()
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("systemctl"), "--user show-environment")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return result;
        if (!process.WaitForExit(1000))
        {
            try { process.Kill(); } catch { }
            return result;
        }
        var output = process.StandardOutput.ReadToEnd();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            result[line[..index]] = line[(index + 1)..].TrimEnd('\r');
        }
    }
    catch
    {
        // Best-effort only; parent process environment remains the fallback.
    }
    return result;
}

static void RestartUserServiceForRadio(string serviceName, ILogger logger)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("systemctl"), $"--user restart {serviceName}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return;
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(); } catch { }
            logger.LogWarning("[RadioChat] timed out restarting {ServiceName}", serviceName);
            return;
        }
        if (process.ExitCode == 0) logger.LogInformation("[RadioChat] restarted {ServiceName}", serviceName);
        else logger.LogWarning("[RadioChat] restart {ServiceName} exit={ExitCode}: {Error}", serviceName, process.ExitCode, process.StandardError.ReadToEnd().Trim());
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[RadioChat] failed to restart {ServiceName}", serviceName);
    }
}

static bool IsUserServiceActive(string serviceName)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("systemctl"), $"--user is-active {serviceName}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return false;
        if (!process.WaitForExit(1000))
        {
            try { process.Kill(); } catch { }
            return false;
        }
        return process.StandardOutput.ReadToEnd().Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

static bool IsGnomeRadioShortcutRegistered()
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("gsettings"), "get org.gnome.settings-daemon.plugins.media-keys custom-keybindings")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyCurrentUserSessionEnvironment(psi.Environment);
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return false;
        if (!process.WaitForExit(1000))
        {
            try { process.Kill(); } catch { }
            return false;
        }
        return process.StandardOutput.ReadToEnd().Contains("/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/mobmate-radio-chat/", StringComparison.Ordinal);
    }
    catch { return false; }
}

static bool EnsureGnomeRadioShortcutCurrent(string? display, ILogger logger)
{
    var expected = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-trigger.sh") + " toggle";
    var baseSchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/mobmate-radio-chat/";
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("gsettings"), $"get {baseSchema} command")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyCurrentUserSessionEnvironment(psi.Environment);
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is not null)
        {
            if (!process.WaitForExit(1000)) { try { process.Kill(); } catch { } }
            else if (process.ExitCode == 0)
            {
                var current = process.StandardOutput.ReadToEnd().Trim().Trim('\'');
                if (current == expected) return true;
            }
        }
    }
    catch { }

    // Steam install path can change after depot/tarball swaps; repair stale /tmp or dev paths.
    return ForceInstallGnomeRadioShortcut(display, logger) && IsGnomeRadioShortcutRegistered();
}

static bool ForceInstallGnomeRadioShortcut(string? display, ILogger logger)
{
    try
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-shortcut-helper.sh");
        if (!File.Exists(helperPath)) return false;
        var binding = ToGnomeBinding(display ?? "Ctrl+Shift+F4");
        if (Directory.Exists("/run/host"))
        {
            var env = new Dictionary<string, string?>();
            ApplyCurrentUserSessionEnvironment(env);
            RadioChatService.SanitizeSteamRuntimeEnvironment(env);
            var queued = RadioChatService.TryStartHostUserCommand("mobmate-radio-shortcut-repair", "/bin/sh", new[] { helperPath, "install-gnome", binding }, env, logger);
            if (queued) Thread.Sleep(1000);
            return queued;
        }
        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("bash"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(helperPath);
        psi.ArgumentList.Add("install-gnome");
        psi.ArgumentList.Add(binding);
        ApplyCurrentUserSessionEnvironment(psi.Environment);
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return false;
        if (!process.WaitForExit(5000)) { try { process.Kill(entireProcessTree: true); } catch { } return false; }
        var error = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode == 0) return true;
        logger.LogWarning("[RadioChat] GNOME shortcut repair exit={ExitCode}: {Error}", process.ExitCode, error);
    }
    catch (Exception ex) { logger.LogWarning(ex, "[RadioChat] GNOME shortcut repair failed"); }
    return false;
}

static void StartDetached(string fileName, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };
    ApplyCurrentUserSessionEnvironment(psi.Environment);
    System.Diagnostics.Process.Start(psi)?.Dispose();
}
// ── Translation API ──────────────────────────────────────────
app.MapGet("/api/translation/languages", () =>
{
    var langs = new List<object> { new { code = "OFF", label = "Off (OFF)" } };
    foreach (var code in LanguageOptionsTranslation.TranslationLangs)
        langs.Add(new { code, label = LanguageOptionsTranslation.DisplayTranslationTarget(code) });
    return Results.Ok(langs);
});

app.MapGet("/api/translation/config", () =>
{
    var cfg = LoadTranslationConfig();
    return Results.Ok(new
    {
        talkTarget     = cfg.TalkTarget,
        modelAvailable = translationService.IsLoaded,
    });
});

app.MapPost("/api/translation/config", async (HttpRequest request) =>
{
    string? talkTarget = null;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("talkTarget", out var v))
                talkTarget = v.GetString();
        }
        catch { return Results.BadRequest(ApiMessage("bad_request", "Invalid JSON")); }
    }
    if (talkTarget == null)
        return Results.BadRequest(ApiMessage("bad_request", "talkTarget is required"));

    var normalized = LanguageOptionsTranslation.NormalizeTranslationTarget(talkTarget);
    if (talkTarget != "OFF" && !LanguageOptionsTranslation.IsTranslationLangSupported(normalized))
        return Results.BadRequest(ApiMessage("bad_request", $"Unsupported translation target: {talkTarget}"));

    var cfg = LoadTranslationConfig();
    cfg.TalkTarget = normalized;
    ttsRouter.InvalidateOnsetConfiguration();
    SaveTranslationConfig(cfg);
    ttsRouter.InvalidateOnsetConfiguration();
    translationLogger.LogInformation("[Translation] config saved: talkTarget={Target}", normalized);
    return Results.Ok(new { talkTarget = normalized });
});
// ── VRChat OSC API ───────────────────────────────────────────
app.MapGet("/api/vrchat-osc/config", () => Results.Ok(vrchatOscService.GetConfig()));

app.MapPost("/api/vrchat-osc/config", async (HttpRequest req) =>
{
    try
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<VrchatOscConfig>(req.Body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body == null) return Results.BadRequest("Invalid config body");
        vrchatOscService.SetConfig(body);
        var saved = vrchatOscService.GetConfig();
        SaveVrchatOscConfig(saved);
        return Results.Ok(saved);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/vrchat-osc/test", async (HttpRequest req) =>
{
    VrchatOscTestRequest? body = null;
    if (req.ContentLength.GetValueOrDefault() > 0)
    {
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync<VrchatOscTestRequest>(req.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { }
    }
    var cfg = vrchatOscService.GetConfig();
    var text = string.IsNullOrWhiteSpace(body?.Text)
        ? $"MobMate OSC test {DateTime.Now:HH:mm:ss}"
        : body!.Text!.Trim();
    if (text.Length > 144) text = text.Substring(0, 144);
    var ok = vrchatOscService.SendTestText(text);
    return Results.Ok(new { ok, cfg.Host, cfg.Port, text });
});

app.MapGet("/api/hearing/config", () => Results.Ok(hearingService!.GetConfig()));

app.MapPost("/api/hearing/config", async (HttpRequest req) =>
{
    try
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<LinuxHearingConfig>(req.Body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body == null) return Results.BadRequest(new { ok = false, error = "Invalid config body" });
        hearingService!.SetConfig(body);
        return Results.Ok(hearingService.GetConfig());
    }
    catch (Exception ex)
    {
        hearingLogger.LogWarning(ex, "[Hearing] config save failed");
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/hearing/status", () =>
{
    return Results.Ok(hearingService!.GetStatus());
});

app.MapPost("/api/hearing/start", () =>
{
    try
    {
        hearingService!.Start();
        UnlockSteamAchievement(SteamAchievementService.FirstHearingStart);
        return Results.Ok(hearingService.GetStatus());
    }
    catch (Exception ex)
    {
        hearingLogger.LogWarning(ex, "[Hearing] start failed");
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/hearing/stop", () =>
{
    hearingService!.Stop();
    return Results.Ok(hearingService.GetStatus());
});

app.MapGet("/api/hearing/history", (int? max) =>
{
    return Results.Ok(hearingService!.GetHistory(max ?? 20));
});

app.MapGet("/api/hearing/languages", () =>
{
    var asrLangs = new[]
    {
        new { code = "auto", label = "Auto" },
        new { code = "ja", label = "Japanese" },
        new { code = "en", label = "English" },
        new { code = "ko", label = "Korean" },
        new { code = "zh", label = "Chinese" },
        new { code = "yue", label = "Cantonese" },
    };
    var translationLangs = LanguageOptionsTranslation.TranslationLangs
        .Select(code => new { code, label = LanguageOptionsTranslation.DisplayTranslationTarget(code) });
    return Results.Ok(new { asrLangs, translationLangs });
});

app.MapGet("/api/hearing/input-devices", async () =>
{
    try
    {
        var descriptions = await GetPipeWireDescriptions("list sources");
        var psi = NewPactlStartInfo("list short sources", redirectStandardOutput: true, redirectStandardError: true);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return Results.Ok(new { ok = false, error = "pactl not found", devices = Array.Empty<object>() });
        var output = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return Results.Ok(new { ok = false, error = stderr.Trim(), devices = Array.Empty<object>() });

        var devices = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
            .Select(parts =>
            {
                var id = parts[1].Trim();
                var isMonitor = id.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase);
                var name = descriptions.TryGetValue(id, out var desc) && !string.IsNullOrWhiteSpace(desc)
                    ? desc
                    : id;
                if (isMonitor && !name.Contains("monitor", StringComparison.OrdinalIgnoreCase)) name += " (monitor)";
                return new { id, name, kind = isMonitor ? "loopback" : "mic", isDefault = false };
            })
            .OrderByDescending(d => d.kind == "loopback")
            .ThenBy(d => d.name, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
        return Results.Ok(new { ok = true, devices });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message, devices = Array.Empty<object>() });
    }
});

app.MapPost("/api/hearing/subtitle-open", () =>
{
    var url = new Uri(new Uri(selectedBaseUrl ?? throw new InvalidOperationException("WebHost URL is not selected.")), "subtitle.html?window=1").AbsoluteUri;
    TryOpenSubtitleWindow(url);
    return Results.Ok(new { ok = true, url });
});
app.MapPost("/api/hearing/subtitle-close", () =>
{
    try
    {
        if (_subtitleProcess is { HasExited: false })
        {
            _subtitleProcess.Kill();
            _subtitleProcess = null;
        }
        // When launched on the host (Steam pressure-vessel), the window runs as a transient
        // user unit rather than a child process, so also stop it via the systemd user manager.
        RadioChatService.TryStopHostUserUnit("mobmate-subtitle-window", logger);
    }
    catch { }
    return Results.Ok(ApiOk());
});

// ---- Audio devices ----
// PipeWire uses "node.name = \"...\"" and "device.description = \"...\"" inside block properties.
// This helper parses `pactl list sinks|sources` and returns rawName → friendlyDescription.
async Task<Dictionary<string, string>> GetPipeWireDescriptions(string pactlArg)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    try
    {
        var psi2 = NewPactlStartInfo(pactlArg, redirectStandardOutput: true);
        using var proc2 = System.Diagnostics.Process.Start(psi2);
        if (proc2 == null) return map;
        var text = await proc2.StandardOutput.ReadToEndAsync();
        await proc2.WaitForExitAsync();
        string? curName = null; string? curDesc = null;
        static string? ExtractQuoted(string line, string key)
        {
            var prefix = key + " = \"";
            var t = line.TrimStart();
            if (!t.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var rest = t.Substring(prefix.Length);
            var end = rest.IndexOf('"');
            return end >= 0 ? rest.Substring(0, end) : rest;
        }
        foreach (var rawLine in text.Split('\n'))
        {
            // Block header: non-empty line with no leading whitespace (locale-agnostic; works for 日本語 "シンク #N" etc.)
            if (rawLine.Length > 0 && rawLine[0] != '\t' && rawLine[0] != ' ')
            {
                if (curName != null && curDesc != null) map[curName] = curDesc;
                curName = null; curDesc = null; continue;
            }
            curName  ??= ExtractQuoted(rawLine, "node.name");
            curDesc  ??= ExtractQuoted(rawLine, "device.description");
        }
        if (curName != null && curDesc != null) map[curName] = curDesc;
    }
    catch { /* fall back to raw names */ }
    return map;
}
app.MapGet("/api/audio/input-devices", () =>
{
    // Use native bridge for device IDs: miniaudio identifies PulseAudio sources by description name
    // (e.g. "Ryzen HD Audio Controller アナログステレオ"), not by PipeWire node name.
    // Filter out "Monitor of" entries — they capture TTS output, not real mic input.
    logger.LogInformation("[AudioInput] action=list begin nativeExe={NativeExe}", nativeExe);
    var json = NativeLibraryBridge.GetAudioInputDevicesJson(nativeExe);
    logger.LogInformation("[AudioInput] action=list returned json={DeviceResult}", json);
    try
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        var devArr = root?["devices"]?.AsArray();
        if (devArr == null) return Results.Content(json, "application/json");
        var filtered = new List<object>();
        foreach (var d in devArr)
        {
            var id   = d?["id"]?.GetValue<string>()   ?? "";
            var name = d?["name"]?.GetValue<string>() ?? id;
            var isDefault = d?["isDefault"]?.GetValue<bool>() ?? false;
            if (!id.StartsWith("Monitor of ", StringComparison.OrdinalIgnoreCase))
                filtered.Add(new { id, name, isDefault });
        }
        return Results.Ok(new { ok = true, devices = filtered.ToArray() });
    }
    catch
    {
        return Results.Content(json, "application/json");
    }
});

// ---- TTS ----
app.MapGet("/api/tts/devices", async () =>
{
    try
    {
        // Collect friendly sink descriptions via PipeWire-aware pactl parser
        var descriptions = await GetPipeWireDescriptions("list sinks");

        var psi = NewPactlStartInfo("list short sinks", redirectStandardOutput: true, redirectStandardError: true);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return Results.Ok(new { ok = false, error = "pactl not found", devices = Array.Empty<object>() });
        var output = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return Results.Ok(new { ok = false, error = stderr.Trim(), devices = Array.Empty<object>() });

        var devices = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
            .Where(parts => !IsInternalMobMateSink(parts[1].Trim()))
            .Select(parts =>
            {
                var name = parts[1].Trim();
                // MobMate virtual mic sink: show a clear label (fix: was wrongly prefixed "Monitor of")
                string friendly;
                if (string.Equals(name, LinuxVirtualMicSinkName, StringComparison.Ordinal))
                    friendly = "MobMate TTS Virtual Mic";
                else if (descriptions.TryGetValue(name, out var desc) && !string.IsNullOrWhiteSpace(desc))
                    friendly = desc;
                else
                    friendly = name;
                // friendlyName left empty so app.js shows only the friendly label, not raw（raw）
                return new { id = name, name = friendly, friendlyName = "", isDefault = false, wirelessLikely = false };
            })
            .Cast<object>()
            .ToArray();
        return Results.Ok(new { ok = true, devices });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message, devices = Array.Empty<object>() });
    }
});

app.MapGet("/api/tts/status", () =>
{
    var status = ttsRouter.GetStatus();
    var voicevox = ProbeVoicevox();
    var monitorEnabled = linuxTtsConfig.MonitorVolumePercent > 0;
    var piperReady = status.PiperPlusRuntimeAvailable && status.PiperPlusModelInstalled;
    var kokoroReady = status.KokoroModelInstalled;
    var mossReady = status.MossModelInstalled;
    return Results.Ok(new
    {
        available = status.Available || piperReady || voicevox.available || kokoroReady || mossReady,
        isSpeaking = status.IsSpeaking,
        error = piperReady ? null : status.Error,
        outputDeviceId = linuxTtsConfig.OutputDeviceId,
        outputDeviceName = linuxTtsConfig.OutputDeviceName,
        outputDeviceMode = string.IsNullOrWhiteSpace(linuxTtsConfig.OutputDeviceId) ? "system_default" : "specific",
        wirelessLikely = false,
        preSilenceMs = linuxTtsConfig.PreSilenceMs,
        fallbackReason = status.FallbackReason,
        pendingCount = status.PendingCount,
        droppedCount = status.DroppedCount,
        lastTtsText = status.LastTtsText,
        lastTtsStartedAt = status.LastTtsStartedAt,
        monitorEnabled,
        monitorVolumePercent = linuxTtsConfig.MonitorVolumePercent,
        wavOutputEnabled = linuxTtsConfig.WavOutputEnabled,
        monitorFallbackReason = linuxMonitorFallbackReason,
        engine = linuxTtsConfig.Engine,
        effectiveEngine = status.EffectiveEngine,
        engineAvailable = status.EngineAvailable
            || piperReady
            || (string.Equals(linuxTtsConfig.Engine, "voicevox", StringComparison.OrdinalIgnoreCase) && voicevox.available)
            || (string.Equals(linuxTtsConfig.Engine, "kokoro", StringComparison.OrdinalIgnoreCase) && kokoroReady)
            || (string.Equals(linuxTtsConfig.Engine, "moss", StringComparison.OrdinalIgnoreCase) && mossReady),
        engineError = status.EngineError,
        voicevoxApi = LinuxVoicevoxApi,
        voicevoxSpeakerId = linuxTtsConfig.VoicevoxSpeakerId,
        voicevoxAutoEmotion = linuxTtsConfig.VoicevoxAutoEmotion,
        ttsReflectEmotion = linuxTtsConfig.TtsReflectEmotion,
        ttsReflectContourStrength = linuxTtsConfig.TtsReflectContourStrength,
        ttsReflectToneEmphasis = linuxTtsConfig.TtsReflectToneEmphasis,
        piperPlusModelId = linuxTtsConfig.PiperPlusModelId,
        piperPlusModelInstalled = status.PiperPlusModelInstalled,
        piperPlusRuntimeAvailable = status.PiperPlusRuntimeAvailable,
        voicevoxAvailable = voicevox.available,
        voicevoxError = voicevox.error,
        piperPlusError = status.PiperPlusError,
        piperPlusModelPath = linuxTtsConfig.PiperPlusModelPath,
        piperAvailable = status.PiperPlusRuntimeAvailable,
        piperModel = linuxTtsConfig.PiperPlusModelPath,
        piperModelInstalled = !string.IsNullOrEmpty(linuxTtsConfig.PiperPlusModelPath) && File.Exists(linuxTtsConfig.PiperPlusModelPath),
        kokoroModelId = linuxTtsConfig.KokoroModelId,
        kokoroSpeakerId = linuxTtsConfig.KokoroSpeakerId,
        kokoroSpeed = linuxTtsConfig.KokoroSpeed,
        kokoroModelInstalled = status.KokoroModelInstalled,
        kokoroError = status.KokoroError,
        mossModelId = linuxTtsConfig.MossModelId,
        mossVoiceId = linuxTtsConfig.MossVoiceId,
        mossMaxNewFrames = linuxTtsConfig.MossMaxNewFrames,
        mossModelInstalled = status.MossModelInstalled,
        mossError = status.MossError
    });
});

app.MapPost("/api/tts/speak", async (HttpRequest req) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(ApiError("text is required"));
        if (doc.RootElement.TryGetProperty("engine", out var speakEngine) && speakEngine.GetString() == "kokoro" &&
            (!kokoroService.IsModelInstalled || linuxTtsConfig.Engine != "kokoro" ||
             (doc.RootElement.TryGetProperty("kokoroModelId", out var speakModel) &&
              speakModel.GetString() != linuxTtsConfig.KokoroModelId)))
            return Results.Ok(new { ok = false, success = false, error = "Kokoro model is not ready or selected." });
        var interrupt = !doc.RootElement.TryGetProperty("interrupt", out var i) || i.GetBoolean();
        try
        {
            var added = adaptiveVocabStore.AddText(text, "auto", AdaptiveVocabSources.Tts);
            if (added > 0)
                adaptiveVocabLogger.LogInformation("[AdaptiveVocab] learned {Count} TTS entries", added);
        }
        catch (Exception ex)
        {
            adaptiveVocabLogger.LogWarning(ex, "[AdaptiveVocab] TTS learning failed");
        }
        var result = await SpeakWithOutputRoutingAsync(text, interrupt);
        if (result.Success)
        {
            UnlockSteamAchievement(SteamAchievementService.FirstTtsSent);
            if (string.Equals(ttsRouter.GetStatus().EffectiveEngine, "voicevox", StringComparison.OrdinalIgnoreCase))
                UnlockSteamAchievement(SteamAchievementService.FirstVoicevoxTts);
        }
        return result.Success
            ? Results.Ok(new { ok = true, success = true })
            : Results.Ok(new { ok = false, success = false, error = result.Error });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiError(ex.Message, "request.invalid_json"));
    }
});

app.MapPost("/api/tts/stop", () =>
{
    ttsRouter.StopAsync();
    return Results.Ok(ApiOk());
});

app.MapPost("/api/tts/config", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var kokoroSelectionRequested = doc.RootElement.TryGetProperty("engine", out var requestedEngine) &&
        requestedEngine.ValueKind == JsonValueKind.String &&
        string.Equals(requestedEngine.GetString(), "kokoro", StringComparison.OrdinalIgnoreCase);
    if (doc.RootElement.TryGetProperty("kokoroModelId", out var requestedKokoro) &&
        requestedKokoro.ValueKind == JsonValueKind.String && requestedKokoro.GetString() is string requestedId &&
        !string.IsNullOrWhiteSpace(requestedId))
    {
        kokoroSelectionRequested = true;
        if (!MobMate.Core.Tts.KokoroTtsService.IsModelDirValid(Path.Combine(kokoroModelRoot, requestedId)))
            return Results.BadRequest(ApiError("Kokoro model is incomplete or unreadable. Remove it and download it again.", "tts.kokoro_unavailable"));
    }
    // Stage all fields; rejected requests must not mutate the live config by reference.
    var cfg = JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(linuxTtsConfig))!;
    if (string.IsNullOrEmpty(cfg.PiperPlusExePath)) cfg.PiperPlusExePath = piperExe;
    if (string.IsNullOrEmpty(cfg.PiperPlusModelPath)) cfg.PiperPlusModelPath = piperModel;
    var configChanged = false;
    if (doc.RootElement.TryGetProperty("engine", out var ep))
    {
        cfg.Engine = ep.GetString() ?? cfg.Engine;
        cfg.PiperPlusExePath = ResolvePiperExeForEngine(cfg.Engine);
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("outputDeviceId", out var outputDeviceProp))
    {
        var outputDeviceId = NormalizeTtsOutputSink(outputDeviceProp.GetString() ?? string.Empty);
        cfg.OutputDeviceId = outputDeviceId;
        cfg.OutputDeviceName = outputDeviceId;
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("preSilenceMs", out var preSilProp))
    {
        cfg.PreSilenceMs = preSilProp.GetInt32();
        if (cfg.PreSilenceMs < 0 || cfg.PreSilenceMs > 3000)
            return Results.BadRequest(ApiError("preSilenceMs must be between 0 and 3000", "tts.invalid_pre_silence_ms"));
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("voicevoxSpeakerId", out var vvSpeakerProp))
    {
        cfg.VoicevoxSpeakerId = vvSpeakerProp.ValueKind == JsonValueKind.Number
            ? vvSpeakerProp.GetInt32().ToString()
            : vvSpeakerProp.GetString() ?? cfg.VoicevoxSpeakerId;
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("voicevoxAutoEmotion", out var vvAutoProp))
    {
        cfg.VoicevoxAutoEmotion = vvAutoProp.GetBoolean();
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("monitorVolumePercent", out var monVolProp))
    {
        cfg.MonitorVolumePercent = monVolProp.GetInt32();
        if (cfg.MonitorVolumePercent < 0 || cfg.MonitorVolumePercent > 100 || cfg.MonitorVolumePercent % 10 != 0)
            return Results.BadRequest(ApiError("monitorVolumePercent must be 0, 10, 20, ..., 100", "tts.invalid_monitor_volume_percent"));
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("wavOutputEnabled", out var wavOutputProp))
    {
        cfg.WavOutputEnabled = wavOutputProp.GetBoolean();
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("piperPlusExePath", out var pp))
    {
        var ppPath = pp.GetString();
        if (!string.IsNullOrEmpty(ppPath))
        {
            cfg.PiperPlusExePath = ppPath;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("piperPlusModelPath", out var pm))
    {
        var pmPath = pm.GetString();
        if (!string.IsNullOrEmpty(pmPath))
        {
            cfg.PiperPlusModelPath = pmPath;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("piperPlusPersistent", out var ppPersistent))
    {
        cfg.PiperPlusPersistent = ppPersistent.GetBoolean();
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("piperPlusModelId", out var pmId))
    {
        var modelId = pmId.GetString();
        var modelPath = ResolvePiperPlusModelPathById(modelId);
        if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(modelPath))
        {
            var runtime = ResolvePiperRuntimeById(modelId);
            if (!string.IsNullOrWhiteSpace(runtime))
            {
                cfg.Engine = runtime;
                cfg.PiperPlusExePath = ResolvePiperExeForEngine(runtime);
            }
            cfg.PiperPlusModelId = modelId;
            cfg.PiperPlusModelPath = modelPath;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("kokoroModelId", out var kmIdProp))
    {
        var modelId = kmIdProp.GetString();
        if (modelId != null && modelId.Length <= 120)
        {
            cfg.KokoroModelId = modelId;
            cfg.KokoroModelDir = ResolveKokoroModelDir(kokoroModelRoot, modelId) ?? string.Empty;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("kokoroSpeakerId", out var kmSidProp) && kmSidProp.TryGetInt32(out var kmSid))
    {
        cfg.KokoroSpeakerId = Math.Clamp(kmSid, 0, 999);
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("kokoroSpeed", out var kmSpProp) && kmSpProp.TryGetSingle(out var kmSp))
    {
        cfg.KokoroSpeed = Math.Clamp(kmSp, 0.5f, 2.0f);
        configChanged = true;
    }
    if (doc.RootElement.TryGetProperty("mossModelId", out var mossIdProp))
    {
        var modelId = mossIdProp.GetString();
        if (modelId != null && modelId.Length <= 120)
        {
            cfg.MossModelId = modelId;
            cfg.MossModelDir = ResolveMossModelDir(mossModelRoot, modelId) ?? string.Empty;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("mossVoiceId", out var mossVoiceProp))
    {
        var voiceId = mossVoiceProp.GetString();
        if (!string.IsNullOrWhiteSpace(voiceId) && voiceId.Length <= 80)
        {
            cfg.MossVoiceId = voiceId;
            configChanged = true;
        }
    }
    if (doc.RootElement.TryGetProperty("mossMaxNewFrames", out var mossFramesProp) && mossFramesProp.TryGetInt32(out var mossFrames))
    {
        cfg.MossMaxNewFrames = Math.Clamp(mossFrames, 20, 400);
        configChanged = true;
    }
    if (configChanged)
    {
        if (!string.IsNullOrWhiteSpace(cfg.KokoroModelId))
            cfg.KokoroModelDir = ResolveKokoroModelDir(kokoroModelRoot, cfg.KokoroModelId) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cfg.MossModelId))
            cfg.MossModelDir = ResolveMossModelDir(mossModelRoot, cfg.MossModelId) ?? string.Empty;
        if (kokoroSelectionRequested)
        {
            if (!MobMate.Core.Tts.KokoroTtsService.IsModelDirValid(cfg.KokoroModelDir))
                return Results.BadRequest(ApiError("Kokoro model is incomplete or unreadable. Remove it and download it again.", "tts.kokoro_unavailable"));
            // Initialize before committing other services or output-device side effects.
            kokoroService.SetConfig(cfg.KokoroModelDir, cfg.KokoroSpeakerId, cfg.KokoroSpeed);
            if (!kokoroService.IsModelInstalled)
            {
                var error = kokoroService.LastError ?? "Kokoro model is not ready.";
                kokoroService.SetConfig(linuxTtsConfig.KokoroModelDir, linuxTtsConfig.KokoroSpeakerId, linuxTtsConfig.KokoroSpeed);
                return Results.BadRequest(ApiError(error, "tts.kokoro_unavailable"));
            }
        }
        mobfyOnset.Invalidate();
        using var onsetRouteChange = ttsRouter.BeginOnsetRouteChange(cfg);
        ttsRouter.SetConfig(cfg);
        linuxTtsConfig = cfg;
        if (doc.RootElement.TryGetProperty("outputDeviceId", out _))
        {
            ttsOutputSinkName = cfg.OutputDeviceId;
            Environment.SetEnvironmentVariable("PULSE_SINK", string.IsNullOrWhiteSpace(ttsOutputSinkName) ? null : ttsOutputSinkName);
            NativeLibraryBridge.SetTtsOutputDevice(nativeExe, ttsOutputSinkName);
        }
        WarmUpMossIfSelected(linuxTtsConfig);
        await EnsureLinuxMonitorPlaybackAsync(ttsOutputSinkName, linuxTtsConfig.MonitorVolumePercent);
    }
    return Results.Ok(new
    {
        ok = true,
        engine = ttsRouter.GetStatus().EffectiveEngine,
        monitorEnabled = linuxTtsConfig.MonitorVolumePercent > 0,
        monitorVolumePercent = linuxTtsConfig.MonitorVolumePercent,
        wavOutputEnabled = linuxTtsConfig.WavOutputEnabled,
        monitorFallbackReason = linuxMonitorFallbackReason
    });
});

// ---- Virtual mic setup (PipeWire/PulseAudio) ----
app.MapGet("/api/setup/virtual-mic/status", async () =>
{
    // Query pactl for virtual sinks
    try
    {
        var psi = NewPactlStartInfo("list short sinks", redirectStandardOutput: true);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return Results.Ok(new { ok = false, error = "pactl not found", sinks = Array.Empty<string>() });
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var sinks = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t').ElementAtOrDefault(1) ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray();
        var virtualSinks = sinks.Where(s => s.Contains("MobMate")).ToArray();
        return Results.Ok(new { ok = true, sinks, virtualSinks });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message, sinks = Array.Empty<string>() });
    }
});

app.MapPost("/api/setup/virtual-mic/create", async (HttpRequest req) =>
{
    ttsRouter.InvalidateOnsetConfiguration();
    using var doc = req.ContentLength == 0
        ? JsonDocument.Parse("{}")
        : await JsonDocument.ParseAsync(req.Body);
    var sinkName = doc.RootElement.TryGetProperty("sinkName", out var sn)
        ? sn.GetString() ?? LinuxVirtualMicSinkName
        : LinuxVirtualMicSinkName;
    var desc = doc.RootElement.TryGetProperty("description", out var d)
        ? d.GetString() ?? LinuxVirtualMicDescription
        : LinuxVirtualMicDescription;
    var sourceName = doc.RootElement.TryGetProperty("sourceName", out var sourceProp)
        ? sourceProp.GetString() ?? (string.Equals(sinkName, LinuxVirtualMicSinkName, StringComparison.Ordinal) ? LinuxVirtualMicSourceName : sinkName + "Source")
        : (string.Equals(sinkName, LinuxVirtualMicSinkName, StringComparison.Ordinal) ? LinuxVirtualMicSourceName : sinkName + "Source");
    try
    {
        // Check if sink already exists
        var checkPsi = NewPactlStartInfo("list short sinks", redirectStandardOutput: true);
        using var checkProc = System.Diagnostics.Process.Start(checkPsi)!;
        var existing = await checkProc.StandardOutput.ReadToEndAsync();
        await checkProc.WaitForExitAsync();
        if (existing.Contains(sinkName))
        {
            NativeLibraryBridge.SetTtsOutputDevice(nativeExe, sinkName);
            var sourceOk = await EnsureVirtualMicSourceAsync(sinkName, sourceName, desc);
            logger.LogInformation("Virtual mic already exists: {Sink}", sinkName);
            return Results.Ok(new { ok = true, sinkName, monitorSource = sinkName + ".monitor", virtualSource = sourceName, virtualSourceCreated = sourceOk, alreadyExisted = true });
        }

        var args = $"load-module module-null-sink sink_name={sinkName} sink_properties=device.description=\"{desc}\"";
        var psi = NewPactlStartInfo(args, redirectStandardOutput: true, redirectStandardError: true);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode == 0)
        {
            NativeLibraryBridge.SetTtsOutputDevice(nativeExe, sinkName);
            var sourceOk = await EnsureVirtualMicSourceAsync(sinkName, sourceName, desc);
            logger.LogInformation("Virtual mic created: {Sink}", sinkName);
            return Results.Ok(new { ok = true, sinkName, monitorSource = sinkName + ".monitor", virtualSource = sourceName, virtualSourceCreated = sourceOk, alreadyExisted = false });
        }
        return Results.Ok(new { ok = false, error = stderr.Trim() });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/setup/virtual-mic/delete", async (HttpRequest req) =>
{
    ttsRouter.InvalidateOnsetConfiguration();
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var sinkName = doc.RootElement.TryGetProperty("sinkName", out var sn)
        ? sn.GetString() ?? "MobMateVirtualMic"
        : "MobMateVirtualMic";
    try
    {
        // Find module index for this sink
        var listPsi = NewPactlStartInfo("list short modules", redirectStandardOutput: true);
        using var listProc = System.Diagnostics.Process.Start(listPsi)!;
        var modules = await listProc.StandardOutput.ReadToEndAsync();
        await listProc.WaitForExitAsync();
        var moduleId = modules.Split('\n')
            .FirstOrDefault(l => l.Contains("module-null-sink") && l.Contains(sinkName))
            ?.Split('\t').FirstOrDefault();
        if (string.IsNullOrEmpty(moduleId))
            return Results.Ok(new { ok = false, error = "sink not found" });

        var psi = NewPactlStartInfo($"unload-module {moduleId}");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        logger.LogInformation("Virtual mic deleted: {Sink}", sinkName);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

// ---- Realtime session (ASR engines require model files on Bazzite) ----
RealtimeSessionController? realtimeSession = null;
var realtimeSessionService = new RealtimeSessionService(ttsRouter.StopOnset);
IDisposable? realtimeWorkLease = null;
var realtimeMutationGate = new SemaphoreSlim(1, 1);
string lastRealtimeTtsFinal = string.Empty;
string lastRealtimeEngine = string.Empty;
string lastRealtimeLanguage = string.Empty;
bool realtimeTtsEnabled = true;
bool onsetExplicitInput = false;
int onsetInputChanged = 0;
bool onsetIdleKnown = true;
long onsetControlGeneration = 0;
string onsetCaptureId = "";
string? OnsetBlockReason()
{
    if (!realtimeTtsEnabled) return "tts-disabled";
    if (Volatile.Read(ref onsetInputChanged) != 0) return "input-changed";
    if (ttsRouter.OnsetRouteChanging) return "route-changing";
    if (!onsetExplicitInput) return "input-required";
    if (!string.Equals(ttsOutputSinkName, LinuxVirtualMicSinkName, StringComparison.Ordinal)) return "output-required";
    if (pendingSpeechService.GetConfig().Mode != "instant" || pendingSpeechService.GetStatus().PendingCount != 0) return "approval-pending";
    if (LoadTranslationConfig().TalkTarget != "OFF") return "translation-on";
    return null;
}
bool OnsetPermitted() => OnsetBlockReason() == null;
object OnsetStatus()
{
    if (MobfyOnsetSelection.IsSelected(uiSettingsDocument)) return mobfyOnset.GetStatus();
    var blocked = realtimeSession == null ? "start-required" : OnsetBlockReason();
    return ttsRouter.GetOnsetStatus(blocked);
}

void BindOnset(bool enabled, bool newSession = false, long? expectedGeneration = null)
{
    var generation = expectedGeneration ?? Volatile.Read(ref onsetControlGeneration);
    enabled = enabled && generation == Volatile.Read(ref onsetControlGeneration);
    // Same already-selected route as the body wrapper, never a fallback sink.
    if (enabled && !string.IsNullOrWhiteSpace(ttsOutputSinkName)) Environment.SetEnvironmentVariable("PULSE_SINK", ttsOutputSinkName);
    if (enabled && !onsetExplicitInput)
    {
        var inputs = NativeLibraryBridge.GetAudioInputDevicesJson(nativeExe);
        onsetExplicitInput = OnsetCapturePolicy.IsExplicitInput(inputs, onsetCaptureId);
    }
    var handler = ttsRouter.ConfigureOnset(enabled, lastRealtimeLanguage,
        () => generation == Volatile.Read(ref onsetControlGeneration) && OnsetPermitted(), newSession, newSession || onsetIdleKnown);
    if (enabled && generation != Volatile.Read(ref onsetControlGeneration)) { ttsRouter.StopOnset(); handler = null; }
    if (handler != null && realtimeSession?.SetVadStartHandler(handler) != true) ttsRouter.DisableOnset("native-unavailable");
    else if (handler == null && !newSession) realtimeSession?.SetVadStartHandler(null);
}
string lastRealtimeStatusFinalEmitted = string.Empty;
void MaybeLearnAdaptiveAsrText(RealtimePollResult poll, string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return;
    if (!poll.ConfidenceAvailable || poll.ConfidenceScore is null or < 0.80)
        return;
    var trimmed = text.Trim();
    var textKey = NormalizeAdaptiveHotwordText(trimmed);
    if (string.IsNullOrEmpty(textKey)) return;
    var now = DateTimeOffset.UtcNow;
    if (textKey == lastAdaptiveVocabLearnedTextKey && (now - lastAdaptiveVocabLearnedAt).TotalSeconds < 10)
        return;
    lastAdaptiveVocabLearnedTextKey = textKey;
    lastAdaptiveVocabLearnedAt = now;
    var source = AdaptiveVocabSources.Asr;
    lock (adaptiveRmsLock)
    {
        if (poll.CaptureRms > 0)
        {
            if (adaptiveRmsCount >= 3 && adaptiveRmsAverage > 0 && poll.CaptureRms > adaptiveRmsAverage * adaptiveVocabOptions.EmphaticRmsMultiplier)
                source = AdaptiveVocabSources.AsrEmphatic;
            adaptiveRmsAverage = adaptiveRmsCount == 0 ? poll.CaptureRms : (adaptiveRmsAverage * Math.Min(adaptiveRmsCount, 9) + poll.CaptureRms) / (Math.Min(adaptiveRmsCount, 9) + 1);
            adaptiveRmsCount++;
        }
    }

    // Re-speak detection: within window + IPA similarity >= 0.4 → penalise old entry
    // (emphatic限定解除: 全finalで発火。同一ポール±tolerance秒は除外)
    var respeakElapsed = (now - lastAdaptiveFinalAt).TotalSeconds;
    if (!string.IsNullOrEmpty(lastAdaptiveFinalText)
        && respeakElapsed > adaptiveVocabOptions.RespeakSamePollToleranceSeconds
        && respeakElapsed <= adaptiveVocabOptions.RespeakWindowSeconds
        && !string.IsNullOrEmpty(lastAdaptiveFinalIpa)
        && !string.IsNullOrEmpty(currentAdaptiveIpa))
    {
        try
        {
            var similarity = PhoneticAssist.IpaSimilarity(currentAdaptiveIpa, lastAdaptiveFinalIpa);
            if (similarity >= 0.4 && !string.Equals(textKey, NormalizeAdaptiveHotwordText(lastAdaptiveFinalText), StringComparison.OrdinalIgnoreCase))
            {
                adaptiveVocabStore.AdjustPriority(lastAdaptiveFinalText, -20);
                adaptiveVocabLogger.LogInformation(
                    "[AdaptiveVocab] re-speak detected: old=[{Old}] new=[{New}] similarity={Similarity:F2} elapsed={Elapsed:F1}s → old priority -20",
                    lastAdaptiveFinalText, trimmed, similarity, respeakElapsed);
            }
        }
        catch (Exception ex)
        {
            adaptiveVocabLogger.LogDebug(ex, "[AdaptiveVocab] re-speak detection failed");
        }
    }

    // Update re-speak state
    lastAdaptiveFinalText = trimmed;
    lastAdaptiveFinalAt = now;
    if (!string.IsNullOrEmpty(currentAdaptiveIpa)) lastAdaptiveFinalIpa = currentAdaptiveIpa;

    try
    {
        var added = adaptiveVocabStore.AddText(trimmed, string.IsNullOrWhiteSpace(lastRealtimeLanguage) ? "auto" : lastRealtimeLanguage, source);
        if (added > 0)
            adaptiveVocabLogger.LogInformation("[AdaptiveVocab] learned {Count} entries source={Source} lang={Lang}", added, source, lastRealtimeLanguage);
    }
    catch (Exception ex)
    {
        adaptiveVocabLogger.LogWarning(ex, "[AdaptiveVocab] ASR learning failed");
    }
}

// PhoneticAssist (joint_auto 補助レーン) state — Windows(App.WebHost) parity
const string RealtimeEnginePhoneme = "phoneme";
string lastPhoneticAssistDebugKey = string.Empty;
string lastPhoneticAssistAcceptedKey = string.Empty;
string lastPhoneticAssistAcceptedText = string.Empty;
string lastPhoneticAssistAcceptedReason = string.Empty;
object phoneticAssistPrefetchLock = new();
string phoneticAssistPrefetchKey = string.Empty;
Task<string?>? phoneticAssistPrefetchTask = null;
// Talk 翻訳: 直近の翻訳結果と翻訳元 final（Windows parity）
string lastTranslatedFinalText = string.Empty;
string lastTranslatedFinalSource = string.Empty;
// TTS echo loop suppression: after TTS finishes, suppress ASR→TTS re-trigger for a cooldown period.
DateTimeOffset ttsSuppressUntil = DateTimeOffset.MinValue;
bool lastTtsSpeakingSnapshot = false;
const int TtsCooldownAfterSpeakMs = 2500; // suppress window after TTS trigger/finish (covers speaker echo via mic)
const int TtsRecentStartedEchoMs = 6000; // covers cases where status polling misses the speaking window

static string NormalizeRealtimeEchoText(string text) => text.Trim('.', '。', '、', ',', ' ', '　', '!', '！', '?', '？');

static bool IsShortPhoneticAssistPcm(RealtimePollResult poll, int expectedSampleRate)
{
    if (poll.FinalPcmSampleRate != expectedSampleRate) return false;
    var bytes = poll.FinalPcm16Le.Length;
    if (bytes < 3200) return false;
    var maxBytes = expectedSampleRate * 2 * 12 / 10; // 1.2s mono pcm16
    if (bytes > maxBytes) return false;
    return poll.VadSpeechCount > 0
        || string.Equals(poll.VadState, "speech", StringComparison.OrdinalIgnoreCase)
        || poll.CaptureRms > 0;
}

static string PhoneticAssistKey(RealtimePollResult poll, string primaryText) =>
    $"{poll.CaptureSamples}:{poll.FinalPcm16Le.Length}:{(primaryText ?? string.Empty).Trim()}:{poll.ConfidenceScore}";

bool ShouldRunPhoneticAssistForPrimary(RealtimePollResult poll, string primaryText)
{
    var primaryForPhoneticAssist = (primaryText ?? string.Empty).Trim();
    var primaryRuneCount = primaryForPhoneticAssist.EnumerateRunes().Count();
    var lowConfidence = !poll.ConfidenceAvailable || poll.ConfidenceScore is < 0.45;
    return primaryRuneCount <= 1 || lowConfidence;
}

void StartPhoneticAssistPrefetch(RealtimePollResult poll, string primaryText)
{
    if (phonemeCtcService?.IsAvailable != true
        || !string.Equals(lastRealtimeEngine, "joint_auto", StringComparison.OrdinalIgnoreCase)
        || !IsShortPhoneticAssistPcm(poll, phonemeCtcService.SampleRate)
        || !ShouldRunPhoneticAssistForPrimary(poll, primaryText))
        return;

    var key = PhoneticAssistKey(poll, primaryText);
    lock (phoneticAssistPrefetchLock)
    {
        if (key == phoneticAssistPrefetchKey) return;
        var pcm = poll.FinalPcm16Le.ToArray();
        phoneticAssistPrefetchKey = key;
        phoneticAssistPrefetchTask = Task.Run(() => phonemeCtcService.Transcribe(PcmToFloat32(pcm)));
    }
    phonemeCtcLogger?.LogDebug("[PhoneticAssist] prefetch started key={Key} bytes={Bytes}", key, poll.FinalPcm16Le.Length);
}

string? GetPrefetchedPhoneticAssistIpa(string key)
{
    Task<string?>? task;
    lock (phoneticAssistPrefetchLock)
        task = key == phoneticAssistPrefetchKey ? phoneticAssistPrefetchTask : null;
    return task?.GetAwaiter().GetResult();
}

IReadOnlyList<PhoneticHotword> BuildAdaptiveHotwords(string? lang)
{
    try
    {
        return adaptiveVocabStore.GetTopEntries(lang, maxCount: adaptiveVocabOptions.PhoneticHotwordMaxCount)
            .Where(e => !string.IsNullOrWhiteSpace(e.Text) && !string.IsNullOrWhiteSpace(e.Phonemes))
            .Select(e => new PhoneticHotword(e.Text, string.Empty, e.Phonemes!, Enabled: true, Priority: e.Priority))
            .ToArray();
    }
    catch (Exception ex)
    {
        phonemeCtcLogger?.LogWarning(ex, "[AdaptiveVocab] hotword load failed");
        return Array.Empty<PhoneticHotword>();
    }
}

static string NormalizeAdaptiveHotwordText(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
    return new string(chars);
}

PhoneticAssistCandidate? TrySelectAdaptiveHotword(string ipa, string? lang, string primaryText)
{
    var primaryKey = NormalizeAdaptiveHotwordText(primaryText);
    var hotwords = BuildAdaptiveHotwords(lang)
        .Where(h => NormalizeAdaptiveHotwordText(h.Text) != primaryKey)
        .ToArray();
    if (hotwords.Length == 0) return null;
    var best = PhoneticAssist.RankHotwords(ipa, hotwords).FirstOrDefault();
    if (best == null) return null;
    phonemeCtcLogger?.LogDebug(
        "[AdaptiveVocab] hotword candidate text=[{Text}] score={Score} accepted={Accepted} reason={Reason}",
        best.Text,
        best.Score,
        best.Accepted,
        best.Reason);
    if (!best.Accepted || best.Score < adaptiveVocabOptions.PhoneticHotwordScoreThreshold)
        return null;
    // score >= 1.0 はIPA完全一致 = 自己マッチの可能性が高いため拒否
    if (best.Score >= 1.0)
    {
        phonemeCtcLogger?.LogDebug(
            "[AdaptiveVocab] hotword rejected self-match: text=[{Text}] score={Score}",
            best.Text, best.Score);
        return null;
    }
    return best;
}

// SenseVoice fallback幻聴gate (2026-07-14): セッション言語がCJK (ja/ko/zh) のとき、
// その言語のスクリプトを一切含まないSenseVoice出力（例: jaセッションで "The."）は
// 無音/ノイズからのdecode幻聴とみなして棄却する。en/autoはgate対象外。(Windows parity)
static bool SenseVoiceTextPlausibleForLanguage(string? text, string? lang)
{
    if (string.IsNullOrWhiteSpace(text)) return false;
    var l = (lang ?? string.Empty).Trim().ToLowerInvariant();
    if (l.StartsWith("ja"))
        return text.EnumerateRunes().Any(r =>
            (r.Value >= 0x3040 && r.Value <= 0x30FF) ||   // ひらがな/カタカナ
            (r.Value >= 0x4E00 && r.Value <= 0x9FFF));    // 漢字
    if (l.StartsWith("ko"))
        return text.EnumerateRunes().Any(r =>
            (r.Value >= 0xAC00 && r.Value <= 0xD7AF) ||   // ハングル音節
            (r.Value >= 0x1100 && r.Value <= 0x11FF));    // ハングル字母
    if (l.StartsWith("zh"))
        return text.EnumerateRunes().Any(r =>
            (r.Value >= 0x4E00 && r.Value <= 0x9FFF) ||
            (r.Value >= 0x3400 && r.Value <= 0x4DBF));
    return true;
}

void RememberAdaptiveHotwordPhonemes(string? text, string? lang, string? ipa)
{
    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(ipa)) return;
    var trimmed = text.Trim();
    if (trimmed.Length > 120 || !trimmed.Any(char.IsLetterOrDigit)) return;
    try
    {
        adaptiveVocabStore.SetPhonemesForText(trimmed, lang, ipa, AdaptiveVocabSources.Asr);
    }
    catch (Exception ex)
    {
        phonemeCtcLogger?.LogDebug(ex, "[AdaptiveVocab] phoneme store failed");
    }
}

(bool ttsSuppressed, bool loopbackSuspected) GetRealtimeTtsSuppressState(string selectedText)
{
    var ttsStatus = ttsRouter.GetStatus();
    var ttsNowSpeaking = ttsRouter.IsSpeakingForRealtimeBody;
    if (!ttsNowSpeaking && lastTtsSpeakingSnapshot)
        ttsSuppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(TtsCooldownAfterSpeakMs); // extend on TTS finish
    lastTtsSpeakingSnapshot = ttsNowSpeaking;

    var recentTtsStarted = DateTimeOffset.TryParse(ttsStatus.LastTtsStartedAt, out var lastTtsStartedAt)
        && DateTimeOffset.UtcNow - lastTtsStartedAt.ToUniversalTime() < TimeSpan.FromMilliseconds(TtsRecentStartedEchoMs);
    var ttsSuppressed = ttsNowSpeaking || DateTimeOffset.UtcNow < ttsSuppressUntil || recentTtsStarted;
    var normalizedSelected = NormalizeRealtimeEchoText(selectedText);
    var normalizedRealtimeTts = NormalizeRealtimeEchoText(lastRealtimeTtsFinal);
    var normalizedLastTts = NormalizeRealtimeEchoText(ttsStatus.LastTtsText ?? string.Empty);
    var sameAsRecentTts = !string.IsNullOrEmpty(normalizedSelected)
        && ((!string.IsNullOrEmpty(normalizedRealtimeTts) && string.Equals(normalizedSelected, normalizedRealtimeTts, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(normalizedLastTts) && string.Equals(normalizedSelected, normalizedLastTts, StringComparison.OrdinalIgnoreCase)));
    return (ttsSuppressed, ttsSuppressed || sameAsRecentTts);
}

void TraceRealtimeTtsDecision(RealtimePollResult poll, bool dropped, string speakText, bool duplicate, bool suppressed)
{
    if (Environment.GetEnvironmentVariable("MOBMATE_PRIVATE_ENDPOINT_TRACE") != "1") return;
    var status = ttsRouter.GetStatus();
    logger.LogInformation("[PrivateEndpoint] tts_guard finalDetectedAtMs={Detected} epoch=current enabled={Enabled} dropped={Dropped} speakable={Speakable} duplicate={Duplicate} suppressed={Suppressed} speaking={Speaking} suppressUntil={Until} lastStarted={Started}",
        poll.FinalDetectedAtMs, realtimeTtsEnabled, dropped, HasSpeakableText(speakText), duplicate, suppressed,
        status.IsSpeaking, ttsSuppressUntil.ToUnixTimeMilliseconds(), status.LastTtsStartedAt);
}

TtsProsodyProfile? AnalyzeRealtimeSourceProsody(string text, RealtimePollResult poll)
{
    return poll.FinalPcm16Le.Length >= 3200 && poll.FinalPcmSampleRate > 0
        ? TtsProsodyAnalyzer.AnalyzeTextAndPcm(text, poll.FinalPcm16Le, poll.FinalPcmSampleRate)
        : null;
}

// PhoneticAssist: joint_auto の final で空/1文字/低confidence時のみ音素CTCを実行し、
// filler accepted のときだけ最終テキストを置換する（Windows App.WebHost と同一条件・しきい値）。
(string? Text, string? Engine, string? DecisionReason) TryApplyPhoneticAssist(RealtimePollResult poll, string primaryText)
{
    var outputTicket = realtimeOutputEpoch.Capture();
    if (phonemeCtcService?.IsAvailable != true
        || !string.Equals(lastRealtimeEngine, "joint_auto", StringComparison.OrdinalIgnoreCase)
        || poll.FinalPcm16Le.Length < 3200
        || poll.FinalPcmSampleRate != phonemeCtcService.SampleRate)
        return (null, null, null);

    var primaryForPhoneticAssist = (primaryText ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(primaryForPhoneticAssist))
        return (null, null, null);
    var shouldRunPhoneticAssist = ShouldRunPhoneticAssistForPrimary(poll, primaryForPhoneticAssist);
    var debugKey = PhoneticAssistKey(poll, primaryForPhoneticAssist);
    if (debugKey == lastPhoneticAssistAcceptedKey && !string.IsNullOrEmpty(lastPhoneticAssistAcceptedText))
        return (lastPhoneticAssistAcceptedText, RealtimeEnginePhoneme,
            string.IsNullOrEmpty(lastPhoneticAssistAcceptedReason) ? "phonetic_assist_filler" : lastPhoneticAssistAcceptedReason);
    if (debugKey == lastPhoneticAssistDebugKey)
        return (null, null, null);

    lastPhoneticAssistDebugKey = debugKey;
    var ipa = GetPrefetchedPhoneticAssistIpa(debugKey)
        ?? phonemeCtcService.Transcribe(PcmToFloat32(poll.FinalPcm16Le));
    if (ipa == null)
        return (null, null, null);
    // Store IPA for re-speak detection (current poll; previous is in lastAdaptiveFinalIpa)
    currentAdaptiveIpa = ipa;

    var lang = string.IsNullOrEmpty(lastRealtimeLanguage) ? poll.Language : lastRealtimeLanguage;
    var assistLang = ResolvePhoneticAssistLanguage(lang);
    var hotword = TrySelectAdaptiveHotword(ipa, lastRealtimeLanguage, primaryForPhoneticAssist);
    if (hotword != null)
    {
        lastPhoneticAssistAcceptedKey = debugKey;
        lastPhoneticAssistAcceptedText = hotword.Text;
        lastPhoneticAssistAcceptedReason = "phonetic_assist_hotword";
        phonemeCtcLogger?.LogInformation(
            "[AdaptiveVocab] hotword accepted primary=[{Primary}] output=[{Output}] score={Score} confidence={Confidence}",
            primaryForPhoneticAssist,
            hotword.Text,
            hotword.Score,
            poll.ConfidenceScore);
        return (hotword.Text, RealtimeEnginePhoneme, "phonetic_assist_hotword");
    }

    if (poll.ConfidenceAvailable && poll.ConfidenceScore is >= 0.80)
        realtimeOutputEpoch.Commit(outputTicket,
            () => { RememberAdaptiveHotwordPhonemes(primaryForPhoneticAssist, lastRealtimeLanguage, ipa); return true; }, () => false);

    if (!shouldRunPhoneticAssist)
        return (null, null, null);

    var filler = PhoneticAssist.TryFiller(primaryForPhoneticAssist, ipa, assistLang);
    var fallback = filler.Accepted ? filler : PhoneticAssist.TrySurfaceFallback(primaryForPhoneticAssist, ipa, assistLang);
    // P0 2026-07-14: surface レーンは音響ゲート必須（ノイズPCM由来のゴミ音節をTTSに流さない）
    if (fallback.Accepted && fallback.Lane == "surface"
        && !PhoneticAssist.PcmLooksArticulated(poll.FinalPcm16Le, poll.FinalPcmSampleRate))
    {
        phonemeCtcLogger?.LogDebug(
            "[PhoneticAssist] surface rejected by pcm gate output=[{Output}] pcmBytes={PcmBytes}",
            fallback.OutputText ?? string.Empty, poll.FinalPcm16Le.Length);
        fallback = PhoneticAssistResult.Reject("surface", ipa, "pcm_not_articulated");
    }
    var surface = PhoneticAssist.IpaToText(ipa, assistLang);
    if (fallback.Accepted && !string.IsNullOrEmpty(fallback.OutputText))
    {
        lastPhoneticAssistAcceptedKey = debugKey;
        lastPhoneticAssistAcceptedText = fallback.OutputText;
        lastPhoneticAssistAcceptedReason = $"phonetic_assist_{fallback.Lane}";
    }
    phonemeCtcLogger?.LogDebug(
        "[PhoneticAssist] joint_auto debug primary=[{Primary}] ipa=[{Ipa}] surface=[{Surface}] accepted={Accepted} lane={Lane} output=[{Output}] reason={Reason} confidence={Confidence}",
        primaryForPhoneticAssist, ipa, surface, fallback.Accepted, fallback.Lane, fallback.OutputText ?? string.Empty, fallback.Reason, poll.ConfidenceScore);

    return fallback.Accepted && !string.IsNullOrEmpty(fallback.OutputText)
        ? (fallback.OutputText, RealtimeEnginePhoneme, $"phonetic_assist_{fallback.Lane}")
        : (null, null, null);
}

// Talk 翻訳が有効なら翻訳してから発話、無効なら原文発話（Windows parity）。
// 翻訳は非同期で実行し、結果は lastTranslatedFinalText/Source に保存して次回 poll 応答で字幕反映。
void TriggerRealtimeSpeak(string speakText, string sourceFinalKey, string srcLang, TtsProsodyProfile? prosody, long finalDetectedAtMs = 0)
{
    var outputTicket = realtimeOutputEpoch.Capture();
    var talkTarget = LoadTranslationConfig().TalkTarget;
    if (talkTarget != "OFF" && translationService.IsLoaded)
    {
        var capturedSpeak  = speakText;
        var capturedKey    = sourceFinalKey;
        var capturedLang   = (string.IsNullOrWhiteSpace(srcLang) || srcLang == "auto") ? "ja" : srcLang;
        var capturedProsody = prosody;
        var capturedFinalDetectedAtMs = finalDetectedAtMs;
        _ = Task.Run(async () =>
        {
            var finalSpeak = capturedSpeak;
            try
            {
                var translated = translationService.Translate(capturedSpeak, capturedLang, talkTarget);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    finalSpeak = translated;
                    realtimeOutputEpoch.Commit(outputTicket, () =>
                    {
                    lastTranslatedFinalText   = translated;
                    lastTranslatedFinalSource = capturedKey;
                    UnlockSteamAchievement(SteamAchievementService.FirstTranslatedOutput);
                    translationLogger.LogDebug("[Translation] {Src}->{Tgt}: [{Original}] -> [{Translated}]",
                        capturedLang, talkTarget, capturedSpeak, translated);
                    return true;
                    }, () => false);
                }
            }
            catch (Exception ex)
            {
                translationLogger.LogWarning(ex, "[Translation] realtime translate failed, fallback original");
            }
            await realtimeOutputEpoch.Commit<Task>(outputTicket,
                () => SpeakWithOutputRoutingAsync(finalSpeak, false, capturedProsody, capturedFinalDetectedAtMs, outputTicket.Cancellation),
                () => Task.CompletedTask);
        }).ContinueWith(t => logger.LogError(t.Exception?.GetBaseException(), "[TTS] realtime translate-speak failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
    else
    {
        _ = realtimeOutputEpoch.Commit<Task>(outputTicket,
                () => SpeakWithOutputRoutingAsync(speakText, false, prosody, finalDetectedAtMs, outputTicket.Cancellation),
                () => Task.CompletedTask)
            .ContinueWith(t => logger.LogError(t.Exception?.GetBaseException(), "[TTS] realtime speak failed"),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}

var benchRealtimeJob = new BenchJobState();
object benchRealtimeOptionsLock = new object();
(string route, string engine, string language, string scene, string prefilter, string moonshineDllDir, string moonshineModelRoot, int moonshineThreads) benchRealtimeOptions =
    ("realtime", "joint_auto", "ja", "short_command", "auto", string.Empty, string.Empty, 0);

// ---- Linux bench helpers (live ASR と同じ経路でベンチを通す) ----
// BenchManifestRow: (Index, Wav, Expected, Language, Scene)
static List<(int Index, string Wav, string Expected, string Language, string Scene)> ParseBenchManifestCsv(string path)
{
    var rows = new List<(int, string, string, string, string)>();
    if (!File.Exists(path)) return rows;
    var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
    for (int i = 1; i < lines.Length; i++)
    {
        var cols = SplitCsvLine(lines[i]);
        if (cols.Count < 4) continue;
        rows.Add((i - 1, cols[0], cols[1], cols[2], cols[3]));
    }
    return rows;
}

static List<string> SplitCsvLine(string line)
{
    var result = new List<string>();
    int i = 0;
    while (i < line.Length)
    {
        if (line[i] == '"')
        {
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < line.Length)
            {
                if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                else if (line[i] == '"') { i++; break; }
                else { sb.Append(line[i++]); }
            }
            result.Add(sb.ToString());
            if (i < line.Length && line[i] == ',') i++;
        }
        else
        {
            int start = i;
            while (i < line.Length && line[i] != ',') i++;
            result.Add(line.Substring(start, i - start));
            if (i < line.Length && line[i] == ',') i++;
        }
    }
    return result;
}

static (float[] samples, int sampleRate) ReadWavAsFloat32ForBench(string path)
{
    try
    {
        using var f = File.OpenRead(path);
        using var reader = new System.IO.BinaryReader(f);
        if (new string(reader.ReadChars(4)) != "RIFF") return (Array.Empty<float>(), 16000);
        reader.ReadInt32(); // fileSize
        if (new string(reader.ReadChars(4)) != "WAVE") return (Array.Empty<float>(), 16000);
        int sampleRate = 16000, channels = 1, bits = 16;
        while (f.Position < f.Length - 4)
        {
            var chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // audioFormat
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byteRate
                reader.ReadInt16(); // blockAlign
                bits = reader.ReadInt16();
                if (chunkSize > 16) reader.ReadBytes(chunkSize - 16);
            }
            else if (chunkId == "data")
            {
                if (bits == 16)
                {
                    var bytes = reader.ReadBytes(chunkSize);
                    int total = bytes.Length / 2;
                    if (channels <= 1)
                    {
                        var s = new float[total];
                        for (int i = 0; i < total; i++)
                            s[i] = System.BitConverter.ToInt16(bytes, i * 2) / 32768.0f;
                        return (s, sampleRate);
                    }
                    var mono = new float[total / channels];
                    for (int i = 0; i < mono.Length; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++) sum += System.BitConverter.ToInt16(bytes, (i * channels + c) * 2) / 32768.0f;
                        mono[i] = sum / channels;
                    }
                    return (mono, sampleRate);
                }
                f.Seek(chunkSize, SeekOrigin.Current);
            }
            else { f.Seek(chunkSize, SeekOrigin.Current); }
        }
    }
    catch { }
    return (Array.Empty<float>(), 16000);
}

/// <summary>
/// bench eval 用: 句読点・スペースを除去して正規化。CER/exact の比較に使う。
/// CJK/JP/KO/ASCII の代表的な文末記号と空白を除去し、大文字小文字を統一する。
/// </summary>
static string BenchNormText(string s)
{
    var sb = new System.Text.StringBuilder(s.Length);
    foreach (char c in s.Trim())
    {
        // 句読点・スペースをスキップ (CJK/JP/KO/ASCII)
        if (c is '。' or '、' or '！' or '？' or '·' or '…' or '!' or '?' or ',' or '.' or '，'
                or '\u3000' or ' ' or '\t' or '\u00B7')
            continue;
        sb.Append(char.ToLowerInvariant(c));
    }
    return sb.ToString();
}

static bool BenchExactMatch(string expected, string actual)
    => string.Equals(BenchNormText(expected), BenchNormText(actual), StringComparison.Ordinal);

static double BenchComputeCer(string expected, string actual)
{
    var e = BenchNormText(expected);
    var a = BenchNormText(actual);
    if (e.Length == 0) return a.Length == 0 ? 0.0 : 1.0;
    var dp = new int[e.Length + 1, a.Length + 1];
    for (int i = 0; i <= e.Length; i++) dp[i, 0] = i;
    for (int j = 0; j <= a.Length; j++) dp[0, j] = j;
    for (int i = 1; i <= e.Length; i++)
        for (int j = 1; j <= a.Length; j++)
        {
            int cost = e[i - 1] == a[j - 1] ? 0 : 1;
            dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
        }
    return (double)dp[e.Length, a.Length] / e.Length;
}

static string BuildBenchJsonlRow(int index, string wav, string expected, string transcript,
    string selectedEngine, string decisionReason, string requestedEngine, string language,
    bool exact, double cer, int latencyMs, string? error)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("{\"index\":").Append(index);
    sb.Append(",\"wav\":\"").Append(JsonEscapeString(wav)).Append('"');
    sb.Append(",\"expected\":\"").Append(JsonEscapeString(expected)).Append('"');
    sb.Append(",\"transcript\":\"").Append(JsonEscapeString(transcript)).Append('"');
    sb.Append(",\"exact\":").Append(exact ? "true" : "false");
    sb.Append(",\"cer\":").Append(cer.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    sb.Append(",\"latency_ms\":").Append(latencyMs);
    sb.Append(",\"route\":\"linux_csharp_bench\"");
    sb.Append(",\"engine\":\"").Append(JsonEscapeString(requestedEngine)).Append('"');
    sb.Append(",\"selected_engine\":\"").Append(JsonEscapeString(selectedEngine)).Append('"');
    sb.Append(",\"decision_reason\":\"").Append(JsonEscapeString(decisionReason)).Append('"');
    sb.Append(",\"language\":\"").Append(JsonEscapeString(language)).Append('"');
    if (!string.IsNullOrEmpty(error))
        sb.Append(",\"error\":\"").Append(JsonEscapeString(error)).Append('"');
    sb.Append('}');
    return sb.ToString();
}

static string JsonEscapeString(string s)
    => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

// base opts JSON に language/scene を上書きして返す（JsonSerializer を使わず文字列操作）
static string SetJsonBenchField(string baseJson, string language, string scene)
{
    // シンプルに末尾の } を削ってフィールドを追加する
    var j = baseJson.TrimEnd();
    if (j.EndsWith('}')) j = j[..^1];
    j += $",\"language\":\"{JsonEscapeString(language)}\",\"scene\":\"{JsonEscapeString(scene)}\"}}";
    return j;
}

// ---- end Linux bench helpers ----

string ResolveDefaultBenchManifestPath()
{
    var candidates = new[]
    {
        Path.Combine(appRoot, "workbenchi", "asr-core-15", "manifest.csv"),
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "..", "..", "..", "workbenchi", "asr-core-15", "manifest.csv")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_MobMateV2", "workbenchi", "asr-core-15", "manifest.csv")
    };
    return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
}

static int CountManifestRows(string manifestPath)
{
    return File.Exists(manifestPath)
        ? Math.Max(0, File.ReadLines(manifestPath).Count(l => !string.IsNullOrWhiteSpace(l)) - 1)
        : 0;
}

static bool IsAllowedBenchValue(string value, params string[] allowed) =>
    allowed.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

app.MapGet("/api/bench-realtime-route/status", () => Results.Ok(benchRealtimeJob.Snapshot()));

app.MapPost("/api/bench-realtime-route/start", (JsonElement body) =>
{
    var snap = benchRealtimeJob.Snapshot();
    if (snap.State == "running")
        return Results.Conflict(new { code = "bench.realtime_route_already_running", message = "realtime route bench already running", job = snap });

    if (realtimeSession != null)
    {
        var poll = realtimeSession.Poll();
        if (!string.Equals(poll.State, "completed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(poll.State, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { code = "bench.realtime_session_active", message = "stop realtime recording before starting realtime route bench", state = poll.State });
        }
        realtimeSession.Dispose();
        realtimeSession = null;
    }

    var engine = "joint_auto";
    var language = "ja";
    var scene = "short_command";
    var prefilter = "off";
    var initialPrompt = string.Empty;
    var manifestPath = ResolveDefaultBenchManifestPath();
    var effectiveMoonshineDllDir = moonshineDllDir;
    var effectiveMoonshineModelRoot = moonshineModelRoot;
    var defaultMoonshineThreads = Math.Max(1, Environment.ProcessorCount / 2);
    var requestedMoonshineThreads = defaultMoonshineThreads;
    if (body.TryGetProperty("engine", out var eng) && eng.ValueKind == JsonValueKind.String) engine = eng.GetString() ?? engine;
    if (body.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String) language = lang.GetString() ?? language;
    if (body.TryGetProperty("scene", out var sc) && sc.ValueKind == JsonValueKind.String) scene = sc.GetString() ?? scene;
    if (body.TryGetProperty("prefilter", out var pf) && pf.ValueKind == JsonValueKind.String) prefilter = pf.GetString() ?? prefilter;
    if (body.TryGetProperty("initialPrompt", out var ip) && ip.ValueKind == JsonValueKind.String) initialPrompt = ip.GetString() ?? string.Empty;
    if (body.TryGetProperty("manifestPath", out var mp) && mp.ValueKind == JsonValueKind.String) manifestPath = mp.GetString() ?? manifestPath;
    if (body.TryGetProperty("moonshineDllDir", out var moonDll) && moonDll.ValueKind == JsonValueKind.String) effectiveMoonshineDllDir = moonDll.GetString() ?? effectiveMoonshineDllDir;
    if (body.TryGetProperty("moonshineModelRoot", out var moonRoot) && moonRoot.ValueKind == JsonValueKind.String) effectiveMoonshineModelRoot = moonRoot.GetString() ?? effectiveMoonshineModelRoot;
    if (body.TryGetProperty("moonshineThreads", out var moonThreads)
        && (moonThreads.ValueKind != JsonValueKind.Number || !moonThreads.TryGetInt32(out requestedMoonshineThreads)
            || requestedMoonshineThreads < 1 || requestedMoonshineThreads > defaultMoonshineThreads))
    {
        return Results.BadRequest(ApiError($"moonshineThreads must be an integer between 1 and {defaultMoonshineThreads}", "realtime.invalid_moonshine_threads"));
    }

    if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        return Results.BadRequest(ApiError("bench manifest not found", "bench.manifest_not_found"));
    if (!IsAllowedBenchValue(engine, "whisper", "moonshine", "joint_auto"))
        return Results.BadRequest(ApiError("engine must be whisper, moonshine, or joint_auto", "realtime.invalid_engine"));
    if (!IsAllowedBenchValue(scene, "short_command", "talk_clean", "talk_low_voice", "talk_noisy", "desktop_hearing"))
        return Results.BadRequest(ApiError("invalid scene", "realtime.invalid_scene"));
    if (!IsAllowedBenchValue(prefilter, "auto", "normal", "strong", "off"))
        return Results.BadRequest(ApiError("prefilter must be auto, normal, strong, or off", "realtime.invalid_prefilter"));
    if (initialPrompt.Length > 2000)
        return Results.BadRequest(ApiError("initialPrompt is too long", "realtime.initial_prompt_too_long"));

    var effectiveMoonshineThreads = string.Equals(engine, "moonshine", StringComparison.OrdinalIgnoreCase)
        ? requestedMoonshineThreads
        : defaultMoonshineThreads;

    var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var outDir = Path.Combine(appRoot, "temp", "bench");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"rtbench_{ts}.jsonl");
    var manifestTotal = CountManifestRows(manifestPath);
    benchRealtimeJob.Start(total: manifestTotal);
    benchRealtimeJob.ResultPath = outPath;
    lock (benchRealtimeOptionsLock)
    {
        benchRealtimeOptions = ("realtime", engine, language, scene, prefilter, effectiveMoonshineDllDir, effectiveMoonshineModelRoot, effectiveMoonshineThreads);
    }

    _ = Task.Run(async () =>
    {
        try
        {
            // Linux bench: live ASR と同じ経路（make_poll_json Linux path と同等）
            // 1. Moonshine native one-shot (transcribe_pcm16_core 経由)
            // 2. joint_auto かつ JA/ZH/KO: C# SenseVoice/ParaformerZh overlay (poll handler と同じロジック)
            // 3. 選択: Paraformer > SenseVoice > Moonshine
            // Whisper engine 指定時は Whisper one-shot のみ
            var moonOpts = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["engine"] = "moonshine",
                ["moonshineDllDir"] = effectiveMoonshineDllDir,
                ["moonshineModelRoot"] = effectiveMoonshineModelRoot,
                ["threads"] = effectiveMoonshineThreads,
                ["vadSilenceDurationMs"] = 3000,
                ["usePrefilter"] = prefilter != "off",
                ["prefilterProfile"] = prefilter == "off" ? "auto" : prefilter,
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var whisperOpts = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["engine"] = "whisper",
                ["modelPath"] = whisperModelPath,
                ["whisperDllDir"] = whisperDllDir,
                ["threads"] = Math.Max(1, Environment.ProcessorCount / 2),
                ["useGpu"] = whisperUseGpu,
                ["vadSilenceDurationMs"] = 3000,
                ["usePrefilter"] = prefilter != "off",
                ["prefilterProfile"] = prefilter == "off" ? "auto" : prefilter,
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            var rows = ParseBenchManifestCsv(manifestPath);
            benchRealtimeJob.Total = rows.Count;

            var svLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ja", "zh", "ko" };
            bool isJointAuto = string.Equals(engine, "joint_auto", StringComparison.OrdinalIgnoreCase);
            bool isWhisper = string.Equals(engine, "whisper", StringComparison.OrdinalIgnoreCase);

            await using var writer = new StreamWriter(outPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            int completed = 0, exactCount = 0;
            double cerSum = 0.0;

            foreach (var row in rows)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string finalText = "";
                string selectedEngine = "none";
                string decisionReason = "linux_bench";
                string? rowError = null;

                try
                {
                    // Step 1: Moonshine + Whisper (joint_auto: 両方実行してfast-path選択)
                    // live ASR make_poll_json Linux path と同等のロジック
                    string moonText = "", whisperText = "";
                    int moonLatencyMs = 0, whisperLatencyMs = 0;

                    if (!isWhisper)
                    {
                        var rowOpts = SetJsonBenchField(moonOpts, row.Language, row.Scene);
                        var r = NativeLibraryBridge.TranscribeWavWithOptionsJson(row.Wav, rowOpts);
                        if (r.Success) { moonText = r.Transcript ?? ""; moonLatencyMs = r.LatencyMs; }
                    }
                    if (isWhisper || isJointAuto)
                    {
                        var rowOpts = SetJsonBenchField(whisperOpts, row.Language, row.Scene);
                        var r = NativeLibraryBridge.TranscribeWavWithOptionsJson(row.Wav, rowOpts);
                        if (r.Success) { whisperText = r.Transcript ?? ""; whisperLatencyMs = r.LatencyMs; }
                        else if (isWhisper) { rowError = r.Message; }
                    }

                    // Fast-path selection (live ASR と同じロジック)
                    if (isJointAuto && !string.IsNullOrEmpty(moonText) && !string.IsNullOrEmpty(whisperText))
                    {
                        int lenDelta = moonText.Length - whisperText.Length;
                        bool moonFast = moonLatencyMs <= 180 && lenDelta >= -4;
                        if (moonFast) { finalText = moonText; selectedEngine = "moonshine"; decisionReason = "linux_bench_joint_fastpath_moonshine"; }
                        else { finalText = whisperText; selectedEngine = "whisper"; decisionReason = "linux_bench_joint_fallback_whisper"; }
                    }
                    else if (!string.IsNullOrEmpty(moonText)) { finalText = moonText; selectedEngine = "moonshine"; decisionReason = "linux_bench_moonshine"; }
                    else if (!string.IsNullOrEmpty(whisperText)) { finalText = whisperText; selectedEngine = "whisper"; decisionReason = "linux_bench_whisper"; }

                    // Step 2: SenseVoice/ParaformerZh overlay (joint_auto + JA/ZH/KO のみ)
                    // poll handler と同じロジック: Paraformer/SenseVoice が勝てば上書き
                    if (isJointAuto && svLanguages.Contains(row.Language))
                    {
                        var (pcm, rate) = ReadWavAsFloat32ForBench(row.Wav);
                        if (pcm.Length >= 1600)
                        {
                            // ZH: SenseVoice 優先（Linux実測で Paraformer+1件上回る）、空なら Paraformer fallback
                            // JA/KO: SenseVoice のみ
                            string? pfText = null, svText = null;
                            if (senseVoiceService?.IsAvailable == true)
                            {
                                svText = senseVoiceService.Transcribe(pcm, rate, row.Language);
                            }
                            if (string.IsNullOrWhiteSpace(svText) &&
                                paraformerZhService?.IsAvailable == true &&
                                string.Equals(row.Language, "zh", StringComparison.OrdinalIgnoreCase))
                            {
                                pfText = paraformerZhService.Transcribe(pcm, rate);
                            }
                            if (!string.IsNullOrWhiteSpace(svText))
                            {
                                finalText = svText!;
                                selectedEngine = "sense_voice";
                                decisionReason = "linux_bench_sense_voice";
                            }
                            else if (!string.IsNullOrWhiteSpace(pfText))
                            {
                                finalText = pfText!;
                                selectedEngine = "paraformer_zh";
                                decisionReason = "linux_bench_paraformer_zh";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    rowError = ex.Message;
                }

                sw.Stop();
                int latencyMs = (int)sw.ElapsedMilliseconds;
                bool isExact = BenchExactMatch(row.Expected, finalText);
                double cer = BenchComputeCer(row.Expected, finalText);

                await writer.WriteLineAsync(BuildBenchJsonlRow(
                    row.Index, row.Wav, row.Expected, finalText,
                    selectedEngine, decisionReason, engine, row.Language,
                    isExact, cer, latencyMs, rowError));
                await writer.FlushAsync();

                completed++;
                if (isExact) exactCount++;
                cerSum += cer;
                benchRealtimeJob.Completed = completed;
                benchRealtimeJob.ExactCount = exactCount;
                benchRealtimeJob.AvgCer = completed > 0 ? Math.Round(cerSum / completed, 4) : null;
            }

            benchRealtimeJob.Complete(outPath);
        }
        catch (Exception ex)
        {
            benchRealtimeJob.Fail(ex.Message);
        }
    });

    return Results.Ok(new { code = "bench.realtime_route_started", message = "realtime route bench started", jobId = benchRealtimeJob.JobId, resultPath = outPath });
});

app.MapGet("/api/bench-realtime-route/results", () =>
{
    var snap = benchRealtimeJob.Snapshot();
    if (snap.State != "completed" || string.IsNullOrEmpty(snap.ResultPath))
        return Results.NotFound(ApiError("no completed results", "bench.no_completed_results"));
    return Results.Ok(new
    {
        jobId = snap.JobId,
        total = snap.Total,
        completed = snap.Completed,
        exactCount = snap.ExactCount,
        avgCer = snap.AvgCer,
        resultPath = snap.ResultPath,
        options = new { benchRealtimeOptions.route, benchRealtimeOptions.engine, benchRealtimeOptions.language, benchRealtimeOptions.scene, benchRealtimeOptions.prefilter, benchRealtimeOptions.moonshineDllDir, benchRealtimeOptions.moonshineModelRoot, benchRealtimeOptions.moonshineThreads }
    });
});

app.MapPost("/api/realtime/create", async (HttpRequest req) =>
{
    var lease = addonWorkGate.TryEnterWork();
    if (lease is null) return Results.Conflict(new { ok = false, code = "addon.busy" });
    await realtimeMutationGate.WaitAsync();
    try
    {
    if (IsDemoSessionExpired()) return DemoSessionExpiredResult();
    if (realtimeSession != null)
        return Results.Ok(new { ok = false, error = "session already active" });
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    try
    {
        realtimeSessionService.Create(() => RealtimeSessionController.Create(nativeExe, body, logger));
        realtimeSession = realtimeSessionService.Current;
        realtimeOutputEpoch.Begin();
        realtimeWorkLease = lease;
        lease = null;
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
    }
    finally { lease?.Dispose(); realtimeMutationGate.Release(); }
});

app.MapPost("/api/realtime/start", async (HttpRequest req) =>
{
    var onsetStartGeneration = Volatile.Read(ref onsetControlGeneration);
    var startLease = addonWorkGate.TryEnterWork();
    if (startLease is null) return Results.Conflict(new { ok = false, code = "addon.busy" });
    await realtimeMutationGate.WaitAsync();
    try
    {
        if (IsDemoSessionExpired()) return DemoSessionExpiredResult();
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        // Inject optional ASR paths if available (Phase 14: Linux ASR)
        string sessionOptions = body;
        string clientSource = "unknown";
        long clientOperationId = 0;
        string requestedEngine = string.Empty;
        string requestedLanguage = string.Empty;
        bool useLanguageProfile = false;
        string? requestedAddonMode = null;
        bool requestedTtsEnabled = true;
        bool requestedFillerEnabled = false;
        string onsetCaptureDeviceId = "";
        try
        {
            using var startDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            requestedEngine = startDoc.RootElement.TryGetProperty("engine", out var startEngine) ? startEngine.GetString() ?? string.Empty : string.Empty;
            requestedLanguage = startDoc.RootElement.TryGetProperty("language", out var startLanguage) ? startLanguage.GetString() ?? string.Empty : string.Empty;
            useLanguageProfile = startDoc.RootElement.TryGetProperty("useLanguageProfile", out var profileFlag) && profileFlag.ValueKind == JsonValueKind.True;
            requestedAddonMode = startDoc.RootElement.TryGetProperty("addonLanguageMode", out var modeFlag)
                ? modeFlag.ValueKind == JsonValueKind.String ? modeFlag.GetString() ?? "" : "" : null;
            requestedTtsEnabled = !startDoc.RootElement.TryGetProperty("ttsEnabled", out var ttsProp) || ttsProp.GetBoolean();
            requestedFillerEnabled = startDoc.RootElement.TryGetProperty("fillerEnabled", out var fillerProp) && fillerProp.ValueKind == JsonValueKind.True;
            onsetCaptureDeviceId = startDoc.RootElement.TryGetProperty("captureDeviceId", out var captureProp) ? captureProp.GetString() ?? "" : "";
            clientSource = startDoc.RootElement.TryGetProperty("clientSource", out var sourceProp) ? sourceProp.GetString() ?? clientSource : clientSource;
            clientOperationId = startDoc.RootElement.TryGetProperty("clientOperationId", out var opProp) && opProp.TryGetInt64(out var op) ? op : 0;
        }
        catch
        {
            requestedEngine = string.Empty;
            requestedLanguage = string.Empty;
            requestedTtsEnabled = true;
        }
        // An explicit unsupported request never mutates session/profile/epoch
        // and never silently selects another engine, even with profile following.
        var requestedAddon = AddonCatalog.Engines.FirstOrDefault(e => e.Id == requestedEngine);
        if (requestedAddon is not null)
        {
            if (requestedAddon.Id != AddonCatalog.QwenId || requestedAddon.Platform != "linux-x64")
                return Results.BadRequest(new { ok = false, code = "addon.unsupported" });
            if (!requestedAddon.Models.Any(m => m.Languages.Contains(requestedLanguage)))
                return Results.BadRequest(ApiError("unsupported realtime language", "realtime.unsupported_language"));
        }
        await addonLoad;
        var desiredAtStart = string.IsNullOrWhiteSpace(requestedLanguage) || requestedLanguage == "auto" ? null : addonManager.Describe(requestedLanguage).Desired;
        var followsStandaloneProfile = useLanguageProfile && desiredAtStart?.Enabled == true && !desiredAtStart.JointRequested;
        var rejectRequested = RealtimeLanguagePolicy.RejectsExplicit(requestedEngine, requestedLanguage, !string.IsNullOrEmpty(whisperDllDir), moonshineModelRoot, JointRuntimeAvailable());
        // Preserve an already supported Joint language when it is explicitly
        // redirected to a standalone profile; do not relax other caller guards.
        var legacyJointProfile = requestedEngine == "joint_auto" && followsStandaloneProfile &&
            new[] { "ja", "en", "zh", "ko", "yue" }.Contains(requestedLanguage, StringComparer.OrdinalIgnoreCase);
        var rejectProfileJoint = useLanguageProfile && desiredAtStart?.Enabled == true && desiredAtStart.JointRequested &&
            RealtimeLanguagePolicy.RejectsExplicit("joint_auto", requestedLanguage, !string.IsNullOrEmpty(whisperDllDir), moonshineModelRoot, JointRuntimeAvailable());
        if ((rejectRequested && !legacyJointProfile) || rejectProfileJoint)
            return Results.BadRequest(ApiError("unsupported realtime language", "realtime.unsupported_language"));
        if (requestedAddonMode is not null && (requestedAddonMode is not ("requested" or "auto") ||
            desiredAtStart?.EngineId != AddonCatalog.QwenId || requestedAddonMode != desiredAtStart.LanguageMode))
            return Results.BadRequest(new { ok = false, code = "addon.invalid_language_mode" });
        // Keep the current session/output and desired profile untouched on a
        // rejected explicit language. Do not silently start a different route.
        lastRealtimeEngine = requestedEngine;
        lastRealtimeLanguage = requestedLanguage;
        realtimeTtsEnabled = requestedTtsEnabled;
        realtimeOutputEpoch.Invalidate();
        realtimeSessionService.Dispose();
        realtimeSession = null;
        realtimeWorkLease?.Dispose();
        realtimeWorkLease = null;
        await addonLoad;
        if (string.IsNullOrWhiteSpace(lastRealtimeLanguage) || lastRealtimeLanguage == "auto")
            await addonManager.StopAsync(req.HttpContext.RequestAborted);
        else await addonManager.ApplyLanguageAsync(lastRealtimeLanguage, req.HttpContext.RequestAborted);
        var addonState = addonManager.Describe();
        if (useLanguageProfile && !string.IsNullOrWhiteSpace(requestedLanguage) && requestedLanguage != "auto" &&
            addonState.Desired?.Enabled == true && (!addonState.State.Ready || !addonState.State.Enabled ||
                addonState.Language != requestedLanguage))
            return Results.Conflict(new { ok = false, code = "addon.not_ready" });
        var resolvedEngine = addonManager.ResolveRecognitionEngine(requestedEngine, useLanguageProfile);
        if (AddonCatalog.Engines.Any(e => e.Id == resolvedEngine) &&
            (resolvedEngine != AddonCatalog.QwenId || !addonState.State.Ready || !addonState.State.Enabled ||
             addonState.Desired?.EngineId != resolvedEngine || addonState.Language != requestedLanguage))
            return Results.Conflict(new { ok = false, code = "addon.not_ready" });
        var jointAddon = resolvedEngine == "joint_auto" && addonState.JointEligible && addonState.Desired?.EngineId == AddonCatalog.QwenId;
        var usesAddon = resolvedEngine == AddonCatalog.QwenId || jointAddon;
        if (usesAddon)
        {
            NativeLibraryBridge.RegisterAddonTranscriberV2(nativeExe,
                jointAddon ? addonTranscriber.JointCallbackV2 : addonTranscriber.CallbackV2, joint: jointAddon);
            var opts = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject ?? new JsonObject();
            opts["engine"] = resolvedEngine;
            opts["addonJointRequested"] = jointAddon;
            opts["addonJointLanguage"] = requestedLanguage;
            opts["addonEngineId"] = addonState.Desired!.EngineId;
            opts["addonLanguageMode"] = addonState.Desired.LanguageMode;
            opts["addonModelId"] = addonState.Desired.ModelId;
            opts["addonModelRevision"] = AddonCatalog.Engines.Single(e => e.Id == addonState.Desired.EngineId).Models.Single(m => m.Id == addonState.Desired.ModelId).Revision;
            sessionOptions = opts.ToJsonString();
        }
        lastRealtimeEngine = resolvedEngine;
        // PhoneticAssist: 新規セッションごとに採用キャッシュをリセット（Windows parity）
        currentAdaptiveIpa = string.Empty;
        lastPhoneticAssistDebugKey = string.Empty;
        lastPhoneticAssistAcceptedKey = string.Empty;
        lastPhoneticAssistAcceptedText = string.Empty;
        lastPhoneticAssistAcceptedReason = string.Empty;
        if ((!string.IsNullOrEmpty(moonshineDllDir) && !string.IsNullOrEmpty(moonshineModelRoot)) ||
            (!string.IsNullOrEmpty(whisperDllDir) && !string.IsNullOrEmpty(whisperModelPath)))
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(sessionOptions) ? "{}" : sessionOptions);
                var engine = doc.RootElement.TryGetProperty("engine", out var ep) ? ep.GetString() : "";
                if (engine == "joint_auto" || engine == "safe_auto" || engine == "moonshine" || engine == "whisper" || engine == AddonCatalog.QwenId)
                {
                    logger.LogInformation("[ASR] Injecting paths for engine={Engine} lang={Lang}", engine,
                        doc.RootElement.TryGetProperty("language", out var lp) ? lp.GetString() : "");
                    // Merge runtime paths into options JSON
                    var opts = new System.Text.Json.Nodes.JsonObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        opts[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
                    if (engine == "joint_auto" || engine == "safe_auto" || engine == "moonshine")
                    {
                        if (engine == "safe_auto") opts["engine"] = "moonshine";
                        opts["moonshineDllDir"]   = moonshineDllDir;
                        opts["moonshineModelRoot"] = moonshineModelRoot;
                    }
                    if (engine == "whisper" || engine == "joint_auto")
                    {
                        // joint_auto: Whisper paths injected as fallback when Moonshine fails
                        opts["whisperDllDir"] = whisperDllDir;
                        opts["whisperCpuDllDir"] = whisperCpuDllDir;
                        opts["modelPath"] = whisperModelPath;
                        opts["useGpu"] = whisperUseGpu;
                    }
                    if (!string.IsNullOrEmpty(sileroVadModelPath) && !string.IsNullOrEmpty(onnxRuntimeDllDir))
                    {
                        opts["sileroVadModelPath"] = sileroVadModelPath;
                        opts["onnxRuntimeDllDir"] = onnxRuntimeDllDir;
                        opts["vadSileroStartThresholdPermille"] = 550;
                        opts["vadSileroContinueThresholdPermille"] = 400;
                        opts["vadSileroSoftStartThresholdPermille"] = 300;
                    }
                    if (!opts.ContainsKey("vadRmsThreshold"))
                        opts["vadRmsThreshold"] = 900;
                    if (!opts.ContainsKey("vadSilenceDurationMs"))
                        opts["vadSilenceDurationMs"] = 1000;
                    sessionOptions = opts.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
            }
            catch { /* use body as-is on parse failure */ }
        }
        // Windows WebHost と同様、毎回 dispose+再作成して captureDeviceId などのオプションを反映する
        lastRealtimeTtsFinal = string.Empty;
        lastRealtimeStatusFinalEmitted = string.Empty;
        lastPhoneticAssistAcceptedKey = string.Empty;
        lastPhoneticAssistAcceptedText = string.Empty;
        lastPhoneticAssistAcceptedReason = string.Empty;
        lock (phoneticAssistPrefetchLock)
        {
            phoneticAssistPrefetchKey = string.Empty;
            phoneticAssistPrefetchTask = null;
        }
        var adaptivePrompt = adaptivePromptBuilder.Build(
            string.Empty,
            string.IsNullOrWhiteSpace(lastRealtimeLanguage) ? "auto" : lastRealtimeLanguage);
        try
        {
            var opts = JsonNode.Parse(string.IsNullOrWhiteSpace(sessionOptions) ? "{}" : sessionOptions) as JsonObject ?? new JsonObject();
            var existingPrompt = opts.TryGetPropertyValue("initialPrompt", out var promptNode) ? promptNode?.GetValue<string>() ?? string.Empty : string.Empty;
            adaptivePrompt = adaptivePromptBuilder.Build(existingPrompt, string.IsNullOrWhiteSpace(lastRealtimeLanguage) ? "auto" : lastRealtimeLanguage);
            opts["initialPrompt"] = adaptivePrompt.Prompt;
            if (!opts.ContainsKey("vadSilenceDurationMs"))
                opts["vadSilenceDurationMs"] = 1000;
            sessionOptions = opts.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (Exception ex)
        {
            adaptiveVocabLogger.LogWarning(ex, "[AdaptiveVocab] prompt injection failed");
        }
        logger.LogInformation("[Realtime] op={OperationId} source={Source} action=start ttsEnabled={TtsEnabled}", clientOperationId, clientSource, realtimeTtsEnabled);
        realtimeSession = null;
        realtimeSessionService.Start(() => RealtimeSessionController.Create(nativeExe, string.IsNullOrWhiteSpace(sessionOptions) ? "{}" : sessionOptions, logger));
        realtimeSession = realtimeSessionService.Current;
        onsetExplicitInput = false;
        Interlocked.Exchange(ref onsetInputChanged, 0);
        onsetIdleKnown = true;
        onsetCaptureId = onsetCaptureDeviceId;
        BindOnset(requestedFillerEnabled, newSession: true, expectedGeneration: onsetStartGeneration);
        realtimeOutputEpoch.Begin();
        realtimeWorkLease = startLease;
        startLease = null;
        UnlockSteamAchievement(SteamAchievementService.FirstSessionStart);
        return Results.Ok(new { ok = true, adaptivePromptInjected = adaptivePrompt.Injected, adaptiveVocabCount = adaptivePrompt.VocabCount });
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
    finally { startLease?.Dispose(); realtimeMutationGate.Release(); }
});

app.MapGet("/api/realtime/filler", () => Results.Ok(OnsetStatus()));
app.MapPost("/api/realtime/filler", async (HttpRequest request) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    if (body.RootElement.TryGetProperty("inputChanged", out var changed) && changed.ValueKind == JsonValueKind.True)
    {
        mobfyOnset.Invalidate();
        Interlocked.Exchange(ref onsetInputChanged, 1);
        ttsRouter.InvalidateOnsetConfiguration();
        return Results.Ok(OnsetStatus());
    }
    var hasEnabled = body.RootElement.TryGetProperty("enabled", out var value);
    var enabled = hasEnabled && value.ValueKind == JsonValueKind.True;
    var language = body.RootElement.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
    if (MobfyOnsetSelection.IsSelected(uiSettingsDocument))
    {
        var control = hasEnabled ? Interlocked.Increment(ref mobfyOnsetControlGeneration)
            : Volatile.Read(ref mobfyOnsetControlGeneration);
        if (hasEnabled) Volatile.Write(ref mobfyOnsetPreference, enabled);
        lock (voiceConversionSessionLock)
        {
            if (control != Volatile.Read(ref mobfyOnsetControlGeneration)) return Results.Ok(mobfyOnset.GetStatus());
            PrepareMobfyOnset(language);
            if (hasEnabled && !enabled) mobfyOnset.Stop();
            return Results.Ok(mobfyOnset.GetStatus());
        }
    }
    var prepareOnly = !hasEnabled && language != null;
    var generation = !prepareOnly ? Interlocked.Increment(ref onsetControlGeneration) : Volatile.Read(ref onsetControlGeneration);
    if (!prepareOnly && !enabled) ttsRouter.StopOnset();
    await realtimeMutationGate.WaitAsync();
    try
    {
        if (!prepareOnly)
        {
            if (generation != Volatile.Read(ref onsetControlGeneration)) return Results.Ok(OnsetStatus());
            if (realtimeSession == null) ttsRouter.StopOnset();
            else BindOnset(enabled, expectedGeneration: generation);
        }
        if (language != null && (prepareOnly || enabled)) ttsRouter.PrepareOnset(language, realtimeSession == null || onsetIdleKnown);
        return Results.Ok(OnsetStatus());
    }
    finally { realtimeMutationGate.Release(); }
});

app.MapPost("/api/realtime/stop", async (bool? discard) =>
{
    Interlocked.Increment(ref onsetControlGeneration);
    ttsRouter.StopOnset();
    realtimeOutputEpoch.Invalidate();
    await realtimeMutationGate.WaitAsync();
    realtimeOutputEpoch.Invalidate();
    var stopped = false;
    try
    {
        if (realtimeSessionService.Current == null) { stopped = true; return Results.Ok(new { ok = true, state = "idle" }); }
        if (discard == true) realtimeTtsEnabled = false;
        realtimeSessionService.Stop();
        realtimeSession = null;
        stopped = true;
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
    finally
    {
        if (stopped) { realtimeWorkLease?.Dispose(); realtimeWorkLease = null; }
        realtimeMutationGate.Release();
    }
});

app.MapGet("/api/realtime/poll", () =>
{
    realtimeMutationGate.Wait();
    try
    {
    if (realtimeSession == null)
        return Results.Ok(new { ok = false, state = "idle", error = "no session" });
    try
    {
        var outputTicket = realtimeOutputEpoch.Capture();
        var result = realtimeSession.Poll();
        if (result.Error == "capture_device_lost")
        {
            realtimeOutputEpoch.Invalidate();
            return Results.Ok(result);
        }
        return realtimeOutputEpoch.Commit<IResult>(outputTicket, () =>
        {
        MaybeLearnAdaptiveAsrText(result, !string.IsNullOrWhiteSpace(result.SelectedText) ? result.SelectedText : result.Final);
        return Results.Ok(result);
        }, () => Results.Ok(new { state = "idle", discarded = true }));
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
    }
    finally { realtimeMutationGate.Release(); }
});

app.MapGet("/api/realtime/status", () =>
{
    realtimeMutationGate.Wait();
    try
    {
    if (realtimeSession == null)
        return Results.Ok(new { state = "idle", partial = "", final = "", captureSamples = 0, captureRms = 0, vadState = "silence", error = (string?)null });
    try
    {
        var outputTicket = realtimeOutputEpoch.Capture();
        ttsRouter.RefreshOnset(OnsetPermitted, onsetIdleKnown);
        var poll = realtimeSession.Poll();
        onsetIdleKnown = poll.VadState == "silence";
        if (!string.IsNullOrEmpty(poll.Error)) ttsRouter.StopOnset();
        ttsRouter.RefreshOnset(OnsetPermitted, onsetIdleKnown);
        if (poll.Error == "capture_device_lost")
        {
            ttsRouter.StopOnset();
            realtimeOutputEpoch.Invalidate();
            return Results.Ok(poll);
        }
        StartPhoneticAssistPrefetch(poll, !string.IsNullOrWhiteSpace(poll.SelectedText) ? poll.SelectedText : poll.Final);
        string? senseVoiceFinal = null;
        string? paraformerFinal = null;
        var effectiveLanguage = string.IsNullOrWhiteSpace(poll.Language) ? lastRealtimeLanguage : poll.Language;
        bool isJointAutoZh = string.Equals(lastRealtimeEngine, "joint_auto", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(effectiveLanguage, "zh", StringComparison.OrdinalIgnoreCase);
        bool isExplicitSenseVoice = string.Equals(lastRealtimeEngine, "sense_voice", StringComparison.OrdinalIgnoreCase);
        bool isExplicitParaformer = string.Equals(lastRealtimeEngine, "paraformer_zh", StringComparison.OrdinalIgnoreCase);

        var pollTextForManagedAsr = !string.IsNullOrWhiteSpace(poll.SelectedText) ? poll.SelectedText : poll.Final;
        // Explicit native and add-on routes have already selected their final.
        // Only managed engines and the existing Auto policies may overlay it.
        if (!string.Equals(lastRealtimeEngine, "whisper", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(lastRealtimeEngine, "moonshine", StringComparison.OrdinalIgnoreCase) &&
            !AddonCatalog.Engines.Any(engine => engine.Id == lastRealtimeEngine) &&
            (isExplicitSenseVoice || isExplicitParaformer || !string.IsNullOrWhiteSpace(pollTextForManagedAsr)) &&
            poll.FinalPcm16Le.Length >= 1600 && poll.FinalPcmSampleRate > 0)
        {
            var samples = PcmToFloat32(poll.FinalPcm16Le);
            var svLang = string.IsNullOrWhiteSpace(effectiveLanguage) ? "auto" : effectiveLanguage;

            if (isExplicitParaformer)
            {
                // explicit paraformer_zh: Paraformer のみ
                if (paraformerZhService?.IsAvailable == true)
                {
                    paraformerFinal = paraformerZhService.Transcribe(samples, poll.FinalPcmSampleRate);
                    if (!string.IsNullOrWhiteSpace(paraformerFinal))
                        paraformerLogger.LogInformation("[ParaformerZh] linux final: {Text}", paraformerFinal);
                }
            }
            else if (senseVoiceService?.IsAvailable == true)
            {
                // joint_auto + JA/KO: native(Moonshine/Whisper)優先、SenseVoiceはfallback
                //   native が 2文字以上返した場合はSenseVoiceスキップ
                // joint_auto + ZH: SenseVoice優先維持（Linux実測でZH+1件上回る）
                var isJointAutoJaKo = string.Equals(lastRealtimeEngine, "joint_auto", StringComparison.OrdinalIgnoreCase)
                                      && !isJointAutoZh;
                var nativeRuneCount = pollTextForManagedAsr.EnumerateRunes().Count();
                var skipSenseVoice = isJointAutoJaKo && nativeRuneCount >= 2;
                if (skipSenseVoice)
                {
                    senseVoiceLogger.LogInformation(
                        "[SenseVoice] joint_auto JA/KO: native=[{Native}] ({Runes}chars) → native wins, SenseVoice skipped",
                        pollTextForManagedAsr, nativeRuneCount);
                }
                else
                {
                    senseVoiceFinal = senseVoiceService.Transcribe(samples, poll.FinalPcmSampleRate, svLang);
                    if (!string.IsNullOrWhiteSpace(senseVoiceFinal) &&
                        !SenseVoiceTextPlausibleForLanguage(senseVoiceFinal, svLang))
                    {
                        senseVoiceLogger.LogInformation(
                            "[SenseVoice] rejected: script mismatch lang={Lang} sv=[{Sv}]", svLang, senseVoiceFinal);
                        senseVoiceFinal = null;
                    }
                    if (!string.IsNullOrWhiteSpace(senseVoiceFinal))
                        senseVoiceLogger.LogInformation("[SenseVoice] linux final: {Text}", senseVoiceFinal);
                    // ZH joint_auto: SenseVoice が空なら Paraformer fallback
                    if (string.IsNullOrWhiteSpace(senseVoiceFinal) && isJointAutoZh && paraformerZhService?.IsAvailable == true)
                    {
                        paraformerFinal = paraformerZhService.Transcribe(samples, poll.FinalPcmSampleRate);
                        if (!string.IsNullOrWhiteSpace(paraformerFinal))
                            paraformerLogger.LogInformation("[ParaformerZh] linux final (fallback): {Text}", paraformerFinal);
                    }
                }
            }
        }
        var selectedFinal = !string.IsNullOrWhiteSpace(senseVoiceFinal) ? senseVoiceFinal : paraformerFinal;
        var selectedEngine = !string.IsNullOrWhiteSpace(senseVoiceFinal) ? "sense_voice" : "paraformer_zh";
        var selectedReason = !string.IsNullOrWhiteSpace(senseVoiceFinal) ? "linux_sense_voice" : "linux_paraformer_zh";
        var primary = string.IsNullOrWhiteSpace(selectedFinal) ? pollTextForManagedAsr : selectedFinal;
        var fresh = !string.IsNullOrWhiteSpace(primary) && !string.Equals(primary, lastRealtimeStatusFinalEmitted, StringComparison.Ordinal);
        var candidatePhonetic = fresh && !(isExplicitParaformer && string.IsNullOrWhiteSpace(selectedFinal) && paraformerZhService?.IsAvailable == true)
            ? TryApplyPhoneticAssist(poll, primary) : ((string? Text, string? Engine, string? DecisionReason))(null, null, null);
        return realtimeOutputEpoch.Commit<IResult>(outputTicket, () =>
        {
        if (string.IsNullOrWhiteSpace(selectedFinal))
        {
            if (string.Equals(lastRealtimeEngine, "paraformer_zh", StringComparison.OrdinalIgnoreCase) &&
                paraformerZhService?.IsAvailable == true)
            {
                LogRealtimeAsrReady(poll, poll.SelectedText ?? poll.Final ?? string.Empty, poll.SelectedEngine, poll.DecisionReason, "native");
                return Results.Ok(new
                {
                    state = poll.State,
                    partial = poll.Partial,
                    final = poll.Final,
                    selectedText = poll.SelectedText,
                    selectedEngine = poll.SelectedEngine,
                    decisionReason = poll.DecisionReason,
                    language = effectiveLanguage,
                    resolvedScene = poll.ResolvedScene,
                    confidenceAvailable = poll.ConfidenceAvailable,
                    confidenceScore = poll.ConfidenceScore,
                    confidenceUnavailableReason = poll.ConfidenceUnavailableReason,
                    latencyMs = poll.LatencyMs,
                    finalDetectedAtMs = poll.FinalDetectedAtMs,
                    finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
                    finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
                    error = poll.Error,
                    captureSamples = poll.CaptureSamples,
                    captureRms = poll.CaptureRms,
                    captureAgcGain = poll.CaptureAgcGain,
                    vadState = poll.VadState,
                    vadSpeechCount = poll.VadSpeechCount,
                    engineLoaded = true
                });
            }
            var pollFinal = !string.IsNullOrWhiteSpace(poll.SelectedText) ? poll.SelectedText : poll.Final;
            var suppressRepeatedPollFinal = !string.IsNullOrWhiteSpace(pollFinal) &&
                string.Equals(pollFinal, lastRealtimeStatusFinalEmitted, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(pollFinal) && !suppressRepeatedPollFinal)
                lastRealtimeStatusFinalEmitted = pollFinal;
            if (string.IsNullOrWhiteSpace(pollFinal))
                return Results.Ok(poll);
            if (suppressRepeatedPollFinal)
            {
                return Results.Ok(new
                {
                    state = poll.State,
                    partial = poll.Partial,
                    final = "",
                    selectedText = "",
                    selected_text = "",
                    selectedEngine = "none",
                    selected_engine = "none",
                    decisionReason = "no_new_transcript",
                    decision_reason = "no_new_transcript",
                    language = effectiveLanguage,
                    resolvedScene = poll.ResolvedScene,
                    confidenceAvailable = false,
                    confidence_available = false,
                    confidenceScore = (double?)null,
                    confidence_score = (double?)null,
                    confidenceUnavailableReason = poll.ConfidenceUnavailableReason,
                    confidence_unavailable_reason = poll.ConfidenceUnavailableReason,
                    latencyMs = poll.LatencyMs,
                    finalDetectedAtMs = poll.FinalDetectedAtMs,
                    finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
                    finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
                    error = poll.Error,
                    captureSamples = poll.CaptureSamples,
                    captureRms = poll.CaptureRms,
                    captureAgcGain = poll.CaptureAgcGain,
                    vadState = poll.VadState,
                    vadSpeechCount = poll.VadSpeechCount,
                    rawJson = poll.RawJson,
                    loopbackSuspected = false,
                    processedFinal = "",
                    textFilter = new { dropped = false, laughReplaced = false, ngMatched = false, reason = (string?)null }
                });
            }

            var pollFilterResult = RealtimeTextFilter.Apply(pollFinal, SnapshotTextFilterConfig(effectiveLanguage));
            var pollSuppress = GetRealtimeTtsSuppressState(pollFinal);
            var pollPhonetic = candidatePhonetic;
            var pollSpeakText = pollPhonetic.Text ?? pollFilterResult.Text;
            LogRealtimeAsrReady(poll, pollPhonetic.Text ?? pollFinal, pollPhonetic.Engine ?? poll.SelectedEngine, pollPhonetic.DecisionReason ?? poll.DecisionReason, "native");
            TraceRealtimeTtsDecision(poll, pollFilterResult.Dropped, pollSpeakText,
                string.Equals(pollFinal, lastRealtimeTtsFinal, StringComparison.Ordinal), pollSuppress.ttsSuppressed);
            // Recognition-only clients must not create a TTS cooldown for the next session.
            if (realtimeTtsEnabled && !pollFilterResult.Dropped && HasSpeakableText(pollSpeakText) &&
                !string.Equals(pollFinal, lastRealtimeTtsFinal, StringComparison.Ordinal) &&
                !pollSuppress.ttsSuppressed)
            {
                lastRealtimeTtsFinal = pollFinal;
                ttsSuppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(TtsCooldownAfterSpeakMs);
                var sourceProsody = AnalyzeRealtimeSourceProsody(pollSpeakText, poll);
                TriggerRealtimeSpeak(pollSpeakText, pollFinal, effectiveLanguage, sourceProsody, poll.FinalDetectedAtMs);
            }
            return Results.Ok(new
            {
                state = poll.State,
                partial = poll.Partial,
                final = pollPhonetic.Text ?? poll.Final,
                selectedText = pollPhonetic.Text ?? poll.SelectedText,
                selected_text = pollPhonetic.Text ?? poll.SelectedText,
                selectedEngine = pollPhonetic.Engine ?? poll.SelectedEngine,
                selected_engine = pollPhonetic.Engine ?? poll.SelectedEngine,
                decisionReason = pollPhonetic.DecisionReason ?? poll.DecisionReason,
                decision_reason = pollPhonetic.DecisionReason ?? poll.DecisionReason,
                language = effectiveLanguage,
                resolvedScene = poll.ResolvedScene,
                confidenceAvailable = poll.ConfidenceAvailable,
                confidence_available = poll.ConfidenceAvailable,
                confidenceScore = poll.ConfidenceScore,
                confidence_score = poll.ConfidenceScore,
                confidenceUnavailableReason = poll.ConfidenceUnavailableReason,
                confidence_unavailable_reason = poll.ConfidenceUnavailableReason,
                latencyMs = poll.LatencyMs,
                finalDetectedAtMs = poll.FinalDetectedAtMs,
                finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
                finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
                error = poll.Error,
                captureSamples = poll.CaptureSamples,
                captureRms = poll.CaptureRms,
                captureAgcGain = poll.CaptureAgcGain,
                vadState = poll.VadState,
                vadSpeechCount = poll.VadSpeechCount,
                rawJson = poll.RawJson,
                loopbackSuspected = pollSuppress.loopbackSuspected,
                processedFinal = pollPhonetic.Text ?? pollFilterResult.Text,
                translatedFinal = (!string.IsNullOrEmpty(lastTranslatedFinalSource) && lastTranslatedFinalSource == pollFinal) ? lastTranslatedFinalText : (string?)null,
                textFilter = new
                {
                    dropped = pollFilterResult.Dropped,
                    laughReplaced = pollFilterResult.LaughReplaced,
                    ngMatched = pollFilterResult.NgMatched,
                    reason = pollFilterResult.Reason,
                }
            });
        }
        var filterResult = RealtimeTextFilter.Apply(selectedFinal, SnapshotTextFilterConfig(effectiveLanguage));
        var processedFinal = filterResult.Text;
        // 翻訳元キー（blank化前の認識 final を保持して translatedFinal の照合に使う）
        var selectedFinalKey = selectedFinal;
        // PhoneticAssist は新規 emit のときだけ評価（repeat poll での再発話を防ぐ）。
        bool svFreshEmit = !string.Equals(selectedFinal, lastRealtimeStatusFinalEmitted, StringComparison.Ordinal);
        (string? Text, string? Engine, string? DecisionReason) svPhonetic = svFreshEmit
            ? candidatePhonetic
            : (null, null, null);
        if (!svFreshEmit)
        {
            selectedFinal = string.Empty;
            processedFinal = string.Empty;
        }
        else
        {
            lastRealtimeStatusFinalEmitted = selectedFinal;
            if (svPhonetic.Text != null) processedFinal = svPhonetic.Text;
        }
        var svSpeakText = svPhonetic.Text ?? processedFinal;
        LogRealtimeAsrReady(poll, svPhonetic.Text ?? selectedFinal, svPhonetic.Engine ?? selectedEngine, svPhonetic.DecisionReason ?? selectedReason, selectedEngine);
        // Echo loop suppression: suppress ASR→TTS re-trigger during TTS playback + cooldown.
        // Set cooldown both at trigger time (handles instant-fail/fast TTS) and when TTS finishes.
        var suppressState = GetRealtimeTtsSuppressState(selectedFinal);
        var ttsSuppressed = suppressState.ttsSuppressed;
        TraceRealtimeTtsDecision(poll, filterResult.Dropped, svSpeakText,
            string.Equals(selectedFinal, lastRealtimeTtsFinal, StringComparison.Ordinal), ttsSuppressed);
        if (realtimeTtsEnabled && !filterResult.Dropped && HasSpeakableText(svSpeakText) &&
            !string.Equals(selectedFinal, lastRealtimeTtsFinal, StringComparison.Ordinal) &&
            !ttsSuppressed)
        {
            lastRealtimeTtsFinal = selectedFinal;
            // Set cooldown at trigger time — prevents re-loop even if TTS fails/completes instantly
            ttsSuppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(TtsCooldownAfterSpeakMs);
            var sourceProsody = AnalyzeRealtimeSourceProsody(svSpeakText, poll);
            TriggerRealtimeSpeak(svSpeakText, selectedFinalKey, effectiveLanguage, sourceProsody, poll.FinalDetectedAtMs);
        }
        // loopbackSuspected: during TTS suppress window, mark result as echo candidate.
        // app.js L782 checks this field to skip adding the result to history.
        var loopbackSuspected = suppressState.loopbackSuspected;
        return Results.Ok(new
        {
            state = poll.State,
            partial = poll.Partial,
            final = svPhonetic.Text ?? selectedFinal,
            selected_text = svPhonetic.Text ?? selectedFinal,
            selected_engine = svPhonetic.Engine ?? selectedEngine,
            decision_reason = svPhonetic.DecisionReason ?? selectedReason,
            language = effectiveLanguage,
            resolvedScene = poll.ResolvedScene,
            confidence_available = false,
            confidence_score = (double?)null,
            confidence_unavailable_reason = "sense_voice_confidence_unavailable",
            candidates = new[] { new { engine = svPhonetic.Engine ?? selectedEngine, text = svPhonetic.Text ?? selectedFinal, confidence_available = false, confidence_score = (double?)null } },
            latencyMs = poll.LatencyMs,
            finalDetectedAtMs = poll.FinalDetectedAtMs,
            finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
            finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
            captureSamples = poll.CaptureSamples,
            captureRms = poll.CaptureRms,
            captureAgcGain = poll.CaptureAgcGain,
            vadState = poll.VadState,
            vadSpeechCount = poll.VadSpeechCount,
            engineLoaded = true,
            error = poll.Error,
            loopbackSuspected,
            processedFinal,
            translatedFinal = (!string.IsNullOrEmpty(lastTranslatedFinalSource) && lastTranslatedFinalSource == selectedFinalKey) ? lastTranslatedFinalText : (string?)null,
            textFilter = new
            {
                dropped = filterResult.Dropped,
                laughReplaced = filterResult.LaughReplaced,
                ngMatched = filterResult.NgMatched,
                reason = filterResult.Reason,
            }
        });
        }, () => Results.Ok(new { state = "idle", discarded = true }));
    }
    catch (Exception ex) { return Results.Ok(new { state = "error", error = ex.Message }); }
    }
    finally { realtimeMutationGate.Release(); }
});

app.MapPost("/api/realtime/destroy", () =>
{
    realtimeOutputEpoch.Invalidate();
    realtimeMutationGate.Wait();
    try
    {
    realtimeSessionService.Dispose();
    realtimeSession = null;
    realtimeWorkLease?.Dispose();
    realtimeWorkLease = null;
    lastRealtimeTtsFinal = string.Empty;
    lastRealtimeStatusFinalEmitted = string.Empty;
    return Results.Ok(ApiOk());
    }
    finally { realtimeMutationGate.Release(); }
});

// ---- Shutdown ----
app.Lifetime.ApplicationStopping.Register(() =>
{
    linuxMonitorLoopback.RequestStop();
    linuxMonitorStopping.Cancel();
    realtimeSessionService.Dispose();
    realtimeSession = null;
    realtimeWorkLease?.Dispose();
    realtimeWorkLease = null;
    ttsRouter.Dispose();
    primaryTts.Dispose();
    linuxMonitorFallbackReason = linuxMonitorLoopback.StopWithinAsync(linuxMonitorGate, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    if (linuxMonitorFallbackReason != null)
        logger.LogWarning("Linux monitor shutdown incomplete: {Reason}", linuxMonitorFallbackReason);
    logger.LogInformation("MobMate Linux stopped.");
});

void TryOpenWebUiInBrowser(string url)
{
    var disabled = string.Equals(Environment.GetEnvironmentVariable("MOBMATE_NO_BROWSER"), "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Environment.GetEnvironmentVariable("MOBMATE_OPEN_BROWSER"), "0", StringComparison.OrdinalIgnoreCase);

    if (disabled)
    {
        logger.LogInformation("Browser auto-open disabled by environment. Open {Url} manually.", url);
        return;
    }

    try
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            runtimeDir = $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";
        }

        var psi = new System.Diagnostics.ProcessStartInfo("xdg-open", url)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtimeDir}/bus";
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && File.Exists(Path.Combine(runtimeDir, "wayland-0")))
        {
            psi.Environment["WAYLAND_DISPLAY"] = "wayland-0";
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) && Directory.Exists("/tmp/.X11-unix") && File.Exists("/tmp/.X11-unix/X0"))
        {
            psi.Environment["DISPLAY"] = ":0";
        }
        System.Diagnostics.Process.Start(psi)?.Dispose();
        logger.LogInformation("Browser auto-open requested: {Url}", url);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Browser auto-open failed. Open {Url} manually.", url);
    }
}

void TryOpenUrlInBrowser(string url, string label)
{
    try
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            runtimeDir = $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";
        }

        var psi = new System.Diagnostics.ProcessStartInfo("xdg-open", url)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtimeDir}/bus";
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && File.Exists(Path.Combine(runtimeDir, "wayland-0")))
        {
            psi.Environment["WAYLAND_DISPLAY"] = "wayland-0";
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) && Directory.Exists("/tmp/.X11-unix") && File.Exists("/tmp/.X11-unix/X0"))
        {
            psi.Environment["DISPLAY"] = ":0";
        }
        System.Diagnostics.Process.Start(psi)?.Dispose();
        logger.LogInformation("{Label} open requested: {Url}", label, url);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "{Label} open failed. Open {Url} manually.", label, url);
    }
}

void TryOpenSubtitleWindow(string url)
{
    try
    {
        // Prevent duplicate windows
        if (_subtitleProcess is { HasExited: false })
        {
            logger.LogInformation("Subtitle window already running (pid={Pid}).", _subtitleProcess.Id);
            return;
        }
        _subtitleProcess = null;

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "open-subtitle-window.py");
        if (!File.Exists(scriptPath))
        {
            logger.LogWarning("Subtitle window script not found: {Path}. Falling back to xdg-open.", scriptPath);
            TryOpenUrlInBrowser(url, "Subtitle window");
            return;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";

        var psi = new System.Diagnostics.ProcessStartInfo(RadioChatService.ResolveHostCommand("python3"), $"\"{scriptPath}\" \"{url}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        RadioChatService.SanitizeSteamRuntimeEnvironment(psi.Environment);
        psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;

        // open-subtitle-window.py forces GDK_BACKEND=x11 (XWayland) so decoration removal,
        // RGBA transparency and click-through input shapes work. Under XWayland the X server
        // requires a valid XAUTHORITY; when launched from a systemd user service the parent
        // process env lacks it, so the window dies with "Authorization required". Pull the
        // live session env (DISPLAY/XAUTHORITY/WAYLAND_DISPLAY/DBUS) from systemd --user.
        var sessionEnv = ReadSystemdUserEnvironmentForRadio();
        string? PickEnv(string key) =>
            (sessionEnv.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                ? v
                : (Environment.GetEnvironmentVariable(key) is { Length: > 0 } p ? p : null);

        var dbus = PickEnv("DBUS_SESSION_BUS_ADDRESS") ?? $"unix:path={runtimeDir}/bus";
        psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = dbus;

        var xauthority = PickEnv("XAUTHORITY");
        if (!string.IsNullOrWhiteSpace(xauthority))
            psi.Environment["XAUTHORITY"] = xauthority;

        var display = PickEnv("DISPLAY") ?? (File.Exists("/tmp/.X11-unix/X0") ? ":0" : null);
        if (!string.IsNullOrWhiteSpace(display))
            psi.Environment["DISPLAY"] = display;

        // Keep WAYLAND_DISPLAY available as a fallback backend for the python script.
        var waylandDisplay = PickEnv("WAYLAND_DISPLAY")
            ?? (File.Exists(Path.Combine(runtimeDir, "wayland-0")) ? "wayland-0" : null);
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            psi.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

        // Force X11 backend: open-subtitle-window.py requires GDK_BACKEND=x11 for
        // set_decorated(False), RGBA transparency and click-through to work under XWayland.
        // The parent process (systemd service) may have GDK_BACKEND=wayland in its env;
        // always override explicitly so python3's os.environ.setdefault() cannot be bypassed.
        psi.Environment["GDK_BACKEND"] = "x11";

        if (RadioChatService.TryStartHostUserCommand("mobmate-subtitle-window", "/usr/bin/python3", new[] { scriptPath, url }, psi.Environment, logger))
        {
            logger.LogInformation("Subtitle window opened via host user service: {Url}", url);
            return;
        }

        psi.Environment.TryGetValue("PATH", out var dbgPath);
        psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var dbgLd);
        psi.Environment.TryGetValue("WEBKIT_EXEC_PATH", out var dbgWebkit);
        psi.Environment.TryGetValue("LD_PRELOAD", out var dbgPreload);
        RadioChatService.AppendHelperDebug($"subtitle env exe={psi.FileName} runhost={Directory.Exists("/run/host")} PATH={dbgPath} LD_LIBRARY_PATH={dbgLd} WEBKIT_EXEC_PATH={dbgWebkit} LD_PRELOAD={dbgPreload}");

        _subtitleProcess = System.Diagnostics.Process.Start(psi);
        if (_subtitleProcess is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var output = await _subtitleProcess.StandardOutput.ReadToEndAsync();
                    var error = await _subtitleProcess.StandardError.ReadToEndAsync();
                    await _subtitleProcess.WaitForExitAsync();
                    if (!string.IsNullOrWhiteSpace(output) || !string.IsNullOrWhiteSpace(error))
                        RadioChatService.AppendHelperDebug($"subtitle direct exit={_subtitleProcess.ExitCode} out={output.Trim()} err={error.Trim()}");
                }
                catch { }
            });
        }
        logger.LogInformation("Subtitle window opened: {Url} (pid={Pid})", url, _subtitleProcess?.Id);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Subtitle window open failed. Open {Url} manually.", url);
    }
}

const int RuntimeSchemaVersion = 1;
var runtimePath = Path.Combine(AppUserPaths.ConfigDir, "webhost-runtime.json");
var runtimePid = Environment.ProcessId;
var startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

void WriteRuntimeFile(int port)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var tempPath = $"{runtimePath}.{runtimePid}.tmp";
    var json = JsonSerializer.Serialize(new
    {
        schemaVersion = RuntimeSchemaVersion,
        pid = runtimePid,
        port,
        startedUtc = startedUtc.ToString("O"),
    });
    File.WriteAllText(tempPath, json, new UTF8Encoding(false));
    File.Move(tempPath, runtimePath, overwrite: true);
}

void RemoveOwnedRuntimeFile()
{
    try
    {
        if (!File.Exists(runtimePath)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(runtimePath, Encoding.UTF8));
        if (document.RootElement.TryGetProperty("pid", out var pid) && pid.GetInt32() == runtimePid)
            File.Delete(runtimePath);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[Runtime] Failed to remove owned runtime file: {Path}", runtimePath);
    }
}

app.Urls.Add($"http://127.0.0.1:{SelectStableLoopbackPort()}");
try
{
    await app.StartAsync();
    var selectedUrl = app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()?.Addresses
        .Select(address => new Uri(address))
        .Single(uri => uri.IsLoopback && uri.Port > 0)
        ?? throw new InvalidOperationException("Kestrel did not report its selected loopback URL.");
    selectedBaseUrl = selectedUrl.GetLeftPart(UriPartial.Authority) + "/";
    WriteRuntimeFile(selectedUrl.Port);
    logger.LogInformation("[Runtime] selectedUrl={SelectedUrl} runtimeFile={RuntimeFile}", selectedBaseUrl, runtimePath);

    await RestoreVirtualMicOnStartupAsync();
    logger.LogInformation("Open {Url} in a browser to configure.", selectedBaseUrl);
    UnlockSteamAchievement(SteamAchievementService.ThanksForLaunching);
    steamAchievements.StartUsageTimer();
    _ = Task.Run(() => TryOpenWebUiInBrowser(selectedBaseUrl));
    await app.WaitForShutdownAsync();
}
catch (OperationCanceledException)
{
}
finally
{
    RemoveOwnedRuntimeFile();
    await app.DisposeAsync();
}

static int SelectStableLoopbackPort()
{
    const int preferredPort = 5000;
    const int fallbackCount = 100;
    for (var port = preferredPort; port < preferredPort + fallbackCount; port++)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return port;
        }
        catch (SocketException)
        {
        }
    }

    return 0;
}

public sealed class LinuxHearingConfig
{
    public string Engine { get; set; } = "joint_auto";
    public string Lang { get; set; } = "auto";
    public string TranslateTo { get; set; } = "OFF";
    public int ChunkMergeMs { get; set; } = 2000;
    public string AudioMode { get; set; } = "loopback";
    public string MicDeviceHint { get; set; } = "";
    public int OverlayFontSize { get; set; } = 36;
    public string OverlayBackground { get; set; } = "blue";
    public int OverlayOpacity { get; set; } = 72;
    public int OverlayHistory { get; set; } = 6;
    public int OverlayDoneKeepSeconds { get; set; } = 12;
    public string OverlayPosition { get; set; } = "custom";
    public string OverlayFlow { get; set; } = "horizontalRight";
}

public sealed class LinuxHearingStatus
{
    public bool Running { get; set; }
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string SelectedEngine { get; set; } = "";
    public long UpdatedAtMs { get; set; }
    public int HistoryCount { get; set; }
    public string? Error { get; set; }
}

public sealed class LinuxHearingHistoryItem
{
    public long AtMs { get; set; }
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string SelectedEngine { get; set; } = "";
}
