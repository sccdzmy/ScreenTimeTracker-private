using ScreenTimeTracker.ScreenTime.Domain.Services;

namespace ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;

public record WebsiteUsageSessionOptimizationResult(IReadOnlySet<WebsiteUsageSession> SessionsToRemove);

public static class WebsiteUsageSessionOptimizer
{
    public static WebsiteUsageSessionOptimizationResult Optimize(IReadOnlyList<WebsiteUsageSession> sessions,
        TimeSpan mergeTolerance, TimeSpan minValidDuration) => new(SessionMergeAlgorithm.Optimize(sessions,
            mergeTolerance, minValidDuration, s => s.WebsiteId, s => s.StartTime, s => s.EndTime,
            (s, end) => s.UpdateEndTime(end), s => s.MarkAsOptimized()));
}
