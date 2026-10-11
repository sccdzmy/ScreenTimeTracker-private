using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ScreenTimeTracker.ScreenTime.Features.Tracking.TrackWebsiteUsage;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

var results = new List<object>();
var runCount = args.Contains("--single-run", StringComparer.Ordinal) ? 1 : 3;
for (var run = 1; run <= runCount; run++)
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    var counter = new SqlCounter();
    var services = new ServiceCollection().AddLogging();
    services.AddSingleton<TimeProvider>(TimeProvider.System);
    services.AddDbContext<ScreenTimeDbContext>(o => o.UseSqlite(connection).AddInterceptors(counter));
    services.AddSingleton<UserIdleStore>(); services.AddSingleton<SystemSuspendStore>();
    services.AddSingleton<ActiveWebsiteUsageSessionStore>(); services.AddSingleton<ActiveAppUsageSessionStore>();
    var assembly = typeof(ScreenTimeDbContext).Assembly;
    foreach (var name in new[] { "TrackingCoordinator", "TrackingIdentityCache", "UserSettingsSnapshot" })
    {
        var type = assembly.GetType("ScreenTimeTracker.ScreenTime.Features.Tracking." + name);
        if (type is not null) services.AddSingleton(type);
    }
    services.AddSingleton<WebsiteActivityProcessor>();
    await using var provider = services.BuildServiceProvider();
    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        await db.Database.EnsureCreatedAsync(); db.UserSettings.Add(UserSettings.CreateDefault()); await db.SaveChangesAsync();
    }
    var snapshotType = assembly.GetType("ScreenTimeTracker.ScreenTime.Features.Tracking.UserSettingsSnapshot");
    var snapshot = snapshotType is null ? null : provider.GetRequiredService(snapshotType);
    var readSettings = snapshotType?.GetMethod("GetAsync");
    if (readSettings is not null) await (Task)readSettings.Invoke(snapshot, [CancellationToken.None])!; // Keep startup outside measurements.
    var processor = provider.GetRequiredService<WebsiteActivityProcessor>();
    var process = typeof(WebsiteActivityProcessor).GetMethod("ProcessActivityReportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    counter.Reset(); GC.Collect(); GC.WaitForPendingFinalizers();
    var allocated = GC.GetTotalAllocatedBytes(true); using var current = Process.GetCurrentProcess();
    var cpu = current.TotalProcessorTime; var watch = Stopwatch.StartNew(); long peakWorking = 0;
    var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    for (var tick = 0; tick < 2400; tick++)
    {
        now += TimeSpan.FromMilliseconds(1500);
        await (Task)process.Invoke(processor, [new WebsiteActivityReport("fictional.example", "Fictional", TimeSpan.FromMilliseconds(1500), now), CancellationToken.None])!;
        if ((tick + 1) % 10 == 0)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            var session = provider.GetRequiredService<ActiveWebsiteUsageSessionStore>().Current!;
            await db.PersistActiveSessionAsync(session, session.LastActiveAt, CancellationToken.None); await db.SaveChangesAsync();
        }
        // 360 idle polls + 240 polls for each of the two savers over one simulated hour.
        var polls = (tick + 1) % 20 == 0 ? 7 : 0;
        for (var i = 0; i < polls; i++)
        {
            if (snapshot is not null) await (Task)readSettings!.Invoke(snapshot, [CancellationToken.None])!;
            else { using var scope = provider.CreateScope(); await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().UserSettings.AsNoTracking().SingleAsync(); }
        }
        if (tick % 100 == 0) { current.Refresh(); peakWorking = Math.Max(peakWorking, current.WorkingSet64); }
    }
    watch.Stop(); current.Refresh(); peakWorking = Math.Max(peakWorking, current.WorkingSet64);
    results.Add(new { run, sql = counter.Total, settingsQueries = counter.Settings, websiteQueries = counter.Websites,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, cpuMilliseconds = (current.TotalProcessorTime - cpu).TotalMilliseconds,
        elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, sampledPeakWorkingBytes = peakWorking, peakProcessWorkingBytes = current.PeakWorkingSet64 });
}
Console.WriteLine(JsonSerializer.Serialize(results, ReportOptions.Value));

internal static class ReportOptions { public static readonly JsonSerializerOptions Value = new() { WriteIndented = true }; }
internal sealed class SqlCounter : DbCommandInterceptor
{
    public int Total, Settings, Websites;
    public void Reset() => (Total, Settings, Websites) = (0, 0, 0);
    private void Count(DbCommand command)
    {
        Total++;
        if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("UserSettings", StringComparison.Ordinal)) Settings++;
        if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("Websites", StringComparison.Ordinal)) Websites++;
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Count(command); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Count(command); return ValueTask.FromResult(result); }
}
