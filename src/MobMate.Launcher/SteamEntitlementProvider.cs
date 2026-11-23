using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MobMate.Core;

namespace MobMate.Launcher;

internal sealed record SteamSubscriptionSnapshot(
    bool ApiAvailable,
    bool SubscriptionsAvailable,
    bool LoggedOn,
    ulong SteamId,
    bool SupporterOwned,
    bool LegacyOwned,
    string Reason);

internal interface ISteamSubscriptionBackend
{
    SteamSubscriptionSnapshot CheckSubscriptions();
}

internal sealed class SteamEntitlementProvider(
    ISteamSubscriptionBackend backend,
    IEntitlementCache cache,
    ILegacyEntitlementToken legacyToken,
    ILogger logger) : IEntitlementProvider
{
    public SupporterEntitlement GetSupporterEntitlement()
    {
        var snapshot = backend.CheckSubscriptions();
        if (!snapshot.ApiAvailable || snapshot.SteamId == 0)
        {
            logger.LogWarning("Steam entitlement unavailable: {Reason}", snapshot.Reason);
            return SupporterEntitlement.Unknown;
        }

        if (!snapshot.SubscriptionsAvailable || !snapshot.LoggedOn)
        {
            var cached = cache.Read(snapshot.SteamId, DateTimeOffset.UtcNow);
            if (cached.Allowed)
            {
                logger.LogInformation("Steam offline/cache fallback: {Reason}", cached.Reason);
                return SupporterEntitlement.Owned;
            }

            var now = DateTimeOffset.UtcNow;
            var legacy = legacyToken.Read(snapshot.SteamId, now);
            if (!legacy.Valid || !snapshot.LoggedOn && !legacy.OfflineAllowed)
            {
                logger.LogInformation("Steam entitlement fallback denied: cache={CacheReason}; V1={V1Reason}", cached.Reason, legacy.Reason);
                return SupporterEntitlement.Unknown;
            }

            if (!snapshot.LoggedOn)
            {
                logger.LogInformation("V1 Legacy token accepted read-only during offline transition");
                return SupporterEntitlement.Owned;
            }

            if (cache.WriteAndVerify(snapshot.SteamId, now))
            {
                legacyToken.Delete(snapshot.SteamId);
                logger.LogInformation("V1 Legacy token migrated to signed entitlement cache");
            }
            return SupporterEntitlement.Owned;
        }

        if (snapshot.SupporterOwned || snapshot.LegacyOwned)
        {
            var cacheWritten = cache.WriteAndVerify(snapshot.SteamId, DateTimeOffset.UtcNow);
            if (cacheWritten)
            {
                legacyToken.Delete(snapshot.SteamId);
            }
            logger.LogInformation(
                "Steam supporter entitlement confirmed; SteamId={SteamId} Source={Source} CacheWritten={CacheWritten}",
                snapshot.SteamId,
                snapshot.SupporterOwned ? "supporter" : "legacy",
                cacheWritten);
            return SupporterEntitlement.Owned;
        }

        cache.Delete(snapshot.SteamId);
        legacyToken.Delete(snapshot.SteamId);
        logger.LogInformation("Steam supporter entitlement not owned; SteamId={SteamId}", snapshot.SteamId);
        return SupporterEntitlement.NotOwned;
    }
}

internal sealed class FakeSteamSubscriptionBackend(SteamSubscriptionSnapshot snapshot) : ISteamSubscriptionBackend
{
    public SteamSubscriptionSnapshot CheckSubscriptions() => snapshot;
}

internal sealed class NativeSteamSubscriptionBackend(string installRoot) : ISteamSubscriptionBackend
{
    public SteamSubscriptionSnapshot CheckSubscriptions()
    {
        if (OperatingSystem.IsWindows() && !SteamLaunchContext.AllowPersistentConnection && !SteamDlcProbe.IsChild)
            return SteamDlcProbe.Run<SteamSubscriptionSnapshot>(Unavailable, "supporter");

        IntPtr handle = IntPtr.Zero;
        var initialized = false;
        try
        {
            foreach (var candidate in GetSteamApiCandidates())
            {
                if (candidate != "libsteam_api.so" && !File.Exists(candidate))
                {
                    continue;
                }

                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    break;
                }
            }

            if (handle == IntPtr.Zero)
            {
                return Unavailable("Steam API library not found");
            }

            var init = GetInitDelegate(handle);
            var shutdown = GetDelegate<SteamApiShutdown>(handle, "SteamAPI_Shutdown");
            var getApps = GetAppsDelegate(handle);
            var getUser = Marshal.GetDelegateForFunctionPointer<SteamApiGetInterface>(SteamDlcProbe.ResolveInterfaceGetter(handle, "User"));
            var isSubscribed = GetDelegate<SteamApiIsSubscribedApp>(handle, "SteamAPI_ISteamApps_BIsSubscribedApp");
            var isLoggedOn = GetDelegate<SteamApiIsLoggedOn>(handle, "SteamAPI_ISteamUser_BLoggedOn");
            var getSteamId = GetDelegate<SteamApiGetSteamId>(handle, "SteamAPI_ISteamUser_GetSteamID");

            if (!init())
            {
                return Unavailable("Steam API initialization failed");
            }
            initialized = true;

            var apps = getApps();
            var user = getUser();
            if (apps == IntPtr.Zero || user == IntPtr.Zero)
            {
                return Unavailable("Steam apps or user interface unavailable");
            }

            var steamId = getSteamId(user);
            var loggedOn = isLoggedOn(user);
            try
            {
                return new SteamSubscriptionSnapshot(
                    true,
                    true,
                    loggedOn,
                    steamId,
                    isSubscribed(apps, BuildInfo.SteamSupporterDlcAppId),
                    isSubscribed(apps, BuildInfo.SteamLegacyDlcAppId),
                    "Steam subscription check completed");
            }
            catch (Exception ex)
            {
                return new SteamSubscriptionSnapshot(true, false, loggedOn, steamId, false, false, $"Steam subscription API error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return Unavailable($"Steam API error: {ex.Message}");
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

    private IEnumerable<string> GetSteamApiCandidates()
    {
        var overridePath = Environment.GetEnvironmentVariable("MOBMATE_STEAM_API_LIBRARY");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return overridePath;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(installRoot, "stable", "steam_api64.dll");
            yield break;
        }

        yield return Path.Combine(installRoot, "stable", "libs", "steam", "libsteam_api.so");
        yield return Path.Combine(installRoot, "stable", "libsteam_api.so");
        yield return "libsteam_api.so";
    }

    private static T GetDelegate<T>(IntPtr handle, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, name, out var symbol) || symbol == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(name);
        }
        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }

    private static SteamApiInit GetInitDelegate(IntPtr handle)
    {
        if (NativeLibrary.TryGetExport(handle, "SteamAPI_Init", out var init) && init != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer<SteamApiInit>(init);
        }
        if (NativeLibrary.TryGetExport(handle, "SteamAPI_InitSafe", out var initSafe) && initSafe != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer<SteamApiInit>(initSafe);
        }
        throw new EntryPointNotFoundException("SteamAPI_Init/SteamAPI_InitSafe");
    }

    private static SteamApiGetInterface GetAppsDelegate(IntPtr handle)
    {
        return Marshal.GetDelegateForFunctionPointer<SteamApiGetInterface>(SteamDlcProbe.ResolveInterfaceGetter(handle, "Apps"));
    }

    private static SteamSubscriptionSnapshot Unavailable(string reason) =>
        new(false, false, false, 0, false, false, reason);

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
