using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
using MobMate.Core;

namespace MobMate.Launcher;

internal sealed record LegacyTokenResult(bool Valid, bool OfflineAllowed, string Reason);

internal interface ILegacyEntitlementToken
{
    LegacyTokenResult Read(ulong steamId, DateTimeOffset now);
    void Delete(ulong steamId);
}

internal sealed class FakeLegacyEntitlementToken(LegacyTokenResult result) : ILegacyEntitlementToken
{
    public int DeleteCount { get; private set; }
    public LegacyTokenResult Read(ulong steamId, DateTimeOffset now) => result;
    public void Delete(ulong steamId) => DeleteCount++;
}

internal sealed class WindowsLegacyV1EntitlementToken(string secret, DateTimeOffset offlineDeadline) : ILegacyEntitlementToken
{
    private const string RegistryPath = @"Software\JavaSoft\Prefs\whisper";
    private const string TokenPrefix = "vulkan_dlc_token_";

    public static WindowsLegacyV1EntitlementToken CreateDefault()
    {
        var configuredSecret = Environment.GetEnvironmentVariable("MOBMATE_STEAM_DLC_SECRET");
        var tokenSecret = string.IsNullOrWhiteSpace(configuredSecret) ? BuildInfo.SecretKey : configuredSecret;
        var deadline = DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParseExact(
                BuildInfo.BuildTimeUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var buildTime))
        {
            deadline = buildTime.ToUniversalTime().AddDays(30);
        }
        return new WindowsLegacyV1EntitlementToken(tokenSecret, deadline);
    }

    public LegacyTokenResult Read(ulong steamId, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, false, "V1 entitlement token is Windows-only");
        }
        if (steamId == 0 || string.IsNullOrWhiteSpace(secret))
        {
            return new(false, false, "V1 entitlement token is unavailable");
        }

        try
        {
            var key = TokenPrefix + steamId;
            using var preferences = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var token = preferences?.GetValue(key) as string ?? preferences?.GetValue(key.ToLowerInvariant()) as string;
            if (string.IsNullOrWhiteSpace(token))
            {
                return new(false, false, "V1 entitlement token not found for current SteamID");
            }

            var valid = GenerateTokenCandidates(BuildInfo.SteamLegacyDlcAppId, secret)
                .Any(expected => FixedTimeEquals(token.Trim(), expected));
            return valid
                ? new(true, offlineDeadline != DateTimeOffset.MinValue && now <= offlineDeadline, "V1 Legacy entitlement token valid")
                : new(false, false, "V1 Legacy entitlement token signature mismatch");
        }
        catch (Exception ex)
        {
            return new(false, false, $"V1 entitlement token invalid: {ex.Message}");
        }
    }

    public void Delete(ulong steamId)
    {
        if (!OperatingSystem.IsWindows() || steamId == 0)
        {
            return;
        }
        try
        {
            var key = TokenPrefix + steamId;
            using var preferences = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            preferences?.DeleteValue(key, throwOnMissingValue: false);
            preferences?.DeleteValue(key.ToLowerInvariant(), throwOnMissingValue: false);
        }
        catch
        {
            // A failed cleanup must not revoke the newly verified entitlement.
        }
    }

    private static IEnumerable<string> GenerateTokenCandidates(int appId, string tokenSecret) =>
        GetMachineIdCandidates(tokenSecret)
            .Select(machineId => HmacHex(tokenSecret, $"{appId}|{machineId}"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

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
            || nic.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }
        var label = $"{nic.Name} {nic.Description}";
        string[] virtualHints =
        [
            "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap", "tunnel",
            "wintun", "wireguard", "zerotier", "tailscale", "loopback", "pseudo",
        ];
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
