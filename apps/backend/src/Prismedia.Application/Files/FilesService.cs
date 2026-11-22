using Prismedia.Application.Jobs;
using Prismedia.Application.Jobs.Ports;
using Prismedia.Contracts.Files;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Application.Files;

/// <summary>
/// Application service for watched-root filesystem management. It validates root-relative
/// paths, coordinates disk operations through <see cref="IManagedFileStorage"/>, rewrites
/// known catalog source paths after moves, and queues scans for affected roots.
/// </summary>
public sealed partial class FilesService(
    IFilesPersistence persistence,
    IManagedFileStorage storage,
    IFileArchivePreparationService archives,
    IJobQueueService jobs,
    EntitySourcePathMutationCoordinator sourcePathMutations,
    IFileUploadStaging uploads,
    ILibraryFileChangeIntake? fileChanges = null) {
    /// <summary>Lists watched roots available to the Files page.</summary>
    public async Task<FileRootsResponse> ListRootsAsync(bool hideNsfw, CancellationToken cancellationToken) {
        var roots = await persistence.ListRootsAsync(cancellationToken);
        return new FileRootsResponse(roots
            .Where(root => !hideNsfw || !root.IsNsfw)
            .OrderBy(root => root.Label, StringComparer.OrdinalIgnoreCase)
            .Select(root => new FileRoot(root.Id, root.Label, root.Path, root.Enabled, root.IsReadOnly))
            .ToArray());
    }

    /// <summary>Lists direct children under a root-relative directory.</summary>
    public async Task<FileChildrenResponse> ListChildrenAsync(
        FileChildrenRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var directory = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        await EnsureVisiblePathAsync(directory, hideNsfw, cancellationToken);
        var entries = await storage.ListChildrenAsync(directory, cancellationToken);
        if (hideNsfw) {
            entries = await FilterVisibleEntriesAsync(directory, entries, cancellationToken);
        }

        entries = await ApplyExcludedFlagsAsync(directory.Root.Id, entries, cancellationToken);
        entries = await ApplyThumbnailsAsync(directory, entries, hideNsfw, cancellationToken);
        return new FileChildrenResponse(request.RootId, directory.RelativePath, entries);
    }

    /// <summary>Gets metadata for one watched-root path.</summary>
    public async Task<FileDetail> GetDetailAsync(
        FileDetailRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var path = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        await EnsureVisiblePathAsync(path, hideNsfw, cancellationToken);
        var excluded = await IsExcludedAsync(path.Root.Id, path.RelativePath, cancellationToken);
        var linked = excluded
            ? []
            : await persistence.ListLinkedEntitiesAsync(path.AbsolutePath, hideNsfw, cancellationToken);
        var detail = await storage.GetDetailAsync(path, linked, cancellationToken);
        return excluded
            ? detail with {
                Entry = detail.Entry with { Excluded = true },
                LinkedEntities = [],
                CanPreview = false
            }
            : detail with { Entry = detail.Entry with { Excluded = false } };
    }

    /// <summary>Gets content metadata for one previewable file.</summary>
    public async Task<FileContentInfo> GetContentInfoAsync(
        FileDetailRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var path = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        await EnsureVisiblePathAsync(path, hideNsfw, cancellationToken);
        return await storage.GetContentInfoAsync(path, cancellationToken);
    }

    /// <summary>
    /// Collects the directory's visible entries and starts preparing an ephemeral ZIP archive.
    /// Compression continues outside the request scope and is observed through the returned id.
    /// </summary>
    public async Task<FileArchivePreparation> PrepareArchiveAsync(
        FileArchiveRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var directory = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        await EnsureVisiblePathAsync(directory, hideNsfw, cancellationToken);
        var detail = await storage.GetDetailAsync(directory, [], cancellationToken);
        if (detail.Entry.Kind != FileEntryKind.Directory) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Only folders can be prepared as ZIP archives.");
        }

        var entries = new List<FileArchiveEntry>();
        await CollectArchiveEntriesAsync(directory, directory, hideNsfw, entries, cancellationToken);
        var folderName = string.IsNullOrWhiteSpace(directory.RelativePath)
            ? directory.Root.Label
            : Path.GetFileName(directory.RelativePath);
        return archives.Start(new FileArchivePlan($"{SafeDownloadName(folderName)}.zip", entries));
    }

    /// <summary>Creates a folder and queues scans for the affected root.</summary>
    public async Task<FileOperationResponse> CreateFolderAsync(
        FileCreateFolderRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var name = CleanSegment(request.Name);
        var relativePath = CombineRelative(NormalizeRelativePath(request.ParentPath), name);
        var target = await ResolveAsync(request.RootId, relativePath, hideNsfw, cancellationToken);
        EnsureWritable(target.Root);
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        await storage.CreateDirectoryAsync(target, cancellationToken);
        return new FileOperationResponse(await QueueChangedScansAsync(
            target.Root, [target.AbsolutePath], cancellationToken));
    }

    /// <summary>Renames a file or folder within its current parent directory.</summary>
    public async Task<FileOperationResponse> RenameAsync(
        FileRenameRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var source = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        EnsureWritable(source.Root);
        EnsureNotRoot(source, "Library roots cannot be renamed from Files.");
        await EnsureVisiblePathAsync(source, hideNsfw, cancellationToken);
        var parent = Path.GetDirectoryName(source.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        var target = await ResolveAsync(
            request.RootId,
            CombineRelative(parent, CleanSegment(request.Name)),
            hideNsfw,
            cancellationToken);
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        await MoveResolvedAsync(source, target, cancellationToken);
        return new FileOperationResponse(await QueueChangedScansAsync(
            source.Root, [source.AbsolutePath, target.AbsolutePath], cancellationToken));
    }

    /// <summary>Moves a file or folder between watched-root locations.</summary>
    public async Task<FileOperationResponse> MoveAsync(
        FileMoveRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var source = await ResolveAsync(request.SourceRootId, request.SourcePath, hideNsfw, cancellationToken);
        EnsureWritable(source.Root);
        EnsureNotRoot(source, "Library roots cannot be moved from Files.");
        await EnsureVisiblePathAsync(source, hideNsfw, cancellationToken);
        var target = await ResolveAsync(request.TargetRootId, request.TargetPath, hideNsfw, cancellationToken);
        EnsureWritable(target.Root);
        EnsureNotRoot(target, "A library root cannot be replaced by a move.");
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        await MoveResolvedAsync(source, target, cancellationToken);
        if (source.Root.Id == target.Root.Id) {
            return new FileOperationResponse(await QueueChangedScansAsync(
                source.Root, [source.AbsolutePath, target.AbsolutePath], cancellationToken));
        }
        var sourceQueued = await QueueChangedScansAsync(
            source.Root, [source.AbsolutePath], cancellationToken);
        var targetQueued = await QueueChangedScansAsync(
            target.Root, [target.AbsolutePath], cancellationToken);
        return new FileOperationResponse(sourceQueued + targetQueued);
    }

    /// <summary>Permanently deletes one watched-root file or directory and queues scans.</summary>
    public async Task<FileOperationResponse> DeleteAsync(
        FileDeleteRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(request.Path)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Deleting a library root from Files is not supported.");
        }

        var target = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        EnsureWritable(target.Root);
        EnsureNotRoot(target, "Deleting a library root from Files is not supported.");
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        var executed = await sourcePathMutations.ExecuteAsync(
            target.AbsolutePath,
            token => storage.DeleteAsync(target, token),
            cancellationToken);
        if (!executed) {
            throw SourceLifecycleConflict(target.RelativePath);
        }
        return new FileOperationResponse(await QueueChangedScansAsync(
            target.Root, [target.AbsolutePath], cancellationToken));
    }

    /// <summary>Marks a watched-root file or directory as excluded from future scans.</summary>
    public async Task<FileOperationResponse> ExcludeAsync(
        FileExclusionRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(request.Path)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Library roots cannot be excluded from Files.");
        }

        var target = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        EnsureNotRoot(target, "Library roots cannot be excluded from Files.");
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        var detail = await storage.GetDetailAsync(target, [], cancellationToken);
        if (target.Root.IsReadOnly)
            throw new FileOperationException(ApiProblemCodes.ReadOnlyLibrary, "Manage external library inclusion in its connected application. Prismedia retains these items and their history.");
        await persistence.UpsertExclusionAsync(
            target.Root.Id,
            target.RelativePath,
            detail.Entry.Kind,
            cancellationToken);
        return new FileOperationResponse(await QueueChangedScansAsync(
            target.Root, [target.AbsolutePath], cancellationToken));
    }

    /// <summary>Removes a watched-root file or directory scan exclusion.</summary>
    public async Task<FileOperationResponse> RemoveExclusionAsync(
        FileExclusionRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(request.Path)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Library roots cannot have exclusions removed from Files.");
        }

        var target = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        await EnsureVisiblePathAsync(target, hideNsfw, cancellationToken);
        await persistence.RemoveExclusionAsync(target.Root.Id, target.RelativePath, cancellationToken);
        return new FileOperationResponse(await QueueChangedScansAsync(
            target.Root, [target.AbsolutePath], cancellationToken));
    }

    /// <summary>Queues scans for one watched root.</summary>
    public async Task<FileOperationResponse> RescanAsync(
        FileRescanRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var root = await GetRootAsync(request.RootId, hideNsfw, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Path)) {
            return new FileOperationResponse(await QueueScansAsync([root], cancellationToken, forceReconcile: true));
        }
        var target = await ResolveAsync(request.RootId, request.Path, hideNsfw, cancellationToken);
        return new FileOperationResponse(await QueueChangedScansAsync(
            root, [target.AbsolutePath], cancellationToken));
    }

    private async Task MoveResolvedAsync(
        ResolvedFilePath source,
        ResolvedFilePath target,
        CancellationToken cancellationToken) {
        var executed = await sourcePathMutations.ExecuteAsync(
            source.AbsolutePath,
            async token => {
                await storage.MoveAsync(source, target, token);
                await persistence.ApplyPathPrefixRewriteAsync(
                    source.AbsolutePath,
                    target.AbsolutePath,
                    token);
            },
            cancellationToken);
        if (!executed) {
            throw SourceLifecycleConflict(source.RelativePath);
        }
    }

    private async Task CollectArchiveEntriesAsync(
        ResolvedFilePath archiveRoot,
        ResolvedFilePath directory,
        bool hideNsfw,
        List<FileArchiveEntry> collected,
        CancellationToken cancellationToken) {
        var children = await storage.ListChildrenAsync(directory, cancellationToken);
        if (hideNsfw) {
            children = await FilterVisibleEntriesAsync(directory, children, cancellationToken);
        }

        foreach (var entry in children) {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = AbsolutePathForEntry(directory.Root.Path, entry.Path);
            var resolved = new ResolvedFilePath(directory.Root, entry.Path, absolutePath);
            if (!storage.ResolvesInsideRoot(resolved)) {
                // Links that lead outside the library root are never packed into archives.
                continue;
            }

            var archivePath = Path.GetRelativePath(archiveRoot.AbsolutePath, absolutePath).Replace('\\', '/');
            var isDirectory = entry.Kind == FileEntryKind.Directory;
            collected.Add(new FileArchiveEntry(absolutePath, archivePath, isDirectory, entry.SizeBytes ?? 0));
            if (isDirectory) {
                await CollectArchiveEntriesAsync(
                    archiveRoot,
                    resolved,
                    hideNsfw,
                    collected,
                    cancellationToken);
            }
        }
    }

    private static string SafeDownloadName(string value) {
        var cleaned = string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "files" : cleaned;
    }

    /// <summary>
    /// Rejects filesystem writes against an externally managed (read-only) library root before any
    /// disk effect. The infrastructure mutation guard enforces the same boundary at write time.
    /// </summary>
    private static void EnsureWritable(FileLibraryRoot root) {
        if (root.IsReadOnly) {
            throw new FileOperationException(
                ApiProblemCodes.ReadOnlyLibrary,
                "This library is externally managed. Prismedia keeps its files read-only.");
        }
    }

    /// <summary>
    /// Rejects operations whose normalized target is the library root itself (for example a path of
    /// "/", "." or "a/.."), which must never be deleted, moved, renamed, or replaced from Files.
    /// </summary>
    private static void EnsureNotRoot(ResolvedFilePath path, string message) {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Root.Path));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.AbsolutePath));
        if (FileSystemPathComparison.Equals(root, target)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, message);
        }
    }

    private static FileConflictException SourceLifecycleConflict(string relativePath) =>
        new($"The Entity linked to '{relativePath}' is currently being changed. Try again when that operation finishes.");

    private async Task<ResolvedFilePath> ResolveAsync(
        Guid rootId,
        string? relativePath,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var root = await GetRootAsync(rootId, hideNsfw, cancellationToken);
        var normalizedRoot = Path.GetFullPath(root.Path);
        var normalizedRelative = NormalizeRelativePath(relativePath);
        if (Path.IsPathRooted(normalizedRelative)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Files paths must be relative to a library root.");
        }

        var absolute = string.IsNullOrWhiteSpace(normalizedRelative)
            ? normalizedRoot
            : Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelative));
        var trimmedRoot = Path.TrimEndingDirectorySeparator(normalizedRoot);
        var trimmedAbsolute = Path.TrimEndingDirectorySeparator(absolute);
        var inside = FileSystemPathComparison.IsSameOrDescendant(trimmedRoot, trimmedAbsolute);
        if (!inside) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Files paths cannot escape the selected library root.");
        }

        if (IsInsideUploadStaging(trimmedRoot, trimmedAbsolute)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Files paths cannot address in-progress uploads.");
        }

        return new ResolvedFilePath(root, ToRelativePath(normalizedRelative), absolute);
    }

    /// <summary>Whether a path inside a root is the root's upload staging folder or lies under it.</summary>
    private static bool IsInsideUploadStaging(string trimmedRoot, string trimmedAbsolute) {
        var relative = Path.GetRelativePath(trimmedRoot, trimmedAbsolute).Replace('\\', '/');
        var firstSegment = relative.Split('/', 2)[0];
        return FileSystemPathComparison.Equals(firstSegment, FileUploadStaging.DirectoryName);
    }

    private async Task<FileLibraryRoot> GetRootAsync(Guid rootId, bool hideNsfw, CancellationToken cancellationToken) {
        var root = await persistence.GetRootAsync(rootId, cancellationToken);
        if (root is null || (hideNsfw && root.IsNsfw)) {
            throw new FileOperationException(ApiProblemCodes.RootNotFound, "Library root was not found.");
        }

        return root;
    }

    private async Task<IReadOnlyList<FileEntry>> FilterVisibleEntriesAsync(
        ResolvedFilePath directory,
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken) {
        if (entries.Count == 0) {
            return entries;
        }

        var absolutePaths = entries
            .Select(entry => AbsolutePathForEntry(directory.Root.Path, entry.Path))
            .ToArray();
        var hidden = await persistence.ListHiddenPathsAsync(directory.AbsolutePath, absolutePaths, cancellationToken);
        if (hidden.Count == 0) {
            return entries;
        }

        return entries
            .Where(entry => !hidden.Contains(AbsolutePathForEntry(directory.Root.Path, entry.Path)))
            .ToArray();
    }

    private async Task<IReadOnlyList<FileEntry>> ApplyExcludedFlagsAsync(
        Guid rootId,
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken) {
        if (entries.Count == 0) {
            return entries;
        }

        var relativePaths = entries.Select(entry => entry.Path).ToArray();
        var excluded = await persistence.ListExcludedRelativePathsAsync(rootId, relativePaths, cancellationToken);
        if (excluded.Count == 0) {
            return entries.Select(entry => entry with { Excluded = false }).ToArray();
        }

        return entries
            .Select(entry => entry with { Excluded = excluded.Contains(entry.Path) })
            .ToArray();
    }

    /// <summary>Adds preview thumbnails to listed files and folders so the folder view can show them.</summary>
    private async Task<IReadOnlyList<FileEntry>> ApplyThumbnailsAsync(
        ResolvedFilePath directory,
        IReadOnlyList<FileEntry> entries,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var candidates = entries.Where(entry => !entry.Excluded).ToArray();
        if (candidates.Length == 0) {
            return entries;
        }

        var thumbnails = await persistence.ListThumbnailsAsync(
            directory.AbsolutePath,
            candidates.Select(entry => AbsolutePathForEntry(directory.Root.Path, entry.Path)).ToArray(),
            hideNsfw,
            cancellationToken);
        if (thumbnails.Count == 0) {
            return entries;
        }

        return entries
            .Select(entry => entry.Excluded
                ? entry
                : entry with {
                    ThumbnailUrl = thumbnails.GetValueOrDefault(
                        Path.TrimEndingDirectorySeparator(AbsolutePathForEntry(directory.Root.Path, entry.Path)))
                })
            .ToArray();
    }

    private async Task<bool> IsExcludedAsync(
        Guid rootId,
        string relativePath,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(relativePath)) {
            return false;
        }

        var excluded = await persistence.ListExcludedRelativePathsAsync(rootId, [relativePath], cancellationToken);
        return excluded.Contains(relativePath);
    }

    private async Task EnsureVisiblePathAsync(
        ResolvedFilePath path,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        if (!hideNsfw) {
            return;
        }

        var hidden = await persistence.ListHiddenPathsAsync(path.AbsolutePath, [path.AbsolutePath], cancellationToken);
        if (hidden.Contains(path.AbsolutePath)) {
            throw new FileOperationException(ApiProblemCodes.NotFound, "File or folder was not found.");
        }
    }

    private static string AbsolutePathForEntry(string rootPath, string entryPath) =>
        Path.GetFullPath(Path.Combine(rootPath, entryPath));

    private async Task<int> QueueScansAsync(
        IEnumerable<FileLibraryRoot> roots,
        CancellationToken cancellationToken,
        bool forceReconcile = false) {
        var uniqueRoots = roots
            .GroupBy(root => root.Id)
            .Select(group => group.First())
            .Where(root => root.Enabled)
            .ToArray();
        if (uniqueRoots.Length == 0) {
            return 0;
        }

        var queued = 0;
        foreach (var root in uniqueRoots) {
            var selection = new LibraryScanSelection(
                Videos: root.ScanVideos,
                Images: root.ScanImages,
                Audio: root.ScanAudio,
                Books: root.ScanBooks,
                Comics: root.ScanBooks);
            queued += await (forceReconcile
                ? LibraryScanJobs.QueueReconcileScansForRootAsync(
                    jobs, root.Id, root.Label, selection, cancellationToken)
                : LibraryScanJobs.QueueScansForRootAsync(
                    jobs, root.Id, root.Label, selection, cancellationToken));
        }

        return queued;
    }

    private Task<int> QueueChangedScansAsync(
        FileLibraryRoot root,
        IReadOnlyCollection<string> absolutePaths,
        CancellationToken cancellationToken) =>
        fileChanges is null
            ? QueueScansAsync([root], cancellationToken)
            : LibraryScanJobs.QueueChangedPathsForRootAsync(
                jobs,
                fileChanges,
                root.Id,
                root.Label,
                new LibraryScanSelection(
                    Videos: root.ScanVideos,
                    Images: root.ScanImages,
                    Audio: root.ScanAudio,
                    Books: root.ScanBooks,
                    Comics: root.ScanBooks),
                absolutePaths,
                cancellationToken);

    private static string CleanSegment(string name) {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\')) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "File or folder names must be valid path segments.");
        }

        return trimmed;
    }

    private static string CombineRelative(string parent, string child) =>
        ToRelativePath(string.IsNullOrWhiteSpace(parent)
            ? child
            : Path.Combine(parent, child));

    private static string NormalizeRelativePath(string? path) =>
        ToRelativePath(path?.Trim() ?? string.Empty);

    private static string ToRelativePath(string path) =>
        path.Replace('\\', '/').Trim('/');
}
