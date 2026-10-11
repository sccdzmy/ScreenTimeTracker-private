using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Statistics;

public enum UsageIdentityKind { App, Website, AppCategory, WebsiteCategory }

public static class UsageDistributionQueries
{
    public static async Task<Dictionary<Guid, TimeSpan>> ReadAsync(ScreenTimeDbContext db,
        UsageIdentityKind kind, IEnumerable<Guid> ids, TimeRange window, CancellationToken ct)
    {
        // SQL identifiers come only from this fixed mapping; all values are parameters.
        var (table, ownerTable, ownerKey, groupKey) = kind switch
        {
            UsageIdentityKind.App => ("ScreenTime_AppUsageSessions", "ScreenTime_Apps", "AppId", "Id"),
            UsageIdentityKind.AppCategory => ("ScreenTime_AppUsageSessions", "ScreenTime_Apps", "AppId", "AppCategoryId"),
            UsageIdentityKind.Website => ("ScreenTime_WebsiteUsageSessions", "ScreenTime_Websites", "WebsiteId", "Id"),
            UsageIdentityKind.WebsiteCategory => ("ScreenTime_WebsiteUsageSessions", "ScreenTime_Websites", "WebsiteId", "WebsiteCategoryId"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var sql = $"""
            SELECT o."{groupKey}" AS "IdentityId",
                   SUM(MAX(0, MIN(s."EndTime", $end) - MAX(s."StartTime", $start))) AS "Milliseconds"
            FROM "{table}" AS s JOIN "{ownerTable}" AS o ON o."Id" = s."{ownerKey}"
            WHERE s."EndTime" > $start AND s."StartTime" < $end
              AND o."{groupKey}" IN (SELECT value FROM json_each($ids))
            GROUP BY o."{groupKey}"
            HAVING SUM(MAX(0, MIN(s."EndTime", $end) - MAX(s."StartTime", $start))) > 0
            """;
        var rows = await db.Database.SqlQueryRaw<UsageDurationRow>(sql,
            new SqliteParameter("$start", window.Start.ToUnixTimeMilliseconds()),
            new SqliteParameter("$end", window.End.ToUnixTimeMilliseconds()),
            new SqliteParameter("$ids", JsonSerializer.Serialize(ids.Select(id => id.ToString().ToUpperInvariant()))))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.IdentityId,
            row => TimeSpan.FromTicks(checked(row.Milliseconds * TimeSpan.TicksPerMillisecond)));
    }
}

public sealed class UsageDurationRow
{
    public Guid IdentityId { get; set; }
    public long Milliseconds { get; set; }
}
