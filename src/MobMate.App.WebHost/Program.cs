using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MobMate.Core;
using MobMate.Core.Asr;
using MobMate.Core.Bench;
using MobMate.Core.Native;
using MobMate.Core.Realtime;
using MobMate.Core.Text;
using MobMate.Core.Tts;
using MobMate.Core.VoiceConversion;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

// Must precede all normal initialization, including environment fallbacks.
_ = SteamLaunchContext.AllowPersistentConnection;
if (NativeVulkanSteamBackend.TryHandleProbe(args, out var probeExitCode))
{
    Environment.ExitCode = probeExitCode;
    return;
}

// ggml v1.8.5+ は静的初期化で std::set_terminate(ggml_uncaught_exception) を登録する。
// このハンドラは .NET CLR の例外処理と衝突し、abort() (FAST_FAIL_FATAL_APP_EXIT) で
// プロセスを即時終了させる。GGML_NO_BACKTRACE を立てるとハンドラ登録をスキップする。
// 注意: .NET の Environment.SetEnvironmentVariable は Win32 環境ブロックのみを更新し、
// ggml の getenv() が読む ucrtbase の _environ キャッシュとは同期しない。そのため
// ggml.dll がロードされる前に、ucrtbase の _putenv_s で CRT 側環境を直接更新する。
GgmlEnvFix.DisableBacktrace();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

/* Use executable directory as base for package-relative paths. */
var appRoot = AppContext.BaseDirectory;

AppLogging.Initialize(appRoot, MobMateCore.BuildChannel);
using var loggerFactory = AppLogging.CreateLoggerFactory(MobMateCore.BuildChannel);
var logger = loggerFactory.CreateLogger("MobMate.App.WebHost");
var realtimeOutputEpoch = new RealtimeOutputEpoch();
app.Lifetime.ApplicationStopping.Register(realtimeOutputEpoch.Dispose);
var steamAchievements = new SteamAchievementService(appRoot, loggerFactory.CreateLogger("SteamAchievement"));
void UnlockSteamAchievement(string achievementId) => _ = Task.Run(() => steamAchievements.Unlock(achievementId));
app.Lifetime.ApplicationStopping.Register(() => steamAchievements.Dispose());
var adaptiveVocabLogger = loggerFactory.CreateLogger("AdaptiveVocab");
var adaptiveVocabOptions = AppConfigLoader.Load(Path.Combine(appRoot, "mobmate.config.json")).AdaptiveVocab ?? new AdaptiveVocabOptions();
var adaptiveVocabStore = new AdaptiveVocabStore(Path.Combine(AppContext.BaseDirectory, "app", "adaptive_vocab.json"), adaptiveVocabOptions);
var adaptivePromptBuilder = new AdaptivePromptBuilder(adaptiveVocabStore, adaptiveVocabOptions);
logger.LogInformation("Starting MobMate WebHost (channel={Channel})", MobMateCore.BuildChannel);
logger.LogInformation("[Environment] appVersion={AppVersion} osDescription={OsDescription} osVersion={OsVersion} framework={Framework} osArch={OsArch} processArch={ProcessArch} cpuCount={CpuCount} process64Bit={Process64Bit}",
    MobMateCore.Version, RuntimeInformation.OSDescription, Environment.OSVersion.VersionString,
    RuntimeInformation.FrameworkDescription, RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture,
    Environment.ProcessorCount, Environment.Is64BitProcess);
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    logger.LogCritical(eventArgs.ExceptionObject as Exception, "[Process] unhandled exception terminating={Terminating}", eventArgs.IsTerminating);
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
    logger.LogError(eventArgs.Exception, "[Process] unobserved task exception");
AppDomain.CurrentDomain.ProcessExit += (_, _) => logger.LogInformation("[Process] ProcessExit received exitCode={ExitCode}", Environment.ExitCode);
app.Lifetime.ApplicationStopping.Register(() => logger.LogInformation("[Process] ApplicationStopping received"));

MobMate.Core.Asr.SenseVoiceAsrService? senseVoiceService = null;
ILogger? senseVoiceLogger = null;
PhonemeCtcService? phonemeCtcService = null;
ILogger? phonemeCtcLogger = null;

// Translation サービス
var translationService = new TranslationService();
var translationLogger = loggerFactory.CreateLogger("Translation");
string? lastTranslatedFinalText   = null;  // 直近の翻訳結果
string? lastTranslatedFinalSource = null;  // 翻訳元 effectiveFinal
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

string HearingConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "hearing.json");
HearingConfig LoadHearingConfig()
{
    var path = HearingConfigPath();
    if (!File.Exists(path)) return new HearingConfig();
    try
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<HearingConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new HearingConfig();
    }
    catch { return new HearingConfig(); }
}
void SaveHearingConfig(HearingConfig cfg)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(HearingConfigPath(), json, Encoding.UTF8);
}
// === VRChat OSC (chatbox) ===
var vrchatOscLogger = loggerFactory.CreateLogger("VrchatOsc");
var vrchatOscService = new VrchatOscService(vrchatOscLogger);
string VrchatOscConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "vrchat_osc.json");
VrchatOscConfig LoadVrchatOscConfig()
{
    var path = VrchatOscConfigPath();
    if (!File.Exists(path)) return new VrchatOscConfig();
    try
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<VrchatOscConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new VrchatOscConfig();
    }
    catch { return new VrchatOscConfig(); }
}
void SaveVrchatOscConfig(VrchatOscConfig cfg)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(VrchatOscConfigPath(), json, Encoding.UTF8);
}
vrchatOscService.SetConfig(LoadVrchatOscConfig());
app.Lifetime.ApplicationStopping.Register(() => vrchatOscService.Dispose());

// 起動時に非同期でモデルをロード（重いので Start で遅延実行）
_ = Task.Run(() => translationService.Load(appRoot, translationLogger));

AppUserPaths.EnsureV2Directories();
logger.LogInformation("[Startup] steam cloud v2 root: {Root}", AppUserPaths.V2Root);
logger.LogInformation("[Startup] steam cloud v2 config: {Config}", AppUserPaths.ConfigDir);
logger.LogInformation("[Startup] steam cloud v2 profiles: {Profiles}", AppUserPaths.ProfilesDir);
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
var initialUiLanguage = AppPresetBootstrap.ResolveInitialLanguage();
var ignoreBootstrap = AppPresetBootstrap.EnsureRuntimeIgnoreFile(appRoot, AppUserPaths.ConfigDir, initialUiLanguage);
logger.LogInformation("[Startup] ignore bootstrap: action={Action} lang={Lang} target={Target} source={Source}",
    ignoreBootstrap.Action, ignoreBootstrap.Language, ignoreBootstrap.TargetPath, ignoreBootstrap.SourcePath ?? "-");

/* Log startup diagnostics: runtime/model detection */
var startupConfig = LoadAppConfig();
AppConfigLoader.LogStartupDiagnostics(startupConfig, appRoot, logger);
TryStartConfiguredVoicevox(startupConfig, appRoot, logger);

// sherpa-onnx native DLL 隔離ロード (libs/sherpa-onnx/ に配置)
{
    var sherpaDir = Path.Combine(appRoot, "libs", "sherpa-onnx");
    if (Directory.Exists(sherpaDir))
    {
        try
        {
            NativeLibrary.Load(Path.Combine(sherpaDir, "onnxruntime.dll"));
            var sherpaHandle = NativeLibrary.Load(Path.Combine(sherpaDir, "sherpa-onnx-c-api.dll"));
            NativeLibrary.SetDllImportResolver(
                typeof(SherpaOnnx.OfflineRecognizer).Assembly,
                (name, _, _) => name == "sherpa-onnx-c-api" ? sherpaHandle : IntPtr.Zero);
            logger.LogInformation("[SenseVoice] DLL ロード完了: {Dir}", sherpaDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SenseVoice] DLL ロード失敗");
        }
    }
    else
    {
        logger.LogWarning("[SenseVoice] libs/sherpa-onnx が存在しません: {Dir}", sherpaDir);
    }
}

static string ResolveNativeCommandPath(string nativePath)
{
    if (!nativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return nativePath;
    return Path.Combine(Path.GetDirectoryName(nativePath) ?? ".", "mobmate-native.exe");
}

static object ApiMessage(string code, string message) => new { code, message };

static Dictionary<string, object?> ApiProblemCode(string code) => new() { ["code"] = code };

static void QuarantineStaleV1RootDlls(string appRoot, ILogger logger)
{
    string[] forbiddenDllNames =
    [
        "ggml-base.dll",
        "ggml-cpu.dll",
        "ggml-vulkan.dll",
        "ggml.dll",
        "whisper.dll",
        "mobmate_native_bridge.dll",
    ];

    var quarantineDir = Path.Combine(appRoot, "_stale_v1_dlls", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    foreach (var dllName in forbiddenDllNames)
    {
        var path = Path.Combine(appRoot, dllName);
        if (!File.Exists(path)) continue;

        try
        {
            var info = new FileInfo(path);
            Directory.CreateDirectory(quarantineDir);
            var target = Path.Combine(quarantineDir, dllName);
            File.Move(path, target);
            logger.LogWarning("[Startup] Quarantined stale V1 root DLL: path={Path} size={Size} modified={Modified} quarantine={Quarantine}",
                info.FullName, info.Length, info.LastWriteTime, target);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Startup] Failed to quarantine stale V1 root DLL: {Path}. Startup will continue, but native DLL loading may crash.", path);
        }
    }

    var remaining = forbiddenDllNames
        .Select(name => Path.Combine(appRoot, name))
        .Where(File.Exists)
        .ToArray();
    if (remaining.Length > 0)
    {
        logger.LogWarning("[Startup] Forbidden root-level V1 DLLs are still present after cleanup: {Paths}", string.Join(", ", remaining));
    }
}

const string RealtimeEngineWhisper = "whisper";
const string RealtimeEngineMoonshine = "moonshine";
const string RealtimeEngineJointAuto = "joint_auto";
const string RealtimeEngineSafeAuto = "safe_auto";
const string RealtimeEngineSenseVoice = "sense_voice";
const string RealtimeEngineParaformerZh = "paraformer_zh";
const string RealtimeEngineHaas = AddonCatalog.HaasId;
const string RealtimeEnginePhoneme = "phoneme";
const string RealtimeSceneAuto = "auto";
const string RealtimeSceneShortCommand = "short_command";
const string RealtimePrefilterAuto = "auto";
const string RealtimePrefilterOff = "off";
const string TtsEngineAuto = "auto";
const string TtsEngineWindowsSapi = "windows_sapi";
const string TtsEngineVoicevox = "voicevox";
const string TtsEnginePiper = "piper";
const string TtsEnginePiperPlus = "piper_plus";
const string TtsEngineKokoro = "kokoro";
const string TtsEngineMoss = "moss";
// MOSS voice-clone/prosody reference from live ASR is still unstable; keep off for v2.1.0.
const bool EnableMossAsrReferenceAudio = false;
const string VirtualAudioOfficialUrl = "https://vb-audio.com/Cable/";
const string DefaultVoicevoxApi = "http://127.0.0.1:50021";
const string VoicevoxInstallVersion = "0.25.2";
const string VoicevoxArchiveName = "voicevox-windows-directml-0.25.2.zip";
const string VoicevoxDownloadUrl = "https://github.com/VOICEVOX/voicevox/releases/download/0.25.2/voicevox-windows-directml-0.25.2.zip";
const string DefaultVcClientApi = "http://127.0.0.1:18000";
const string VcClientInstallVersion = "2.1.4-alpha";
const string VcClientArchiveName = "vcclient_win_dml_2.1.4-alpha.zip";
const string VcClientDownloadUrl = "https://huggingface.co/wok000/vcclient000/resolve/main/vcclient_win_dml_2.1.4-alpha.zip";
const string VcClientOfficialUrl = "https://github.com/w-okada/voice-changer";

string[] RealtimeEngineValues = [RealtimeEngineWhisper, RealtimeEngineMoonshine, RealtimeEngineJointAuto, RealtimeEngineSafeAuto, RealtimeEngineSenseVoice, RealtimeEngineParaformerZh];
string[] RealtimeSceneValues = [RealtimeSceneAuto, RealtimeSceneShortCommand, "talk_clean", "talk_noisy", "talk_low_voice", "desktop_hearing"];
string[] RealtimePrefilterValues = [RealtimePrefilterAuto, "normal", "strong", RealtimePrefilterOff];
string[] TtsEngineValues = [TtsEngineAuto, TtsEngineWindowsSapi, TtsEngineVoicevox, TtsEnginePiper, TtsEnginePiperPlus, TtsEngineKokoro, TtsEngineMoss];

bool IsLiteralValue(string value, IEnumerable<string> allowed) => allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
int PreferredLanguageOrder(string code) => code switch { "ja" => 0, "en" => 1, "zh" => 2, "ko" => 3, _ => 10 };

string ResolveConfigPath(string path) => Path.IsPathFullyQualified(path) ? path : Path.Combine(appRoot, path);

string CloudAppConfigPath() => Path.Combine(AppUserPaths.ConfigDir, "mobmate.config.json");
string PackageAppConfigPath() => Path.Combine(appRoot, "mobmate.config.json");
string RealtimeStartPendingPath() => Path.Combine(AppUserPaths.ConfigDir, "realtime_start.pending");
bool realtimeStartRecoveryRequired = File.Exists(RealtimeStartPendingPath());
void MarkRealtimeStartPending(string engine, string clientSource)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    File.WriteAllText(RealtimeStartPendingPath(), $"engine={engine}\nsource={clientSource}\nutc={DateTimeOffset.UtcNow:O}\n", Encoding.UTF8);
    realtimeStartRecoveryRequired = true;
    logger.LogWarning("[Realtime] start pending marker created engine={Engine} source={Source}", engine, clientSource);
}
void ClearRealtimeStartPending()
{
    try { File.Delete(RealtimeStartPendingPath()); }
    catch (Exception ex) { logger.LogWarning(ex, "[Realtime] failed to remove start pending marker"); return; }
    realtimeStartRecoveryRequired = false;
}
string voicevoxInstallDir = Path.Combine(AppUserPaths.ConfigDir, "voicevox");
string voicevoxArchivePath = Path.Combine(voicevoxInstallDir, VoicevoxArchiveName);
string voicevoxExtractDir = Path.Combine(voicevoxInstallDir, "engine-" + VoicevoxInstallVersion);
var voicevoxDownloadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
string vcclientInstallDir = Path.Combine(AppUserPaths.ConfigDir, "vcclient");
string vcclientArchivePath = Path.Combine(vcclientInstallDir, VcClientArchiveName);
string vcclientExtractDir = Path.Combine(vcclientInstallDir, "engine-" + VcClientInstallVersion);
var vcclientDownloadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
object vcclientInstallLock = new object();
bool vcclientInstallRunning = false;
long vcclientInstallBytesReceived = 0;
long? vcclientInstallTotalBytes = null;
string? vcclientInstallError = null;
string vcclientInstallPhase = "idle";
DateTimeOffset? vcclientInstallStartedAt = null;
DateTimeOffset? vcclientInstallFinishedAt = null;
object voicevoxInstallLock = new object();
bool voicevoxInstallRunning = false;
long voicevoxInstallBytesReceived = 0;
long? voicevoxInstallTotalBytes = null;
string? voicevoxInstallError = null;
string voicevoxInstallPhase = "idle";
DateTimeOffset? voicevoxInstallStartedAt = null;
DateTimeOffset? voicevoxInstallFinishedAt = null;
app.Lifetime.ApplicationStopping.Register(() => voicevoxDownloadHttp.Dispose());
app.Lifetime.ApplicationStopping.Register(() => vcclientDownloadHttp.Dispose());

AppConfig LoadAppConfig()
{
    var config = File.Exists(PackageAppConfigPath())
        ? AppConfigLoader.Load(PackageAppConfigPath())
        : AppConfigLoader.Load();

    if (File.Exists(CloudAppConfigPath()))
    {
        var userConfig = AppConfigLoader.Load(CloudAppConfigPath());
        config.VoicevoxExePath = userConfig.VoicevoxExePath;
        config.AdaptiveVocab = userConfig.AdaptiveVocab ?? config.AdaptiveVocab;
        if (userConfig.ExtensionData is { Count: > 0 })
        {
            foreach (var (key, value) in userConfig.ExtensionData)
            {
                config.ExtensionData[key] = value.Clone();
            }
        }
    }

    return config;
}

void SaveAppConfig(AppConfig config)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var path = CloudAppConfigPath();
    var userConfig = File.Exists(path) ? AppConfigLoader.Load(path) : new AppConfig();
    userConfig.VoicevoxExePath = config.VoicevoxExePath;
    userConfig.AdaptiveVocab = config.AdaptiveVocab;
    var json = JsonSerializer.Serialize(userConfig, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json, Encoding.UTF8);
}

static string VoicevoxDialogTitle(string? lang, string? title)
{
    if (!string.IsNullOrWhiteSpace(title) && title.Length <= 100) return title;
    return (lang ?? string.Empty).Trim().ToLowerInvariant() switch
{
    "ja" => "VOICEVOX.exe を選択",
    "ko" => "VOICEVOX.exe 선택",
    "zh_cn" => "选择 VOICEVOX.exe",
    "zh_tw" => "選擇 VOICEVOX.exe",
    _ => "Select VOICEVOX.exe",
};
}

static (string? Path, int ExitCode, string? Error) ShowVoicevoxFileDialog(string title)
{
    var safeTitle = title.Replace("'", "''");
    var script = $@"
Add-Type -AssemblyName System.Windows.Forms
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = '{safeTitle}'
$dialog.Filter = 'VOICEVOX.exe|VOICEVOX.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*'
$dialog.CheckFileExists = $true
$dialog.Multiselect = $false
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{
  [Console]::WriteLine($dialog.FileName)
  exit 0
}}
exit 2
";
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    foreach (var shell in new[] { "pwsh", "powershell.exe" })
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
                ArgumentList = { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            });
            if (process == null) continue;
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            return (string.IsNullOrWhiteSpace(output) ? null : output, process.ExitCode, string.IsNullOrWhiteSpace(error) ? null : error);
        }
        catch (Exception ex)
        {
            if (shell.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) return (null, -1, ex.Message);
        }
    }
    return (null, -1, "PowerShell is not available");
}

static bool IsVoicevoxApiAvailable(string apiUrl, int timeoutMs = 700)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        var response = client.GetAsync($"{apiUrl.TrimEnd('/')}/speakers").GetAwaiter().GetResult();
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static void TryStartConfiguredVoicevox(AppConfig config, string appRoot, ILogger logger)
{
    if (string.IsNullOrWhiteSpace(config.VoicevoxExePath)) return;
    var exePath = Path.IsPathFullyQualified(config.VoicevoxExePath)
        ? config.VoicevoxExePath
        : Path.Combine(appRoot, config.VoicevoxExePath);
    if (!File.Exists(exePath))
    {
        logger.LogWarning("[Startup] VOICEVOX exe missing: {Path}", config.VoicevoxExePath);
        return;
    }
    if (IsVoicevoxApiAvailable(DefaultVoicevoxApi))
    {
        logger.LogInformation("[Startup] VOICEVOX API already available; skip auto start.");
        return;
    }
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? appRoot,
            UseShellExecute = true,
        });
        logger.LogInformation("[Startup] VOICEVOX auto start requested: {Path}", config.VoicevoxExePath);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Startup] VOICEVOX auto start failed");
    }
}

string? FindInstalledVoicevoxExe()
{
    if (!Directory.Exists(voicevoxExtractDir)) return null;
    return Directory.EnumerateFiles(voicevoxExtractDir, "VOICEVOX.exe", SearchOption.AllDirectories).FirstOrDefault();
}

static bool IsVcClientApiAvailable(string apiUrl, int timeoutMs = 700)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        var response = client.GetAsync($"{apiUrl.TrimEnd('/')}/api/slot-manager/slots").GetAwaiter().GetResult();
        return response.IsSuccessStatusCode;
    }
    catch { return false; }
}

string? FindInstalledVcClientLauncher()
{
    if (!Directory.Exists(vcclientExtractDir)) return null;
    var files = Directory.EnumerateFiles(vcclientExtractDir, "*.*", SearchOption.AllDirectories)
        .Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    string? Pick(string name) => files.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    return Pick("VCClient.exe")
        ?? Pick("vcclient.exe")
        ?? Pick("MMVCServerSIO.exe")
        ?? Pick("start_http.bat")
        ?? Pick("start.bat")
        ?? files.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Contains("vcclient", StringComparison.OrdinalIgnoreCase));
}

void RepairVcClientDirectMlLayout()
{
    var internalDir = Path.Combine(vcclientExtractDir, "dist", "main", "_internal");
    var source = Path.Combine(internalDir, "onnxruntime", "capi", "DirectML.dll");
    var destination = Path.Combine(internalDir, "torch_directml", "DirectML.dll");
    if (File.Exists(destination) || !File.Exists(source)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? internalDir);
    File.Copy(source, destination, overwrite: true);
}

object VcClientInstallStateResponse()
{
    lock (vcclientInstallLock)
    {
        var launcher = FindInstalledVcClientLauncher();
        var engineRunning = IsVcClientApiAvailable(DefaultVcClientApi);
        var progress = vcclientInstallTotalBytes.HasValue && vcclientInstallTotalBytes.Value > 0
            ? Math.Clamp((double)vcclientInstallBytesReceived / vcclientInstallTotalBytes.Value, 0.0, 1.0)
            : 0.0;
        return new
        {
            ok = true,
            supported = true,
            installed = File.Exists(vcclientArchivePath),
            extracted = !string.IsNullOrWhiteSpace(launcher),
            engineRunning,
            phase = vcclientInstallPhase,
            running = vcclientInstallRunning,
            version = VcClientInstallVersion,
            archivePath = vcclientArchivePath,
            runPath = launcher ?? string.Empty,
            downloadUrl = VcClientDownloadUrl,
            officialUrl = VcClientOfficialUrl,
            bytesReceived = vcclientInstallBytesReceived,
            totalBytes = vcclientInstallTotalBytes,
            progress = Math.Round(progress, 4),
            error = engineRunning ? null : vcclientInstallError,
            startedAt = vcclientInstallStartedAt?.ToString("O"),
            finishedAt = vcclientInstallFinishedAt?.ToString("O")
        };
    }
}

async Task DownloadVcClientArchiveAsync(CancellationToken ct)
{
    Directory.CreateDirectory(vcclientInstallDir);
    var tmpPath = vcclientArchivePath + ".download";
    try
    {
        lock (vcclientInstallLock) vcclientInstallPhase = "download";
        using var resp = await vcclientDownloadHttp.GetAsync(VcClientDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        lock (vcclientInstallLock)
        {
            vcclientInstallTotalBytes = resp.Content.Headers.ContentLength;
            vcclientInstallBytesReceived = 0;
        }
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(tmpPath);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            lock (vcclientInstallLock) vcclientInstallBytesReceived += read;
        }
        output.Close();
        if (File.Exists(vcclientArchivePath)) File.Delete(vcclientArchivePath);
        File.Move(tmpPath, vcclientArchivePath);
        lock (vcclientInstallLock) vcclientInstallError = null;
        await ExtractAndStartVcClientAsync(ct);
    }
    catch (Exception ex)
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        lock (vcclientInstallLock) vcclientInstallError = ex.Message;
    }
    finally
    {
        lock (vcclientInstallLock)
        {
            vcclientInstallPhase = "idle";
            vcclientInstallRunning = false;
            vcclientInstallFinishedAt = DateTimeOffset.UtcNow;
        }
    }
}

async Task ExtractAndStartVcClientAsync(CancellationToken ct)
{
    try
    {
        lock (vcclientInstallLock) vcclientInstallPhase = "extract";
        var launcher = FindInstalledVcClientLauncher();
        if (string.IsNullOrWhiteSpace(launcher))
        {
            if (!File.Exists(vcclientArchivePath)) throw new FileNotFoundException("VCClient archive not found", vcclientArchivePath);
            if (Directory.Exists(vcclientExtractDir)) Directory.Delete(vcclientExtractDir, recursive: true);
            Directory.CreateDirectory(vcclientExtractDir);
            ZipFile.ExtractToDirectory(vcclientArchivePath, vcclientExtractDir);
            launcher = FindInstalledVcClientLauncher();
        }
        if (string.IsNullOrWhiteSpace(launcher)) throw new FileNotFoundException("VCClient launcher not found after extraction", vcclientExtractDir);
        RepairVcClientDirectMlLayout();

        lock (vcclientInstallLock) vcclientInstallPhase = "start";
        if (!IsVcClientApiAvailable(DefaultVcClientApi))
        {
            Process.Start(new ProcessStartInfo { FileName = launcher, WorkingDirectory = Path.GetDirectoryName(launcher) ?? vcclientExtractDir, UseShellExecute = true });
            await Task.Delay(2500, ct);
        }
        vcclientInstallError = IsVcClientApiAvailable(DefaultVcClientApi, 2000) ? null : "VCClient API is not available yet. Start VCClient and set API port to 18000 if needed.";
    }
    catch (Exception ex)
    {
        lock (vcclientInstallLock) vcclientInstallError = ex.Message;
    }
    finally
    {
        lock (vcclientInstallLock)
        {
            vcclientInstallPhase = "idle";
            vcclientInstallRunning = false;
            vcclientInstallFinishedAt = DateTimeOffset.UtcNow;
        }
    }
}

