using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MobMate.Core;

public sealed class SteamAchievementService : IDisposable
{
    private const int FullAppId = 4261090;
    private const int DemoAppId = 4448740;

    public const string ThanksForLaunching = "THANKS_FOR_LAUNCHING";
    public const string SetupComplete = "SETUP_COMPLETE";
    public const string VirtualAudioReady = "VIRTUAL_AUDIO_READY";
    public const string First30Minutes = "FIRST_30_MINUTES";
    public const string FirstSessionStart = "FIRST_SESSION_START";
    public const string FirstTtsSent = "FIRST_TTS_SENT";
    public const string FirstVoicevoxTts = "FIRST_VOICEVOX_TTS";
    public const string FirstMobfyStart = "FIRST_MOBFY_START";
    public const string FirstVcModelImport = "FIRST_VC_MODEL_IMPORT";
    public const string FirstHearingStart = "FIRST_HEARING_START";
    public const string FirstTranslatedOutput = "FIRST_TRANSLATED_OUTPUT";
    public const string FirstRadioChat = "FIRST_RADIO_CHAT";
    public const string FirstDiscordGuide = "FIRST_DISCORD_GUIDE";

    private readonly string _appRoot;
    private readonly ILogger? _logger;
    private readonly bool _enabled;
    private readonly object _lock = new();
    private Timer? _usageTimer;
    private UsageState _usageState = new();

    public SteamAchievementService(string appRoot, ILogger? logger = null)
    {
        _appRoot = appRoot;
        _logger = logger;
        _enabled = SteamLaunchContext.AllowPersistentConnection;
    }

    public void Unlock(string achievementId)
    {
        if (string.IsNullOrWhiteSpace(achievementId)) return;
        lock (_lock)
        {
            try
            {
                UnlockCore(achievementId);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[SteamAchievement] unlock failed: {AchievementId}", achievementId);
            }
        }
    }

