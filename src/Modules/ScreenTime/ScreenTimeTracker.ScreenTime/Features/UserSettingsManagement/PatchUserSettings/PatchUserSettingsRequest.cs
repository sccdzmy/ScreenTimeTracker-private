using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.UserSettingsManagement.PatchUserSettings;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record PatchUserSettingsRequest(
    OptionalValue<AppTrackingSettingsDto> AppTracking = default,
    OptionalValue<WebsiteTrackingSettingsDto> WebsiteTracking = default,
    OptionalValue<IdleDetectionSettingsDto> IdleDetection = default,
    OptionalValue<TimeBoundarySettingsDto> TimeBoundary = default
);