object VoicevoxInstallStateResponse()
{
    lock (voicevoxInstallLock)
    {
        var exePath = FindInstalledVoicevoxExe();
        var progress = voicevoxInstallTotalBytes.HasValue && voicevoxInstallTotalBytes.Value > 0
            ? Math.Clamp((double)voicevoxInstallBytesReceived / voicevoxInstallTotalBytes.Value, 0.0, 1.0)
            : 0.0;
        return new
        {
            ok = true,
            installed = File.Exists(voicevoxArchivePath),
            extracted = !string.IsNullOrWhiteSpace(exePath),
            engineRunning = IsVoicevoxApiAvailable(DefaultVoicevoxApi),
            phase = voicevoxInstallPhase,
            running = voicevoxInstallRunning,
            version = VoicevoxInstallVersion,
            archivePath = voicevoxArchivePath,
            runPath = exePath ?? string.Empty,
            downloadUrl = VoicevoxDownloadUrl,
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
    try
    {
        lock (voicevoxInstallLock) voicevoxInstallPhase = "download";
        using var resp = await voicevoxDownloadHttp.GetAsync(VoicevoxDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
        await ExtractAndStartVoicevoxAsync(ct);
    }
    catch (Exception ex)
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
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

async Task ExtractAndStartVoicevoxAsync(CancellationToken ct)
{
    try
    {
        lock (voicevoxInstallLock) voicevoxInstallPhase = "extract";
        var exePath = FindInstalledVoicevoxExe();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            if (!File.Exists(voicevoxArchivePath)) throw new FileNotFoundException("VOICEVOX archive not found", voicevoxArchivePath);
            if (Directory.Exists(voicevoxExtractDir)) Directory.Delete(voicevoxExtractDir, recursive: true);
            Directory.CreateDirectory(voicevoxExtractDir);
            ZipFile.ExtractToDirectory(voicevoxArchivePath, voicevoxExtractDir);
            exePath = FindInstalledVoicevoxExe();
        }
        if (string.IsNullOrWhiteSpace(exePath)) throw new FileNotFoundException("VOICEVOX.exe not found after extraction", voicevoxExtractDir);

        var config = LoadAppConfig();
        config.VoicevoxExePath = exePath;
        SaveAppConfig(config);

        lock (voicevoxInstallLock) voicevoxInstallPhase = "start";
        if (!IsVoicevoxApiAvailable(DefaultVoicevoxApi))
        {
            Process.Start(new ProcessStartInfo { FileName = exePath, WorkingDirectory = Path.GetDirectoryName(exePath) ?? voicevoxExtractDir, UseShellExecute = true });
            await Task.Delay(1500, ct);
        }
        voicevoxInstallError = IsVoicevoxApiAvailable(DefaultVoicevoxApi, 2000) ? null : "VOICEVOX API is not available yet";
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

static bool LooksLikeVirtualAudioDeviceName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return false;
    var lower = name.ToLowerInvariant();
    string[] hints = ["virtual audio", "virtual cable", "virtual audio cable", "vb-audio", "vb cable", "cable input", "cable output", "voicemeeter", "sonar"];
    return hints.Any(lower.Contains);
}

static int VirtualAudioDeviceScore(TtsDeviceInfo device)
{
    var text = $"{device.Name} {device.FriendlyName} {device.TokenId}".ToLowerInvariant();
    if (text.Contains("cable input") || text.Contains("vb-audio")) return 0;
    if (text.Contains("vb cable") || text.Contains("virtual cable") || text.Contains("virtual audio")) return 1;
    if (text.Contains("voicemeeter")) return 2;
    if (text.Contains("sonar")) return 3;
    return 9;
}

/* Serve static files from web/ */
var webDir = Path.Combine(appRoot, "web");
if (Directory.Exists(webDir))
{
    var fileProvider = new PhysicalFileProvider(webDir);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider,
        DefaultFileNames = new List<string> { "index.html" }
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = "",
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    });
}

/* In-memory bench job state */
var benchJob = new BenchJobState();
var benchRealtimeJob = new BenchJobState();
RealtimeSessionController? realtimeSession = null;
Action? stopOnsetForSession = null;
var realtimeSessionService = new RealtimeSessionService(() => stopOnsetForSession?.Invoke());
object realtimeLock = new object();
var realtimeMutationGate = new SemaphoreSlim(1, 1);
IDisposable? realtimeWorkLease = null;
var addonWorkGate = new AddonOperationGate();
var addonHttp = new HttpClient();
var addonManager = new AddonLanguageManager(Path.Combine(AppUserPaths.V2Root, "asr-addons"), addonHttp);
var addonTranscriber = new AddonNativeTranscriber(addonManager, app.Lifetime.ApplicationStopping, addonWorkGate);
var addonLoad = Task.Run(() => addonManager.LoadAsync(app.Lifetime.ApplicationStopping));
var addonEndpoints = new AddonAsrEndpoints(app, addonManager, addonLoad, addonWorkGate);
bool AddonAvailable() { var state = addonManager.Snapshot(); return OperatingSystem.IsWindows() && state.Ready && state.Enabled; }
bool IsAddonEngine(string engine) => AddonCatalog.Engines.Any(e => e.Id == engine);
bool AddonEngineAvailable(string engine) => AddonAvailable() && addonManager.Snapshot().EngineId == engine;
bool ValidAddonMode(string? mode, string? addonEngine, bool used) => used && addonEngine == AddonCatalog.QwenId
    ? mode is "requested" or "auto" : mode is null;
bool RealtimeEngineAllowed(string engine) => IsLiteralValue(engine, RealtimeEngineValues) ||
    (IsAddonEngine(engine) && AddonEngineAvailable(engine));
app.Lifetime.ApplicationStopping.Register(() =>
{
    addonEndpoints.Dispose();
    lock (realtimeLock)
    {
        try { realtimeSessionService.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "[Realtime] shutdown failed"); }
        finally
        {
            realtimeSession = null;
            realtimeWorkLease?.Dispose();
            realtimeWorkLease = null;
        }
    }
    try { addonManager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch (Exception ex) { logger.LogWarning(ex, "[AddonAsr] shutdown failed"); }
    addonHttp.Dispose();
});

string ResolveDefaultRealtimeEngine(AppConfig config)
{
    var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
    return whisperRuntime.UseGpu ? RealtimeEngineJointAuto : RealtimeEngineMoonshine;
}

bool IsLegacyWindowsCpuOnlyRuntime(AppConfig config)
{
    if (!OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
    var detailed = AppConfigLoader.ResolveWhisperRuntimeDetailed(config, appRoot);
    return string.Equals(detailed.ReasonCode, "windows_legacy_cpu_only", StringComparison.OrdinalIgnoreCase);
}

string ResolveRealtimeEngineForRuntime(string requestedEngine, AppConfig config, ILogger? log = null)
{
    if (!IsLegacyWindowsCpuOnlyRuntime(config)) return requestedEngine;
    if (string.Equals(requestedEngine, RealtimeEngineWhisper, StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestedEngine, RealtimeEngineJointAuto, StringComparison.OrdinalIgnoreCase))
    {
        log?.LogWarning("[Realtime] Windows legacy runtime detected; forcing engine {RequestedEngine} -> {Engine}", requestedEngine, RealtimeEngineMoonshine);
        return RealtimeEngineMoonshine;
    }
    return requestedEngine;
}

var whisperModelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
var whisperTurbo = new WhisperTurboDownload(whisperModelHttp, Path.Combine(AppUserPaths.V2Root, "asr-models", "whisper"));
app.Lifetime.ApplicationStopping.Register(() => whisperModelHttp.Dispose());

IReadOnlyList<(string Path, string Name, string Size)> EnumerateWhisperModels()
{
    var roots = new List<string> { Path.Combine(appRoot, "models", "whisper"), whisperTurbo.Root };
    var steamInstallRoot = AppConfigLoader.GetSteamInstallRootFallback(appRoot);
    if (steamInstallRoot != null)
    {
        roots.Add(Path.Combine(steamInstallRoot, "models", "whisper"));
    }

    var configured = Path.GetFullPath(Path.Combine(appRoot, LoadAppConfig().DefaultModelPath));
    return roots
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*.bin", SearchOption.TopDirectoryOnly))
        .Concat(File.Exists(configured) ? new[] { configured } : Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path =>
        {
            var info = new FileInfo(path);
            var rel = path.StartsWith(whisperTurbo.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? path : Path.GetRelativePath(appRoot, path).Replace('\\', '/');
            return (Path: rel, Name: Path.GetFileName(path), Size: info.Length.ToString());
        })
        .OrderBy(m => m.Name.Contains("large", StringComparison.OrdinalIgnoreCase) ? 0 : m.Name.Contains("small", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
        .ThenBy(m => m.Name)
        .ToArray();
}

string ResolveInitialWhisperModel(AppConfig config)
{
    var models = EnumerateWhisperModels();
    if (models.Count == 0) return config.DefaultModelPath;
    var configured = models.FirstOrDefault(m => string.Equals(
        Path.GetFullPath(Path.Combine(appRoot, m.Path)),
        Path.GetFullPath(Path.Combine(appRoot, config.DefaultModelPath)), StringComparison.OrdinalIgnoreCase));
    // Keep the existing GPU/CPU preference for package defaults, but retain custom paths.
    var packageDefault = config.DefaultModelPath.Replace('\\', '/');
    if (!string.IsNullOrEmpty(configured.Path) &&
        !string.Equals(packageDefault, "models/whisper/ggml-small-q8_0.bin", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(packageDefault, WhisperTurboDownload.RelativePath, StringComparison.OrdinalIgnoreCase))
        return configured.Path;
    var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
    var preferred = whisperRuntime.UseGpu ? "large" : "small";
    var match = models.FirstOrDefault(m => m.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrEmpty(match.Path)) return match.Path;
    if (!string.IsNullOrEmpty(configured.Path)) return configured.Path;
    return models[0].Path;
}

string selectedWhisperModelPath = ResolveInitialWhisperModel(LoadAppConfig());

AppConfig LoadConfigWithSelectedWhisperModel()
{
    var config = LoadAppConfig();
    if (!string.IsNullOrWhiteSpace(selectedWhisperModelPath)) config.DefaultModelPath = selectedWhisperModelPath;
    return config;
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = MobMateCore.Version,
    buildChannel = MobMateCore.BuildChannel,
    isDemo = MobMateCore.IsDemo,
    commitSha = MobMateCore.CommitSha,
    buildTimeUtc = MobMateCore.BuildTimeUtc,
}));

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

IResult ReleaseStatusResponse()
{
    var config = LoadConfigWithSelectedWhisperModel();
    var detailed = AppConfigLoader.ResolveWhisperRuntimeDetailed(config, appRoot);
    return Results.Ok(new
    {
        version = MobMateCore.Version,
        buildChannel = MobMateCore.BuildChannel,
        isDemo = MobMateCore.IsDemo,
        commitSha = MobMateCore.CommitSha,
        buildTimeUtc = MobMateCore.BuildTimeUtc,
        runtimeMode = detailed.Selection.UseGpu ? "vulkan" : "cpu",
        runtimeReason = detailed.Selection.Reason,
        runtimeReasonCode = detailed.ReasonCode,
        legacyWindowsCpuOnly = string.Equals(detailed.ReasonCode, "windows_legacy_cpu_only", StringComparison.OrdinalIgnoreCase),
        realtimeStartRecoveryRequired,
        steamStatus = detailed.SteamStatus.ToString().ToLowerInvariant(),
        dlcStatus = detailed.DlcStatus.ToString().ToLowerInvariant(),
        cloudConfigDir = AppUserPaths.ConfigDir,
    });
}

app.MapGet("/api/release/status", () => ReleaseStatusResponse());
app.MapGet("/api/runtime/status", () => ReleaseStatusResponse());

app.MapGet("/api/whisper/models", () =>
{
    var config = LoadAppConfig();
    var autoModel = ResolveInitialWhisperModel(config);
    return Results.Ok(new { selected = selectedWhisperModelPath, autoSelected = autoModel, models = EnumerateWhisperModels().Select(m => new { path = m.Path, name = m.Name, size = m.Size }) });
});

app.MapPost("/api/whisper/model", async (HttpRequest request) =>
{
    try
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, app.Lifetime.ApplicationStopping);
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellation.Token);
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return Results.BadRequest(ApiMessage("request.invalid_json", "path must be a string"));
        var path = await whisperTurbo.ResolveSelectionAsync(pathProp.GetString() ?? string.Empty,
            EnumerateWhisperModels().Select(m => m.Path), cancellation.Token);
        if (path is null) return Results.BadRequest(ApiMessage("whisper.unknown_model", "unknown whisper model"));
        cancellation.Token.ThrowIfCancellationRequested();
        selectedWhisperModelPath = path;
        return Results.Ok(new { code = "whisper.model_selected", message = "whisper model selected", selected = selectedWhisperModelPath });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
    catch (OperationCanceledException)
    {
        return Results.Json(ApiMessage("whisper.download_cancelled", "Whisper model request cancelled"), statusCode: 408);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
    {
        logger.LogWarning(ex, "[Whisper] model download failed; selection preserved");
        return Results.Json(ApiMessage("whisper.download_failed", "Whisper Turbo download failed; previous model retained"), statusCode: 502);
    }
});

app.MapGet("/api/config", () =>
{
    var config = LoadConfigWithSelectedWhisperModel();
    var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
    return Results.Ok(new
    {
        config.NativeExePath,
        config.DefaultModelPath,
        WhisperRuntimeMode = whisperRuntime.UseGpu ? "vulkan" : "cpu",
        WhisperDllDir = whisperRuntime.WhisperDllDir,
        WhisperRuntimeReason = whisperRuntime.Reason,
        config.DefaultMoonshineDllDir,
        config.DefaultMoonshineModelRoot,
        config.VoicevoxExePath,
        DefaultRealtimeEngine = ResolveDefaultRealtimeEngine(config),
    });
});

app.MapPost("/api/config/voicevox-path", async (HttpRequest request) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        var path = body.RootElement.TryGetProperty("path", out var pathProp) ? pathProp.GetString()?.Trim() ?? string.Empty : string.Empty;
        if (path.Length > 4096)
            return Results.BadRequest(ApiMessage("config.invalid_voicevox_exe_path", "voicevox path is too long"));
        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = ResolveConfigPath(path);
            if (!File.Exists(resolved) || !string.Equals(Path.GetExtension(resolved), ".exe", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiMessage("config.invalid_voicevox_exe_path", "voicevox path must be empty or an existing .exe file"));
            if (!string.Equals(Path.GetFileName(resolved), "VOICEVOX.exe", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiMessage("config.invalid_voicevox_exe_path", "voicevox path must point to VOICEVOX.exe"));
        }

        var config = LoadAppConfig();
        config.VoicevoxExePath = path;
        SaveAppConfig(config);
        return Results.Ok(new { code = "config.voicevox_path_saved", message = "voicevox path saved", voicevoxExePath = config.VoicevoxExePath });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("config.voicevox_path_failed"));
    }
});

app.MapPost("/api/config/voicevox-path/browse", (string? lang, string? title) =>
{
    var result = ShowVoicevoxFileDialog(VoicevoxDialogTitle(lang, title));
    if (result.ExitCode == 2)
        return Results.Ok(new { code = "config.voicevox_path_browse_cancelled", cancelled = true, path = string.Empty });
    if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Path))
        return Results.Problem(detail: result.Error ?? "file dialog failed", extensions: ApiProblemCode("config.voicevox_path_browse_failed"));
    if (!File.Exists(result.Path) || !string.Equals(Path.GetFileName(result.Path), "VOICEVOX.exe", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(ApiMessage("config.invalid_voicevox_exe_path", "voicevox path must point to VOICEVOX.exe"));
    return Results.Ok(new { code = "config.voicevox_path_selected", cancelled = false, path = result.Path });
});

app.MapGet("/api/voicevox/install-status", () => Results.Ok(VoicevoxInstallStateResponse()));

app.MapGet("/api/vcclient/install-status", () => Results.Ok(VcClientInstallStateResponse()));

app.MapPost("/api/vcclient/official-site", () =>
{
    try
    {
        Process.Start(new ProcessStartInfo(VcClientOfficialUrl) { UseShellExecute = true });
        return Results.Ok(new { ok = true, url = VcClientOfficialUrl });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/vcclient/install", () =>
{
    lock (vcclientInstallLock)
    {
        if (vcclientInstallRunning)
            return Results.Conflict(new { ok = false, error = "VCClient install is already running" });
        vcclientInstallRunning = true;
        vcclientInstallBytesReceived = 0;
        vcclientInstallTotalBytes = null;
        vcclientInstallError = null;
        vcclientInstallStartedAt = DateTimeOffset.UtcNow;
        vcclientInstallFinishedAt = null;
    }
    _ = Task.Run(() => File.Exists(vcclientArchivePath)
        ? ExtractAndStartVcClientAsync(CancellationToken.None)
        : DownloadVcClientArchiveAsync(CancellationToken.None));
    return Results.Ok(VcClientInstallStateResponse());
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

var audioInputLogger = loggerFactory.CreateLogger("AudioInput");
app.MapGet("/api/audio/input-devices", () =>
{
    var config = LoadAppConfig();
    var nativeExe = Path.IsPathFullyQualified(config.NativeExePath)
        ? config.NativeExePath
        : Path.Combine(appRoot, config.NativeExePath);
    audioInputLogger.LogInformation("[AudioInput] action=list begin nativeExe={NativeExe}", nativeExe);
    var json = NativeLibraryBridge.GetAudioInputDevicesJson(nativeExe);
    audioInputLogger.LogInformation("[AudioInput] action=list returned json={DeviceResult}", json);
    return Results.Content(json, "application/json");
});

/* Phase 7.5-L: text filter / NG / laugh replacement */
var textFilterLock = new object();
var textFilterConfig = RealtimeTextFilterConfig.CreateDefault(initialUiLanguage);
textFilterConfig.UiLanguage = ignoreBootstrap.Language;
var ignoreFilePath = Path.Combine(AppUserPaths.ConfigDir, "_ignore.txt");

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

app.MapGet("/api/ui/i18n", (string? lang) =>
{
    var requested = AppPresetBootstrap.NormalizeLanguage(lang);
    if (string.IsNullOrEmpty(requested)) requested = textFilterConfig.UiLanguage;
    var resolved = AppPresetBootstrap.NormalizeLanguage(requested);
    if (string.IsNullOrEmpty(resolved)) resolved = "en";

    var strings = ReadUiPreset("en");
    foreach (var kv in ReadUiPreset(resolved)) strings[kv.Key] = kv.Value;
    return Results.Ok(new { language = resolved, strings });
});

var whisperLanguageLabels = WhisperLanguageCatalog.Labels;

IReadOnlyList<string> DetectMoonshineLanguages()
{
    var config = LoadAppConfig();
    var root = Path.IsPathFullyQualified(config.DefaultMoonshineModelRoot) ? config.DefaultMoonshineModelRoot : Path.Combine(appRoot, config.DefaultMoonshineModelRoot);
    return MoonshineModelCatalog.Detect(root);
}

IReadOnlyList<string> GetRealtimeLanguageCodes(string engine, AddonState? addonState = null)
{
    var normalized = (engine ?? string.Empty).Trim().ToLowerInvariant();
    if (IsAddonEngine(normalized))
    {
        var state = addonState ?? addonManager.Snapshot();
        return OperatingSystem.IsWindows() && state.Ready && state.Enabled && state.EngineId == normalized
            ? AddonCatalog.Engines.Single(e => e.Id == normalized).Models.FirstOrDefault(m => m.Id == state.ModelId)?.Languages ?? Array.Empty<string>()
            : Array.Empty<string>();
    }
    var moonshine = DetectMoonshineLanguages();
    if (normalized == RealtimeEngineMoonshine) return moonshine;
    if (normalized == RealtimeEngineJointAuto)
    {
        var config = LoadConfigWithSelectedWhisperModel();
        var runtime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
        string Absolute(string path) => Path.IsPathFullyQualified(path) ? path : Path.Combine(appRoot, path);
        if (IsLegacyWindowsCpuOnlyRuntime(config) || string.IsNullOrWhiteSpace(runtime.WhisperDllDir) || string.IsNullOrWhiteSpace(config.DefaultMoonshineDllDir) ||
            string.IsNullOrWhiteSpace(config.DefaultModelPath) ||
            !File.Exists(Path.Combine(Absolute(runtime.WhisperDllDir), "whisper.dll")) ||
            !File.Exists(Path.Combine(Absolute(config.DefaultMoonshineDllDir), "moonshine.dll")) ||
            !File.Exists(Absolute(config.DefaultModelPath))) return Array.Empty<string>();
    }
    if (normalized == RealtimeEngineJointAuto || normalized == RealtimeEngineSafeAuto) return moonshine.Where(code => whisperLanguageLabels.ContainsKey(code)).ToArray();
    // sense_voice: SenseVoice 対応言語 (yue=広東語 含む)
    if (normalized == RealtimeEngineSenseVoice)
        return new[] { "ja", "en", "zh", "ko", "yue" };
    if (normalized == RealtimeEngineParaformerZh) return new[] { "zh" };
    return whisperLanguageLabels.Keys.OrderBy(PreferredLanguageOrder).ThenBy(x => x).ToArray();
}

object RealtimeLanguageResponse(string engine, AddonState? addonState = null)
{
    var codes = GetRealtimeLanguageCodes(engine, addonState);
    return codes.Select(code => new { code, label = whisperLanguageLabels.TryGetValue(code, out var label) ? label : code }).ToArray();
}

app.MapGet("/api/realtime/languages", () =>
{
    var config = LoadAppConfig();
    var addonState = addonManager.Snapshot();
    return Results.Ok(new
    {
        defaultEngine = ResolveDefaultRealtimeEngine(config),
        languagesByEngine = new
        {
            whisper = RealtimeLanguageResponse(RealtimeEngineWhisper),
            moonshine = RealtimeLanguageResponse(RealtimeEngineMoonshine),
            joint_auto = RealtimeLanguageResponse(RealtimeEngineJointAuto),
            safe_auto = RealtimeLanguageResponse(RealtimeEngineSafeAuto),
            sense_voice = RealtimeLanguageResponse(RealtimeEngineSenseVoice),
            paraformer_zh = RealtimeLanguageResponse(RealtimeEngineParaformerZh),
            haas_ja = RealtimeLanguageResponse(RealtimeEngineHaas, addonState),
            parakeet_ja = RealtimeLanguageResponse(AddonCatalog.ParakeetId, addonState),
            qwen3_asr_ja = RealtimeLanguageResponse(AddonCatalog.QwenId, addonState),
            nemotron_3_5 = RealtimeLanguageResponse(AddonCatalog.NemotronId, addonState),
        },
        senseVoiceAvailable = senseVoiceService?.IsAvailable == true
    });
});

bool IsRealtimeLanguageSupported(string engine, string language) => GetRealtimeLanguageCodes(engine).Contains(language, StringComparer.OrdinalIgnoreCase);

string ReadIgnoreWords()
{
    return File.Exists(ignoreFilePath) ? File.ReadAllText(ignoreFilePath, Encoding.UTF8) : string.Empty;
}

int CountIgnoreRules() => RealtimeTextFilter.SplitLines(ReadIgnoreWords()).Count;

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
        };
    }
    snapshot.NgWords = ReadIgnoreWords();

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

object TextFilterConfigResponse(RealtimeTextFilterConfig config) => new
{
    uiLanguage = config.UiLanguage,
    ngMode = config.NgMode,
    ngSource = "_ignore.txt",
    ngRuleCount = CountIgnoreRules(),
    laughPreset = config.LaughPreset,
    resolvedLaughLanguage = RealtimeTextFilterConfig.NormalizeLaughLanguage(config.LaughPreset == "auto" ? config.UiLanguage : config.LaughPreset),
    laughReplacementEnabled = config.LaughReplacementEnabled,
    manualLaughTokens = ResolveTextFilterTokens(config).Manual,
    autoLaughTokens = ResolveTextFilterTokens(config).Auto,
    laughReplacementWords = ResolveTextFilterTokens(config).Replacement,
    manualTokenCount = RealtimeTextFilter.SplitCsv(ResolveTextFilterTokens(config).Manual).Count,
    autoTokenCount = RealtimeTextFilter.SplitCsv(ResolveTextFilterTokens(config).Auto).Count,
    replacementCount = RealtimeTextFilter.SplitCsv(ResolveTextFilterTokens(config).Replacement).Count,
};

(string Manual, string Auto, string Replacement) ResolveTextFilterTokens(RealtimeTextFilterConfig config)
{
    if (!string.Equals(config.LaughPreset, "auto", StringComparison.OrdinalIgnoreCase))
        return (config.ManualLaughTokens, config.AutoLaughTokens, config.LaughReplacementWords);
    var profile = RealtimeTextFilterConfig.GetLaughProfile(config.UiLanguage);
    return (profile.ManualTokens, profile.AutoTokens, profile.ReplacementWords);
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

app.MapGet("/api/text-filter/config", () =>
{
    lock (textFilterLock)
    {
        return Results.Ok(TextFilterConfigResponse(textFilterConfig));
    }
});

app.MapPost("/api/text-filter/config", async (HttpRequest request) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        lock (textFilterLock)
        {
            if (body.RootElement.TryGetProperty("uiLanguage", out var uiLangProp))
            {
                var uiLang = AppPresetBootstrap.NormalizeLanguage(uiLangProp.GetString());
                if (string.IsNullOrEmpty(uiLang)) return Results.BadRequest(ApiMessage("text_filter.invalid_ui_language", "uiLanguage must be en, ja, ko, zh_cn, or zh_tw"));
                textFilterConfig.UiLanguage = uiLang;
            }
            if (body.RootElement.TryGetProperty("ngMode", out var ngModeProp))
            {
                var mode = (ngModeProp.GetString() ?? "simple").Trim().ToLowerInvariant();
                if (mode is not ("simple" or "regex")) return Results.BadRequest(ApiMessage("text_filter.invalid_ng_mode", "ngMode must be simple or regex"));
                if (mode == "regex")
                {
                    var validation = ValidateNgWordsError(ReadIgnoreWords(), mode);
                    if (validation != null) return Results.BadRequest(ApiMessage("text_filter.invalid_ng_words", validation));
                }
                textFilterConfig.NgMode = mode;
            }
            if (body.RootElement.TryGetProperty("laughReplacementEnabled", out var enabledProp))
                textFilterConfig.LaughReplacementEnabled = enabledProp.GetBoolean();
            if (body.RootElement.TryGetProperty("laughPreset", out var presetProp))
            {
                var preset = (presetProp.GetString() ?? "auto").Trim().ToLowerInvariant();
                if (preset is not ("auto" or "custom" or "ja" or "en" or "ko" or "zh"))
                    return Results.BadRequest(ApiMessage("text_filter.invalid_laugh_preset", "laughPreset must be auto, custom, ja, en, ko, or zh"));
                textFilterConfig.LaughPreset = preset;
                if (preset is not ("auto" or "custom")) textFilterConfig.ApplyLaughPreset(preset);
            }
            if (body.RootElement.TryGetProperty("manualLaughTokens", out var manualProp))
            {
                var value = manualProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiMessage("text_filter.manual_laugh_tokens_too_large", "manualLaughTokens is too large"));
                textFilterConfig.ManualLaughTokens = value;
                textFilterConfig.LaughPreset = "custom";
            }
            if (body.RootElement.TryGetProperty("autoLaughTokens", out var autoProp))
            {
                var value = autoProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiMessage("text_filter.auto_laugh_tokens_too_large", "autoLaughTokens is too large"));
                textFilterConfig.AutoLaughTokens = value;
                textFilterConfig.LaughPreset = "custom";
            }
            if (body.RootElement.TryGetProperty("laughReplacementWords", out var replProp))
            {
                var value = replProp.GetString() ?? string.Empty;
                if (value.Length > 2000 || RealtimeTextFilter.SplitCsv(value).Count > 200) return Results.BadRequest(ApiMessage("text_filter.laugh_replacement_words_too_large", "laughReplacementWords is too large"));
                textFilterConfig.LaughReplacementWords = value;
                textFilterConfig.LaughPreset = "custom";
            }
            return Results.Ok(TextFilterConfigResponse(textFilterConfig));
        }
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapGet("/api/text-filter/ng-words", () =>
{
    var text = ReadIgnoreWords();
    var preset = AppPresetBootstrap.ResolveIgnorePresetPath(appRoot, textFilterConfig.UiLanguage) ?? string.Empty;
    return Results.Ok(new { source = "_ignore.txt", presetFallback = preset, mode = textFilterConfig.NgMode, text, count = RealtimeTextFilter.SplitLines(text).Count });
});

app.MapPost("/api/text-filter/ng-words", async (HttpRequest request) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        string text;
        if (body.RootElement.TryGetProperty("loadPreset", out var loadPresetProp) && loadPresetProp.GetBoolean())
        {
            var lang = body.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : textFilterConfig.UiLanguage;
            var preset = AppPresetBootstrap.ResolveIgnorePresetPath(appRoot, lang ?? "en");
            text = !string.IsNullOrEmpty(preset) && File.Exists(preset) ? File.ReadAllText(preset, Encoding.UTF8) : string.Empty;
        }
        else
        {
            text = body.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        }
        var validation = ValidateNgWordsError(text, textFilterConfig.NgMode);
        if (validation != null) return Results.BadRequest(ApiMessage("text_filter.invalid_ng_words", validation));
        Directory.CreateDirectory(AppUserPaths.ConfigDir);
        File.WriteAllText(ignoreFilePath, text, new UTF8Encoding(false));
        return Results.Ok(new { code = "text_filter.ng_words_saved", message = "ng words saved", source = "_ignore.txt", count = RealtimeTextFilter.SplitLines(text).Count });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapPost("/api/text-filter/preview", async (HttpRequest request) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        var text = body.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        var lang = body.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
        var result = RealtimeTextFilter.Apply(text, SnapshotTextFilterConfig(lang));
        return Results.Ok(new { rawText = result.RawText, text = result.Text, dropped = result.Dropped, laughReplaced = result.LaughReplaced, ngMatched = result.NgMatched, reason = result.Reason });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapGet("/api/bench/status", () =>
{
    var snapshot = benchJob.Snapshot();
    return Results.Ok(snapshot);
});

app.MapPost("/api/bench/start", () =>
{
    var workLease = addonWorkGate.TryEnterWork();
    if (workLease is null) return Results.Conflict(ApiMessage("addon.busy", "Addon operation is running"));
    var scheduled = false;
    try
    {
    var snap = benchJob.Snapshot();
    if (snap.State == "running")
    {
        return Results.Conflict(new { code = "bench.already_running", message = "bench already running", job = snap });
    }

    var config = LoadConfigWithSelectedWhisperModel();
    var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
    if (string.IsNullOrWhiteSpace(config.DefaultManifestPath))
    {
        return Results.BadRequest(ApiMessage("bench.manifest_not_configured", "bench manifest is not configured in this package"));
    }
    var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var outDir = Path.Combine(appRoot, "temp", "bench");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"bench_{ts}.jsonl");

    /* Resolve native exe path relative to app root */
    var nativeExe = Path.IsPathFullyQualified(config.NativeExePath)
        ? config.NativeExePath
        : Path.Combine(appRoot, config.NativeExePath);
    var nativeCommand = ResolveNativeCommandPath(nativeExe);

    benchJob.Start(total: 15);
    benchJob.ResultPath = outPath;

    /* Run bench in background */
    _ = Task.Run(async () =>
    {
        try
        {
            var benchArgs = new[]
            {
                "bench",
                "--manifest", config.DefaultManifestPath,
                "--out", outPath,
                "--model", config.DefaultModelPath,
                "--whisper-dll-dir", whisperRuntime.WhisperDllDir,
                "--threads", config.DefaultThreads.ToString(),
            };

            var result = NativeCommandRunner.Run(nativeCommand, benchArgs, appRoot);

            if (!result.Success || !File.Exists(outPath))
            {
                benchJob.Fail(result.Stderr ?? $"exit code {result.ExitCode}");
                return;
            }

            /* Parse JSONL for summary stats */
            var lines = await File.ReadAllLinesAsync(outPath);
            int completed = 0, exact = 0, errors = 0;
            double cerSum = 0.0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    completed++;
                    if (root.TryGetProperty("exact", out var ex) && ex.GetBoolean()) exact++;
                    if (root.TryGetProperty("error", out var err) && err.GetString() is not null) errors++;
                    if (root.TryGetProperty("cer", out var cer)) cerSum += cer.GetDouble();
                }
                catch { /* skip malformed lines */ }
            }

            benchJob.Completed = completed;
            benchJob.ExactCount = exact;
            benchJob.AvgCer = completed > 0 ? Math.Round(cerSum / completed, 4) : null;
            benchJob.Complete(outPath);
        }
        catch (Exception ex)
        {
            benchJob.Fail(ex.Message);
        }
        finally { workLease.Dispose(); }
    });

    scheduled = true;
    return Results.Ok(new { code = "bench.started", message = "bench started", jobId = benchJob.JobId });
    }
    finally { if (!scheduled) workLease.Dispose(); }
});

app.MapGet("/api/bench/results", () =>
{
    var snap = benchJob.Snapshot();
    if (snap.State != "completed" || string.IsNullOrEmpty(snap.ResultPath))
    {
        return Results.NotFound(ApiMessage("bench.no_completed_results", "no completed results"));
    }
    return Results.Ok(new
    {
        jobId = snap.JobId,
        total = snap.Total,
        completed = snap.Completed,
        exactCount = snap.ExactCount,
        avgCer = snap.AvgCer,
        resultPath = snap.ResultPath,
    });
});

/* Realtime route bench job state */
object benchRealtimeOptionsLock = new object();
(string route, string engine, string language, string scene, string prefilter, string moonshineDllDir, string moonshineModelRoot, int moonshineThreads) benchRealtimeOptions =
    ("realtime", RealtimeEngineWhisper, "ja", RealtimeSceneShortCommand, RealtimePrefilterAuto, string.Empty, string.Empty, 0);

/* Realtime session state */
(string? engine, string? language, string? scene, string? prefilterProfile, int initialPromptLength) lastStartOptions =
    (null, null, null, null, 0);

object adaptiveRmsLock = new object();
double adaptiveRmsAverage = 0.0;
int adaptiveRmsCount = 0;

Func<RealtimePollResult, string, string, RealtimeFinalOutput>? replayOutputProcessor = null;

app.MapGet("/api/bench-realtime-route/status", () =>
{
    var snap = benchRealtimeJob.Snapshot();
    return Results.Ok(snap);
});

app.MapPost("/api/bench-realtime-route/start", (JsonElement body) =>
{
    var workLease = addonWorkGate.TryEnterWork();
    if (workLease is null) return Results.Conflict(ApiMessage("addon.busy", "Addon operation is running"));
    var scheduled = false;
    try
    {
    var snap = benchRealtimeJob.Snapshot();
    if (snap.State == "running")
    {
        return Results.Conflict(new { code = "bench.realtime_route_already_running", message = "realtime route bench already running", job = snap });
    }

    lock (realtimeLock)
    {
        if (realtimeSession != null)
        {
            var poll = realtimeSession.Poll();
            if (!string.Equals(poll.State, "completed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(poll.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new
                {
                    code = "bench.realtime_session_active",
                    message = "stop realtime recording before starting realtime route bench",
                    state = poll.State
                });
            }
            realtimeSessionService.Dispose();
            realtimeSession = null;
            realtimeWorkLease?.Dispose();
            realtimeWorkLease = null;
        }
    }

    var config = LoadConfigWithSelectedWhisperModel();
    var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
    /* Parse optional body params */
    string engine = ResolveDefaultRealtimeEngine(config);
    string language = "ja";
    string scene = RealtimeSceneShortCommand;
    string prefilter = RealtimePrefilterOff;
    string initialPrompt = string.Empty;
    string manifestPath = config.DefaultManifestPath;
    string effectiveMoonshineDllDir = config.DefaultMoonshineDllDir;
    string effectiveMoonshineModelRoot = config.DefaultMoonshineModelRoot;
    int requestedMoonshineThreads = config.DefaultThreads;
    bool experimentalAsrCandidatesEnabled = false;
    bool productionReplay = false;
    bool managedProductionReplay = false;
    string experimentalAsrCandidateEngine = string.Empty;
    string experimentalAsrCandidateLanguage = string.Empty;
    string experimentalAsrCandidateModelDir = string.Empty;
    string? addonLanguageMode = null;
    if (body.TryGetProperty("addonLanguageMode", out var hint))
        addonLanguageMode = hint.ValueKind == JsonValueKind.String ? hint.GetString() ?? "" : "";
    if (body.TryGetProperty("engine", out var eng) && eng.ValueKind == JsonValueKind.String) engine = eng.GetString()!;
    if (body.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String) language = lang.GetString()!;
    if (body.TryGetProperty("scene", out var sc) && sc.ValueKind == JsonValueKind.String) scene = sc.GetString()!;
    if (body.TryGetProperty("prefilter", out var pf) && pf.ValueKind == JsonValueKind.String) prefilter = pf.GetString()!;
    if (body.TryGetProperty("initialPrompt", out var ip) && ip.ValueKind == JsonValueKind.String) initialPrompt = ip.GetString() ?? string.Empty;
    if (body.TryGetProperty("manifestPath", out var mp) && mp.ValueKind == JsonValueKind.String) manifestPath = mp.GetString()!;
    if (body.TryGetProperty("moonshineDllDir", out var moonDll) && moonDll.ValueKind == JsonValueKind.String) effectiveMoonshineDllDir = moonDll.GetString() ?? effectiveMoonshineDllDir;
    if (body.TryGetProperty("moonshineModelRoot", out var moonRoot) && moonRoot.ValueKind == JsonValueKind.String) effectiveMoonshineModelRoot = moonRoot.GetString() ?? effectiveMoonshineModelRoot;
    if (body.TryGetProperty("moonshineThreads", out var moonThreads)
        && (moonThreads.ValueKind != JsonValueKind.Number || !moonThreads.TryGetInt32(out requestedMoonshineThreads)
            || requestedMoonshineThreads < 1 || requestedMoonshineThreads > config.DefaultThreads))
    {
        return Results.BadRequest(ApiMessage("realtime.invalid_moonshine_threads", $"moonshineThreads must be an integer between 1 and {config.DefaultThreads}"));
    }
    if (body.TryGetProperty("experimentalAsrCandidatesEnabled", out var expEnabled) && expEnabled.ValueKind == JsonValueKind.True) experimentalAsrCandidatesEnabled = true;
    if (body.TryGetProperty("experimentalAsrCandidateEngine", out var expEngine) && expEngine.ValueKind == JsonValueKind.String) experimentalAsrCandidateEngine = expEngine.GetString() ?? string.Empty;
    if (body.TryGetProperty("experimentalAsrCandidateLanguage", out var expLang) && expLang.ValueKind == JsonValueKind.String) experimentalAsrCandidateLanguage = expLang.GetString() ?? string.Empty;
    if (body.TryGetProperty("experimentalAsrCandidateModelDir", out var expModelDir) && expModelDir.ValueKind == JsonValueKind.String) experimentalAsrCandidateModelDir = expModelDir.GetString() ?? string.Empty;
    if (body.TryGetProperty("mode", out var mode))
    {
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("production_replay" or "native_segment_replay" or "full_candidate_diagnostic"))
            return Results.BadRequest(ApiMessage("bench.invalid_mode", "unknown realtime bench mode"));
        managedProductionReplay = mode.GetString() == "production_replay";
        productionReplay = mode.GetString() is "production_replay" or "native_segment_replay";
    }

    engine = engine.Trim().ToLowerInvariant();
    language = language.Trim().ToLowerInvariant();
    scene = scene.Trim().ToLowerInvariant();
    prefilter = prefilter.Trim().ToLowerInvariant();

    if (string.IsNullOrWhiteSpace(manifestPath))
    {
        return Results.BadRequest(ApiMessage("bench.manifest_not_configured", "bench manifest is not configured in this package"));
    }

    if (!RealtimeEngineAllowed(engine)) return Results.BadRequest(ApiMessage("realtime.invalid_engine", "unsupported realtime engine"));
    if (productionReplay && engine is not (RealtimeEngineJointAuto or RealtimeEngineMoonshine or RealtimeEngineWhisper) && !IsAddonEngine(engine))
        return Results.BadRequest(ApiMessage("bench.unsupported_replay_engine", "production replay is unavailable for this engine"));
    if (!IsRealtimeLanguageSupported(engine, language)) return Results.BadRequest(ApiMessage("realtime.unsupported_language", "language is not supported by the selected engine"));
    if (!IsLiteralValue(scene, RealtimeSceneValues)) return Results.BadRequest(ApiMessage("realtime.invalid_scene", "invalid scene"));
    if (!IsLiteralValue(prefilter, RealtimePrefilterValues)) return Results.BadRequest(ApiMessage("realtime.invalid_prefilter", "prefilter must be auto, normal, strong, or off"));
    if (initialPrompt.Length > 2000) return Results.BadRequest(ApiMessage("realtime.initial_prompt_too_long", "initialPrompt is too long"));

    int effectiveMoonshineThreads = string.Equals(engine, RealtimeEngineMoonshine, StringComparison.OrdinalIgnoreCase)
        ? requestedMoonshineThreads
        : config.DefaultThreads;

    if (IsJointAutoZh(engine, language))
    {
        experimentalAsrCandidatesEnabled = true;
        experimentalAsrCandidateEngine = "paraformer_zh";
        experimentalAsrCandidateLanguage = "zh";
        experimentalAsrCandidateModelDir = Path.Combine(appRoot, "models", "paraformer_zh");
    }

    var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var outDir = Path.Combine(appRoot, "temp", "bench");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"rtbench_{ts}.jsonl");

    var nativeExe = Path.IsPathFullyQualified(config.NativeExePath)
        ? config.NativeExePath
        : Path.Combine(appRoot, config.NativeExePath);

    if (IsAddonEngine(engine))
    {
        try { NativeLibraryBridge.RegisterAddonTranscriberV2(nativeExe, addonTranscriber.CallbackV2); }
        catch (Exception ex) { return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("addon.bridge_unavailable")); }
    }
    var addonAtBenchStart = addonManager.Snapshot();
    var jointAddonAtBenchStart = productionReplay && engine == RealtimeEngineJointAuto && OperatingSystem.IsWindows() &&
        addonAtBenchStart.Ready && addonAtBenchStart.Enabled && addonAtBenchStart.JointRequested;
    // Manifest rows determine language; the request's initial language does not exclude JA rows.
    var usesAddon = IsAddonEngine(engine) || jointAddonAtBenchStart;
    if (!ValidAddonMode(addonLanguageMode, addonAtBenchStart.EngineId, usesAddon))
        return Results.BadRequest(ApiMessage("addon.invalid_language_mode", "Qwen requires an explicit requested or auto hint; other routes do not accept addonLanguageMode"));
    var addonModelAtBenchStart = usesAddon ? AddonCatalog.Engines.Single(e => e.Id == addonAtBenchStart.EngineId)
        .Models.Single(m => m.Id == addonAtBenchStart.ModelId) : null;
    if (jointAddonAtBenchStart)
    {
        try { NativeLibraryBridge.RegisterAddonTranscriberV2(nativeExe, addonTranscriber.JointCallbackV2, joint: true); }
        catch (Exception ex) { return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("addon.bridge_unavailable")); }
    }

    var manifestTotal = File.Exists(manifestPath)
        ? Math.Max(0, File.ReadLines(manifestPath).Count(l => !string.IsNullOrWhiteSpace(l)) - 1)
        : 50;
    benchRealtimeJob.Start(total: manifestTotal);
    benchRealtimeJob.ResultPath = outPath;
    lock (benchRealtimeOptionsLock)
    {
        benchRealtimeOptions = (managedProductionReplay ? "production_replay" : productionReplay ? "native_segment_replay" : "realtime", engine, language, scene, prefilter, effectiveMoonshineDllDir, effectiveMoonshineModelRoot, effectiveMoonshineThreads);
    }

    _ = Task.Run(async () =>
    {
        try
        {
            var benchOptions = new Dictionary<string, object?>
            {
                ["engine"] = engine,
                ["language"] = language,
                ["scene"] = scene,
                ["initialPrompt"] = initialPrompt,
                ["prefilterProfile"] = prefilter == RealtimePrefilterOff ? RealtimePrefilterAuto : prefilter,
                ["usePrefilter"] = prefilter != RealtimePrefilterOff,
                ["modelPath"] = config.DefaultModelPath,
                ["whisperDllDir"] = whisperRuntime.WhisperDllDir,
                ["moonshineDllDir"] = effectiveMoonshineDllDir,
                ["moonshineModelRoot"] = effectiveMoonshineModelRoot,
                ["threads"] = effectiveMoonshineThreads,
                ["useGpu"] = whisperRuntime.UseGpu,
                ["productionReplay"] = productionReplay,
                ["addonJointRequested"] = jointAddonAtBenchStart,
                ["addonEngineId"] = usesAddon ? addonAtBenchStart.EngineId : AddonCatalog.HaasId,
                ["addonModelId"] = addonModelAtBenchStart?.Id ?? "",
                ["addonModelRevision"] = addonModelAtBenchStart?.Revision ?? "",
                ["vadSilenceDurationMs"] = 3000,
                ["sileroVadModelPath"] = "models/silero_vad/silero_vad_int8.onnx",
                ["onnxRuntimeDllDir"] = "libs/onnxruntime",
            };
            if (addonLanguageMode is not null) benchOptions["addonLanguageMode"] = addonLanguageMode;
            if (experimentalAsrCandidatesEnabled)
            {
                benchOptions["experimentalAsrCandidatesEnabled"] = true;
                benchOptions["experimentalAsrCandidateEngine"] = experimentalAsrCandidateEngine;
                benchOptions["experimentalAsrCandidateLanguage"] = experimentalAsrCandidateLanguage;
                benchOptions["experimentalAsrCandidateModelDir"] = experimentalAsrCandidateModelDir;
            }
            var benchOptionsJson = JsonSerializer.Serialize(benchOptions);

            var finalizer = managedProductionReplay
                ? RealtimeReplayFinalizer.Create((poll, rowLanguage, rowScene) =>
                {
                    lock (realtimeLock)
                        return replayOutputProcessor?.Invoke(poll, engine, rowLanguage)
                            ?? throw new InvalidOperationException("Replay output processor is unavailable");
                })
                : null;
            var result = string.Equals(engine, RealtimeEngineSenseVoice, StringComparison.OrdinalIgnoreCase)
                ? RunSenseVoiceManifestBench(senseVoiceService, manifestPath, outPath, language, logger)
                : NativeLibraryBridge.RealtimeBenchManifest(nativeExe, manifestPath, outPath, benchOptionsJson, finalizer);

            if (!result.Success || !File.Exists(outPath))
            {
                benchRealtimeJob.Fail(result.Message);
                return;
            }

            var lines = await File.ReadAllLinesAsync(outPath);
            int completed = 0, exact = 0, errors = 0;
            double cerSum = 0.0, latencySum = 0.0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    completed++;
                    if (root.TryGetProperty("exact", out var ex) && ex.GetBoolean()) exact++;
                    if (root.TryGetProperty("error", out var err) && err.GetString() is not null) errors++;
                    if (root.TryGetProperty("cer", out var cer)) cerSum += cer.GetDouble();
                    if (root.TryGetProperty("latency_ms", out var lat)) latencySum += lat.GetInt64();
                }
                catch { /* skip malformed lines */ }
            }

            benchRealtimeJob.Completed = completed;
            benchRealtimeJob.ExactCount = exact;
            benchRealtimeJob.AvgCer = completed > 0 ? Math.Round(cerSum / completed, 4) : null;
            benchRealtimeJob.Complete(outPath);
        }
        catch (Exception ex)
        {
            benchRealtimeJob.Fail(ex.Message);
        }
        finally { workLease.Dispose(); }
    });

    scheduled = true;
    return Results.Ok(new { code = "bench.realtime_route_started", message = "realtime route bench started", jobId = benchRealtimeJob.JobId });
    }
    finally { if (!scheduled) workLease.Dispose(); }
});

