using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Prismedia.Application.Files;

/// <summary>
/// Periodically deletes resumable uploads that stopped receiving bytes, so abandoned partial files
/// do not keep using library disk space. Runs shortly after startup and then hourly.
/// </summary>
internal sealed class FileUploadSessionPruner(
    IServiceScopeFactory scopeFactory,
    ILogger<FileUploadSessionPruner> logger) : BackgroundService {
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested) {
                await PruneOnceAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal hosted-service shutdown.
        }
    }

    private async Task PruneOnceAsync(CancellationToken cancellationToken) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var removed = await scope.ServiceProvider.GetRequiredService<FilesService>()
                .PruneAbandonedUploadsAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (removed > 0) {
                logger.LogInformation("Removed {Count} abandoned Files uploads.", removed);
            }
        } catch (Exception error) when (error is not OperationCanceledException) {
            logger.LogWarning(error, "Abandoned Files upload cleanup failed; it will retry later.");
        }
    }
}
