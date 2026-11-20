using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prismedia.Api.Security;
using Prismedia.Application.Entities;
using Prismedia.Contracts.Security;

namespace Prismedia.Api.Tests;

public sealed class SecurityHeadersTests : IDisposable {
    private const string InlineBootstrap = "\n\t\t\t\twindow.__boot = 1;\n\t\t\t";
    private static readonly Guid EntityId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"prismedia-headers-{Guid.NewGuid():N}");

    private string CacheRoot => Path.Combine(_root, "cache");

    public SecurityHeadersTests() {
        Directory.CreateDirectory(CacheRoot);
    }

    [Fact]
    public async Task CacheAssetsRequireAuthentication() {
        await WriteCacheAssetAsync("videos/example/thumb.jpg", [0xff, 0xd8, 0xff, 0xd9]);
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var response = await anonymous.GetAsync("/assets/videos/example/thumb.jpg");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CacheAssetsAcceptBearerCookieAndQueryTokens() {
        await WriteCacheAssetAsync("videos/example/thumb.jpg", [0xff, 0xd8, 0xff, 0xd9]);
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/assets/videos/example/thumb.jpg");
        bearer.Headers.Authorization = new AuthenticationHeaderValue(ApiAuthenticationProtocol.BearerScheme, TestAuth.Token);
        using var bearerResponse = await client.SendAsync(bearer);

        using var cookie = new HttpRequestMessage(HttpMethod.Get, "/assets/videos/example/thumb.jpg");
        cookie.Headers.Add("Cookie", $"{PrismediaAuthentication.SessionCookieName}={TestAuth.Token}");
        using var cookieResponse = await client.SendAsync(cookie);

        using var queryResponse = await client.GetAsync(
            $"/assets/videos/example/thumb.jpg?{ApiAuthenticationProtocol.AccessTokenQuery}={TestAuth.Token}");

        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cookieResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
    }

    [Fact]
    public async Task CacheAssetsArePrivateNoSniffAndSandboxed() {
        await WriteCacheAssetAsync("studios/logo.svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        await WriteCacheAssetAsync("plugins/artwork/poster.jpg", [0xff, 0xd8, 0xff, 0xd9]);
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var svg = await client.GetAsync("/assets/studios/logo.svg");
        using var artwork = await client.GetAsync("/assets/plugins/artwork/poster.jpg");

        svg.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        Assert.True(svg.Headers.CacheControl?.Private);
        Assert.False(svg.Headers.CacheControl?.Public);
        Assert.Equal(SecurityHeaders.NoSniff, Header(svg, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Equal(SecurityHeaders.FileContentSecurityPolicy, Header(svg, SecurityHeaders.ContentSecurityPolicyHeader));
        artwork.EnsureSuccessStatusCode();
        Assert.True(artwork.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromDays(365), artwork.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task SvgEntityFileContentIsSandboxed() {
        var path = Path.Combine(_root, "logo.svg");
        await File.WriteAllTextAsync(path, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        using var factory = CreateFactory(new FixedEntityFileContentService(
            new EntityFileContent(EntityId, "source", path, "image/svg+xml")));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/api/entities/{EntityId}/files/source");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SecurityHeaders.NoSniff, Header(response, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Equal(SecurityHeaders.FileContentSecurityPolicy, Header(response, SecurityHeaders.ContentSecurityPolicyHeader));
    }

    [Fact]
    public async Task PdfEntityFileContentIsNotSandboxedSoBrowserViewersStillOpenIt() {
        var path = Path.Combine(_root, "book.pdf");
        await File.WriteAllTextAsync(path, "%PDF-1.4");
        using var factory = CreateFactory(new FixedEntityFileContentService(
            new EntityFileContent(EntityId, "source", path, "application/pdf")));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/api/entities/{EntityId}/files/source");

        response.EnsureSuccessStatusCode();
        Assert.Equal(SecurityHeaders.NoSniff, Header(response, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Null(Header(response, SecurityHeaders.ContentSecurityPolicyHeader));
    }

    [Theory]
    [InlineData("previews/clip.mp4", "video/mp4")]
    [InlineData("audio/track.mp3", "audio/mpeg")]
    public async Task MediaCacheAssetsAreNotSandboxedSoDirectPlaybackWorks(string relativePath, string expectedType) {
        await WriteCacheAssetAsync(relativePath, [0x00, 0x01]);
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/assets/{relativePath}");

        response.EnsureSuccessStatusCode();
        Assert.Equal(expectedType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SecurityHeaders.NoSniff, Header(response, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Null(Header(response, SecurityHeaders.ContentSecurityPolicyHeader));
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("application/xml", true)]
    [InlineData("image/svg+xml; charset=utf-8", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData(null, true)]
    [InlineData("application/pdf", false)]
    [InlineData("video/mp4", false)]
    [InlineData("audio/flac", false)]
    public void FileLockdownCoversEveryPotentiallyActiveType(string? contentType, bool expected) =>
        Assert.Equal(expected, SecurityHeaders.ShouldLockDownFileContent(contentType));

    [Fact]
    public async Task EveryResponseCarriesBaselineHeaders() {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(SecurityHeaders.NoSniff, Header(response, SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Equal(SecurityHeaders.SameOriginReferrer, Header(response, SecurityHeaders.ReferrerPolicyHeader));
        Assert.Equal(SecurityHeaders.SameOriginFrames, Header(response, SecurityHeaders.FrameOptionsHeader));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/videos/some-route")]
    public async Task SpaShellResponsesCarryHashedScriptPolicy(string path) {
        var staticRoot = Path.Combine(_root, "web");
        Directory.CreateDirectory(staticRoot);
        await File.WriteAllTextAsync(
            Path.Combine(staticRoot, "index.html"),
            $"<!doctype html><html><head><script type=\"module\" src=\"/_app/x.js\"></script></head>" +
            $"<body><div><script>{InlineBootstrap}</script></div></body></html>");
        using var factory = CreateFactory(staticRoot: staticRoot);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var policy = Header(response, SecurityHeaders.ContentSecurityPolicyHeader);
        Assert.NotNull(policy);
        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(InlineBootstrap)));
        var scriptDirective = policy.Split(';').Select(part => part.Trim()).Single(part => part.StartsWith("script-src ", StringComparison.Ordinal));
        Assert.Contains("'self'", scriptDirective);
        Assert.Contains($"'sha256-{expectedHash}'", scriptDirective);
        Assert.DoesNotContain("'unsafe-inline'", scriptDirective);
        Assert.DoesNotContain("'unsafe-eval'", scriptDirective);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("frame-ancestors 'self'", policy);
        Assert.Contains("worker-src 'self' blob:", policy);
    }

    [Fact]
    public void ShellPolicyDoesNotRestrictStylesFontsOrImagesThatBookFramesInherit() {
        var policy = SecurityHeaders.BuildSpaShellPolicy("<html></html>");

        Assert.DoesNotContain("default-src", policy);
        Assert.DoesNotContain("style-src", policy);
        Assert.DoesNotContain("font-src", policy);
        Assert.DoesNotContain("img-src", policy);
    }

    [Fact]
    public async Task ForwardedProtoIsIgnoredWhenNoProxyIsTrusted() {
        using var factory = CreateFactory(remoteIp: "10.0.0.5");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client, forwardedFor: "203.0.113.9");

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardedProtoFromUntrustedPeerIsIgnored() {
        using var factory = CreateFactory(remoteIp: "10.0.0.5", trustedProxies: "10.0.0.9, 192.168.0.0/24");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client, forwardedFor: "203.0.113.9");

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("10.0.0.0/24")]
    public async Task ForwardedProtoFromTrustedProxyMarksSessionCookieSecure(string trusted) {
        using var factory = CreateFactory(remoteIp: "10.0.0.5", trustedProxies: trusted);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var cookie = await LoginCookieAsync(client, forwardedFor: "203.0.113.9");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustedProxyThrottlesSignInsPerForwardedClient() {
        using var factory = CreateFactory(remoteIp: "10.0.0.5", trustedProxies: "10.0.0.5");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        HttpStatusCode last = HttpStatusCode.OK;
        for (var attempt = 0; attempt < 20 && last != HttpStatusCode.TooManyRequests; attempt++) {
            last = await LoginStatusAsync(client, "203.0.113.1", $"wrong-{attempt}");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(client, "203.0.113.2", TestAuth.Password));
    }

    [Fact]
    public void InvalidTrustedProxyEntryFailsLoudly() {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TrustedProxies.EnvironmentKey] = "not-an-ip" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => TrustedProxies.CreateOptions(configuration));
    }

    [Fact]
    public void TrustedProxyOptionsDoNotTrustLoopbackImplicitly() {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TrustedProxies.EnvironmentKey] = "10.0.0.9" })
            .Build();

        var options = TrustedProxies.CreateOptions(configuration)!;

        Assert.DoesNotContain(IPAddress.Loopback, options.KnownProxies);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Null(TrustedProxies.CreateOptions(new ConfigurationBuilder().Build()));
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<string> LoginCookieAsync(HttpClient client, string forwardedFor) {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new LoginRequest(TestAuth.Username, TestAuth.Password))
        };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith($"{PrismediaAuthentication.SessionCookieName}=", StringComparison.Ordinal));
    }

    private static async Task<HttpStatusCode> LoginStatusAsync(HttpClient client, string forwardedFor, string password) {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new LoginRequest(TestAuth.Username, password))
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ||
        response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    private async Task WriteCacheAssetAsync(string relativePath, byte[] bytes) {
        var path = Path.Combine(CacheRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private WebApplicationFactory<Program> CreateFactory(
        IEntityFileContentService? contentService = null,
        string? staticRoot = null,
        string? remoteIp = null,
        string? trustedProxies = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => {
                builder.UseSetting("Prismedia:CacheDir", CacheRoot);
                if (staticRoot is not null) {
                    builder.UseSetting("Prismedia:StaticWebRoot", staticRoot);
                }

                if (trustedProxies is not null) {
                    builder.UseSetting(TrustedProxies.ConfigurationKey, trustedProxies);
                }

                builder.ConfigureServices(services => {
                    if (contentService is not null) {
                        services.AddSingleton(contentService);
                    }

                    if (remoteIp is not null) {
                        services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp)));
                    }
                });
            })
            .WithTestAuth();

    /// <summary>TestServer has no socket peer; pin one so proxy trust can be exercised.</summary>
    private sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextMiddleware) => {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class FixedEntityFileContentService(EntityFileContent content) : IEntityFileContentService {
        public Task<EntityFileContent?> GetContentAsync(Guid entityId, string role, CancellationToken cancellationToken) =>
            Task.FromResult<EntityFileContent?>(entityId == EntityId ? content : null);
    }
}
