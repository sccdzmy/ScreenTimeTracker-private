using Microsoft.Extensions.Logging;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public partial class ActiveAppUsageSessionAutoSaver(
    UserSettingsSnapshot settingsSnapshot,
    TrackingCoordinator coordinator,
    Microsoft.Extensions.Logging.ILogger<ActiveAppUsageSessionAutoSaver> logger,
    IServiceScopeFactory scopeFactory,
    ActiveAppUsageSessionStore activeSessionStore,
    TimeProvider timeProvider
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = await GetAutoSaveIntervalAsync(stoppingToken);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var latestInterval = await GetAutoSaveIntervalAsync(stoppingToken);
                if (latestInterval != interval)
                {
                    interval = latestInterval;
                    timer.Period = interval;
                }

                try { await SaveOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogSaveFailed(logger, ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session save failed ({ErrorType}); retrying next interval.")]
    private static partial void LogSaveFailed(ILogger logger, string errorType);

    public async Task SaveOnceAsync(CancellationToken cancellationToken)
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        var session = activeSessionStore.Current;
        if (session is null) return;
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        await context.PersistActiveSessionAsync(session, timeProvider.GetUtcNow(), cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        mutation.Complete();
    }

    private async Task<TimeSpan> GetAutoSaveIntervalAsync(CancellationToken cancellationToken)
    {
        var userSettings = await settingsSnapshot.GetAsync(cancellationToken);
        return userSettings.AppTracking.ActiveUsageSessionAutoSaveInterval;
    }
}
