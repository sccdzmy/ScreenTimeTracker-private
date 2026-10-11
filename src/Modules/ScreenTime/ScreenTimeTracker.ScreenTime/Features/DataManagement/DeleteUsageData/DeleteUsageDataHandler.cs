using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.DataManagement.DeleteUsageData;

public class DeleteUsageDataHandler(TrackingCoordinator coordinator, TrackingIdentityCache identityCache, ActiveAppUsageSessionStore activeSessions, TimeProvider timeProvider,
    UserSettingsSnapshot settingsSnapshot,
    ScreenTimeDbContext context)
    : IRequestHandler<DeleteUsageDataCommand>
{
    public async ValueTask<Unit> Handle(
        DeleteUsageDataCommand request,
        CancellationToken cancellationToken
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        var settings = await settingsSnapshot.GetAsync(cancellationToken);
        var dayCutoffHour = settings.TimeBoundary.DayCutoffHour;
        var timeRange = LogicalDay.CalculateUtcWindow(
            request.StartDate,
            request.EndDate,
            settings.TimeBoundary.DayCutoffHour,
            request.TimeZoneInfo
        );

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context
            .AppUsageSessions.Where(x => timeRange.Start < x.EndTime && x.StartTime < timeRange.End)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        identityCache.InvalidateAll();
        if (activeSessions.Current is { } current && current.StartTime < timeRange.End && timeRange.Start < timeProvider.GetUtcNow())
            activeSessions.Current = new ActiveAppUsageSessionState(current.AppId, timeProvider.GetUtcNow());
        mutation.Complete();
        return Unit.Value;
    }
}