app.MapGet("/api/bench-realtime-route/results", () =>
{
    var snap = benchRealtimeJob.Snapshot();
    if (snap.State != "completed" || string.IsNullOrEmpty(snap.ResultPath))
    {
        return Results.NotFound(ApiMessage("bench.no_completed_results", "no completed results"));
    }
    return Results.Ok(new
    {
        jobId = snap.JobId,
        total = snap.Total,
        completed = snap.Completed,
        exactCount = snap.ExactCount,
        avgCer = snap.AvgCer,
        resultPath = snap.ResultPath,
        options = new { benchRealtimeOptions.route, benchRealtimeOptions.engine, benchRealtimeOptions.language, benchRealtimeOptions.scene, benchRealtimeOptions.prefilter, benchRealtimeOptions.moonshineDllDir, benchRealtimeOptions.moonshineModelRoot, benchRealtimeOptions.moonshineThreads },
    });
});

(string Id, string Name, string Path, string ModelPath, string ConfigPath, string Language, string License, string SourcePageUrl, string Runtime)? ResolvePiperPlusModel(string? modelId)
{
    var models = EnumeratePiperPlusModels().ToArray();
    if (models.Length == 0) return null;
    if (!string.IsNullOrWhiteSpace(modelId))
    {
        var match = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match.Id)) return match;
    }
    return models[0];
}

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
    // Check any direct subdirectory that is a valid MOSS model dir (handles any TtsSubDir name)
    if (Directory.Exists(outer))
    {
        foreach (var sub in Directory.EnumerateDirectories(outer))
            if (MobMate.Core.Tts.MossTtsService.IsModelDirValid(sub)) return sub;
    }
    // Fallback: flat structure (manually placed)
    return MobMate.Core.Tts.MossTtsService.IsModelDirValid(outer) ? outer : null;
}

string[]? ReadMossVoicesFromManifest(string modelDir)
{
    var path = System.IO.Path.Combine(modelDir, "browser_poc_manifest.json");
    if (!File.Exists(path)) return null;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        if (!doc.RootElement.TryGetProperty("builtin_voices", out var arr)) return null;
        var voices = arr.EnumerateArray()
            .Select(v => v.TryGetProperty("voice", out var vp) ? vp.GetString() : null)
            .Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();
        return voices.Length > 0 ? voices : null;
    }
    catch { return null; }
}

string ResolveDefaultPiperPlusExe(TtsConfig config)
{
    if (!string.IsNullOrWhiteSpace(config.PiperPlusExePath)) return config.PiperPlusExePath;
    return Path.Combine(appRoot, "libs", "piper_plus", "PiperPlus.Cli.exe");
}

IEnumerable<(string Id, string Name, string Path, string ModelPath, string ConfigPath, string Language, string License, string SourcePageUrl, string Runtime)> EnumeratePiperPlusModels()
{
    // 1. Subdirectory-style models/piper_plus/<id>/ (Piper-Plus multilingual models)
    var modelRoot = Path.Combine(appRoot, "models", "piper_plus");
    if (Directory.Exists(modelRoot))
    {
        foreach (var dir in Directory.EnumerateDirectories(modelRoot).OrderBy(Path.GetFileName))
        {
            var onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Contains(".cpu.opt.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x).FirstOrDefault();
            if (string.IsNullOrEmpty(onnx)) continue;

            var props = ReadProperties(Path.Combine(dir, "mobmate-model.properties"));
            var folder = Path.GetFileName(dir);
            var id = props.TryGetValue("id", out var idValue) && !string.IsNullOrWhiteSpace(idValue) ? idValue : folder;
            var display = props.TryGetValue("display_name", out var displayValue) && !string.IsNullOrWhiteSpace(displayValue) ? displayValue : id;
            var configPath = Directory.GetFiles(dir, "*.onnx.json", SearchOption.TopDirectoryOnly).OrderBy(x => x).FirstOrDefault()
                ?? Path.Combine(dir, "config.json");
            yield return (
                id,
                display,
                dir,
                onnx,
                File.Exists(configPath) ? configPath : string.Empty,
                props.TryGetValue("language", out var lang) ? lang : string.Empty,
                props.TryGetValue("license", out var license) ? license : string.Empty,
                props.TryGetValue("source_page_url", out var source) ? source : string.Empty,
                InferPiperRuntime(props, id));
        }
    }

    // 2. Flat *.onnx files in libs/piper_models/ (standard single-language piper models)
    var flatDir = Path.Combine(appRoot, "libs", "piper_models");
    if (Directory.Exists(flatDir))
    {
        foreach (var onnx in Directory.GetFiles(flatDir, "*.onnx", SearchOption.TopDirectoryOnly)
            .OrderBy(System.IO.Path.GetFileName))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(onnx);
            var jsonPath = onnx + ".json";
            yield return (
                id,
                id,
                flatDir,
                onnx,
                File.Exists(jsonPath) ? jsonPath : string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                TtsEnginePiper);
        }
    }
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
        // Try nested structure first (any subdir that is a valid model dir)
        string? modelDir = null;
        if (Directory.Exists(outerDir))
        {
            foreach (var sub in Directory.EnumerateDirectories(outerDir))
            {
                if (MobMate.Core.Tts.MossTtsService.IsModelDirValid(sub)) { modelDir = sub; break; }
            }
        }
        // Fallback: flat structure (manually placed)
        if (modelDir == null && MobMate.Core.Tts.MossTtsService.IsModelDirValid(outerDir))
            modelDir = outerDir;
        if (modelDir == null) continue;

        var props = ReadProperties(Path.Combine(outerDir, "mobmate-model.properties"));
        var id = props.TryGetValue("id", out var idV) && !string.IsNullOrWhiteSpace(idV) ? idV : Path.GetFileName(outerDir);
        var name = props.TryGetValue("display_name", out var dV) && !string.IsNullOrWhiteSpace(dV) ? dV : id;
        // Read voices from manifest (most accurate), then from properties, then hardcoded default
        var voices = ReadMossVoicesFromManifest(modelDir)
                     ?? (props.TryGetValue("voices", out var voicesV) && !string.IsNullOrWhiteSpace(voicesV)
                         ? voicesV.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray()
                         : new[] { "Ava", "Adam", "Junhao", "Soyo" });
        yield return new { id, name, path = modelDir, voices };
    }
}

Dictionary<string, string> ReadProperties(string path)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return values;
    foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
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
        ? TtsEnginePiperPlus
        : TtsEnginePiper;
}

/* TTS service - must be declared before realtime endpoints */
var wavOutputStore = new TtsWavOutputStore(Path.Combine(AppUserPaths.V2Root, "wav-output"), loggerFactory.CreateLogger("WavOutput"));
WindowsSapiTtsService ttsService = new WindowsSapiTtsService(wavOutputStore, loggerFactory.CreateLogger("WindowsSapiTts"));
var ttsConfig = LoadAppConfig();
string ttsNativeExe = Path.IsPathFullyQualified(ttsConfig.NativeExePath)
    ? ttsConfig.NativeExePath
    : Path.Combine(appRoot, ttsConfig.NativeExePath);
TtsPlaybackService ttsPlaybackService = new TtsPlaybackService(ttsNativeExe, loggerFactory.CreateLogger("TtsPlayback"));

/* Phase 7.4: Create external TTS engines if configured */
// Always create VOICEVOX service with default URL; router will check availability at runtime
VoicevoxTtsService voicevoxService = new VoicevoxTtsService("http://127.0.0.1:50021", 1, true);

// Piper-Plus service is always created; runtime/model availability is checked dynamically.
var initialTtsConfig = ttsService.GetConfig();
PiperPlusTtsService? piperService = new PiperPlusTtsService(
    ResolveDefaultPiperPlusExe(initialTtsConfig),
    ResolvePiperPlusModel(initialTtsConfig.PiperPlusModelId)?.ModelPath);

// Kokoro TTS service (sherpa-onnx). DLL already loaded above.
var kokoroModelRoot = Path.Combine(appRoot, "models", "kokoro");
var kokoroService = new MobMate.Core.Tts.KokoroTtsService();
var kokoroDlManager = new MobMate.Core.Tts.KokoroModelDownloadManager();
app.Lifetime.ApplicationStopping.Register(() => { kokoroService.Dispose(); kokoroDlManager.Dispose(); });
if (!string.IsNullOrEmpty(initialTtsConfig.KokoroModelId))
{
    var kokoroDir = ResolveKokoroModelDir(kokoroModelRoot, initialTtsConfig.KokoroModelId);
    if (!string.IsNullOrEmpty(kokoroDir))
        kokoroService.SetConfig(kokoroDir, initialTtsConfig.KokoroSpeakerId, initialTtsConfig.KokoroSpeed);
}

