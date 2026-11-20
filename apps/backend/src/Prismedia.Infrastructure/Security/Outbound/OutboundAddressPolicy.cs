using System.Net;
using System.Net.Sockets;

namespace Prismedia.Infrastructure.Security.Outbound;

/// <summary>
/// Which destination networks a guarded outbound fetch may reach.
/// </summary>
public enum OutboundNetworkScope {
    /// <summary>Only publicly routable destinations; private, shared, and unique-local ranges are refused.</summary>
    PublicOnly,

    /// <summary>
    /// Public destinations plus private LAN ranges (RFC 1918, carrier-grade NAT, IPv6 unique-local), for
    /// fetches whose sources are legitimately self-hosted on the household network.
    /// </summary>
    AllowPrivateNetworks
}

/// <summary>
/// Classifies outbound destination addresses for server-side fetches of untrusted URLs. Loopback,
/// unspecified, link-local, multicast/reserved, and cloud instance-metadata addresses are refused under
/// every scope; private ranges are refused unless the scope allows them. IPv4-mapped and NAT64-embedded
/// IPv6 addresses are classified by their embedded IPv4 address.
/// </summary>
public static class OutboundAddressPolicy {
    // Instance-metadata endpoints that do not fall inside an always-blocked range.
    private static readonly IPAddress[] MetadataAddresses = [
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud (inside 100.64.0.0/10)
        IPAddress.Parse("168.63.129.16"), // Azure wire server
        IPAddress.Parse("fd00:ec2::254") // AWS IPv6 (inside fc00::/7)
    ];

    private static readonly byte[] Nat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Returns true when a guarded fetch may connect to <paramref name="address"/>.</summary>
    /// <param name="address">Resolved destination address.</param>
    /// <param name="scope">Networks the caller may reach.</param>
    public static bool IsAllowed(IPAddress address, OutboundNetworkScope scope) {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        if (MetadataAddresses.Any(blocked => blocked.Equals(address))) {
            return false;
        }

        return address.AddressFamily switch {
            AddressFamily.InterNetwork => IsAllowedIPv4(address.GetAddressBytes(), scope),
            AddressFamily.InterNetworkV6 => IsAllowedIPv6(address, scope),
            _ => false
        };
    }

    /// <summary>Unwraps IPv4-mapped (<c>::ffff:a.b.c.d</c>) and NAT64 (<c>64:ff9b::a.b.c.d</c>) addresses.</summary>
    private static IPAddress Normalize(IPAddress address) {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) {
            return address;
        }

        if (address.IsIPv4MappedToIPv6) {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return bytes.AsSpan(0, 12).SequenceEqual(Nat64Prefix)
            ? new IPAddress(bytes.AsSpan(12, 4))
            : address;
    }

    private static bool IsAllowedIPv4(byte[] b, OutboundNetworkScope scope) {
        if (b[0] == 0 // 0.0.0.0/8 "this network"
            || b[0] == 127 // loopback
            || (b[0] == 169 && b[1] == 254) // link-local, includes 169.254.169.254 metadata
            || b[0] >= 224) { // multicast, reserved, broadcast
            return false;
        }

        var isPrivate = b[0] == 10 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168) // 192.168.0.0/16
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // 100.64.0.0/10 carrier-grade NAT
        return !isPrivate || scope == OutboundNetworkScope.AllowPrivateNetworks;
    }

    private static bool IsAllowedIPv6(IPAddress address, OutboundNetworkScope scope) {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal // fe80::/10
            || address.IsIPv6Multicast) {
            return false;
        }

        var isPrivate = address.IsIPv6UniqueLocal // fc00::/7
            || address.IsIPv6SiteLocal; // deprecated fec0::/10
        return !isPrivate || scope == OutboundNetworkScope.AllowPrivateNetworks;
    }
}
