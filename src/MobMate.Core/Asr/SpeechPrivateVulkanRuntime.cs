#if DEBUG && QWEN_PRIVATE_VULKAN
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MobMate.Core.Asr;

// Private evaluation only. The ordinary installer still verifies its pinned CPU
// package before SpeechWorker is reached; this is an additional fixed artifact.
internal static class SpeechPrivateVulkanRuntime
{
    internal const string RootVariable = "MOBMATE_PRIVATE_QWEN_VULKAN_ROOT";
    private const string ArchiveName = "speech-0.10.1-linux-x64-vulkan.zip";
    private const string ArchiveHash = "187d639813c1836f24bcd22f518b9050f8c10bfd6af3b8198c2d8fb6122165ba";
    private const string ExecutableHash = "bb9569edf68df79ba15814a40be1d2548f1e30dfa4f3a477be1872103441bb02";
    private static readonly Dictionary<string, (long Length, int Mode)> Entries = new(StringComparer.Ordinal)
    {
        ["libspeech.so"] = (14, 0xa1ff),
        ["libspeech.so.4"] = (49024856, 0x81ed),
        ["speech"] = (51756176, 0x81ed),
        ["speech.h"] = (85854, 0x81a4)
    };

    internal static async Task<string> VerifyAsync(string root, CancellationToken token)
    {
        if (!SpeechLinuxRuntime.Available || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("Private Vulkan root/platform is invalid");
        root = Path.GetFullPath(root);
        var archive = Path.Combine(root, ArchiveName);
        var directory = Path.Combine(root, "runtime");
        var executable = Path.Combine(directory, "speech");
        AddonPackageInstaller.EnsureNoReparsePoints(archive);
        AddonPackageInstaller.EnsureNoReparsePoints(directory);
        AddonPackageInstaller.EnsureNoReparsePoints(executable);
        await VerifyFileAsync(archive, 32437928, ArchiveHash, token).ConfigureAwait(false);
        using (var zip = ZipFile.OpenRead(archive))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count != Entries.Count) throw new InvalidDataException("Private Vulkan archive layout changed");
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!Entries.TryGetValue(entry.FullName, out var expected) || !seen.Add(entry.FullName) ||
                    entry.Length != expected.Length || (entry.ExternalAttributes >> 16 & 0xffff) != expected.Mode)
                    throw new InvalidDataException("Private Vulkan archive entry changed");
            }
        }
        if (!Directory.Exists(directory) || Directory.GetFileSystemEntries(directory).Length != 1 ||
            !File.Exists(executable) || (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException("Private Vulkan runtime layout changed");
        await VerifyFileAsync(executable, 51756176, ExecutableHash, token).ConfigureAwait(false);
        return executable;
    }

    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length != size || !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(hash)))
            throw new InvalidDataException("Private Vulkan pinned artifact changed");
    }

    internal static async Task RecordAsync(string root, int pid, string ready, string info, string diagnostics,
        bool truncated, CancellationToken token)
    {
        var output = Path.Combine(root, $"worker-evidence-{pid}.json");
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, true);
        using var readyJson = JsonDocument.Parse(ready);
        using var infoJson = JsonDocument.Parse(info);
        await JsonSerializer.SerializeAsync(stream, new { pid, ready = readyJson.RootElement, info = infoJson.RootElement,
            diagnostics, diagnosticsTruncated = truncated }, cancellationToken: token).ConfigureAwait(false);
    }
}
#endif
