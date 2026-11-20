using System.IO.Compression;

namespace Prismedia.Application.Files;

/// <summary>
/// Bounded extraction of single archive members (for example a CBZ page) so that a member whose
/// uncompressed size is huge — or whose header understates it — cannot exhaust memory or disk.
/// </summary>
public static class ArchiveEntryReader {
    /// <summary>Largest uncompressed archive member Prismedia will extract for serving or thumbnailing.</summary>
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Copies one archive member into <paramref name="destination"/>, enforcing
    /// <paramref name="maxBytes"/> against both the declared header length and the bytes actually read.
    /// </summary>
    /// <exception cref="InvalidDataException">The member exceeds <paramref name="maxBytes"/>.</exception>
    public static async Task CopyBoundedAsync(
        ZipArchiveEntry entry,
        Stream destination,
        CancellationToken cancellationToken,
        long maxBytes = MaxEntryBytes) {
        if (entry.Length > maxBytes) {
            throw new InvalidDataException("The archive member is larger than the supported limit.");
        }

        await using var source = entry.Open();
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) {
            copied += read;
            if (copied > maxBytes) {
                throw new InvalidDataException("The archive member is larger than the supported limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
