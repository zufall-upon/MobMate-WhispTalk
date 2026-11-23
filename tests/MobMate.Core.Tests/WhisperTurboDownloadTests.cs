extern alias SteamCore;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SteamCore::MobMate.Core.Asr;
using Xunit;

namespace MobMate.Steam.Tests;

public sealed class WhisperTurboDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whisper-turbo-tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Model = Encoding.UTF8.GetBytes("synthetic pinned whisper model");
    private static readonly AddonArtifact Pin = new("https://example.invalid/turbo.bin", Model.Length,
        Convert.ToHexString(SHA256.HashData(Model)));

    [Fact]
    public async Task DiscoveryAndLocalSelectionDoNotDownloadOrCreateCache()
    {
        using var handler = new Handler(() => throw new InvalidOperationException("unexpected HTTP"));
        using var http = new HttpClient(handler);
        var download = Create(http);
        Assert.False(Directory.Exists(_root));
        Assert.Equal("custom.bin", await download.ResolveSelectionAsync("custom.bin", new[] { "custom.bin" }, default));
        Assert.Null(await download.ResolveSelectionAsync("https://example.invalid/arbitrary.bin", Array.Empty<string>(), default));
        var existing = Path.Combine("steam", "models", WhisperTurboDownload.FileName);
        Assert.Equal(existing, await download.ResolveSelectionAsync(WhisperTurboDownload.RelativePath, new[] { existing }, default));
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(_root));
        Assert.Equal("models/whisper/ggml-small-q8_0.bin", new SteamCore::MobMate.Core.AppConfig().DefaultModelPath);
    }

    [Fact]
    public async Task VerifiedDownloadIsAtomicAndReusedAcrossInstances()
    {
        using var handler = new Handler(() => Response(Model));
        using var http = new HttpClient(handler);
        var first = Create(http);
        var second = Create(http);
        var results = await Task.WhenAll<string?>(first.ResolveSelectionAsync(WhisperTurboDownload.RelativePath, Array.Empty<string>(), default),
            second.EnsureDownloadedAsync(default));
        Assert.All(results, path => Assert.Equal(first.ModelPath, path));
        Assert.Equal(Model, File.ReadAllBytes(first.ModelPath));
        Assert.Equal(first.ModelPath, await second.ResolveSelectionAsync(first.ModelPath, new[] { first.ModelPath }, default));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("digest")]
    [InlineData("header")]
    [InlineData("partial-response")]
    public async Task InvalidResponseNeverAdoptsModelOrRetries(string failure)
    {
        var bytes = failure switch
        {
            "truncated" => Model[..^1],
            "oversized" => Model.Concat(new byte[] { 0 }).ToArray(),
            "digest" => new byte[Model.Length],
            _ => Model
        };
        using var handler = new Handler(() =>
        {
            var response = Response(bytes);
            if (failure == "header") response.Content.Headers.ContentLength = Model.Length + 1;
            if (failure == "partial-response") response.StatusCode = HttpStatusCode.PartialContent;
            return response;
        });
        using var http = new HttpClient(handler);
        var download = Create(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => download.EnsureDownloadedAsync(default));
        Assert.False(File.Exists(download.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExistingInvalidModelIsPreservedWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var existing = new byte[Model.Length];
        File.WriteAllBytes(Path.Combine(_root, WhisperTurboDownload.FileName), existing);
        using var handler = new Handler(() => throw new InvalidOperationException("unexpected HTTP"));
        using var http = new HttpClient(handler);
        var download = Create(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => download.ResolveSelectionAsync(download.ModelPath, new[] { download.ModelPath }, default));
        Assert.Equal(existing, File.ReadAllBytes(download.ModelPath));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringStreamingRemovesOnlyItsPartialAndReleasesLock()
    {
        Directory.CreateDirectory(_root);
        var unrelated = Path.Combine(_root, "retained.partial");
        File.WriteAllText(unrelated, "preserve");
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelStream(Model, cancellation))
        });
        using var http = new HttpClient(handler);
        var download = Create(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.EnsureDownloadedAsync(cancellation.Token));
        Assert.False(File.Exists(download.ModelPath));
        Assert.Equal(new[] { unrelated }, Directory.GetFiles(_root, "*.partial"));
        using var goodHandler = new Handler(() => Response(Model));
        using var goodHttp = new HttpClient(goodHandler);
        Assert.Equal(download.ModelPath, await Create(goodHttp).EnsureDownloadedAsync(default));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task DiskFailureAndHttpFailureDoNotAdoptOrRetry()
    {
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var download = new WhisperTurboDownload(http, _root, Pin, _ => 0);
        await Assert.ThrowsAsync<IOException>(() => download.EnsureDownloadedAsync(default));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(http).EnsureDownloadedAsync(default));
        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(download.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task ConcurrentExternalFileIsNeverOverwritten()
    {
        using var handler = new Handler(() =>
        {
            File.WriteAllText(Path.Combine(_root, WhisperTurboDownload.FileName), "external model");
            return Response(Model);
        });
        using var http = new HttpClient(handler);
        var download = Create(http);
        await Assert.ThrowsAsync<IOException>(() => download.EnsureDownloadedAsync(default));
        Assert.Equal("external model", File.ReadAllText(download.ModelPath));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    private WhisperTurboDownload Create(HttpClient http) => new(http, _root, Pin, _ => long.MaxValue);
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK)
    {
        // No Content-Length: exercise the streaming bound, not only the header check.
        Content = new StreamContent(new UnseekableStream(bytes))
    };

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Pin.Url, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(response());
        }
    }

    private class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancelStream(byte[] bytes, CancellationTokenSource cancellation) : UnseekableStream(bytes)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) cancellation.Cancel();
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
