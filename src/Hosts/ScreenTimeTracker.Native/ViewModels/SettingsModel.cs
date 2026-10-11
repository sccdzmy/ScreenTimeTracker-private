using System.Globalization;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.ViewModels;
public sealed class SettingEntry(string group, string key, string label, object value)
{
    public string Group { get; } = group;
    public string Key { get; } = key;
    public string Label { get; } = Text.Of(label);
    public bool IsBoolean { get; } = value is bool;
    public string Value { get; set; } = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    public bool BoolValue { get; set; } = value is true;
}
public sealed class SettingsModel(TrackerClient client) : PageModel(client)
{
    public List<SettingEntry> Entries { get; private set; } = [];
    public bool AutoStart { get; set; }
    public bool SilentStart { get; set; }
    public string Language { get; set; } = Text.Language;
    public string Pairing { get; private set; } = "";
    public override Task RefreshAsync() => RunAsync(async ct =>
    {
        var localTask = Client.GetAsync<LocalSettings>("/api/desktop-settings/local-settings", ct);
        var userTask = Client.GetAsync<UserSettings>("/api/screen-time/user-settings", ct);
        await Task.WhenAll(localTask, userTask); ct.ThrowIfCancellationRequested();
        var local = await localTask; var user = await userTask; AutoStart = local.IsAutoStartEnabled; SilentStart = local.IsSilentStartEnabled; Language = local.Language;
        Entries = [
            new("timeBoundary", "dayCutoffHour", "一天起点（0–23）|Day start hour (0–23)", user.TimeBoundary.DayCutoffHour),
            new("idleDetection", "isEnabled", "启用空闲检测|Detect idle time", user.IdleDetection.IsEnabled),
            new("idleDetection", "inactivityThresholdSeconds", "空闲判定阈值（秒）|Idle threshold (seconds)", user.IdleDetection.InactivityThresholdSeconds),
            new("idleDetection", "pollingIntervalSeconds", "空闲检查间隔（秒）|Idle check interval (seconds)", user.IdleDetection.PollingIntervalSeconds),
            new("appTracking", "metadataStaleThresholdMinutes", "应用元数据刷新间隔（分钟）|App metadata interval (minutes)", user.AppTracking.MetadataStaleThresholdMinutes),
            new("appTracking", "activeUsageSessionAutoSaveIntervalSeconds", "应用记录保存间隔（秒）|App autosave interval (seconds)", user.AppTracking.ActiveUsageSessionAutoSaveIntervalSeconds),
            new("appTracking", "minValidUsageSessionDurationSeconds", "应用最短有效记录（秒）|App minimum session (seconds)", user.AppTracking.MinValidUsageSessionDurationSeconds),
            new("appTracking", "usageSessionMergeToleranceSeconds", "应用记录合并容差（秒）|App merge tolerance (seconds)", user.AppTracking.UsageSessionMergeToleranceSeconds),
            new("appTracking", "usageSessionOptimizationIntervalMinutes", "应用记录整理间隔（分钟）|App optimization interval (minutes)", user.AppTracking.UsageSessionOptimizationIntervalMinutes),
            new("websiteTracking", "activeUsageSessionAutoSaveIntervalSeconds", "网站记录保存间隔（秒）|Website autosave interval (seconds)", user.WebsiteTracking.ActiveUsageSessionAutoSaveIntervalSeconds),
            new("websiteTracking", "minValidUsageSessionDurationSeconds", "网站最短有效记录（秒）|Website minimum session (seconds)", user.WebsiteTracking.MinValidUsageSessionDurationSeconds),
            new("websiteTracking", "usageSessionMergeToleranceSeconds", "网站记录合并容差（秒）|Website merge tolerance (seconds)", user.WebsiteTracking.UsageSessionMergeToleranceSeconds),
            new("websiteTracking", "usageSessionOptimizationIntervalMinutes", "网站记录整理间隔（分钟）|Website optimization interval (minutes)", user.WebsiteTracking.UsageSessionOptimizationIntervalMinutes)
        ];
        Changed(nameof(Entries)); Changed(nameof(AutoStart)); Changed(nameof(SilentStart)); Changed(nameof(Language));
    });
    public Task SaveAsync() => RunAsync(async ct =>
    {
        var groups = new Dictionary<string, Dictionary<string, object>>();
        foreach (var entry in Entries)
        {
            if (!groups.TryGetValue(entry.Group, out var group)) groups[entry.Group] = group = [];
            group[entry.Key] = entry.IsBoolean ? entry.BoolValue : int.Parse(entry.Value, CultureInfo.InvariantCulture);
        }
        await Client.ChangeAsync(HttpMethod.Patch, "/api/screen-time/user-settings", groups, ct);
        await Client.ChangeAsync(HttpMethod.Patch, "/api/desktop-settings/local-settings", new { isAutoStartEnabled = AutoStart, isSilentStartEnabled = SilentStart, Language }, ct);
        Text.Language = Language;
    }, false);
    public Task PairAsync() => RunAsync(async ct =>
    {
        var code = await Client.PostAsync<PairingCode>("/api/auth/pairing-code", new { }, ct); ct.ThrowIfCancellationRequested();
        Pairing = $"{code.Code} · {Text.Of("有效期 5 分钟|Valid for 5 minutes")}"; Changed(nameof(Pairing));
    }, false);
    public Task RevokeAsync() => RunAsync(ct => Client.ChangeAsync(HttpMethod.Delete, "/api/auth/extensions", null, ct), false);
}
