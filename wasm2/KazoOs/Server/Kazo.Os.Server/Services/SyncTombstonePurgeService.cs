namespace Kazo.Os.Server.Services;

/// <summary>
/// Purges the sync tombstones older than <see cref="SyncService.TombstoneRetention"/> (ADR 0101
/// section 1): once at startup, then every <see cref="Period"/>, like a nightly maintenance job.
/// </summary>
public sealed partial class SyncTombstonePurgeService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SyncTombstonePurgeService> logger) : BackgroundService
{
    /// <summary>Delay between two purges.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(24);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                ISyncService sync = scope.ServiceProvider.GetRequiredService<ISyncService>();
                int purged = await sync.PurgeExpiredTombstonesAsync(time.GetUtcNow(), stoppingToken);
                if (purged > 0)
                {
                    LogPurged(logger, purged, SyncService.TombstoneRetention.Days);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Maintenance must never stop the server: try again at the next period.
                LogPurgeFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Period, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} sync record(s) deleted more than {Days} days ago.")]
    private static partial void LogPurged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync tombstone purge failed.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