// MOSS TTS service (ONNX Runtime) + download manager (Phase 3).
var mossModelRoot = Path.Combine(appRoot, "models", "moss-tts");
var mossService = new MobMate.Core.Tts.MossTtsService();
var mossDlManager = new MobMate.Core.Tts.MossModelDownloadManager();
app.Lifetime.ApplicationStopping.Register(() => { mossService.Dispose(); mossDlManager.Dispose(); });
if (!string.IsNullOrEmpty(initialTtsConfig.MossModelId))
{
    var mossDir = ResolveMossModelDir(mossModelRoot, initialTtsConfig.MossModelId);
    if (!string.IsNullOrEmpty(mossDir))
        mossService.SetConfig(mossDir, initialTtsConfig.MossVoiceId, initialTtsConfig.MossMaxNewFrames);
}

TtsEngineRouter ttsRouter = new TtsEngineRouter(ttsService, ttsPlaybackService, voicevoxService, piperService, kokoroService, mossService, wavOutputStore);
stopOnsetForSession = ttsRouter.StopOnset;
var ttsLogger = loggerFactory.CreateLogger("Tts");
app.Lifetime.ApplicationStopping.Register(() => ttsRouter.DisposeAsync().AsTask().GetAwaiter().GetResult());

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
    var lastFinal = ttsPlaybackService.LastPlaybackFinalDetectedAtMs;
    var lastMs = ttsPlaybackService.LastFinalToPlaybackMs;
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

void WarmUpMossIfSelected(TtsConfig config)
{
    if (!string.Equals(config.Engine, TtsEngineMoss, StringComparison.OrdinalIgnoreCase)) return;
    _ = mossService.WarmUpAsync()
        .ContinueWith(t =>
        {
            if (t.IsFaulted) ttsLogger.LogDebug(t.Exception?.GetBaseException(), "[TTS] MOSS warmup failed");
            else ttsLogger.LogDebug("[TTS] MOSS warmup result={Ok}", t.Result);
        });
}

// 起動時にTTS出力デバイスをnativeに伝達（設定済みの場合）。
// Web UI表示ラベルではなくraw device名をnativeへ渡す。
if (!string.IsNullOrWhiteSpace(initialTtsConfig.OutputDeviceId))
{
    var deviceList = ttsService.EnumerateDevices();
    var rawDevice = deviceList.Devices.FirstOrDefault(d => string.Equals(d.Id, initialTtsConfig.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
    if (rawDevice != null)
        initialTtsConfig.OutputDeviceName = rawDevice.Name;
}
if (!string.IsNullOrEmpty(initialTtsConfig.OutputDeviceName))
    NativeLibraryBridge.SetTtsOutputDevice(ttsNativeExe, initialTtsConfig.OutputDeviceName, initialTtsConfig.OutputDeviceId);

// 起動時に保存済みTTS configをrouterへ適用する。
// VOICEVOX availability はrouter内で背景チェックされるため、起動をブロックしない。
_ = Task.Run(() =>
{
    try
    {
        ttsRouter.SetConfig(initialTtsConfig);
        WarmUpMossIfSelected(initialTtsConfig);
        ttsLogger.LogInformation("[TTS] startup SetConfig applied: effectiveEngine={Engine}",
            ttsRouter.GetStatus().EffectiveEngine);
    }
    catch (Exception ex)
    {
        ttsLogger.LogWarning(ex, "[TTS] startup SetConfig failed");
    }
});

// [TTS診断] 起動時のサーバー保存TTS config。engine=windows_sapi / voicevoxApi空 ならサーバー側に
// ユーザー設定が永続化されておらず、VOICEVOX化は Web UI の /api/tts/config POST 依存であることを示す。
ttsLogger.LogInformation(
    "[TTS] startup config: engine={Engine} monitorVol={MonVol} outDevId={Id} outDevName={Name} voicevoxApi={Api}",
    initialTtsConfig.Engine, initialTtsConfig.MonitorVolumePercent, initialTtsConfig.OutputDeviceId, initialTtsConfig.OutputDeviceName, initialTtsConfig.VoicevoxApi);

// Rich TTS (Kokoro) voice catalog path
var richVoiceCatalogPath = Path.Combine(appRoot, "libs", "preset", "rich_tts_catalog.json");
MobMate.Core.Tts.RichVoiceEntry[] LoadRichVoiceCatalog() => MobMate.Core.Tts.RichVoiceCatalog.Load(richVoiceCatalogPath);

// Piper voice catalog + download manager
var piperVoiceCatalogPath = Path.Combine(appRoot, "libs", "preset", "piper_voice_catalog.json");
var piperVoiceCatalogCachePath = Path.Combine(AppUserPaths.ConfigDir, "piper_voice_catalog.json");
var piperVoiceCatalogRemoteUrl = Environment.GetEnvironmentVariable("MOBMATE_PIPER_CATALOG_URL") ?? string.Empty;
var piperVoiceModelRoot = Path.Combine(appRoot, "models", "piper_plus");
var piperVoiceDlManager = new MobMate.Core.Tts.PiperVoiceDownloadManager();
var piperVoiceCatalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
app.Lifetime.ApplicationStopping.Register(() => piperVoiceDlManager.Dispose());
app.Lifetime.ApplicationStopping.Register(() => piperVoiceCatalogHttp.Dispose());

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
var mobfyOnset = new MobfyOnsetFiller(ttsRouter, ttsNativeExe, voiceConversionSessionLock,
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
mobfyOnsetSettingsChanged = (before, after) =>
{
    if (!MobfyOnsetSelection.Changed(before, after)) return;
    mobfyOnset.Invalidate();
    PrepareMobfyOnset(before.Settings.GetValueOrDefault("mobmate.realtimeLanguage") != after.Settings.GetValueOrDefault("mobmate.realtimeLanguage")
        ? MobfyOnsetSelection.Language(after) : null);
};
app.Lifetime.ApplicationStopping.Register(mobfyOnset.Dispose);
long voiceConversionOperationId = 0;
DateTimeOffset voiceConversionLastDiagLog = DateTimeOffset.MinValue;
app.Lifetime.ApplicationStopping.Register(() =>
{
    lock (voiceConversionSessionLock)
    {
        voiceConversionLogger.LogInformation("[VC] action=shutdown_destroy begin handleValid={HandleValid}", voiceConversionSessionHandle != IntPtr.Zero);
        mobfyOnset.Stop();
        NativeLibraryBridge.VoiceConversionDestroy(voiceConversionSessionHandle);
        voiceConversionSessionHandle = IntPtr.Zero;
        voiceConversionLogger.LogInformation("[VC] action=shutdown_destroy ok");
    }
});

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

        obj["wavOutputEnabled"] = ttsRouter.GetConfig().WavOutputEnabled;
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
            InjectVadPaths(obj);
            if (ensureAudibleLiveOutput) EnsureAudibleOutputVolume(obj);
        }
        else
        {
            // Default mode keeps the existing DSP/VAD path unchanged.
            InjectVadPaths(obj);
            InjectLaughAsrAssist(obj);
            if (ensureAudibleLiveOutput) EnsureAudibleOutputVolume(obj);
        }

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

    void InjectVadPaths(JsonObject obj)
    {
        if (obj["sileroVadModelPath"] is null)
            obj["sileroVadModelPath"] = Path.Combine(appRoot, "models", "silero_vad", "silero_vad_int8.onnx");
        if (obj["onnxRuntimeDllDir"] is null)
            obj["onnxRuntimeDllDir"] = Path.Combine(appRoot, "libs", "onnxruntime");
        // Speech Enhancement (DTLN): inject path if models exist
        var seModelDir = Path.Combine(appRoot, "models", "dtln");
        if (obj["seModelDir"] is null && Directory.Exists(seModelDir))
            obj["seModelDir"] = seModelDir;
    }

    void InjectLaughAsrAssist(JsonObject obj)
    {
        var cfg = LoadAppConfig();
        if (obj["moonshineDllDir"] is null)
            obj["moonshineDllDir"] = cfg.DefaultMoonshineDllDir;
        if (obj["moonshineModelRoot"] is null)
            obj["moonshineModelRoot"] = cfg.DefaultMoonshineModelRoot;
        if (obj["laughAsrLanguage"] is null)
            obj["laughAsrLanguage"] = NormalizeMoonshineLang(textFilterConfig.UiLanguage);
        var existingLaughAsrTokens = obj["laughAsrTokens"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(existingLaughAsrTokens))
        {
            var tokens = ResolveTextFilterTokens(textFilterConfig);
            obj["laughAsrTokens"] = string.Join(',', new[] { tokens.Manual, tokens.Auto }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    static string NormalizeMoonshineLang(string? lang)
    {
        var s = (lang ?? "ja").ToLowerInvariant().Replace('-', '_');
        if (s.StartsWith("zh")) return "zh";
        if (s.StartsWith("ko")) return "ko";
        if (s.StartsWith("en")) return "en";
        return "ja";
    }

    static void EnsureAudibleOutputVolume(JsonObject obj)
    {
        const double mobfyMinimumAudibleOutputVolumePercent = 140.0;
        var volume = 100.0;
        try { volume = obj["outputVolumePercent"]?.GetValue<double>() ?? 100.0; } catch { }
        if (!double.IsFinite(volume) || volume < mobfyMinimumAudibleOutputVolumePercent)
            obj["outputVolumePercent"] = mobfyMinimumAudibleOutputVolumePercent;
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

int VoiceConversionNativeSampleRate(IntPtr handle, int fallback)
{
    if (handle == IntPtr.Zero) return fallback;
    try
    {
        var status = NativeLibraryBridge.VoiceConversionPollStatus(handle);
        if (!status.Success || string.IsNullOrWhiteSpace(status.RawJson)) return fallback;
        using var doc = JsonDocument.Parse(status.RawJson);
        if (doc.RootElement.TryGetProperty("sampleRate", out var sr) && sr.TryGetInt32(out var value) && value > 0)
            return value;
    }
    catch { }
    return fallback;
}

static short[] ResamplePcm16(short[] input, int inputRate, int outputRate, int? outputLength = null)
{
    if (input.Length == 0) return Array.Empty<short>();
    if (inputRate <= 0 || outputRate <= 0 || inputRate == outputRate)
    {
        if (outputLength is null || outputLength.Value == input.Length) return input;
    }
    int outLen = outputLength ?? Math.Max(1, (int)Math.Round(input.Length * (double)outputRate / Math.Max(1, inputRate)));
    var output = new short[outLen];
    double ratio = input.Length > 1 && outLen > 1 ? (double)(input.Length - 1) / (outLen - 1) : 0.0;
    for (int i = 0; i < outLen; i++)
    {
        double src = i * ratio;
        int i0 = (int)src;
        int i1 = Math.Min(i0 + 1, input.Length - 1);
        double frac = src - i0;
        output[i] = (short)Math.Clamp((int)Math.Round(input[i0] * (1.0 - frac) + input[i1] * frac), short.MinValue, short.MaxValue);
    }
    return output;
}

static (int Start, int Length) FindPcm16ActiveRange(short[] input, int sampleRate)
{
    if (input.Length == 0) return (0, 0);
    int peak = 0;
    for (int i = 0; i < input.Length; i++) peak = Math.Max(peak, Math.Abs((int)input[i]));
    if (peak <= 0) return (0, input.Length);

    int threshold = Math.Max(96, peak / 50); // about -50 dBFS or 2% peak
    int start = 0;
    while (start < input.Length && Math.Abs((int)input[start]) < threshold) start++;
    if (start >= input.Length) return (0, input.Length);
    int end = input.Length - 1;
    while (end > start && Math.Abs((int)input[end]) < threshold) end--;

    int pad = Math.Max(1, sampleRate / 10); // 100 ms context, close to live VAD hold behavior
    start = Math.Max(0, start - pad);
    end = Math.Min(input.Length - 1, end + pad);
    return (start, end - start + 1);
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
var pendingSpeechLogger = loggerFactory.CreateLogger("PendingSpeech");
var pendingSpeechService = new PendingSpeechService(appRoot, pendingSpeechLogger);
app.Lifetime.ApplicationStopping.Register(pendingSpeechService.Dispose);
var radioChatLogger = loggerFactory.CreateLogger("RadioChat");
var radioChatService = new RadioChatService(appRoot, initialUiLanguage, radioChatLogger);
app.Lifetime.ApplicationStopping.Register(radioChatService.Dispose);
radioChatService.SetPendingStatusProvider(() => pendingSpeechService.GetStatus());
radioChatService.SetSpeakHandler(async (page, digit) =>
{
    if (page == -1)
    {
        if (digit == 5)
        {
            await pendingSpeechService.CancelFirstAsync(CancellationToken.None);
            return true;
        }
        await pendingSpeechService.ApproveBySlotAsync(digit, (text, token) => SpeakDirectAsync(text, token, null), CancellationToken.None);
        return true;
    }
    return await radioChatService.SpeakSlotAsync(page, digit, (text, token) => SpeakDirectAsync(text, token, null), CancellationToken.None);
});

// SenseVoice ASR サービス (sense_voice エンジン + joint_auto ZH/JA/KO 対応)
var senseVoiceModelDir = Path.Combine(appRoot, "models", "sense_voice");
senseVoiceLogger = loggerFactory.CreateLogger("SenseVoice");
try
{
    senseVoiceService = new SenseVoiceAsrService(senseVoiceModelDir, senseVoiceLogger);
    if (senseVoiceService.IsAvailable)
        app.Lifetime.ApplicationStopping.Register(senseVoiceService.Dispose);
}
catch (Exception ex)
{
    senseVoiceLogger.LogWarning(ex, "[SenseVoice] サービス初期化失敗");
}

// Phoneme CTC ASR (PhoneticAssist): モデル同梱時のみ有効化。初期はDebugログ用途のみ。
var phonemeCtcModelDir = Path.Combine(appRoot, "models", "phoneme_ctc");
phonemeCtcLogger = loggerFactory.CreateLogger("PhonemeCtc");
try
{
    if (File.Exists(Path.Combine(phonemeCtcModelDir, "model.int8.onnx"))
        && File.Exists(Path.Combine(phonemeCtcModelDir, "id2token.json")))
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
    phonemeCtcLogger.LogWarning(ex, "[PhonemeCtc] サービス初期化失敗");
}

// === Hearing Service ===
var hearingLogger = loggerFactory.CreateLogger("Hearing");
HearingService hearingService;
{
    var hearingConfig = LoadConfigWithSelectedWhisperModel();
    var hearingWhisperRuntime = AppConfigLoader.ResolveWhisperRuntime(hearingConfig, appRoot);
    var hearingNativeExe = Path.IsPathFullyQualified(hearingConfig.NativeExePath)
        ? hearingConfig.NativeExePath
        : Path.Combine(appRoot, hearingConfig.NativeExePath);
    hearingService = new HearingService(
        nativeExePath:    hearingNativeExe,
        whisperModelPath: selectedWhisperModelPath,
        whisperDllDir:    hearingWhisperRuntime.WhisperDllDir,
        whisperThreads:       Math.Max(1, hearingConfig.DefaultThreads),
        senseVoice:           senseVoiceService,
        translation:          translationService,
        logger:               hearingLogger,
        whisperUseGpu:        hearingWhisperRuntime.UseGpu,
        sileroVadModelPath:   Path.Combine(appRoot, "models", "silero_vad", "silero_vad_int8.onnx"),
        onnxRuntimeDllDir:    Path.Combine(appRoot, "libs", "onnxruntime"),
        addonOperationGate:   addonWorkGate
    );
    hearingService.SetConfig(LoadHearingConfig());
    void CleanupHearing()
    {
        SubtitleOverlayService.Close();
        hearingService.Stop();
        hearingService.Dispose();
    }
    app.Lifetime.ApplicationStopping.Register(CleanupHearing);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupHearing();
}

static bool IsJointAutoZh(string? engine, string? lang) =>
    string.Equals(engine, "joint_auto", StringComparison.OrdinalIgnoreCase)
    && string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase);

static bool IsSafeAuto(string? engine) =>
    string.Equals(engine, "safe_auto", StringComparison.OrdinalIgnoreCase);

static float[] PcmToFloat32(byte[] pcm16le)
{
    var samples = new float[pcm16le.Length / 2];
    for (int i = 0; i < samples.Length; i++)
        samples[i] = BitConverter.ToInt16(pcm16le, i * 2) / 32768.0f;
    return samples;
}

static NativeLibraryResult RunSenseVoiceManifestBench(
    SenseVoiceAsrService? senseVoice,
    string manifestPath,
    string outPath,
    string language,
    ILogger logger)
{
    if (senseVoice == null || !senseVoice.IsAvailable)
        return new NativeLibraryResult { Success = false, Message = "SenseVoice is not available" };
    if (!File.Exists(manifestPath))
        return new NativeLibraryResult { Success = false, Message = $"Manifest not found: {manifestPath}" };

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
        foreach (var row in ReadBenchManifestRows(manifestPath))
        {
            var sw = Stopwatch.StartNew();
            string? text = null;
            string? error = null;
            try
            {
                var (samples, sampleRate) = ReadPcm16WavAsFloat(row.Wav);
                text = senseVoice.Transcribe(samples, sampleRate, language == "auto" ? "ja" : language) ?? string.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "[BenchRealtime/SenseVoice] failed: {Wav}", row.Wav);
            }
            sw.Stop();

            var cer = BestCer(row.Expected, row.Aliases, text ?? string.Empty);
            var exact = cer <= 0.000001;
            var item = new Dictionary<string, object?>
            {
                ["file"] = row.Wav,
                ["expected"] = row.Expected,
                ["text"] = text ?? string.Empty,
                ["transcript"] = text ?? string.Empty,
                ["selected_text"] = text ?? string.Empty,
                ["cer"] = cer,
                ["exact"] = exact,
                ["selected_engine"] = "sense_voice",
                ["decision_reason"] = "single_engine_managed",
                ["latency_ms"] = sw.ElapsedMilliseconds,
            };
            if (!string.IsNullOrEmpty(error)) item["error"] = error;
            writer.WriteLine(JsonSerializer.Serialize(item));
        }
        return new NativeLibraryResult { Success = true, Message = "SenseVoice manifest bench completed" };
    }
    catch (Exception ex)
    {
        return new NativeLibraryResult { Success = false, Message = ex.Message };
    }
}

static IEnumerable<(string Wav, string Expected, string Aliases)> ReadBenchManifestRows(string path)
{
    var lines = File.ReadAllLines(path, Encoding.UTF8);
    for (int i = 1; i < lines.Length; i++)
    {
        if (string.IsNullOrWhiteSpace(lines[i])) continue;
        var cols = ParseCsvLine(lines[i]);
        if (cols.Count < 2) continue;
        yield return (cols[0], cols[1], cols.Count >= 5 ? cols[4] : string.Empty);
    }
}

static List<string> ParseCsvLine(string line)
{
    var cols = new List<string>();
    var sb = new StringBuilder();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else
            {
                quoted = !quoted;
            }
        }
        else if (c == ',' && !quoted)
        {
            cols.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(c);
        }
    }
    cols.Add(sb.ToString());
    return cols;
}

static (float[] Samples, int SampleRate) ReadPcm16WavAsFloat(string path)
{
    using var fs = File.OpenRead(path);
    using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not RIFF wav");
    br.ReadInt32();
    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not WAVE wav");

    short channels = 0;
    int sampleRate = 0;
    short bitsPerSample = 0;
    byte[]? data = null;
    while (fs.Position + 8 <= fs.Length)
    {
        var id = Encoding.ASCII.GetString(br.ReadBytes(4));
        var size = br.ReadInt32();
        var next = fs.Position + size + (size % 2);
        if (id == "fmt ")
        {
            var format = br.ReadInt16();
            channels = br.ReadInt16();
            sampleRate = br.ReadInt32();
            br.ReadInt32();
            br.ReadInt16();
            bitsPerSample = br.ReadInt16();
            if (format != 1 || bitsPerSample != 16) throw new InvalidDataException("only PCM16 wav is supported");
        }
        else if (id == "data")
        {
            data = br.ReadBytes(size);
        }
        fs.Position = Math.Min(next, fs.Length);
    }
    if (data == null || channels <= 0 || sampleRate <= 0 || bitsPerSample != 16) throw new InvalidDataException("invalid wav");

    var frameCount = data.Length / (2 * channels);
    var samples = new float[frameCount];
    for (int i = 0; i < frameCount; i++)
    {
        var sum = 0.0f;
        for (int ch = 0; ch < channels; ch++)
            sum += BitConverter.ToInt16(data, (i * channels + ch) * 2) / 32768.0f;
        samples[i] = sum / channels;
    }
    return (samples, sampleRate);
}

static string NormalizeCerText(string text)
{
    var normalized = text.Normalize(NormalizationForm.FormKC);
    foreach (var ch in new[] { "、", "。", "，", "．", ",", ".", "!", "?", "！", "？", " ", "　", "\t", "\r", "\n" })
        normalized = normalized.Replace(ch, string.Empty);
    return normalized.Trim();
}

static double CharErrorRate(string reference, string hypothesis)
{
    var r = NormalizeCerText(reference);
    var h = NormalizeCerText(hypothesis);
    if (r.Length == 0) return h.Length == 0 ? 0.0 : 1.0;
    var prev = Enumerable.Range(0, h.Length + 1).ToArray();
    for (int i = 1; i <= r.Length; i++)
    {
        var cur = new int[h.Length + 1];
        cur[0] = i;
        for (int j = 1; j <= h.Length; j++)
        {
            var cost = r[i - 1] == h[j - 1] ? 0 : 1;
            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
        }
        prev = cur;
    }
    return (double)prev[h.Length] / r.Length;
}

static double BestCer(string expected, string aliases, string hypothesis)
{
    var best = CharErrorRate(expected, hypothesis);
    foreach (var alias in (aliases ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        best = Math.Min(best, CharErrorRate(alias, hypothesis));
    return best;
}

static PhoneticAssistLanguage ResolvePhoneticAssistLanguage(string? lang)
{
    if (lang != null && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.Chinese;
    if (lang != null && lang.Equals("ko", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.Korean;
    if (lang != null && lang.Equals("en", StringComparison.OrdinalIgnoreCase)) return PhoneticAssistLanguage.English;
    return PhoneticAssistLanguage.Japanese;
}

// Demo mode: 15-minute full-feature session limit (replaces the old 3-minute repeating announcement).
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
    // 残り2分の警告アナウンス（UI言語1言語のみ・1回のみ）
    var demoWarningMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "体験版のセッションは、残り2分で終了します。",
        ["en"] = "This trial session ends in two minutes.",
        ["ko"] = "체험판 세션이 2분 후에 종료됩니다.",
        ["zh_cn"] = "试用会话将在2分钟后结束。",
        ["zh_tw"] = "試用工作階段將在2分鐘後結束。",
    };
    string demoWarningMsg = demoWarningMessages.TryGetValue(initialUiLanguage, out var dw) ? dw : demoWarningMessages["en"];
    demoWarningTimer = new System.Threading.Timer(_ =>
    {
        demoLogger.LogInformation("[Demo] session warning: 2 minutes remaining");
        _ = ttsRouter.SpeakAsync(demoWarningMsg, null, null, true, CancellationToken.None, null);
    }, null, TimeSpan.FromMinutes(DemoSessionWarningMinutes), System.Threading.Timeout.InfiniteTimeSpan);

    // 15分経過: 期限切れフラグを立て、稼働中のセッションを停止する
    demoExpireTimer = new System.Threading.Timer(_ =>
    {
        lock (demoSessionLock) { demoSessionExpired = true; }
        demoLogger.LogInformation("[Demo] session expired: blocking start APIs and stopping running sessions");
        try
        {
            lock (realtimeLock)
            {
                try { realtimeSessionService.Stop(); }
                finally
                {
                    realtimeSession = null;
                    realtimeWorkLease?.Dispose();
                    realtimeWorkLease = null;
                }
            }
        }
        catch (Exception ex)
        {
            demoLogger.LogWarning(ex, "[Demo] realtime stop on expiry failed");
        }
        try
        {
            SubtitleOverlayService.Close();
            hearingService.Stop();
        }
        catch (Exception ex)
        {
            demoLogger.LogWarning(ex, "[Demo] hearing stop on expiry failed");
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

object virtualAudioInstallLock = new object();
bool virtualAudioInstallRunning = false;
DateTimeOffset? virtualAudioInstallStartedAt = null;
DateTimeOffset? virtualAudioInstallFinishedAt = null;
int? virtualAudioInstallExitCode = null;
string? virtualAudioInstallError = null;
string virtualAudioInstallLastMessage = string.Empty;

void UpdateVirtualAudioInstallMessage(string? line)
{
    if (string.IsNullOrWhiteSpace(line)) return;
    var text = line.Trim();
    lock (virtualAudioInstallLock)
    {
        if (text.StartsWith("DOWNLOAD_PROGRESS ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && long.TryParse(parts[1], out var received) && long.TryParse(parts[2], out var total))
            {
                var receivedMb = received / 1024d / 1024d;
                if (total > 0)
                {
                    var totalMb = total / 1024d / 1024d;
                    var pct = Math.Clamp(received / (double)total * 100d, 0d, 100d);
                    virtualAudioInstallLastMessage = $"Downloading VB-CABLE... {pct:0.0}% ({receivedMb:0.0}/{totalMb:0.0} MB)";
                }
                else
                {
                    virtualAudioInstallLastMessage = $"Downloading VB-CABLE... {receivedMb:0.0} MB";
                }
            }
        }
        else if (text.StartsWith("DOWNLOAD_START", StringComparison.OrdinalIgnoreCase)) virtualAudioInstallLastMessage = "Downloading VB-CABLE...";
        else if (text.StartsWith("EXTRACT_START", StringComparison.OrdinalIgnoreCase)) virtualAudioInstallLastMessage = "Extracting VB-CABLE installer...";
        else if (text.StartsWith("INSTALLER_START", StringComparison.OrdinalIgnoreCase)) virtualAudioInstallLastMessage = "Starting VB-CABLE installer...";
        else if (text.StartsWith("UAC_PROMPT_MAY_APPEAR", StringComparison.OrdinalIgnoreCase)) virtualAudioInstallLastMessage = "Waiting for installer permission...";
        else if (text.StartsWith("INSTALLER_EXIT", StringComparison.OrdinalIgnoreCase)) virtualAudioInstallLastMessage = text;
        else virtualAudioInstallLastMessage = text;
    }
}

TtsDeviceInfo[] GetVirtualAudioCandidates()
{
    var list = ttsService.EnumerateDevices();
    if (!list.Ok) return Array.Empty<TtsDeviceInfo>();
    return list.Devices
        .Where(d => LooksLikeVirtualAudioDeviceName(d.Name) || LooksLikeVirtualAudioDeviceName(d.FriendlyName) || LooksLikeVirtualAudioDeviceName(d.TokenId))
        .OrderBy(VirtualAudioDeviceScore)
        .ThenBy(d => d.Name)
        .ToArray();
}

object VirtualAudioStatusResponse()
{
    var candidates = GetVirtualAudioCandidates();
    var preferred = candidates.FirstOrDefault();
    var detected = preferred != null;
    return new
    {
        ok = true,
        detected,
        preferredOutputDeviceId = preferred?.Id ?? string.Empty,
        preferredOutputDeviceName = preferred?.Name ?? string.Empty,
        candidates = candidates.Select(d => new { id = d.Id, name = d.Name, friendlyName = d.FriendlyName, tokenId = d.TokenId, isDefault = d.IsDefault }),
        officialUrl = VirtualAudioOfficialUrl,
        code = detected ? "virtual_audio.detected" : "virtual_audio.not_detected",
        message = detected ? "virtual audio device detected" : "virtual audio device not detected"
    };
}

string? ResolveVirtualAudioInstaller()
{
    var packagePath = Path.Combine(appRoot, "ps1", "InstallVirtualAudio.bat");
    if (File.Exists(packagePath)) return packagePath;
    var toolPath = Path.Combine(appRoot, "tools", "InstallVirtualAudio.bat");
    if (File.Exists(toolPath)) return toolPath;
    return null;
}

object VirtualAudioInstallStateResponse()
{
    lock (virtualAudioInstallLock)
    {
        return new
        {
            ok = true,
            install = new
            {
                running = virtualAudioInstallRunning,
                startedAt = virtualAudioInstallStartedAt?.ToString("O"),
                finishedAt = virtualAudioInstallFinishedAt?.ToString("O"),
                exitCode = virtualAudioInstallExitCode,
                error = virtualAudioInstallError,
                lastMessage = virtualAudioInstallLastMessage
            },
            virtualAudio = VirtualAudioStatusResponse()
        };
    }
}

app.MapGet("/api/virtual-audio/status", () => Results.Ok(VirtualAudioStatusResponse()));

app.MapGet("/api/virtual-audio/install-status", () => Results.Ok(VirtualAudioInstallStateResponse()));

app.MapPost("/api/virtual-audio/install", () =>
{
    var installer = ResolveVirtualAudioInstaller();
    if (installer == null) return Results.NotFound(ApiMessage("virtual_audio.installer_missing", "InstallVirtualAudio.bat not found"));
    lock (virtualAudioInstallLock)
    {
        if (virtualAudioInstallRunning) return Results.Conflict(ApiMessage("virtual_audio.install_running", "VB-CABLE installer is already running"));
        virtualAudioInstallRunning = true;
        virtualAudioInstallStartedAt = DateTimeOffset.UtcNow;
        virtualAudioInstallFinishedAt = null;
        virtualAudioInstallExitCode = null;
        virtualAudioInstallError = null;
        virtualAudioInstallLastMessage = "VB-CABLE installer started";
    }

    _ = Task.Run(() =>
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{installer}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(installer) ?? appRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("failed to start installer");
            var outputLines = new List<string>();
            var errorLines = new List<string>();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { outputLines.Add(e.Data); UpdateVirtualAudioInstallMessage(e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) { errorLines.Add(e.Data); UpdateVirtualAudioInstallMessage(e.Data); } };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            proc.WaitForExit();
            lock (virtualAudioInstallLock)
            {
                virtualAudioInstallExitCode = proc.ExitCode;
                var error = string.Join(Environment.NewLine, errorLines).Trim();
                virtualAudioInstallError = string.IsNullOrWhiteSpace(error) ? null : error;
                virtualAudioInstallLastMessage = proc.ExitCode == 0 ? "VB-CABLE installer finished" : $"VB-CABLE installer exited with code={proc.ExitCode}";
                var lastOutput = outputLines.LastOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
                if (!string.IsNullOrWhiteSpace(lastOutput)) virtualAudioInstallLastMessage += $": {lastOutput}";
            }
        }
        catch (Exception ex)
        {
            lock (virtualAudioInstallLock)
            {
                virtualAudioInstallError = ex.Message;
                virtualAudioInstallLastMessage = ex.Message;
            }
        }
        finally
        {
            lock (virtualAudioInstallLock)
            {
                virtualAudioInstallRunning = false;
                virtualAudioInstallFinishedAt = DateTimeOffset.UtcNow;
            }
        }
    });

    return Results.Ok(new { ok = true, running = true, code = "virtual_audio.install_started", message = "VB-CABLE installer started" });
});

app.MapPost("/api/virtual-audio/apply", async (HttpRequest request) =>
{
    var deviceId = string.Empty;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("deviceId", out var idProp)) deviceId = idProp.GetString() ?? string.Empty;
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    var candidates = GetVirtualAudioCandidates();
    var selected = !string.IsNullOrWhiteSpace(deviceId)
        ? candidates.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
        : candidates.FirstOrDefault();
    if (selected == null) return Results.NotFound(ApiMessage("virtual_audio.not_detected", "virtual audio device not detected"));

    var config = ttsService.GetConfig();
    config.OutputDeviceId = selected.Id;
    config.OutputDeviceName = selected.Name;
    using var onsetRouteChange = ttsRouter.BeginOnsetRouteChange();
    ttsRouter.SetConfig(config);
    NativeLibraryBridge.SetTtsOutputDevice(ttsNativeExe, selected.Name, selected.Id);
    UnlockSteamAchievement(SteamAchievementService.VirtualAudioReady);
    UnlockSteamAchievement(SteamAchievementService.SetupComplete);
    return Results.Ok(new { ok = true, code = "virtual_audio.output_applied", message = "virtual audio output selected", outputDeviceId = selected.Id, outputDeviceName = selected.Name });
});

app.MapPost("/api/virtual-audio/open-sound-settings", () =>
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "mmsys.cpl sounds") { UseShellExecute = true });
        return Results.Ok(new { ok = true, code = "virtual_audio.sound_settings_opened", message = "sound settings opened" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("virtual_audio.sound_settings_failed"));
    }
});

app.MapPost("/api/external/discord-voice-settings", () =>
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.gg/nrXFheFWqP") { UseShellExecute = true });
        UnlockSteamAchievement(SteamAchievementService.FirstDiscordGuide);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("discord.open_failed"));
    }
});

// ── Review prompt (P2: polite Steam review request, full version only) ───
var reviewPromptLogger = loggerFactory.CreateLogger("ReviewPrompt");
string ReviewPromptStatePath() => Path.Combine(AppUserPaths.ConfigDir, "review_prompt.json");
ReviewPromptState LoadReviewPromptState()
{
    var path = ReviewPromptStatePath();
    if (!File.Exists(path)) return new ReviewPromptState();
    try
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<ReviewPromptState>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ReviewPromptState();
    }
    catch { return new ReviewPromptState(); }
}
void SaveReviewPromptState(ReviewPromptState state)
{
    Directory.CreateDirectory(AppUserPaths.ConfigDir);
    var json = JsonSerializer.Serialize(state,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    File.WriteAllText(ReviewPromptStatePath(), json, Encoding.UTF8);
}

object reviewPromptLock = new object();
var reviewPromptState = LoadReviewPromptState();
int reviewSessionAsrSuccessCount = 0;
string reviewLastCountedFinalText = string.Empty;
System.Threading.Timer? reviewUsageTimer = null;

if (!MobMateCore.IsDemo)
{
    lock (reviewPromptLock)
    {
        reviewPromptState.LaunchCount++;
        try { SaveReviewPromptState(reviewPromptState); }
        catch (Exception ex) { reviewPromptLogger.LogWarning(ex, "[ReviewPrompt] launch count save failed"); }
    }
    reviewPromptLogger.LogInformation("[ReviewPrompt] launchCount={Count} totalUsageMinutes={Minutes} promptCount={Prompts}",
        reviewPromptState.LaunchCount, reviewPromptState.TotalUsageMinutes, reviewPromptState.PromptCount);

    // アプリ起動中は5分毎に累計利用時間を加算する（簡易方式）
    reviewUsageTimer = new System.Threading.Timer(_ =>
    {
        lock (reviewPromptLock)
        {
            reviewPromptState.TotalUsageMinutes += 5;
            try { SaveReviewPromptState(reviewPromptState); }
            catch (Exception ex) { reviewPromptLogger.LogWarning(ex, "[ReviewPrompt] usage save failed"); }
        }
    }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    app.Lifetime.ApplicationStopping.Register(() => reviewUsageTimer?.Dispose());
}

bool ShouldShowReviewPrompt()
{
    if (MobMateCore.IsDemo) return false;
    lock (reviewPromptLock)
    {
        if (reviewPromptState.DismissedForever) return false;
        if (reviewPromptState.PromptCount >= 3) return false;
        if (reviewPromptState.LaunchCount < 5) return false;
        if (reviewPromptState.TotalUsageMinutes < 180) return false;
        if (reviewSessionAsrSuccessCount < 10) return false;
        if (!string.IsNullOrEmpty(reviewPromptState.LastPromptUtc)
            && DateTime.TryParse(reviewPromptState.LastPromptUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var lastPrompt)
            && (DateTime.UtcNow - lastPrompt).TotalDays < 30)
        {
            return false;
        }
        return true;
    }
}

app.MapGet("/api/review-prompt/status", () => Results.Ok(new { shouldShow = ShouldShowReviewPrompt() }));

app.MapPost("/api/review-prompt/ack", async (HttpRequest request) =>
{
    string action = string.Empty;
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        if (body.RootElement.TryGetProperty("action", out var actionProp))
            action = (actionProp.GetString() ?? string.Empty).Trim().ToLowerInvariant();
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
    if (action is not ("review" or "later" or "never"))
        return Results.BadRequest(ApiMessage("review_prompt.invalid_action", "action must be review, later, or never"));
    lock (reviewPromptLock)
    {
        reviewPromptState.PromptCount++;
        reviewPromptState.LastPromptUtc = DateTime.UtcNow.ToString("O");
        if (action == "never") reviewPromptState.DismissedForever = true;
        try { SaveReviewPromptState(reviewPromptState); }
        catch (Exception ex) { reviewPromptLogger.LogWarning(ex, "[ReviewPrompt] ack save failed"); }
    }
    reviewPromptLogger.LogInformation("[ReviewPrompt] ack action={Action}", action);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/external/steam-review", () =>
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://url/StoreAppPage/4261090") { UseShellExecute = true });
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("steam.open_failed"));
    }
});

