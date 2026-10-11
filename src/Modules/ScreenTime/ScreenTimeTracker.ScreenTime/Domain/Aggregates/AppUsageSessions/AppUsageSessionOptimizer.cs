using ScreenTimeTracker.ScreenTime.Domain.Services;

namespace ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;

public record AppUsageSessionOptimizationResult(IReadOnlySet<AppUsageSession> SessionsToRemove);

public static class AppUsageSessionOptimizer
{
    public static AppUsageSessionOptimizationResult Optimize(IReadOnlyList<AppUsageSession> sessions,
        TimeSpan mergeTolerance, TimeSpan minValidDuration) => new(SessionMergeAlgorithm.Optimize(sessions,
            mergeTolerance, minValidDuration, s => s.AppId, s => s.StartTime, s => s.EndTime,
            (s, end) => s.UpdateEndTime(end), s => s.MarkAsOptimized()));
}
