using System.Net;
using Prismedia.Application.Artwork;
using Prismedia.Infrastructure.Security.Outbound;

namespace Prismedia.Infrastructure.Media.Artwork;

/// <summary>Downloaded artwork bytes together with their sniffed format.</summary>
/// <param name="Bytes">Complete image content.</param>
/// <param name="Format">Format detected from the content's magic bytes.</param>
public sealed record RemoteArtworkImage(byte[] Bytes, SniffedImageFormat Format);

/// <summary>
/// Single guarded fetcher for remote artwork named by metadata proposals and plugins. It accepts only
/// absolute http(s) URLs, reads response bodies under a size cap, and returns content only when it is a
/// recognized raster image. The default client connects through <see cref="GuardedHttpHandlerFactory"/>,
/// which refuses loopback, link-local, and instance-metadata destinations (including on redirect hops).
/// </summary>
/// <remarks>
/// Private LAN ranges are reachable by default because self-hosted metadata sources (user-installed
/// scrapers and plugins pointed at household services) legitimately return LAN artwork URLs.
/// </remarks>
public sealed class RemoteArtworkFetcher {
    /// <summary>Largest artwork response accepted.</summary>
    public const long DefaultMaxBytes = 32L * 1024 * 1024;

    // A browser-like agent: many image CDNs reject generic/bot agents with 403.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const int MaxRedirects = 5;

    private static readonly Lazy<HttpClient> SharedClient = new(CreateDefaultHttpClient);

    /// <summary>Default end-to-end deadline for one artwork download, covering headers and body.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly long _maxBytes;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a fetcher.</summary>
    /// <param name="http">
    /// Optional client for tests or custom hosts. When null, a process-wide guarded client is used.
    /// </param>
    /// <param name="maxBytes">Largest accepted response body.</param>
    /// <param name="timeout">
    /// End-to-end deadline for one download (headers and body); defaults to <see cref="DefaultTimeout"/>.
    /// </param>
    public RemoteArtworkFetcher(HttpClient? http = null, long maxBytes = DefaultMaxBytes, TimeSpan? timeout = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _http = http ?? SharedClient.Value;
        _maxBytes = maxBytes;
        _timeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
    }

    /// <summary>Creates the guarded production client used when no client is supplied.</summary>
    public static HttpClient CreateDefaultHttpClient() =>
        new(GuardedHttpHandlerFactory.Create(OutboundNetworkScope.AllowPrivateNetworks, MaxRedirects)) {
            Timeout = TimeSpan.FromSeconds(30)
        };

    /// <summary>
    /// Downloads and validates one artwork URL. Returns null when the URL is not an allowed remote URL,
    /// the destination is refused or unreachable, the response is unsuccessful or too large, or the body is
    /// not a supported image. Only caller cancellation propagates.
    /// </summary>
    /// <param name="url">Candidate artwork URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<RemoteArtworkImage?> TryFetchAsync(string? url, CancellationToken cancellationToken) {
        if (!RemoteArtworkUrl.TryParse(url, out var uri)) {
            return null;
        }

        // HttpClient.Timeout stops at the response headers when streaming, so bound the body read too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is { } declared && declared > _maxBytes) {
                return null;
            }

            var bytes = await ReadCappedAsync(response.Content, token);
            if (bytes is null) {
                return null;
            }

            var format = ImageSignature.Detect(bytes);
            return format is null ? null : new RemoteArtworkImage(bytes, format);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
            or InvalidOperationException or NotSupportedException or UriFormatException or WebException) {
            return null;
        }
    }

    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken) {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
            if (buffer.Length + read > _maxBytes) {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
