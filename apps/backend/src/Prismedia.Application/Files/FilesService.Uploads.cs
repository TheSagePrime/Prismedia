using Prismedia.Contracts.Files;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Application.Files;

/// <summary>
/// Resumable upload use cases for the Files page. Each file is its own upload: it starts with
/// its declared size and conflict policy, receives ordered chunks into a hidden staging file on
/// the library's filesystem, and is moved into place only once every byte has arrived.
/// </summary>
public sealed partial class FilesService {
    /// <summary>
    /// Starts one resumable upload. A skip policy whose destination already exists finishes at once
    /// without any bytes being sent; a fail policy reports the conflict before any bytes are sent.
    /// </summary>
    /// <param name="request">Destination, declared size, and conflict policy.</param>
    /// <param name="hideNsfw">Whether NSFW-owned paths are hidden from the caller.</param>
    /// <returns>A session to send chunks to, or the immediate completion for a skipped file.</returns>
    public async Task<FileUploadCreateResponse> CreateUploadAsync(
        FileUploadCreateRequest request,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        if (request.SizeBytes < 0) {
            throw new FileOperationException(ApiProblemCodes.InvalidUpload, "Upload size cannot be negative.");
        }

        var relativePath = CombineRelative(
            NormalizeRelativePath(request.TargetPath),
            CleanUploadRelativePath(request.RelativePath));
        var destination = await ResolveAsync(request.RootId, relativePath, hideNsfw, cancellationToken);
        EnsureWritable(destination.Root);
        EnsureNotRoot(destination, "Uploads need a file name.");
        await EnsureVisiblePathAsync(destination, hideNsfw, cancellationToken);

        if (uploads.DestinationExists(destination)) {
            switch (request.ConflictPolicy) {
                case FileUploadConflictPolicy.Skip:
                    return new FileUploadCreateResponse(
                        null,
                        new FileUploadCompletion(FileUploadOutcome.Skipped, destination.RelativePath, 0));
                case FileUploadConflictPolicy.Fail:
                    throw new FileConflictException($"A file or folder already exists at {destination.RelativePath}.");
            }
        }

        var available = uploads.GetAvailableFreeBytes(destination.Root);
        if (available is { } free && free < request.SizeBytes) {
            throw new FileOperationException(
                ApiProblemCodes.InsufficientStorage,
                $"The library disk has {free:N0} bytes free, which is not enough for {destination.RelativePath}.");
        }

        var state = await uploads.CreateAsync(
            destination.Root,
            new FileUploadSessionRecord(
                Guid.NewGuid(),
                destination.Root.Id,
                destination.RelativePath,
                request.SizeBytes,
                request.ConflictPolicy,
                DateTimeOffset.UtcNow),
            cancellationToken);
        return new FileUploadCreateResponse(ToSession(state), null);
    }

    /// <summary>Gets a staged upload's progress so a client can resume after an interruption.</summary>
    /// <param name="id">Upload session identifier.</param>
    public async Task<FileUploadSession> GetUploadAsync(Guid id, CancellationToken cancellationToken) {
        var (_, state) = await FindUploadAsync(id, cancellationToken);
        return ToSession(state);
    }

    /// <summary>
    /// Stores one chunk of a staged upload. The chunk must start at the current received length;
    /// a mismatch is reported with <see cref="ApiProblemCodes.UploadOffsetMismatch"/> so the client
    /// can query progress and continue from the right byte.
    /// </summary>
    /// <param name="id">Upload session identifier.</param>
    /// <param name="offset">Byte offset the chunk starts at.</param>
    /// <param name="contentLength">Declared chunk length when the request carries one.</param>
    /// <param name="content">Chunk bytes.</param>
    /// <returns>The upload's progress after storing the chunk.</returns>
    public async Task<FileUploadSession> AppendUploadChunkAsync(
        Guid id,
        long offset,
        long? contentLength,
        Stream content,
        CancellationToken cancellationToken) {
        var (root, state) = await FindUploadAsync(id, cancellationToken);
        EnsureWritable(root);
        if (offset != state.ReceivedBytes) {
            throw new FileOperationException(
                ApiProblemCodes.UploadOffsetMismatch,
                $"The upload has {state.ReceivedBytes} bytes; the next chunk must start there.");
        }

        if (contentLength is { } length) {
            if (length > FileUploadStaging.MaxChunkBytes || offset + length > state.Record.SizeBytes) {
                throw new FileOperationException(ApiProblemCodes.InvalidUpload, "The chunk is larger than the remaining upload.");
            }

            if (uploads.GetAvailableFreeBytes(root) is { } free && free < length) {
                throw new FileOperationException(ApiProblemCodes.InsufficientStorage, "The library disk is full.");
            }
        }

        var updated = await uploads.AppendAsync(root, state.Record, offset, content, cancellationToken);
        return ToSession(updated);
    }

