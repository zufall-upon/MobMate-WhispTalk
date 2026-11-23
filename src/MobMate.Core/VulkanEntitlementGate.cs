using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MobMate.Core;

internal sealed record VulkanSteamSnapshot(
    bool ApiAvailable,
    bool SubscriptionsAvailable,
    bool LoggedOn,
    ulong SteamId,
    bool NewVulkanOwned,
    bool LegacyOwned,
    string Reason,
    AppConfigLoader.SteamworksStatus SteamStatus);

internal interface IVulkanSteamBackend
{
    VulkanSteamSnapshot Check(string appRoot, int newVulkanAppId, int legacyAppId);
}

internal sealed record VulkanLegacyTokenResult(bool Valid, bool OfflineAllowed, string Reason);

internal interface IVulkanLegacyTokenReader
{
    VulkanLegacyTokenResult Read(ulong steamId, int legacyAppId, DateTimeOffset now);
}

internal sealed class VulkanEntitlementGate(
    IVulkanSteamBackend backend,
    IVulkanEntitlementCache cache,
    IVulkanLegacyTokenReader legacyToken,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan MemoryCacheLifetime = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private MemoizedResult? _memoized;

    private sealed record MemoizedResult(
        string AppRoot,
        int NewVulkanAppId,
        int LegacyAppId,
        DateTimeOffset CheckedAt,
        AppConfigLoader.SteamDlcGateResult Result);

    public static VulkanEntitlementGate CreateDefault() => new(
        new NativeVulkanSteamBackend(),
        VulkanEntitlementCache.CreateDefault(),
        WindowsVulkanLegacyTokenReader.CreateDefault(),
        TimeProvider.System);

    public AppConfigLoader.SteamDlcGateResult Check(int legacyAppId, string appRoot)
    {
        var newVulkanAppId = BuildInfo.SteamNewVulkanDlcAppId;
        if (legacyAppId <= 0 || newVulkanAppId <= 0)
        {
            return Result(false, "Vulkan DLC AppID is not configured", "steam_dlc_appid_not_configured",
                AppConfigLoader.SteamworksStatus.Disabled, AppConfigLoader.DlcOwnershipStatus.Error);
        }

        var normalizedRoot = Path.GetFullPath(appRoot);
        var now = timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (_memoized is not null
                && _memoized.AppRoot == normalizedRoot
                && _memoized.NewVulkanAppId == newVulkanAppId
                && _memoized.LegacyAppId == legacyAppId
                && now - _memoized.CheckedAt < MemoryCacheLifetime)
            {
                return _memoized.Result;
            }

            var result = CheckCore(normalizedRoot, newVulkanAppId, legacyAppId, now);
            _memoized = new(normalizedRoot, newVulkanAppId, legacyAppId, now, result);
            return result;
        }
    }

    private AppConfigLoader.SteamDlcGateResult CheckCore(
        string appRoot,
        int newVulkanAppId,
        int legacyAppId,
        DateTimeOffset now)
    {
        var allowedAppIds = new HashSet<int> { newVulkanAppId, legacyAppId };
        var snapshot = backend.Check(appRoot, newVulkanAppId, legacyAppId);
        if (!snapshot.ApiAvailable || snapshot.SteamId == 0)
        {
            return Result(false, snapshot.Reason, "steam_entitlement_unavailable",
                snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.NotChecked);
        }

        if (snapshot.SubscriptionsAvailable && snapshot.LoggedOn)
        {
            var ownedAppId = snapshot.NewVulkanOwned ? newVulkanAppId : snapshot.LegacyOwned ? legacyAppId : 0;
            if (ownedAppId != 0)
            {
                var cacheWritten = cache.WriteAndVerify(snapshot.SteamId, ownedAppId, allowedAppIds, now);
                var source = ownedAppId == newVulkanAppId ? "new Vulkan" : "Legacy";
                return Result(true, $"Steam {source} DLC owned; cacheWritten={cacheWritten}", "vulkan_owned_online",
                    snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.OwnedOnline);
            }

            cache.Delete(snapshot.SteamId);
            return Result(false, "Steam Vulkan and Legacy DLC not owned", "vulkan_missing_online",
                snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.MissingOnline);
        }

        var cached = cache.Read(snapshot.SteamId, allowedAppIds, now);
        if (cached.Allowed)
        {
            return Result(true, cached.Reason, "vulkan_offline_cache_valid",
                snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.OwnedOfflineCache);
        }

        var legacy = legacyToken.Read(snapshot.SteamId, legacyAppId, now);
        if (legacy.Valid && (snapshot.LoggedOn || legacy.OfflineAllowed))
        {
            return Result(true, legacy.Reason, "legacy_v1_token_valid",
                snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.OwnedOfflineCache);
        }

        return Result(false, $"{snapshot.Reason}; {cached.Reason}; {legacy.Reason}", "vulkan_offline_cache_missing",
            snapshot.SteamStatus, AppConfigLoader.DlcOwnershipStatus.MissingOfflineCache);
    }

    private static AppConfigLoader.SteamDlcGateResult Result(
        bool allowed,
        string reason,
        string reasonCode,
        AppConfigLoader.SteamworksStatus steamStatus,
        AppConfigLoader.DlcOwnershipStatus dlcStatus) =>
        new(allowed, reason, reasonCode, steamStatus, dlcStatus);
}

