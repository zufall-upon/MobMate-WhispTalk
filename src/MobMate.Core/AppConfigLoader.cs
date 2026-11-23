using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MobMate.Core;

/// <summary>
/// Loads AppConfig from JSON file, falling back to defaults.
/// </summary>
public static class AppConfigLoader
{
    public interface ISteamDlcGateService
    {
        SteamDlcGateResult CheckVulkanDlc(int appId, string appRoot);
        void Shutdown();
    }

    public sealed record WhisperRuntimeSelection(string WhisperDllDir, bool UseGpu, string Reason);
    public enum SteamworksStatus { Disabled, DllMissing, LoadFailed, InitFailed, Initialized, Error }
    public enum DlcOwnershipStatus { NotChecked, OwnedOnline, MissingOnline, OwnedOfflineCache, MissingOfflineCache, Disabled, Error }
    public sealed record SteamDlcGateResult(bool Allowed, string Reason, string ReasonCode, SteamworksStatus SteamStatus, DlcOwnershipStatus DlcStatus);
    public sealed record WhisperRuntimeSelectionDetailed(
        WhisperRuntimeSelection Selection,
        string ReasonCode,
        SteamworksStatus SteamStatus,
        DlcOwnershipStatus DlcStatus);

    private static readonly ISteamDlcGateService s_steamDlcGate = new SteamDlcGateService();
    private static readonly VulkanEntitlementGate s_vulkanEntitlementGate = VulkanEntitlementGate.CreateDefault();

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads config from the given path. Returns defaults when file is missing.
    /// Throws when file exists but is malformed.
    /// </summary>
    public static AppConfig Load(string? configPath = null)
    {
        var path = configPath ?? "mobmate.config.json";

        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var raw = File.ReadAllText(path);
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(raw, s_options);
            return config ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Config file '{path}' is malformed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Logs startup diagnostics: checks for required runtime assets and models.
    /// </summary>
    public static void LogStartupDiagnostics(AppConfig config, string appRoot, ILogger? logger = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Startup] === Runtime/Model Detection ===");

        // Native bridge
        var nativeBridge = ResolvePath(config.NativeExePath, appRoot);
        sb.AppendLine($"[Startup] native bridge: {(File.Exists(nativeBridge) ? "OK" : "MISSING")} ({config.NativeExePath})");

        // Whisper model
        var whisperModel = ResolvePath(config.DefaultModelPath, appRoot);
        sb.AppendLine($"[Startup] whisper model: {(File.Exists(whisperModel) ? "OK" : "MISSING")} ({config.DefaultModelPath})");

        // Whisper DLL dir
        var whisperDllDir = ResolvePath(config.DefaultWhisperDllDir, appRoot);
        bool hasWhisperDll = Directory.Exists(whisperDllDir) && File.Exists(Path.Combine(whisperDllDir, "whisper.dll"));
        bool hasGgmlVulkan = Directory.Exists(whisperDllDir) && File.Exists(Path.Combine(whisperDllDir, "ggml-vulkan.dll"));
        sb.AppendLine($"[Startup] whisper.dll: {(hasWhisperDll ? "OK" : "MISSING")}");
        sb.AppendLine($"[Startup] ggml-vulkan.dll: {(hasGgmlVulkan ? "OK" : "MISSING")} (required even for CPU mode)");

        // Moonshine DLL dir
        var moonshineDllDir = ResolvePath(config.DefaultMoonshineDllDir, appRoot);
        bool hasMoonshineDll = Directory.Exists(moonshineDllDir) && File.Exists(Path.Combine(moonshineDllDir, "moonshine.dll"));
        bool hasOnnxRuntime = Directory.Exists(moonshineDllDir) && File.Exists(Path.Combine(moonshineDllDir, "onnxruntime.dll"));
        sb.AppendLine($"[Startup] moonshine.dll: {(hasMoonshineDll ? "OK" : "MISSING")}");
        sb.AppendLine($"[Startup] onnxruntime.dll: {(hasOnnxRuntime ? "OK" : "MISSING")}");

        // Moonshine model root
        var moonshineModelRoot = ResolvePath(config.DefaultMoonshineModelRoot, appRoot);
        sb.AppendLine($"[Startup] moonshine models: {(Directory.Exists(moonshineModelRoot) ? "OK" : "MISSING")} ({config.DefaultMoonshineModelRoot})");

        // Web assets
        var webDir = Path.Combine(appRoot, "web");
        bool hasWeb = Directory.Exists(webDir) && File.Exists(Path.Combine(webDir, "index.html"));
        sb.AppendLine($"[Startup] web assets: {(hasWeb ? "OK" : "MISSING")}");

        // Presets
        var presetDir = Path.Combine(appRoot, "libs", "preset");
        bool hasPresets = Directory.Exists(presetDir);
        sb.AppendLine($"[Startup] presets: {(hasPresets ? "OK" : "MISSING")}");

        // Manifest (optional)
        if (string.IsNullOrWhiteSpace(config.DefaultManifestPath))
        {
            sb.AppendLine("[Startup] bench manifest: DISABLED (not configured - standard package)");
        }
        else
        {
            var manifestPath = ResolvePath(config.DefaultManifestPath, appRoot);
            sb.AppendLine($"[Startup] bench manifest: {(File.Exists(manifestPath) ? "OK" : "MISSING")} ({config.DefaultManifestPath})");
        }

        // Vulkan libs (optional, for GPU mode)
        var vulkanDir = ResolveSteamDlcPath(config.DefaultVulkanWhisperDllDir, appRoot, IsVulkanRuntimeDirectory);
        bool hasVulkan = Directory.Exists(vulkanDir) && File.Exists(Path.Combine(vulkanDir, "whisper.dll"));
        sb.AppendLine($"[Startup] libs/vulkan: {(hasVulkan ? "OK (GPU mode available)" : "NOT INCLUDED (CPU-only or separate download)")} ({vulkanDir})");

        var selectedWhisper = ResolveWhisperRuntime(config, appRoot);
        sb.AppendLine($"[Startup] whisper runtime selected: {(selectedWhisper.UseGpu ? "Vulkan" : "CPU")} dir={selectedWhisper.WhisperDllDir} reason={selectedWhisper.Reason}");

        if (logger != null)
            logger.LogInformation("{Diagnostics}", sb.ToString().TrimEnd());
        else
            Console.WriteLine(sb.ToString());
    }

