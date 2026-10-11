using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.UserSettingsManagement.PatchUserSettings;

public record PatchUserSettingsCommand(
    OptionalValue<AppTrackingSettingsDto> AppTracking = default,
    OptionalValue<WebsiteTrackingSettingsDto> WebsiteTracking = default,
    OptionalValue<IdleDetectionSettingsDto> IdleDetection = default,
    OptionalValue<TimeBoundarySettingsDto> TimeBoundary = default
) : IRequest<ErrorOr<Updated>>;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record AppTrackingSettingsDto(
    int MetadataStaleThresholdMinutes,
    int ActiveUsageSessionAutoSaveIntervalSeconds,
    int MinValidUsageSessionDurationSeconds,
    int UsageSessionMergeToleranceSeconds,
    int UsageSessionOptimizationIntervalMinutes
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record WebsiteTrackingSettingsDto(
    int ActiveUsageSessionAutoSaveIntervalSeconds,
    int MinValidUsageSessionDurationSeconds,
    int UsageSessionMergeToleranceSeconds,
    int UsageSessionOptimizationIntervalMinutes
);

public record IdleDetectionSettingsDto(
    bool IsEnabled,
    int InactivityThresholdSeconds,
    int PollingIntervalSeconds
);

public record TimeBoundarySettingsDto(int DayCutoffHour);
