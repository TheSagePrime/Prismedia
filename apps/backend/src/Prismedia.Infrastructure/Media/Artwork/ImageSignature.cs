using System.Buffers.Binary;
using Prismedia.Contracts.Media;

namespace Prismedia.Infrastructure.Media.Artwork;

/// <summary>A raster image format recognized from its leading bytes.</summary>
/// <param name="ContentType">Canonical MIME type for the format.</param>
/// <param name="Extension">File extension (with leading dot) used when caching the image.</param>
public sealed record SniffedImageFormat(string ContentType, string Extension);

/// <summary>
/// Identifies downloaded artwork by magic bytes so cached files are stored with an extension (and thus a
/// served content type) that matches their real content rather than whatever the source URL claimed.
/// Only raster formats the app renders are recognized; text formats such as SVG or HTML never match.
/// </summary>
public static class ImageSignature {
    /// <summary>JPEG.</summary>
    public static readonly SniffedImageFormat Jpeg = new(MediaContentTypes.ImageJpeg, ".jpg");

    /// <summary>PNG (including animated PNG).</summary>
    public static readonly SniffedImageFormat Png = new(MediaContentTypes.ImagePng, ".png");

    /// <summary>GIF.</summary>
    public static readonly SniffedImageFormat Gif = new(MediaContentTypes.ImageGif, ".gif");

    /// <summary>WebP.</summary>
    public static readonly SniffedImageFormat Webp = new(MediaContentTypes.ImageWebp, ".webp");

    /// <summary>AVIF.</summary>
    public static readonly SniffedImageFormat Avif = new(MediaContentTypes.ImageAvif, ".avif");

    /// <summary>Windows bitmap.</summary>
    public static readonly SniffedImageFormat Bmp = new(MediaContentTypes.ImageBmp, ".bmp");

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly int[] BmpHeaderSizes = [12, 40, 52, 56, 64, 108, 124];

    /// <summary>Detects the image format of <paramref name="data"/>.</summary>
    /// <param name="data">The complete image bytes, or at least their first few hundred bytes.</param>
    /// <returns>The recognized format, or null when the content is not a supported raster image.</returns>
    public static SniffedImageFormat? Detect(ReadOnlySpan<byte> data) {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) {
            return Jpeg;
        }
        if (data.StartsWith(PngMagic)) {
            return Png;
        }
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) {
            return Gif;
        }
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) {
            return Webp;
        }
        if (IsAvif(data)) {
            return Avif;
        }
        if (IsBmp(data)) {
            return Bmp;
        }
        return null;
    }

    /// <summary>ISO-BMFF <c>ftyp</c> box whose major or compatible brands include <c>avif</c>/<c>avis</c>.</summary>
    private static bool IsAvif(ReadOnlySpan<byte> data) {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8)) {
            return false;
        }

        var boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), (uint)data.Length);
        if (boxSize < 16) {
            return false;
        }

        // Major brand at 8, minor version at 12, compatible brands from 16 to the end of the box.
        for (var offset = 8; offset + 4 <= boxSize; offset += offset == 8 ? 8 : 4) {
            var brand = data.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>"BM" header with zeroed reserved fields and a known DIB header size.</summary>
    private static bool IsBmp(ReadOnlySpan<byte> data) {
        if (data.Length < 18 || data[0] != (byte)'B' || data[1] != (byte)'M') {
            return false;
        }

        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        var dibHeaderSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        return reserved == 0 && BmpHeaderSizes.Contains(dibHeaderSize);
    }
}
