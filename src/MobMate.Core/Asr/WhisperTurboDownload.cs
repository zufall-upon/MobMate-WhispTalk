using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace MobMate.Core.Asr;

/// <summary>Installs the optional pinned Turbo model only after an explicit user request.</summary>
internal sealed class WhisperTurboDownload
{
    internal const string FileName = "ggml-large-v3-turbo-q8_0.bin";
    internal const string RelativePath = "models/whisper/" + FileName;
    internal static readonly AddonArtifact Artifact = new(
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/" + FileName,
        874188075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1");

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly AddonArtifact _artifact;
    private readonly Func<string, long> _freeSpace;

    internal WhisperTurboDownload(HttpClient http, string root)
        : this(http, root, Artifact, path => new DriveInfo(path).AvailableFreeSpace) { }

    internal WhisperTurboDownload(HttpClient http, string root, AddonArtifact artifact, Func<string, long> freeSpace)
    {
        _http = http;
        Root = Path.GetFullPath(root);
        _artifact = artifact;
        _freeSpace = freeSpace;
    }

    internal string Root { get; }
    internal string ModelPath => Path.Combine(Root, FileName);

    // The existing selection API accepts local paths or this one pinned download request.
    internal async Task<string?> ResolveSelectionAsync(string requested, IEnumerable<string> installed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(requested, ModelPath, comparison))
            return await EnsureDownloadedAsync(cancellationToken).ConfigureAwait(false);
        var local = installed.FirstOrDefault(path => string.Equals(path, requested, comparison));
        if (local is not null) return local;
        if (!string.Equals(requested, RelativePath, StringComparison.Ordinal)) return null;
        // Reuse an existing app/Steam/custom installation before acquiring a second copy.
        local = installed.FirstOrDefault(path => string.Equals(Path.GetFileName(path), FileName, comparison)
            && !string.Equals(path, ModelPath, comparison));
        return local ?? await EnsureDownloadedAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> EnsureDownloadedAsync(CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(Root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Root);
            // A second app/channel must not replace an in-progress or existing model.
            using var fileLock = new FileStream(Path.Combine(Root, ".turbo-download.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(ModelPath))
            {
                await VerifyExistingAsync(cancellationToken).ConfigureAwait(false);
                return ModelPath;
            }
            if (_freeSpace(Root) < _artifact.Bytes + 1024 * 1024)
                throw new IOException("Insufficient free space for the Whisper Turbo model");

            using var response = await _http.GetAsync(_artifact.Url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK ||
                (response.Content.Headers.ContentLength is long length && length != _artifact.Bytes))
                throw new InvalidDataException("Whisper Turbo response size differs from the pinned model");

            temporary = Path.Combine(Root, ".turbo-" + Guid.NewGuid().ToString("N") + ".partial");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + count);
                    if (received > _artifact.Bytes) throw new InvalidDataException("Whisper Turbo download exceeds the pinned size");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (received != _artifact.Bytes || !MatchesDigest(digest.GetHashAndReset()))
                    throw new InvalidDataException("Whisper Turbo download size or SHA256 does not match");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, ModelPath, overwrite: false);
            temporary = null;
            return ModelPath;
        }
        finally
        {
            try
            {
                if (temporary is not null) File.Delete(temporary);
            }
            finally { gate.Release(); }
        }
    }

    private async Task VerifyExistingAsync(CancellationToken cancellationToken)
    {
        await using var input = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != _artifact.Bytes ||
            !MatchesDigest(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)))
            throw new InvalidDataException("Existing Whisper Turbo model differs; it has been preserved");
    }

    private bool MatchesDigest(byte[] digest) =>
        string.Equals(Convert.ToHexString(digest), _artifact.Sha256, StringComparison.OrdinalIgnoreCase);
}
