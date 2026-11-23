namespace MobMate.Core;

/// <summary>
/// Placeholder for MobMate.Core library.
/// </summary>
public static class MobMateCore
{
    private const string FullSteamAppId = "4261090";
    private const string DemoSteamAppId = "4448740";

    public const string Version = BuildInfo.AppVersion;
    public const string BuildChannel = BuildInfo.BuildChannel;
    public static bool IsDemo => ResolveIsDemo(
        Environment.GetEnvironmentVariable("SteamAppId"),
        Environment.GetEnvironmentVariable("SteamGameId"),
        BuildInfo.IsDemo);
    public const string CommitSha = BuildInfo.CommitSha;
    public const string BuildTimeUtc = BuildInfo.BuildTimeUtc;

    public static bool ResolveIsDemo(string? steamAppId, string? steamGameId, bool packageIsDemo)
    {
        if (steamAppId == DemoSteamAppId || steamGameId == DemoSteamAppId) return true;
        if (steamAppId == FullSteamAppId || steamGameId == FullSteamAppId) return false;
        return packageIsDemo;
    }
}
