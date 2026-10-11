using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackWebsiteUsage;

public sealed partial class WebsiteUsageSessionOptimizationWorker(UserSettingsSnapshot settingsSnapshot,
    SessionCompactionRunner runner, ILogger<WebsiteUsageSessionOptimizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await settingsSnapshot.GetAsync(stoppingToken);
        using var timer = new PeriodicTimer(settings.WebsiteTracking.UsageSessionOptimizationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                settings = await settingsSnapshot.GetAsync(stoppingToken);
                timer.Period = settings.WebsiteTracking.UsageSessionOptimizationInterval;
                try { await runner.RunAsync(true, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { LogBatchFailed(logger); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session compaction failed; the next interval will retry.")]
    private static partial void LogBatchFailed(ILogger logger);
}
