using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.BuildingBlocks.Security;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.Desktop.Security;

public static class NativeQueryEndpoints
{
    // Explicit opt-in routes preserve the response shape of every existing endpoint.
    public static void MapNativeQueries(this WebApplication app)
    {
        foreach (string kind in new[] { "apps", "websites" })
        app.MapGet($"/api/screen-time/{kind}/page", async (int? offset, int? pageSize, string? search, ScreenTimeDbContext db, CancellationToken ct) =>
        {
            int skip = offset ?? 0, take = pageSize ?? 100;
            if (skip < 0 || skip > 1_000_000 || take is < 1 or > 500 || search?.Length > 200) return Results.BadRequest();
            if (kind == "apps")
            {
                var query = db.Apps.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search) || x.ProcessName.Contains(search));
                var rows = await query.OrderBy(x => x.Id).Skip(skip).Take(take + 1).ToListAsync(ct);
                return Results.Ok(new { items = rows.Take(take).Select(x => new { x.Id, x.Name, x.Color, iconId = ManagedIcons.Id(x.IconPath), x.IsSystem, x.AppCategoryId, x.ProcessName, x.AllowMetadataAutoRefresh }), nextOffset = rows.Count > take ? (int?)(skip + take) : null });
            }
            if (kind == "websites")
            {
                var query = db.Websites.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search) || x.Host.Contains(search));
                var rows = await query.OrderBy(x => x.Id).Skip(skip).Take(take + 1).ToListAsync(ct);
                return Results.Ok(new { items = rows.Take(take).Select(x => new { x.Id, x.Name, x.Color, iconId = ManagedIcons.Id(x.IconPath), x.IsSystem, x.WebsiteCategoryId, x.Host, x.AllowMetadataAutoRefresh }), nextOffset = rows.Count > take ? (int?)(skip + take) : null });
            }
            return Results.NotFound();
        }).WithName("PagedManagement" + kind).WithSummary("Opt-in bounded application or website page; legacy lists remain unchanged.");
        app.MapGet("/api/screen-time/usage/{kind}/timeline/page", async (string kind, DateOnly startDate, DateOnly endDate, string timeZoneId, [FromQuery] Guid[]? includedIds, [FromQuery] Guid[]? excludedIds, int? offset, int? pageSize, ScreenTimeDbContext db, CancellationToken ct) =>
        {
            int skip = offset ?? 0, take = pageSize ?? 500;
            if (skip < 0 || skip > 1_000_000 || take is < 1 or > 1000 || startDate != endDate || includedIds?.Length > 500 || excludedIds?.Length > 500) return Results.BadRequest();
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Results.BadRequest(); }
            var cutoff = await db.UserSettings.Select(x => x.TimeBoundary.DayCutoffHour).SingleAsync(ct);
            var window = LogicalDay.CalculateUtcWindow(startDate, endDate, cutoff, zone);
            if (kind is "apps" or "app-categories")
            {
                var query = from session in db.AppUsageSessions.AsNoTracking()
                            join entity in db.Apps.AsNoTracking() on session.AppId equals entity.Id
                            join category in db.AppCategories.AsNoTracking() on entity.AppCategoryId equals category.Id
                            where window.Start < session.EndTime && session.StartTime < window.End
                            select new { session.Id, ObjectId = kind == "apps" ? entity.Id : category.Id, Name = kind == "apps" ? entity.Name : category.Name, Color = kind == "apps" ? entity.Color : category.Color, session.StartTime, session.EndTime };
                if (includedIds is { Length: > 0 }) query = query.Where(x => includedIds.Contains(x.ObjectId));
                else if (excludedIds is { Length: > 0 }) query = query.Where(x => !excludedIds.Contains(x.ObjectId));
                var rows = await query.OrderBy(x => x.StartTime).ThenBy(x => x.Id).Skip(skip).Take(take + 1).ToListAsync(ct);
                return Results.Ok(new { items = rows.Take(take).Select(x => new { id = x.ObjectId, x.Name, x.Color, startTime = x.StartTime < window.Start ? window.Start : x.StartTime, endTime = x.EndTime > window.End ? window.End : x.EndTime }), nextOffset = rows.Count > take ? (int?)(skip + take) : null });
            }
            if (kind is "websites" or "website-categories")
            {
                var query = from session in db.WebsiteUsageSessions.AsNoTracking()
                            join entity in db.Websites.AsNoTracking() on session.WebsiteId equals entity.Id
                            join category in db.WebsiteCategories.AsNoTracking() on entity.WebsiteCategoryId equals category.Id
                            where window.Start < session.EndTime && session.StartTime < window.End
                            select new { session.Id, ObjectId = kind == "websites" ? entity.Id : category.Id, Name = kind == "websites" ? entity.Name : category.Name, Color = kind == "websites" ? entity.Color : category.Color, session.StartTime, session.EndTime };
                if (includedIds is { Length: > 0 }) query = query.Where(x => includedIds.Contains(x.ObjectId));
                else if (excludedIds is { Length: > 0 }) query = query.Where(x => !excludedIds.Contains(x.ObjectId));
                var rows = await query.OrderBy(x => x.StartTime).ThenBy(x => x.Id).Skip(skip).Take(take + 1).ToListAsync(ct);
                return Results.Ok(new { items = rows.Take(take).Select(x => new { id = x.ObjectId, x.Name, x.Color, startTime = x.StartTime < window.Start ? window.Start : x.StartTime, endTime = x.EndTime > window.End ? window.End : x.EndTime }), nextOffset = rows.Count > take ? (int?)(skip + take) : null });
            }
            return Results.NotFound();
        }).WithName("PagedTimeline").WithSummary("One logical day of exact clipped intervals with bounded pages, ordered by start and session ID.");
    }
}
