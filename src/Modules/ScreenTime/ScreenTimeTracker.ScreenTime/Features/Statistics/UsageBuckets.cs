using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;

namespace ScreenTimeTracker.ScreenTime.Features.Statistics;

/// <summary>UTC buckets share the same logical-day and daylight-saving rules for all identities.</summary>
public enum UsageBucketGranularity { Hour, Day }

public sealed class UsageBuckets
{
    public IReadOnlyList<TimeRange> Ranges { get; }
    private readonly TimeSpan[] _durations;
    private readonly TimeRange _window;

    public UsageBuckets(DateOnly start, DateOnly end, UsageBucketGranularity granularity,
        TimeZoneInfo zone, int cutoff)
    {
        _window = LogicalDay.CalculateUtcWindow(start, end, cutoff, zone);
        var ranges = new List<TimeRange>();
        if (granularity == UsageBucketGranularity.Hour)
            for (var utc = _window.Start; utc < _window.End; utc = utc.AddHours(1))
                ranges.Add(new TimeRange(utc, utc.AddHours(1)));
        else if (granularity == UsageBucketGranularity.Day)
            for (var day = start; day <= end; day = day.AddDays(1))
                ranges.Add(LogicalDay.From(day, cutoff, zone).UtcWindow);
        Ranges = ranges;
        _durations = new TimeSpan[ranges.Count];
    }

    public void Add(TimeRange session)
    {
        var part = session.Intersect(_window);
        if (part is null) return;
        int low = 0, high = Ranges.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (Ranges[mid].End > part.Value.Start) high = mid;
            else low = mid + 1;
        }
        for (var i = low; i < Ranges.Count && Ranges[i].Start < part.Value.End; i++)
            _durations[i] += Ranges[i].OverlapDuration(part.Value);
    }

    public long Seconds(int index) => (long)_durations[index].TotalSeconds;
}
