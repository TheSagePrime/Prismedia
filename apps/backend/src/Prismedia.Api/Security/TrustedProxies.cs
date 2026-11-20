using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Prismedia.Api.Security;

/// <summary>
/// Opt-in reverse proxy support. Forwarded headers (<c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c>) are honoured only when the direct peer is one of the
/// operator-configured proxies, so a TLS-terminating proxy yields <c>Secure</c> session
/// cookies and the login throttle sees the real client address. With no configuration
/// every forwarded header is ignored, including from loopback.
/// </summary>
internal static class TrustedProxies {
    /// <summary>Environment variable holding a comma-separated list of proxy IPs or CIDR ranges.</summary>
    internal const string EnvironmentKey = "PRISMEDIA_TRUSTED_PROXIES";

    /// <summary>Configuration-file equivalent of <see cref="EnvironmentKey"/>.</summary>
    internal const string ConfigurationKey = "Prismedia:TrustedProxies";

    /// <summary>
    /// Parses the configured proxy list into forwarded-header options, or returns null when
    /// no proxy is configured. Throws on an unparseable entry so a typo fails loudly at startup
    /// instead of silently trusting nothing (or everything).
    /// </summary>
    internal static ForwardedHeadersOptions? CreateOptions(IConfiguration configuration) {
        var raw = configuration[EnvironmentKey] ?? configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw)) {
            return null;
        }

        var options = new ForwardedHeadersOptions {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Walk back through every trusted hop; processing stops at the first untrusted address.
            ForwardLimit = null,
        };
        // The framework trusts loopback by default; only the operator's list is trusted here.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (entry.Contains('/')) {
                if (!System.Net.IPNetwork.TryParse(entry, out var network)) {
                    throw new InvalidOperationException($"{EnvironmentKey} entry '{entry}' is not a valid CIDR range.");
                }

                options.KnownIPNetworks.Add(network);
                continue;
            }

            if (!IPAddress.TryParse(entry, out var address)) {
                throw new InvalidOperationException($"{EnvironmentKey} entry '{entry}' is not a valid IP address.");
            }

            options.KnownProxies.Add(address);
            if (address.IsIPv4MappedToIPv6) {
                options.KnownProxies.Add(address.MapToIPv4());
            } else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                options.KnownProxies.Add(address.MapToIPv6());
            }
        }

        return options;
    }

    /// <summary>Adds the forwarded-headers middleware when trusted proxies are configured.</summary>
    internal static IApplicationBuilder UsePrismediaTrustedProxies(
        this IApplicationBuilder app,
        IConfiguration configuration) {
        var options = CreateOptions(configuration);
        return options is null ? app : app.UseForwardedHeaders(options);
    }
}
