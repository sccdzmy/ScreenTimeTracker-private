using ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Websites;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;
using AppRank = ScreenTimeTracker.ScreenTime.Features.Apps.GetAppUsageDistribution;
using WebsiteRank = ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteUsageDistribution;
using AppCategoryRank = ScreenTimeTracker.ScreenTime.Features.AppCategories.GetAppCategoryUsageDistribution;
using WebsiteCategoryRank = ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategoryUsageDistribution;
using AppStats = ScreenTimeTracker.ScreenTime.Features.Apps.GetAppUsage;
using WebsiteStats = ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteUsage;
using AppCategoryStats = ScreenTimeTracker.ScreenTime.Features.AppCategories.GetAppCategoryUsage;
using WebsiteCategoryStats = ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategoryUsage;

var count = args.Length == 0 ? 10000 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
var temporary = Path.Combine(Path.GetTempPath(), "stt-history-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var priorDirectory = Environment.CurrentDirectory;
Environment.CurrentDirectory = temporary;
try
{
    var probe = new Probe(); var clock = new BenchmarkClock();
    var services = new ServiceCollection().AddLogging();
    services.AddSingleton<TimeProvider>(clock);
    services.AddDbContext<ScreenTimeDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(temporary, "synthetic.db") + ";Pooling=False").AddInterceptors(probe));
    services.AddSingleton<ActiveAppUsageSessionStore>(); services.AddSingleton<ActiveWebsiteUsageSessionStore>();
    services.AddSingleton<TrackingCoordinator>(); services.AddSingleton<TrackingIdentityCache>(); services.AddSingleton<UserSettingsSnapshot>();
    var assembly = typeof(ScreenTimeDbContext).Assembly;
    var runnerType = assembly.GetType("ScreenTimeTracker.ScreenTime.Features.Tracking.SessionCompactionRunner");
    var gateType = assembly.GetType("ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData.ExportGenerationGate");
    if (runnerType is not null) services.AddSingleton(runnerType);
    if (gateType is not null) services.AddSingleton(gateType);
    await using var provider = services.BuildServiceProvider();
    var begin = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        await db.Database.EnsureCreatedAsync();
        var settings = UserSettings.CreateDefault(); settings.Update(timeBoundary: new TimeBoundarySettings(0)); db.UserSettings.Add(settings);
        var apps = Enumerable.Range(0, 20).Select(i => App.CreateDiscovered(begin, "Fictional", "synthetic-" + i)).ToArray();
        var sites = Enumerable.Range(0, 20).Select(i => Website.CreateDiscovered(begin, "Fictional", "synthetic-" + i + ".example")).ToArray();
        db.Apps.AddRange(apps); db.Websites.AddRange(sites); await db.SaveChangesAsync();
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (table, column, ids) in new[] {
            ("ScreenTime_AppUsageSessions", "AppId", apps.Select(a => a.Id)),
            ("ScreenTime_WebsiteUsageSessions", "WebsiteId", sites.Select(w => w.Id)) })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"""
                WITH RECURSIVE seq(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM seq WHERE i+1 < $count)
                INSERT INTO {table} (Id, {column}, StartTime, EndTime, IsOptimized)
                SELECT printf('%08X-0000-0000-0000-%012X', i % 20, i),
                    json_extract($ids, '$[' || (i % 20) || ']'), $start+i*15000, $start+i*15000+8001, 0 FROM seq
                """;
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$start", begin.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.ToString().ToUpperInvariant())));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
    var snapshot = provider.GetRequiredService<UserSettingsSnapshot>(); await snapshot.GetAsync();
    var from = DateOnly.FromDateTime(begin.UtcDateTime);
    var to = DateOnly.FromDateTime(begin.AddMilliseconds(count * 15000L).UtcDateTime);
    var measures = new List<object>();
    measures.Add(await Measure("statistics", async () =>
    {
        using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        await new AppRank.GetAppUsageDistributionHandler(snapshot, db).Handle(new(from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new WebsiteRank.GetWebsiteUsageDistributionHandler(snapshot, db).Handle(new(from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new AppCategoryRank.GetAppCategoryUsageDistributionHandler(snapshot, db).Handle(new(from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new WebsiteCategoryRank.GetWebsiteCategoryUsageDistributionHandler(snapshot, db).Handle(new(from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new AppStats.GetAppUsageHandler(snapshot, db).Handle(new(AppStats.UsageGranularity.Day, from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new WebsiteStats.GetWebsiteUsageHandler(snapshot, db).Handle(new(WebsiteStats.UsageGranularity.Day, from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new AppCategoryStats.GetAppCategoryUsageHandler(snapshot, db).Handle(new(AppCategoryStats.UsageGranularity.Day, from, to, TimeZoneInfo.Utc), CancellationToken.None);
        await new WebsiteCategoryStats.GetWebsiteCategoryUsageHandler(snapshot, db).Handle(new(WebsiteCategoryStats.UsageGranularity.Day, from, to, TimeZoneInfo.Utc), CancellationToken.None);
    }, probe, provider, observeSaves: false));
    measures.Add(await Measure("export", async () =>
    {
        using var scope = provider.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<ExportDataHandler>(scope.ServiceProvider);
        object? result = await handler.Handle(new(), CancellationToken.None);
        var destination = Path.Combine(temporary, "download.json");
        try
        {
            await using var stream = File.Create(destination);
            if (result is IAsyncDisposable disposable)
            {
                await using (disposable)
                {
                    await using var input = (Stream)result.GetType().GetMethod("Open")!.Invoke(result, null)!;
                    await input.CopyToAsync(stream);
                }
            }
            else await JsonSerializer.SerializeAsync(stream, result, ReportOptions.Web);
        }
        finally { File.Delete(destination); }
    }, probe, provider, observeSaves: true));
    measures.Add(await Measure("compaction", async () =>
    {
        if (runnerType is not null)
        {
            var runner = provider.GetRequiredService(runnerType); var run = runnerType.GetMethod("RunAsync")!;
            for (var kind = 0; kind < 2; kind++)
            {
                var website = kind == 1;
                while (true)
                {
                    await (Task)run.Invoke(runner, [website, CancellationToken.None, int.MaxValue])!;
                    using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
                    var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(1) - (await snapshot.GetAsync()).IdleDetection.InactivityThreshold;
                    if (website ? !await db.WebsiteUsageSessions.AnyAsync(s => !s.IsOptimized && s.EndTime <= cutoff) : !await db.AppUsageSessions.AnyAsync(s => !s.IsOptimized && s.EndTime <= cutoff)) break;
                    await Task.Yield(); // Compress scheduled rounds, but retain each round's budget and lock releases.
                }
            }
        }
        else
        {
            for (var kind = 0; kind < 2; kind++) await OriginalCompaction(provider, kind == 1);
        }
    }, probe, provider, observeSaves: true));
    Console.WriteLine(JsonSerializer.Serialize(new { sessionsPerKind = count, measures }, ReportOptions.Indented));
}
finally { Environment.CurrentDirectory = priorDirectory; Directory.Delete(temporary, true); }

static async Task<object> Measure(string name, Func<Task> work, Probe probe, ServiceProvider provider, bool observeSaves)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); probe.Reset();
    var allocated = GC.GetTotalAllocatedBytes(true); using var process = Process.GetCurrentProcess();
    var cpu = ReadCpu(); var timer = Stopwatch.StartNew();
    var waits = new List<double>(); var saves = new List<double>();
    var task = Task.Run(work); long peak = 0;
    while (!task.IsCompleted)
    {
        process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
        if (observeSaves)
        {
            var wait = Stopwatch.StartNew();
            using var lease = await provider.GetRequiredService<TrackingCoordinator>().EnterAsync(CancellationToken.None);
            waits.Add(wait.Elapsed.TotalMilliseconds);
            var save = Stopwatch.StartNew();
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            // The first attempt inserts a fictional session. With fixed fake time, later attempts
            // measure unchanged-save lookup and coordinator contention, not sustained writes.
            var session = new ActiveAppUsageSessionState(App.UnknownAppId, provider.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(-1));
            await db.PersistActiveSessionAsync(session, provider.GetRequiredService<TimeProvider>().GetUtcNow(), CancellationToken.None);
            await db.SaveChangesAsync(); lease.Complete(); saves.Add(save.Elapsed.TotalMilliseconds);
        }
        await Task.Delay(10);
    }
    await task; timer.Stop(); process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
    return new { name, sqlCommands = probe.Commands, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
        cpuMilliseconds = (ReadCpu() - cpu).TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, sampledPeakWorkingBytes = peak,
        peakProcessWorkingBytes = process.PeakWorkingSet64, saveSamples = saves.Count,
        maxCoordinatorWaitMilliseconds = waits.Count == 0 ? 0 : waits.Max(),
        maxSaveMilliseconds = saves.Count == 0 ? 0 : saves.Max() };
}

static TimeSpan ReadCpu()
{
    using var process = Process.GetCurrentProcess();
    return process.TotalProcessorTime;
}

static async Task OriginalCompaction(ServiceProvider provider, bool website)
{
    using var lease = await provider.GetRequiredService<TrackingCoordinator>().EnterAsync(CancellationToken.None);
    using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
    var settings = await provider.GetRequiredService<UserSettingsSnapshot>().GetAsync();
    var cutoff = provider.GetRequiredService<TimeProvider>().GetUtcNow() - TimeSpan.FromMinutes(1) - settings.IdleDetection.InactivityThreshold;
    if (website)
    {
        var first = await db.WebsiteUsageSessions.Where(s => !s.IsOptimized && s.EndTime <= cutoff).OrderBy(s => s.StartTime).FirstOrDefaultAsync();
        if (first is null) return;
        var anchor = await db.WebsiteUsageSessions.Where(s => s.IsOptimized && s.EndTime <= first.StartTime).OrderByDescending(s => s.EndTime).FirstOrDefaultAsync();
        var start = anchor?.StartTime ?? first.StartTime;
        var rows = await db.WebsiteUsageSessions.Where(s => s.StartTime >= start && s.EndTime <= cutoff).OrderBy(s => s.StartTime).ToListAsync();
        var result = WebsiteUsageSessionOptimizer.Optimize(rows, settings.WebsiteTracking.UsageSessionMergeTolerance, settings.WebsiteTracking.MinValidUsageSessionDuration);
        db.WebsiteUsageSessions.RemoveRange(result.SessionsToRemove);
    }
    else
    {
        var first = await db.AppUsageSessions.Where(s => !s.IsOptimized && s.EndTime <= cutoff).OrderBy(s => s.StartTime).FirstOrDefaultAsync();
        if (first is null) return;
        var anchor = await db.AppUsageSessions.Where(s => s.IsOptimized && s.EndTime <= first.StartTime).OrderByDescending(s => s.EndTime).FirstOrDefaultAsync();
        var start = anchor?.StartTime ?? first.StartTime;
        var rows = await db.AppUsageSessions.Where(s => s.StartTime >= start && s.EndTime <= cutoff).OrderBy(s => s.StartTime).ToListAsync();
        var result = AppUsageSessionOptimizer.Optimize(rows, settings.AppTracking.UsageSessionMergeTolerance, settings.AppTracking.MinValidUsageSessionDuration);
        db.AppUsageSessions.RemoveRange(result.SessionsToRemove);
    }
    await db.SaveChangesAsync(); lease.Complete();
}
internal sealed class BenchmarkClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
}
internal sealed class Probe : DbCommandInterceptor
{
    private int _commands;
    public int Commands => Volatile.Read(ref _commands);
    public void Reset() => Interlocked.Exchange(ref _commands, 0);
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
}

internal static class ReportOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
