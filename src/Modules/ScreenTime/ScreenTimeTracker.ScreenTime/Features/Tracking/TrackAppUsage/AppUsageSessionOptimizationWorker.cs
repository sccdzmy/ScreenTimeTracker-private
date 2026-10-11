using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public sealed partial class AppUsageSessionOptimizationWorker(UserSettingsSnapshot settingsSnapshot,
    SessionCompactionRunner runner, ILogger<AppUsageSessionOptimizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await settingsSnapshot.GetAsync(stoppingToken);
        using var timer = new PeriodicTimer(settings.AppTracking.UsageSessionOptimizationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                settings = await settingsSnapshot.GetAsync(stoppingToken);
                timer.Period = settings.AppTracking.UsageSessionOptimizationInterval;
                try { await runner.RunAsync(false, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { LogBatchFailed(logger); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session compaction failed; the next interval will retry.")]
    private static partial void LogBatchFailed(ILogger logger);
}
