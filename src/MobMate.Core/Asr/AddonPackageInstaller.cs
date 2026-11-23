using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MobMate.Core.Asr;

/// <summary>
/// A verified package, not a ready or enabled engine. The caller must initialize and
/// test the adapter before changing its active configuration.
/// </summary>
public sealed record PreparedAddonPackage(string Directory, string RuntimeExecutable, string ModelFile);

/// <summary>Stages a pinned runtime and model without modifying an existing active configuration.</summary>
public interface IAddonPackageInstaller
{
    Task<PreparedAddonPackage> PrepareAsync(string root, AddonEngine engine, AddonModel model, CancellationToken cancellationToken = default);
    Task VerifyAsync(string root, AddonEngine engine, AddonModel model, PreparedAddonPackage package, CancellationToken cancellationToken = default);
}

public sealed class AddonPackageInstaller : IAddonPackageInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly Func<string, long> _freeSpace;

    public AddonPackageInstaller(HttpClient http, Func<string, long>? freeSpace = null)
    {
        _http = http;
        _freeSpace = freeSpace ?? (root => new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace);
    }

    public async Task<PreparedAddonPackage> PrepareAsync(
        string root, AddonEngine engine, AddonModel model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!engine.Models.Contains(model)) throw new ArgumentException("Model is not in the engine catalog", nameof(model));
        ValidateArtifact(engine.Runtime.Artifact);
        ValidateArtifact(model.Artifact);
        ValidateFileName(model.FileName);
        var notices = GetNotices(engine, model);
        if (engine.Runtime.MaximumExpandedBytes <= 0 || engine.Runtime.Files.Count == 0)
            throw new InvalidDataException("Invalid runtime layout");

        var fullRoot = Path.GetFullPath(root);
        var gate = Gates.GetOrAdd(fullRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            EnsureNoReparsePoints(fullRoot);
            Directory.CreateDirectory(fullRoot);
            EnsureNoReparsePoints(fullRoot);
            var existingPackages = Path.Combine(fullRoot, "packages");
            EnsureNoReparsePoints(existingPackages);
            if (Directory.Exists(existingPackages))
            {
                foreach (var directory in Directory.GetDirectories(existingPackages))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                    EnsureNoReparsePoints(directory);
                    if (!File.Exists(Path.Combine(directory, model.FileName))) continue;
                    var existing = new PreparedAddonPackage(directory, RuntimePath(Path.Combine(directory, "runtime"), engine.Runtime.Executable),
                        Path.Combine(directory, model.FileName));
                    // A retained package is reused only after the same complete verification. Never silently repair it.
                    await VerifyAsync(fullRoot, engine, model, existing, cancellationToken).ConfigureAwait(false);
                    return existing;
                }
            }
            var needed = checked(engine.Runtime.Artifact.Bytes + model.Artifact.Bytes + engine.Runtime.MaximumExpandedBytes + notices.Sum(n => n.Artifact.Bytes));
            if (_freeSpace(fullRoot) < needed) throw new IOException("Insufficient free space for staging and extraction");

            var stages = Path.Combine(fullRoot, ".staging");
            var packages = Path.Combine(fullRoot, "packages");
            EnsureNoReparsePoints(stages);
            EnsureNoReparsePoints(packages);
            Directory.CreateDirectory(stages);
            Directory.CreateDirectory(packages);
            EnsureNoReparsePoints(stages);
            EnsureNoReparsePoints(packages);
            var id = Guid.NewGuid().ToString("N");
            staging = Path.Combine(stages, id);
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "runtime.zip");
            var modelPath = Path.Combine(staging, model.FileName);
            await FetchAsync(fullRoot, engine.Runtime.Artifact, archive, cancellationToken).ConfigureAwait(false);
            await FetchAsync(fullRoot, model.Artifact, modelPath, cancellationToken).ConfigureAwait(false);
            foreach (var notice in notices)
                await FetchAsync(fullRoot, notice.Artifact, Path.Combine(staging, notice.FileName), cancellationToken).ConfigureAwait(false);
            var runtimePath = Path.Combine(staging, "runtime");
            Directory.CreateDirectory(runtimePath);
            await ExtractAsync(archive, runtimePath, engine, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(packages, id);
            Directory.Move(staging, destination);
            staging = null;
            return new PreparedAddonPackage(destination, RuntimePath(Path.Combine(destination, "runtime"), engine.Runtime.Executable),
                Path.Combine(destination, model.FileName));
        }
        finally
        {
            try
            {
                if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>Rechecks a previously staged package before its executable can be used.</summary>
    public async Task VerifyAsync(string root, AddonEngine engine, AddonModel model, PreparedAddonPackage package, CancellationToken cancellationToken = default)
    {
        if (!engine.Models.Contains(model)) throw new ArgumentException("Model is not in the engine catalog", nameof(model));
        ValidateArtifact(engine.Runtime.Artifact);
        ValidateArtifact(model.Artifact);
        ValidateFileName(model.FileName);
        var notices = GetNotices(engine, model);
        var packages = Path.GetFullPath(Path.Combine(root, "packages"));
        var directory = Path.GetFullPath(package.Directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(directory), packages, comparison) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new InvalidDataException("Package is outside the package store");
        var runtime = Path.Combine(directory, "runtime");
        var executable = RuntimePath(runtime, engine.Runtime.Executable);
        var modelPath = Path.Combine(directory, model.FileName);
        if (!string.Equals(Path.GetFullPath(package.RuntimeExecutable), executable, comparison) ||
            !string.Equals(Path.GetFullPath(package.ModelFile), modelPath, comparison))
            throw new InvalidDataException("Package paths do not match the catalog");
        var archive = Path.Combine(directory, "runtime.zip");
        EnsureNoReparsePoints(archive);
        EnsureNoReparsePoints(modelPath);
        EnsureNoReparsePoints(runtime);
        await VerifyFileAsync(archive, engine.Runtime.Artifact, cancellationToken).ConfigureAwait(false);
        await VerifyFileAsync(modelPath, model.Artifact, cancellationToken).ConfigureAwait(false);
        foreach (var notice in notices)
        {
            var noticePath = Path.Combine(directory, notice.FileName);
            EnsureNoReparsePoints(noticePath);
            await VerifyFileAsync(noticePath, notice.Artifact, cancellationToken).ConfigureAwait(false);
        }
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in engine.Runtime.Files)
        {
            ValidateRuntimePath(file);
            if (!expected.Add(file)) throw new InvalidDataException("Duplicate runtime file name");
        }
        ValidateRuntimePath(engine.Runtime.Executable);
        if (!expected.Contains(engine.Runtime.Executable) || engine.Runtime.MaximumExpandedBytes <= 0)
            throw new InvalidDataException("Invalid runtime layout");
        ValidateRuntimeTree(runtime, engine.Runtime);
        if (engine.Id == AddonCatalog.QwenId && SpeechLinuxRuntime.Matches(engine.Runtime))
        {
            await SpeechLinuxRuntime.VerifyExtractedAsync(archive, runtime, cancellationToken).ConfigureAwait(false);
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRuntimePath(entry.FullName);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((unixType != 0 && unixType != 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                !expected.Contains(entry.FullName) || !seen.Add(entry.FullName))
                throw new InvalidDataException("Unsafe or unexpected runtime archive entry");
            expanded = checked(expanded + entry.Length);
            if (expanded > engine.Runtime.MaximumExpandedBytes) throw new InvalidDataException("Runtime archive exceeds expansion limit");
            var path = RuntimePath(runtime, entry.FullName);
            EnsureNoReparsePoints(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != entry.Length) throw new InvalidDataException("Runtime file length changed");
            await using var original = entry.Open();
            await using var extracted = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var expectedHash = await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false);
            var actualHash = await SHA256.HashDataAsync(extracted, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) throw new InvalidDataException("Runtime file changed");
        }
        if (!seen.SetEquals(expected)) throw new InvalidDataException("Runtime archive is incomplete");
    }

    private static async Task VerifyFileAsync(string path, AddonArtifact artifact, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length != artifact.Bytes) throw new InvalidDataException("Artifact length mismatch");
        var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(artifact.Sha256)))
            throw new InvalidDataException("Artifact SHA256 mismatch");
    }

    private static void ValidateArtifact(AddonArtifact artifact)
    {
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            artifact.Bytes <= 0 || artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid pinned artifact");
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid package file name");
    }

    internal static void ValidateRuntimePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name))
            throw new InvalidDataException("Invalid relative runtime path");
        foreach (var part in name.Split('/')) ValidateFileName(part);
    }

    internal static string RuntimePath(string root, string relative)
    {
        ValidateRuntimePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Runtime path escaped its root");
        return path;
    }

    internal static void ValidateRuntimeTree(string root, AddonRuntime runtime)
    {
        EnsureNoReparsePoints(root);
        if (!Directory.Exists(root)) throw new InvalidDataException("Runtime is missing");
        var files = new HashSet<string>(runtime.Files, StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in runtime.Files)
        {
            ValidateRuntimePath(file);
            for (var slash = file.LastIndexOf('/'); slash > 0; slash = file.LastIndexOf('/', slash - 1))
                directories.Add(file[..slash]);
        }
        var pending = new Stack<string>(); pending.Push(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.GetFileSystemEntries(pending.Pop()))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime contains a link");
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!directories.Contains(relative)) throw new InvalidDataException("Unexpected runtime directory");
                    pending.Push(entry);
                }
                else if (!files.Contains(relative) || !seen.Add(relative)) throw new InvalidDataException("Runtime layout changed");
            }
        }
        if (!seen.SetEquals(files)) throw new InvalidDataException("Runtime layout is incomplete");
    }

    private async Task FetchAsync(string root, AddonArtifact artifact, string destination, CancellationToken token)
    {
        // Optional verified import cache inside this package store; no arbitrary external executable path.
        var cached = Path.Combine(root, "artifacts", artifact.Sha256.ToLowerInvariant());
        EnsureNoReparsePoints(cached);
        if (File.Exists(cached))
        {
            await VerifyFileAsync(cached, artifact, token).ConfigureAwait(false);
            File.Copy(cached, destination, overwrite: false);
        }
        else await DownloadAsync(artifact, destination, token).ConfigureAwait(false);
    }

    internal static IReadOnlyList<AddonNotice> GetNotices(AddonEngine engine, AddonModel model)
    {
        var notices = (engine.Runtime.Notices ?? Array.Empty<AddonNotice>())
            .Concat(model.Notices ?? Array.Empty<AddonNotice>()).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "runtime", "runtime.zip", model.FileName };
        foreach (var notice in notices)
        {
            ValidateFileName(notice.FileName);
            ValidateArtifact(notice.Artifact);
            if (!names.Add(notice.FileName)) throw new InvalidDataException("Duplicate package file name");
        }
        return notices;
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Package path contains a link or reparse point");
        }
    }

    // Also used by isolated evaluation fixtures before a new runtime layout is admitted to the product catalog.
    internal async Task DownloadAsync(AddonArtifact artifact, string destination, CancellationToken token)
    {
        ValidateArtifact(artifact);
        EnsureNoReparsePoints(destination);
        using var response = await _http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size != artifact.Bytes)
            throw new InvalidDataException("Artifact length mismatch");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > artifact.Bytes) throw new InvalidDataException("Artifact length exceeded");
            hash.AppendData(buffer, 0, count);
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (total != artifact.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
            throw new InvalidDataException("Artifact length or SHA256 mismatch");
    }

    private static async Task ExtractAsync(string path, string target, AddonEngine engine, CancellationToken token)
    {
        var runtime = engine.Runtime;
        if (engine.Id == AddonCatalog.QwenId && SpeechLinuxRuntime.Matches(runtime))
        {
            await SpeechLinuxRuntime.ExtractAsync(path, target, token).ConfigureAwait(false);
            return;
        }
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in runtime.Files)
        {
            ValidateRuntimePath(file);
            if (!expected.Add(file)) throw new InvalidDataException("Duplicate runtime file name");
        }
        ValidateRuntimePath(runtime.Executable);
        if (!expected.Contains(runtime.Executable)) throw new InvalidDataException("Missing worker executable in runtime layout");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            ValidateRuntimePath(entry.FullName);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((unixType != 0 && unixType != 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                !expected.Contains(entry.FullName) || !seen.Add(entry.FullName))
                throw new InvalidDataException("Unsafe or unexpected runtime archive entry");
            expanded = checked(expanded + entry.Length);
            if (expanded > runtime.MaximumExpandedBytes) throw new InvalidDataException("Runtime archive exceeds expansion limit");
            var filePath = RuntimePath(target, entry.FullName);
            EnsureNoReparsePoints(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Runtime entry exceeds declared length");
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("Runtime entry length mismatch");
        }
        if (!seen.SetEquals(expected)) throw new InvalidDataException("Runtime archive is incomplete");
    }
}
