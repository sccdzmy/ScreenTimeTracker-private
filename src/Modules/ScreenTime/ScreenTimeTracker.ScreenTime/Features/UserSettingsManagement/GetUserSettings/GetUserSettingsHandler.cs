using ScreenTimeTracker.ScreenTime.Features.Tracking;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.UserSettingsManagement.GetUserSettings;

public class GetUserSettingsHandler(UserSettingsSnapshot settingsSnapshot)
    : IRequestHandler<GetUserSettingsQuery, GetUserSettingsResponse>
{
    public async ValueTask<GetUserSettingsResponse> Handle(
        GetUserSettingsQuery request,
        CancellationToken cancellationToken
    )
    {
        var userSettings = await settingsSnapshot.GetAsync(cancellationToken);

        return new GetUserSettingsResponse(
            new AppTrackingSettingsDto(
                (int)userSettings.AppTracking.MetadataStaleThreshold.TotalSeconds,
                (int)userSettings.AppTracking.ActiveUsageSessionAutoSaveInterval.TotalSeconds,
                (int)userSettings.AppTracking.MinValidUsageSessionDuration.TotalSeconds,
                (int)userSettings.AppTracking.UsageSessionMergeTolerance.TotalSeconds,
                (int)userSettings.AppTracking.UsageSessionOptimizationInterval.TotalSeconds
            ),
            new WebsiteTrackingSettingsDto(
                (int)userSettings.WebsiteTracking.ActiveUsageSessionAutoSaveInterval.TotalSeconds,
                (int)userSettings.WebsiteTracking.MinValidUsageSessionDuration.TotalSeconds,
                (int)userSettings.WebsiteTracking.UsageSessionMergeTolerance.TotalSeconds,
                (int)userSettings.WebsiteTracking.UsageSessionOptimizationInterval.TotalSeconds
            ),
            new IdleDetectionSettingsDto(
                userSettings.IdleDetection.IsEnabled,
                (int)userSettings.IdleDetection.InactivityThreshold.TotalSeconds,
                (int)userSettings.IdleDetection.PollingInterval.TotalSeconds
            ),
            new TimeBoundarySettingsDto(userSettings.TimeBoundary.DayCutoffHour)
        );
    }
}
