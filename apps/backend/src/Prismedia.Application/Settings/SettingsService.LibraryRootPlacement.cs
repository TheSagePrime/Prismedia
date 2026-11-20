using Prismedia.Application.Files;
using Prismedia.Contracts.Settings;

namespace Prismedia.Application.Settings;

/// <summary>
/// Whether a non-admin library creator may place a watched root at a path.
/// </summary>
/// <param name="Allowed">False when the path overlaps a root the caller cannot access.</param>
/// <param name="RequiresNsfw">True when the path lies inside an NSFW root, so the new root must be NSFW too.</param>
public sealed record LibraryRootPlacement(bool Allowed, bool RequiresNsfw);

/// <summary>
/// Placement rules for library roots managed by non-admin library creators: their roots may not
/// overlap roots they cannot access, and roots nested in an NSFW root inherit its NSFW flag.
/// </summary>
public sealed partial class SettingsService {
    /// <summary>
    /// Returns the paths of every existing root the restricted caller cannot access: roots not
    /// granted to them, NSFW roots when their NSFW ceiling is off, and disabled roots they did
    /// not create.
    /// </summary>
    /// <param name="userId">Restricted (non-admin) caller.</param>
    /// <param name="allowedRootIds">Roots granted to the caller.</param>
    /// <param name="allowNsfw">Caller's server-enforced NSFW ceiling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> ListInaccessibleLibraryRootPathsAsync(
        Guid userId,
        IReadOnlySet<Guid> allowedRootIds,
        bool allowNsfw,
        CancellationToken cancellationToken) {
        var roots = await _persistence.ListLibraryRootsAsync(cancellationToken);
        return roots
            .Where(root => !CanRestrictedUserAccess(root, userId, allowedRootIds, allowNsfw))
            .Select(root => root.Path)
            .ToArray();
    }

    /// <summary>
    /// Evaluates a proposed root path for a restricted caller. The path is rejected when it is
    /// the same as, an ancestor of, or a descendant of any root the caller cannot access, and it
    /// must be NSFW when it lies inside an accessible NSFW root.
    /// </summary>
    /// <param name="path">Proposed root path.</param>
    /// <param name="userId">Restricted (non-admin) caller.</param>
    /// <param name="allowedRootIds">Roots granted to the caller.</param>
    /// <param name="allowNsfw">Caller's server-enforced NSFW ceiling.</param>
    /// <param name="excludeRootId">Root being updated, which never conflicts with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LibraryRootPlacement> EvaluateRestrictedLibraryRootPlacementAsync(
        string path,
        Guid userId,
        IReadOnlySet<Guid> allowedRootIds,
        bool allowNsfw,
        Guid? excludeRootId,
        CancellationToken cancellationToken) {
        if (!TryNormalizeRootPath(path, out var candidate)) {
            return new LibraryRootPlacement(false, false);
        }

        var requiresNsfw = false;
        var roots = await _persistence.ListLibraryRootsAsync(cancellationToken);
        foreach (var root in roots) {
            if (root.Id == excludeRootId || !TryNormalizeRootPath(root.Path, out var existing)) {
                continue;
            }

            var candidateInside = FileSystemPathComparison.IsSameOrDescendant(existing, candidate);
            var candidateContains = FileSystemPathComparison.IsSameOrDescendant(candidate, existing);
            if (!candidateInside && !candidateContains) {
                continue;
            }

            if (!CanRestrictedUserAccess(root, userId, allowedRootIds, allowNsfw)) {
                return new LibraryRootPlacement(false, false);
            }

            requiresNsfw |= candidateInside && root.IsNsfw;
        }

        return new LibraryRootPlacement(true, requiresNsfw);
    }

    /// <summary>True when <paramref name="path"/> is at or below any of <paramref name="hiddenRootPaths"/>.</summary>
    internal static bool IsWithinAnyRoot(string path, IReadOnlyCollection<string> hiddenRootPaths) =>
        TryNormalizeRootPath(path, out var normalized) &&
        hiddenRootPaths.Any(hidden =>
            TryNormalizeRootPath(hidden, out var root) &&
            FileSystemPathComparison.IsSameOrDescendant(root, normalized));

    private static bool CanRestrictedUserAccess(
        LibraryRoot root,
        Guid userId,
        IReadOnlySet<Guid> allowedRootIds,
        bool allowNsfw) =>
        allowedRootIds.Contains(root.Id) &&
        (allowNsfw || !root.IsNsfw) &&
        (root.Enabled || root.CreatedByUserId == userId);

    private static bool TryNormalizeRootPath(string? path, out string normalized) {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) {
            return false;
        }

        try {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return true;
        } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }
    }
}
