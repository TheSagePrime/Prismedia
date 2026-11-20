using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Prismedia.Application.Files;
using Prismedia.Contracts.Entities;
using Prismedia.Contracts.Media;
using Prismedia.Contracts.Plugins;
using Prismedia.Domain.Entities;
using Prismedia.Infrastructure.Media.Artwork;
using Prismedia.Infrastructure.Media.Processing;
using Prismedia.Infrastructure.Persistence;
using Prismedia.Infrastructure.Persistence.Entities;

namespace Prismedia.Infrastructure.Plugins;

/// <summary>
/// Downloads plugin artwork into the Prismedia cache and updates entity file rows.
/// </summary>
public sealed class PluginArtworkDownloader {
    private const int MaxConcurrentArtworkDownloads = 8;
    private readonly PrismediaDbContext _db;
    private readonly PluginArtworkServiceOptions _options;
    private readonly RemoteArtworkFetcher _fetcher;
    private readonly HashSet<Guid> _artworkEntityIds = [];
    private readonly Dictionary<string, RemoteArtworkImage?> _stagedDownloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _newArtworkPaths = new(FileSystemPathComparison.Comparer);

    public PluginArtworkDownloader(
        PrismediaDbContext db,
        PluginArtworkServiceOptions options,
        HttpClient? http = null) {
        _db = db;
        _options = options;
        _fetcher = new RemoteArtworkFetcher(http);
    }

    /// <summary>
    /// Fetches remote artwork before an Entity lifecycle lease is acquired. Apply then consumes these
    /// in-memory bytes while holding only the short database/filesystem publication boundary.
    /// </summary>
    public async Task StageAsync(
        IEnumerable<string?> urls,
        CancellationToken cancellationToken) {
        _stagedDownloads.Clear();
        var distinctUrls = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var downloads = new RemoteArtworkImage?[distinctUrls.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinctUrls.Length),
            new ParallelOptions {
                MaxDegreeOfParallelism = MaxConcurrentArtworkDownloads,
                CancellationToken = cancellationToken
            },
            async (index, token) => downloads[index] = await TryDownloadRemoteAsync(distinctUrls[index], token));

        for (var index = 0; index < distinctUrls.Length; index++) {
            _stagedDownloads[distinctUrls[index]] = downloads[index];
        }
    }

    /// <summary>Marks staged artwork publication committed and releases its temporary bookkeeping.</summary>
    public void CommitStagedWrites() {
        _stagedDownloads.Clear();
        _newArtworkPaths.Clear();
    }

    /// <summary>
    /// Removes only files created by an apply whose database transaction failed. Pre-existing deterministic
    /// cache paths are never deleted, so rollback cannot break artwork already referenced by another apply.
    /// </summary>
    public void RollbackStagedWrites() {
        foreach (var path in _newArtworkPaths) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            } catch (IOException) {
                // Best effort: an unreferenced deterministic cache file is safe for later cleanup/reuse.
            } catch (UnauthorizedAccessException) {
                // Best effort for read-only cache mounts; the database transaction still remained atomic.
            }
        }
        _newArtworkPaths.Clear();
        _stagedDownloads.Clear();
        _artworkEntityIds.Clear();
    }

    /// <summary>
    /// Downloads explicitly selected role-to-URL artwork choices for an entity.
    /// </summary>
    public async Task DownloadSelectedImagesAsync(
        Guid entityId,
        IReadOnlyDictionary<string, string?> selectedImages,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        foreach (var (roleCode, url) in selectedImages) {
            if (string.IsNullOrWhiteSpace(url) || !roleCode.TryDecodeAs<EntityFileRole>(out var role)) {
                continue;
            }

            // A single unreachable or blocked image (e.g. a CDN 403) must not fail the whole apply.
            var image = await TryDownloadAsync(url, cancellationToken);
            if (image is null) {
                continue;
            }

            await UpsertArtworkAsync(entityId, role, url, image, now, cancellationToken);
        }
    }

    /// <summary>
    /// Downloads one plugin image candidate, swallowing unreachable remote artwork failures.
    /// </summary>
    public async Task DownloadPluginImageAsync(
        EntityRow entity,
        ImageCandidate image,
        EntityFileRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        var downloaded = await TryDownloadAsync(image.Url, cancellationToken);
        if (downloaded is null) {
            return;
        }

        await UpsertArtworkAsync(entity.Id, role, image.Url, downloaded, now, cancellationToken);
        entity.UpdatedAt = now;
    }

    /// <summary>
    /// Returns the entities that received artwork since the last drain and clears the set.
    /// The apply service reads this after saving so grid-thumbnail variants can be
    /// regenerated from the newly committed covers.
    /// </summary>
    public IReadOnlyCollection<Guid> DrainArtworkEntityIds() {
        var ids = _artworkEntityIds.ToArray();
        _artworkEntityIds.Clear();
        return ids;
    }

    /// <summary>
    /// Fetches remote artwork, returning null when the image is not an allowed remote URL, unreachable,
    /// blocked, too large, not a supported image, or times out so callers can skip it without aborting the
    /// surrounding metadata apply.
    /// </summary>
    private async Task<RemoteArtworkImage?> TryDownloadAsync(string url, CancellationToken cancellationToken) {
        if (_stagedDownloads.TryGetValue(url, out var staged)) {
            return staged;
        }

        return await TryDownloadRemoteAsync(url, cancellationToken);
    }

    private Task<RemoteArtworkImage?> TryDownloadRemoteAsync(string url, CancellationToken cancellationToken) =>
        _fetcher.TryFetchAsync(url, cancellationToken);

    private async Task UpsertArtworkAsync(
        Guid entityId,
        EntityFileRole role,
        string url,
        RemoteArtworkImage image,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        // The extension (and therefore the served content type) comes from the sniffed content, never the URL.
        var ext = image.Format.Extension;
        var relativePath = Path.Combine(
            "plugins",
            "artwork",
            entityId.ToString(),
            $"{role.ToCode()}-{ShortHash(url)}{ext}");
        var physicalPath = Path.Combine(_options.CacheRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        var createdByThisApply = !File.Exists(physicalPath);
        await File.WriteAllBytesAsync(physicalPath, image.Bytes, cancellationToken);
        if (createdByThisApply) {
            _newArtworkPaths.Add(physicalPath);
        }

        var publicPath = $"{AssetPaths.AssetsUrlPrefix}{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";
        _artworkEntityIds.Add(entityId);
        var existing = await FindEntityFileAsync(entityId, role, cancellationToken);
        if (existing is null) {
            _db.EntityFiles.Add(new EntityFileRow {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                Role = role,
                Path = publicPath,
                MimeType = image.Format.ContentType,
                Source = FileSourceKind.Custom.ToCode(),
                CreatedAt = now,
                UpdatedAt = now
            });
        } else {
            existing.Path = publicPath;
            existing.MimeType = image.Format.ContentType;
            existing.Source = FileSourceKind.Custom.ToCode();
            existing.UpdatedAt = now;
        }
    }

    private async Task<EntityFileRow?> FindEntityFileAsync(Guid entityId, EntityFileRole role, CancellationToken cancellationToken) =>
        _db.EntityFiles.Local.FirstOrDefault(row => row.EntityId == entityId && row.Role == role)
        ?? await _db.EntityFiles.FirstOrDefaultAsync(row => row.EntityId == entityId && row.Role == role, cancellationToken);

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
