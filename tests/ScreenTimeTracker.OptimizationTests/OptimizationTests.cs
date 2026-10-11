using ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;
using Mediator;
using System.Text.Json;
using ScreenTimeTracker.ScreenTime.Features.DataManagement.ImportData;
using ScreenTimeTracker.ScreenTime.Features.Apps.PatchApp;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using ScreenTimeTracker.ScreenTime.Features.Apps.DeleteApp;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;
using ScreenTimeTracker.ScreenTime.Features.Tracking.TrackWebsiteUsage;
using ScreenTimeTracker.ScreenTime.Features.UserSettingsManagement.PatchUserSettings;
using ScreenTimeTracker.ScreenTime.Features.Websites.DeleteWebsite;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace ScreenTimeTracker.OptimizationTests;

public sealed class OptimizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task SettingsReadsCoalesceAndInvalidationReloadsCommittedValues()
    {
        await using var f = await Fixture.Create();
        var snapshot = f.Get<UserSettingsSnapshot>(); f.Sql.Reset();
        var values = await Task.WhenAll(Enumerable.Range(0,100).Select(_ => snapshot.GetAsync(Ct)));
        Assert.All(values, x => Assert.Same(values[0], x)); Assert.Equal(1, f.Sql.SettingsReads);
        using var scope = f.Scope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var settings = await db.UserSettings.SingleAsync(Ct); settings.Update(timeBoundary: new TimeBoundarySettings(7)); await db.SaveChangesAsync(Ct);
        Assert.Equal(7, (await snapshot.GetAsync(Ct)).TimeBoundary.DayCutoffHour);
        Assert.NotSame(values[0], await snapshot.GetAsync(Ct));
    }
    [Fact]
    public async Task InvalidatedInFlightReadCannotPublishStaleSnapshot()
    {
        await using var f = await Fixture.Create();
        f.Sql.BlockSettings = true;
        var snapshot = f.Get<UserSettingsSnapshot>(); var pending = snapshot.GetAsync(Ct);
        await f.Sql.Blocked.Task.WaitAsync(Ct); snapshot.Invalidate(); f.Sql.Release.TrySetResult();
        await pending; Assert.Equal(2, f.Sql.SettingsReads);
    }
    [Fact]
    public async Task FailedSettingSaveKeepsPreviousSnapshot()
    {
        await using var f = await Fixture.Create(); var snapshot = f.Get<UserSettingsSnapshot>(); var before = await snapshot.GetAsync(Ct);
        using var scope = f.Scope(); var handler = ActivatorUtilities.CreateInstance<PatchUserSettingsHandler>(scope.ServiceProvider);
        f.Sql.FailWrites = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => handler.Handle(new PatchUserSettingsCommand(TimeBoundary: new TimeBoundarySettingsDto(7)), Ct).AsTask());
        Assert.Same(before, await snapshot.GetAsync(Ct));
    }
    [Fact]
    public void LruCachesAreBoundedAndRejectOldVersions()
    {
        var cache = new TrackingIdentityCache();
        for (var i=0;i<512;i++) { cache.PutWebsite(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid(), cache.WebsiteVersion); cache.PutApp(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new AppIdentity(Guid.NewGuid(), null, false, DateTimeOffset.MinValue),cache.AppVersion); }
        Assert.True(cache.TryWebsite("0", out _)); Assert.True(cache.TryApp("0", out _));
        cache.PutWebsite("512",Guid.NewGuid(),cache.WebsiteVersion); cache.PutApp("512",new AppIdentity(Guid.NewGuid(),null,false,DateTimeOffset.MinValue),cache.AppVersion);
        Assert.False(cache.TryWebsite("1",out _)); Assert.False(cache.TryApp("1",out _));
        var appVersion = cache.AppVersion; var websiteVersion = cache.WebsiteVersion; cache.InvalidateAll();
        cache.PutWebsite("stale",Guid.NewGuid(),websiteVersion); cache.PutApp("stale",new AppIdentity(Guid.NewGuid(),null,false,DateTimeOffset.MinValue),appVersion);
        Assert.False(cache.TryWebsite("stale",out _)); Assert.False(cache.TryApp("stale",out _));
    }
    [Fact]
    public async Task HourOfContinuousWebsiteActivityHasOneIdentityReadAndNoSettingsPolling()
    {
        await using var f = await Fixture.Create(); await f.Get<UserSettingsSnapshot>().GetAsync(Ct); f.Sql.Reset();
        for(var i=0;i<2400;i++) { f.Clock.Advance(TimeSpan.FromMilliseconds(1500)); await f.Report(); if(i%10==9) await f.Get<ActiveWebsiteUsageSessionAutoSaver>().SaveOnceAsync(Ct); }
        Assert.Equal(1,f.Sql.WebsiteReads); Assert.Equal(0,f.Sql.SettingsReads);
        using var scope=f.Scope(); var sessions=await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().WebsiteUsageSessions.ToListAsync(Ct);
        Assert.Single(sessions); Assert.Equal(TimeSpan.FromHours(1),sessions[0].UsagePeriod.End-sessions[0].UsagePeriod.Start);
    }
    [Fact]
    public async Task UnchangedWebsiteSaveDoesNotTouchDatabase()
    {
        await using var f=await Fixture.Create(); await f.Report(); var saver=f.Get<ActiveWebsiteUsageSessionAutoSaver>(); await saver.SaveOnceAsync(Ct);
        f.Sql.Reset(); await saver.SaveOnceAsync(Ct); Assert.Equal(0,f.Sql.Commands);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1500)); await f.Report(); await saver.SaveOnceAsync(Ct); Assert.True(f.Sql.Commands>0);
    }
    [Fact]
    public async Task FailedWebsiteSaveIsRetriedWithoutAcknowledgingIt()
    {
        await using var f=await Fixture.Create(); await f.Report(); var saver=f.Get<ActiveWebsiteUsageSessionAutoSaver>(); var current=f.Get<ActiveWebsiteUsageSessionStore>().Current;
        f.Sql.FailWrites=true; await Assert.ThrowsAsync<DbUpdateException>(()=>saver.SaveOnceAsync(Ct));
        Assert.Null(f.Get<ActiveWebsiteUsageSessionStore>().LastSaved); Assert.Equal(current,f.Get<ActiveWebsiteUsageSessionStore>().Current);
        f.Sql.FailWrites=false; await saver.SaveOnceAsync(Ct); Assert.Equal(current,f.Get<ActiveWebsiteUsageSessionStore>().LastSaved);
    }
    [Fact]
    public async Task AppWithoutWindowChangesStillAccumulatesAndSavesIdleTime()
    {
        await using var f=await Fixture.Create(); var store=f.Get<ActiveAppUsageSessionStore>(); store.Current=new ActiveAppUsageSessionState(App.IdleAppId,f.Clock.GetUtcNow());
        var saver=f.Get<ActiveAppUsageSessionAutoSaver>(); f.Clock.Advance(TimeSpan.FromSeconds(15)); await saver.SaveOnceAsync(Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await saver.SaveOnceAsync(Ct);
        using var scope=f.Scope(); var session=await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.SingleAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(30),session.UsagePeriod.End-session.UsagePeriod.Start);
    }
    [Fact]
    public async Task DuplicateAppEventsHitCacheButExpiredMetadataAndNewPathAreRechecked()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a","C:/fictional/a.exe"); f.Sql.Reset();
        for(var i=0;i<100;i++) { f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Window("fictional-a","C:/fictional/a.exe"); }
        Assert.Equal(0,f.Sql.AppReads); Assert.Equal(0,f.Sql.SettingsReads); Assert.Equal(1,f.Metadata.Calls);
        f.Clock.Advance(TimeSpan.FromHours(24)); await f.Window("fictional-a","C:/fictional/a.exe"); Assert.Equal(2,f.Metadata.Calls);
        await f.Window("fictional-a","C:/fictional/b.exe"); Assert.Equal(3,f.Metadata.Calls);
    }
    [Fact]
    public async Task FailedSwitchRestoresPreviousSessionAndCanRetry()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); f.Clock.Advance(TimeSpan.FromSeconds(20));
        var prior=f.Get<ActiveAppUsageSessionStore>().Current; f.Sql.FailWrites=true;
        await Assert.ThrowsAsync<DbUpdateException>(()=>f.Window("fictional-b",null)); Assert.Equal(prior,f.Get<ActiveAppUsageSessionStore>().Current);
        f.Sql.FailWrites=false; await f.Window("fictional-b",null); Assert.NotEqual(prior,f.Get<ActiveAppUsageSessionStore>().Current);
        using var scope=f.Scope(); Assert.Single(await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.ToListAsync(Ct));
    }
    [Fact]
    public async Task AppDeleteInvalidatesIdentityAndNextEventCreatesFreshIdentity()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var id=f.Get<ActiveAppUsageSessionStore>().Current!.AppId;
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<DeleteAppHandler>(scope.ServiceProvider); Assert.False((await handler.Handle(new DeleteAppCommand(id),Ct)).IsError); }
        Assert.False(f.Get<TrackingIdentityCache>().TryApp("fictional-a",out _)); await f.Window("fictional-a",null);
        Assert.NotEqual(id,f.Get<ActiveAppUsageSessionStore>().Current!.AppId);
    }
    [Fact]
    public async Task WebsiteDeleteInvalidatesIdentityAndNextReportCreatesFreshIdentity()
    {
        await using var f=await Fixture.Create(); await f.Report(); var id=f.Get<ActiveWebsiteUsageSessionStore>().Current!.WebsiteId;
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<DeleteWebsiteHandler>(scope.ServiceProvider); Assert.False((await handler.Handle(new DeleteWebsiteCommand(id),Ct)).IsError); }
        Assert.False(f.Get<TrackingIdentityCache>().TryWebsite("fictional.example",out _)); f.Clock.Advance(TimeSpan.FromMilliseconds(1500)); await f.Report();
        Assert.NotEqual(id,f.Get<ActiveWebsiteUsageSessionStore>().Current!.WebsiteId);
    }
    [Fact]
    public async Task ConcurrentSaveAndSwitchProduceDisjointSessions()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); f.Clock.Advance(TimeSpan.FromSeconds(15));
        await Task.WhenAll(f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct),f.Window("fictional-b",null));
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        using var scope=f.Scope(); var sessions=await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(2,sessions.Count); Assert.Equal(sessions[0].EndTime,sessions[1].StartTime);
    }
    [Fact]
    public async Task ContinuousWebsiteAcrossMidnightRemainsOneAccurateSession()
    {
        await using var f=await Fixture.Create(); f.Clock.Now=new DateTimeOffset(2026,1,10,23,59,59,TimeSpan.Zero); await f.Report();
        f.Clock.Advance(TimeSpan.FromSeconds(1.5)); await f.Report(); await f.Get<ActiveWebsiteUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        using var scope=f.Scope(); var session=await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().WebsiteUsageSessions.SingleAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(3),session.UsagePeriod.End-session.UsagePeriod.Start);
    }
    [Fact]
    public async Task IdleAndSuspendedReportsDoNotUpdateSession()
    {
        await using var f=await Fixture.Create(); await f.Report(); var prior=f.Get<ActiveWebsiteUsageSessionStore>().Current;
        f.Get<UserIdleStore>().Current=new(true); f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Report(); Assert.Equal(prior,f.Get<ActiveWebsiteUsageSessionStore>().Current);
        f.Get<UserIdleStore>().Current=new(false); f.Get<SystemSuspendStore>().Current=new(true); await f.Report(); Assert.Equal(prior,f.Get<ActiveWebsiteUsageSessionStore>().Current);
    }
    [Fact]
    public async Task BufferRetainsFirstDuplicateTimestampAndAllDifferentTransitions()
    {
        using var buffer=new ForegroundEventBuffer(); var now=DateTimeOffset.UnixEpoch;
        buffer.Enqueue(new(new WindowInfo("a",null),now),0,0); buffer.Enqueue(new(new WindowInfo("a",null),now.AddSeconds(1)),0,0); buffer.Enqueue(new(new WindowInfo("b",null),now.AddSeconds(2)),0,0);
        Assert.Equal(2,buffer.Count); Assert.Equal(now,(await buffer.ReadAsync(Ct)).Latest.OccurredAt); Assert.Equal("b",(await buffer.ReadAsync(Ct)).Latest.WindowInfo!.ProcessName);
    }
    [Fact]
    public async Task BufferBoundsMemoryAndSummarizesOverflowWithoutBlockingProducer()
    {
        using var buffer=new ForegroundEventBuffer(); var now=DateTimeOffset.UnixEpoch;
        for(var i=0;i<10000;i++) buffer.Enqueue(new(new WindowInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture),null),now.AddSeconds(i)),0,0);
        Assert.Equal(256,buffer.Count); for(var i=0;i<256;i++) Assert.Null((await buffer.ReadAsync(Ct)).GapStart);
        var batch=await buffer.ReadAsync(Ct); Assert.Equal(now.AddSeconds(256),batch.GapStart); Assert.Equal(now.AddSeconds(9999),batch.Latest.OccurredAt); Assert.Equal(9744,batch.DroppedCount);
        buffer.Enqueue(new(new WindowInfo("recovered",null),now.AddSeconds(10000)),0,0); Assert.Equal("recovered",(await buffer.ReadAsync(Ct)).Latest.WindowInfo!.ProcessName);
    }
    [Fact]
    public async Task OverflowGapIsAttributedToUnknownAndNextApplicationStartsAtLatestEvent()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var start=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromSeconds(20));
        var batch=new ForegroundBatch(new(new WindowInfo("fictional-b",null),f.Clock.GetUtcNow()),start.AddSeconds(10),20);
        await f.Batch(batch); using var scope=f.Scope(); var sessions=await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(2,sessions.Count); Assert.Equal(start.AddSeconds(10),sessions[0].EndTime); Assert.Equal(App.UnknownAppId,sessions[1].AppId); Assert.Equal(start.AddSeconds(10),sessions[1].StartTime); Assert.Equal(start.AddSeconds(20),sessions[1].EndTime);
    }
    [Fact]
    public async Task OverflowAcrossSuspendDoesNotCreateUnknownSleepTime()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var start=f.Clock.GetUtcNow();
        f.Get<SystemSuspendStore>().Current=new(true); f.Get<ActiveAppUsageSessionStore>().Current=null; f.Clock.Advance(TimeSpan.FromHours(1)); f.Get<SystemSuspendStore>().Current=new(false);
        await f.Batch(new ForegroundBatch(new(new WindowInfo("fictional-b",null),f.Clock.GetUtcNow()),start.AddSeconds(10),20));
        using var scope=f.Scope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.ToListAsync(Ct)); Assert.Equal(f.Clock.GetUtcNow(),f.Get<ActiveAppUsageSessionStore>().Current!.StartTime);
    }
    [Fact]
    public async Task FailedBulkDeleteRollsBackHistoryAndActiveSession()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct); var prior=f.Get<ActiveAppUsageSessionStore>().Current;
        f.Sql.FailAppDelete=true;
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<DeleteAppHandler>(scope.ServiceProvider); await Assert.ThrowsAsync<DbUpdateException>(()=>handler.Handle(new DeleteAppCommand(prior!.AppId),Ct).AsTask()); }
        using var check=f.Scope(); Assert.Single(await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.ToListAsync(Ct)); Assert.Equal(prior,f.Get<ActiveAppUsageSessionStore>().Current);
    }
    [Fact]
    public async Task SuccessfulImportInvalidatesBothCachesAndSnapshotOnlyAfterCommit()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); await f.Report();
        var before=await f.Get<UserSettingsSnapshot>().GetAsync(Ct); var cache=f.Get<TrackingIdentityCache>();
        using var scope=f.Scope(); var handler=ActivatorUtilities.CreateInstance<ImportDataHandler>(scope.ServiceProvider);
        var json=ImportDocument("fictional-imported"); Assert.False((await handler.Handle(new ImportDataCommand(json),Ct)).IsError);
        Assert.False(cache.TryApp("fictional-a",out _)); Assert.False(cache.TryWebsite("fictional.example",out _));
        Assert.NotSame(before,await f.Get<UserSettingsSnapshot>().GetAsync(Ct));
        Assert.Null(f.Get<ActiveWebsiteUsageSessionStore>().LastSaved);
    }
    [Fact]
    public async Task FailedImportKeepsCachesAndCommittedSettings()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); await f.Report();
        var before=await f.Get<UserSettingsSnapshot>().GetAsync(Ct); var cache=f.Get<TrackingIdentityCache>();
        var appVersion=cache.AppVersion; var websiteVersion=cache.WebsiteVersion; f.Sql.FailWrites=true;
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<ImportDataHandler>(scope.ServiceProvider); await Assert.ThrowsAsync<DbUpdateException>(()=>handler.Handle(new ImportDataCommand(ImportDocument("fictional-imported")),Ct).AsTask()); }
        Assert.Equal(appVersion,cache.AppVersion); Assert.Equal(websiteVersion,cache.WebsiteVersion);
        Assert.True(cache.TryApp("fictional-a",out _)); Assert.True(cache.TryWebsite("fictional.example",out _)); Assert.Same(before,await f.Get<UserSettingsSnapshot>().GetAsync(Ct));
    }
    [Fact]
    public async Task MetadataPolicyEditInvalidatesCacheAndDisabledRefreshStillCachesObservedPath()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a","C:/fictional/a.exe"); var id=f.Get<ActiveAppUsageSessionStore>().Current!.AppId;
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<PatchAppHandler>(scope.ServiceProvider); Assert.False((await handler.Handle(new PatchAppCommand(id,AllowMetadataAutoRefresh:false),Ct)).IsError); }
        Assert.False(f.Get<TrackingIdentityCache>().TryApp("fictional-a",out _));
        await f.Window("fictional-a","C:/fictional/b.exe"); f.Sql.Reset(); await f.Window("fictional-a","C:/fictional/b.exe"); Assert.Equal(0,f.Sql.AppReads); Assert.Equal(1,f.Metadata.Calls);
    }
    [Fact]
    public async Task IdleCorrectionAndResumePreserveExactBoundaries()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var start=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromMinutes(12)); await f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        f.Get<UserIdleStore>().Current=new(true);
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage.UserBecameIdleHandler>(scope.ServiceProvider); await handler.Handle(new UserBecameIdleEvent(start.AddMinutes(2)),Ct); }
        Assert.Equal(App.IdleAppId,f.Get<ActiveAppUsageSessionStore>().Current!.AppId); await f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        f.Get<UserIdleStore>().Current=new(false); await f.Window("fictional-b",null);
        using var check=f.Scope(); var sessions=await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(2,sessions.Count); Assert.Equal(start.AddMinutes(2),sessions[0].EndTime); Assert.Equal(App.IdleAppId,sessions[1].AppId); Assert.Equal(f.Clock.GetUtcNow(),sessions[1].EndTime);
    }
    [Fact]
    public async Task RealSuspendHandlerClosesSessionAndResumeDoesNotCountSleep()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Get<SystemSuspendStore>().Current=new(true);
        using(var scope=f.Scope()) { var handler=ActivatorUtilities.CreateInstance<SystemSuspendingHandler>(scope.ServiceProvider); await handler.Handle(new SystemSuspendingEvent(),Ct); }
        Assert.Null(f.Get<ActiveAppUsageSessionStore>().Current); f.Clock.Advance(TimeSpan.FromHours(1)); f.Get<SystemSuspendStore>().Current=new(false); await f.Window("fictional-b",null); f.Clock.Advance(TimeSpan.FromSeconds(10)); await f.Get<ActiveAppUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        using var check=f.Scope(); var sessions=await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(2,sessions.Count); Assert.All(sessions,x=>Assert.Equal(TimeSpan.FromSeconds(10),x.UsagePeriod.End-x.UsagePeriod.Start)); Assert.Equal(TimeSpan.FromHours(1),sessions[1].UsagePeriod.Start-sessions[0].UsagePeriod.End);
    }
    [Fact]
    public async Task BufferDoesNotMergeSameApplicationAcrossLifecycleBoundary()
    {
        using var buffer=new ForegroundEventBuffer(); var now=DateTimeOffset.UnixEpoch;
        buffer.Enqueue(new(new WindowInfo("a",null),now),0,0); buffer.Enqueue(new(new WindowInfo("a",null),now.AddHours(1)),0,2);
        Assert.Equal(2,buffer.Count); Assert.Equal(0,(await buffer.ReadAsync(Ct)).SuspendVersion); Assert.Equal(2,(await buffer.ReadAsync(Ct)).SuspendVersion);
    }
    [Fact]
    public async Task PendingOldEpochEventCannotRestorePreSuspendActivity()
    {
        await using var f=await Fixture.Create(); var start=f.Clock.GetUtcNow(); f.Get<SystemSuspendStore>().Current=new(true); f.Get<SystemSuspendStore>().Current=new(false);
        await f.Batch(new ForegroundBatch(new(new WindowInfo("fictional-old",null),start)));
        Assert.Null(f.Get<ActiveAppUsageSessionStore>().Current);
    }
    [Fact]
    public async Task InterruptedOverflowRepairsActivePrefixAndResumePortionButExcludesSleep()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var start=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromSeconds(20)); f.Get<SystemSuspendStore>().Current=new(true);
        using(var scope=f.Scope()) { await ActivatorUtilities.CreateInstance<SystemSuspendingHandler>(scope.ServiceProvider).Handle(new SystemSuspendingEvent(),Ct); }
        f.Clock.Advance(TimeSpan.FromHours(1)); f.Get<SystemSuspendStore>().Current=new(false); var resumed=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromSeconds(10));
        await f.Batch(new ForegroundBatch(new(new WindowInfo("fictional-b",null),f.Clock.GetUtcNow()),start.AddSeconds(10),30,LatestSuspendVersion:2,LatestEpochStartedAt:resumed));
        using var check=f.Scope(); var sessions=await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(3,sessions.Count); Assert.Equal(start.AddSeconds(10),sessions[0].EndTime);
        Assert.Equal(App.UnknownAppId,sessions[1].AppId); Assert.Equal(start.AddSeconds(20),sessions[1].EndTime);
        Assert.Equal(App.UnknownAppId,sessions[2].AppId); Assert.Equal(resumed,sessions[2].StartTime);
        Assert.Equal(TimeSpan.FromHours(1),sessions[2].StartTime-sessions[1].EndTime);
    }
    [Fact]
    public async Task InterruptedOverflowPreservesIdleAndReattributesOnlyActiveTime()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var start=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromMinutes(12));
        f.Get<UserIdleStore>().Current=new(true);
        using(var scope=f.Scope()) { await ActivatorUtilities.CreateInstance<ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage.UserBecameIdleHandler>(scope.ServiceProvider).Handle(new UserBecameIdleEvent(start.AddMinutes(2)),Ct); }
        f.Get<UserIdleStore>().Current=new(false); var resumed=f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromSeconds(10));
        await f.Batch(new ForegroundBatch(new(new WindowInfo("fictional-b",null),f.Clock.GetUtcNow()),start.AddMinutes(1),30,LatestIdleVersion:2,LatestEpochStartedAt:resumed));
        using var check=f.Scope(); var sessions=await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().AppUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(4,sessions.Count); Assert.Equal(App.UnknownAppId,sessions[1].AppId); Assert.Equal(App.IdleAppId,sessions[2].AppId); Assert.Equal(resumed,sessions[2].EndTime); Assert.Equal(App.UnknownAppId,sessions[3].AppId);
    }
    [Fact]
    public async Task UncommittedSwitchIsNotPublishedToSessionReaders()
    {
        await using var f=await Fixture.Create(); await f.Window("fictional-a",null); var prior=f.Get<ActiveAppUsageSessionStore>().Current; f.Clock.Advance(TimeSpan.FromSeconds(15));
        f.Sql.BlockWrites=true; var pending=f.Window("fictional-b",null); await f.Sql.Blocked.Task.WaitAsync(Ct);
        Assert.Equal(prior,f.Get<ActiveAppUsageSessionStore>().Current); f.Sql.Release.TrySetResult(); await pending; Assert.NotEqual(prior,f.Get<ActiveAppUsageSessionStore>().Current);
    }
    [Fact]
    public async Task FailedWebsiteSwitchKeepsPriorSessionAndRetriesWithoutDuplicateIntervals()
    {
        await using var f=await Fixture.Create(); await f.Report(); var prior=f.Get<ActiveWebsiteUsageSessionStore>().Current; f.Clock.Advance(TimeSpan.FromSeconds(1.5));
        f.Sql.FailWrites=true; await Assert.ThrowsAsync<DbUpdateException>(()=>f.Report("fictional-second.example")); Assert.Equal(prior,f.Get<ActiveWebsiteUsageSessionStore>().Current);
        f.Sql.FailWrites=false; await f.Report("fictional-second.example"); await f.Get<ActiveWebsiteUsageSessionAutoSaver>().SaveOnceAsync(Ct);
        using var check=f.Scope(); var sessions=await check.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().WebsiteUsageSessions.OrderBy(x=>x.StartTime).ToListAsync(Ct);
        Assert.Equal(2,sessions.Count); Assert.Equal(sessions[0].EndTime,sessions[1].StartTime);
    }
    [Fact]
    public async Task IdleMonitorPublishesOnlyRealTransitionsAndStillRestartsActivity()
    {
        await using var f=await Fixture.Create();
        using(var scope=f.Scope()) { var db=scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); var settings=await db.UserSettings.SingleAsync(Ct); settings.Update(idleDetection:new IdleDetectionSettings(true,TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(25))); await db.SaveChangesAsync(Ct); }
        f.Idle.Seconds=2; using var monitor=ActivatorUtilities.CreateInstance<UserIdleMonitor>(f.Services);
        await monitor.StartAsync(Ct);
        try
        {
            await f.Notifications.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5),Ct); Assert.Equal(0,f.Notifications.ActiveCount); Assert.True(f.Get<UserIdleStore>().Current.IsUserIdle);
            f.Idle.Seconds=0; await f.Notifications.Active.Task.WaitAsync(TimeSpan.FromSeconds(5),Ct);
            Assert.Equal(1,f.Notifications.IdleCount); Assert.Equal(1,f.Notifications.ActiveCount); Assert.False(f.Get<UserIdleStore>().Current.IsUserIdle);
        }
        finally { await monitor.StopAsync(Ct); }
    }
    private static string ImportDocument(string name)=>JsonSerializer.Serialize(new {version=4,apps=new[]{new {name="Fictional",processName=name,color="#123456",appCategoryName="Uncategorized",allowMetadataAutoRefresh=true}},appCategories=Array.Empty<object>(),appUsageSessions=Array.Empty<object>(),websites=Array.Empty<object>(),websiteCategories=Array.Empty<object>(),websiteUsageSessions=Array.Empty<object>()});

}

