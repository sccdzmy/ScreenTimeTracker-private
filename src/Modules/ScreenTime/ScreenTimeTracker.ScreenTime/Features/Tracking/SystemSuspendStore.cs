namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

public record SystemSuspendState(bool IsSystemSuspendActive);

public class SystemSuspendStore
{
    private readonly object _gate = new();
    private SystemSuspendState _current = new(false);
    private long _version;
    public long Version { get { lock (_gate) return _version; } }
    public SystemSuspendState Current
    {
        get { lock (_gate) return _current; }
        set { lock (_gate) { if (_current.IsSystemSuspendActive != value.IsSystemSuspendActive) _version++; _current = value; } }
    }
}