internal sealed class NativeVulkanSteamBackend : IVulkanSteamBackend
{
    public VulkanSteamSnapshot Check(string appRoot, int newVulkanAppId, int legacyAppId)
    {
        return OperatingSystem.IsWindows() && !SteamLaunchContext.AllowPersistentConnection && !SteamDlcProbe.IsChild
            ? SteamDlcProbe.Run<VulkanSteamSnapshot>(reason => Unavailable(reason, AppConfigLoader.SteamworksStatus.Error),
                "vulkan", appRoot, newVulkanAppId.ToString(CultureInfo.InvariantCulture), legacyAppId.ToString(CultureInfo.InvariantCulture))
            : CheckInProcess(appRoot, newVulkanAppId, legacyAppId);
    }

    internal static bool TryHandleProbe(string[] args, out int exitCode) => SteamDlcProbe.TryHandle(args, values =>
    {
        if (values.Length == 4 && values[0] == "vulkan"
            && int.TryParse(values[2], out var newId) && int.TryParse(values[3], out var legacyId))
            return new NativeVulkanSteamBackend().CheckInProcess(values[1], newId, legacyId);
        if (values.Length == 3 && values[0] == "single" && int.TryParse(values[2], out var appId))
            return AppConfigLoader.CheckSteamVulkanDlc(appId, values[1]);
        return null;
    }, out exitCode);

    private VulkanSteamSnapshot CheckInProcess(string appRoot, int newVulkanAppId, int legacyAppId)
    {
        if (newVulkanAppId <= 0 || legacyAppId <= 0)
        {
            return Unavailable("Vulkan DLC AppID is invalid", AppConfigLoader.SteamworksStatus.Disabled);
        }
        IntPtr handle = IntPtr.Zero;
        var initialized = false;
        try
        {
            foreach (var candidate in GetSteamApiCandidates(appRoot))
            {
                if (candidate != "libsteam_api.so" && !File.Exists(candidate)) continue;
                if (NativeLibrary.TryLoad(candidate, out handle)) break;
            }
            if (handle == IntPtr.Zero)
            {
                return Unavailable("Steam API library not found", AppConfigLoader.SteamworksStatus.DllMissing);
            }

            var init = GetInitDelegate(handle);
            var getApps = GetAppsDelegate(handle);
            var getUser = Marshal.GetDelegateForFunctionPointer<SteamApiGetInterface>(SteamDlcProbe.ResolveInterfaceGetter(handle, "User"));
            var isSubscribed = GetDelegate<SteamApiIsSubscribedApp>(handle, "SteamAPI_ISteamApps_BIsSubscribedApp");
            var isLoggedOn = GetDelegate<SteamApiIsLoggedOn>(handle, "SteamAPI_ISteamUser_BLoggedOn");
            var getSteamId = GetDelegate<SteamApiGetSteamId>(handle, "SteamAPI_ISteamUser_GetSteamID");
            if (!init())
            {
                return Unavailable("Steam API initialization failed", AppConfigLoader.SteamworksStatus.InitFailed);
            }
            initialized = true;

            var apps = getApps();
            var user = getUser();
            if (apps == IntPtr.Zero || user == IntPtr.Zero)
            {
                return Unavailable("Steam apps or user interface unavailable", AppConfigLoader.SteamworksStatus.InitFailed);
            }

            var steamId = getSteamId(user);
            var loggedOn = isLoggedOn(user);
            try
            {
                return new(true, true, loggedOn, steamId,
                    isSubscribed(apps, (uint)newVulkanAppId),
                    isSubscribed(apps, (uint)legacyAppId),
                    "Steam Vulkan subscription check completed",
                    AppConfigLoader.SteamworksStatus.Initialized);
            }
            catch (Exception ex)
            {
                return new(true, false, loggedOn, steamId, false, false,
                    $"Steam Vulkan subscription API error: {ex.Message}",
                    AppConfigLoader.SteamworksStatus.Error);
            }
        }
        catch (Exception ex)
        {
            return Unavailable($"Steam API error: {ex.Message}", AppConfigLoader.SteamworksStatus.Error);
        }
        finally
        {
            if (initialized && handle != IntPtr.Zero)
            {
                try { GetDelegate<SteamApiShutdown>(handle, "SteamAPI_Shutdown")(); }
                catch { }
            }
            if (handle != IntPtr.Zero)
            {
                try { NativeLibrary.Free(handle); }
                catch { }
            }
        }
    }

