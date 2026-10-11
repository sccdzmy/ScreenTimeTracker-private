namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public sealed record ForegroundBatch(ForegroundWindowChangedMessage Latest, DateTimeOffset? GapStart = null,
    long DroppedCount = 0, long IdleVersion = 0, long SuspendVersion = 0,
    long? LatestIdleVersion = null, long? LatestSuspendVersion = null, DateTimeOffset? LatestEpochStartedAt = null);

/// <summary>Non-blocking producer; at most 256 transitions plus one overflow summary.</summary>
public sealed class ForegroundEventBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<ForegroundBatch> _queue = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private ForegroundBatch? _lastQueued;
    private ForegroundBatch? _overflow;
    public int Count { get { lock (_gate) return _queue.Count; } }
    public void Enqueue(ForegroundWindowChangedMessage message, long idleVersion, long suspendVersion)
    {
        lock (_gate)
        {
            if (_overflow is { } overflow)
                _overflow = message.OccurredAt >= overflow.Latest.OccurredAt
                    ? overflow with { Latest = message, DroppedCount = overflow.DroppedCount + 1, LatestEpochStartedAt = (overflow.LatestIdleVersion ?? overflow.IdleVersion) != idleVersion ||
                        (overflow.LatestSuspendVersion ?? overflow.SuspendVersion) != suspendVersion ? message.OccurredAt : overflow.LatestEpochStartedAt,
                        LatestIdleVersion = idleVersion, LatestSuspendVersion = suspendVersion }
                    : overflow with { DroppedCount = overflow.DroppedCount + 1 };
            else if (_lastQueued is { } lastQueued && lastQueued.Latest.WindowInfo == message.WindowInfo && _queue.Count != 0 &&
                lastQueued.IdleVersion == idleVersion && lastQueued.SuspendVersion == suspendVersion)
                return; // Same identity: retain the first timestamp, not the latest one.
            else if (_queue.Count == 256)
                _overflow = new ForegroundBatch(message, message.OccurredAt, 1, idleVersion, suspendVersion);
            else { var batch = new ForegroundBatch(message, IdleVersion: idleVersion, SuspendVersion: suspendVersion); _queue.Enqueue(batch); _lastQueued = batch; }
            Signal();
        }
    }
    public async Task<ForegroundBatch> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_queue.Count != 0)
                {
                    var message = _queue.Dequeue();
                    if (_queue.Count == 0) _lastQueued = null;
                    return message;
                }
                if (_overflow is { } overflow) { _overflow = null; return overflow; }
            }
            await _ready.WaitAsync(cancellationToken);
        }
    }
    public void Dispose() => _ready.Dispose();
    private void Signal() { if (_ready.CurrentCount == 0) _ready.Release(); }
}
