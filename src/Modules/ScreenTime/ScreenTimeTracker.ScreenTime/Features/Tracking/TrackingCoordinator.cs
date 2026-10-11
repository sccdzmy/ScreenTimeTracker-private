using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

/// <summary>One owner for session transitions and their database commits. Failed operations restore memory.</summary>
public sealed class TrackingCoordinator(ActiveAppUsageSessionStore apps, ActiveWebsiteUsageSessionStore websites) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate, apps, websites);
    }
    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;
        private readonly ActiveAppUsageSessionStore _apps;
        private readonly ActiveWebsiteUsageSessionStore _websites;
        private readonly ActiveAppUsageSessionState? _appState;
        private readonly ActiveWebsiteUsageSessionState? _websiteState, _savedWebsite;
        private bool _committed;
        internal Lease(SemaphoreSlim gate, ActiveAppUsageSessionStore apps, ActiveWebsiteUsageSessionStore websites)
        {
            _gate = gate; _apps = apps; _websites = websites;
            _appState = apps.Current; _websiteState = websites.Current; _savedWebsite = websites.LastSaved;
        }
        public void Complete() => _committed = true;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is null) return;
            if (!_committed) { _apps.Current = _appState; _websites.Current = _websiteState; _websites.LastSaved = _savedWebsite; }
            gate.Release();
        }
    }
}
