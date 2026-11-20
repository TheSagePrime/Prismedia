using System.Net;
using System.Net.Sockets;
using Prismedia.Infrastructure.Security.Outbound;

namespace Prismedia.Infrastructure.Tests.Security;

public sealed class OutboundAddressPolicyTests {
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.6.7")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    public void AlwaysBlockedAddressesAreRefusedInEveryScope(string address) {
        var ip = IPAddress.Parse(address);
        Assert.False(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.PublicOnly));
        Assert.False(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.AllowPrivateNetworks));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:192.168.1.10")]
    public void PrivateAddressesAreAllowedOnlyWhenTheScopePermitsThem(string address) {
        var ip = IPAddress.Parse(address);
        Assert.False(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.PublicOnly));
        Assert.True(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.AllowPrivateNetworks));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:93.184.216.34")]
    public void PublicAddressesAreAllowed(string address) {
        var ip = IPAddress.Parse(address);
        Assert.True(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.PublicOnly));
        Assert.True(OutboundAddressPolicy.IsAllowed(ip, OutboundNetworkScope.AllowPrivateNetworks));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task GuardedHandlerNeverConnectsToLoopback(string host) {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(GuardedHttpHandlerFactory.Create(OutboundNetworkScope.AllowPrivateNetworks, maxRedirects: 5));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://{host}:{port}/poster.jpg"));

        Assert.False(listener.Pending());
    }
}
