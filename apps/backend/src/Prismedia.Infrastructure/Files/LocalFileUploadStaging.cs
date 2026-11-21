using System.Collections.Concurrent;
using System.Text.Json;
using Prismedia.Application.Files;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;
using Prismedia.Infrastructure.Acquisition;

namespace Prismedia.Infrastructure.Files;

/// <summary>
/// Local-disk staging for resumable Files uploads. Each upload is a <c>{id}.part</c> data file and a
/// <c>{id}.json</c> description inside <see cref="FileUploadStaging.DirectoryName"/> at the top of its
/// library root. The data file's length is the received byte count, so progress survives restarts
/// and interrupted chunks keep whatever arrived. Completion moves the data file with a no-overwrite
/// rename on the same filesystem; when the destination folder is on another filesystem the bytes are
/// first copied to a hidden name in that folder and then renamed, so a partial file never appears at
/// the destination path.
/// </summary>
public sealed class LocalFileUploadStaging(ILibraryFileMutationGuard mutations) : IFileUploadStaging {
    private const string DataExtension = ".part";
    private const string RecordExtension = ".json";
    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> sessionLocks = new();

    /// <inheritdoc />
    public async Task<FileUploadSessionState> CreateAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        CancellationToken cancellationToken) {
        var directory = StagingDirectory(root);
        await using var protection = await mutations.EnterAsync([directory], cancellationToken);
        EnsureSafeStagingDirectory(root, create: true);
        var data = DataPath(root, record.Id);
        try {
            await using (new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            }
        } catch (DirectoryNotFoundException) {
            // Cleanup removed the empty staging folder between the check and the write; recreate it once.
            EnsureSafeStagingDirectory(root, create: true);
            await using (new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            }
        }

        await File.WriteAllTextAsync(
            RecordPath(root, record.Id),
            JsonSerializer.Serialize(StoredRecord.From(record), RecordJson),
            cancellationToken);
        return new FileUploadSessionState(record, 0, record.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<FileUploadSessionState?> FindAsync(
        FileLibraryRoot root,
        Guid id,
        CancellationToken cancellationToken) {
        var recordPath = RecordPath(root, id);
        var dataPath = DataPath(root, id);
        if (!File.Exists(recordPath) || !File.Exists(dataPath) || !IsSafeStagingDirectory(root)) {
            return null;
        }

        StoredRecord? stored;
        try {
            stored = JsonSerializer.Deserialize<StoredRecord>(
                await File.ReadAllTextAsync(recordPath, cancellationToken),
                RecordJson);
        } catch (Exception error) when (error is JsonException or IOException) {
            return null;
        }

        if (stored is null || stored.Id != id || stored.RootId != root.Id) {
            return null;
        }

        return ReadState(stored.ToRecord(), dataPath, recordPath);
    }

    /// <inheritdoc />
    public async Task<FileUploadSessionState> AppendAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        long offset,
        Stream content,
        CancellationToken cancellationToken) {
        var gate = sessionLocks.GetOrAdd(record.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken)) {
            throw new FileOperationException(
                ApiProblemCodes.UploadOffsetMismatch,
                "Another chunk of this upload is still being stored.");
        }

        try {
            var dataPath = DataPath(root, record.Id);
            await using var protection = await mutations.EnterAsync([dataPath], cancellationToken);
            EnsureSafeStagingDirectory(root, create: false);
            if (!File.Exists(dataPath)) {
                throw new FileOperationException(ApiProblemCodes.NotFound, "The upload was not found.");
            }

            await using (var output = new FileStream(dataPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
                if (output.Length != offset) {
                    throw new FileOperationException(
                        ApiProblemCodes.UploadOffsetMismatch,
                        $"The upload has {output.Length} bytes; the next chunk must start there.");
                }

                output.Seek(0, SeekOrigin.End);
                await CopyBoundedAsync(content, output, record.SizeBytes - offset, cancellationToken);
            }

            return ReadState(record, dataPath, RecordPath(root, record.Id));
        } finally {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CommitAsync(
        FileLibraryRoot root,
        FileUploadSessionRecord record,
        ResolvedFilePath destination,
        CancellationToken cancellationToken) {
        var dataPath = DataPath(root, record.Id);
        await using var protection = await mutations.EnterAsync([dataPath, destination.AbsolutePath], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeStagingDirectory(root, create: false);
        LocalManagedFileStorage.EnsureNoSymlinkAncestor(destination);
        if (!File.Exists(dataPath)) {
            throw new FileOperationException(ApiProblemCodes.NotFound, "The upload was not found.");
        }

        if (DestinationExists(destination)) {
            throw new FileConflictException($"A file or folder already exists at {destination.RelativePath}.");
        }

        var parent = Path.GetDirectoryName(destination.AbsolutePath)!;
        try {
            Directory.CreateDirectory(parent);
        } catch (IOException) {
            throw new FileConflictException($"A file is in the way of the folders for {destination.RelativePath}.");
        }

        // Re-check after creating folders: a link may not be introduced between the check and the move.
        LocalManagedFileStorage.EnsureNoSymlinkAncestor(destination);
        if (SameFilesystem(dataPath, parent)) {
            MoveWithoutOverwrite(dataPath, destination);
        } else {
            // Copy to a hidden name beside the destination, then rename within that folder, which is
            // always atomic. The staged bytes stay intact until the final rename succeeds.
            var temporary = Path.Combine(parent, $".{record.Id:N}{DataExtension}");
            try {
                File.Copy(dataPath, temporary, overwrite: true);
                MoveWithoutOverwrite(temporary, destination);
            } finally {
                File.Delete(temporary);
            }

            File.Delete(dataPath);
        }

        File.Delete(RecordPath(root, record.Id));
        sessionLocks.TryRemove(record.Id, out _);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(FileLibraryRoot root, Guid id, CancellationToken cancellationToken) {
        var dataPath = DataPath(root, id);
        await using var protection = await mutations.EnterAsync([dataPath], cancellationToken);
        if (!IsSafeStagingDirectory(root)) {
            return;
        }

        File.Delete(dataPath);
        File.Delete(RecordPath(root, id));
        sessionLocks.TryRemove(id, out _);
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(
        FileLibraryRoot root,
        DateTimeOffset idleBefore,
        CancellationToken cancellationToken) {
        var directory = StagingDirectory(root);
        if (!Directory.Exists(directory) || !IsSafeStagingDirectory(root)) {
            return 0;
        }

        await using var protection = await mutations.EnterAsync([directory], cancellationToken);
        var removed = 0;
        var ids = Directory.EnumerateFiles(directory)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in ids) {
            cancellationToken.ThrowIfCancellationRequested();
            var data = Path.Combine(directory, name + DataExtension);
            var recordPath = Path.Combine(directory, name + RecordExtension);
            var lastActivity = new[] { data, recordPath }
                .Where(File.Exists)
                .Select(path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            if (lastActivity >= idleBefore || (Guid.TryParse(name, out var id) && IsBusy(id))) {
                continue;
            }

            File.Delete(data);
            File.Delete(recordPath);
            if (Guid.TryParse(name, out var staleId)) {
                sessionLocks.TryRemove(staleId, out _);
            }
            removed++;
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any()) {
            Directory.Delete(directory);
        }

        return removed;
    }

    /// <inheritdoc />
    public bool DestinationExists(ResolvedFilePath destination) =>
        File.Exists(destination.AbsolutePath) ||
        Directory.Exists(destination.AbsolutePath) ||
        new FileInfo(destination.AbsolutePath).LinkTarget is not null;

    /// <inheritdoc />
    public long? GetAvailableFreeBytes(FileLibraryRoot root) {
        try {
            return new DriveInfo(Path.GetFullPath(root.Path)).AvailableFreeSpace;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }
    }

    /// <summary>Absolute path of a root's staging folder.</summary>
    internal static string StagingDirectory(FileLibraryRoot root) =>
        Path.Combine(Path.GetFullPath(root.Path), FileUploadStaging.DirectoryName);

    private static string DataPath(FileLibraryRoot root, Guid id) =>
        Path.Combine(StagingDirectory(root), id.ToString("N") + DataExtension);

    private static string RecordPath(FileLibraryRoot root, Guid id) =>
        Path.Combine(StagingDirectory(root), id.ToString("N") + RecordExtension);

    private bool IsBusy(Guid id) => sessionLocks.TryGetValue(id, out var gate) && gate.CurrentCount == 0;

    private static FileUploadSessionState ReadState(FileUploadSessionRecord record, string dataPath, string recordPath) {
        var data = new FileInfo(dataPath);
        var lastWrite = new[] { data.LastWriteTimeUtc, File.GetLastWriteTimeUtc(recordPath) }.Max();
        var lastActivity = new DateTimeOffset(lastWrite, TimeSpan.Zero);
        return new FileUploadSessionState(
            record,
            data.Length,
            lastActivity > record.CreatedAt ? lastActivity : record.CreatedAt);
    }

    private static async Task CopyBoundedAsync(
        Stream content,
        Stream output,
        long remaining,
        CancellationToken cancellationToken) {
        var buffer = new byte[81920];
        while (true) {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) {
                return;
            }

            if (read > remaining) {
                await output.WriteAsync(buffer.AsMemory(0, (int)remaining), cancellationToken);
                throw new FileOperationException(ApiProblemCodes.InvalidUpload, "The chunk runs past the declared upload size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static void MoveWithoutOverwrite(string source, ResolvedFilePath destination) {
        try {
            File.Move(source, destination.AbsolutePath, overwrite: false);
        } catch (IOException) when (File.Exists(destination.AbsolutePath) || Directory.Exists(destination.AbsolutePath)) {
            throw new FileConflictException($"A file or folder already exists at {destination.RelativePath}.");
        }
    }

    /// <summary>
    /// Whether two paths live on the same mounted filesystem, judged by the longest mount point
    /// containing each. Unknown mounts are treated as the same filesystem.
    /// </summary>
    private static bool SameFilesystem(string first, string second) {
        string[] mounts;
        try {
            mounts = DriveInfo.GetDrives().Select(drive => drive.Name).ToArray();
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            return true;
        }

        var firstMount = MountPointOf(CompletedPayloadFileSystem.CanonicalPath(first), mounts);
        var secondMount = MountPointOf(CompletedPayloadFileSystem.CanonicalPath(second), mounts);
        return firstMount is null || secondMount is null || FileSystemPathComparison.Equals(firstMount, secondMount);
    }

    private static string? MountPointOf(string path, IReadOnlyList<string> mounts) =>
        mounts
            .Where(mount => FileSystemPathComparison.IsSameOrDescendant(mount, path))
            .OrderByDescending(mount => Path.TrimEndingDirectorySeparator(mount).Length)
            .FirstOrDefault();

    private static void EnsureSafeStagingDirectory(FileLibraryRoot root, bool create) {
        var directory = StagingDirectory(root);
        if (create && !Directory.Exists(directory) && !File.Exists(directory)) {
            Directory.CreateDirectory(directory);
        }

        if (!IsSafeStagingDirectory(root)) {
            throw new FileOperationException(
                ApiProblemCodes.InvalidPath,
                "The upload staging folder must be a real folder inside the library root.");
        }
    }

    private static bool IsSafeStagingDirectory(FileLibraryRoot root) {
        var directory = StagingDirectory(root);
        if (!Directory.Exists(directory)) {
            return false;
        }

        var info = new DirectoryInfo(directory);
        return !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is null;
    }

    /// <summary>On-disk JSON shape of an upload description.</summary>
    private sealed record StoredRecord(
        Guid Id,
        Guid RootId,
        string RelativePath,
        long SizeBytes,
        string ConflictPolicy,
        DateTimeOffset CreatedAt) {
        public static StoredRecord From(FileUploadSessionRecord record) =>
            new(
                record.Id,
                record.RootId,
                record.RelativePath,
                record.SizeBytes,
                record.ConflictPolicy.ToCode(),
                record.CreatedAt);

        public FileUploadSessionRecord ToRecord() =>
            new(Id, RootId, RelativePath, SizeBytes, ConflictPolicy.DecodeAs<FileUploadConflictPolicy>(), CreatedAt);
    }
}
