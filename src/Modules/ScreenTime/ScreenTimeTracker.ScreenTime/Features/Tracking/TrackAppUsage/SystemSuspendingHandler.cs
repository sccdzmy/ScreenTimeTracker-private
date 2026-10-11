using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public class SystemSuspendingHandler(
    TrackingCoordinator coordinator,
    ActiveAppUsageSessionStore activeSessionStore,
    ScreenTimeDbContext context,
    TimeProvider timeProvider
) : INotificationHandler<SystemSuspendingEvent>
{
    public async ValueTask Handle(
        SystemSuspendingEvent notification,
        CancellationToken cancellationToken
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        var activeSession = activeSessionStore.Current;
        if (activeSession is null)
            return;

        var now = timeProvider.GetUtcNow();
        await context.PersistActiveSessionAsync(activeSession, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        activeSessionStore.Current = null;
        mutation.Complete();

        return;
    }
}
