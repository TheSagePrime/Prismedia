using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Prismedia.Application.Acquisition;
using Prismedia.Infrastructure.Security.Outbound;

namespace Prismedia.Infrastructure.Acquisition;

/// <summary>
/// Resolves a usable magnet link from an indexer release's info page. Meta-search indexers (e.g. Torrentz2)
/// surface only a web page through Prowlarr, but that page embeds the actual magnet — so we fetch it and
/// extract the first <c>magnet:?xt=urn:btih:</c> link (HTML-entity decoded). This is the automatic counterpart
/// to the manual .torrent upload fallback.
///
/// The fetched URL comes from a (possibly public) indexer feed and is therefore untrusted, so this is a
/// guarded outbound fetch: destinations that resolve to loopback/private/link-local addresses are refused
/// (SSRF protection for a LAN-resident server), redirects are not followed, and the response is size-capped.
/// </summary>
public sealed partial class ReleaseLinkResolver(HttpClient http) : IReleaseLinkResolver {
    private const int MaxPageBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"magnet:\?xt=urn:btih:[A-Za-z0-9]{32,40}[^""'<>\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetRegex();

    public async Task<string?> ResolveMagnetAsync(string infoUrl, CancellationToken cancellationToken) {
        if (!Uri.TryCreate(infoUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            return null;
        }

        if (!await IsPublicDestinationAsync(uri, cancellationToken)) {
            return null;
        }

        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Some torrent sites block default user agents.
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Prismedia)");
            // Read only headers first so an oversized body is never fully buffered.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var page = await ReadCappedAsync(response, cancellationToken);
            var decoded = WebUtility.HtmlDecode(page);
            var match = MagnetRegex().Match(decoded);
            return match.Success ? match.Value : null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    /// <summary>Reads at most <see cref="MaxPageBytes"/> of the response so a hostile/huge page can't exhaust memory.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= MaxPageBytes) {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Fast pre-check that refuses URLs whose host resolves to a non-public address. This blocks an indexer
    /// from steering the server's outbound fetch at internal services (SSRF) from its trusted LAN position.
    /// The production client re-applies the same policy at connect time
    /// (<see cref="GuardedHttpHandlerFactory"/>), so a DNS answer that changes after this check is still refused.
    /// </summary>
    private static async Task<bool> IsPublicDestinationAsync(Uri uri, CancellationToken cancellationToken) {
        try {
            IPAddress[] addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(address => !IsBlocked(address));
        } catch (Exception ex) when (ex is SocketException or ArgumentException) {
            return false;
        }
    }

    private static bool IsBlocked(IPAddress address) =>
        !OutboundAddressPolicy.IsAllowed(address, OutboundNetworkScope.PublicOnly);
}
