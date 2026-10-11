namespace ScreenTimeTracker.ScreenTime.Domain.Services;

public static class SessionMergeAlgorithm
{
    public static bool BeyondTolerance(DateTimeOffset end, DateTimeOffset nextStart, TimeSpan tolerance) =>
        nextStart > end && nextStart - end > tolerance;

    public static IReadOnlySet<T> Optimize<T>(IReadOnlyList<T> sessions, TimeSpan tolerance, TimeSpan minimum,
        Func<T, Guid> identity, Func<T, DateTimeOffset> start, Func<T, DateTimeOffset> end,
        Action<T, DateTimeOffset> updateEnd, Action<T> markOptimized) where T : class
    {
        var removed = new HashSet<T>();
        for (var i = 0; i < sessions.Count; i++)
        {
            var current = sessions[i];
            for (var j = i + 1; j < sessions.Count; j++)
            {
                var next = sessions[j];
                if (BeyondTolerance(end(current), start(next), tolerance)) break;
                if (identity(current) != identity(next)) continue;
                updateEnd(current, end(next));
                for (var k = i + 1; k <= j; k++) removed.Add(sessions[k]);
                i = j;
            }
        }
        foreach (var session in sessions)
        {
            if (removed.Contains(session)) continue;
            if (end(session) - start(session) < minimum) removed.Add(session);
            else markOptimized(session);
        }
        return removed;
    }
}
