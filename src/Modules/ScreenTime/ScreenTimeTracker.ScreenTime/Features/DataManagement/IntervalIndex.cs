using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
namespace ScreenTimeTracker.ScreenTime.Features.DataManagement;
// Imports arrive sorted. Binary searches avoid scanning every historical interval.
internal sealed class IntervalIndex
{
    private readonly List<TimeRange> _existing = [];
    private readonly List<TimeRange> _added = [];
    public IntervalIndex(IEnumerable<TimeRange> ranges)
    {
        foreach (var range in ranges.OrderBy(x => x.Start))
        {
            if (_existing.Count != 0 && range.Start <= _existing[^1].End)
            {
                var previous = _existing[^1];
                _existing[^1] = new TimeRange(previous.Start, range.End > previous.End ? range.End : previous.End);
            }
            else _existing.Add(range);
        }
    }
    public bool Overlaps(TimeRange range) => Overlaps(_existing, range) || Overlaps(_added, range);
    private static bool Overlaps(List<TimeRange> ranges, TimeRange range)
    {
        int low = 0, high = ranges.Count;
        while (low < high) { int middle = low + (high - low) / 2; if (ranges[middle].Start < range.End) low = middle + 1; else high = middle; }
        return low > 0 && ranges[low - 1].End > range.Start;
    }
    public void Add(TimeRange range)
    {
        if (_added.Count != 0 && range.Start < _added[^1].End) throw new ArgumentException("Intervals must be ordered and disjoint.");
        _added.Add(range);
    }
}
