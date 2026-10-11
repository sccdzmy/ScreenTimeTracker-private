using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

public sealed record TrackingSettings(AppTrackingSettings AppTracking, WebsiteTrackingSettings WebsiteTracking,
    IdleDetectionSettings IdleDetection, TimeBoundarySettings TimeBoundary);

/// <summary>Contains immutable values, never a tracked EF entity. Invalidations happen after commit.</summary>
public sealed class UserSettingsSnapshot(IServiceScopeFactory scopeFactory) : IDisposable
{
    public void Dispose() => _refresh.Dispose();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _gate = new();
    private long _version;
    private TrackingSettings? _current;
    public void Invalidate() { lock (_gate) { _version++; _current = null; } }
    public async Task<TrackingSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { if (_current is { } current) return current; }
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                long version;
                lock (_gate) { if (_current is { } current) return current; version = _version; }
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
                var settings = await db.UserSettings.AsNoTracking().SingleAsync(cancellationToken);
                var snapshot = new TrackingSettings(settings.AppTracking, settings.WebsiteTracking, settings.IdleDetection, settings.TimeBoundary);
                lock (_gate)
                {
                    if (version != _version) continue;
                    _current = snapshot;
                    return snapshot;
                }
            }
        }
        finally { _refresh.Release(); }
    }
}
