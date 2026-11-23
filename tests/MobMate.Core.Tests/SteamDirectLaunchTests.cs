extern alias SteamCore;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;
using SteamCore::MobMate.Core;

namespace MobMate.Steam.Tests;

public sealed class SteamDirectLaunchTests
{
    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData("4261090", "4261090", null, null, false)]
    [InlineData("480", "480", "1", null, false)]
    [InlineData("4261090", "480", "1", null, false)]
    [InlineData("4261090", "4448740", "1", null, false)]
    [InlineData("4261090", "4261090", "1", "direct", false)]
    [InlineData("4261090", "4261090", "1", "steam", true)]
    [InlineData("4448740", "4448740", "1", null, true)]
    [InlineData("4261090", null, "1", null, true)]
    [InlineData(null, "4261090", "1", null, true)]
    public void LaunchHintRequiresMatchingIdentityAndClientOptIn(string? app, string? game, string? client, string? launcher, bool expected)
        => Assert.Equal(expected, SteamLaunchContext.IsSteamLaunch(app, game, client, launcher));

    [Fact]
    public void DirectLaunchFeatureCallsAndUsageTimerCannotReconnectAfterEnvironmentChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(SteamLaunchContext.AllowPersistentConnection);
        var app = Environment.GetEnvironmentVariable("SteamAppId");
        var game = Environment.GetEnvironmentVariable("SteamGameId");
        var client = Environment.GetEnvironmentVariable("SteamClientLaunch");
        try
        {
            using var before = new SteamAchievementService("missing-steam-library");
            before.Unlock(SteamAchievementService.ThanksForLaunching);
            before.StartUsageTimer();
            Assert.Equal(app, Environment.GetEnvironmentVariable("SteamAppId"));
            Assert.Equal(game, Environment.GetEnvironmentVariable("SteamGameId"));
            Environment.SetEnvironmentVariable("SteamAppId", "4261090");
            Environment.SetEnvironmentVariable("SteamGameId", "4261090");
            Environment.SetEnvironmentVariable("SteamClientLaunch", "1");
            using var after = new SteamAchievementService("missing-steam-library");
            foreach (var service in new[] { before, after })
            {
                service.Unlock(SteamAchievementService.FirstSessionStart);
                service.StartUsageTimer();
                Assert.Null(typeof(SteamAchievementService).GetField("_usageTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
            }
            Assert.False(SteamLaunchContext.AllowPersistentConnection);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SteamAppId", app);
            Environment.SetEnvironmentVariable("SteamGameId", game);
            Environment.SetEnvironmentVariable("SteamClientLaunch", client);
        }
    }

    [Fact]
    public void ProbeResponseRejectsMissingDuplicateNullAndUnknownEnumFields()
    {
        var snapshot = Snapshot();
        var valid = JsonSerializer.Serialize(snapshot);
        Assert.Equal(snapshot, SteamDlcProbe.ReadResult<VulkanSteamSnapshot>(valid));
        Assert.Null(SteamDlcProbe.ReadResult<VulkanSteamSnapshot>("{}"));
        Assert.Null(SteamDlcProbe.ReadResult<VulkanSteamSnapshot>(valid.Replace("\"SteamId\":123", "\"SteamId\":123,\"SteamId\":123")));
        Assert.Null(SteamDlcProbe.ReadResult<VulkanSteamSnapshot>(valid.Replace("\"Reason\":\"fixture\"", "\"Reason\":null")));
        Assert.Null(SteamDlcProbe.ReadResult<VulkanSteamSnapshot>(valid.Replace("\"SteamStatus\":4", "\"SteamStatus\":999")));
    }

    [Theory]
    [InlineData("echo {}")]
    [InlineData("echo not-json")]
    [InlineData("exit /b 9")]
    public void ProbeTransportMalformedOrAbnormalExitIsUnavailableNotOffline(string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);
        var result = SteamDlcProbe.RunProcess(start, reason => Snapshot() with
        {
            ApiAvailable = false, SubscriptionsAvailable = false, SteamId = 0,
            Reason = reason, SteamStatus = AppConfigLoader.SteamworksStatus.Error,
        });
        Assert.False(result.ApiAvailable);
        Assert.Equal(0UL, result.SteamId);
        Assert.Equal(AppConfigLoader.SteamworksStatus.Error, result.SteamStatus);
    }

    [Fact]
    public void VulkanRetainsParentMemoAndOnlineRevocationOfflineAndUnavailableBoundaries()
    {
        var root = NewRoot();
        var time = new Clock();
        var backend = new Backend(Snapshot());
        var cache = new VulkanEntitlementCache(Path.Combine(root, "cache.json"), "fixture-secret", "fixture-machine");
        var gate = new VulkanEntitlementGate(backend, cache, new Legacy(false, false), time);
        var ids = new HashSet<int> { 4967740, 4268150 };
        Assert.Equal(AppConfigLoader.DlcOwnershipStatus.OwnedOnline, gate.Check(4268150, root).DlcStatus);
        backend.Value = Snapshot() with { NewVulkanOwned = false, LegacyOwned = false };
        Assert.True(gate.Check(4268150, root).Allowed);
        Assert.Equal(1, backend.Calls);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AppConfigLoader.DlcOwnershipStatus.MissingOnline, gate.Check(4268150, root).DlcStatus);
        Assert.False(cache.Read(123, ids, time.GetUtcNow()).Allowed);
        Assert.True(cache.WriteAndVerify(123, 4967740, ids, time.GetUtcNow()));
        backend.Value = Snapshot() with { LoggedOn = false };
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AppConfigLoader.DlcOwnershipStatus.OwnedOfflineCache, gate.Check(4268150, root).DlcStatus);
        backend.Value = Snapshot() with { ApiAvailable = false, SubscriptionsAvailable = false };
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(gate.Check(4268150, root).Allowed);
        backend.Value = Snapshot() with { SteamId = 0 };
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(gate.Check(4268150, root).Allowed);
        backend.Value = Snapshot() with { SteamId = 456, LoggedOn = false };
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(gate.Check(4268150, root).Allowed);
        Assert.False(cache.Read(123, ids, time.GetUtcNow().AddDays(31)).Allowed);
        File.AppendAllText(Path.Combine(root, "cache.json"), "tampered");
        Assert.False(cache.Read(123, ids, time.GetUtcNow()).Allowed);
    }

    [Fact]
    public void VulkanLegacyOwnershipAndOfflineTransitionKeepExistingMeaning()
    {
        var root = NewRoot();
        var time = new Clock();
        var backend = new Backend(Snapshot() with { NewVulkanOwned = false, LegacyOwned = true });
        var cache = new VulkanEntitlementCache(Path.Combine(root, "cache.json"), "fixture-secret", "fixture-machine");
        var gate = new VulkanEntitlementGate(backend, cache, new Legacy(false, false), time);
        Assert.True(gate.Check(4268150, root).Allowed);
        Assert.Equal(4268150, cache.Read(123, new HashSet<int> { 4967740, 4268150 }, time.GetUtcNow()).AppId);
        backend.Value = Snapshot() with { LoggedOn = false, SteamId = 789 };
        Assert.True(new VulkanEntitlementGate(backend, cache, new Legacy(true, true), time).Check(4268150, root).Allowed);
        Assert.False(new VulkanEntitlementGate(backend, cache, new Legacy(true, false), time).Check(4268150, root).Allowed);
        backend.Value = backend.Value with { LoggedOn = true, SubscriptionsAvailable = false };
        Assert.True(new VulkanEntitlementGate(backend, cache, new Legacy(true, false), time).Check(4268150, root).Allowed);
    }

    [Fact]
    public void VulkanNativeErrorDoesNotConsumePreviouslyValidOfflineCache()
    {
        var root = NewRoot();
        var time = new Clock();
        var backend = new Backend(Snapshot() with
        {
            ApiAvailable = false, SubscriptionsAvailable = false,
            Reason = "Steam API error: missing export", SteamStatus = AppConfigLoader.SteamworksStatus.Error,
        });
        var cache = new VulkanEntitlementCache(Path.Combine(root, "cache.json"), "fixture-secret", "fixture-machine");
        var ids = new HashSet<int> { 4967740, 4268150 };
        Assert.True(cache.WriteAndVerify(123, 4967740, ids, time.GetUtcNow()));
        var gate = new VulkanEntitlementGate(backend, cache, new Legacy(false, false), time);

        var unavailable = gate.Check(4268150, root);
        Assert.False(unavailable.Allowed);
        Assert.Equal("steam_entitlement_unavailable", unavailable.ReasonCode);
        Assert.Equal(AppConfigLoader.SteamworksStatus.Error, unavailable.SteamStatus);
        Assert.Equal(AppConfigLoader.DlcOwnershipStatus.NotChecked, unavailable.DlcStatus);
        Assert.True(cache.Read(123, ids, time.GetUtcNow()).Allowed);

        backend.Value = Snapshot() with { LoggedOn = false };
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AppConfigLoader.DlcOwnershipStatus.OwnedOfflineCache, gate.Check(4268150, root).DlcStatus);
    }

    private static VulkanSteamSnapshot Snapshot() => new(true, true, true, 123, true, false, "fixture", AppConfigLoader.SteamworksStatus.Initialized);
    private static string NewRoot()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../temp/steam-probe/tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }
    private sealed class Backend(VulkanSteamSnapshot value) : IVulkanSteamBackend
    {
        internal VulkanSteamSnapshot Value = value;
        internal int Calls;
        public VulkanSteamSnapshot Check(string root, int newId, int legacyId) { Calls++; return Value; }
    }
    private sealed class Legacy(bool valid, bool offlineAllowed) : IVulkanLegacyTokenReader
    {
        public VulkanLegacyTokenResult Read(ulong steamId, int appId, DateTimeOffset now) => new(valid, offlineAllowed, "fixture");
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan interval) => _now += interval;
    }
}