    public void StartUsageTimer()
    {
        if (!_enabled) return;
        _usageState = LoadUsageState();
        if (_usageState.TotalMinutes >= 30)
        {
            Unlock(First30Minutes);
            return;
        }

        _usageTimer ??= new Timer(_ =>
        {
            lock (_lock)
            {
                _usageState.TotalMinutes += 5;
                SaveUsageState(_usageState);
                if (_usageState.TotalMinutes >= 30)
                    UnlockCore(First30Minutes);
            }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        _usageTimer?.Dispose();
    }

    private void UnlockCore(string achievementId)
    {
        if (!_enabled) return;
        using var api = SteamApiSession.TryCreate(_appRoot, _logger);
        if (api == null)
        {
            _logger?.LogWarning("[SteamAchievement] Steam API unavailable: {AchievementId}", achievementId);
            return;
        }
        if (api.GetAchievement(achievementId, out var achieved) && achieved) return;
        if (!api.SetAchievement(achievementId))
        {
            _logger?.LogWarning("[SteamAchievement] SetAchievement failed: {AchievementId}", achievementId);
            return;
        }
        _logger?.LogInformation("[SteamAchievement] SetAchievement result=true TargetId={AchievementId}", achievementId);
        if (!api.StoreStats())
        {
            _logger?.LogWarning("[SteamAchievement] StoreStats failed: {AchievementId}", achievementId);
            return;
        }
        _logger?.LogInformation("[SteamAchievement] StoreStats result=true TargetId={AchievementId}", achievementId);
        _logger?.LogInformation("[SteamAchievement] unlocked: {AchievementId}", achievementId);
    }

    private static string UsageStatePath => Path.Combine(AppUserPaths.ConfigDir, "steam_achievement_usage.json");

    private static UsageState LoadUsageState()
    {
        try
        {
            if (!File.Exists(UsageStatePath)) return new UsageState();
            return JsonSerializer.Deserialize<UsageState>(File.ReadAllText(UsageStatePath)) ?? new UsageState();
        }
        catch { return new UsageState(); }
    }

    private static void SaveUsageState(UsageState state)
    {
        try
        {
            Directory.CreateDirectory(AppUserPaths.ConfigDir);
            File.WriteAllText(UsageStatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch { }
    }

    private sealed class UsageState
    {
        public int TotalMinutes { get; set; }
    }

    private sealed class SteamApiSession : IDisposable
    {
        private readonly IntPtr _linuxHandle;
        private readonly Native.SteamAPIShutdownFn? _linuxShutdown;
        private readonly Native.SteamAPIRunCallbacksFn? _linuxRunCallbacks;
        private readonly Native.SteamAPISteamUtilsFn? _utilsFn;
        private readonly Native.SteamAPIGetAppIdFn? _getAppIdFn;
        private readonly Native.SteamAPISteamUserStatsFn _statsFn;
        private readonly Native.SteamAPIRequestCurrentStatsFn? _requestCurrentStatsFn;
        private readonly Native.SteamAPIGetAchievementFn _getFn;
        private readonly Native.SteamAPISetAchievementFn _setFn;
        private readonly Native.SteamAPIStoreStatsFn _storeFn;
        private bool _initialized;

        private SteamApiSession(
            IntPtr linuxHandle,
            Native.SteamAPIShutdownFn? linuxShutdown,
            Native.SteamAPIRunCallbacksFn? linuxRunCallbacks,
            Native.SteamAPISteamUtilsFn? utilsFn,
            Native.SteamAPIGetAppIdFn? getAppIdFn,
            Native.SteamAPISteamUserStatsFn statsFn,
            Native.SteamAPIRequestCurrentStatsFn? requestCurrentStatsFn,
            Native.SteamAPIGetAchievementFn getFn,
            Native.SteamAPISetAchievementFn setFn,
            Native.SteamAPIStoreStatsFn storeFn)
        {
            _linuxHandle = linuxHandle;
            _linuxShutdown = linuxShutdown;
            _linuxRunCallbacks = linuxRunCallbacks;
            _utilsFn = utilsFn;
            _getAppIdFn = getAppIdFn;
            _statsFn = statsFn;
            _requestCurrentStatsFn = requestCurrentStatsFn;
            _getFn = getFn;
            _setFn = setFn;
            _storeFn = storeFn;
            _initialized = true;
        }

        public static SteamApiSession? TryCreate(string appRoot, ILogger? logger)
        {
            return OperatingSystem.IsLinux()
                ? TryCreateLinux(appRoot, logger)
                : TryCreateWindows(appRoot, logger);
        }

        public bool GetAchievement(string id, out bool achieved)
        {
            achieved = false;
            var stats = _statsFn();
            return stats != IntPtr.Zero && _getFn(stats, id, out achieved);
        }

        public bool RequestCurrentStats()
        {
            if (_requestCurrentStatsFn == null) return true;
            var stats = _statsFn();
            return stats != IntPtr.Zero && _requestCurrentStatsFn(stats);
        }

        public bool SetAchievement(string id)
        {
            var stats = _statsFn();
            return stats != IntPtr.Zero && _setFn(stats, id);
        }

        public bool StoreStats()
        {
            var stats = _statsFn();
            return stats != IntPtr.Zero && _storeFn(stats);
        }

        public uint GetCurrentAppId()
        {
            var utils = _utilsFn?.Invoke() ?? IntPtr.Zero;
            return utils != IntPtr.Zero ? _getAppIdFn?.Invoke(utils) ?? 0 : 0;
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try
                {
                    if (_linuxShutdown != null) _linuxShutdown.Invoke();
                    else Native.SteamAPI_Shutdown();
                }
                catch { }
                _initialized = false;
            }
            if (_linuxHandle != IntPtr.Zero)
            {
                try { NativeLibrary.Free(_linuxHandle); }
                catch { }
            }
        }

        private static SteamApiSession? TryCreateWindows(string appRoot, ILogger? logger)
        {
            var dll = Path.Combine(appRoot, "steam_api64.dll");
            if (!File.Exists(dll))
            {
                logger?.LogWarning("[SteamAchievement] steam_api64.dll not found: {Path}", dll);
                return null;
            }

            EnsureSteamAppEnvironment(logger);

            IntPtr handle = IntPtr.Zero;
            try { handle = NativeLibrary.Load(dll); }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[SteamAchievement] steam_api64.dll load failed: {Path}", dll);
                return null;
            }
            try
            {
                var init = GetLinuxInitDelegate(handle);
                var shutdown = GetLinuxDelegate<Native.SteamAPIShutdownFn>(handle, "SteamAPI_Shutdown");
                var runCallbacks = GetLinuxDelegate<Native.SteamAPIRunCallbacksFn>(handle, "SteamAPI_RunCallbacks");
                var isSteamRunning = GetLinuxDelegate<Native.SteamAPIIsSteamRunningFn>(handle, "SteamAPI_IsSteamRunning");
                if (!isSteamRunning())
                {
                    logger?.LogWarning("[SteamAchievement] SteamAPI_IsSteamRunning=false SteamAppId={SteamAppId} SteamGameId={SteamGameId}",
                        Environment.GetEnvironmentVariable("SteamAppId"),
                        Environment.GetEnvironmentVariable("SteamGameId"));
                }
                if (!init())
                {
                    logger?.LogWarning("[SteamAchievement] SteamAPI_Init=false SteamAppId={SteamAppId} SteamGameId={SteamGameId}",
                        Environment.GetEnvironmentVariable("SteamAppId"),
                        Environment.GetEnvironmentVariable("SteamGameId"));
                    NativeLibrary.Free(handle);
                    return null;
                }
                var session = new SteamApiSession(
                    handle,
                    shutdown,
                    runCallbacks,
                    TryGetLinuxDelegate<Native.SteamAPISteamUtilsFn>(handle, "SteamAPI_SteamUtils_v010"),
                    TryGetLinuxDelegate<Native.SteamAPIGetAppIdFn>(handle, "SteamAPI_ISteamUtils_GetAppID"),
                    GetLinuxDelegate<Native.SteamAPISteamUserStatsFn>(handle, "SteamAPI_SteamUserStats_v013"),
                    TryGetLinuxDelegate<Native.SteamAPIRequestCurrentStatsFn>(handle, "SteamAPI_ISteamUserStats_RequestCurrentStats"),
                    GetLinuxDelegate<Native.SteamAPIGetAchievementFn>(handle, "SteamAPI_ISteamUserStats_GetAchievement"),
                    GetLinuxDelegate<Native.SteamAPISetAchievementFn>(handle, "SteamAPI_ISteamUserStats_SetAchievement"),
                    GetLinuxDelegate<Native.SteamAPIStoreStatsFn>(handle, "SteamAPI_ISteamUserStats_StoreStats"));
                session.LogAppId(logger);
                session.WaitForCurrentStats(logger);
                return session;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                logger?.LogWarning(ex, "[SteamAchievement] Windows Steam API entrypoint unavailable");
                if (handle != IntPtr.Zero)
                {
                    try { NativeLibrary.Free(handle); }
                    catch { }
                }
                return null;
            }
        }

        private static void EnsureSteamAppEnvironment(ILogger? logger)
        {
            var appId = BuildInfo.IsDemo ? DemoAppId : FullAppId;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamAppId")))
            {
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
                logger?.LogInformation("[SteamAchievement] SteamAppId fallback set: {AppId}", appId);
            }
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamGameId")))
            {
                Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());
                logger?.LogInformation("[SteamAchievement] SteamGameId fallback set: {AppId}", appId);
            }
        }

        private static SteamApiSession? TryCreateLinux(string appRoot, ILogger? logger)
        {
            EnsureSteamAppEnvironment(logger);

            foreach (var candidate in GetLinuxSteamApiCandidates(appRoot))
            {
                IntPtr handle = IntPtr.Zero;
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

                    var init = GetLinuxInitDelegate(handle);
                    var shutdown = GetLinuxDelegate<Native.SteamAPIShutdownFn>(handle, "SteamAPI_Shutdown");
                    var runCallbacks = GetLinuxDelegate<Native.SteamAPIRunCallbacksFn>(handle, "SteamAPI_RunCallbacks");
                    if (!init())
                    {
                        NativeLibrary.Free(handle);
                        continue;
                    }

                    var session = new SteamApiSession(
                        handle,
                        shutdown,
                        runCallbacks,
                        TryGetLinuxDelegate<Native.SteamAPISteamUtilsFn>(handle, "SteamAPI_SteamUtils_v010"),
                        TryGetLinuxDelegate<Native.SteamAPIGetAppIdFn>(handle, "SteamAPI_ISteamUtils_GetAppID"),
                        GetLinuxDelegate<Native.SteamAPISteamUserStatsFn>(handle, "SteamAPI_SteamUserStats_v013"),
                        TryGetLinuxDelegate<Native.SteamAPIRequestCurrentStatsFn>(handle, "SteamAPI_ISteamUserStats_RequestCurrentStats"),
                        GetLinuxDelegate<Native.SteamAPIGetAchievementFn>(handle, "SteamAPI_ISteamUserStats_GetAchievement"),
                        GetLinuxDelegate<Native.SteamAPISetAchievementFn>(handle, "SteamAPI_ISteamUserStats_SetAchievement"),
                        GetLinuxDelegate<Native.SteamAPIStoreStatsFn>(handle, "SteamAPI_ISteamUserStats_StoreStats"));
                    session.LogAppId(logger);
                    session.WaitForCurrentStats(logger);
                    return session;
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[SteamAchievement] Linux Steam API candidate failed: {Candidate}", candidate);
                    if (handle != IntPtr.Zero)
                    {
                        try { NativeLibrary.Free(handle); }
                        catch { }
                    }
                }
            }
            return null;
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

        private static T GetLinuxDelegate<T>(IntPtr handle, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(handle, name, out var symbol) || symbol == IntPtr.Zero)
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(symbol);
        }

        private static T? TryGetLinuxDelegate<T>(IntPtr handle, string name) where T : Delegate
        {
            return NativeLibrary.TryGetExport(handle, name, out var symbol) && symbol != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<T>(symbol)
                : null;
        }

        private static Native.SteamAPIInitFn GetLinuxInitDelegate(IntPtr handle)
        {
            if (NativeLibrary.TryGetExport(handle, "SteamAPI_Init", out var init) && init != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<Native.SteamAPIInitFn>(init);
            if (NativeLibrary.TryGetExport(handle, "SteamAPI_InitSafe", out var initSafe) && initSafe != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<Native.SteamAPIInitFn>(initSafe);
            throw new EntryPointNotFoundException("SteamAPI_Init/SteamAPI_InitSafe");
        }

        private void WaitForCurrentStats(ILogger? logger)
        {
            var result = RequestCurrentStats();
            logger?.LogInformation("[SteamAchievement] RequestCurrentStats result={Result}", result);
            if (!result)
            {
                logger?.LogWarning("[SteamAchievement] RequestCurrentStats failed");
                return;
            }

            for (var i = 0; i < 20; i++)
            {
                try
                {
                    if (OperatingSystem.IsLinux()) _linuxRunCallbacks?.Invoke();
                    else Native.SteamAPI_RunCallbacks();
                }
                catch { }
                Thread.Sleep(50);
            }
        }

        private void LogAppId(ILogger? logger)
        {
            var expectedAppId = BuildInfo.IsDemo ? DemoAppId : FullAppId;
            var currentAppId = GetCurrentAppId();
            logger?.LogInformation("[SteamAchievement] Current Steam AppID={CurrentAppId} ExpectedAppID={ExpectedAppId} SteamAppId={SteamAppId} SteamGameId={SteamGameId}",
                currentAppId,
                expectedAppId,
                Environment.GetEnvironmentVariable("SteamAppId"),
                Environment.GetEnvironmentVariable("SteamGameId"));
            if (currentAppId != 0 && currentAppId != expectedAppId)
            {
                logger?.LogWarning("[SteamAchievement] Steam AppID mismatch: CurrentAppID={CurrentAppId} ExpectedAppID={ExpectedAppId}", currentAppId, expectedAppId);
            }
        }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPIInitFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SteamAPIShutdownFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SteamAPIRunCallbacksFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr SteamAPISteamUtilsFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint SteamAPIGetAppIdFn(IntPtr utils);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPIIsSteamRunningFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr SteamAPISteamUserStatsFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPIRequestCurrentStatsFn(IntPtr stats);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPIGetAchievementFn(IntPtr stats, string name, [MarshalAs(UnmanagedType.I1)] out bool achieved);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPISetAchievementFn(IntPtr stats, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool SteamAPIStoreStatsFn(IntPtr stats);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_Init();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_Shutdown();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_RunCallbacks();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_SteamUserStats_v013();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUserStats_RequestCurrentStats(IntPtr stats);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUserStats_GetAchievement(IntPtr stats, string name, [MarshalAs(UnmanagedType.I1)] out bool achieved);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUserStats_SetAchievement(IntPtr stats, string name);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUserStats_StoreStats(IntPtr stats);
    }
}
