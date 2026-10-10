using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ScreenTimeTracker.BuildingBlocks.Persistence;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Websites;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;

namespace ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

public class ScreenTimeDbContext(DbContextOptions<ScreenTimeDbContext> options,
    UserSettingsSnapshot? settingsSnapshot = null, TrackingIdentityCache? identityCache = null)
    : ModuleDbContext(options, "ScreenTime")
{
    public DbSet<AppUsageSession> AppUsageSessions { get; set; }
    public DbSet<App> Apps { get; set; }
    public DbSet<AppCategory> AppCategories { get; set; }
    public DbSet<WebsiteUsageSession> WebsiteUsageSessions { get; set; }
    public DbSet<Website> Websites { get; set; }
    public DbSet<WebsiteCategory> WebsiteCategories { get; set; }
    public DbSet<UserSettings> UserSettings { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var changes = CaptureInvalidations();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        InvalidateCommitted(changes);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = CaptureInvalidations();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        InvalidateCommitted(changes);
        return result;
    }

    private (bool Settings, bool Apps, bool Websites) CaptureInvalidations()
    {
        var changed = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        return (changed.Any(e => e.Entity is UserSettings), changed.Any(e => e.Entity is App), changed.Any(e => e.Entity is Website));
    }

    private void InvalidateCommitted((bool Settings, bool Apps, bool Websites) changes)
    {
        // Explicit bulk/import transactions publish invalidation in their handler after commit.
        if (Database.CurrentTransaction is not null) return;
        if (changes.Settings) settingsSnapshot?.Invalidate();
        if (changes.Apps) identityCache?.InvalidateApps();
        if (changes.Websites) identityCache?.InvalidateWebsites();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUnixMsConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ScreenTimeDbContext).Assembly);
    }
}

public class DateTimeOffsetToUnixMsConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToUnixMsConverter()
        : base(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)) { }
}