internal sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; }=new(2026,1,10,12,0,0,TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow()=>Now;
    public void Advance(TimeSpan duration)=>Now+=duration;
}
internal sealed class Metadata : IExecutableMetadataProvider
{
    public int Calls { get; private set; }
    public ExecutableMetadata GetMetadata(string executablePath) { Calls++; return new("Fictional",null,null); }
}
internal sealed class SqlProbe : DbCommandInterceptor
{
    public int Commands, SettingsReads, WebsiteReads, AppReads;
    public bool FailWrites, FailAppDelete, BlockSettings, BlockWrites;
    public string? BlockReadContains;
    public TaskCompletionSource Blocked { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset()=>(Commands,SettingsReads,WebsiteReads,AppReads)=(0,0,0,0);
    private void Count(DbCommand command)
    {
        Commands++;
        var sql=command.CommandText;
        if(sql.StartsWith("SELECT",StringComparison.Ordinal)) { if(sql.Contains("UserSettings",StringComparison.Ordinal)) SettingsReads++; if(sql.Contains("Websites",StringComparison.Ordinal)) WebsiteReads++; if(sql.Contains("FROM \"ScreenTime_Apps\"",StringComparison.Ordinal)) AppReads++; }
        if(FailWrites && !sql.StartsWith("SELECT",StringComparison.Ordinal) || FailAppDelete && sql.Contains("DELETE FROM \"ScreenTime_Apps\"",StringComparison.Ordinal)) throw new InvalidOperationException("Injected synthetic write failure.");
    }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
    {
        Count(command);
        if (BlockReadContains is { } fragment && command.CommandText.Contains(fragment, StringComparison.Ordinal)) { BlockReadContains = null; Blocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        if(BlockWrites && !command.CommandText.StartsWith("SELECT",StringComparison.Ordinal)) { BlockWrites=false; Blocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        if(BlockSettings && command.CommandText.Contains("UserSettings",StringComparison.Ordinal)) { BlockSettings=false; Blocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        return result;
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<int> result,CancellationToken cancellationToken=default) { Count(command); return ValueTask.FromResult(result); }
}
internal sealed class Fixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "stt-round2-" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Services { get; }
    public Clock Clock { get; }=new(); public Metadata Metadata { get; }=new(); public SqlProbe Sql { get; }=new();
    public IdleProvider Idle { get; }=new(); public Notifications Notifications { get; }=new();
    private Fixture(SqliteConnection connection, bool fileScopes)
    {
        _connection=connection; var services=new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(Clock); services.AddSingleton<IExecutableMetadataProvider>(Metadata);
        services.AddSingleton<IIdleTimeProvider>(Idle); services.AddSingleton<IPublisher>(Notifications);
        services.AddDbContext<ScreenTimeDbContext>(o=> { if (fileScopes) o.UseSqlite(connection.ConnectionString); else o.UseSqlite(connection); o.AddInterceptors(Sql); });
        services.AddSingleton<ActiveAppUsageSessionStore>(); services.AddSingleton<ActiveWebsiteUsageSessionStore>();
        services.AddSingleton<UserIdleStore>(); services.AddSingleton<SystemSuspendStore>();
        services.AddSingleton<TrackingCoordinator>(); services.AddSingleton<TrackingIdentityCache>(); services.AddSingleton<UserSettingsSnapshot>();
        services.AddSingleton<SessionCompactionRunner>();
        services.AddSingleton(new ExportGenerationGate(Path.Combine(_temporary, "ExportTemporary")));
        services.AddSingleton<WebsiteActivityProcessor>(); services.AddSingleton<ForegroundWindowProcessor>();
        services.AddSingleton<ActiveAppUsageSessionAutoSaver>(); services.AddSingleton<ActiveWebsiteUsageSessionAutoSaver>();
        Services=services.BuildServiceProvider();
    }
    public static async Task<Fixture> Create(bool fileScopes = false)
    {
        var connection=new SqliteConnection(fileScopes ? "Data Source=" + Path.Combine(Path.GetTempPath(), "stt-round2-db-" + Guid.NewGuid().ToString("N") + ".db") + ";Pooling=False" : "Data Source=:memory:"); await connection.OpenAsync(TestContext.Current.CancellationToken); var fixture=new Fixture(connection, fileScopes);
        using var scope=fixture.Scope(); var db=scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>(); await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken); db.UserSettings.Add(UserSettings.CreateDefault()); await db.SaveChangesAsync(TestContext.Current.CancellationToken); fixture.Sql.Reset(); return fixture;
    }
    public T Get<T>() where T:notnull=>Services.GetRequiredService<T>(); public IServiceScope Scope()=>Services.CreateScope();
    public Task Report(string host="fictional.example")=>Invoke(Get<WebsiteActivityProcessor>(),"ProcessActivityReportAsync",[new WebsiteActivityReport(host,"Fictional",TimeSpan.FromMilliseconds(1500),Clock.GetUtcNow()),TestContext.Current.CancellationToken]);
    public Task Window(string name,string? path)=>Batch(new ForegroundBatch(new(new WindowInfo(name,path),Clock.GetUtcNow()),IdleVersion:Get<UserIdleStore>().Version,SuspendVersion:Get<SystemSuspendStore>().Version));
    public Task Batch(ForegroundBatch batch)=>Invoke(Get<ForegroundWindowProcessor>(),"ProcessWindowChangeAsync",[batch.Latest,TestContext.Current.CancellationToken,batch]);
    private static Task Invoke(object target,string method,object?[] args)=>(Task)target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args)!;
    public async ValueTask DisposeAsync() { var path = _connection.DataSource; await Services.DisposeAsync(); await _connection.DisposeAsync(); if (Directory.Exists(_temporary)) Directory.Delete(_temporary, true); if (File.Exists(path)) { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } }
}

internal sealed class IdleProvider : IIdleTimeProvider
{
    public int Seconds;
    public TimeSpan GetSystemIdleTime()=>TimeSpan.FromSeconds(Volatile.Read(ref Seconds));
}
internal sealed class Notifications : IPublisher
{
    public int IdleCount,ActiveCount;
    public TaskCompletionSource Idle { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Active { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask Publish<TNotification>(TNotification notification,CancellationToken cancellationToken=default) where TNotification:INotification=>Publish((object)notification,cancellationToken);
    public ValueTask Publish(object notification,CancellationToken cancellationToken=default)
    {
        if(notification is UserBecameIdleEvent) { Interlocked.Increment(ref IdleCount); Idle.TrySetResult(); }
        if(notification is UserBecameActiveEvent) { Interlocked.Increment(ref ActiveCount); Active.TrySetResult(); }
        return ValueTask.CompletedTask;
    }
}
