using Mediator;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackAppUsage;

public class UserBecameActiveHandler(
    TimeProvider timeProvider,
    IForegroundWindowMonitor foregroundWindowMonitor,
    ForegroundWindowProcessor foregroundWindowProcessor
) : INotificationHandler<UserBecameActiveEvent>
{
    public async ValueTask Handle(
        UserBecameActiveEvent notification,
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
