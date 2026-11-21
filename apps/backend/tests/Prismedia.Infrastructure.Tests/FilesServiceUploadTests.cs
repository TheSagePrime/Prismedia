using Prismedia.Application.Files;
using Prismedia.Application.Jobs;
using Prismedia.Contracts.Files;
using Prismedia.Contracts.System;
using Prismedia.Domain.Entities;

namespace Prismedia.Infrastructure.Tests;

/// <summary>Resumable chunked Files uploads: offsets, resume, atomic completion, conflicts, and cleanup.</summary>
public sealed partial class FilesServiceTests {
    private string StagingDirectory => Path.Combine(_tempRoot.FullName, FileUploadStaging.DirectoryName);

    [Fact]
    public async Task ChunkedUploadStoresExactBytesAtNestedPathAndQueuesScan() {
        var service = CreateService();
        var bytes = Enumerable.Range(0, 10_000).Select(index => (byte)(index % 251)).ToArray();

        var completion = await FileUploadTestHelpers.UploadAsync(
            service, _rootId, "Incoming", "Album/Cover.jpg", bytes, chunkSize: 4096);

        var stored = Path.Combine(_tempRoot.FullName, "Incoming", "Album", "Cover.jpg");
        Assert.Equal(FileUploadOutcome.Uploaded, completion.Outcome);
        Assert.Equal("Incoming/Album/Cover.jpg", completion.RelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));
        Assert.Contains(JobType.ScanLibrary, Queue.Enqueued.Select(job => job.Type));
        Assert.Empty(Directory.EnumerateFiles(StagingDirectory));
    }

    [Fact]
    public async Task EmptyFilesUploadWithoutChunks() {
        var service = CreateService();

        var completion = await FileUploadTestHelpers.UploadAsync(service, _rootId, "", "empty.txt", []);

        Assert.Equal(FileUploadOutcome.Uploaded, completion.Outcome);
        Assert.Equal(0, new FileInfo(Path.Combine(_tempRoot.FullName, "empty.txt")).Length);
    }

    [Fact]
    public async Task ChunkAtWrongOffsetIsRejectedWithoutChangingStoredBytes() {
        var service = CreateService();
        var session = await StartAsync(service, "clip.mp4", 8);
        await service.AppendUploadChunkAsync(session.Id, 0, 4, new MemoryStream([1, 2, 3, 4]), default);

        var behind = await Assert.ThrowsAsync<FileOperationException>(() =>
            service.AppendUploadChunkAsync(session.Id, 0, 4, new MemoryStream([9, 9, 9, 9]), default));
        var ahead = await Assert.ThrowsAsync<FileOperationException>(() =>
            service.AppendUploadChunkAsync(session.Id, 6, 2, new MemoryStream([9, 9]), default));

        Assert.Equal(ApiProblemCodes.UploadOffsetMismatch, behind.Code);
        Assert.Equal(ApiProblemCodes.UploadOffsetMismatch, ahead.Code);
        Assert.Equal(4, (await service.GetUploadAsync(session.Id, default)).ReceivedBytes);
    }

    [Fact]
    public async Task InterruptedChunkKeepsArrivedBytesSoTheUploadResumes() {
        var service = CreateService();
        var bytes = Enumerable.Range(0, 100).Select(index => (byte)index).ToArray();
        var session = await StartAsync(service, "resume.bin", bytes.Length);

        await Assert.ThrowsAsync<IOException>(() =>
            service.AppendUploadChunkAsync(session.Id, 0, 60, new DroppingStream(bytes[..60], failAfter: 25), default));
        var resumed = await service.GetUploadAsync(session.Id, default);
        Assert.Equal(25, resumed.ReceivedBytes);

        await service.AppendUploadChunkAsync(
            session.Id, resumed.ReceivedBytes, bytes.Length - 25, new MemoryStream(bytes[25..]), default);
        var completion = await service.CompleteUploadAsync(session.Id, hideNsfw: false, default);

        Assert.Equal(FileUploadOutcome.Uploaded, completion.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_tempRoot.FullName, "resume.bin")));
    }

    [Fact]
    public async Task PartialUploadNeverAppearsAtItsFinalPath() {
        var service = CreateService();
        var finalPath = Path.Combine(_tempRoot.FullName, "movie.mkv");
        var session = await StartAsync(service, "movie.mkv", 10);
        await service.AppendUploadChunkAsync(session.Id, 0, 6, new MemoryStream(new byte[6]), default);

        Assert.False(File.Exists(finalPath));
        var early = await Assert.ThrowsAsync<FileOperationException>(() =>
            service.CompleteUploadAsync(session.Id, hideNsfw: false, default));
        Assert.Equal(ApiProblemCodes.UploadIncomplete, early.Code);
        Assert.False(File.Exists(finalPath));
        var listing = await service.ListChildrenAsync(new FileChildrenRequest(_rootId, ""), false, default);
        Assert.Empty(listing.Entries);

        await service.AppendUploadChunkAsync(session.Id, 6, 4, new MemoryStream(new byte[4]), default);
        await service.CompleteUploadAsync(session.Id, hideNsfw: false, default);
        Assert.Equal(10, new FileInfo(finalPath).Length);
    }

    [Fact]
    public async Task ChunkPastDeclaredSizeIsRejected() {
        var service = CreateService();
        var session = await StartAsync(service, "small.txt", 3);

        var declared = await Assert.ThrowsAsync<FileOperationException>(() =>
            service.AppendUploadChunkAsync(session.Id, 0, 4, new MemoryStream([1, 2, 3, 4]), default));
        var undeclared = await Assert.ThrowsAsync<FileOperationException>(() =>
            service.AppendUploadChunkAsync(session.Id, 0, null, new MemoryStream([1, 2, 3, 4]), default));

        Assert.Equal(ApiProblemCodes.InvalidUpload, declared.Code);
        Assert.Equal(ApiProblemCodes.InvalidUpload, undeclared.Code);
        Assert.True((await service.GetUploadAsync(session.Id, default)).ReceivedBytes <= 3);
    }

    [Fact]
    public async Task SkipPolicyFinishesImmediatelyWhenTheDestinationExists() {
        var existing = Path.Combine(_tempRoot.FullName, "photo.jpg");
        await File.WriteAllTextAsync(existing, "original");
        var service = CreateService();

        var response = await service.CreateUploadAsync(
            new FileUploadCreateRequest(_rootId, "", "photo.jpg", 3, FileUploadConflictPolicy.Skip), false, default);

        Assert.Null(response.Session);
        Assert.Equal(FileUploadOutcome.Skipped, response.Completion?.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(existing));
        Assert.False(Directory.Exists(StagingDirectory));
    }

    [Fact]
    public async Task SkipPolicyLeavesAFileThatAppearedDuringTheUpload() {
        var service = CreateService();
        var session = await StartAsync(service, "late.jpg", 3, FileUploadConflictPolicy.Skip);
        await service.AppendUploadChunkAsync(session.Id, 0, 3, new MemoryStream([1, 2, 3]), default);
        var existing = Path.Combine(_tempRoot.FullName, "late.jpg");
        await File.WriteAllTextAsync(existing, "arrived first");

        var completion = await service.CompleteUploadAsync(session.Id, false, default);

        Assert.Equal(FileUploadOutcome.Skipped, completion.Outcome);
        Assert.Equal("arrived first", await File.ReadAllTextAsync(existing));
        Assert.Empty(Directory.EnumerateFiles(StagingDirectory));
    }

    [Fact]
    public async Task KeepBothStoresNumberedCopiesBesideTheExistingFile() {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot.FullName, "IMG_0001.HEIC"), "original");
        var service = CreateService();

        var first = await FileUploadTestHelpers.UploadAsync(
            service, _rootId, "", "IMG_0001.HEIC", [1], FileUploadConflictPolicy.KeepBoth);
        var second = await FileUploadTestHelpers.UploadAsync(
            service, _rootId, "", "IMG_0001.HEIC", [2], FileUploadConflictPolicy.KeepBoth);

        Assert.Equal(FileUploadOutcome.Renamed, first.Outcome);
        Assert.Equal("IMG_0001 (1).HEIC", first.RelativePath);
        Assert.Equal("IMG_0001 (2).HEIC", second.RelativePath);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_tempRoot.FullName, "IMG_0001.HEIC")));
        Assert.Equal([2], await File.ReadAllBytesAsync(Path.Combine(_tempRoot.FullName, "IMG_0001 (2).HEIC")));
    }

    [Fact]
    public async Task FailPolicyReportsConflictsWithoutTouchingTheExistingFile() {
        var existing = Path.Combine(_tempRoot.FullName, "taken.mp4");
        await File.WriteAllTextAsync(existing, "original");
        var service = CreateService();

        await Assert.ThrowsAsync<FileConflictException>(() => service.CreateUploadAsync(
            new FileUploadCreateRequest(_rootId, "", "taken.mp4", 1, FileUploadConflictPolicy.Fail), false, default));

        var session = await StartAsync(service, "later.mp4", 1, FileUploadConflictPolicy.Fail);
        await service.AppendUploadChunkAsync(session.Id, 0, 1, new MemoryStream([7]), default);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot.FullName, "later.mp4"), "won the race");
        await Assert.ThrowsAsync<FileConflictException>(() => service.CompleteUploadAsync(session.Id, false, default));

        Assert.Equal("original", await File.ReadAllTextAsync(existing));
        Assert.Equal("won the race", await File.ReadAllTextAsync(Path.Combine(_tempRoot.FullName, "later.mp4")));
        Assert.Equal(1, (await service.GetUploadAsync(session.Id, default)).ReceivedBytes);
    }

    [Theory]
    [InlineData("", "../escape.txt")]
    [InlineData("", "Album/../../escape.txt")]
    [InlineData("..", "escape.txt")]
    [InlineData("", ".prismedia-uploads/planted.part")]
    [InlineData(".prismedia-uploads", "planted.part")]
    [InlineData("", "")]
    [InlineData("", "Album/./photo.jpg")]
    public async Task UploadPathsCannotEscapeTheRootOrAddressStaging(string targetPath, string relativePath) {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<FileOperationException>(() => service.CreateUploadAsync(
            new FileUploadCreateRequest(_rootId, targetPath, relativePath, 1), false, default));

        Assert.Equal(ApiProblemCodes.InvalidPath, error.Code);
        Assert.False(File.Exists(Path.Combine(_tempRoot.Parent!.FullName, "escape.txt")));
    }

    [Fact]
    public async Task FilesOperationsCannotReachTheStagingFolder() {
        var service = CreateService();
        await StartAsync(service, "pending.mp4", 5);

        var listing = await service.ListChildrenAsync(new FileChildrenRequest(_rootId, ""), false, default);
        Assert.DoesNotContain(listing.Entries, entry => entry.Name == FileUploadStaging.DirectoryName);
        await Assert.ThrowsAsync<FileOperationException>(() =>
            service.DeleteAsync(new FileDeleteRequest(_rootId, FileUploadStaging.DirectoryName), false, default));
        await Assert.ThrowsAsync<FileOperationException>(() =>
            service.ListChildrenAsync(new FileChildrenRequest(_rootId, FileUploadStaging.DirectoryName), false, default));
        Assert.True(Directory.Exists(StagingDirectory));
    }

    [Fact]
    public async Task SymlinkedStagingFolderIsRefused() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        var outside = Directory.CreateTempSubdirectory("prismedia-files-outside-");
        try {
            Directory.CreateSymbolicLink(StagingDirectory, outside.FullName);
            var service = CreateService();

            await Assert.ThrowsAsync<FileOperationException>(() => StartAsync(service, "x.bin", 1));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.FullName));
        } finally {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancelDeletesPartialBytes() {
        var service = CreateService();
        var session = await StartAsync(service, "cancel.bin", 4);
        await service.AppendUploadChunkAsync(session.Id, 0, 2, new MemoryStream([1, 2]), default);

        await service.CancelUploadAsync(session.Id, default);

        Assert.Empty(Directory.EnumerateFiles(StagingDirectory));
        var missing = await Assert.ThrowsAsync<FileOperationException>(() => service.GetUploadAsync(session.Id, default));
        Assert.Equal(ApiProblemCodes.NotFound, missing.Code);
    }

    [Fact]
    public async Task PruneRemovesOnlyAbandonedUploads() {
        var service = CreateService();
        var abandoned = await StartAsync(service, "old.bin", 4);
        var active = await StartAsync(service, "new.bin", 4);
        var stale = DateTime.UtcNow - FileUploadStaging.IdleLifetime - TimeSpan.FromHours(1);
        foreach (var path in Directory.EnumerateFiles(StagingDirectory, $"{abandoned.Id:N}.*")) {
            File.SetLastWriteTimeUtc(path, stale);
        }

        var removed = await service.PruneAbandonedUploadsAsync(DateTimeOffset.UtcNow, default);

        Assert.Equal(1, removed);
        await Assert.ThrowsAsync<FileOperationException>(() => service.GetUploadAsync(abandoned.Id, default));
        Assert.Equal(0, (await service.GetUploadAsync(active.Id, default)).ReceivedBytes);
    }

    [Fact]
    public async Task UploadsAreRefusedWhenTheDiskIsTooSmall() {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            StartAsync(service, "huge.mkv", long.MaxValue / 2));

        Assert.Equal(ApiProblemCodes.InsufficientStorage, error.Code);
        Assert.False(Directory.Exists(StagingDirectory));
    }

    [Theory]
    [InlineData("photo.jpg", 1, "photo (1).jpg")]
    [InlineData("Album/clip.tar.gz", 3, "Album/clip.tar (3).gz")]
    [InlineData("README", 2, "README (2)")]
    [InlineData("Album/.hidden", 1, "Album/.hidden (1)")]
    public void NumberedPathKeepsFolderAndExtension(string path, int attempt, string expected) =>
        Assert.Equal(expected, FileUploadStaging.NumberedPath(path, attempt));

    [Theory]
    [InlineData("/media/.prismedia-uploads/abc.part", true)]
    [InlineData("/media/.prismedia-uploads", true)]
    [InlineData("/media/Photos/.prismedia-uploads-not/abc.jpg", false)]
    [InlineData("/media/Photos/abc.jpg", false)]
    public void StagingPathsAreRecognized(string path, bool expected) =>
        Assert.Equal(expected, FileUploadStaging.IsStagingPath(path));

    private async Task<FileUploadSession> StartAsync(
        FilesService service,
        string relativePath,
        long size,
        FileUploadConflictPolicy policy = FileUploadConflictPolicy.Fail) {
        var response = await service.CreateUploadAsync(
            new FileUploadCreateRequest(_rootId, "", relativePath, size, policy), false, default);
        return Assert.IsType<FileUploadSession>(response.Session);
    }

    /// <summary>Stream that yields some bytes and then fails, like a dropped connection mid-chunk.</summary>
    private sealed class DroppingStream(byte[] bytes, int failAfter) : MemoryStream(bytes) {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (Position >= failAfter) {
                throw new IOException("Connection dropped.");
            }

            var allowed = (int)Math.Min(buffer.Length, failAfter - Position);
            return await base.ReadAsync(buffer[..allowed], cancellationToken);
        }
    }
}

/// <summary>Drives a complete resumable upload through the public service methods.</summary>
internal static class FileUploadTestHelpers {
    public static async Task<FileUploadCompletion> UploadAsync(
        FilesService service,
        Guid rootId,
        string targetPath,
        string relativePath,
        byte[] bytes,
        FileUploadConflictPolicy policy = FileUploadConflictPolicy.Fail,
        int chunkSize = 1024) {
        var response = await service.CreateUploadAsync(
            new FileUploadCreateRequest(rootId, targetPath, relativePath, bytes.Length, policy), false, default);
        if (response.Completion is { } immediate) {
            return immediate;
        }

        var session = response.Session!;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize) {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            await service.AppendUploadChunkAsync(
                session.Id, offset, length, new MemoryStream(bytes, offset, length), default);
        }

        return await service.CompleteUploadAsync(session.Id, false, default);
    }
}
