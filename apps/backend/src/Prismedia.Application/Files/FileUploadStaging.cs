using Prismedia.Domain.Entities;

namespace Prismedia.Application.Files;

/// <summary>
/// Fixed rules for resumable Files uploads. Partial uploads are staged in a hidden folder at the
/// top of their library root so they share the library's filesystem: completing an upload is a
/// rename rather than a second copy, and a partial file never appears at its final path.
/// </summary>
public static class FileUploadStaging {
    /// <summary>
    /// Hidden folder under each library root that holds partial uploads. Library scans skip
    /// dot-folders, the Files page hides them, and Files paths may not address it.
    /// </summary>
    public const string DirectoryName = ".prismedia-uploads";

    /// <summary>How long an upload may sit without receiving bytes before it is discarded.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(24);

    /// <summary>Largest chunk body accepted in one request.</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    /// <summary>Most numbered names tried when keeping both files.</summary>
    public const int MaxKeepBothAttempts = 1000;

    /// <summary>
    /// Whether an absolute path lies inside any upload staging folder. Used to keep staging
    /// writes out of filesystem change detection.
    /// </summary>
    /// <param name="absolutePath">Absolute filesystem path to test.</param>
    public static bool IsStagingPath(string absolutePath) =>
        absolutePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => FileSystemPathComparison.Equals(segment, DirectoryName));

    /// <summary>
    /// Builds the numbered name used when keeping both files, such as <c>photo (2).jpg</c>.
    /// </summary>
    /// <param name="relativePath">Root-relative requested path.</param>
    /// <param name="attempt">1-based number to place in the name.</param>
    /// <returns>Root-relative path with the numbered basename in the same folder.</returns>
    public static string NumberedPath(string relativePath, int attempt) {
        var slash = relativePath.LastIndexOf('/');
        var folder = slash >= 0 ? relativePath[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? relativePath[(slash + 1)..] : relativePath;
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem.Length == name.Length) {
            extension = string.Empty;
        }

        return $"{folder}{stem} ({attempt}){extension}";
    }
}

/// <summary>
/// Durable description of one resumable upload, written when the upload starts.
/// </summary>
/// <param name="Id">Upload session identifier.</param>
/// <param name="RootId">Library root that receives the file.</param>
/// <param name="RelativePath">Requested root-relative destination path.</param>
/// <param name="SizeBytes">Declared file size in bytes.</param>
/// <param name="ConflictPolicy">What completion does when the destination exists.</param>
/// <param name="CreatedAt">When the upload started.</param>
public sealed record FileUploadSessionRecord(
    Guid Id,
    Guid RootId,
    string RelativePath,
    long SizeBytes,
    FileUploadConflictPolicy ConflictPolicy,
    DateTimeOffset CreatedAt);

/// <summary>
/// A staged upload together with how many bytes are stored and when bytes last arrived.
/// </summary>
/// <param name="Record">Durable upload description.</param>
/// <param name="ReceivedBytes">Bytes stored in the staged file; the next chunk starts here.</param>
/// <param name="LastActivityAt">When the upload last started or received bytes.</param>
public sealed record FileUploadSessionState(
    FileUploadSessionRecord Record,
    long ReceivedBytes,
    DateTimeOffset LastActivityAt);

/// <summary>
/// Infrastructure port that stores partial uploads beside their library root and moves finished
/// ones into place. Implementations must refuse symlinked staging or destination folders and must
/// never expose a partially written file at a destination path.
/// </summary>
public interface IFileUploadStaging {
    /// <summary>Creates the staging files for a new upload.</summary>
    /// <param name="root">Writable library root that receives the file.</param>
    /// <param name="record">Upload description to persist.</param>
    Task<FileUploadSessionState> CreateAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        CancellationToken cancellationToken);

    /// <summary>Finds a staged upload under one root, or null when it does not exist there.</summary>
    Task<FileUploadSessionState?> FindAsync(FileLibraryRoot root, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one chunk. The chunk must start exactly at the stored length; bytes past the declared
    /// size are rejected. Bytes that arrived before a dropped connection stay stored, so a client
    /// resumes from <see cref="FileUploadSessionState.ReceivedBytes"/>.
    /// </summary>
    /// <param name="root">Library root that holds the upload.</param>
    /// <param name="record">Upload being extended.</param>
    /// <param name="offset">Byte offset the chunk starts at.</param>
    /// <param name="content">Chunk bytes.</param>
    /// <returns>The upload state after the chunk was stored.</returns>
    Task<FileUploadSessionState> AppendAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        long offset,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a fully received upload to its destination without overwriting anything, then removes
    /// its staging files. Throws <see cref="FileConflictException"/> when the destination exists.
    /// </summary>
    Task CommitAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        ResolvedFilePath destination,
        CancellationToken cancellationToken);

    /// <summary>Deletes a staged upload's files. Deleting a missing upload succeeds.</summary>
    Task DeleteAsync(FileLibraryRoot root, Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes staged uploads under one root whose last activity is older than the cutoff.</summary>
    /// <returns>Number of uploads removed.</returns>
    Task<int> PruneAsync(FileLibraryRoot root, DateTimeOffset idleBefore, CancellationToken cancellationToken);

    /// <summary>Whether a file or folder already occupies a destination path.</summary>
    bool DestinationExists(ResolvedFilePath destination);

    /// <summary>Free bytes on the filesystem holding the root, or null when unknown.</summary>
    long? GetAvailableFreeBytes(FileLibraryRoot root);
}
