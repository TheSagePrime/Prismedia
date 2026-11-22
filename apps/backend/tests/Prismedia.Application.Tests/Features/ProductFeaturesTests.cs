using Prismedia.Application.Features;
using Prismedia.Application.Jobs;
using Prismedia.Domain.Entities;

namespace Prismedia.Application.Tests.Features;

public sealed class ProductFeaturesTests {
    [Theory]
    [InlineData(null, FeatureProfile.Simple)]
    [InlineData("", FeatureProfile.Simple)]
    [InlineData("simple", FeatureProfile.Simple)]
    [InlineData(" FULL ", FeatureProfile.Full)]
    public void ProfileDefaultsToSimpleAndParsesCodes(string? configured, FeatureProfile expected) =>
        Assert.Equal(expected, ProductFeatureSet.ParseProfile(configured));

    [Fact]
    public void UnknownProfileIsRejectedInsteadOfGuessing() =>
        Assert.Throws<InvalidOperationException>(() => ProductFeatureSet.ParseProfile("photos-only"));

    [Fact]
    public void SimpleProfileTurnsEveryOptionalAreaOffAndFullTurnsThemOn() {
        var simple = new ProductFeatureSet(FeatureProfile.Simple);
        var full = new ProductFeatureSet(FeatureProfile.Full);

        Assert.Empty(simple.Enabled);
        Assert.All(Enum.GetValues<ProductFeature>(), feature => Assert.False(simple.IsEnabled(feature)));
        Assert.Equal(Enum.GetValues<ProductFeature>(), full.Enabled);
    }

    [Theory]
    [InlineData(JobType.AutoIdentify, ProductFeature.Identify)]
    [InlineData(JobType.AcquireSubtitles, ProductFeature.Identify)]
    [InlineData(JobType.UpdatePlugins, ProductFeature.Plugins)]
    [InlineData(JobType.MonitoredSearch, ProductFeature.Acquisition)]
    [InlineData(JobType.ScanAudio, ProductFeature.Audio)]
    [InlineData(JobType.ScanBook, ProductFeature.Books)]
    public void HiddenAreasOwnTheirBackgroundJobs(JobType type, ProductFeature owner) {
        Assert.Equal(owner, ProductFeatureJobPolicy.OwnerOf(type));
        Assert.False(ProductFeatureJobPolicy.IsAllowed(new ProductFeatureSet(FeatureProfile.Simple), type));
        Assert.True(ProductFeatureJobPolicy.IsAllowed(new ProductFeatureSet(FeatureProfile.Full), type));
    }

    [Theory]
    [InlineData(JobType.ScanLibrary)]
    [InlineData(JobType.ScanGallery)]
    [InlineData(JobType.ProbeVideo)]
    [InlineData(JobType.GenerateImageThumbnail)]
    [InlineData(JobType.GenerateTrickplay)]
    [InlineData(JobType.ExtractSubtitles)]
    [InlineData(JobType.RefreshCollection)]
    [InlineData(JobType.DatabaseBackup)]
    public void CoreLibraryJobsAlwaysRun(JobType type) {
        Assert.Null(ProductFeatureJobPolicy.OwnerOf(type));
        Assert.True(ProductFeatureJobPolicy.IsAllowed(new ProductFeatureSet(FeatureProfile.Simple), type));
    }

    [Fact]
    public void EveryAcquisitionAndIdentifyJobTypeIsOwned() {
        var unowned = Enum.GetValues<JobType>()
            .Where(type => type.ToCode().StartsWith("acquisition", StringComparison.Ordinal) ||
                           type.ToCode().StartsWith("identify", StringComparison.Ordinal) ||
                           type.ToCode().Contains("acquire", StringComparison.Ordinal))
            .Where(type => ProductFeatureJobPolicy.OwnerOf(type) is null)
            .ToArray();

        Assert.Empty(unowned);
    }

    [Fact]
    public async Task JobsDoNotQueueDownstreamWorkForHiddenAreas() {
        var queue = new RecordingQueue();
        var context = new JobContext(Snapshot(JobType.ScanLibrary), queue, new ProductFeatureSet(FeatureProfile.Simple));

        var identify = await context.EnqueueIfNeededAsync(new EnqueueJobRequest(JobType.AutoIdentify));
        var thumbnail = await context.EnqueueIfNeededAsync(new EnqueueJobRequest(JobType.GenerateImageThumbnail));
        var batch = await context.EnqueueBatchAsync([
            new EnqueueJobRequest(JobType.AcquireSubtitles),
            new EnqueueJobRequest(JobType.ProbeVideo),
        ]);

        Assert.Null(identify);
        Assert.NotNull(thumbnail);
        Assert.Equal(1, batch);
        Assert.Equal([JobType.GenerateImageThumbnail, JobType.ProbeVideo], queue.Enqueued.Select(request => request.Type));
    }

    private static JobRunSnapshot Snapshot(JobType type) =>
        new(Guid.NewGuid(), type, JobRunStatus.Running, 0, null, "{}", null, null, null, DateTimeOffset.UtcNow, null, null);

    private sealed class RecordingQueue : IJobQueueService {
        public List<EnqueueJobRequest> Enqueued { get; } = [];

        public Task<JobRunSnapshot> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken) {
            Enqueued.Add(request);
            return Task.FromResult(Snapshot(request.Type));
        }

        public Task<int> EnqueueBatchAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken cancellationToken) {
            Enqueued.AddRange(requests);
            return Task.FromResult(requests.Count);
        }

        public Task<bool> HasPendingAsync(JobType type, string? targetEntityId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<JobRunSnapshot> EnqueueAsync(JobType type, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobRunSnapshot>> ListAsync(bool hideNsfw, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelAsync(JobType? type, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelRunAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ClearFailuresAsync(JobType? type, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JobRunSnapshot?> ClaimNextAsync(string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RecoverStaleRunningAsync(string currentWorkerId, TimeSpan staleAfter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateProgressAsync(Guid id, int progress, string? message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid id, string? message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Guid id, string message, TimeSpan retryDelay, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobQueueCount>> GetQueueCountsAsync(bool hideNsfw, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> PruneHistoryAsync(TimeSpan retention, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
