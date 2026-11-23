using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MobMateWhisp")]
[assembly: InternalsVisibleTo("MobMateLinux")]
[assembly: InternalsVisibleTo("MobMate.Core.Tests")]

namespace MobMate.Core;

// Capture before any Steam API call or application-provided environment fallback.
// Steam's environment is a launch hint, not authenticated provenance: manually
// supplied or inherited matching values can still look like a library launch.
internal static class SteamLaunchContext
{
    internal const string LauncherContextVariable = "MOBMATE_STEAM_LAUNCH_CONTEXT";
    internal static readonly bool AllowPersistentConnection;

    static SteamLaunchContext()
    {
        AllowPersistentConnection = !OperatingSystem.IsWindows() || IsSteamLaunch(
            Environment.GetEnvironmentVariable("SteamAppId"),
            Environment.GetEnvironmentVariable("SteamGameId"),
            Environment.GetEnvironmentVariable("SteamClientLaunch"),
            Environment.GetEnvironmentVariable(LauncherContextVariable));
    }

    internal static bool IsSteamLaunch(string? appId, string? gameId, string? clientLaunch, string? launcherContext)
    {
        if (launcherContext == "direct" || clientLaunch != "1") return false;
        var id = string.IsNullOrWhiteSpace(appId) ? gameId : appId;
        return id is "4261090" or "4448740"
            && (string.IsNullOrWhiteSpace(appId) || appId == id)
            && (string.IsNullOrWhiteSpace(gameId) || gameId == id);
    }
}
