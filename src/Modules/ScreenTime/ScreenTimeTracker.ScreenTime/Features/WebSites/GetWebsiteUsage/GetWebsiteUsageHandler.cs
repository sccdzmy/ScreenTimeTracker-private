using ScreenTimeTracker.ScreenTime.Features.Statistics;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteUsage;

public class GetWebsiteUsageHandler(UserSettingsSnapshot settingsSnapshot,
    ScreenTimeDbContext context)
    : IRequestHandler<GetWebsiteUsageQuery, List<GetWebsiteUsageResponseItem>>
{
    public async ValueTask<List<GetWebsiteUsageResponseItem>> Handle(
        GetWebsiteUsageQuery request,
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

        var buckets = new UsageBuckets(request.StartDate, request.EndDate, (UsageBucketGranularity)request.Granularity,
            request.TimeZoneInfo, settings.TimeBoundary.DayCutoffHour);
        var timeBuckets = buckets.Ranges;
        if (timeBuckets.Count == 0) return [];

        var sessionQuery = context
            .WebsiteUsageSessions.AsNoTracking()
            .Where(x => timeRange.Start < x.EndTime && x.StartTime < timeRange.End);

        if (request.IncludedIds is not null)
            sessionQuery = sessionQuery.Where(x => request.IncludedIds.Contains(x.WebsiteId));
        else if (request.ExcludedIds is not null)
            sessionQuery = sessionQuery.Where(x => !request.ExcludedIds.Contains(x.WebsiteId));

        var sessions = sessionQuery
            .Select(x => new TimeRange(x.StartTime, x.EndTime))
            .AsAsyncEnumerable();

        await foreach (var session in sessions.WithCancellation(cancellationToken))
            buckets.Add(session);

        var result = new List<GetWebsiteUsageResponseItem>(timeBuckets.Count);
        for (int i = 0; i < timeBuckets.Count; i++)
        {
            result.Add(
                new GetWebsiteUsageResponseItem(
                    timeBuckets[i].Start,
                    timeBuckets[i].End,
                    buckets.Seconds(i)
                )
            );
        }

        return result;
    }

}