    /// <summary>
    /// Moves a fully received upload into the library, applying its conflict policy against what
    /// exists now, and queues a scan for the new file.
    /// </summary>
    /// <param name="id">Upload session identifier.</param>
    /// <param name="hideNsfw">Whether NSFW-owned paths are hidden from the caller.</param>
    /// <returns>Where the file was stored, or that it was skipped.</returns>
    public async Task<FileUploadCompletion> CompleteUploadAsync(
        Guid id,
        bool hideNsfw,
        CancellationToken cancellationToken) {
        var (root, state) = await FindUploadAsync(id, cancellationToken);
        EnsureWritable(root);
        var record = state.Record;
        if (state.ReceivedBytes != record.SizeBytes) {
            throw new FileOperationException(
                ApiProblemCodes.UploadIncomplete,
                $"The upload has {state.ReceivedBytes} of {record.SizeBytes} bytes.");
        }

        for (var attempt = 0; attempt <= FileUploadStaging.MaxKeepBothAttempts; attempt++) {
            var candidate = attempt == 0
                ? record.RelativePath
                : FileUploadStaging.NumberedPath(record.RelativePath, attempt);
            var destination = await ResolveAsync(record.RootId, candidate, hideNsfw, cancellationToken);
            await EnsureVisiblePathAsync(destination, hideNsfw, cancellationToken);
            try {
                await uploads.CommitAsync(root, record, destination, cancellationToken);
            } catch (FileConflictException) when (record.ConflictPolicy == FileUploadConflictPolicy.KeepBoth) {
                continue;
            } catch (FileConflictException) when (record.ConflictPolicy == FileUploadConflictPolicy.Skip) {
                await uploads.DeleteAsync(root, record.Id, cancellationToken);
                return new FileUploadCompletion(FileUploadOutcome.Skipped, destination.RelativePath, 0);
            }

            var queued = await QueueChangedScansAsync(root, [destination.AbsolutePath], cancellationToken);
            return new FileUploadCompletion(
                attempt == 0 ? FileUploadOutcome.Uploaded : FileUploadOutcome.Renamed,
                destination.RelativePath,
                queued);
        }

        throw new FileConflictException($"No free numbered name was found for {record.RelativePath}.");
    }

    /// <summary>Cancels a staged upload and deletes its partial bytes. Unknown ids succeed.</summary>
    /// <param name="id">Upload session identifier.</param>
    public async Task CancelUploadAsync(Guid id, CancellationToken cancellationToken) {
        foreach (var root in await persistence.ListRootsAsync(cancellationToken)) {
            if (root.IsReadOnly) {
                continue;
            }

            if (await uploads.FindAsync(root, id, cancellationToken) is not null) {
                await uploads.DeleteAsync(root, id, cancellationToken);
                return;
            }
        }
    }

    /// <summary>
    /// Deletes staged uploads that have received nothing for <see cref="FileUploadStaging.IdleLifetime"/>.
    /// </summary>
    /// <param name="now">Current time used to compute the idle cutoff.</param>
    /// <returns>Number of abandoned uploads removed across all writable roots.</returns>
    public async Task<int> PruneAbandonedUploadsAsync(DateTimeOffset now, CancellationToken cancellationToken) {
        var removed = 0;
        foreach (var root in await persistence.ListRootsAsync(cancellationToken)) {
            if (!root.IsReadOnly) {
                removed += await uploads.PruneAsync(root, now - FileUploadStaging.IdleLifetime, cancellationToken);
            }
        }

        return removed;
    }

    private async Task<(FileLibraryRoot Root, FileUploadSessionState State)> FindUploadAsync(
        Guid id,
        CancellationToken cancellationToken) {
        foreach (var root in await persistence.ListRootsAsync(cancellationToken)) {
            if (root.IsReadOnly) {
                continue;
            }

            if (await uploads.FindAsync(root, id, cancellationToken) is { } state) {
                return (root, state);
            }
        }

        throw new FileOperationException(ApiProblemCodes.NotFound, "The upload was not found. It may have expired or finished.");
    }

    private static FileUploadSession ToSession(FileUploadSessionState state) =>
        new(
            state.Record.Id,
            state.Record.RootId,
            state.Record.RelativePath,
            state.Record.SizeBytes,
            state.ReceivedBytes,
            FileUploadStaging.MaxChunkBytes,
            state.LastActivityAt + FileUploadStaging.IdleLifetime);

    /// <summary>
    /// Validates an upload's folder-relative path segment by segment, so dropped folders keep their
    /// structure while dot segments and invalid names are refused before anything is written.
    /// </summary>
    private static string CleanUploadRelativePath(string relativePath) {
        var segments = ToRelativePath(relativePath ?? string.Empty)
            .Split('/')
            .Select(segment => segment.Trim())
            .ToArray();
        if (segments.Length == 0 || segments.All(string.IsNullOrEmpty)) {
            throw new FileOperationException(ApiProblemCodes.InvalidPath, "Uploaded files must include a relative path.");
        }

        return string.Join('/', segments.Select(CleanSegment));
    }
}
