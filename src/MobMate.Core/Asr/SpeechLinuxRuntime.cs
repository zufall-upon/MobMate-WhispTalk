using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MobMate.Core.Asr;

// One pinned Qwen CPU archive, not generic support for links or Linux addons.
// Its development .so alias is verified but deliberately absent from disk:
// the shipped speech worker runs without it. All other archives retain the
// installer's existing special-entry rejection.
internal static class SpeechLinuxRuntime
{
    internal const string ArchiveSha256 = "3396d6b2ec38c2922ff0f24a760f7b45c4ac601788f53f03eb77134393bc768b";
    private const long ArchiveBytes = 6206720;
    private static readonly string[] Files = { "speech", "libspeech.so.4", "speech.h" };
    private static readonly Dictionary<string, (long Bytes, int Mode, string Sha256)> Entries = new(StringComparer.Ordinal)
    {
        ["libspeech.so"] = (14, 0xa1ff, "601a75f79365ef36f63e07ce46a133e4d18c2ab0d0df3b4e24eecf7012955207"),
        ["libspeech.so.4"] = (6793656, 0x81ed, "620a1e5b613bae9249596f1c80e6f0a0df9414e1dcb12de587c3e809a8c9089c"),
        ["speech"] = (9546768, 0x81ed, "35b0019af08e7a1972eb9450fd262a6dc2df4e09c2a578fe229098df8a54fcee"),
        ["speech.h"] = (85854, 0x81a4, "1150c8d525527f0d30b4ed54395249a5ed444ebd90ccc433cdf915e06c837c44")
    };

    internal static bool Available => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal static AddonRuntime ForQwen(AddonRuntime windows) => windows with
    {
        Artifact = new("https://github.com/nyosegawa/speech.cpp/releases/download/v0.10.1/speech-0.10.1-linux-x64-cpu.zip", ArchiveBytes, ArchiveSha256),
        Executable = "speech", Files = Array.AsReadOnly(Files), MaximumExpandedBytes = 32L * 1024 * 1024
    };

    internal static bool Matches(AddonRuntime runtime) => Available && runtime.Id == "speech.cpp" && runtime.Version == "0.10.1" &&
        runtime.Artifact.Bytes == ArchiveBytes && runtime.Artifact.Sha256 == ArchiveSha256 &&
        runtime.Executable == "speech" && runtime.Files.SequenceEqual(Files, StringComparer.Ordinal) &&
        runtime.MaximumExpandedBytes == 32L * 1024 * 1024;

    internal static async Task VerifyArchiveAsync(string path, CancellationToken token)
    {
        AddonPackageInstaller.EnsureNoReparsePoints(path);
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (input.Length != ArchiveBytes || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != ArchiveSha256.ToUpperInvariant())
                throw new InvalidDataException("Pinned Linux runtime changed");
        }
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != Entries.Count) throw new InvalidDataException("Pinned Linux runtime layout changed");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!Entries.TryGetValue(entry.FullName, out var expected) || !seen.Add(entry.FullName) ||
                entry.Length != expected.Bytes || (entry.ExternalAttributes >> 16 & 0xffff) != expected.Mode ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe or unexpected pinned Linux entry");
            await using var content = entry.Open();
            var hash = await SHA256.HashDataAsync(content, token).ConfigureAwait(false);
            if (Convert.ToHexString(hash) != expected.Sha256.ToUpperInvariant())
                throw new InvalidDataException("Pinned Linux entry changed");
            if (entry.FullName == "libspeech.so")
            {
                using var alias = new StreamReader(entry.Open(), Encoding.UTF8, false);
                if (await alias.ReadToEndAsync(token).ConfigureAwait(false) != "libspeech.so.4")
                    throw new InvalidDataException("Pinned Linux alias changed");
            }
        }
    }

    internal static async Task ExtractAsync(string archive, string target, CancellationToken token)
    {
        await VerifyArchiveAsync(archive, token).ConfigureAwait(false);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var name in Files)
        {
            var path = AddonPackageInstaller.RuntimePath(target, name);
            AddonPackageInstaller.EnsureNoReparsePoints(path);
            await using (var source = zip.GetEntry(name)!.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(output, token).ConfigureAwait(false);
            if (OperatingSystem.IsLinux() && name == "speech")
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static async Task VerifyExtractedAsync(string archive, string target, CancellationToken token)
    {
        await VerifyArchiveAsync(archive, token).ConfigureAwait(false);
        foreach (var name in Files)
        {
            var path = AddonPackageInstaller.RuntimePath(target, name);
            AddonPackageInstaller.EnsureNoReparsePoints(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var expected = Entries[name];
            if (input.Length != expected.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != expected.Sha256.ToUpperInvariant())
                throw new InvalidDataException("Extracted Linux runtime changed");
        }
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(Path.Combine(target, "speech")) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException("Linux worker is not executable");
    }
}
