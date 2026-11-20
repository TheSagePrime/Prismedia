using System.Net;
using System.Net.Sockets;
using System.Text;
using Prismedia.Contracts.Media;
using Prismedia.Infrastructure.Media.Artwork;

namespace Prismedia.Infrastructure.Tests.Media;

public sealed class RemoteArtworkFetcherTests {
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Theory]
    [InlineData("/var/lib/secret")]
    [InlineData("file:///var/lib/secret")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("ftp://images.test/poster.jpg")]
    [InlineData("https://user:pass@images.test/poster.jpg")]
    [InlineData("relative/poster.jpg")]
    public async Task NonHttpReferencesAreNeverRequested(string url) {
        var handler = new StubHandler(_ => new ByteArrayContent(Png));
        var fetcher = new RemoteArtworkFetcher(new HttpClient(handler));

        Assert.Null(await fetcher.TryFetchAsync(url, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExtensionAndContentTypeComeFromSniffedContentNotTheUrl() {
        var fetcher = new RemoteArtworkFetcher(new HttpClient(new StubHandler(_ => new ByteArrayContent(Png))));

        var image = await fetcher.TryFetchAsync("https://images.test/poster.html", CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal(".png", image.Format.Extension);
        Assert.Equal(MediaContentTypes.ImagePng, image.Format.ContentType);
    }

    [Theory]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("root:x:0:0:root:/root:/bin/sh")]
    public async Task NonImageContentIsRejected(string body) {
        var fetcher = new RemoteArtworkFetcher(new HttpClient(new StubHandler(_ =>
            new ByteArrayContent(Encoding.UTF8.GetBytes(body)))));

        Assert.Null(await fetcher.TryFetchAsync("https://images.test/poster.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponsesAreRejectedEvenWithoutContentLength() {
        var oversized = new byte[4096];
        Png.CopyTo(oversized, 0);
        var fetcher = new RemoteArtworkFetcher(
            new HttpClient(new StubHandler(_ => new StreamContent(new MemoryStream(oversized)))),
            maxBytes: 1024);

        Assert.Null(await fetcher.TryFetchAsync("https://images.test/poster.png", CancellationToken.None));
    }

    [Fact]
    public async Task StalledResponseBodiesHitTheDownloadDeadline() {
        var fetcher = new RemoteArtworkFetcher(
            new HttpClient(new StubHandler(_ => new StreamContent(new StalledStream(Png)))),
            timeout: TimeSpan.FromMilliseconds(200));

        var started = DateTime.UtcNow;
        Assert.Null(await fetcher.TryFetchAsync("https://images.test/poster.png", CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task UnsuccessfulResponsesAreSkipped() {
        var fetcher = new RemoteArtworkFetcher(new HttpClient(new StubHandler(
            _ => new ByteArrayContent(Png),
            HttpStatusCode.Forbidden)));

        Assert.Null(await fetcher.TryFetchAsync("https://images.test/poster.png", CancellationToken.None));
    }

    [Fact]
    public async Task DefaultClientRefusesLoopbackDestinations() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fetcher = new RemoteArtworkFetcher();

        Assert.Null(await fetcher.TryFetchAsync($"http://127.0.0.1:{port}/poster.jpg", CancellationToken.None));
        Assert.Null(await fetcher.TryFetchAsync($"http://[::ffff:127.0.0.1]:{port}/poster.jpg", CancellationToken.None));
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0 }, ".gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, ".webp")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x69, 0x66, 0x31, 0, 0, 0, 0, 0x6D, 0x69, 0x66, 0x31, 0x61, 0x76, 0x69, 0x66, 0x6D, 0x69, 0x61, 0x66 }, ".avif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x46, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0, 0, 0 }, ".bmp")]
    public void SupportedFormatsAreRecognized(byte[] header, string extension) =>
        Assert.Equal(extension, ImageSignature.Detect(header)?.Extension);

    [Theory]
    [InlineData(new byte[] { 0x42, 0x4D, 0x20, 0x69, 0x73, 0x20, 0x74, 0x65, 0x78, 0x74, 0x20, 0x6E, 0x6F, 0x74, 0x20, 0x61, 0x6E, 0x20 })]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0, 0x6D, 0x70, 0x34, 0x31, 0x69, 0x73, 0x6F, 0x6D })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    public void LookalikeContentIsNotRecognized(byte[] header) =>
        Assert.Null(ImageSignature.Detect(header));

    /// <summary>Returns its prefix, then never completes another read until cancelled.</summary>
    private sealed class StalledStream(byte[] prefix) : Stream {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (!_sentPrefix) {
                _sentPrefix = true;
                prefix.CopyTo(buffer);
                return prefix.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpContent> content,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content(request) });
        }
    }
}
