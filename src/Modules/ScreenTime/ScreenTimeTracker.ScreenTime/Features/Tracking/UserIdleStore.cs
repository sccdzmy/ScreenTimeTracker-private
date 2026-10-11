namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

public record UserIdleState(bool IsUserIdle);

public class UserIdleStore
{
    private readonly object _gate = new();
    private UserIdleState _current = new(false);
    private long _version;
    public long Version { get { lock (_gate) return _version; } }
    public UserIdleState Current
    {
        get { lock (_gate) return _current; }
        set { lock (_gate) { if (_current.IsUserIdle != value.IsUserIdle) _version++; _current = value; } }
    }
}