// ── Translation API ──────────────────────────────────────────────────────
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
    // "OFF" は常に許可。それ以外は supported langs のみ。
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

app.MapGet("/api/translation/languages", () =>
{
    var langs = new List<object>
    {
        new { code = "OFF", label = "Off (OFF)" }
    };
    foreach (var code in LanguageOptionsTranslation.TranslationLangs)
        langs.Add(new { code, label = LanguageOptionsTranslation.DisplayTranslationTarget(code) });
    return Results.Ok(langs);
});

// ========================================================
// Hearing API
// ========================================================

app.MapGet("/api/hearing/config", () => Results.Ok(hearingService.GetConfig()));

app.MapPost("/api/hearing/config", async (HttpRequest req) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<HearingConfig>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body == null) return Results.BadRequest("Invalid config body");
        hearingService.SetConfig(body);
        var saved = hearingService.GetConfig();
        SaveHearingConfig(saved);
        return Results.Ok(saved);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/hearing/status", () => Results.Ok(hearingService.GetStatus()));

app.MapPost("/api/hearing/start", () =>
{
    if (IsDemoSessionExpired())
    {
        return DemoSessionExpiredResult();
    }
    var lease = addonWorkGate.TryEnterWork();
    if (lease is null) return Results.Conflict(ApiMessage("addon.busy", "Addon operation is running"));
    try
    {
        hearingService.Start(lease);
        UnlockSteamAchievement(SteamAchievementService.FirstHearingStart);
        return Results.Ok(hearingService.GetStatus());
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/hearing/stop", () =>
{
    hearingService.Stop();
    return Results.Ok(hearingService.GetStatus());
});

app.MapGet("/api/hearing/history", (int? max) =>
    Results.Ok(hearingService.GetHistory(max ?? 20)));

app.MapGet("/api/hearing/languages", () =>
{
    var asrLangs = GetRealtimeLanguageCodes(RealtimeEngineWhisper)
        .Select(code => new
        {
            code,
            label = whisperLanguageLabels.TryGetValue(code, out var l) ? l : code,
        });

    var translationLangs = LanguageOptionsTranslation.TranslationLangs
        .Select(code => new
        {
            code,
            label = LanguageOptionsTranslation.DisplayTranslationTarget(code),
        });

    return Results.Ok(new { asrLangs, translationLangs });
});

// ---- 字幕オーバーレイ (WPF透明ウィンドウ) ----
var subtitlePosSavePath = Path.Combine(AppUserPaths.ConfigDir, "subtitle_pos.json");
app.MapPost("/api/hearing/subtitle-open", () =>
{
    SubtitleOverlayService.Open(hearingService, subtitlePosSavePath);
    return Results.Ok();
});

app.MapPost("/api/hearing/subtitle-close", () =>
{
    SubtitleOverlayService.Close();
    return Results.Ok();
});

// ========================================================
// VRChat OSC API
// ========================================================

app.MapGet("/api/vrchat-osc/config", () => Results.Ok(vrchatOscService.GetConfig()));

app.MapPost("/api/vrchat-osc/config", async (HttpRequest req) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<VrchatOscConfig>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
            body = await JsonSerializer.DeserializeAsync<VrchatOscTestRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

bool realtimeTtsEnabled = false;
bool onsetExplicitInput = false;
int onsetInputChanged = 0;
bool onsetIdleKnown = true;
long onsetControlGeneration = 0;
string onsetCaptureId = "", onsetNativeExe = "";
string? OnsetBlockReason()
{
    if (!realtimeTtsEnabled) return "tts-disabled";
    if (Volatile.Read(ref onsetInputChanged) != 0) return "input-changed";
    if (ttsRouter.OnsetRouteChanging) return "route-changing";
    if (!onsetExplicitInput) return "input-required";
    var config = ttsRouter.GetConfig();
    if (!LooksLikeVirtualAudioDeviceName(config.OutputDeviceName)) return "output-required";
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
    if (enabled && !onsetExplicitInput)
    {
        var inputs = NativeLibraryBridge.GetAudioInputDevicesJson(onsetNativeExe);
        onsetExplicitInput = OnsetCapturePolicy.IsExplicitInput(inputs, onsetCaptureId);
    }
    var handler = ttsRouter.ConfigureOnset(enabled, lastStartOptions.language ?? "auto",
        () => generation == Volatile.Read(ref onsetControlGeneration) && OnsetPermitted(), newSession, newSession || onsetIdleKnown);
    if (enabled && generation != Volatile.Read(ref onsetControlGeneration)) { ttsRouter.StopOnset(); handler = null; }
    if (handler != null && realtimeSession?.SetVadStartHandler(handler) != true) ttsRouter.DisableOnset("native-unavailable");
    else if (handler == null && !newSession) realtimeSession?.SetVadStartHandler(null);
}
// sense_voice エンジン選択時は native には moonshine を渡す（PCM取得用）
// SenseVoice の実行は C# 側で制御
string lastTtsFinalText = string.Empty;
long lastTtsFinalDetectedAtMs = 0;
string lastTtsProsodyFinalText = string.Empty;
byte[] lastTtsProsodyPcm16Le = Array.Empty<byte>();
int lastTtsProsodySampleRate = 0;
RealtimeTextFilterResult? lastRealtimeTextFilterResult = null;
string lastFilteredFinalSource = string.Empty;
string lastPhoneticAssistDebugKey = string.Empty;
string lastPhoneticAssistAcceptedKey = string.Empty;
string lastPhoneticAssistAcceptedText = string.Empty;
string lastPhoneticAssistAcceptedReason = string.Empty;
string lastAdaptiveVocabLearnedTextKey = string.Empty;
DateTimeOffset lastAdaptiveVocabLearnedAt = DateTimeOffset.MinValue;
// Re-speak detection state
string lastAdaptiveFinalText = string.Empty;
string lastAdaptiveFinalIpa = string.Empty;
DateTimeOffset lastAdaptiveFinalAt = DateTimeOffset.MinValue;
string currentAdaptiveIpa = string.Empty; // set per-poll in TryApplyPhoneticAssist
object phoneticAssistPrefetchLock = new();
string phoneticAssistPrefetchKey = string.Empty;
Task<string?>? phoneticAssistPrefetchTask = null;

static bool IsShortPhoneticAssistPcm(RealtimePollResult poll, int expectedSampleRate)
{
    if (poll.FinalPcmSampleRate != expectedSampleRate) return false;
    var bytes = poll.FinalPcm16Le.Length;
    if (bytes < 3200) return false; // 0.1s@16kHz未満は音素情報不足
    var maxBytes = expectedSampleRate * 2 * 12 / 10; // 1.2s mono pcm16
    if (bytes > maxBytes) return false;
    return poll.VadSpeechCount > 0
        || string.Equals(poll.VadState, "speech", StringComparison.OrdinalIgnoreCase)
        || poll.CaptureRms > 0;
}

static string PhoneticAssistKey(RealtimePollResult poll, string primaryText) =>
    $"{poll.CaptureSamples}:{poll.FinalPcm16Le.Length}:{primaryText.Trim()}:{poll.ConfidenceScore}";

bool ShouldRunPhoneticAssistForPrimary(RealtimePollResult poll, string primaryText)
{
    var primaryForPhoneticAssist = (primaryText ?? string.Empty).Trim();
    var primaryRuneCount = primaryForPhoneticAssist.EnumerateRunes().Count();
    var lowConfidence = !poll.ConfidenceAvailable || poll.ConfidenceScore is < 0.45;
    return primaryRuneCount <= 1 || lowConfidence;
}

Task<string?>? StartPhoneticAssistPrefetch(RealtimePollResult poll, string primaryText, string? engine = null, bool isolated = false)
{
    if (phonemeCtcService?.IsAvailable != true
        || (engine ?? lastStartOptions.engine) != RealtimeEngineJointAuto
        || !IsShortPhoneticAssistPcm(poll, phonemeCtcService.SampleRate)
        || !ShouldRunPhoneticAssistForPrimary(poll, primaryText))
        return null;

    if (isolated)
    {
        var pcm = poll.FinalPcm16Le.ToArray();
        return Task.Run(() => phonemeCtcService.Transcribe(PcmToFloat32(pcm)));
    }
    var key = PhoneticAssistKey(poll, primaryText);
    lock (phoneticAssistPrefetchLock)
    {
        if (key == phoneticAssistPrefetchKey) return phoneticAssistPrefetchTask;
        var pcm = poll.FinalPcm16Le.ToArray();
        phoneticAssistPrefetchKey = key;
        phoneticAssistPrefetchTask = Task.Run(() => phonemeCtcService.Transcribe(PcmToFloat32(pcm)));
    }
    phonemeCtcLogger?.LogDebug("[PhoneticAssist] prefetch started key={Key} bytes={Bytes}", key, poll.FinalPcm16Le.Length);
    return phoneticAssistPrefetchTask;
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
// 無音/ノイズからのdecode幻聴とみなして棄却する。en/autoはgate対象外。
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

async Task SpeakDirectAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsody = null, long finalDetectedAtMs = 0)
{
    ct.ThrowIfCancellationRequested();
    var st = ttsRouter.GetStatus();
    var effectiveProsody = IsTtsProsodyEnabledForEngine(st.EffectiveEngine) ? sourceProsody : null;
    var speakStartAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var finalToSpeakMs = finalDetectedAtMs > 0 ? speakStartAtMs - finalDetectedAtMs : (long?)null;
    System.Threading.Interlocked.Exchange(ref lastRealtimeTtsSpeakFinalDetectedAtMs, finalDetectedAtMs);
    System.Threading.Interlocked.Exchange(ref lastRealtimeFinalToTtsSpeakMs, finalToSpeakMs ?? -1);
    ttsLogger.LogInformation(
        "[TTS] speak start: effectiveEngine={Engine} available={Avail} outDev={Dev} prosody={HasProsody} finalDetectedAtMs={FinalDetectedAtMs} ttsSpeakStartAtMs={TtsSpeakStartAtMs} finalToTtsSpeakMs={FinalToTtsSpeakMs} text=[{Text}]",
        st.EffectiveEngine, st.Available, st.OutputDeviceName, effectiveProsody != null,
        finalDetectedAtMs, speakStartAtMs, finalToSpeakMs, text);
    var r = await ttsRouter.SpeakAsync(text, null, null, false, ct, effectiveProsody, finalDetectedAtMs);
    if (r.Success)
        ttsLogger.LogInformation("[TTS] speak ok: effectiveEngine={Engine}", st.EffectiveEngine);
    else
        ttsLogger.LogWarning("[TTS] speak FAILED: effectiveEngine={Engine} error={Error}", st.EffectiveEngine, r.Error);
}

Task SpeakWithPendingAsync(string text, CancellationToken ct, TtsProsodyProfile? sourceProsody = null, long finalDetectedAtMs = 0,
    RealtimeOutputTicket? origin = null)
    => origin is { } ticket ? realtimeOutputEpoch.Commit<Task>(ticket,
        () => pendingSpeechService.EnqueueOrSpeakAsync(text, (s, token) => realtimeOutputEpoch.Commit<Task>(ticket,
            () => SpeakDirectAsync(s, token, sourceProsody, finalDetectedAtMs), () => Task.CompletedTask), ct, ticket.Cancellation),
        () => Task.CompletedTask)
        : pendingSpeechService.EnqueueOrSpeakAsync(text, (s, token) => SpeakDirectAsync(s, token, sourceProsody, finalDetectedAtMs), ct);

pendingSpeechService.OnTimeoutSpeakAsync = (text, ct) => SpeakDirectAsync(text, ct, null);

static bool IsTtsProsodyEnabledForEngine(string? engine)
{
    // MOSS は自声 F0 カーブを後処理 pitch-shift で転写する方式に変更し prosody 有効化。
    // Kokoro の intonation 反映は未対応のため引き続き無効。
    return !string.Equals(engine, TtsEngineKokoro, StringComparison.OrdinalIgnoreCase);
}

static string GetNewFinalText(string finalText, string lastSpoken, long finalDetectedAtMs, long lastFinalDetectedAtMs)
{
    if (string.IsNullOrWhiteSpace(finalText) || finalText == lastSpoken)
        return string.Empty;
    if (finalDetectedAtMs > 0 && (string.IsNullOrEmpty(lastSpoken) ||
        (lastFinalDetectedAtMs > 0 && finalDetectedAtMs != lastFinalDetectedAtMs)))
        return finalText.Trim();
    if (!string.IsNullOrEmpty(lastSpoken) && finalText.StartsWith(lastSpoken))
    {
        var suffix = finalText.Substring(lastSpoken.Length).TrimStart();
        return suffix;
    }
    var lines = finalText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
    return lines.Length > 0 ? lines[lines.Length - 1] : finalText.Trim();
}

static string NormalizeLoopbackText(string text)
{
    var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
    return new string(chars);
}

static bool IsRecentTtsLoopbackCandidate(string asrText, TtsStatus ttsStatus)
{
    if (string.IsNullOrWhiteSpace(asrText) || string.IsNullOrWhiteSpace(ttsStatus.LastTtsText)) return false;
    if (!DateTime.TryParse(ttsStatus.LastTtsStartedAt, out var startedAt)) return false;
    var elapsedMs = (DateTime.UtcNow - startedAt.ToUniversalTime()).TotalMilliseconds;
    if (elapsedMs < 0 || elapsedMs > 6000) return false;

    var asr = NormalizeLoopbackText(asrText);
    var tts = NormalizeLoopbackText(ttsStatus.LastTtsText);
    if (asr.Length < 2 || tts.Length < 2) return false;
    return asr.Contains(tts, StringComparison.Ordinal) || tts.Contains(asr, StringComparison.Ordinal);
}

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
            // Only penalise if the new text differs from the old (same text is just repetition, not correction)
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
        var added = adaptiveVocabStore.AddText(trimmed, lastStartOptions.language ?? "auto", source);
        if (added > 0)
            adaptiveVocabLogger.LogInformation("[AdaptiveVocab] learned {Count} entries source={Source} lang={Lang}", added, source, lastStartOptions.language ?? "auto");
    }
    catch (Exception ex)
    {
        adaptiveVocabLogger.LogWarning(ex, "[AdaptiveVocab] ASR learning failed");
    }
}

void MaybeSpeakRealtimeFinal(RealtimePollResult poll, string? senseVoiceFinal = null, string? finalOverride = null)
{
    var outputTicket = realtimeOutputEpoch.Capture();
    var finalForTts = finalOverride ?? senseVoiceFinal ?? poll.SelectedText ?? poll.Final ?? string.Empty;
    if (!string.IsNullOrEmpty(finalForTts) && poll.FinalPcm16Le.Length >= 3200 && poll.FinalPcmSampleRate > 0)
    {
        lastTtsProsodyFinalText = finalForTts;
        lastTtsProsodyPcm16Le = poll.FinalPcm16Le;
        lastTtsProsodySampleRate = poll.FinalPcmSampleRate;
    }

    // Review prompt (P2): 新しいfinalテキストを「成功発話1回」としてカウント
    // （finalは累積文字列なので、変化したときだけ+1。ポーリング重複はここで排除される）
    if (!string.IsNullOrEmpty(finalForTts) && finalForTts != reviewLastCountedFinalText)
    {
        reviewLastCountedFinalText = finalForTts;
        lock (reviewPromptLock) { reviewSessionAsrSuccessCount++; }
    }

    var ttsStatus = ttsRouter.GetStatus();
    var vrchatOscEnabled = vrchatOscService.GetConfig().Enabled;
    if ((!realtimeTtsEnabled && !vrchatOscEnabled) || string.IsNullOrEmpty(finalForTts))
    {
        return;
    }
    if (ttsRouter.IsSpeakingForRealtimeBody)
    {
        // A retained final can carry the next utterance's age clock (including 0).
        // Only consuming different text may replace its stored segment boundary.
        if (finalForTts != lastTtsFinalText)
        {
            lastTtsFinalText = finalForTts;
            lastTtsFinalDetectedAtMs = poll.FinalDetectedAtMs;
        }
        return;
    }

    var newText = GetNewFinalText(finalForTts, lastTtsFinalText, poll.FinalDetectedAtMs, lastTtsFinalDetectedAtMs);
    if (string.IsNullOrWhiteSpace(newText))
    {
        return;
    }
    if (IsRecentTtsLoopbackCandidate(newText, ttsStatus))
    {
        lastTtsFinalText = finalForTts;
        lastTtsFinalDetectedAtMs = poll.FinalDetectedAtMs;
        return;
    }

    var filterResult = RealtimeTextFilter.Apply(newText, SnapshotTextFilterConfig(lastStartOptions.language));
    lastRealtimeTextFilterResult = filterResult;
    lastFilteredFinalSource = finalForTts;
    if (filterResult.Dropped || string.IsNullOrWhiteSpace(filterResult.Text))
    {
        lastTtsFinalText = finalForTts;
        lastTtsFinalDetectedAtMs = poll.FinalDetectedAtMs;
        return;
    }
    newText = filterResult.Text;
    var finalDetectedAtMs = poll.FinalDetectedAtMs;

    if (newText.Length > 500) newText = newText.Substring(0, 500);
    lastTtsFinalText = finalForTts;
    lastTtsFinalDetectedAtMs = poll.FinalDetectedAtMs;
    TtsProsodyProfile? sourceProsody = null;
    var pcmForProsody = poll.FinalPcm16Le.Length >= 3200
        ? poll.FinalPcm16Le
        : (lastTtsProsodyFinalText == finalForTts ? lastTtsProsodyPcm16Le : Array.Empty<byte>());
    var sampleRateForProsody = poll.FinalPcmSampleRate > 0
        ? poll.FinalPcmSampleRate
        : (lastTtsProsodyFinalText == finalForTts ? lastTtsProsodySampleRate : 0);
    if (pcmForProsody.Length >= 3200 && sampleRateForProsody > 0)
        sourceProsody = TtsProsodyAnalyzer.AnalyzeTextAndPcm(newText, pcmForProsody, sampleRateForProsody);

    Task<bool>? mossReferenceTask = null;
    if (EnableMossAsrReferenceAudio && ttsStatus.EffectiveEngine == "moss" && pcmForProsody.Length >= 3200 && sampleRateForProsody > 0)
    {
        var capturedPcm = pcmForProsody.ToArray();
        var capturedSampleRate = sampleRateForProsody;
        mossReferenceTask = ttsRouter.SetMossReferencePcmAsync(capturedPcm, capturedSampleRate, outputTicket.Cancellation);
    }

    // ── 翻訳 (Talk 翻訳が有効な場合は翻訳テキストを TTS で発話) ──
    var talkTarget = LoadTranslationConfig().TalkTarget;
    if (talkTarget != "OFF" && translationService.IsLoaded)
    {
        string srcLang           = lastStartOptions.language ?? "ja";
        string capturedNewText   = newText;
        string capturedFinalSrc  = finalForTts;
        TtsProsodyProfile? capturedProsody = sourceProsody;
        long capturedFinalDetectedAtMs = finalDetectedAtMs;
        Task<bool>? capturedMossReferenceTask = mossReferenceTask;
        Task.Run(() =>
        {
            try
            {
                var translated = translationService.Translate(capturedNewText, srcLang, talkTarget);
                string speakText = string.IsNullOrWhiteSpace(translated) ? capturedNewText : translated;
                realtimeOutputEpoch.Commit(outputTicket, () =>
                {
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    lastTranslatedFinalText   = translated;
                    lastTranslatedFinalSource = capturedFinalSrc;
                    UnlockSteamAchievement(SteamAchievementService.FirstTranslatedOutput);
                    translationLogger.LogDebug("[Translation] {Src} -> {Tgt}: [{Original}] -> [{Translated}]",
                        srcLang, talkTarget, capturedNewText, translated);
                }
                var oscText = string.IsNullOrWhiteSpace(translated) ? capturedNewText : translated!;
                if (vrchatOscEnabled)
                    vrchatOscService.EnqueueText(oscText);
                return true;
                }, () => false);
                if (realtimeTtsEnabled)
                {
                    try { capturedMossReferenceTask?.GetAwaiter().GetResult(); } catch (Exception ex) { translationLogger.LogDebug(ex, "[TTS] MOSS reference audio encode failed"); }
                    _ = SpeakWithPendingAsync(speakText, outputTicket.Cancellation, capturedProsody, capturedFinalDetectedAtMs, outputTicket)
                        .ContinueWith(t => translationLogger.LogError(t.Exception?.GetBaseException(),
                            "[TTS] translation-speak error"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                translationLogger.LogWarning(ex, "[Translation] translate+speak failed, falling back to original");
                if (vrchatOscEnabled) realtimeOutputEpoch.Commit(outputTicket,
                    () => { vrchatOscService.EnqueueText(capturedNewText); return true; }, () => false);
                if (realtimeTtsEnabled)
                {
                    try { capturedMossReferenceTask?.GetAwaiter().GetResult(); } catch (Exception encodeEx) { logger.LogDebug(encodeEx, "[TTS] MOSS reference audio encode failed"); }
                    _ = SpeakWithPendingAsync(capturedNewText, outputTicket.Cancellation, capturedProsody, capturedFinalDetectedAtMs, outputTicket)
                        .ContinueWith(t => logger.LogError(t.Exception?.GetBaseException(),
                            "[TTS] fallback-speak error"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        });
        return;
    }

    if (vrchatOscEnabled)
        vrchatOscService.EnqueueText(newText);
    if (realtimeTtsEnabled)
        _ = Task.Run(async () =>
        {
            if (mossReferenceTask != null)
            {
                try { await mossReferenceTask.ConfigureAwait(false); } catch (Exception ex) { logger.LogDebug(ex, "[TTS] MOSS reference audio encode failed"); }
            }
            await SpeakWithPendingAsync(newText, outputTicket.Cancellation, sourceProsody, finalDetectedAtMs, outputTicket).ConfigureAwait(false);
        }).ContinueWith(t => logger.LogError(t.Exception?.GetBaseException(), "[TTS] MaybeSpeakRealtimeFinal error"), TaskContinuationOptions.OnlyOnFaulted);
}

(string? Text, string? Engine, string? DecisionReason) TryApplyPhoneticAssist(RealtimePollResult poll, string primaryText,
    string? engine = null, string? language = null, bool replay = false, Task<string?>? replayPrefetch = null)
{
    var outputTicket = realtimeOutputEpoch.Capture();
    if (phonemeCtcService?.IsAvailable != true
        || (engine ?? lastStartOptions.engine) != RealtimeEngineJointAuto
        || poll.FinalPcm16Le.Length < 3200
        || poll.FinalPcmSampleRate != phonemeCtcService.SampleRate)
        return (null, null, null);

    var primaryForPhoneticAssist = primaryText.Trim();
    if (string.IsNullOrEmpty(primaryForPhoneticAssist))
        return (null, null, null);
    var shouldRunPhoneticAssist = ShouldRunPhoneticAssistForPrimary(poll, primaryForPhoneticAssist);
    var debugKey = PhoneticAssistKey(poll, primaryForPhoneticAssist);
    if (!replay && debugKey == lastPhoneticAssistAcceptedKey && !string.IsNullOrEmpty(lastPhoneticAssistAcceptedText))
        return (lastPhoneticAssistAcceptedText, RealtimeEnginePhoneme,
            string.IsNullOrEmpty(lastPhoneticAssistAcceptedReason) ? "phonetic_assist_filler" : lastPhoneticAssistAcceptedReason);
    if (!replay && debugKey == lastPhoneticAssistDebugKey)
        return (null, null, null);

    if (!replay) lastPhoneticAssistDebugKey = debugKey;
    var ipa = (replay ? replayPrefetch?.GetAwaiter().GetResult() : GetPrefetchedPhoneticAssistIpa(debugKey))
        ?? phonemeCtcService.Transcribe(PcmToFloat32(poll.FinalPcm16Le));
    if (ipa == null)
        return (null, null, null);
    // Store IPA for re-speak detection (current poll; previous is in lastAdaptiveFinalIpa)
    if (!replay) currentAdaptiveIpa = ipa;

    var effectiveLanguage = language ?? lastStartOptions.language ?? poll.Language;
    var assistLang = ResolvePhoneticAssistLanguage(effectiveLanguage);
    var hotword = TrySelectAdaptiveHotword(ipa, effectiveLanguage, primaryForPhoneticAssist);
    if (hotword != null)
    {
        if (!replay)
        {
            lastPhoneticAssistAcceptedKey = debugKey;
            lastPhoneticAssistAcceptedText = hotword.Text;
            lastPhoneticAssistAcceptedReason = "phonetic_assist_hotword";
        }
        phonemeCtcLogger?.LogInformation(
            "[AdaptiveVocab] hotword accepted primary=[{Primary}] output=[{Output}] score={Score} confidence={Confidence}",
            primaryForPhoneticAssist,
            hotword.Text,
            hotword.Score,
            poll.ConfidenceScore);
        return (hotword.Text, RealtimeEnginePhoneme, "phonetic_assist_hotword");
    }

    if (!replay && poll.ConfidenceAvailable && poll.ConfidenceScore is >= 0.80)
        realtimeOutputEpoch.Commit(outputTicket,
            () => { RememberAdaptiveHotwordPhonemes(primaryForPhoneticAssist, effectiveLanguage, ipa); return true; }, () => false);

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
    if (!replay && fallback.Accepted && !string.IsNullOrEmpty(fallback.OutputText))
    {
        lastPhoneticAssistAcceptedKey = debugKey;
        lastPhoneticAssistAcceptedText = fallback.OutputText;
        lastPhoneticAssistAcceptedReason = $"phonetic_assist_{fallback.Lane}";
    }
    phonemeCtcLogger?.LogDebug(
        "[PhoneticAssist] joint_auto debug primary=[{Primary}] ipa=[{Ipa}] surface=[{Surface}] accepted={Accepted} lane={Lane} output=[{Output}] reason={Reason} confidence={Confidence}",
        primaryForPhoneticAssist,
        ipa,
        surface,
        fallback.Accepted,
        fallback.Lane,
        fallback.OutputText ?? string.Empty,
        fallback.Reason,
        poll.ConfidenceScore);

    return fallback.Accepted && !string.IsNullOrEmpty(fallback.OutputText)
        ? (fallback.OutputText, RealtimeEnginePhoneme, $"phonetic_assist_{fallback.Lane}")
        : (null, null, null);
}

RealtimeFinalOutput ProcessRealtimeFinalOutput(RealtimePollResult poll, string? engine, string? language, bool replay = false)
{
    var prefetch = StartPhoneticAssistPrefetch(poll, poll.SelectedText ?? poll.Final ?? string.Empty, engine, replay);
    try
    {
        return RealtimeFinalOutputProcessor.Process(poll, engine, language,
        senseVoiceService?.IsAvailable != true ? null : (input, lang) =>
        {
            var text = senseVoiceService.Transcribe(PcmToFloat32(input.FinalPcm16Le), input.FinalPcmSampleRate, lang);
            if (!string.IsNullOrEmpty(text) && !SenseVoiceTextPlausibleForLanguage(text, lang))
            {
                senseVoiceLogger?.LogInformation("[SenseVoice] rejected: script mismatch lang={Lang} sv=[{Sv}]", lang, text);
                return null;
            }
            senseVoiceLogger?.LogInformation("[SenseVoice] compare: native=[{Native}] sv=[{Sv}] → {Winner}",
                input.Final, text ?? "(empty)", !string.IsNullOrEmpty(text) ? "sense_voice" : "native");
            return text;
        },
        (input, primary) =>
        {
            var assist = TryApplyPhoneticAssist(input, primary, engine, language, replay, prefetch);
            return assist.Text is null ? null : new RealtimeFinalOverride(assist.Text,
                assist.Engine ?? input.SelectedEngine, assist.DecisionReason ?? input.DecisionReason);
        });
    }
    finally
    {
        // Even if another recognizer failed, do not release the replay's ASR
        // lease while an isolated prefetch is still using the runtime.
        if (replay) prefetch?.GetAwaiter().GetResult();
    }
}

// Initialized after the normal per-session state, before the application serves requests.
replayOutputProcessor = (poll, replayEngine, replayLanguage) =>
    ProcessRealtimeFinalOutput(poll, replayEngine, replayLanguage, replay: true);

app.MapGet("/api/realtime/status", () =>
{
    lock (realtimeLock)
    {
        if (realtimeSession == null)
        {
            return Results.Ok(new { state = "none", partial = "", final = "", error = (string?)null,
                options = new { engine = lastStartOptions.engine, language = lastStartOptions.language,
                    scene = lastStartOptions.scene, prefilterProfile = lastStartOptions.prefilterProfile,
                    initialPromptLength = lastStartOptions.initialPromptLength } });
        }
        var outputTicket = realtimeOutputEpoch.Capture();
        ttsRouter.RefreshOnset(OnsetPermitted, onsetIdleKnown);
        var poll = realtimeSession.Poll();
        onsetIdleKnown = poll.VadState == "silence";
        if (!string.IsNullOrEmpty(poll.Error)) ttsRouter.StopOnset();
        ttsRouter.RefreshOnset(OnsetPermitted, onsetIdleKnown);
        var output = ProcessRealtimeFinalOutput(poll, lastStartOptions.engine, lastStartOptions.language);
        return realtimeOutputEpoch.Commit<IResult>(outputTicket, () =>
        {
        var finalForOutput = output.Text;
        var finalForTts = finalForOutput;
        LogRealtimeAsrReady(poll, finalForOutput, output.Engine, output.DecisionReason, output.SenseVoiceText != null ? "sense_voice" : "native");
        MaybeLearnAdaptiveAsrText(poll, finalForOutput);
        MaybeSpeakRealtimeFinal(poll, output.SenseVoiceText, output.PhoneticText);
        // Phase 7.3-2: Loopback detection
        // テキスト類似度 OR TTS発話中に届いたfinalはエコーとみなして抑制
        var loopbackSuspected = !string.IsNullOrEmpty(finalForTts)
            && (ttsService.CheckLoopbackSuspected(finalForTts)
                || (realtimeTtsEnabled && ttsRouter.IsSpeakingForRealtimeBody));
        return Results.Ok(new
        {
            state = poll.State,
            partial = poll.Partial,
            final = finalForOutput,
            selectedText = finalForOutput,
            selectedEngine = output.Engine,
            decisionReason = output.DecisionReason,
            language = poll.Language,
            resolvedScene = poll.ResolvedScene,
            confidenceAvailable = poll.ConfidenceAvailable,
            confidenceScore = poll.ConfidenceScore,
            confidenceUnavailableReason = poll.ConfidenceUnavailableReason,
            latencyMs = poll.LatencyMs,
            addonCandidate = poll.AddonCandidate,
            addonWallMs = poll.AddonWallMs,
            jointExistingStatus = poll.JointExistingStatus,
            jointAddonStatus = poll.JointAddonStatus,
            jointSelectionReason = poll.JointSelectionReason,
            existingJointWallMs = poll.ExistingJointWallMs,
            jointTotalWallMs = poll.JointTotalWallMs,
            addonAddedWaitMs = poll.AddonAddedWaitMs,
            finalDetectedAtMs = poll.FinalDetectedAtMs,
            finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
            finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
            captureSamples = poll.CaptureSamples,
            captureRms = poll.CaptureRms,
            captureAgcGain = poll.CaptureAgcGain,
            vadState = poll.VadState,
            vadSpeechCount = poll.VadSpeechCount,
            error = poll.Error,
          options = new { engine = lastStartOptions.engine, language = lastStartOptions.language,
                scene = lastStartOptions.scene, prefilterProfile = lastStartOptions.prefilterProfile,
                    initialPromptLength = lastStartOptions.initialPromptLength,
                    senseVoiceAvailable = senseVoiceService?.IsAvailable == true },
            processedFinal = (lastFilteredFinalSource == finalForOutput) ? lastRealtimeTextFilterResult?.Text : null,
            textFilter = (lastFilteredFinalSource == finalForOutput && lastRealtimeTextFilterResult != null)
                ? new
                {
                    dropped = lastRealtimeTextFilterResult.Dropped,
                    laughReplaced = lastRealtimeTextFilterResult.LaughReplaced,
                    ngMatched = lastRealtimeTextFilterResult.NgMatched,
                    reason = lastRealtimeTextFilterResult.Reason,
                }
                : null,
            loopbackSuspected = loopbackSuspected,
            translatedFinal = (lastTranslatedFinalSource == finalForOutput) ? lastTranslatedFinalText : null
        });
        }, () => Results.Ok(new { state = "idle", discarded = true }));
    }
});

var realtimeLogger = loggerFactory.CreateLogger("Realtime");
app.MapPost("/api/realtime/start", async (HttpRequest request) =>
{
    var onsetStartGeneration = Volatile.Read(ref onsetControlGeneration);
    if (IsDemoSessionExpired())
    {
        return DemoSessionExpiredResult();
    }
    string captureDeviceId = string.Empty;
    string engine = ResolveDefaultRealtimeEngine(LoadConfigWithSelectedWhisperModel());
    string language = "ja";
    string scene = RealtimeSceneShortCommand;
    string prefilterProfile = RealtimePrefilterAuto;
    string initialPrompt = string.Empty;
    int vadSilenceDurationMs = 1000;
    bool ttsEnabled = false;
    bool fillerEnabled = false;
    string clientSource = "unknown";
    bool useLanguageProfile = false;
    string? addonLanguageMode = null;
    long clientOperationId = 0;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("captureDeviceId", out var idProp))
            {
                captureDeviceId = idProp.GetString() ?? string.Empty;
            }
            if (body.RootElement.TryGetProperty("engine", out var engineProp))
            {
                engine = (engineProp.GetString() ?? engine).Trim().ToLowerInvariant();
            }
            if (body.RootElement.TryGetProperty("language", out var languageProp))
            {
                language = (languageProp.GetString() ?? "ja").Trim().ToLowerInvariant();
            }
            if (body.RootElement.TryGetProperty("scene", out var sceneProp))
            {
                scene = (sceneProp.GetString() ?? RealtimeSceneShortCommand).Trim().ToLowerInvariant();
            }
            if (body.RootElement.TryGetProperty("prefilterProfile", out var prefilterProp))
            {
                prefilterProfile = (prefilterProp.GetString() ?? RealtimePrefilterAuto).Trim().ToLowerInvariant();
            }
            if (body.RootElement.TryGetProperty("initialPrompt", out var promptProp))
            {
                initialPrompt = promptProp.GetString() ?? string.Empty;
            }
            if (body.RootElement.TryGetProperty("addonLanguageMode", out var hint))
                addonLanguageMode = hint.ValueKind == JsonValueKind.String ? hint.GetString() ?? "" : "";
            if (body.RootElement.TryGetProperty("vadSilenceDurationMs", out var vadSilenceProp) && vadSilenceProp.TryGetInt32(out var vadSilenceMs))
            {
                vadSilenceDurationMs = vadSilenceMs;
            }
            if (body.RootElement.TryGetProperty("ttsEnabled", out var ttsProp))
            {
                ttsEnabled = ttsProp.GetBoolean();
            }
            fillerEnabled = body.RootElement.TryGetProperty("fillerEnabled", out var fillerProp) && fillerProp.ValueKind == JsonValueKind.True;
            if (body.RootElement.TryGetProperty("clientSource", out var sourceProp)) clientSource = sourceProp.GetString() ?? clientSource;
            if (body.RootElement.TryGetProperty("useLanguageProfile", out var profileProp) && profileProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                useLanguageProfile = profileProp.GetBoolean();
            if (body.RootElement.TryGetProperty("clientOperationId", out var opProp) && opProp.TryGetInt64(out var op)) clientOperationId = op;
        }
        catch (JsonException e) { realtimeLogger.LogWarning(e, "[Realtime/Start] JSON parse error (defaults used)"); }
    }

    if (realtimeStartRecoveryRequired && !IsSafeAuto(engine) && !IsAddonEngine(engine))
    {
        realtimeLogger.LogWarning("[Realtime] previous start ended unexpectedly; forcing Safe Auto requestedEngine={RequestedEngine}", engine);
        engine = RealtimeEngineSafeAuto;
    }

    if (!RealtimeEngineAllowed(engine) && !IsAddonEngine(engine))
    {
        return Results.BadRequest(ApiMessage("realtime.unsupported_engine", "unsupported realtime engine"));
    }
    await addonLoad;
    var requestedProfile = language == "auto" ? null : addonManager.Describe(language).Desired;
    if ((IsAddonEngine(engine) && !AddonCatalog.Engines.First(e => e.Id == engine).Models.Any(m => m.Languages.Contains(language))) ||
        (!IsAddonEngine(engine) && !IsRealtimeLanguageSupported(engine, language) &&
         !(useLanguageProfile && language != "auto" && whisperLanguageLabels.ContainsKey(language) &&
           requestedProfile?.Enabled == true)) ||
        (useLanguageProfile && requestedProfile?.Enabled == true && requestedProfile.JointRequested &&
         !IsRealtimeLanguageSupported(RealtimeEngineJointAuto, language)))
    {
        return Results.BadRequest(ApiMessage("realtime.unsupported_language", "unsupported realtime language"));
    }
    if (!IsLiteralValue(scene, RealtimeSceneValues))
    {
        return Results.BadRequest(ApiMessage("realtime.unsupported_scene", "unsupported realtime scene"));
    }
    if (!IsLiteralValue(prefilterProfile, RealtimePrefilterValues))
    {
        return Results.BadRequest(ApiMessage("realtime.unsupported_prefilter", "unsupported realtime prefilter"));
    }
    if (initialPrompt.Length > 2000)
    {
        return Results.BadRequest(ApiMessage("realtime.initial_prompt_too_long", "initial prompt is too long"));
    }

    if (!(useLanguageProfile && requestedProfile?.Enabled == true) && addonLanguageMode != null &&
        !ValidAddonMode(addonLanguageMode, IsAddonEngine(engine) ? engine : requestedProfile?.EngineId,
            IsAddonEngine(engine) || (engine == RealtimeEngineJointAuto && requestedProfile?.Enabled == true && requestedProfile.JointRequested)))
        return Results.BadRequest(ApiMessage("addon.invalid_language_mode", "Qwen requires an explicit requested or auto hint; other routes do not accept addonLanguageMode"));

    var adaptivePrompt = adaptivePromptBuilder.Build(initialPrompt, language);
    initialPrompt = adaptivePrompt.Prompt;

    var startLease = addonWorkGate.TryEnterWork();
    if (startLease is null) return Results.Conflict(ApiMessage("addon.busy", "Addon operation is running"));
    await realtimeMutationGate.WaitAsync();
    try
    {
    realtimeOutputEpoch.Invalidate();
    lock (realtimeLock)
    {
        // A language replacement must finish the old native session before its
        // resident/context can change, including direct API clients.
        realtimeSessionService.Dispose();
        realtimeSession = null;
        realtimeWorkLease?.Dispose();
        realtimeWorkLease = null;
    }
    if (language == "auto") await addonManager.StopAsync(request.HttpContext.RequestAborted);
    else await addonManager.ApplyLanguageAsync(language, request.HttpContext.RequestAborted);
    var startAddon = addonManager.Describe();
    var languageProfile = startAddon.Desired;
    if (useLanguageProfile && language != "auto" && languageProfile?.Enabled == true)
    {
        if (!startAddon.State.Ready || !startAddon.State.Enabled || startAddon.Language != language)
            return Results.Conflict(ApiMessage("addon.not_ready", "Addon is not ready"));
        engine = languageProfile.JointRequested ? RealtimeEngineJointAuto : languageProfile.EngineId;
        addonLanguageMode = languageProfile.EngineId == AddonCatalog.QwenId ? languageProfile.LanguageMode : null;
    }
    if (IsAddonEngine(engine) && (!startAddon.State.Ready || !startAddon.State.Enabled ||
        startAddon.Language != language || startAddon.State.EngineId != engine))
        return Results.Conflict(ApiMessage("addon.not_ready", "Addon is not ready"));
    if (!IsRealtimeLanguageSupported(engine, language))
        return Results.BadRequest(ApiMessage("realtime.unsupported_language", "unsupported realtime language"));
    lock (realtimeLock)
    {
        var requestedStartOptions = (engine, language, scene, prefilterProfile, initialPrompt.Length);
        try
        {
            var config = LoadConfigWithSelectedWhisperModel();
            engine = ResolveRealtimeEngineForRuntime(engine, config, realtimeLogger);
            var whisperRuntime = AppConfigLoader.ResolveWhisperRuntime(config, appRoot);
            var nativeExe = Path.IsPathFullyQualified(config.NativeExePath)
                ? config.NativeExePath
                : Path.Combine(appRoot, config.NativeExePath);
            if (IsAddonEngine(engine))
            {
                if (!AddonEngineAvailable(engine)) return Results.Conflict(ApiMessage("addon.not_ready", "Addon is not ready"));
                NativeLibraryBridge.RegisterAddonTranscriberV2(nativeExe, addonTranscriber.CallbackV2);
            }

            // Build options JSON for native create
            // sense_voice は C# 側で処理するため、native には moonshine を渡す
            var safeAuto = IsSafeAuto(engine);
            var nativeEngine = engine == RealtimeEngineSenseVoice || safeAuto ? RealtimeEngineMoonshine : engine;
            var addonAtStart = startAddon.State;
            var jointAddonAtStart = nativeEngine == RealtimeEngineJointAuto && OperatingSystem.IsWindows() &&
                startAddon.Language == language && startAddon.JointEligible;
            var usesAddon = IsAddonEngine(engine) || jointAddonAtStart;
            if (usesAddon && addonAtStart.EngineId == AddonCatalog.QwenId && addonLanguageMode is null)
                addonLanguageMode = languageProfile?.LanguageMode;
            if (!ValidAddonMode(addonLanguageMode, addonAtStart.EngineId, usesAddon))
                return Results.BadRequest(ApiMessage("addon.invalid_language_mode", "Qwen requires an explicit requested or auto hint; other routes do not accept addonLanguageMode"));
            var addonModelAtStart = usesAddon ? AddonCatalog.Engines.Single(e => e.Id == addonAtStart.EngineId)
                .Models.Single(m => m.Id == addonAtStart.ModelId) : null;
            if (jointAddonAtStart) NativeLibraryBridge.RegisterAddonTranscriberV2(nativeExe, addonTranscriber.JointCallbackV2, joint: true);
            var enableParaformerZhCandidate = IsJointAutoZh(engine, language);
            var paraformerZhModelDir = Path.Combine(appRoot, "models", "paraformer_zh");
            var optionsJson = JsonSerializer.Serialize(new
            {
                engine = nativeEngine,
                addonJointRequested = jointAddonAtStart,
                addonJointLanguage = language,
                addonEngineId = usesAddon ? addonAtStart.EngineId : AddonCatalog.HaasId,
                addonModelId = addonModelAtStart?.Id ?? "",
                addonModelRevision = addonModelAtStart?.Revision ?? "",
                whisperDllDir = safeAuto ? string.Empty : whisperRuntime.WhisperDllDir,
                modelPath = safeAuto ? string.Empty : config.DefaultModelPath,
                moonshineDllDir = config.DefaultMoonshineDllDir,
                moonshineModelRoot = config.DefaultMoonshineModelRoot,
                language,
                useGpu = safeAuto ? false : whisperRuntime.UseGpu,
                threads = config.DefaultThreads,
                initialPrompt,
                scene,
                vadSilenceDurationMs,
                usePrefilter = prefilterProfile != RealtimePrefilterOff,
                prefilterProfile = prefilterProfile == RealtimePrefilterOff ? RealtimePrefilterAuto : prefilterProfile,
                captureDeviceId,
                sileroVadModelPath = "models/silero_vad/silero_vad_int8.onnx",
                onnxRuntimeDllDir = "libs/onnxruntime",
                // Phase 7A: Speaker identification - auto-enable on recording start per spec
                speakerIdEnabled = true,
                speakerProfilePath = Path.Combine(AppContext.BaseDirectory, "speaker_profile.json"),
                experimentalAsrCandidatesEnabled = enableParaformerZhCandidate,
                experimentalAsrCandidateEngine = enableParaformerZhCandidate ? "paraformer_zh" : string.Empty,
                experimentalAsrCandidateLanguage = enableParaformerZhCandidate ? "zh" : string.Empty,
                experimentalAsrCandidateModelDir = enableParaformerZhCandidate || engine == RealtimeEngineParaformerZh ? paraformerZhModelDir : string.Empty,
            });

            if (addonLanguageMode is not null)
            {
                var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson)!;
                options["addonLanguageMode"] = JsonSerializer.SerializeToElement(addonLanguageMode);
                optionsJson = JsonSerializer.Serialize(options);
            }
            // Request validation precedes replacement; only now publish the new options.
            lastStartOptions = requestedStartOptions;
            currentAdaptiveIpa = string.Empty;
            lastPhoneticAssistDebugKey = string.Empty;
            lastPhoneticAssistAcceptedKey = string.Empty;
            lastPhoneticAssistAcceptedText = string.Empty;
            lastPhoneticAssistAcceptedReason = string.Empty;
            lock (phoneticAssistPrefetchLock)
            {
                phoneticAssistPrefetchKey = string.Empty;
                phoneticAssistPrefetchTask = null;
            }
            realtimeTtsEnabled = ttsEnabled;
            lastTtsFinalText = string.Empty;
            lastTtsFinalDetectedAtMs = 0;
            lastRealtimeTextFilterResult = null;
            lastFilteredFinalSource = string.Empty;
            realtimeLogger.LogInformation("[Realtime] op={OperationId} source={Source} action=start deviceId={DeviceId} ttsEnabled={TtsEnabled}", clientOperationId, clientSource, captureDeviceId, ttsEnabled);
            realtimeLogger.LogDebug("[DEBUG] realtime_create options: {Options}", optionsJson);
            realtimeOutputEpoch.Invalidate();
            realtimeSession = null;
            MarkRealtimeStartPending(engine, clientSource);
            var previousLease = realtimeWorkLease;
            realtimeWorkLease = null;
            try { realtimeSessionService.Start(() => RealtimeSessionController.Create(nativeExe, optionsJson, realtimeLogger)); }
            finally { previousLease?.Dispose(); }
            realtimeSession = realtimeSessionService.Current;
            onsetExplicitInput = false;
            Interlocked.Exchange(ref onsetInputChanged, 0);
            onsetIdleKnown = true;
            onsetCaptureId = captureDeviceId;
            onsetNativeExe = nativeExe;
            BindOnset(fillerEnabled, newSession: true, expectedGeneration: onsetStartGeneration);
            realtimeOutputEpoch.Begin();
            realtimeWorkLease = startLease;
            startLease = null;
            ClearRealtimeStartPending();
            UnlockSteamAchievement(SteamAchievementService.FirstSessionStart);
            return Results.Ok(new
            {
                code = "realtime.recording_started",
                message = "recording started",
                adaptivePromptInjected = adaptivePrompt.Injected,
                adaptiveVocabCount = adaptivePrompt.VocabCount
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("realtime.start_failed"));
        }
    }
    }
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
            if (hasEnabled && !enabled) mobfyOnset.Stop(); // Match the existing OFF retirement acknowledgement.
            return Results.Ok(mobfyOnset.GetStatus());
        }
    }
    var prepareOnly = !hasEnabled && language != null;
    var generation = !prepareOnly ? Interlocked.Increment(ref onsetControlGeneration) : Volatile.Read(ref onsetControlGeneration);
    if (!prepareOnly && !enabled) ttsRouter.StopOnset(); // OFF does not wait for a blocking ASR poll.
    await realtimeMutationGate.WaitAsync();
    try
    {
        lock (realtimeLock)
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
    }
    finally { realtimeMutationGate.Release(); }
});

app.MapPost("/api/realtime/stop", (bool? discard) =>
{
    Interlocked.Increment(ref onsetControlGeneration);
    ttsRouter.StopOnset();
    realtimeOutputEpoch.Invalidate();
    realtimeMutationGate.Wait();
    try
    {
    lock (realtimeLock)
    {
        var outputTicket = realtimeOutputEpoch.Invalidate();
        try
        {
            if (realtimeSessionService.Current == null)
            {
                return Results.Ok(new { state = "idle", discarded = discard == true });
            }
            realtimeLogger.LogInformation("[Realtime] source=main action=stop");
            if (discard == true) realtimeTtsEnabled = false;
            RealtimePollResult poll;
            try { poll = realtimeSessionService.Stop()!; }
            finally
            {
                realtimeSession = null;
                realtimeWorkLease?.Dispose();
                realtimeWorkLease = null;
            }
            if (discard == true) return Results.Ok(new { state = "stopped", discarded = true });
            if (!realtimeOutputEpoch.Activate(outputTicket)) return Results.Ok(new { state = "stopped", discarded = true });
            StartPhoneticAssistPrefetch(poll, poll.SelectedText ?? poll.Final ?? string.Empty);
            var phoneticAssist = TryApplyPhoneticAssist(poll, poll.SelectedText ?? poll.Final ?? string.Empty);
            return realtimeOutputEpoch.Commit<IResult>(outputTicket, () =>
            {
            var finalForOutput = phoneticAssist.Text ?? poll.SelectedText ?? poll.Final ?? string.Empty;
            MaybeLearnAdaptiveAsrText(poll, finalForOutput);
            MaybeSpeakRealtimeFinal(poll, finalOverride: phoneticAssist.Text);
            return Results.Ok(new
            {
                state = poll.State,
                partial = poll.Partial,
                final = finalForOutput,
                selectedText = finalForOutput,
                selectedEngine = phoneticAssist.Engine ?? poll.SelectedEngine,
                decisionReason = phoneticAssist.DecisionReason ?? poll.DecisionReason,
                language = poll.Language,
                resolvedScene = poll.ResolvedScene,
                confidenceAvailable = poll.ConfidenceAvailable,
                confidenceScore = poll.ConfidenceScore,
                confidenceUnavailableReason = poll.ConfidenceUnavailableReason,
                latencyMs = poll.LatencyMs,
                addonCandidate = poll.AddonCandidate,
                addonWallMs = poll.AddonWallMs,
                jointExistingStatus = poll.JointExistingStatus,
                jointAddonStatus = poll.JointAddonStatus,
                jointSelectionReason = poll.JointSelectionReason,
                existingJointWallMs = poll.ExistingJointWallMs,
                jointTotalWallMs = poll.JointTotalWallMs,
                addonAddedWaitMs = poll.AddonAddedWaitMs,
                finalDetectedAtMs = poll.FinalDetectedAtMs,
                finalToTtsSpeakMs = GetLastFinalToTtsSpeakMs(poll.FinalDetectedAtMs),
                finalToPlaybackMs = GetLastFinalToPlaybackMs(poll.FinalDetectedAtMs),
                captureSamples = poll.CaptureSamples,
                captureRms = poll.CaptureRms,
                vadState = poll.VadState,
                vadSpeechCount = poll.VadSpeechCount,
                error = poll.Error,
                options = new { engine = lastStartOptions.engine, language = lastStartOptions.language,
                    scene = lastStartOptions.scene, prefilterProfile = lastStartOptions.prefilterProfile,
                    initialPromptLength = lastStartOptions.initialPromptLength },
                processedFinal = lastRealtimeTextFilterResult?.Text,
                textFilter = lastRealtimeTextFilterResult == null ? null : new
                {
                    dropped = lastRealtimeTextFilterResult.Dropped,
                    laughReplaced = lastRealtimeTextFilterResult.LaughReplaced,
                    ngMatched = lastRealtimeTextFilterResult.NgMatched,
                    reason = lastRealtimeTextFilterResult.Reason,
                },
            });
            }, () => Results.Ok(new { state = "stopped", discarded = true }));
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("realtime.stop_failed"));
        }
    }
    }
    finally { realtimeMutationGate.Release(); }
});

/* TTS endpoints */
app.MapGet("/api/tts/status", () =>
{
    var status = ttsRouter.GetStatus();
    return Results.Ok(new
    {
        available = status.Available,
        isSpeaking = status.IsSpeaking,
        error = status.Error,
        outputDeviceId = status.OutputDeviceId,
        outputDeviceName = status.OutputDeviceName,
        outputDeviceMode = status.OutputDeviceMode,
        wirelessLikely = status.WirelessLikely,
        preSilenceMs = status.PreSilenceMs,
        fallbackReason = status.FallbackReason,
        pendingCount = status.PendingCount,
        droppedCount = status.DroppedCount,
        lastTtsText = status.LastTtsText,
        lastTtsStartedAt = status.LastTtsStartedAt,
        monitorEnabled = status.MonitorEnabled,
        monitorVolumePercent = status.MonitorVolumePercent,
        wavOutputEnabled = ttsRouter.GetConfig().WavOutputEnabled,
        monitorFallbackReason = status.MonitorFallbackReason,
        // Phase 7.4: Multi-engine status
        engine = status.Engine,
        effectiveEngine = status.EffectiveEngine,
        engineAvailable = status.EngineAvailable,
        engineError = status.EngineError,
        voicevoxApi = status.VoicevoxApi,
        voicevoxSpeakerId = status.VoicevoxSpeakerId,
        voicevoxAutoEmotion = status.VoicevoxAutoEmotion,
        ttsReflectEmotion = status.TtsReflectEmotion,
        ttsReflectContourStrength = status.TtsReflectContourStrength,
        ttsReflectToneEmphasis = status.TtsReflectToneEmphasis,
        piperPlusModelId = status.PiperPlusModelId,
        piperPlusModelInstalled = status.PiperPlusModelInstalled,
        piperPlusRuntimeAvailable = status.PiperPlusRuntimeAvailable,
        voicevoxAvailable = status.VoicevoxAvailable,
        voicevoxError = status.VoicevoxError,
        piperPlusError = status.PiperPlusError,
        piperPlusModelPath = status.PiperPlusModelPath,
        kokoroModelId = status.KokoroModelId,
        kokoroModelInstalled = status.KokoroModelInstalled,
        kokoroError = status.KokoroError,
        mossModelId = status.MossModelId,
        mossVoiceId = status.MossVoiceId,
        mossModelInstalled = status.MossModelInstalled,
        mossError = status.MossError
    });
});

app.MapPost("/api/tts/config", async (HttpRequest request) =>
{
    try
    {
        var config = ttsService.GetConfig();
        var previousKokoro = (config.KokoroModelDir, config.KokoroSpeakerId, config.KokoroSpeed);
        var kokoroSelectionRequested = false;
        if (request.ContentLength is > 0)
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(request.Body);
                if (body.RootElement.TryGetProperty("engine", out var requestedEngine) &&
                    requestedEngine.ValueKind == JsonValueKind.String &&
                    string.Equals(requestedEngine.GetString(), TtsEngineKokoro, StringComparison.OrdinalIgnoreCase))
                    kokoroSelectionRequested = true;
                if (body.RootElement.TryGetProperty("kokoroModelId", out var requestedKokoro) &&
                    requestedKokoro.ValueKind == JsonValueKind.String && requestedKokoro.GetString() is string requestedId &&
                    !string.IsNullOrWhiteSpace(requestedId))
                {
                    kokoroSelectionRequested = true;
                    if (!MobMate.Core.Tts.KokoroTtsService.IsModelDirValid(Path.Combine(kokoroModelRoot, requestedId)))
                        return Results.BadRequest(ApiMessage("tts.kokoro_unavailable", "Kokoro model is incomplete or unreadable. Remove it and download it again."));
                }
                if (body.RootElement.TryGetProperty("outputDeviceId", out var devIdProp))
                {
                    config.OutputDeviceId = devIdProp.GetString() ?? string.Empty;
                }
                if (body.RootElement.TryGetProperty("outputDeviceName", out var devNameProp))
                {
                    config.OutputDeviceName = devNameProp.GetString() ?? string.Empty;
                }
                if (body.RootElement.TryGetProperty("preSilenceMs", out var preSilProp))
                {
                    config.PreSilenceMs = preSilProp.GetInt32();
                    if (config.PreSilenceMs < 0 || config.PreSilenceMs > 3000)
                        return Results.BadRequest(ApiMessage("tts.invalid_pre_silence_ms", "preSilenceMs must be between 0 and 3000"));
                }
                if (body.RootElement.TryGetProperty("minIntervalMs", out var minIntProp))
                {
                    config.MinIntervalMs = minIntProp.GetInt32();
                    if (config.MinIntervalMs < 0 || config.MinIntervalMs > 10000)
                        return Results.BadRequest(ApiMessage("tts.invalid_min_interval_ms", "minIntervalMs must be between 0 and 10000"));
                }
              if (body.RootElement.TryGetProperty("maxTextLength", out var maxLenProp))
                    {
                        config.MaxTextLength = maxLenProp.GetInt32();
                        if (config.MaxTextLength < 1 || config.MaxTextLength > 500)
                            return Results.BadRequest(ApiMessage("tts.invalid_max_text_length", "maxTextLength must be between 1 and 500"));
                    }
                if (body.RootElement.TryGetProperty("monitorVolumePercent", out var monVolProp))
                    {
                        config.MonitorVolumePercent = monVolProp.GetInt32();
                        // Accept only 0, 10, 20, ..., 100
                        if (config.MonitorVolumePercent < 0 || config.MonitorVolumePercent > 100
                            || config.MonitorVolumePercent % 10 != 0)
                            return Results.BadRequest(ApiMessage("tts.invalid_monitor_volume_percent", "monitorVolumePercent must be 0, 10, 20, ..., 100"));
                    }
                if (body.RootElement.TryGetProperty("wavOutputEnabled", out var wavOutputProp))
                    config.WavOutputEnabled = wavOutputProp.GetBoolean();
                // Phase 7.4: Multi-engine config fields
                if (body.RootElement.TryGetProperty("engine", out var engineProp))
                {
                    var engineVal = engineProp.GetString();
                    if (engineVal != null && engineVal.Length <= 30 && IsLiteralValue(engineVal, TtsEngineValues))
                    {
                        config.Engine = engineVal;
                    }
                    else if (engineVal != null)
                    {
                        return Results.BadRequest(ApiMessage("tts.invalid_engine", "engine must be one of: auto, windows_sapi, voicevox, piper, piper_plus, kokoro, moss"));
                    }
                }
                if (body.RootElement.TryGetProperty("voicevoxApi", out var vvApiProp))
                {
                    var vvApi = vvApiProp.GetString() ?? string.Empty;
                    // First implementation: restrict to localhost for security
                    if (!string.IsNullOrEmpty(vvApi) && !vvApi.StartsWith("http://127.0.0.1:") && !vvApi.StartsWith("http://localhost:"))
                    {
                        return Results.BadRequest(ApiMessage("tts.invalid_voicevox_api", "voicevoxApi must be empty or http://127.0.0.1:<port> / http://localhost:<port>"));
                    }
                    config.VoicevoxApi = vvApi;
                }
                if (body.RootElement.TryGetProperty("voicevoxSpeakerId", out var vvSpkProp))
                {
                    var vvSpk = vvSpkProp.GetString();
                    if (vvSpk != null && vvSpk.Length <= 10)
                        config.VoicevoxSpeakerId = vvSpk;
                }
                if (body.RootElement.TryGetProperty("voicevoxAutoEmotion", out var vvAeProp))
                {
                    config.VoicevoxAutoEmotion = vvAeProp.GetBoolean();
                }
                if (body.RootElement.TryGetProperty("ttsReflectEmotion", out var reProp))
                {
                    config.TtsReflectEmotion = reProp.GetBoolean();
                }
                if (body.RootElement.TryGetProperty("ttsReflectContourStrength", out var csProp))
                {
                    var cs = csProp.GetString();
                    if (cs != null && (cs == "mild" || cs == "normal" || cs == "strong"))
                        config.TtsReflectContourStrength = cs;
                    else if (cs != null)
                        return Results.BadRequest(ApiMessage("tts.invalid_reflect_contour_strength", "ttsReflectContourStrength must be mild, normal, or strong"));
                }
                if (body.RootElement.TryGetProperty("ttsReflectToneEmphasis", out var teProp))
                {
                    var te = teProp.GetString();
                    if (te != null && (te == "mild" || te == "normal" || te == "strong"))
                        config.TtsReflectToneEmphasis = te;
                    else if (te != null)
                        return Results.BadRequest(ApiMessage("tts.invalid_reflect_tone_emphasis", "ttsReflectToneEmphasis must be mild, normal, or strong"));
                }
                if (body.RootElement.TryGetProperty("piperPlusExePath", out var ppExeProp))
                {
                    var ppExe = ppExeProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(ppExe) && !System.IO.File.Exists(ppExe))
                        return Results.BadRequest(ApiMessage("tts.invalid_piper_plus_exe_path", "piperPlusExePath must be empty or an existing file"));
                    config.PiperPlusExePath = ppExe;
                }
                if (body.RootElement.TryGetProperty("piperPlusModelId", out var ppModelProp))
                {
                    var ppModel = ppModelProp.GetString();
                    if (ppModel != null && ppModel.Length <= 120)
                    {
                        config.PiperPlusModelId = ppModel;
                        var model = ResolvePiperPlusModel(ppModel);
                        config.PiperPlusModelPath = model?.ModelPath ?? string.Empty;
                    }
                    else if (ppModel != null)
                        return Results.BadRequest(ApiMessage("tts.invalid_piper_plus_model_id", "piperPlusModelId length must be <= 120"));
                }
                if (body.RootElement.TryGetProperty("piperPlusPersistent", out var ppPersProp))
                {
                    config.PiperPlusPersistent = ppPersProp.GetBoolean();
                }
                if (body.RootElement.TryGetProperty("kokoroModelId", out var kmIdProp))
                {
                    var kmId = kmIdProp.GetString();
                    if (kmId != null && kmId.Length <= 120)
                        config.KokoroModelId = kmId;
                }
                if (body.RootElement.TryGetProperty("kokoroSpeakerId", out var kmSidProp) && kmSidProp.TryGetInt32(out var kmSid))
                    config.KokoroSpeakerId = Math.Clamp(kmSid, 0, 999);
                if (body.RootElement.TryGetProperty("kokoroSpeed", out var kmSpProp) && kmSpProp.TryGetSingle(out var kmSp))
                    config.KokoroSpeed = Math.Clamp(kmSp, 0.5f, 2.0f);
                if (body.RootElement.TryGetProperty("mossModelId", out var mossIdProp))
                {
                    var mossId = mossIdProp.GetString();
                    if (mossId != null && mossId.Length <= 120)
                        config.MossModelId = mossId;
                }
                if (body.RootElement.TryGetProperty("mossVoiceId", out var mossVoiceProp))
                {
                    var mossVoice = mossVoiceProp.GetString();
                    if (!string.IsNullOrWhiteSpace(mossVoice) && mossVoice.Length <= 80)
                        config.MossVoiceId = mossVoice;
                }
                if (body.RootElement.TryGetProperty("mossMaxNewFrames", out var mossFramesProp) && mossFramesProp.TryGetInt32(out var mossFrames))
                    config.MossMaxNewFrames = Math.Clamp(mossFrames, 20, 400);
            }
            catch (JsonException) { /* use defaults */ }
        }
        if (string.IsNullOrWhiteSpace(config.VoicevoxApi))
            config.VoicevoxApi = "http://127.0.0.1:50021";
        if (string.IsNullOrWhiteSpace(config.PiperPlusExePath))
            config.PiperPlusExePath = ResolveDefaultPiperPlusExe(config);
        if (!string.IsNullOrWhiteSpace(config.PiperPlusModelId) && string.IsNullOrWhiteSpace(config.PiperPlusModelPath))
            config.PiperPlusModelPath = ResolvePiperPlusModel(config.PiperPlusModelId)?.ModelPath ?? string.Empty;
        // Resolve Kokoro model directory from model ID
        config.KokoroModelDir = ResolveKokoroModelDir(kokoroModelRoot, config.KokoroModelId) ?? string.Empty;
        config.MossModelDir = ResolveMossModelDir(mossModelRoot, config.MossModelId) ?? string.Empty;
        if (kokoroSelectionRequested)
        {
            if (!MobMate.Core.Tts.KokoroTtsService.IsModelDirValid(config.KokoroModelDir))
                return Results.BadRequest(ApiMessage("tts.kokoro_unavailable", "Kokoro model is incomplete or unreadable. Remove it and download it again."));
            // Initialize before committing other services or output-device side effects.
            kokoroService.SetConfig(config.KokoroModelDir, config.KokoroSpeakerId, config.KokoroSpeed);
            if (!kokoroService.IsModelInstalled)
            {
                var error = kokoroService.LastError ?? "Kokoro model is not ready.";
                kokoroService.SetConfig(previousKokoro.KokoroModelDir, previousKokoro.KokoroSpeakerId, previousKokoro.KokoroSpeed);
                return Results.BadRequest(ApiMessage("tts.kokoro_unavailable", error));
            }
        }
        if (!string.IsNullOrWhiteSpace(config.OutputDeviceId))
        {
            var deviceList = ttsService.EnumerateDevices();
            var rawDevice = deviceList.Devices.FirstOrDefault(d => string.Equals(d.Id, config.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
            if (rawDevice != null)
                config.OutputDeviceName = rawDevice.Name;
        }
        mobfyOnset.Invalidate();
        using var onsetRouteChange = ttsRouter.BeginOnsetRouteChange(config);
        ttsRouter.SetConfig(config);
        WarmUpMossIfSelected(config);
        if (!string.IsNullOrEmpty(config.OutputDeviceName))
            NativeLibraryBridge.SetTtsOutputDevice(ttsNativeExe, config.OutputDeviceName, config.OutputDeviceId);
        return Results.Ok(new { code = "tts.config_applied", message = "config applied" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, extensions: ApiProblemCode("tts.config_failed"));
    }
});

app.MapGet("/api/tts/devices", () =>
{
    var list = ttsService.EnumerateDevices();
    return Results.Ok(new
    {
        ok = list.Ok,
        code = list.Ok ? null : "tts.devices_failed",
        devices = list.Devices.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            friendlyName = d.FriendlyName,
            tokenId = d.TokenId,
            isDefault = d.IsDefault,
            wirelessLikely = d.WirelessLikely
        }),
        error = list.Error
    });
});

