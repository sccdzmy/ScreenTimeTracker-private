using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.ViewModels;
public sealed class StatisticsModel(TrackerClient client, bool details) : PageModel(client)
{
    public bool Details { get; } = details;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today;
    public DateTime TimelineDate { get; set; } = DateTime.Today;
    public string Kind { get; set; } = "apps";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public Guid? SelectedId { get; set; }
    public Guid[] FilterIds { get; set; } = [];
    public bool ExcludeFilter { get; set; }
    public int TopN { get; set; } = 10;
    public bool Daily { get; set; }
    public List<EntityItem> Objects { get; private set; } = [];
    public List<UsageBucket> Buckets { get; private set; } = [];
    public List<UsageBucket> TrendBuckets { get; private set; } = [];
    public List<UsageBucket> HourBuckets { get; private set; } = [];
    public string DateLabel => StartDate.Date == EndDate.Date ? StartDate.ToString(Text.Of("M月d日|MMM d"), System.Globalization.CultureInfo.GetCultureInfo(Text.Language)) : $"{StartDate:M/d} – {EndDate:M/d}";
    public string HourLabel => Text.Of($"{TimelineDate:M月d日} · 小时分布|{TimelineDate.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} · Hourly usage");
    public string TrendLabel => Text.Of(StartDate.Date == EndDate.Date ? "本周趋势|This week" : "每日趋势|Daily trend") + Text.Of(" · 日平均 | · Daily average ") + Text.Duration(TrendBuckets.Count == 0 ? 0 : (long)TrendBuckets.Average(x => x.DurationSeconds));
    public List<RankItem> Ranking { get; private set; } = [];
    public List<RankItem> ChartRanking { get; private set; } = [];
    public List<TimelineItem> Timeline { get; private set; } = [];
    public string Total { get; private set; } = "";
    public string Other { get; private set; } = "";
    public int? NextOffset { get; private set; }
    public int TimelineOffset { get; private set; }
    public int DayCutoffHour { get; private set; }
    public bool NeedsRefresh
    {
        get
        {
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).DateTime;
                var day = now.Hour < DayCutoffHour ? now.Date.AddDays(-1) : now.Date;
                return Details ? TimelineDate.Date == day : StartDate.Date <= day && EndDate.Date >= day;
            }
            catch (TimeZoneNotFoundException) { return false; }
        }
    }
    public Task ChangeDimensionAsync(string kind) { Kind = kind; SelectedId = null; FilterIds = []; Objects = []; Changed(nameof(Objects)); return RefreshAsync(); }
    public override Task RefreshAsync() => LoadAsync(0);
    public Task NextAsync() => NextOffset is { } offset ? LoadAsync(offset) : Task.CompletedTask;
    public Task PreviousAsync() => LoadAsync(Math.Max(0, TimelineOffset - 500));
    private Task LoadAsync(int offset) => RunAsync(async ct =>
    {
        if (TopN is < 1 or > 100) throw new ArgumentException("Top N must be between 1 and 100.");
        if ((EndDate.Date - StartDate.Date).TotalDays > 366 && !Daily && !Details) throw new ArgumentException("Use daily totals for a date range over one year.");
        if (StartDate.Date > EndDate.Date) throw new ArgumentException("Invalid date range.");
        var settings = await Client.GetAsync<UserSettings>("/api/screen-time/user-settings", ct);
        var cutoff = settings.TimeBoundary.DayCutoffHour;
        DayCutoffHour = cutoff;
        List<EntityItem> objects = Objects;
        if (Objects.Count == 0 && Kind.EndsWith("categories", StringComparison.Ordinal)) objects = await Client.GetAsync<List<EntityItem>>($"/api/screen-time/{Kind}", ct);
        else if (Objects.Count == 0) objects = (await Client.GetAsync<PageResult<EntityItem>>($"/api/screen-time/{Kind}/page?pageSize=100", ct)).Items;
        var query = BuildQuery(StartDate, EndDate);
        if (Details)
        {
            TimelineDate = TimelineDate.Date < StartDate.Date ? StartDate : TimelineDate.Date > EndDate.Date ? EndDate : TimelineDate;
            query = BuildQuery(TimelineDate, TimelineDate);
            var page = await Client.GetAsync<PageResult<TimelineItem>>($"/api/screen-time/usage/{Kind}/timeline/page?{query}&offset={offset}", ct);
            ct.ThrowIfCancellationRequested();
            Timeline = page.Items; NextOffset = page.NextOffset; TimelineOffset = offset;
            Changed(nameof(Timeline)); Changed(nameof(NextOffset)); Changed(nameof(TimelineOffset)); Changed(nameof(TimelineDate));
        }
        else
        {
            TimelineDate = TimelineDate.Date < StartDate.Date ? StartDate.Date : TimelineDate.Date > EndDate.Date ? EndDate.Date : TimelineDate.Date;
            var trendStart = StartDate.Date == EndDate.Date ? AddDaysClamped(StartDate.Date, -(int)StartDate.DayOfWeek) : StartDate.Date;
            var trendEnd = StartDate.Date == EndDate.Date ? AddDaysClamped(trendStart, 6) : EndDate.Date;
            var trendTask = Client.GetAsync<List<UsageBucket>>($"/api/screen-time/usage/{Kind}?{BuildQuery(trendStart, trendEnd)}&granularity=Day", ct);
            var hourTask = Client.GetAsync<List<UsageBucket>>($"/api/screen-time/usage/{Kind}?{BuildQuery(TimelineDate, TimelineDate)}&granularity=Hour", ct);
            var bucketTask = Daily && trendStart == StartDate.Date && trendEnd == EndDate.Date ? trendTask
                : !Daily && StartDate.Date == EndDate.Date ? hourTask
                : Client.GetAsync<List<UsageBucket>>($"/api/screen-time/usage/{Kind}?{query}&granularity={(Daily ? "Day" : "Hour")}", ct);
            var distributionTask = Client.GetAsync<Distribution>($"/api/screen-time/usage/{Kind}/distribution?{query}&topN={TopN}", ct);
            await Task.WhenAll(bucketTask, distributionTask, trendTask, hourTask);
            ct.ThrowIfCancellationRequested();
            TrendBuckets = await trendTask; HourBuckets = await hourTask;
            Buckets = await bucketTask; var distribution = await distributionTask; Ranking = distribution.Items;
            ChartRanking = distribution.OthersDurationSeconds > 0 ? [.. Ranking, new RankItem(Guid.Empty, Text.Of("其他|Others"), "#808080", null, distribution.OthersDurationSeconds)] : Ranking;
            Total = Text.Duration(distribution.TotalDurationSeconds);
            Other = $"{Text.Of("其他|Others")}: {distribution.OthersCount} · {Text.Duration(distribution.OthersDurationSeconds)}";
            Changed(nameof(TrendBuckets)); Changed(nameof(HourBuckets)); Changed(nameof(Buckets)); Changed(nameof(Ranking)); Changed(nameof(ChartRanking)); Changed(nameof(Total)); Changed(nameof(Other));
        }
        Changed(nameof(DateLabel)); Changed(nameof(HourLabel)); Changed(nameof(TrendLabel)); Changed(nameof(StartDate)); Changed(nameof(EndDate)); Changed(nameof(TimelineDate));
        DayCutoffHour = cutoff; if (!ReferenceEquals(Objects, objects)) { Objects = objects; Changed(nameof(Objects)); }
    });
    public Task TodayAsync()
    {
        if (!TryGetLogicalToday(out var day)) return Task.CompletedTask;
        StartDate = EndDate = TimelineDate = day; return RefreshAsync();
    }
    public Task Last7DaysAsync()
    {
        if (!TryGetLogicalToday(out var day)) return Task.CompletedTask;
        EndDate = TimelineDate = day; StartDate = AddDaysClamped(day, -6); Daily = true; return RefreshAsync();
    }
    private bool TryGetLogicalToday(out DateTime day)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).DateTime;
            day = now.Hour < DayCutoffHour ? now.Date.AddDays(-1) : now.Date; return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { day = default; Status = Text.Of("请输入有效的时区并刷新。|Enter a valid time zone and refresh."); return false; }
    }
    private static DateTime AddDaysClamped(DateTime date, int days) => new((long)Math.Clamp(date.Ticks + (double)days * TimeSpan.TicksPerDay, DateTime.MinValue.Ticks, DateTime.MaxValue.Date.Ticks));
    public Task ShiftPeriodAsync(int direction)
    {
        int days = (EndDate.Date - StartDate.Date).Days + 1;
        double offset = (double)days * Math.Sign(direction);
        if (StartDate.Ticks + offset * TimeSpan.TicksPerDay < DateTime.MinValue.Ticks || EndDate.Ticks + offset * TimeSpan.TicksPerDay > DateTime.MaxValue.Ticks) return Task.CompletedTask;
        StartDate = StartDate.AddDays(offset); EndDate = EndDate.AddDays(offset); TimelineDate = TimelineDate.AddDays(offset);
        return RefreshAsync();
    }
    public Task SelectTrendDayAsync(DateTime date)
    {
        if (StartDate.Date == EndDate.Date) StartDate = EndDate = date.Date;
        TimelineDate = date.Date;
        return RefreshAsync();
    }
    private string BuildQuery(DateTime start, DateTime end)
    {
        var query = TrackerClient.UsageQuery(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), TimeZoneId, FilterIds.Length == 0 ? SelectedId : null);
        return query + string.Concat(FilterIds.Select(id => $"&{(ExcludeFilter ? "excludedIds" : "includedIds")}={id}"));
    }
    public Task MoreObjectsAsync() => RunAsync(async ct =>
    {
        if (Kind.EndsWith("categories", StringComparison.Ordinal)) return;
        var page = await Client.GetAsync<PageResult<EntityItem>>($"/api/screen-time/{Kind}/page?pageSize=100&offset={Objects.Count}", ct);
        ct.ThrowIfCancellationRequested(); Objects = [.. Objects, .. page.Items]; Changed(nameof(Objects));
    }, false);
    public override void Dispose() { base.Dispose(); Buckets.Clear(); TrendBuckets.Clear(); HourBuckets.Clear(); ChartRanking.Clear(); Ranking.Clear(); Timeline.Clear(); Objects.Clear(); }
}
