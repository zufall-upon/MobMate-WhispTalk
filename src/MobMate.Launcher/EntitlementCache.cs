using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MobMate.Core;

namespace MobMate.Launcher;

internal sealed record EntitlementCacheResult(bool Allowed, string Reason);

internal interface IEntitlementCache
{
    EntitlementCacheResult Read(ulong steamId, DateTimeOffset now);
    bool WriteAndVerify(ulong steamId, DateTimeOffset now);
    void Delete(ulong steamId);
}

internal sealed class SignedEntitlementCache(string path, string secret, string machineId) : IEntitlementCache
{
    private const int SchemaVersion = 1;
    private const string SupporterClaim = "supporter";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public ulong SteamId { get; set; }
        public string Claim { get; set; } = string.Empty;
        public long IssuedUnixSeconds { get; set; }
        public long ExpiresUnixSeconds { get; set; }
        public string MachineId { get; set; } = string.Empty;
        public string Hmac { get; set; } = string.Empty;
    }

    public static SignedEntitlementCache CreateDefault()
    {
        var configuredSecret = Environment.GetEnvironmentVariable("MOBMATE_STEAM_DLC_SECRET");
        var cacheSecret = string.IsNullOrWhiteSpace(configuredSecret) ? BuildInfo.SecretKey : configuredSecret;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        var cachePath = Path.Combine(documents, "MobMateWhispTalk", "v2", "config", "launcher-entitlement.json");
        var binding = $"{Environment.MachineName}|{Environment.UserName}";
        var boundMachineId = string.IsNullOrWhiteSpace(cacheSecret) ? string.Empty : HmacHex(cacheSecret, binding);
        return new SignedEntitlementCache(cachePath, cacheSecret, boundMachineId);
    }

    public EntitlementCacheResult Read(ulong steamId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(machineId))
        {
            return new(false, "offline cache disabled because signing secret is unavailable");
        }
        if (!File.Exists(path))
        {
            return new(false, "offline cache not found");
        }

        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path));
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                return new(false, "offline cache schema is invalid");
            }
            if (document.SteamId != steamId || document.Claim != SupporterClaim)
            {
                return new(false, "offline cache SteamID or claim mismatch");
            }
            if (!FixedTimeEquals(document.MachineId, machineId))
            {
                return new(false, "offline cache machine binding mismatch");
            }
            var maximumExpiry = document.IssuedUnixSeconds + (long)Lifetime.TotalSeconds;
            if (document.ExpiresUnixSeconds > maximumExpiry
                || now.ToUnixTimeSeconds() < document.IssuedUnixSeconds
                || now.ToUnixTimeSeconds() >= document.ExpiresUnixSeconds)
            {
                return new(false, "offline cache expired or has invalid timestamps");
            }
            var expected = Sign(document);
            if (!FixedTimeEquals(document.Hmac, expected))
            {
                return new(false, "offline cache signature mismatch");
            }
            return new(true, "offline cache valid");
        }
        catch (Exception ex)
        {
            return new(false, $"offline cache invalid: {ex.Message}");
        }
    }

    public bool WriteAndVerify(ulong steamId, DateTimeOffset now)
    {
        if (steamId == 0 || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(machineId))
        {
            return false;
        }

        var issued = now.ToUnixTimeSeconds();
        var document = new CacheDocument
        {
            SchemaVersion = SchemaVersion,
            SteamId = steamId,
            Claim = SupporterClaim,
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
            return Read(steamId, now).Allowed;
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
        if (!File.Exists(path))
        {
            return;
        }
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
        $"{document.SchemaVersion}|{document.SteamId}|{document.Claim}|{document.IssuedUnixSeconds}|{document.ExpiresUnixSeconds}|{document.MachineId}");

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
