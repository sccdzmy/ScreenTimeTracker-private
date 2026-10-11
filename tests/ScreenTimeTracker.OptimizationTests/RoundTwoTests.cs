using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Websites;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;
using ScreenTimeTracker.ScreenTime.Features.DataManagement.ImportData;
using ScreenTimeTracker.ScreenTime.Features.Statistics;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Xunit;
using AppStats = ScreenTimeTracker.ScreenTime.Features.Apps.GetAppUsage;
using WebsiteStats = ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteUsage;
using AppCategoryStats = ScreenTimeTracker.ScreenTime.Features.AppCategories.GetAppCategoryUsage;
using WebsiteCategoryStats = ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategoryUsage;

namespace ScreenTimeTracker.OptimizationTests;

public sealed class RoundTwoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task CompactionMatchesOriginalAcrossPagesAndRestart(bool website, bool restart)
    {
        await using var f = await Fixture.Create();
        var input = await Seed(f, website, 3700);
        var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        var expected = Reference(input, website ? settings.WebsiteTracking.UsageSessionMergeTolerance : settings.AppTracking.UsageSessionMergeTolerance,
            website ? settings.WebsiteTracking.MinValidUsageSessionDuration : settings.AppTracking.MinValidUsageSessionDuration);
        var runner = f.Get<SessionCompactionRunner>();
        for (var pass = 0; pass < 30; pass++)
        {
            var result = await runner.RunAsync(website, Ct, maxBatches: 1);
            Assert.InRange(result.LargestPage, 0, SessionCompactionRunner.PageSize);
            if (restart) runner = new SessionCompactionRunner(f.Get<UserSettingsSnapshot>(), f.Get<TrackingIdentityCache>(),
                f.Get<TrackingCoordinator>(), f.Get<ActiveAppUsageSessionStore>(), f.Get<ActiveWebsiteUsageSessionStore>(), f.Clock, f.Services.GetRequiredService<IServiceScopeFactory>());
            using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            var remaining = website ? await db.WebsiteUsageSessions.AnyAsync(s => !s.IsOptimized, Ct) : await db.AppUsageSessions.AnyAsync(s => !s.IsOptimized, Ct);
            if (!remaining) break;
        }
        var actual = await Read(f, website);
        Assert.Equal(expected.Select(Key), actual.Select(Key));
        Assert.All(actual, row => Assert.True(row.Optimized));
        if (restart) runner.Dispose();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LongMergeChainKeepsShortTailUntilItsFutureMatch(bool website)
    {
        await using var f = await Fixture.Create();
        var input = await Seed(f, website, 2300, dense: true);
        var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        var expected = Reference(input, website ? settings.WebsiteTracking.UsageSessionMergeTolerance : settings.AppTracking.UsageSessionMergeTolerance,
            website ? settings.WebsiteTracking.MinValidUsageSessionDuration : settings.AppTracking.MinValidUsageSessionDuration);
        for (var i = 0; i < 8; i++) await f.Get<SessionCompactionRunner>().RunAsync(website, Ct, maxBatches: 1);
        Assert.Equal(expected.Select(Key), (await Read(f, website)).Select(Key));
    }

    [Fact]
    public async Task UnresolvedInterveningRowsAreRevisitedRatherThanSkipped()
    {
        await using var f = await Fixture.Create();
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var apps = Enumerable.Range(0, 1200).Select(i => App.CreateDiscovered(f.Clock.GetUtcNow(), "Fictional", "synthetic-" + i)).ToArray();
        db.Apps.AddRange(apps); var start = f.Clock.GetUtcNow().AddDays(-2);
        var rows = apps.Select((a, i) => AppUsageSession.Create(a.Id, new(start.AddMilliseconds(i), start.AddMilliseconds(i + 1)))).ToArray();
        db.AppUsageSessions.AddRange(rows); await db.SaveChangesAsync(Ct);
        var values = rows.Select(r => new Row(r.Id, r.AppId, r.StartTime, r.EndTime)).ToList();
        var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        var expected = Reference(values, settings.AppTracking.UsageSessionMergeTolerance, settings.AppTracking.MinValidUsageSessionDuration);
        for (var i = 0; i < 10; i++) await f.Get<SessionCompactionRunner>().RunAsync(false, Ct);
        Assert.Equal(expected.Select(Key), (await Read(f, false)).Select(Key));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedCompactionRollsBackAndRetriesWithoutAdvancing(bool website)
    {
        await using var f = await Fixture.Create(); var input = await Seed(f, website, 1500, dense: true);
        f.Sql.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Get<SessionCompactionRunner>().RunAsync(website, Ct));
        Assert.Equal(input.OrderBy(r => r.Start).ThenBy(r => r.Id.ToString().ToUpperInvariant()).Select(Key), (await Read(f, website)).Select(Key));
        f.Sql.FailWrites = false;
        for (var i = 0; i < 5; i++) await f.Get<SessionCompactionRunner>().RunAsync(website, Ct);
        var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        Assert.Equal(Reference(input, website ? settings.WebsiteTracking.UsageSessionMergeTolerance : settings.AppTracking.UsageSessionMergeTolerance,
            website ? settings.WebsiteTracking.MinValidUsageSessionDuration : settings.AppTracking.MinValidUsageSessionDuration).Select(Key), (await Read(f, website)).Select(Key));
    }

    [Fact]
    public async Task CompactionRespectsActiveBoundaryAndCommittedInvalidations()
    {
        await using var f = await Fixture.Create(); await Seed(f, false, 1600, dense: true);
        var runner = f.Get<SessionCompactionRunner>(); await runner.RunAsync(false, Ct, 1);
        using (var lease = await f.Get<TrackingCoordinator>().EnterAsync(Ct))
        {
            using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            await db.AppUsageSessions.ExecuteDeleteAsync(Ct); f.Get<TrackingIdentityCache>().InvalidateAll(); lease.Complete();
        }
        Assert.Equal(0, (await runner.RunAsync(false, Ct)).ReadRows);
        using (var scope = f.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            db.AppUsageSessions.Add(AppUsageSession.Create(App.UnknownAppId, new(f.Clock.Now.AddSeconds(-20), f.Clock.Now.AddSeconds(-1))));
            await db.SaveChangesAsync(Ct);
        }
        f.Get<ActiveAppUsageSessionStore>().Current = new(App.UnknownAppId, f.Clock.Now.AddSeconds(-20));
        await runner.RunAsync(false, Ct); Assert.False((await Read(f, false)).Single().Optimized);
    }

    [Theory]
    [InlineData(UsageIdentityKind.App)] [InlineData(UsageIdentityKind.Website)]
    [InlineData(UsageIdentityKind.AppCategory)] [InlineData(UsageIdentityKind.WebsiteCategory)]
    public async Task SqlAggregationClipsAndKeepsMillisecondPrecision(UsageIdentityKind kind)
    {
        await using var f = await Fixture.Create(); var website = kind is UsageIdentityKind.Website or UsageIdentityKind.WebsiteCategory;
        var input = await Seed(f, website, 80);
        var window = new TimeRange(input[10].Start.AddMilliseconds(1), input[65].End.AddMilliseconds(-1));
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var category = kind is UsageIdentityKind.AppCategory or UsageIdentityKind.WebsiteCategory;
        var ids = category ? new[] { website ? WebsiteCategory.UncategorizedId : AppCategory.UncategorizedId } : input.Select(r => r.Owner).Distinct().ToArray();
        var expected = input.GroupBy(r => category ? ids[0] : r.Owner).ToDictionary(g => g.Key, g => g.Sum(r => window.OverlapDuration(new(r.Start, r.End)).Ticks));
        var actual = await UsageDistributionQueries.ReadAsync(db, kind, ids, window, Ct);
        Assert.Equal(expected.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)), actual.OrderBy(x => x.Key).Select(x => (x.Key, x.Value.Ticks)));
        Assert.Empty(await UsageDistributionQueries.ReadAsync(db, kind, Array.Empty<Guid>(), window, Ct));
        Assert.Empty(await UsageDistributionQueries.ReadAsync(db, kind, ids, new(window.End.AddDays(30), window.End.AddDays(31)), Ct));
    }

    [Theory] [InlineData("UTC", "2026-01-09")] [InlineData("America/New_York", "2026-03-08")] [InlineData("America/New_York", "2026-11-01")]
    public async Task FourBucketHandlersMatchReferenceIncludingDaylightSaving(string zoneId, string dateText)
    {
        await using var f = await Fixture.Create(); var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); var date = DateOnly.Parse(dateText, System.Globalization.CultureInfo.InvariantCulture);
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var settings = await db.UserSettings.SingleAsync(Ct); settings.Update(timeBoundary: new TimeBoundarySettings(0)); await db.SaveChangesAsync(Ct);
        var window = LogicalDay.CalculateUtcWindow(date, date, 0, zone);
        var ranges = new[] { new TimeRange(window.Start.AddMinutes(-3), window.Start.AddMilliseconds(1200)), new TimeRange(window.Start.AddMinutes(59), window.End.AddMinutes(2)) };
        var app = App.CreateDiscovered(window.Start, "Fictional", "synthetic-midnight"); var site = Website.CreateDiscovered(window.Start, "Fictional", "midnight.example");
        db.Apps.Add(app); db.Websites.Add(site); db.AppUsageSessions.AddRange(ranges.Select(r => AppUsageSession.Create(app.Id, r))); db.WebsiteUsageSessions.AddRange(ranges.Select(r => WebsiteUsageSession.Create(site.Id, r))); await db.SaveChangesAsync(Ct);
        var snapshot = f.Get<UserSettingsSnapshot>();
        var a = await new AppStats.GetAppUsageHandler(snapshot, db).Handle(new(AppStats.UsageGranularity.Hour, date, date, zone), Ct);
        var w = await new WebsiteStats.GetWebsiteUsageHandler(snapshot, db).Handle(new(WebsiteStats.UsageGranularity.Hour, date, date, zone), Ct);
        var ac = await new AppCategoryStats.GetAppCategoryUsageHandler(snapshot, db).Handle(new(AppCategoryStats.UsageGranularity.Hour, date, date, zone, [AppCategory.UncategorizedId]), Ct);
        var wc = await new WebsiteCategoryStats.GetWebsiteCategoryUsageHandler(snapshot, db).Handle(new(WebsiteCategoryStats.UsageGranularity.Hour, date, date, zone, ExcludedIds: [Guid.Empty]), Ct);
        var expected = new List<long>();
        for (var hour = window.Start; hour < window.End; hour = hour.AddHours(1)) expected.Add((long)ranges.Sum(r => new TimeRange(hour, hour.AddHours(1)).OverlapDuration(r).TotalSeconds));
        Assert.Equal(expected, a.Select(x => x.DurationSeconds)); Assert.Equal(expected, w.Select(x => x.DurationSeconds)); Assert.Equal(expected, ac.Select(x => x.DurationSeconds)); Assert.Equal(expected, wc.Select(x => x.DurationSeconds));
    }

    [Fact]
    public async Task StreamingExportRoundTripsAndCleansUp()
    {
        await using var f = await Fixture.Create(); await Seed(f, false, 60); await Seed(f, true, 30);
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); var storage = f.Get<ExportGenerationGate>();
        await using (var file = await new ExportDataHandler(NullLogger<ExportDataHandler>.Instance, db, storage).Handle(new(), Ct))
        {
            Assert.NotNull(file); await using var stream = file.Open(); using var reader = new StreamReader(stream); var json = await reader.ReadToEndAsync(Ct);
            using var doc = JsonDocument.Parse(json); Assert.Equal(4, doc.RootElement.GetProperty("version").GetInt32()); Assert.Equal(60, doc.RootElement.GetProperty("appUsageSessions").GetArrayLength()); Assert.Equal(30, doc.RootElement.GetProperty("websiteUsageSessions").GetArrayLength());
            await using var other = await Fixture.Create(); using var otherScope = other.Scope();
            var importer = ActivatorUtilities.CreateInstance<ImportDataHandler>(otherScope.ServiceProvider);
            Assert.False((await importer.Handle(new(json), Ct)).IsError);
            Assert.Equal(60, (await Read(other, false)).Count); Assert.Equal(30, (await Read(other, true)).Count);
        }
        Assert.Empty(Directory.EnumerateFiles(storage.Root));
    }

    [Fact]
    public async Task ExportBusyAndCancellationLeaveNoPartialFile()
    {
        await using var f = await Fixture.Create(); using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); var storage = f.Get<ExportGenerationGate>();
        var handler = new ExportDataHandler(NullLogger<ExportDataHandler>.Instance, db, storage);
        Assert.True(storage.TryEnter()); Assert.Null(await handler.Handle(new(), Ct)); storage.Exit();
        f.Sql.BlockReadContains = "FROM \"ScreenTime_AppCategories\"";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct); var exporting = handler.Handle(new(), cancel.Token).AsTask();
        await f.Sql.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting);
        Assert.Empty(Directory.EnumerateFiles(storage.Root)); Assert.True(storage.TryEnter()); storage.Exit();
    }

    [Fact]
    public async Task ExportSnapshotAllowsConcurrentTrackingSaveAndExcludesLaterRows()
    {
        await using var f = await Fixture.Create(fileScopes: true); await Seed(f, false, 2); await Seed(f, true, 2);
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); var storage = f.Get<ExportGenerationGate>();
        f.Sql.BlockReadContains = "FROM \"ScreenTime_AppCategories\"";
        var exporting = new ExportDataHandler(NullLogger<ExportDataHandler>.Instance, db, storage).Handle(new(), Ct).AsTask();
        await f.Sql.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            using var lease = await f.Get<TrackingCoordinator>().EnterAsync(Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
            using var writeScope = f.Scope(); var writes = writeScope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            var site = Website.CreateDiscovered(f.Clock.Now, "Fictional", "later.example"); writes.Websites.Add(site); await writes.SaveChangesAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct); lease.Complete();
        }
        finally { f.Sql.Release.TrySetResult(); if (!exporting.IsCompleted) await exporting.WaitAsync(TimeSpan.FromSeconds(10), Ct); }
        await using var file = await exporting; Assert.NotNull(file); await using var stream = file.Open(); using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
        Assert.DoesNotContain(doc.RootElement.GetProperty("websites").EnumerateArray(), w => w.GetProperty("host").GetString() == "later.example");
    }

    [Fact]
    public async Task ExportRejectsStorageFailureAndReleasesGenerationGate()
    {
        await using var f = await Fixture.Create(); using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var path = Path.Combine(Path.GetTempPath(), "stt-round2-not-directory-" + Guid.NewGuid().ToString("N")); await File.WriteAllTextAsync(path, "synthetic", Ct);
        try
        {
            using var gate = new ExportGenerationGate(path);
            await Assert.ThrowsAnyAsync<IOException>(() => new ExportDataHandler(NullLogger<ExportDataHandler>.Instance, db, gate).Handle(new(), Ct).AsTask());
            Assert.True(gate.TryEnter()); gate.Exit();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportStartupCleanupOnlyRemovesOwnedRegularFiles()
    {
        await using var f = await Fixture.Create(); var gate = f.Get<ExportGenerationGate>(); var path = gate.CreatePath(); await File.WriteAllTextAsync(path, "synthetic", Ct);
        var unrelated = Path.Combine(gate.Root, "keep.txt"); await File.WriteAllTextAsync(unrelated, "synthetic", Ct);
        await gate.StartAsync(Ct); Assert.False(File.Exists(path)); Assert.True(File.Exists(unrelated));
        Assert.False(gate.IsSafe(Path.Combine(gate.Root, "..", "export-" + new string('0', 32) + ".json.tmp")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompactionTiesHaveDeterministicCursorOrder(bool website)
    {
        await using var f = await Fixture.Create(); await Seed(f, website, 1800, dense: true);
        using (var scope = f.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            var time = f.Clock.Now.AddDays(-10);
            if (website) await db.WebsiteUsageSessions.ExecuteUpdateAsync(set => set.SetProperty(s => s.StartTime, time), Ct);
            else await db.AppUsageSessions.ExecuteUpdateAsync(set => set.SetProperty(s => s.StartTime, time), Ct);
        }
        var input = await Read(f, website); var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        var expected = Reference(input, website ? settings.WebsiteTracking.UsageSessionMergeTolerance : settings.AppTracking.UsageSessionMergeTolerance,
            website ? settings.WebsiteTracking.MinValidUsageSessionDuration : settings.AppTracking.MinValidUsageSessionDuration);
        for (var i = 0; i < 10; i++) await f.Get<SessionCompactionRunner>().RunAsync(website, Ct, 1);
        Assert.Equal(expected.Select(Key), (await Read(f, website)).Select(Key));
    }

    [Fact]
    public async Task AdditiveIndexMigrationUpgradesAndDowngradesWithoutChangingRows()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(Ct);
        await using var db = new ScreenTimeDbContext(new DbContextOptionsBuilder<ScreenTimeDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync(Ct);
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.AppUsageSessions.Add(AppUsageSession.Create(App.UnknownAppId, new(time, time.AddMinutes(1)))); await db.SaveChangesAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT Id FROM ScreenTime_AppUsageSessions WHERE AppId = $id AND StartTime = $start";
        command.Parameters.AddWithValue("$id", App.UnknownAppId); command.Parameters.AddWithValue("$start", time.ToUnixTimeMilliseconds());
        using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct)); Assert.Contains("AppId_StartTime", reader.GetString(3), StringComparison.Ordinal);
        }
        await db.GetService<IMigrator>().MigrateAsync("20260817015030_Init", Ct);
        Assert.Equal(1, await db.AppUsageSessions.CountAsync(Ct));
        await db.Database.MigrateAsync(Ct); Assert.Equal(1, await db.AppUsageSessions.CountAsync(Ct));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task ImportDuringContinuationInvalidatesItsRootAndCursors()
    {
        await using var f = await Fixture.Create(); await Seed(f, false, 1800, dense: true);
        await f.Get<SessionCompactionRunner>().RunAsync(false, Ct, 1);
        using (var scope = f.Scope())
        {
            var document = JsonSerializer.Serialize(new { version = 4, apps = new[] { new { name = "Fictional", color = "#123456", processName = "synthetic-0", allowMetadataAutoRefresh = true, appCategoryName = "Uncategorized" } },
                appCategories = Array.Empty<object>(), appUsageSessions = new[] { new { appProcessName = "synthetic-0", startTime = f.Clock.Now.AddDays(-10).AddMilliseconds(100), endTime = f.Clock.Now.AddDays(-10).AddSeconds(15) } },
                websites = Array.Empty<object>(), websiteCategories = Array.Empty<object>(), websiteUsageSessions = Array.Empty<object>() });
            var importer = ActivatorUtilities.CreateInstance<ImportDataHandler>(scope.ServiceProvider); Assert.False((await importer.Handle(new(document), Ct)).IsError);
        }
        var input = await Read(f, false); var settings = await f.Get<UserSettingsSnapshot>().GetAsync(Ct);
        var expected = Reference(input, settings.AppTracking.UsageSessionMergeTolerance, settings.AppTracking.MinValidUsageSessionDuration);
        for (var i = 0; i < 8; i++) await f.Get<SessionCompactionRunner>().RunAsync(false, Ct, 1);
        Assert.Equal(expected.Select(Key), (await Read(f, false)).Select(Key));
    }

    [Fact]
    public async Task CommittedMergeSettingsReplaceAnInProgressContinuation()
    {
        await using var f = await Fixture.Create(); await Seed(f, false, 1800, dense: true);
        await f.Get<SessionCompactionRunner>().RunAsync(false, Ct, 1);
        using (var scope = f.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); var settings = await db.UserSettings.SingleAsync(Ct); var a = settings.AppTracking;
            settings.Update(appTracking: new AppTrackingSettings(a.IconDirectory, a.MetadataStaleThreshold, a.ActiveUsageSessionAutoSaveInterval,
                TimeSpan.Zero, TimeSpan.Zero, a.UsageSessionOptimizationInterval)); await db.SaveChangesAsync(Ct);
        }
        var input = await Read(f, false); var expected = Reference(input, TimeSpan.Zero, TimeSpan.Zero);
        for (var i = 0; i < 8; i++) await f.Get<SessionCompactionRunner>().RunAsync(false, Ct, 1);
        Assert.Equal(expected.Select(Key), (await Read(f, false)).Select(Key));
    }

    [Fact]
    public async Task ReparseExportStorageCannotWriteOrCleanOutsideFiles()
    {
        await using var f = await Fixture.Create(); var gate = f.Get<ExportGenerationGate>(); gate.CreatePath(); Directory.Delete(gate.Root);
        var outside = Path.Combine(Path.GetTempPath(), "stt-round2-outside-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "export-" + new string('0', 32) + ".json.tmp"); await File.WriteAllTextAsync(sentinel, "synthetic sentinel", Ct);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(gate.Root); start.ArgumentList.Add(outside);
                using var process = System.Diagnostics.Process.Start(start)!; await process.StandardOutput.ReadToEndAsync(Ct); await process.WaitForExitAsync(Ct); Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(gate.Root, outside);
            Assert.Throws<IOException>(() => gate.CreatePath()); await Assert.ThrowsAsync<IOException>(() => gate.StartAsync(Ct));
            Assert.Equal("synthetic sentinel", await File.ReadAllTextAsync(sentinel, Ct));
        }
        finally { if (Directory.Exists(gate.Root)) Directory.Delete(gate.Root); Directory.Delete(outside, true); }
    }

    private static async Task<List<Row>> Seed(Fixture f, bool website, int count, bool dense = false)
    {
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var owners = new Guid[3];
        for (var i = 0; i < owners.Length; i++)
        {
            if (website) { var site = Website.CreateDiscovered(f.Clock.Now, "Fictional", "synthetic-" + i + ".example"); db.Websites.Add(site); owners[i] = site.Id; }
            else { var app = App.CreateDiscovered(f.Clock.Now, "Fictional", "synthetic-" + i); db.Apps.Add(app); owners[i] = app.Id; }
        }
        var rows = new List<Row>(); var now = f.Clock.Now.AddDays(-10); var random = new Random(42);
        for (var i = 0; i < count; i++)
        {
            var start = now;
            // Dense chains cross multiple pages while remaining within the merge tolerance.
            now += dense ? TimeSpan.FromMilliseconds(3) : TimeSpan.FromMilliseconds(random.Next(10, 8000));
            var owner = owners[dense ? i % 2 : random.Next(owners.Length)];
            if (website) { var s = WebsiteUsageSession.Create(owner, new(start, now)); db.WebsiteUsageSessions.Add(s); rows.Add(new(s.Id, owner, start, now)); }
            else { var s = AppUsageSession.Create(owner, new(start, now)); db.AppUsageSessions.Add(s); rows.Add(new(s.Id, owner, start, now)); }
            if (!dense) now += TimeSpan.FromMilliseconds(random.Next(0, 5000));
        }
        await db.SaveChangesAsync(Ct); return rows;
    }
    private static async Task<List<Row>> Read(Fixture f, bool website)
    {
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var rows = website ? await db.WebsiteUsageSessions.AsNoTracking().Select(s => new Row(s.Id, s.WebsiteId, s.StartTime, s.EndTime) { Optimized = s.IsOptimized }).ToListAsync(Ct)
            : await db.AppUsageSessions.AsNoTracking().Select(s => new Row(s.Id, s.AppId, s.StartTime, s.EndTime) { Optimized = s.IsOptimized }).ToListAsync(Ct);
        return rows.OrderBy(r => r.Start).ThenBy(r => r.Id.ToString().ToUpperInvariant(), StringComparer.Ordinal).ToList();
    }
    private static List<Row> Reference(IEnumerable<Row> input, TimeSpan tolerance, TimeSpan minimum)
    {
        var rows = input.Select(r => new Row(r.Id, r.Owner, r.Start, r.End)).OrderBy(r => r.Start).ThenBy(r => r.Id.ToString().ToUpperInvariant(), StringComparer.Ordinal).ToList();
        var removed = new HashSet<Guid>();
        for (var i = 0; i < rows.Count; i++)
        {
            var current = rows[i];
            for (var j = i + 1; j < rows.Count; j++)
            {
                var next = rows[j]; if (next.Start > current.End + tolerance) break;
                if (current.Owner != next.Owner) continue;
                current.End = next.End; for (var k = i + 1; k <= j; k++) removed.Add(rows[k].Id); i = j;
            }
        }
        return rows.Where(r => !removed.Contains(r.Id) && r.End - r.Start >= minimum).ToList();
    }
    private static (Guid, Guid, DateTimeOffset, DateTimeOffset) Key(Row row) => (row.Id, row.Owner, row.Start, row.End);
    private sealed class Row(Guid id, Guid owner, DateTimeOffset start, DateTimeOffset end)
    {
        public Guid Id { get; } = id; public Guid Owner { get; } = owner;
        public DateTimeOffset Start { get; } = start; public DateTimeOffset End { get; set; } = end;
        public bool Optimized { get; init; }
    }
}
