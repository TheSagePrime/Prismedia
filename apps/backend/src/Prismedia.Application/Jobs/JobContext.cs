using Prismedia.Domain.Entities;

namespace Prismedia.Application.Jobs;

/// <summary>
/// Execution context passed to job handlers providing the claimed job snapshot,
/// progress reporting, and the ability to enqueue downstream jobs.
/// </summary>
public sealed class JobContext {
    private readonly IJobQueueService _queue;
    private readonly Features.IProductFeatures? _features;

    /// <summary>Creates the context for one claimed job.</summary>
    /// <param name="job">Claimed job run.</param>
    /// <param name="queue">Queue used for progress and downstream work.</param>
    /// <param name="features">
    /// Enabled product areas. Downstream jobs owned by a switched-off area are not queued, so a
    /// library scan does not schedule identification or other hidden work.
    /// </param>
    public JobContext(JobRunSnapshot job, IJobQueueService queue, Features.IProductFeatures? features = null) {
        Job = job;
        _queue = queue;
        _features = features;
    }

    private bool IsAllowed(JobType type) =>
        _features is null || Features.ProductFeatureJobPolicy.IsAllowed(_features, type);

    /// <summary>
    /// The claimed job run this handler is processing.
    /// </summary>
    public JobRunSnapshot Job { get; }

    /// <summary>
    /// Reports progress on the running job for dashboard display.
    /// </summary>
    public async Task ReportProgressAsync(int progress, string? message = null, CancellationToken cancellationToken = default) {
        await ThrowIfCancelledAsync(cancellationToken);
        await _queue.UpdateProgressAsync(Job.Id, progress, message, cancellationToken);
    }

    /// <summary>
    /// Enqueues a downstream job, skipping if a pending job already exists for the same type and target.
    /// Returns the snapshot when enqueued, or null when deduplicated away or owned by a switched-off area.
    /// </summary>
    public async Task<JobRunSnapshot?> EnqueueIfNeededAsync(EnqueueJobRequest request, CancellationToken cancellationToken = default) {
        await ThrowIfCancelledAsync(cancellationToken);
        if (!IsAllowed(request.Type)) {
            return null;
        }

        if (Job.GraphId is null &&
            await _queue.HasPendingAsync(request.Type, request.TargetEntityId, cancellationToken)) {
            return null;
        }

        return await _queue.EnqueueChildAsync(Job, request, cancellationToken);
    }

    /// <summary>
    /// Enqueues a downstream job unconditionally.
    /// </summary>
    public async Task<JobRunSnapshot> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken = default) {
        await ThrowIfCancelledAsync(cancellationToken);
        return await _queue.EnqueueChildAsync(Job, request, cancellationToken);
    }

    /// <summary>
    /// Appends a dependency-aware node to this job's graph. The current graph and lane are always inherited;
    /// handlers may describe work and predecessor nodes but cannot redirect a child into another workflow.
    /// </summary>
    public async Task<JobRunSnapshot> AppendNodeAsync(
        GraphJobNodeRequest request,
        CancellationToken cancellationToken = default) {
        await ThrowIfCancelledAsync(cancellationToken);
        return await _queue.AppendChildGraphNodeAsync(Job, request, cancellationToken);
    }

    /// <summary>
    /// Enqueues a batch of downstream jobs in a single round-trip, deduplicating against pending jobs.
    /// Returns the number of jobs actually enqueued.
    /// </summary>
    public async Task<int> EnqueueBatchAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken cancellationToken = default) {
        await ThrowIfCancelledAsync(cancellationToken);
        var allowed = requests.Where(request => IsAllowed(request.Type)).ToArray();
        return allowed.Length == 0 ? 0 : await _queue.EnqueueChildBatchAsync(Job, allowed, cancellationToken);
    }

    private async Task ThrowIfCancelledAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (await _queue.IsRunCancelledAsync(Job.Id, cancellationToken)) {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
