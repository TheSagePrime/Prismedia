using System.Buffers.Binary;
using System.IO.Compression;
using Prismedia.Application.Files;

namespace Prismedia.Application.Tests;

public sealed class ArchiveEntryReaderTests {
    [Fact]
    public async Task CopiesMembersWithinTheLimit() {
        using var archive = OpenArchive(CreateZip(new byte[100]));
        using var output = new MemoryStream();

        await ArchiveEntryReader.CopyBoundedAsync(archive.Entries.Single(), output, CancellationToken.None, maxBytes: 100);

        Assert.Equal(100, output.Length);
    }

    [Fact]
    public async Task RejectsMembersWhoseDeclaredSizeExceedsTheLimit() {
        using var archive = OpenArchive(CreateZip(new byte[101]));
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveEntryReader.CopyBoundedAsync(archive.Entries.Single(), output, CancellationToken.None, maxBytes: 100));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task NeverWritesMoreThanTheLimitWhenTheHeaderUnderstatesTheSize() {
        var zip = CreateZip(new byte[64 * 1024]);
        UnderstateUncompressedSizes(zip, declared: 10);
        using var archive = OpenArchive(zip);
        var entry = archive.Entries.Single();
        Assert.Equal(10, entry.Length);
        using var output = new MemoryStream();

        try {
            await ArchiveEntryReader.CopyBoundedAsync(entry, output, CancellationToken.None, maxBytes: 1024);
        } catch (InvalidDataException) {
            // Either the runtime stops at the declared length or the reader's byte count trips.
        }

        Assert.True(output.Length <= 1024);
    }

    private static byte[] CreateZip(byte[] content) {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            using var stream = archive.CreateEntry("page.jpg", CompressionLevel.Optimal).Open();
            stream.Write(content);
        }

        return buffer.ToArray();
    }

    private static ZipArchive OpenArchive(byte[] zip) =>
        new(new MemoryStream(zip), ZipArchiveMode.Read, leaveOpen: false);

    /// <summary>Rewrites the uncompressed-size fields in the local and central headers.</summary>
    private static void UnderstateUncompressedSizes(byte[] zip, uint declared) {
        for (var offset = 0; offset + 4 <= zip.Length; offset++) {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset));
            if (signature == 0x04034b50) {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(offset + 22), declared);
            } else if (signature == 0x02014b50) {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(offset + 24), declared);
            }
        }
    }
}
