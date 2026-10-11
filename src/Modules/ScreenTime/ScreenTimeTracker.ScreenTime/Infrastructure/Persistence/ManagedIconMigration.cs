using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.BuildingBlocks.Security;

namespace ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

internal static class ManagedIconMigration
{
    internal static async Task RunAsync(ScreenTimeDbContext database, CancellationToken ct)
    {
        var objects = new List<object>();
        objects.AddRange(await database.Apps.ToListAsync(ct));
        objects.AddRange(await database.Websites.ToListAsync(ct));
        objects.AddRange(await database.AppCategories.ToListAsync(ct));
        objects.AddRange(await database.WebsiteCategories.ToListAsync(ct));
        foreach (var entity in objects)
        {
            var property = database.Entry(entity).Property("IconPath");
            if (property.CurrentValue is not string path || ManagedIcons.IsSafePath(path)) continue;
            string? replacement = null;
            if (ManagedIcons.IsSafePath(path, legacy: true) && File.Exists(path) && new FileInfo(path).Length <= ManagedIcons.MaxBytes)
                try { replacement = await ManagedIcons.SaveAsync(await File.ReadAllBytesAsync(path, ct), ct); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            property.CurrentValue = replacement;
            database.Entry(entity).Property("IconPathLastUpdatedAt").CurrentValue = DateTimeOffset.UtcNow;
        }
        await database.SaveChangesAsync(ct);
    }
}