/* Phase 7.4: Engine availability endpoint */
app.MapGet("/api/tts/engines", () =>
{
    var config = ttsService.GetConfig();
    var status = ttsRouter.GetStatus();

    // Check VOICEVOX availability via HTTP (retry up to 3 times for startup delay)
    bool voicevoxAvailable = false;
    string? voicevoxError = null;
    var voicevoxSpeakers = new List<object>();
    var voicevoxApi = string.IsNullOrWhiteSpace(config.VoicevoxApi)
        ? "http://127.0.0.1:50021"
        : config.VoicevoxApi.TrimEnd('/');
    if (!string.IsNullOrEmpty(voicevoxApi))
    {
        const int maxRetries = 3;
        const int retryDelayMs = 5000;
        bool voicevoxChecked = false;

        for (int attempt = 0; attempt < maxRetries && !voicevoxChecked; attempt++)
        {
            if (attempt > 0)
                System.Threading.Thread.Sleep(retryDelayMs);

            try
            {
                using var handler = new System.Net.Http.HttpClientHandler();
                using var client = new System.Net.Http.HttpClient(handler);
                client.Timeout = TimeSpan.FromMilliseconds(800);
                var resp = client.GetAsync($"{voicevoxApi}/speakers").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                {
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(json);
                    voicevoxAvailable = true;
                    voicevoxChecked = true;
                    foreach (var speaker in doc.RootElement.EnumerateArray())
                    {
                        var speakerName = speaker.TryGetProperty("name", out var nameProp) ? nameProp.ToString() : null;
                        if (speaker.TryGetProperty("styles", out var stylesProp))
                        {
                            foreach (var style in stylesProp.EnumerateArray())
                            {
                                var styleId = style.TryGetProperty("id", out var sidProp) ? sidProp.ToString() : null;
                                var styleName = style.TryGetProperty("name", out var snameProp) ? snameProp.ToString() : null;
                                if (string.IsNullOrWhiteSpace(styleId))
                                    continue;
                                voicevoxSpeakers.Add(new
                                {
                                    id = styleId,
                                    name = string.IsNullOrWhiteSpace(styleName)
                                        ? speakerName
                                        : $"{speakerName} ({styleName})"
                                });
                            }
                        }
                    }
                }
                else
                {
                    voicevoxError = $"HTTP {(int)resp.StatusCode}";
                    voicevoxChecked = true; // non-2xx response, no need to retry
                }
            }
            catch (Exception ex)
            {
                voicevoxError = ex.Message.Split('\n')[0];
                // continue to next retry
            }
        }

        if (!voicevoxChecked)
        {
            voicevoxError = voicevoxError ?? "connection failed after retries";
        }
    }
    else
    {
        voicevoxError = "api not configured";
    }

    // Check Piper-Plus runtime availability
    bool piperRuntimeAvailable = false;
    string? piperError = null;
    var piperModels = EnumeratePiperPlusModels()
        .Select(m => new { id = m.Id, name = m.Name, path = m.Path, modelPath = m.ModelPath, configPath = m.ConfigPath, language = m.Language, license = m.License, sourcePageUrl = m.SourcePageUrl, runtime = m.Runtime })
        .ToArray();
    var standardPiperModels = piperModels.Where(m => string.Equals(m.runtime, TtsEnginePiper, StringComparison.OrdinalIgnoreCase)).ToArray();
    var piperPlusModels = piperModels.Where(m => string.Equals(m.runtime, TtsEnginePiperPlus, StringComparison.OrdinalIgnoreCase)).ToArray();
    string? piperExe = ResolveDefaultPiperPlusExe(config);
    if (!string.IsNullOrEmpty(piperExe) && System.IO.File.Exists(piperExe))
    {
        piperRuntimeAvailable = true;
    }
    else
    {
        piperError = "runtime not found";
    }

    var configModelId = config.PiperPlusModelId;
    bool piperModelInstalled = !string.IsNullOrEmpty(configModelId) && piperModels.Any(m => string.Equals(m.id, configModelId, StringComparison.OrdinalIgnoreCase));

    // Pre-evaluate disk-based model lists (used for availability + response body)
    var kokoroModelList = EnumerateKokoroModels(kokoroModelRoot).ToArray();
    var mossModelList = EnumerateMossModels(mossModelRoot).ToArray();

    return Results.Ok(new
    {
        engines = new object[]
        {
            new { id = TtsEngineWindowsSapi, name = "Windows", available = status.Available, error = (string?)null, errorCode = (string?)null },
            new { id = TtsEngineVoicevox, name = "VOICEVOX", available = voicevoxAvailable, error = (string?)voicevoxError, errorCode = voicevoxError == null ? null : "tts.voicevox_unavailable" },
            new { id = TtsEnginePiper, name = "Piper", available = piperRuntimeAvailable && standardPiperModels.Length > 0, error = (string?)piperError, errorCode = piperError == null ? null : "tts.piper_unavailable" },
            new { id = TtsEnginePiperPlus, name = "Piper-Plus", available = piperRuntimeAvailable && piperPlusModels.Length > 0, error = (string?)piperError, errorCode = piperError == null ? null : "tts.piper_plus_unavailable" },
            new { id = TtsEngineKokoro, name = "Kokoro", available = kokoroModelList.Any(), error = kokoroService.LastError, errorCode = kokoroModelList.Any() ? null : "tts.kokoro_unavailable" },
            new { id = TtsEngineMoss, name = "MOSS", available = mossModelList.Any(), error = mossService.LastError, errorCode = mossModelList.Any() ? null : "tts.moss_unavailable" }
        },
        piperModels = standardPiperModels,
        piperPlusModels = piperPlusModels,
        voicevoxSpeakers = voicevoxSpeakers,
        kokoroModels = kokoroModelList,
        mossModels = mossModelList
    });
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
    catch { return Results.BadRequest(ApiMessage("tts.voices.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiMessage("tts.voices.id_required", "id is required"));

    var voices = LoadPiperVoiceCatalog();
    var voice = voices.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice is null)
        return Results.NotFound(ApiMessage("tts.voices.not_found", $"voice '{id}' not found in catalog"));

    if (string.IsNullOrEmpty(voice.OnnxUrl))
        return Results.BadRequest(ApiMessage("tts.voices.no_url", "voice has no onnxUrl in catalog"));

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
    if (running && nativeResult is not null)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - voiceConversionLastDiagLog >= TimeSpan.FromSeconds(2))
        {
            voiceConversionLastDiagLog = now;
            voiceConversionLogger.LogInformation("[VC] live status: {Status}", nativeResult.RawJson);
        }
    }
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
            var create = NativeLibraryBridge.VoiceConversionCreate(ttsNativeExe, optionsJson, out voiceConversionSessionHandle);
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
        PrepareMobfyOnset(); // Recheck OFF/selection requests received while native startup held the session lock.
        var status = NativeLibraryBridge.VoiceConversionPollStatus(voiceConversionSessionHandle);
        mobfyOnset.ObserveStatus(status.RawJson);
        voiceConversionLogger.LogInformation("[VC] op={OperationId} action=first_poll returned success={Success} status={Status}", operationId, status.Success, status.RawJson);
        UnlockSteamAchievement(SteamAchievementService.FirstMobfyStart);
        return Results.Ok(new { ok = true, status = VoiceConversionNativeStatusObject(status) });
    }
});

