using ScreenTimeTracker.ScreenTime.Features.Statistics;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Apps.GetAppUsageDistribution;

public class GetAppUsageDistributionHandler(UserSettingsSnapshot settingsSnapshot,
    ScreenTimeDbContext context)
    : IRequestHandler<GetAppUsageDistributionQuery, GetAppUsageDistributionResponse>
{
    public async ValueTask<GetAppUsageDistributionResponse> Handle(
        GetAppUsageDistributionQuery request,
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

        var appQuery = context.Apps.AsNoTracking();

        if (request.IncludedIds is not null)
            appQuery = appQuery.Where(c => request.IncludedIds.Contains(c.Id));
        else if (request.ExcludedIds is not null)
            appQuery = appQuery.Where(c => !request.ExcludedIds.Contains(c.Id));

        var appMap = await appQuery
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Color,
                c.IconPath,
                c.IconPathLastUpdatedAt,
            })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (appMap.Count == 0)
            return new GetAppUsageDistributionResponse([], 0, 0, 0, 0);

        var appIds = appMap.Keys;

        var durationByAppId = await UsageDistributionQueries.ReadAsync(
            context, UsageIdentityKind.App, appIds, timeRange, cancellationToken);

        int totalCount = durationByAppId.Count;

        long totalDurationSeconds = durationByAppId.Values.Sum(x => (long)x.TotalSeconds);

        var topNItems = durationByAppId
            .OrderByDescending(x => x.Value)
            .Take(request.TopN)
            .Select(kvp =>
            {
                var app = appMap[kvp.Key];
                return new AppUsageDistributionItem(
                    Id: kvp.Key,
                    Name: app.Name,
                    Color: app.Color,
                    IconPath: app.IconPath,
                    IconPathLastUpdatedAt: app.IconPathLastUpdatedAt,
                    DurationSeconds: (long)kvp.Value.TotalSeconds
                );
            })
            .ToList();

        int othersCount = totalCount - topNItems.Count;
        long topNDurationSeconds = topNItems.Sum(x => x.DurationSeconds);
        long othersDurationSeconds = Math.Max(0, totalDurationSeconds - topNDurationSeconds);

        return new GetAppUsageDistributionResponse(
            Items: topNItems,
            TotalCount: totalCount,
            TotalDurationSeconds: totalDurationSeconds,
            OthersCount: othersCount,
            OthersDurationSeconds: othersDurationSeconds
        );
    }
}