    public static WhisperRuntimeSelection ResolveWhisperRuntime(AppConfig config, string appRoot)
    {
        return ResolveWhisperRuntimeDetailed(config, appRoot).Selection;
    }

    public static WhisperRuntimeSelectionDetailed ResolveWhisperRuntimeDetailed(AppConfig config, string appRoot)
    {
        var mode = (config.DefaultWhisperRuntimeMode ?? "cpu").Trim().ToLowerInvariant();
        if (mode == "cpu")
        {
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, "mode=cpu"),
                "mode_cpu",
                SteamworksStatus.Disabled,
                DlcOwnershipStatus.Disabled);
        }

        if (mode != "auto" && mode != "vulkan")
        {
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, $"unknown mode '{config.DefaultWhisperRuntimeMode}', fallback cpu"),
                "unknown_mode_fallback_cpu",
                SteamworksStatus.Disabled,
                DlcOwnershipStatus.Disabled);
        }

        var vulkanDir = ResolveSteamDlcPath(config.DefaultVulkanWhisperDllDir, appRoot, IsVulkanRuntimeDirectory);
        if (!Directory.Exists(vulkanDir) || !File.Exists(Path.Combine(vulkanDir, "whisper.dll")))
        {
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, "vulkan runtime files missing"),
                "vulkan_runtime_missing",
                SteamworksStatus.Disabled,
                DlcOwnershipStatus.NotChecked);
        }

        if (!IsSystemVulkanLoaderPresent())
        {
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, "vulkan-1.dll missing"),
                "system_vulkan_loader_missing",
                SteamworksStatus.Disabled,
                DlcOwnershipStatus.NotChecked);
        }

        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, "Windows 10 or later is required for Vulkan runtime; fallback cpu"),
                "windows_legacy_cpu_only",
                SteamworksStatus.Disabled,
                DlcOwnershipStatus.NotChecked);
        }

        if (config.EnableSteamDlcGate)
        {
            if (IsDemoBuild())
            {
                return new WhisperRuntimeSelectionDetailed(
                    new WhisperRuntimeSelection(vulkanDir, true, "demo policy: dlc gate skipped"),
                    "demo_policy_dlc_gate_skipped",
                    SteamworksStatus.Disabled,
                    DlcOwnershipStatus.Disabled);
            }

            var gate = s_steamDlcGate.CheckVulkanDlc(config.SteamVulkanDlcAppId, appRoot);
            if (!gate.Allowed)
            {
                return new WhisperRuntimeSelectionDetailed(
                    new WhisperRuntimeSelection(config.DefaultWhisperDllDir, false, gate.Reason),
                    gate.ReasonCode,
                    gate.SteamStatus,
                    gate.DlcStatus);
            }
            return new WhisperRuntimeSelectionDetailed(
                new WhisperRuntimeSelection(vulkanDir, true, "vulkan eligible"),
                "vulkan_eligible",
                gate.SteamStatus,
                gate.DlcStatus);
        }

        return new WhisperRuntimeSelectionDetailed(
            new WhisperRuntimeSelection(vulkanDir, true, "vulkan eligible"),
            "vulkan_eligible",
            SteamworksStatus.Disabled,
            DlcOwnershipStatus.Disabled);
    }

    /// <summary>
    /// Checks one DLC AppID. The configured Legacy Vulkan AppID also accepts the new Vulkan DLC AppID.
    /// Other AppIDs retain the original single-AppID behavior.
    /// </summary>
    public static SteamDlcGateResult CheckSteamVulkanDlc(int appId, string appRoot)
    {
        return s_steamDlcGate.CheckVulkanDlc(appId, appRoot);
    }

    private static bool IsSystemVulkanLoaderPresent()
    {
        if (OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libvulkan.so.1", out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return File.Exists("/usr/lib64/libvulkan.so.1")
                || File.Exists("/usr/lib/x86_64-linux-gnu/libvulkan.so.1");
        }

        var windir = Environment.GetEnvironmentVariable("WINDIR");
        return !string.IsNullOrWhiteSpace(windir) && File.Exists(Path.Combine(windir, "System32", "vulkan-1.dll"));
    }

    private static bool IsDemoBuild()
    {
        return BuildInfo.IsDemo;
    }

    private static string ResolvePath(string relativePath, string appRoot)
    {
        return Path.IsPathFullyQualified(relativePath)
            ? relativePath
            : Path.Combine(appRoot, relativePath);
    }

    public static string? GetSteamInstallRootFallback(string appRoot)
    {
        var current = new DirectoryInfo(appRoot);
        var parent = current.Parent;
        if (parent == null) return null;
        if (!string.Equals(current.Name, "stable", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(Path.Combine(parent.FullName, "MobMateLauncher.exe"))
            || File.Exists(Path.Combine(parent.FullName, "MobMateLinux"))
            ? parent.FullName
            : null;
    }

    private static string ResolveSteamDlcPath(string relativePath, string appRoot, Func<string, bool> exists)
    {
        if (Path.IsPathFullyQualified(relativePath)) return relativePath;

        var localPath = Path.Combine(appRoot, relativePath);
        if (exists(localPath)) return localPath;

        var installRoot = GetSteamInstallRootFallback(appRoot);
        if (installRoot != null)
        {
            var steamDlcPath = Path.Combine(installRoot, relativePath);
            if (exists(steamDlcPath)) return steamDlcPath;
        }

        return localPath;
    }

    private static bool IsVulkanRuntimeDirectory(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "whisper.dll"));
    }

    private sealed class SteamDlcGateService : ISteamDlcGateService
    {
        private const string OfflineSecretEnv = "MOBMATE_STEAM_DLC_SECRET";
        private const string PrefDlcToken = "vulkan_dlc_token";

        private sealed record OnlineCheck(bool Allowed, bool SteamChecked, string Reason, SteamworksStatus SteamStatus, DlcOwnershipStatus DlcStatus, string ReasonCode);

        private sealed record OfflineCheck(bool Allowed, string Reason, DlcOwnershipStatus DlcStatus, string ReasonCode);

        private static readonly object s_probeLock = new();
        private static (string Root, int AppId, DateTimeOffset At, SteamDlcGateResult Result)? s_probeResult;

        public static bool HasVulkanDlc(int appId, string appRoot, out string reason)
        {
            var result = CheckVulkanDlcCore(appId, appRoot);
            reason = result.Reason;
            return result.Allowed;
        }

        public SteamDlcGateResult CheckVulkanDlc(int appId, string appRoot)
        {
            return CheckVulkanDlcCore(appId, appRoot);
        }

        public void Shutdown()
        {
            try { Native.SteamAPI_Shutdown(); }
            catch { }
        }

        private static SteamDlcGateResult CheckVulkanDlcCore(int appId, string appRoot)
        {
            return appId == BuildInfo.SteamVulkanDlcAppId
                ? s_vulkanEntitlementGate.Check(appId, appRoot)
                : CheckSingleDlcCore(appId, appRoot);
        }

        private static SteamDlcGateResult CheckSingleDlcCore(int appId, string appRoot)
        {
            if (appId <= 0)
            {
                return new SteamDlcGateResult(false, "steam dlc app id is not configured", "steam_dlc_appid_not_configured", SteamworksStatus.Disabled, DlcOwnershipStatus.Error);
            }

            if (OperatingSystem.IsWindows() && !SteamLaunchContext.AllowPersistentConnection && !SteamDlcProbe.IsChild)
            {
                // Same on-demand interval as the Vulkan gate; retained in the parent.
                var root = Path.GetFullPath(appRoot);
                lock (s_probeLock)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (s_probeResult is { } memo && memo.Root == root && memo.AppId == appId
                        && now - memo.At < TimeSpan.FromSeconds(30)) return memo.Result;
                    var result = SteamDlcProbe.Run<SteamDlcGateResult>(reason => new(false, reason,
                        "steam_entitlement_unavailable", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked),
                        "single", root, appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    s_probeResult = (root, appId, now, result);
                    return result;
                }
            }

            var online = TrySteamOnline(appId, appRoot);
            if (online.Allowed)
            {
                SaveV1PreferenceToken(appId);
                return new SteamDlcGateResult(true, online.Reason, "dlc_owned_online", online.SteamStatus, online.DlcStatus);
            }

            if (online.SteamChecked)
            {
                DeleteV1PreferenceToken();
                return new SteamDlcGateResult(false, online.Reason, online.ReasonCode, online.SteamStatus, online.DlcStatus);
            }

            var offline = TryOfflineToken(appId);
            if (offline.Allowed)
            {
                return new SteamDlcGateResult(true, offline.Reason, offline.ReasonCode, online.SteamStatus, offline.DlcStatus);
            }

            return new SteamDlcGateResult(false, $"{online.Reason}; {offline.Reason}", offline.ReasonCode, online.SteamStatus, offline.DlcStatus);
        }

        private static OnlineCheck TrySteamOnline(int appId, string appRoot)
        {
            if (OperatingSystem.IsLinux())
            {
                return TrySteamOnlineLinux(appId, appRoot);
            }

            var steamDll = Path.Combine(appRoot, "steam_api64.dll");
            var steamHandle = IntPtr.Zero;
            if (File.Exists(steamDll))
            {
                try { steamHandle = NativeLibrary.Load(steamDll); }
                catch (Exception ex)
                {
                    return new OnlineCheck(false, false, $"steam_api64.dll load failed: {ex.Message}", SteamworksStatus.LoadFailed, DlcOwnershipStatus.NotChecked, "steam_dll_load_failed");
                }
            }
            else
            {
                return new OnlineCheck(false, false, "steam_api64.dll not found", SteamworksStatus.DllMissing, DlcOwnershipStatus.NotChecked, "steam_dll_missing");
            }

            // Keep the loaded Windows module pinned, as before, until process exit.
            return TrySteamOnlineWindowsLoaded(appId, steamHandle);
        }

        private static OnlineCheck TrySteamOnlineWindowsLoaded(int appId, IntPtr steamHandle)
        {
            var initialized = false;
            try
            {
                if (!GetSteamInitDelegate(steamHandle)())
                {
                    return new OnlineCheck(false, false, "steam api init failed", SteamworksStatus.InitFailed, DlcOwnershipStatus.NotChecked, "steam_init_failed");
                }
                initialized = true;

                var apps = GetAppsDelegate(steamHandle)();
                if (apps == IntPtr.Zero)
                {
                    return new OnlineCheck(false, false, "steam apps interface unavailable", SteamworksStatus.InitFailed, DlcOwnershipStatus.NotChecked, "steam_apps_interface_unavailable");
                }

                var owned = GetSteamDelegate<Native.SteamAPIBIsDlcInstalledFn>(steamHandle, "SteamAPI_ISteamApps_BIsDlcInstalled")(apps, appId);
                return owned
                    ? new OnlineCheck(true, true, "steam dlc owned", SteamworksStatus.Initialized, DlcOwnershipStatus.OwnedOnline, "dlc_owned_online")
                    : new OnlineCheck(false, true, "steam dlc not owned", SteamworksStatus.Initialized, DlcOwnershipStatus.MissingOnline, "dlc_missing_online");
            }
            catch (DllNotFoundException)
            {
                return new OnlineCheck(false, false, "steam_api64.dll not found", SteamworksStatus.DllMissing, DlcOwnershipStatus.NotChecked, "steam_dll_missing");
            }
            catch (EntryPointNotFoundException ex)
            {
                return new OnlineCheck(false, false, $"steam api entrypoint missing: {ex.TargetSite?.Name ?? ex.Message}", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked, "steam_entrypoint_missing");
            }
            catch (Exception ex)
            {
                return new OnlineCheck(false, false, $"steam api error: {ex.Message}", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked, "steam_api_error");
            }
            finally
            {
                if (initialized)
                {
                    try { GetSteamDelegate<Native.SteamAPIShutdownFn>(steamHandle, "SteamAPI_Shutdown")(); }
                    catch { }
                }
            }
        }

        private static OnlineCheck TrySteamOnlineLinux(int appId, string appRoot)
        {
            IntPtr handle = IntPtr.Zero;
            var loadedFrom = string.Empty;
            var invalidCandidates = new List<string>();
            foreach (var candidate in GetLinuxSteamApiCandidates(appRoot))
            {
                try
                {
                    if (candidate == "libsteam_api.so")
                    {
                        if (!NativeLibrary.TryLoad(candidate, out handle)) continue;
                    }
                    else
                    {
                        if (!File.Exists(candidate)) continue;
                        handle = NativeLibrary.Load(candidate);
                    }
                    if (!HasRequiredLinuxSteamExports(handle))
                    {
                        invalidCandidates.Add(candidate);
                        try { NativeLibrary.Free(handle); } catch { }
                        handle = IntPtr.Zero;
                        continue;
                    }
                    loadedFrom = candidate;
                    break;
                }
                catch
                {
                    handle = IntPtr.Zero;
                }
            }

            if (handle == IntPtr.Zero)
            {
                if (invalidCandidates.Count > 0)
                {
                    return new OnlineCheck(false, false, $"libsteam_api.so missing required exports: {string.Join(", ", invalidCandidates)}", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked, "steam_entrypoint_missing");
                }
                return new OnlineCheck(false, false, "libsteam_api.so not found", SteamworksStatus.DllMissing, DlcOwnershipStatus.NotChecked, "steam_dll_missing");
            }

            var initialized = false;
            try
            {
                var init = GetSteamInitDelegate(handle);
                var appsFn = GetAppsDelegate(handle);
                var isDlcInstalled = GetSteamDelegate<Native.SteamAPIBIsDlcInstalledFn>(handle, "SteamAPI_ISteamApps_BIsDlcInstalled");
                var shutdown = GetSteamDelegate<Native.SteamAPIShutdownFn>(handle, "SteamAPI_Shutdown");

                if (!init())
                {
                    return new OnlineCheck(false, false, $"steam api init failed ({loadedFrom})", SteamworksStatus.InitFailed, DlcOwnershipStatus.NotChecked, "steam_init_failed");
                }
                initialized = true;

                var apps = appsFn();
                if (apps == IntPtr.Zero)
                {
                    return new OnlineCheck(false, false, "steam apps interface unavailable", SteamworksStatus.InitFailed, DlcOwnershipStatus.NotChecked, "steam_apps_interface_unavailable");
                }

                var owned = isDlcInstalled(apps, appId);
                return owned
                    ? new OnlineCheck(true, true, "steam dlc owned", SteamworksStatus.Initialized, DlcOwnershipStatus.OwnedOnline, "dlc_owned_online")
                    : new OnlineCheck(false, true, "steam dlc not owned", SteamworksStatus.Initialized, DlcOwnershipStatus.MissingOnline, "dlc_missing_online");
            }
            catch (EntryPointNotFoundException ex)
            {
                return new OnlineCheck(false, false, $"steam api entrypoint missing: {ex.Message}", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked, "steam_entrypoint_missing");
            }
            catch (Exception ex)
            {
                return new OnlineCheck(false, false, $"steam api error: {ex.Message}", SteamworksStatus.Error, DlcOwnershipStatus.NotChecked, "steam_api_error");
            }
            finally
            {
                if (initialized)
                {
                    try { GetSteamDelegate<Native.SteamAPIShutdownFn>(handle, "SteamAPI_Shutdown")(); }
                    catch { }
                }
                try { NativeLibrary.Free(handle); }
                catch { }
            }
        }

        private static IEnumerable<string> GetLinuxSteamApiCandidates(string appRoot)
        {
            var fromEnv = Environment.GetEnvironmentVariable("MOBMATE_STEAM_API_SO");
            if (!string.IsNullOrWhiteSpace(fromEnv)) yield return fromEnv;
            yield return Path.Combine(appRoot, "libsteam_api.so");
            yield return Path.Combine(appRoot, "libs", "steam", "libsteam_api.so");
            yield return Path.Combine(appRoot, "linux64", "libsteam_api.so");
            yield return "libsteam_api.so";
        }

        private static T GetSteamDelegate<T>(IntPtr handle, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(handle, name, out var symbol) || symbol == IntPtr.Zero)
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(symbol);
        }

        private static Native.SteamAPIInitFn GetSteamInitDelegate(IntPtr handle)
        {
            if (NativeLibrary.TryGetExport(handle, "SteamAPI_Init", out var init) && init != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<Native.SteamAPIInitFn>(init);
            if (NativeLibrary.TryGetExport(handle, "SteamAPI_InitSafe", out var initSafe) && initSafe != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<Native.SteamAPIInitFn>(initSafe);
            throw new EntryPointNotFoundException("SteamAPI_Init/SteamAPI_InitSafe");
        }

        private static Native.SteamAPISteamAppsFn GetAppsDelegate(IntPtr handle)
        {
            return Marshal.GetDelegateForFunctionPointer<Native.SteamAPISteamAppsFn>(SteamDlcProbe.ResolveInterfaceGetter(handle, "Apps"));
        }

        private static bool HasRequiredLinuxSteamExports(IntPtr handle)
        {
            try { SteamDlcProbe.ResolveInterfaceGetter(handle, "Apps"); }
            catch (EntryPointNotFoundException) { return false; }
            return (NativeLibrary.TryGetExport(handle, "SteamAPI_Init", out var init) && init != IntPtr.Zero
                    || NativeLibrary.TryGetExport(handle, "SteamAPI_InitSafe", out var initSafe) && initSafe != IntPtr.Zero)
                && NativeLibrary.TryGetExport(handle, "SteamAPI_ISteamApps_BIsDlcInstalled", out var dlc) && dlc != IntPtr.Zero
                && NativeLibrary.TryGetExport(handle, "SteamAPI_Shutdown", out var shutdown) && shutdown != IntPtr.Zero;
        }

        private static OfflineCheck TryOfflineToken(int appId)
        {
            var secret = Environment.GetEnvironmentVariable(OfflineSecretEnv);
            if (string.IsNullOrEmpty(secret))
            {
                secret = BuildInfo.SecretKey;
            }

            if (string.IsNullOrEmpty(secret))
            {
                return new OfflineCheck(false, $"offline token disabled ({OfflineSecretEnv} not set and build local secret is empty)", DlcOwnershipStatus.Disabled, "offline_token_disabled");
            }

            var token = ReadV1PreferenceToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new OfflineCheck(false, "offline token not found", DlcOwnershipStatus.MissingOfflineCache, "offline_token_missing");
            }

            var trimmedToken = token.Trim();
            var ok = GenerateV1TokenCandidates(appId, secret).Any(expected => FixedTimeEqualsHex(trimmedToken, expected));
            return ok
                ? new OfflineCheck(true, "offline token valid", DlcOwnershipStatus.OwnedOfflineCache, "offline_token_valid")
                : new OfflineCheck(false, "offline token invalid", DlcOwnershipStatus.MissingOfflineCache, "offline_token_invalid");
        }

        private static string? ReadV1PreferenceToken()
        {
            if (!OperatingSystem.IsWindows()) return null;

            var steamUserId = GetMostRecentSteamUserId();
            var key = PrefDlcToken + (steamUserId is null ? string.Empty : "_" + steamUserId);
            using var prefs = Registry.CurrentUser.OpenSubKey(@"Software\JavaSoft\Prefs\whisper");
            return prefs?.GetValue(key) as string ?? prefs?.GetValue(key.ToLowerInvariant()) as string;
        }

        private static string? GetMostRecentSteamUserId()
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = steamKey?.GetValue("SteamPath") as string;
                if (string.IsNullOrWhiteSpace(steamPath)) return null;

                var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (!File.Exists(loginUsers)) return null;

                var text = File.ReadAllText(loginUsers);
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    text,
                    "\\\"(?<id>\\d{15,})\\\"\\s*\\{(?<body>[^}]*)\\}",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var body = match.Groups["body"].Value;
                    if (body.Contains("\"mostrecent\"", StringComparison.OrdinalIgnoreCase) && body.Contains("\"1\""))
                    {
                        return match.Groups["id"].Value;
                    }
                }
            }
            catch { }

            return null;
        }

        private static IEnumerable<string> GenerateV1TokenCandidates(int appId, string secret)
        {
            return GetV1MachineIdCandidates(secret)
                .Select(machineId => HmacHex(secret, $"{appId}|{machineId}"))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetV1MachineIdCandidates(string secret)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsV1TokenNicCandidate(nic)) continue;

                    var mac = nic.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length == 0) continue;
                    var machineId = HmacHex(secret, Convert.ToHexString(mac).ToLowerInvariant());
                    if (emitted.Add(machineId)) candidates.Add(machineId);
                }

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    var mac = nic.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length == 0) continue;
                    var machineId = HmacHex(secret, Convert.ToHexString(mac).ToLowerInvariant());
                    if (emitted.Add(machineId)) candidates.Add(machineId);
                }
            }
            catch { }

            var userMachineId = HmacHex(secret, Environment.UserName ?? "unknown");
            if (emitted.Add(userMachineId)) candidates.Add(userMachineId);

            var fallbackMachineId = "fallback";
            if (emitted.Add(fallbackMachineId)) candidates.Add(fallbackMachineId);
            return candidates;
        }

        private static bool IsV1TokenNicCandidate(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                nic.OperationalStatus != OperationalStatus.Up)
            {
                return false;
            }

            var label = $"{nic.Name} {nic.Description}";
            string[] virtualHints =
            {
                "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap", "tunnel",
                "wintun", "wireguard", "zerotier", "tailscale", "loopback", "pseudo"
            };
            return !virtualHints.Any(hint => label.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        private static string HmacHex(string secret, string value)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }

        private static bool FixedTimeEqualsHex(string actual, string expected)
        {
            var a = Encoding.ASCII.GetBytes(actual.ToLowerInvariant());
            var b = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static void SaveV1PreferenceToken(int appId)
        {
            if (!OperatingSystem.IsWindows()) return;
            var secret = Environment.GetEnvironmentVariable(OfflineSecretEnv);
            if (string.IsNullOrEmpty(secret)) secret = BuildInfo.SecretKey;
            if (string.IsNullOrEmpty(secret)) return; // offline token disabled

            var token = GenerateV1TokenCandidates(appId, secret).FirstOrDefault();
            if (token == null) return;

            var steamUserId = GetMostRecentSteamUserId();
            var key = PrefDlcToken + (steamUserId is null ? string.Empty : "_" + steamUserId);
            try
            {
                using var prefs = Registry.CurrentUser.CreateSubKey(@"Software\JavaSoft\Prefs\whisper", writable: true);
                prefs.SetValue(key, token);
                Console.WriteLine($"[DLC] offline token written (key={key})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DLC] offline token write failed: {ex.Message}");
            }
        }

        private static void DeleteV1PreferenceToken()
        {
            if (!OperatingSystem.IsWindows()) return;
            var steamUserId = GetMostRecentSteamUserId();
            var key = PrefDlcToken + (steamUserId is null ? string.Empty : "_" + steamUserId);
            try
            {
                using var prefs = Registry.CurrentUser.OpenSubKey(@"Software\JavaSoft\Prefs\whisper", writable: true);
                if (prefs != null)
                {
                    prefs.DeleteValue(key, throwOnMissingValue: false);
                    prefs.DeleteValue(key.ToLowerInvariant(), throwOnMissingValue: false);
                    Console.WriteLine($"[DLC] offline token deleted (key={key})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DLC] offline token delete failed: {ex.Message}");
            }
        }

        private static class Native
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            public delegate bool SteamAPIInitFn();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate IntPtr SteamAPISteamAppsFn();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            public delegate bool SteamAPIBIsDlcInstalledFn(IntPtr apps, int appId);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate void SteamAPIShutdownFn();

            [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
            public static extern void SteamAPI_Shutdown();
        }
    }
}