app.MapPost("/api/voice-conversion/stop", () =>
{
    voiceConversionLogger.LogInformation("[VC] stop requested");
    lock (voiceConversionSessionLock)
    {
        mobfyOnset.Stop();
        if (voiceConversionSessionHandle == IntPtr.Zero)
        {
            voiceConversionLogger.LogInformation("[VC] stop: no active session");
            return Results.Ok(new { ok = true, status = new { ok = true, mode = "voice_conversion", created = false, running = false } });
        }
        var stop = NativeLibraryBridge.VoiceConversionStop(voiceConversionSessionHandle);
        voiceConversionLogger.LogInformation("[VC] Stop: success={Success} msg={Message}", stop.Success, stop.Message);
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
            return Results.BadRequest(ApiMessage("voice_conversion.not_created", "session not created; call /start first"));
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
        return Results.BadRequest(ApiMessage("voice_conversion.id_required", "id is required"));
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
    catch { return Results.BadRequest(ApiMessage("voice_conversion.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiMessage("voice_conversion.id_required", "id is required"));

    var model = LoadVoiceConversionCatalog().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    if (model is null)
        return Results.NotFound(ApiMessage("voice_conversion.not_found", $"model '{id}' not found in catalog"));

    if (model.RequiresUserAgreement && !acceptLicense)
        return Results.BadRequest(ApiMessage("voice_conversion.license_required", "license agreement is required"));

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
    catch { return Results.BadRequest(ApiMessage("voice_conversion.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(sourcePath))
        return Results.BadRequest(ApiMessage("voice_conversion.source_required", "sourcePath is required"));
    if (string.IsNullOrWhiteSpace(id))
        id = Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.IsNullOrWhiteSpace(displayName)) displayName = id;
    if (!string.Equals(engine, "rvc_onnx", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(engine, "seedvc_ncnn", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(ApiMessage("voice_conversion.invalid_engine", "engine must be rvc_onnx or seedvc_ncnn"));
    if (!acceptLicense)
        return Results.BadRequest(ApiMessage("voice_conversion.license_required", "confirm rights and usage terms with acceptLicense=true"));

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
    catch { return Results.BadRequest(ApiMessage("voice_conversion.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiMessage("voice_conversion.id_required", "id is required"));
    if (!acceptPthRisk)
        return Results.BadRequest(ApiMessage("voice_conversion.pth_risk_required", "acceptPthRisk=true is required"));
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

// ---- Phase 3.5: Voice Conversion offline DSP evaluation ----

app.MapPost("/api/voice-conversion/process-file", async (HttpRequest request, CancellationToken ct) =>
{
    // Expects JSON: { "inputPath": "path/to/input.wav", "outputPath": "path/to/output.wav", "options": {...} }
    string inputPath = "", outputPath = "";
    string optionsJson = "{}";
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = body.RootElement;
        if (root.TryGetProperty("inputPath", out var ip)) inputPath = ip.GetString() ?? "";
        if (root.TryGetProperty("outputPath", out var op)) outputPath = op.GetString() ?? "";
        if (root.TryGetProperty("options", out var opts)) optionsJson = opts.GetRawText();
    }
    catch { return Results.BadRequest(ApiMessage("voice_conversion.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        return Results.BadRequest(ApiMessage("voice_conversion.input_not_found", $"input file not found: {inputPath}"));
    if (string.IsNullOrWhiteSpace(outputPath))
        return Results.BadRequest(ApiMessage("voice_conversion.output_required", "outputPath is required"));

    try
    {
        // Read WAV file (expect 16-bit mono PCM).
        var wavBytes = await File.ReadAllBytesAsync(inputPath, ct);
        // Parse WAV header to extract sample rate and PCM data offset.
        if (wavBytes.Length < 44 || wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I')
            return Results.BadRequest(ApiMessage("voice_conversion.invalid_wav", "not a valid WAV file"));

        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
        int channels = BitConverter.ToInt16(wavBytes, 22);
        if (bitsPerSample != 16)
            return Results.BadRequest(ApiMessage("voice_conversion.unsupported_format", $"only 16-bit PCM supported, got {bitsPerSample}"));

        // Find data chunk.
        int dataOffset = 12;
        int dataSize = 0;
        while (dataOffset + 8 < wavBytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, dataOffset, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, dataOffset + 4);
            if (chunkId == "data") { dataSize = chunkSize; dataOffset += 8; break; }
            dataOffset += 8 + chunkSize;
        }
        if (dataSize == 0)
            return Results.BadRequest(ApiMessage("voice_conversion.no_data_chunk", "WAV data chunk not found"));

        int totalSamples = dataSize / 2; // 16-bit = 2 bytes per sample
        if (channels > 1) totalSamples /= channels; // use first channel only

        var input = new short[totalSamples];
        var output = new short[totalSamples];

        // Extract PCM samples (first channel if stereo).
        int sampleBytes = channels * 2;
        for (int i = 0; i < totalSamples && dataOffset + i * sampleBytes + 1 < wavBytes.Length; i++)
            input[i] = BitConverter.ToInt16(wavBytes, dataOffset + i * sampleBytes);

        // Inject sample rate into options if not set.
        if (!optionsJson.Contains("\"sampleRate\""))
            optionsJson = optionsJson.TrimEnd('}') + $",\"sampleRate\":{sampleRate}" + "}";
        if (optionsJson.StartsWith(",")) optionsJson = "{" + optionsJson;
        optionsJson = ResolveVoiceConversionModelOptionsJson(optionsJson);

        // Create temporary VC session for offline processing.
        var create = NativeLibraryBridge.VoiceConversionCreate(ttsNativeExe, optionsJson, out IntPtr tempHandle);
        if (!create.Success || tempHandle == IntPtr.Zero)
            return Results.Problem(detail: create.Message, statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            if (VoiceConversionRequested(optionsJson))
            {
                var load = NativeLibraryBridge.VoiceConversionLoadModels(tempHandle);
                if (!load.Success)
                    return Results.Problem(detail: load.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var result = NativeLibraryBridge.VoiceConversionProcessBuffer(tempHandle, input, output);
            if (!result.Success)
                return Results.Problem(detail: result.Message, statusCode: StatusCodes.Status500InternalServerError);

            // Write output WAV (16-bit mono PCM).
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                int pcmDataSize = totalSamples * 2;
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcmDataSize);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16); // subchunk1 size
                bw.Write((short)1); // PCM
                bw.Write((short)1); // mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2); // byte rate
                bw.Write((short)2); // block align
                bw.Write((short)16); // bits per sample
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(pcmDataSize);
                for (int i = 0; i < totalSamples; i++)
                    bw.Write(output[i]);
            }

            // Parse pressure JSON.
            object? pressureStats = null;
            if (!string.IsNullOrEmpty(result.RawJson) && result.RawJson != "{}")
            {
                try { pressureStats = JsonDocument.Parse(result.RawJson).RootElement.Clone(); }
                catch { pressureStats = result.RawJson; }
            }

            return Results.Ok(new
            {
                ok = true,
                inputPath,
                outputPath,
                sampleRate,
                totalSamples,
                durationMs = totalSamples * 1000.0 / sampleRate,
                pressure = pressureStats
            });
        }
        finally
        {
            NativeLibraryBridge.VoiceConversionDestroy(tempHandle);
        }
    }
    catch (Exception ex)
    {
        voiceConversionLogger.LogError(ex, "Voice conversion process-file failed");
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ---- Phase 3.5: Voice Conversion live-route inject (for bench) ----

app.MapPost("/api/voice-conversion/process-file-live", async (HttpRequest request, CancellationToken ct) =>
{
    // Uses the RUNNING live session's VAD+DSP pipeline (same as on_capture).
    // Expects JSON: { "inputPath": "...", "outputPath": "..." }
    string inputPath = "", outputPath = "";
    try
    {
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = body.RootElement;
        if (root.TryGetProperty("inputPath", out var ip)) inputPath = ip.GetString() ?? "";
        if (root.TryGetProperty("outputPath", out var op)) outputPath = op.GetString() ?? "";
    }
    catch { return Results.BadRequest(ApiMessage("voice_conversion.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        return Results.BadRequest(ApiMessage("voice_conversion.input_not_found", $"input file not found: {inputPath}"));
    if (string.IsNullOrWhiteSpace(outputPath))
        return Results.BadRequest(ApiMessage("voice_conversion.output_required", "outputPath is required"));
    if (voiceConversionSessionHandle == IntPtr.Zero)
        return Results.BadRequest(ApiMessage("voice_conversion.not_running", "VC session not running. Start Mobfy first."));

    try
    {
        var wavBytes = await File.ReadAllBytesAsync(inputPath, ct);
        if (wavBytes.Length < 44 || wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I')
            return Results.BadRequest(ApiMessage("voice_conversion.invalid_wav", "not a valid WAV file"));

        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
        int channels = BitConverter.ToInt16(wavBytes, 22);
        if (bitsPerSample != 16)
            return Results.BadRequest(ApiMessage("voice_conversion.unsupported_format", $"only 16-bit PCM supported, got {bitsPerSample}"));

        int dataOffset = 12;
        int dataSize = 0;
        while (dataOffset + 8 < wavBytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, dataOffset, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, dataOffset + 4);
            if (chunkId == "data") { dataSize = chunkSize; dataOffset += 8; break; }
            dataOffset += 8 + chunkSize;
        }
        if (dataSize == 0)
            return Results.BadRequest(ApiMessage("voice_conversion.no_data_chunk", "WAV data chunk not found"));

        int totalSamples = dataSize / 2;
        if (channels > 1) totalSamples /= channels;

        var input = new short[totalSamples];

        int sampleBytes = channels * 2;
        for (int i = 0; i < totalSamples && dataOffset + i * sampleBytes + 1 < wavBytes.Length; i++)
            input[i] = BitConverter.ToInt16(wavBytes, dataOffset + i * sampleBytes);

        var active = FindPcm16ActiveRange(input, sampleRate);
        var activeInput = new short[active.Length];
        Array.Copy(input, active.Start, activeInput, 0, active.Length);

        int liveSampleRate;
        NativeLibraryResult result;
        short[] injectInput;
        short[] injectOutput;
        lock (voiceConversionSessionLock)
        {
            if (voiceConversionSessionHandle == IntPtr.Zero)
                return Results.BadRequest(ApiMessage("voice_conversion.not_running", "VC session not running. Start Mobfy first."));
            liveSampleRate = VoiceConversionNativeSampleRate(voiceConversionSessionHandle, sampleRate);
            injectInput = liveSampleRate != sampleRate ? ResamplePcm16(activeInput, sampleRate, liveSampleRate) : activeInput;
            injectOutput = new short[injectInput.Length];

            // Native inject stops the live capture device while reusing the
            // same session/options/DSP route, then restarts capture afterward.
            result = NativeLibraryBridge.VoiceConversionInjectAndCapture(voiceConversionSessionHandle, injectInput, injectOutput);
        }
        if (!result.Success)
            return Results.Problem(detail: result.Message, statusCode: StatusCodes.Status500InternalServerError);

        var activeOutput = liveSampleRate != sampleRate ? ResamplePcm16(injectOutput, liveSampleRate, sampleRate, active.Length) : injectOutput;
        var output = new short[totalSamples];
        Array.Copy(activeOutput, 0, output, active.Start, Math.Min(activeOutput.Length, totalSamples - active.Start));

        // Write output WAV (16-bit mono PCM).
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var fs = new FileStream(outputPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            int pcmDataSize = totalSamples * 2;
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcmDataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(pcmDataSize);
            for (int i = 0; i < totalSamples; i++)
                bw.Write(output[i]);
        }

        object? pressureStats = null;
        if (!string.IsNullOrEmpty(result.RawJson) && result.RawJson != "{}")
        {
            try { pressureStats = JsonDocument.Parse(result.RawJson).RootElement.Clone(); }
            catch { pressureStats = result.RawJson; }
        }

        return Results.Ok(new
        {
            ok = true,
            liveRoute = true,
            inputPath,
            outputPath,
            sampleRate,
            totalSamples,
            durationMs = totalSamples * 1000.0 / sampleRate,
            pressure = pressureStats
        });
    }
    catch (Exception ex)
    {
        voiceConversionLogger.LogError(ex, "Voice conversion process-file-live failed");
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// ---- Rich TTS (Kokoro) APIs ----

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
    catch { return Results.BadRequest(ApiMessage("tts.rich.invalid_body", "invalid JSON body")); }

    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(ApiMessage("tts.rich.id_required", "id is required"));

    var voices = LoadRichVoiceCatalog();
    var voice = voices.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice is null)
        return Results.NotFound(ApiMessage("tts.rich.not_found", $"voice '{id}' not found in rich catalog"));

    // Route by engine type
    if (string.Equals(voice.Engine, "moss", StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(mossModelRoot);
        mossDlManager.StartDownload(voice.Id, mossModelRoot);
        return Results.Ok(new { ok = true, id });
    }

    if (string.IsNullOrEmpty(voice.TarUrl))
        return Results.BadRequest(ApiMessage("tts.rich.no_url", "voice has no tarUrl in catalog"));

    Directory.CreateDirectory(kokoroModelRoot);
    kokoroDlManager.StartDownload(voice, kokoroModelRoot);
    return Results.Ok(new { ok = true, id });
});

app.MapGet("/api/tts/rich/download/{id}/status", (string id) =>
{
    // Check MOSS manager first (non-idle takes priority), then Kokoro
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
    // Route delete by engine in catalog
    var voices = LoadRichVoiceCatalog();
    var voice = voices.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    if (voice != null && string.Equals(voice.Engine, "moss", StringComparison.OrdinalIgnoreCase))
        mossDlManager.Delete(id, mossModelRoot);
    else
        kokoroDlManager.Delete(id, kokoroModelRoot);
    return Results.Ok(new { ok = true, id });
});

app.MapPost("/api/tts/speak", async (HttpRequest request, CancellationToken ct) =>
{
    string text = string.Empty;
    string? engine = null;
    int? voicevoxSpeakerId = null;
    string? requestedKokoroModelId = null;
    bool interrupt = true;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("text", out var textProp))
            {
                text = textProp.GetString() ?? string.Empty;
            }
            if (body.RootElement.TryGetProperty("engine", out var engineProp))
            {
                engine = engineProp.GetString();
            }
            if (body.RootElement.TryGetProperty("kokoroModelId", out var kokoroModelProp))
                requestedKokoroModelId = kokoroModelProp.GetString();
            if (body.RootElement.TryGetProperty("voicevoxSpeakerId", out var speakerProp))
            {
                if (speakerProp.ValueKind == JsonValueKind.Number)
                    voicevoxSpeakerId = speakerProp.GetInt32();
                else if (speakerProp.ValueKind == JsonValueKind.String && int.TryParse(speakerProp.GetString(), out var sid))
                    voicevoxSpeakerId = sid;
            }
            if (body.RootElement.TryGetProperty("interrupt", out var interruptProp))
            {
                interrupt = interruptProp.GetBoolean();
            }
        }
        catch (JsonException) { }
    }

    if (string.IsNullOrWhiteSpace(text))
    {
        return Results.BadRequest(new { success = false, code = "tts.text_required", error = "text is required", message = "text is required" });
    }

    if (string.Equals(engine, TtsEngineKokoro, StringComparison.OrdinalIgnoreCase) &&
        (!kokoroService.IsModelInstalled ||
         (!string.IsNullOrWhiteSpace(requestedKokoroModelId) &&
          !string.Equals(Path.GetFileName(kokoroService.ModelDir), requestedKokoroModelId, StringComparison.OrdinalIgnoreCase))))
        return Results.Ok(new { success = false, code = "tts.speak_failed", error = "Kokoro model is not ready or selected.", fallbackReason = (string?)null });

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

    var result = await ttsRouter.SpeakAsync(text, engine, voicevoxSpeakerId, interrupt, ct);
    if (result.Success)
    {
        UnlockSteamAchievement(SteamAchievementService.FirstTtsSent);
        var effectiveEngine = ttsRouter.GetStatus().EffectiveEngine;
        if (string.Equals(engine, "voicevox", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectiveEngine, "voicevox", StringComparison.OrdinalIgnoreCase))
            UnlockSteamAchievement(SteamAchievementService.FirstVoicevoxTts);
    }
    return Results.Ok(new { success = result.Success, code = result.Success ? (string?)null : "tts.speak_failed", error = result.Error, fallbackReason = result.FallbackReason });
});

app.MapPost("/api/tts/stop", () =>
{
    var result = ttsRouter.StopAsync();
    return Results.Ok(new { success = result.Success, code = result.Success ? (string?)null : "tts.stop_failed", error = result.Error });
});

/* Pending speech endpoints */
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
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("mode", out var modeProp))
                cfg.Mode = modeProp.GetString() ?? cfg.Mode;
            if (body.RootElement.TryGetProperty("waitSeconds", out var waitProp) && waitProp.ValueKind == JsonValueKind.Number)
                cfg.WaitSeconds = waitProp.GetInt32();
            if (body.RootElement.TryGetProperty("maxQueue", out var maxProp) && maxProp.ValueKind == JsonValueKind.Number)
                cfg.MaxQueue = maxProp.GetInt32();
        }
        ttsRouter.InvalidateOnsetConfiguration();
        pendingSpeechService.ApplyConfig(cfg);
        ttsRouter.InvalidateOnsetConfiguration();
        var applied = pendingSpeechService.GetConfig();
        return Results.Ok(new { ok = true, mode = applied.Mode, waitSeconds = applied.WaitSeconds, maxQueue = applied.MaxQueue });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

app.MapGet("/api/pending-speech/status", () =>
{
    var status = pendingSpeechService.GetStatus();
    return Results.Ok(new
    {
        mode = status.Mode,
        waitSeconds = status.WaitSeconds,
        maxQueue = status.MaxQueue,
        pendingCount = status.PendingCount,
        remainingSeconds = status.RemainingSeconds,
        items = status.Items.Select(x => new { id = x.Id, slot = x.Slot, text = x.Text, remainingSeconds = x.RemainingSeconds })
    });
});

app.MapPost("/api/pending-speech/approve", async (HttpRequest request, CancellationToken ct) =>
{
    int slot = 1;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (body.RootElement.TryGetProperty("slot", out var slotProp) && slotProp.ValueKind == JsonValueKind.Number)
                slot = slotProp.GetInt32();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
        }
    }
    await pendingSpeechService.ApproveBySlotAsync(slot, (s, token) => SpeakDirectAsync(s, token, null), ct);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/pending-speech/cancel", async (HttpRequest request, CancellationToken ct) =>
{
    int slot = 1;
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (body.RootElement.TryGetProperty("slot", out var slotProp) && slotProp.ValueKind == JsonValueKind.Number)
                slot = slotProp.GetInt32();
        }
        catch (JsonException ex)
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

/* Radio chat endpoints (Step 2 minimal) */
app.MapGet("/api/radio/status", () =>
{
    var s = radioChatService.GetStatus();
    return Results.Ok(new
    {
        enabled = s.Enabled,
        overlayVisible = s.OverlayVisible,
        commandCount = s.CommandCount,
        commandsPath = s.CommandsPath,
        hotkeyRegistered = s.HotkeyRegistered
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
    if (request.ContentLength is > 0)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("enabled", out var enabledProp) && enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False)
                cfg.Enabled = enabledProp.GetBoolean();
            if (body.RootElement.TryGetProperty("commandsPath", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
                cfg.CommandsPath = pathProp.GetString() ?? cfg.CommandsPath;
            if (body.RootElement.TryGetProperty("shortcut", out var shProp) && shProp.ValueKind == JsonValueKind.Object)
            {
                if (shProp.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String) cfg.Shortcut.Display = d.GetString() ?? cfg.Shortcut.Display;
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
            await pendingSpeechService.ApproveBySlotAsync(digit, (text, token) => SpeakDirectAsync(text, token, null), ct);
            return Results.Ok(new { ok = true, action = "pending_approve", slot = digit });
        }

        var ok = await radioChatService.SpeakSlotAsync(page, digit, (text, token) => SpeakDirectAsync(text, token, null), ct);
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

// Phase 7A: Speaker identification API
app.MapPost("/api/speaker/reset", () =>
{
    var ret = NativeLibraryBridge.mmv2_speaker_reset();
    if (ret != 0) return Results.Ok(new { success = false, code = "speaker.reset_failed", adaptiveVocabReset = false });
    // Confirm the Windows profile used by realtime start is absent. Native can
    // return zero before learning its path or after an ignored removal failure.
    try
    {
        File.GetAttributes(Path.Combine(AppContext.BaseDirectory, "speaker_profile.json"));
        return Results.Ok(new { success = false, code = "speaker.reset_failed", adaptiveVocabReset = false });
    }
    catch (FileNotFoundException) { }
    catch (DirectoryNotFoundException) { }
    // End Windows profile confirmation.
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
    var lang = lastStartOptions.language ?? "auto";
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

app.MapPost("/api/speaker/set-enabled", async (HttpRequest request) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body);
        var enabled = doc.RootElement.TryGetProperty("enabled", out var enabledProp) && enabledProp.GetBoolean();
        var ret = NativeLibraryBridge.mmv2_speaker_set_enabled(enabled ? 1 : 0);
        return Results.Ok(new { success = ret == 0, code = ret == 0 ? (string?)null : "speaker.set_enabled_failed" });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(ApiMessage("request.invalid_json", ex.Message));
    }
});

const int RuntimeSchemaVersion = 1;
var runtimePath = Path.Combine(AppUserPaths.ConfigDir, "webhost-runtime.json");
var runtimePid = Environment.ProcessId;
var startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

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

var forcedBindUrl = Environment.GetEnvironmentVariable("MOBMATE_WEBHOST_BIND_URL");
var selectedBindUrl = !string.IsNullOrWhiteSpace(forcedBindUrl)
    ? forcedBindUrl
    : $"http://127.0.0.1:{SelectStableLoopbackPort()}";
app.Urls.Add(selectedBindUrl);
try
{
    await app.StartAsync();
    var selectedUrl = app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()?.Addresses
        .Select(address => new Uri(address))
        .Single(uri => uri.IsLoopback && uri.Port > 0)
        ?? throw new InvalidOperationException("Kestrel did not report its selected loopback URL.");
    var selectedBaseUrl = selectedUrl.GetLeftPart(UriPartial.Authority) + "/";
    WriteRuntimeFile(selectedUrl.Port);
    logger.LogInformation("[Runtime] selectedUrl={SelectedUrl} runtimeFile={RuntimeFile}", selectedBaseUrl, runtimePath);

    UnlockSteamAchievement(SteamAchievementService.ThanksForLaunching);
    steamAchievements.StartUsageTimer();
    var headless = args.Any(arg => string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase));
    if (!headless && OperatingSystem.IsWindows())
    {
        var uiThread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            using var shell = new MobMateWebShellForm(
                selectedBaseUrl,
                Path.Combine(AppUserPaths.V2Root, "WebView2"),
                Path.Combine(AppUserPaths.ConfigDir, "window-state.json"));
            app.Lifetime.ApplicationStopped.Register(() =>
            {
                try
                {
                    if (shell.IsHandleCreated)
                    {
                        shell.BeginInvoke((Action)(() => shell.Close()));
                    }
                }
                catch { }
            });

            Application.Run(shell);
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        while (uiThread.IsAlive && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            uiThread.Join(200);
        }
        app.Lifetime.StopApplication();
        if (uiThread.IsAlive)
        {
            uiThread.Join(3000);
        }
    }

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

internal sealed class MobMateWebShellForm : Form
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly string _windowStatePath;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Starting MobMate..."
    };

    public MobMateWebShellForm(string url, string userDataFolder, string windowStatePath)
    {
        _url = url;
        _userDataFolder = userDataFolder;
        _windowStatePath = windowStatePath;
        Text = "MobMate";
        MaximizeBox = false;
        MinimumSize = new Size(640, 360);
        Width = MinimumSize.Width;
        Height = 980;
        StartPosition = FormStartPosition.CenterScreen;
        ApplySavedWindowState();
        var exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
        if (exeIcon != null) Icon = exeIcon;
        Controls.Add(_webView);
        Controls.Add(_status);
        HandleCreated += (_, _) => ApplyDarkTitleBar();
        FormClosing += (_, _) => SaveWindowState();
        Shown += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await WaitForBackendAsync();
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.overflowX='hidden';document.body.style.overflowX='hidden';");
                }
                catch { }
            };
            _status.Visible = false;
            _webView.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            _status.Text = "MobMate startup failed. See logs for details.";
            MessageBox.Show(this, ex.Message, "MobMate startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task WaitForBackendAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(new Uri(new Uri(_url), "health"));
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(500);
        }

        throw new TimeoutException("MobMate backend did not become ready.");
    }

    private void ApplyDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            var enabled = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            var captionColor = 0x002b2f30;
            _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
            var textColor = 0x00ffffff;
            _ = DwmSetWindowAttribute(Handle, DwmwaTextColor, ref textColor, sizeof(int));
        }
        catch { }
    }

    private void ApplySavedWindowState()
    {
        try
        {
            if (!File.Exists(_windowStatePath)) return;
            var state = JsonSerializer.Deserialize<WindowStateDto>(File.ReadAllText(_windowStatePath));
            if (state == null) return;
            var width = Math.Max(state.Width, MinimumSize.Width);
            var height = Math.Max(state.Height, MinimumSize.Height);
            var bounds = new Rectangle(state.X, state.Y, width, height);
            if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        catch { }
    }

    private void SaveWindowState()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(_windowStatePath) ?? AppContext.BaseDirectory);
            var state = new WindowStateDto(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            File.WriteAllText(_windowStatePath, JsonSerializer.Serialize(state), Encoding.UTF8);
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private sealed record WindowStateDto(int X, int Y, int Width, int Height);
}

/// <summary>
/// ggml v1.8.5+ が登録する std::terminate ハンドラ (ggml_uncaught_exception) を無効化する。
/// ggml の getenv() は ucrtbase の _environ キャッシュを読むため、Win32 環境ブロックを
/// 更新する Environment.SetEnvironmentVariable では同期されない。ggml.dll と同じ ucrtbase の
/// _putenv_s を直接呼んで CRT 側環境を更新し、併せて子プロセス用に Win32 環境も設定する。
/// </summary>
internal static class GgmlEnvFix
{
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _putenv_s(string name, string value);

    public static void DisableBacktrace()
    {
        try { _putenv_s("GGML_NO_BACKTRACE", "1"); }
        catch { /* ucrtbase 解決失敗時は Win32 側のみで継続 */ }
        Environment.SetEnvironmentVariable("GGML_NO_BACKTRACE", "1");
    }
}

/// <summary>
/// Review prompt (P2) persisted state. Saved to AppUserPaths.ConfigDir/review_prompt.json
/// with camelCase property names: { launchCount, totalUsageMinutes, lastPromptUtc, promptCount, dismissedForever }.
/// </summary>
internal sealed class ReviewPromptState
{
    public int LaunchCount { get; set; }
    public int TotalUsageMinutes { get; set; }
    public string? LastPromptUtc { get; set; }
    public int PromptCount { get; set; }
    public bool DismissedForever { get; set; }
}
