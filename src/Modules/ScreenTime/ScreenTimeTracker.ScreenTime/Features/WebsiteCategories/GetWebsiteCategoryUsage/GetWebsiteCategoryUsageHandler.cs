using ScreenTimeTracker.ScreenTime.Features.Statistics;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategoryUsage;

public class GetWebsiteCategoryUsageHandler(UserSettingsSnapshot settingsSnapshot,
    ScreenTimeDbContext context)
    : IRequestHandler<GetWebsiteCategoryUsageQuery, List<GetWebsiteCategoryUsageResponseItem>>
{
    public async ValueTask<List<GetWebsiteCategoryUsageResponseItem>> Handle(
        GetWebsiteCategoryUsageQuery request,
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

        IAsyncEnumerable<TimeRange> sessions;

        if (request.IncludedIds is not null)
            sessions = context
                .WebsiteUsageSessions.AsNoTracking()
                .Join(
                    context.Websites.AsNoTracking(),
                    session => session.WebsiteId,
                    website => website.Id,
                    (session, website) => new { session, website }
                )
                .Where(x =>
                    request.IncludedIds.Contains(x.website.WebsiteCategoryId)
                    && timeRange.Start < x.session.EndTime
                    && x.session.StartTime < timeRange.End
                )
                .Select(x => new TimeRange(x.session.StartTime, x.session.EndTime))
                .AsAsyncEnumerable();
        else if (request.ExcludedIds is not null)
            sessions = context
                .WebsiteUsageSessions.AsNoTracking()
                .Join(
                    context.Websites.AsNoTracking(),
                    session => session.WebsiteId,
                    website => website.Id,
                    (session, website) => new { session, website }
                )
                .Where(x =>
                    !request.ExcludedIds.Contains(x.website.WebsiteCategoryId)
                    && timeRange.Start < x.session.EndTime
                    && x.session.StartTime < timeRange.End
                )
                .Select(x => new TimeRange(x.session.StartTime, x.session.EndTime))
                .AsAsyncEnumerable();
        else
        {
            sessions = context
                .WebsiteUsageSessions.AsNoTracking()
                .Where(x => timeRange.Start < x.EndTime && x.StartTime < timeRange.End)
                .Select(x => new TimeRange(x.StartTime, x.EndTime))
                .AsAsyncEnumerable();
        }

        await foreach (var session in sessions.WithCancellation(cancellationToken))
            buckets.Add(session);

        var result = new List<GetWebsiteCategoryUsageResponseItem>(timeBuckets.Count);
        for (int i = 0; i < timeBuckets.Count; i++)
        {
            result.Add(
                new GetWebsiteCategoryUsageResponseItem(
                    timeBuckets[i].Start,
                    timeBuckets[i].End,
                    buckets.Seconds(i)
                )
            );
        }

        return result;
    }

}
