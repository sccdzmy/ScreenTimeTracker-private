namespace ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;

public class ActiveWebsiteUsageSessionStore
{
    // Accessed only while holding TrackingCoordinator.
    public ActiveWebsiteUsageSessionState? LastSaved { get; set; }
    private ActiveWebsiteUsageSessionState? _activeWebsiteUsageSessionState;
    public ActiveWebsiteUsageSessionState? Current
    {
        get => Volatile.Read(ref _activeWebsiteUsageSessionState);
        set => Interlocked.Exchange(ref _activeWebsiteUsageSessionState, value);
    }

    public ActiveWebsiteUsageSessionState? Take() =>
        Interlocked.Exchange(ref _activeWebsiteUsageSessionState, null);
}
