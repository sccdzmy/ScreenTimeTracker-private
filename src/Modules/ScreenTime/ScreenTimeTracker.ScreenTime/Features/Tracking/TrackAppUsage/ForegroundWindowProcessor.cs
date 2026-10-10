using ScreenTimeTracker.BuildingBlocks.Security;
using System.Threading.Channels;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public record ForegroundWindowChangedMessage(WindowInfo? WindowInfo, DateTimeOffset OccurredAt);

public partial class ForegroundWindowProcessor(
    TrackingCoordinator coordinator, UserSettingsSnapshot settingsSnapshot, TrackingIdentityCache identities,
    ActiveAppUsageSessionStore activeSessionStore,
    ILogger<ForegroundWindowProcessor> logger,
    IServiceScopeFactory serviceScopeFactory,
    UserIdleStore userIdleStore,
    SystemSuspendStore systemSuspendStore
) : BackgroundService
{
    private readonly ForegroundEventBuffer _buffer = new();

    private DateTimeOffset _lastProcessedAt = DateTimeOffset.MinValue;

    public override void Dispose()
    {
        _buffer.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Enqueue(ForegroundWindowChangedMessage message)
    {
        _buffer.Enqueue(message, userIdleStore.Version, systemSuspendStore.Version);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ForegroundBatch? pending = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = pending ?? await _buffer.ReadAsync(stoppingToken);
                pending = null;
                var message = batch.Latest;
                if ((batch.LatestIdleVersion ?? batch.IdleVersion) != userIdleStore.Version ||
                    (batch.LatestSuspendVersion ?? batch.SuspendVersion) != systemSuspendStore.Version) continue;
                // 处于挂起/空闲状态时，丢弃事件
                if (
                    userIdleStore.Current.IsUserIdle
                    || systemSuspendStore.Current.IsSystemSuspendActive
                )
                    continue;

                // 丢弃比已处理时间还早的过期/倒序事件
                if (message.OccurredAt <= _lastProcessedAt)
                    continue;

                try
                {
                    await ProcessWindowChangeAsync(message, stoppingToken, batch);
                    _lastProcessedAt = message.OccurredAt;
                }
                catch (Exception ex)
                {
                    LogProcessWindowChangeFailed(logger, "collector", ex);
                    pending = batch;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to process foreground change event for process '{ProcessName}'."
    )]
    private static partial void LogProcessWindowChangeFailed(
        ILogger logger,
        string processName,
        Exception ex
    );

    private async Task ProcessWindowChangeAsync(
        ForegroundWindowChangedMessage message,
        CancellationToken cancellationToken,
        ForegroundBatch? batch = null
    )
    {
        using var mutation = await coordinator.EnterAsync(cancellationToken);
        if (userIdleStore.Current.IsUserIdle || systemSuspendStore.Current.IsSystemSuspendActive) return;
        if (batch?.GapStart is null && batch is not null &&
            (batch.IdleVersion != userIdleStore.Version || batch.SuspendVersion != systemSuspendStore.Version)) return;
        if (batch?.GapStart is null && message.WindowInfo is null && activeSessionStore.Current?.AppId == App.UnknownAppId)
        { mutation.Complete(); return; }
        var settings = await settingsSnapshot.GetAsync(cancellationToken);
        bool cached = message.WindowInfo is { } info && identities.TryApp(info.ProcessName, out var candidate) &&
            string.Equals(candidate.ExecutablePath, info.ExecutablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            (!candidate.AllowRefresh || message.OccurredAt - candidate.RefreshedAt < settings.AppTracking.MetadataStaleThreshold);
        AppIdentity? identity = null;
        if (cached) identities.TryApp(message.WindowInfo!.ProcessName, out identity!);
        if (batch?.GapStart is null && cached && activeSessionStore.Current?.AppId == identity!.Id)
        { mutation.Complete(); return; }
        using var scope = serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var executableMetadataProvider = scope.ServiceProvider.GetRequiredService<IExecutableMetadataProvider>();
        var identityVersion = identities.AppVersion;
        Guid appId;
        if (message.WindowInfo is null)
            appId = App.UnknownAppId;
        else if (cached) appId = identity!.Id;
        else
            appId = await IdentifyApp(
                context,
                executableMetadataProvider,
                message.OccurredAt,
                message.WindowInfo,
                cancellationToken
            );

        var nextSession = activeSessionStore.Current;
        if (batch?.GapStart is { } gapStart && batch.IdleVersion == userIdleStore.Version &&
            batch.SuspendVersion == systemSuspendStore.Version)
        {
            var safeStart = gapStart > _lastProcessedAt ? gapStart : _lastProcessedAt;
            if (nextSession is { } prior)
                await context.PersistActiveSessionAsync(prior, safeStart, cancellationToken);
            if (safeStart < message.OccurredAt)
                context.AppUsageSessions.Add(AppUsageSession.Create(App.UnknownAppId,
                    new ScreenTimeTracker.ScreenTime.Domain.ValueObjects.TimeRange(safeStart, message.OccurredAt)));
            nextSession = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        }
        else if (batch?.GapStart is { } interruptedGap)
        {
            // Lifecycle handlers already closed known activity and removed sleep intervals.
            // Repair only recorded active portions; idle and absent (sleep) time remain untouched.
            if (nextSession is { } prior)
            {
                var end = prior.AppId == App.IdleAppId ? batch.LatestEpochStartedAt ?? message.OccurredAt : message.OccurredAt;
                await context.PersistActiveSessionAsync(prior, end, cancellationToken);
            }
            if (batch.LatestEpochStartedAt is { } resumedAt && resumedAt < message.OccurredAt &&
                (nextSession is null || nextSession.AppId == App.IdleAppId))
                context.AppUsageSessions.Add(AppUsageSession.Create(App.UnknownAppId,
                    new ScreenTimeTracker.ScreenTime.Domain.ValueObjects.TimeRange(resumedAt, message.OccurredAt)));
            await ReattributeRecordedGapAsync(context, interruptedGap, message.OccurredAt, cancellationToken);
            nextSession = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        }
        else if (nextSession is null)
            nextSession = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        else if (nextSession.AppId != appId)
        {
            var activeSession = nextSession;
            if (activeSession is not null)
            {
                await context.PersistActiveSessionAsync(
                    activeSession,
                    message.OccurredAt,
                    cancellationToken
                );
            }
            nextSession = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        }

        var identityChanged = context.ChangeTracker.Entries<App>().Any(e => e.State is EntityState.Added or EntityState.Modified);
        await context.SaveChangesAsync(cancellationToken);
        if (message.WindowInfo is { } window)
        {
            var resolved = context.ChangeTracker.Entries<App>().Select(e => e.Entity).FirstOrDefault(a => a.Id == appId);
            if (resolved is not null) identities.PutApp(window.ProcessName,
                new AppIdentity(resolved.Id, window.ExecutablePath, resolved.AllowMetadataAutoRefresh, resolved.MetadataLastRefreshedAt), identityVersion + (identityChanged ? 1 : 0));
        }
        activeSessionStore.Current = nextSession;
        mutation.Complete();
        if (batch?.GapStart is { } start)
            LogOverflow(logger, batch.DroppedCount, Math.Max(0, (message.OccurredAt - start).TotalMilliseconds));
    }

    private static async Task ReattributeRecordedGapAsync(ScreenTimeDbContext context, DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken)
    {
        var stored = await context.AppUsageSessions.Where(s => s.StartTime < end && start < s.EndTime).ToListAsync(cancellationToken);
        var affected = stored.Concat(context.ChangeTracker.Entries<AppUsageSession>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity)).Distinct().ToArray();
        foreach (var session in affected)
        {
            if (session.AppId == App.IdleAppId || session.AppId == App.UnknownAppId) continue;
            var left = session.StartTime > start ? session.StartTime : start;
            var right = session.EndTime < end ? session.EndTime : end;
            if (left >= right) continue;
            var originalId = session.AppId; var originalEnd = session.EndTime;
            if (session.StartTime < left)
            {
                session.UpdateEndTime(left);
                context.AppUsageSessions.Add(AppUsageSession.Create(App.UnknownAppId,
                    new ScreenTimeTracker.ScreenTime.Domain.ValueObjects.TimeRange(left, right)));
            }
            else { session.Reattribute(App.UnknownAppId); session.UpdateEndTime(right); }
            if (right < originalEnd)
                context.AppUsageSessions.Add(AppUsageSession.Create(originalId,
                    new ScreenTimeTracker.ScreenTime.Domain.ValueObjects.TimeRange(right, originalEnd)));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Foreground queue overflow: {Count} events; gap {Milliseconds} ms.")]
    private static partial void LogOverflow(ILogger logger, long count, double milliseconds);

    private async Task<Guid> IdentifyApp(
        ScreenTimeDbContext context,
        IExecutableMetadataProvider executableMetadataProvider,
        DateTimeOffset occurredAt,
        WindowInfo windowInfo,
        CancellationToken cancellationToken
    )
    {
        string processName = windowInfo.ProcessName;
        string? executablePath = windowInfo.ExecutablePath;

        var settings = await settingsSnapshot.GetAsync(cancellationToken);
        App? app = await context.Apps.FirstOrDefaultAsync(
            p => p.ProcessName == processName,
            cancellationToken
        );

        // 新增 App 信息
        if (app is null)
        {
            if (executablePath is null)
                app = App.CreateDiscovered(occurredAt, processName, processName, executablePath);
            else
            {
                using ExecutableMetadata metadata = executableMetadataProvider.GetMetadata(
                    executablePath
                );
                string name = metadata.Name is null ? processName : metadata.Name;
                app = App.CreateDiscovered(occurredAt, name, processName, executablePath);
                string? iconPath = await EnsureIconUpdated(
                    app,
                    metadata,
                    settings.AppTracking.IconDirectory,
                    cancellationToken
                );
                app.RefreshMetadata(occurredAt, executablePath, iconPath);
            }
            context.Apps.Add(app);
        }
        // 已有 App 信息
        else
        {
            if (app.AllowMetadataAutoRefresh && (app.NeedsMetadataRefresh(occurredAt, settings.AppTracking.MetadataStaleThreshold) ||
                !string.Equals(app.ExecutablePath, executablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                if (executablePath is null)
                    app.RefreshMetadata(occurredAt, executablePath, null);
                else
                {
                    using ExecutableMetadata metadata = executableMetadataProvider.GetMetadata(
                        executablePath
                    );
                    string? iconPath = await EnsureIconUpdated(
                        app,
                        metadata,
                        settings.AppTracking.IconDirectory,
                        cancellationToken
                    );
                    app.RefreshMetadata(occurredAt, executablePath, iconPath);
                }
            }
        }
        return app.Id;
    }

    private static async Task<string?> EnsureIconUpdated(
        App app,
        ExecutableMetadata metadata,
        string iconDir,
        CancellationToken cancellationToken
    )
    {
        if (metadata.IconStream is null) return null;
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await metadata.IconStream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > ManagedIcons.MaxBytes) return null;
            output.Write(buffer, 0, read);
        }
        try { return await ManagedIcons.SaveAsync(output.ToArray(), cancellationToken); }
        catch (ArgumentException) { return null; }
    }

}

public interface IExecutableMetadataProvider
{
    public ExecutableMetadata GetMetadata(string executablePath);
}

public class ExecutableMetadata(string? name, Stream? iconStream, string? iconFileExtension)
    : IDisposable
{
    public string? Name { get; } = name;
    public Stream? IconStream { get; } = iconStream;
    public string? IconFileExtension { get; } = iconFileExtension;

    public void Dispose()
    {
        IconStream?.Dispose();
        GC.SuppressFinalize(this);
    }
}
