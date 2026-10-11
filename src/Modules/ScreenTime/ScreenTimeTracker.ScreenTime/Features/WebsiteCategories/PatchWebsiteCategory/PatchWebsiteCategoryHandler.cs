using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteCategories;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.PatchWebsiteCategory;

public class PatchWebsiteCategoryHandler(TrackingCoordinator coordinator,
    ScreenTimeDbContext context, TimeProvider timeProvider)
    : IRequestHandler<PatchWebsiteCategoryCommand, ErrorOr<Updated>>
{
    public async ValueTask<ErrorOr<Updated>> Handle(
        PatchWebsiteCategoryCommand request,
        CancellationToken cancellationToken
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        WebsiteCategory? websiteCategory = await context.WebsiteCategories.FindAsync(
            [request.WebsiteCategoryId],
            cancellationToken
        );

        if (websiteCategory is null)
            return Error.NotFound(
                code: "WebsiteCategory.NotFound",
                description: "The website category with the specified ID was not found."
            );

        if (request.Name.HasValue)
        {
            var exists = await context.WebsiteCategories.AnyAsync(
                x => x.Id != request.WebsiteCategoryId && x.Name == request.Name.Value,
                cancellationToken
            );
            if (exists)
                return Error.Conflict(
                    "WebsiteCategory.NameAlreadyExists",
                    "An website category with the same name already exists."
                );
        }

        websiteCategory.Update(
            request.Name,
            request.Color,
            request.IconPath,
            timeProvider.GetUtcNow()
        );

        await context.SaveChangesAsync(cancellationToken);
        mutation.Complete();
        return Result.Updated;
    }
}
