using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Apps.DeleteApp;

public class DeleteAppHandler(
    TrackingCoordinator coordinator, TrackingIdentityCache identityCache,
    ScreenTimeDbContext context,
    ActiveAppUsageSessionStore activeSessionStore
) : IRequestHandler<DeleteAppCommand, ErrorOr<Deleted>>
{
    public async ValueTask<ErrorOr<Deleted>> Handle(
        DeleteAppCommand request,
        CancellationToken cancellationToken
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        App? app = await context.Apps.FindAsync([request.AppId], cancellationToken);
        if (app is null)
            return Error.NotFound(
                code: "App.NotFound",
                description: "The app with the specified ID was not found."
            );

        if (app.IsSystem)
            return Error.Conflict(
                code: "App.SystemAppCannotBeDeleted",
                description: "System app cannot be deleted."
            );

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // 把所有 App 的数据都删除
        await context
            .AppUsageSessions.Where(log => log.AppId == request.AppId)
            .ExecuteDeleteAsync(cancellationToken);

        context.Apps.Remove(app);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        identityCache.InvalidateAll();
        if (activeSessionStore.Current?.AppId == request.AppId)
            activeSessionStore.Current = null;
        mutation.Complete();
        return Result.Deleted;
    }
}