    private static IEnumerable<string> GetSteamApiCandidates(string appRoot)
    {
        var overridePath = Environment.GetEnvironmentVariable("MOBMATE_STEAM_API_LIBRARY");
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(appRoot, "steam_api64.dll");
            yield break;
        }
        yield return Path.Combine(appRoot, "libs", "steam", "libsteam_api.so");
        yield return Path.Combine(appRoot, "libsteam_api.so");
        yield return "libsteam_api.so";
    }

    private static T GetDelegate<T>(IntPtr handle, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, name, out var symbol) || symbol == IntPtr.Zero)
            throw new EntryPointNotFoundException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }

    private static SteamApiInit GetInitDelegate(IntPtr handle)
    {
        if (NativeLibrary.TryGetExport(handle, "SteamAPI_Init", out var init) && init != IntPtr.Zero)
            return Marshal.GetDelegateForFunctionPointer<SteamApiInit>(init);
        if (NativeLibrary.TryGetExport(handle, "SteamAPI_InitSafe", out var initSafe) && initSafe != IntPtr.Zero)
            return Marshal.GetDelegateForFunctionPointer<SteamApiInit>(initSafe);
        throw new EntryPointNotFoundException("SteamAPI_Init/SteamAPI_InitSafe");
    }

    private static SteamApiGetInterface GetAppsDelegate(IntPtr handle)
    {
        return Marshal.GetDelegateForFunctionPointer<SteamApiGetInterface>(SteamDlcProbe.ResolveInterfaceGetter(handle, "Apps"));
    }

    private static VulkanSteamSnapshot Unavailable(string reason, AppConfigLoader.SteamworksStatus status) =>
        new(false, false, false, 0, false, false, reason, status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SteamApiGetInterface();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiIsSubscribedApp(IntPtr apps, uint appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiIsLoggedOn(IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiGetSteamId(IntPtr user);
}

internal sealed class WindowsVulkanLegacyTokenReader(string secret, DateTimeOffset offlineDeadline) : IVulkanLegacyTokenReader
{
    private const string RegistryPath = @"Software\JavaSoft\Prefs\whisper";

    public static WindowsVulkanLegacyTokenReader CreateDefault()
    {
        var configuredSecret = Environment.GetEnvironmentVariable("MOBMATE_STEAM_DLC_SECRET");
        var tokenSecret = string.IsNullOrWhiteSpace(configuredSecret) ? BuildInfo.SecretKey : configuredSecret;
        var deadline = DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParseExact(BuildInfo.BuildTimeUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var buildTime))
        {
            deadline = buildTime.AddDays(30);
        }
        return new(tokenSecret, deadline);
    }

    public VulkanLegacyTokenResult Read(ulong steamId, int legacyAppId, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows() || steamId == 0 || string.IsNullOrWhiteSpace(secret))
            return new(false, false, "Legacy V1 token unavailable");
        try
        {
            var key = "vulkan_dlc_token_" + steamId;
            using var preferences = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var token = preferences?.GetValue(key) as string ?? preferences?.GetValue(key.ToLowerInvariant()) as string;
            if (string.IsNullOrWhiteSpace(token)) return new(false, false, "Legacy V1 token not found");
            var valid = GetMachineIdCandidates(secret)
                .Select(machineId => HmacHex(secret, $"{legacyAppId}|{machineId}"))
                .Any(expected => FixedTimeEquals(token.Trim(), expected));
            return valid
                ? new(true, offlineDeadline != DateTimeOffset.MinValue && now <= offlineDeadline, "Legacy V1 token valid")
                : new(false, false, "Legacy V1 token signature mismatch");
        }
        catch (Exception ex)
        {
            return new(false, false, $"Legacy V1 token invalid: {ex.Message}");
        }
    }

    private static IEnumerable<string> GetMachineIdCandidates(string tokenSecret)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPreferredNic(nic)) continue;
                AddNicCandidate(nic, tokenSecret, emitted, candidates);
            }
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus != OperationalStatus.Up) continue;
                AddNicCandidate(nic, tokenSecret, emitted, candidates);
            }
        }
        catch { }
        var userMachineId = HmacHex(tokenSecret, Environment.UserName ?? "unknown");
        if (emitted.Add(userMachineId)) candidates.Add(userMachineId);
        if (emitted.Add("fallback")) candidates.Add("fallback");
        return candidates;
    }

    private static void AddNicCandidate(NetworkInterface nic, string tokenSecret, HashSet<string> emitted, List<string> candidates)
    {
        var mac = nic.GetPhysicalAddress().GetAddressBytes();
        if (mac.Length == 0) return;
        var machineId = HmacHex(tokenSecret, Convert.ToHexString(mac).ToLowerInvariant());
        if (emitted.Add(machineId)) candidates.Add(machineId);
    }

    private static bool IsPreferredNic(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
            || nic.OperationalStatus != OperationalStatus.Up) return false;
        var label = $"{nic.Name} {nic.Description}";
        string[] virtualHints = ["virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap", "tunnel", "wintun", "wireguard", "zerotier", "tailscale", "loopback", "pseudo"];
        return !virtualHints.Any(hint => label.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string HmacHex(string key, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var left = Encoding.ASCII.GetBytes(actual.ToLowerInvariant());
        var right = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
