using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ScreenTimeTracker.BuildingBlocks.Security;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Websites;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Websites.RefreshWebsiteMetadata;

public class RefreshWebsiteMetadataHandler(TrackingCoordinator coordinator,
    ScreenTimeDbContext context, TimeProvider timeProvider)
    : IRequestHandler<RefreshWebsiteMetadataCommand, ErrorOr<Updated>>
{
    public async ValueTask<ErrorOr<Updated>> Handle(
        RefreshWebsiteMetadataCommand request,
        CancellationToken cancellationToken
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        Website? website = await context.Websites.FindAsync([request.Id], cancellationToken);
        if (website is null)
            return Error.NotFound(
                code: "Website.NotFound",
                description: "The website with the specified ID was not found."
            );

        if (!website.AllowMetadataAutoRefresh)
            return Error.Conflict("Website.MetadataLocked", "Automatic metadata refresh is disabled.");
        var iconPath = request.Icon is null ? website.IconPath : await ManagedIcons.SaveAsync(request.Icon.Data, cancellationToken);

        website.RefreshMetadata(timeProvider.GetUtcNow(), iconPath);

        await context.SaveChangesAsync(cancellationToken);
        mutation.Complete();
        return Result.Updated;
    }

}
