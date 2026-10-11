using ScreenTimeTracker.ScreenTime.Features.Statistics;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.AppCategories.GetAppCategoryUsageDistribution;

public class GetAppCategoryUsageDistributionHandler(UserSettingsSnapshot settingsSnapshot,
    ScreenTimeDbContext context)
    : IRequestHandler<GetAppCategoryUsageDistributionQuery, GetAppCategoryUsageDistributionResponse>
{
    public async ValueTask<GetAppCategoryUsageDistributionResponse> Handle(
        GetAppCategoryUsageDistributionQuery request,
        CancellationToken cancellationToken
    )
    {
        var settings = await settingsSnapshot.GetAsync(cancellationToken);
        var timeRange = LogicalDay.CalculateUtcWindow(
            request.StartDate,
            request.EndDate,
            settings.TimeBoundary.DayCutoffHour,
            request.TimeZoneInfo
        );

        var appCategoryQuery = context.AppCategories.AsNoTracking();

        if (request.IncludedIds is not null)
            appCategoryQuery = appCategoryQuery.Where(c => request.IncludedIds.Contains(c.Id));
        else if (request.ExcludedIds is not null)
            appCategoryQuery = appCategoryQuery.Where(c => !request.ExcludedIds.Contains(c.Id));

        var appCategoryMap = await appCategoryQuery
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Color,
                c.IconPath,
                c.IconPathLastUpdatedAt,
            })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (appCategoryMap.Count == 0)
            return new GetAppCategoryUsageDistributionResponse([], 0, 0, 0, 0);

        var appCategoryIds = appCategoryMap.Keys;

        var durationByAppCategoryId = await UsageDistributionQueries.ReadAsync(
            context, UsageIdentityKind.AppCategory, appCategoryIds, timeRange, cancellationToken);

        int totalCount = durationByAppCategoryId.Count;

        long totalDurationSeconds = durationByAppCategoryId.Values.Sum(x => (long)x.TotalSeconds);

        var topNItems = durationByAppCategoryId
            .OrderByDescending(x => x.Value)
            .Take(request.TopN)
            .Select(kvp =>
            {
                var category = appCategoryMap[kvp.Key];
                return new AppCategoryUsageDistributionItem(
                    Id: kvp.Key,
                    Name: category.Name,
                    Color: category.Color,
                    IconPath: category.IconPath,
                    IconPathLastUpdatedAt: category.IconPathLastUpdatedAt,
                    DurationSeconds: (long)kvp.Value.TotalSeconds
                );
            })
            .ToList();

        int othersCount = totalCount - topNItems.Count;
        long topNDurationSeconds = topNItems.Sum(x => x.DurationSeconds);
        long othersDurationSeconds = Math.Max(0, totalDurationSeconds - topNDurationSeconds);

        return new GetAppCategoryUsageDistributionResponse(
            Items: topNItems,
            TotalCount: totalCount,
            TotalDurationSeconds: totalDurationSeconds,
            OthersCount: othersCount,
            OthersDurationSeconds: othersDurationSeconds
        );
    }
}
