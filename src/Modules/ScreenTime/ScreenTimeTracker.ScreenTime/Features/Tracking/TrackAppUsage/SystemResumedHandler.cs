using Mediator;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public class SystemResumedHandler(
    TimeProvider timeProvider,
    IForegroundWindowMonitor foregroundWindowMonitor,
    ForegroundWindowProcessor foregroundWindowProcessor
) : INotificationHandler<SystemResumedEvent>
{
    public async ValueTask Handle(
        SystemResumedEvent notification,
        CancellationToken cancellationToken
    )
    {
        var windowInfo = foregroundWindowMonitor.GetForegroundWindow();
        foregroundWindowProcessor.Enqueue(
            new ForegroundWindowChangedMessage(windowInfo, timeProvider.GetUtcNow())
        );

        return;
    }
}
