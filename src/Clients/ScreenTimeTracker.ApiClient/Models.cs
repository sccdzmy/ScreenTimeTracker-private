namespace ScreenTimeTracker.ApiClient;
public sealed record SystemInfo(int ProtocolVersion, string[] Capabilities)
{
    private static readonly string[] RequiredCapabilities = ["native-session", "paged-management", "paged-timeline", "icons", "extension-pairing", "import-export"];
    public void EnsureNativeCompatible()
    {
        if (ProtocolVersion != 1) throw new InvalidOperationException("Incompatible backend protocol.");
        foreach (var capability in RequiredCapabilities)
            if (Capabilities is null || !Capabilities.Contains(capability, StringComparer.Ordinal))
                throw new InvalidOperationException("Backend does not support required capability: " + capability);
    }
}
public sealed record NativeSession(string Token, int ExpiresInSeconds);
public sealed record ControlMessage(string Type, string? BaseUrl = null, string? Token = null, int? Page = null);
public sealed record PageResult<T>(List<T> Items, int? NextOffset);
public sealed record EntityItem(Guid Id, string Name, string Color, string? IconId, bool IsSystem = false,
    Guid? AppCategoryId = null, Guid? WebsiteCategoryId = null, string? ProcessName = null, string? Host = null,
    bool AllowMetadataAutoRefresh = false)
{
    public Guid? CategoryId => AppCategoryId ?? WebsiteCategoryId;
    public string Identity => Host ?? ProcessName ?? "";
    public override string ToString() => Name;
}
public sealed record UsageBucket(DateTimeOffset StartTime, DateTimeOffset EndTime, long DurationSeconds);
public sealed record RankItem(Guid Id, string Name, string Color, string? IconId, long DurationSeconds)
{
    public string Duration => FormatDuration(DurationSeconds);
    public static string FormatDuration(long seconds) => $"{seconds / 3600}h {seconds % 3600 / 60}m {seconds % 60}s";
}
public sealed record Distribution(List<RankItem> Items, int TotalCount, long TotalDurationSeconds, int OthersCount, long OthersDurationSeconds);
public sealed record TimelineItem(Guid Id, string Name, string Color, DateTimeOffset StartTime, DateTimeOffset EndTime)
{
    public string Duration => RankItem.FormatDuration((long)(EndTime - StartTime).TotalSeconds);
}
public sealed record PairingCode(string Code, int ExpiresInSeconds);
public sealed record IconUpload(string IconId);
public sealed record LocalSettings(string DefaultUIOpenMode, bool IsAutoStartEnabled, bool IsSilentStartEnabled, string Language);
public sealed record UserSettings(AppTrackingSettings AppTracking, WebsiteTrackingSettings WebsiteTracking, IdleSettings IdleDetection, TimeBoundarySettings TimeBoundary);
public sealed record AppTrackingSettings(int MetadataStaleThresholdMinutes, int ActiveUsageSessionAutoSaveIntervalSeconds,
    int MinValidUsageSessionDurationSeconds, int UsageSessionMergeToleranceSeconds, int UsageSessionOptimizationIntervalMinutes);
public sealed record WebsiteTrackingSettings(int ActiveUsageSessionAutoSaveIntervalSeconds, int MinValidUsageSessionDurationSeconds,
    int UsageSessionMergeToleranceSeconds, int UsageSessionOptimizationIntervalMinutes);
public sealed record IdleSettings(bool IsEnabled, int InactivityThresholdSeconds, int PollingIntervalSeconds);
public sealed record TimeBoundarySettings(int DayCutoffHour);
