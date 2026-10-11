namespace ScreenTimeTracker.ScreenTime.Features.UserSettingsManagement.GetUserSettings;

public record GetUserSettingsResponse(
    AppTrackingSettingsDto AppTracking,
    WebsiteTrackingSettingsDto WebsiteTracking,
    IdleDetectionSettingsDto IdleDetection,
    TimeBoundarySettingsDto TimeBoundary
);

public record AppTrackingSettingsDto(
    int MetadataStaleThresholdMinutes,
    int ActiveUsageSessionAutoSaveIntervalSeconds,
    int MinValidUsageSessionDurationSeconds,
    int UsageSessionMergeToleranceSeconds,
    int UsageSessionOptimizationIntervalMinutes
);

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
