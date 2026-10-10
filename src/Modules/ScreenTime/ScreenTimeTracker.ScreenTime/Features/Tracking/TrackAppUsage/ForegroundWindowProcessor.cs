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
    ILogger<ForegroundWindowProcessor> logger,
    IServiceScopeFactory serviceScopeFactory,
    UserIdleStore userIdleStore,
    SystemSuspendStore systemSuspendStore
) : BackgroundService
{
    private readonly Channel<ForegroundWindowChangedMessage> _channel =
        Channel.CreateUnbounded<ForegroundWindowChangedMessage>(
            new UnboundedChannelOptions { SingleReader = true }
        );

    private DateTimeOffset _lastProcessedAt = DateTimeOffset.MinValue;

    public void Enqueue(ForegroundWindowChangedMessage message)
    {
        _channel.Writer.TryWrite(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken))
            {
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
                    await ProcessWindowChangeAsync(message, stoppingToken);
                    _lastProcessedAt = message.OccurredAt;
                }
                catch (Exception ex)
                {
                    LogProcessWindowChangeFailed(
                        logger,
                        message.WindowInfo?.ProcessName ?? App.UnknownAppProcessName,
                        ex
                    );
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
        CancellationToken cancellationToken
    )
    {
        using var scope = serviceScopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var executableMetadataProvider =
            scope.ServiceProvider.GetRequiredService<IExecutableMetadataProvider>();
        var activeSessionStore =
            scope.ServiceProvider.GetRequiredService<ActiveAppUsageSessionStore>();

        Guid appId;
        if (message.WindowInfo is null)
            appId = App.UnknownAppId;
        else
            appId = await IdentifyApp(
                context,
                executableMetadataProvider,
                message.OccurredAt,
                message.WindowInfo,
                cancellationToken
            );

        if (activeSessionStore.Current is null)
            activeSessionStore.Current = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        else if (activeSessionStore.Current.AppId != appId)
        {
            var activeSession = activeSessionStore.Take();
            if (activeSession is not null)
            {
                await context.PersistActiveSessionAsync(
                    activeSession,
                    message.OccurredAt,
                    cancellationToken
                );
            }
            activeSessionStore.Current = new ActiveAppUsageSessionState(appId, message.OccurredAt);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Guid> IdentifyApp(
        ScreenTimeDbContext context,
        IExecutableMetadataProvider executableMetadataProvider,
        DateTimeOffset occurredAt,
        WindowInfo windowInfo,
        CancellationToken cancellationToken
    )
    {
        string processName = windowInfo.ProcessName;
        string? executablePath = windowInfo.ExecutablePath;

        UserSettings settings = await context
            .UserSettings.AsNoTracking()
            .SingleAsync(cancellationToken: cancellationToken);
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
            if (app.NeedsMetadataRefresh(occurredAt, settings.AppTracking.MetadataStaleThreshold))
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
