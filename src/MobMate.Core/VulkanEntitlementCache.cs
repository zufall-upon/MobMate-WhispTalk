using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MobMate.Core;

internal sealed record VulkanCacheResult(bool Allowed, int AppId, string Reason);

internal interface IVulkanEntitlementCache
{
    VulkanCacheResult Read(ulong steamId, IReadOnlySet<int> allowedAppIds, DateTimeOffset now);
    bool WriteAndVerify(ulong steamId, int appId, IReadOnlySet<int> allowedAppIds, DateTimeOffset now);
    void Delete(ulong steamId);
}

internal sealed class VulkanEntitlementCache(string path, string secret, string machineId) : IVulkanEntitlementCache
{
    private const int SchemaVersion = 1;
    private const string Claim = "vulkan";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public ulong SteamId { get; set; }
        public int AppId { get; set; }
        public string Claim { get; set; } = string.Empty;
        public long IssuedUnixSeconds { get; set; }
        public long ExpiresUnixSeconds { get; set; }
        public string MachineId { get; set; } = string.Empty;
        public string Hmac { get; set; } = string.Empty;
    }

    public static VulkanEntitlementCache CreateDefault()
    {
        var configuredSecret = Environment.GetEnvironmentVariable("MOBMATE_STEAM_DLC_SECRET");
        var cacheSecret = string.IsNullOrWhiteSpace(configuredSecret) ? BuildInfo.SecretKey : configuredSecret;
        var cachePath = Path.Combine(AppUserPaths.ConfigDir, "vulkan-entitlement-v2.json");
        var binding = $"{Environment.MachineName}|{Environment.UserName}";
        var boundMachineId = string.IsNullOrWhiteSpace(cacheSecret) ? string.Empty : HmacHex(cacheSecret, binding);
        return new VulkanEntitlementCache(cachePath, cacheSecret, boundMachineId);
    }

    public VulkanCacheResult Read(ulong steamId, IReadOnlySet<int> allowedAppIds, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(machineId))
        {
            return new(false, 0, "Vulkan offline cache disabled because signing secret is unavailable");
        }
        if (!File.Exists(path))
        {
            return new(false, 0, "Vulkan offline cache not found");
        }

        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path));
            if (document is null || document.SchemaVersion != SchemaVersion || document.Claim != Claim)
            {
                return new(false, 0, "Vulkan offline cache schema or claim is invalid");
            }
            if (document.SteamId != steamId || !allowedAppIds.Contains(document.AppId))
            {
                return new(false, 0, "Vulkan offline cache SteamID or AppID mismatch");
            }
            if (!FixedTimeEquals(document.MachineId, machineId))
            {
                return new(false, 0, "Vulkan offline cache machine binding mismatch");
            }
            var maximumExpiry = document.IssuedUnixSeconds + (long)Lifetime.TotalSeconds;
            var current = now.ToUnixTimeSeconds();
            if (document.ExpiresUnixSeconds > maximumExpiry
                || current < document.IssuedUnixSeconds
                || current >= document.ExpiresUnixSeconds)
            {
                return new(false, 0, "Vulkan offline cache expired or has invalid timestamps");
            }
            if (!FixedTimeEquals(document.Hmac, Sign(document)))
            {
                return new(false, 0, "Vulkan offline cache signature mismatch");
            }
            return new(true, document.AppId, "Vulkan offline cache valid");
        }
        catch (Exception ex)
        {
            return new(false, 0, $"Vulkan offline cache invalid: {ex.Message}");
        }
    }

    public bool WriteAndVerify(ulong steamId, int appId, IReadOnlySet<int> allowedAppIds, DateTimeOffset now)
    {
        if (steamId == 0 || !allowedAppIds.Contains(appId)
            || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(machineId))
        {
            return false;
        }

        var issued = now.ToUnixTimeSeconds();
        var document = new CacheDocument
        {
            SchemaVersion = SchemaVersion,
            SteamId = steamId,
            AppId = appId,
            Claim = Claim,
            IssuedUnixSeconds = issued,
            ExpiresUnixSeconds = issued + (long)Lifetime.TotalSeconds,
            MachineId = machineId,
        };
        document.Hmac = Sign(document);

        var directory = Path.GetDirectoryName(path)!;
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
            var readBack = Read(steamId, allowedAppIds, now);
            return readBack.Allowed && readBack.AppId == appId;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { }
        }
    }

    public void Delete(ulong steamId)
    {
        if (!File.Exists(path)) return;
        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path));
            if (document is null || document.SteamId == steamId)
            {
                File.Delete(path);
            }
        }
        catch
        {
            File.Delete(path);
        }
    }

    private string Sign(CacheDocument document) => HmacHex(
        secret,
        $"{document.SchemaVersion}|{document.SteamId}|{document.AppId}|{document.Claim}|{document.IssuedUnixSeconds}|{document.ExpiresUnixSeconds}|{document.MachineId}");

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
