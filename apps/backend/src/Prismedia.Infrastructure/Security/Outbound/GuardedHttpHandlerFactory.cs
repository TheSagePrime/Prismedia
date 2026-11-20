using System.Net;
using System.Net.Sockets;

namespace Prismedia.Infrastructure.Security.Outbound;

/// <summary>
/// Builds <see cref="SocketsHttpHandler"/> instances for fetching untrusted URLs. The destination policy
/// is enforced in <see cref="SocketsHttpHandler.ConnectCallback"/>: the handler resolves the host itself,
/// drops addresses the <see cref="OutboundAddressPolicy"/> refuses, and connects the socket to a vetted
/// address. Because the check runs at connect time for every connection — including each redirect hop —
/// a DNS answer that changes between validation and connection cannot steer the fetch elsewhere.
/// </summary>
/// <remarks>
/// Guarded handlers never use an HTTP proxy: with a proxy the connect callback would only see the proxy
/// endpoint and the real destination could not be checked.
/// </remarks>
public static class GuardedHttpHandlerFactory {
    /// <summary>Creates a guarded handler.</summary>
    /// <param name="scope">Destination networks the handler may connect to.</param>
    /// <param name="maxRedirects">Maximum redirects to follow; zero disables redirects.</param>
    public static SocketsHttpHandler Create(OutboundNetworkScope scope, int maxRedirects = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRedirects);
        return new SocketsHttpHandler {
            UseProxy = false,
            AllowAutoRedirect = maxRedirects > 0,
            MaxAutomaticRedirections = Math.Max(1, maxRedirects),
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, scope, cancellationToken)
        };
    }

    /// <summary>
    /// Resolves <paramref name="endpoint"/>, filters the answers through the policy, and returns a stream
    /// connected to the first reachable allowed address.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        OutboundNetworkScope scope,
        CancellationToken cancellationToken) {
        IPAddress[] candidates = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        var allowed = candidates.Where(address => OutboundAddressPolicy.IsAllowed(address, scope)).ToArray();
        if (allowed.Length == 0) {
            throw new HttpRequestException($"Outbound connection to '{endpoint.Host}' is not permitted.");
        }

        SocketException? lastError = null;
        foreach (var address in allowed) {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            } catch (SocketException ex) {
                socket.Dispose();
                lastError = ex;
            } catch {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not connect to '{endpoint.Host}'.", lastError);
    }
}
