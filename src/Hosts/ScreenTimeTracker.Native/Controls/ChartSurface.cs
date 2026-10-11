using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.Controls;
public sealed class ChartSurface : FrameworkElement
{
    private readonly DrawingVisual _drawing = new();
    private readonly List<(Rect Bounds, string Text)> _hits = [];
    private readonly List<(Geometry Shape, string Text)> _sectors = [];
    public IReadOnlyList<UsageBucket> Buckets { get; set; } = [];
    public IReadOnlyList<RankItem> Ranking { get; set; } = [];
    public IReadOnlyList<TimelineItem> Timeline { get; set; } = [];
    public string Mode { get; set; } = "bars";
    public string AxisMode { get; set; } = "auto";
    public DateTime? SelectedDay { get; set; }
    public bool ShowAverage { get; set; }
    public event EventHandler<BucketSelectionEventArgs>? BucketSelected;
    private readonly List<(Rect Bounds, UsageBucket Bucket)> _bucketHits = [];
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;
    public DateTimeOffset? WindowStart { get; set; }
    public DateTimeOffset? WindowEnd { get; set; }
    public int FirstRow { get; set; }
    public ChartSurface() { AddVisualChild(_drawing); MinHeight = 100; SizeChanged += (_, _) => Redraw(); MouseMove += Hover;
        MouseLeftButtonUp += (_, e) => { var point = e.GetPosition(this); var hit = _bucketHits.FirstOrDefault(x => x.Bounds.Contains(point)); if (hit.Bucket is not null) BucketSelected?.Invoke(this, new(hit.Bucket)); };
        MouseLeave += (_, _) => ToolTip = null; }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));
    private static SolidColorBrush Color(string value)
    {
        try { var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
        catch (FormatException) { return Brushes.SteelBlue; }
    }
    private void Label(DrawingContext dc, string value, double x, double y, double size = 11)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, (Brush)(TryFindResource("AppForeground") ?? SystemColors.WindowTextBrush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        text.MaxTextWidth = Math.Max(10, ActualWidth - x); text.MaxTextHeight = 22; text.Trimming = TextTrimming.CharacterEllipsis; dc.DrawText(text, new Point(x, y));
    }
    public void Redraw()
    {
        _hits.Clear(); _sectors.Clear(); _bucketHits.Clear(); using var dc = _drawing.RenderOpen();
        double width = ActualWidth, height = ActualHeight; if (width < 40 || height < 40) return;
        if (Mode == "bars")
        {
            DrawBars(dc, width, height);
        }
        else if (Mode == "distribution")
        {
            long total = Ranking.Sum(x => x.DurationSeconds); if (total <= 0) { Label(dc, Text.Of("没有记录|No records"), 8, 12); return; }
            var center = new Point(height / 2, height / 2); double radius = height / 2 - 18, angle = -Math.PI / 2;
            for (int i = 0; i < Ranking.Count; ++i)
            {
                var item = Ranking[i]; if (item.DurationSeconds <= 0) continue;
                double end = angle + Math.Min(2 * Math.PI - 0.00001, 2 * Math.PI * item.DurationSeconds / total);
                var geometry = new StreamGeometry(); using (var path = geometry.Open())
                { path.BeginFigure(center, true, true); path.LineTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), true, false); path.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, end - angle > Math.PI, SweepDirection.Clockwise, true, false); }
                geometry.Freeze(); _sectors.Add((geometry, $"{item.Name} · {Text.Duration(item.DurationSeconds, true)}")); dc.DrawGeometry(Color(item.Color), null, geometry); angle = end;
                if (i < 8) Label(dc, $"{item.Name} · {Text.Duration(item.DurationSeconds, true)}", height + 8, 8 + i * 26);
            }
        }
        else
        {
            if (Timeline.Count == 0) { Label(dc, Text.Of("没有记录|No records"), 8, 12); return; }
            var start = WindowStart ?? Timeline.Min(x => x.StartTime); var end = WindowEnd ?? Timeline.Max(x => x.EndTime); double span = (end - start).TotalSeconds; if (span <= 0) return;
            var groups = Timeline.GroupBy(x => x.Id).Skip(FirstRow).Take(Math.Max(1, (int)(height - 30) / 26)); int row = 0;
            foreach (var group in groups)
            {
                Label(dc, group.First().Name, 4, row * 26 + 4);
                // Merge only the pixels drawn, while keeping the exact records for the grid and tooltip.
                var last = Rect.Empty;
                foreach (var item in group.OrderBy(x => x.StartTime))
                {
                    double x = 132 + (width - 140) * (item.StartTime - start).TotalSeconds / span;
                    double w = Math.Max(1, (width - 140) * (item.EndTime - item.StartTime).TotalSeconds / span);
                    var rect = new Rect(Math.Floor(x), row * 26 + 4, Math.Ceiling(w), 18);
                    _hits.Add((rect, $"{item.Name} · {TimeZoneInfo.ConvertTime(item.StartTime, TimeZone):HH:mm:ss}–{TimeZoneInfo.ConvertTime(item.EndTime, TimeZone):HH:mm:ss} · {Text.Duration((long)(item.EndTime - item.StartTime).TotalSeconds, true)}"));
                    if (!last.IsEmpty && last.Right >= rect.Left) last.Union(rect);
                    else { if (!last.IsEmpty) dc.DrawRectangle(Color(group.First().Color), null, last); last = rect; }
                }
                if (!last.IsEmpty) dc.DrawRectangle(Color(group.First().Color), null, last); ++row;
            }
            Label(dc, $"{TimeZoneInfo.ConvertTime(start, TimeZone):MM-dd HH:mm} — {TimeZoneInfo.ConvertTime(end, TimeZone):MM-dd HH:mm}", 132, height - 22);
        }
    }
    private void DrawBars(DrawingContext dc, double width, double height)
    {
        if (Buckets.Count == 0) { Label(dc, Text.Of("没有记录|No records"), 8, 12); return; }
        bool hours = AxisMode == "hours", days = AxisMode == "days";
        long maximum = Buckets.Max(x => x.DurationSeconds);
        double upper = Math.Max(3600, Math.Ceiling(maximum / 3600.0) * 3600);
        double left = 4, right = width - 64, bottom = height - 23, top = 7;
        if (right <= left || bottom <= top) return;
        var pen = new Pen((Brush)(TryFindResource("ChartGrid") ?? SystemColors.GrayTextBrush), 1); pen.Freeze();
        for (int level = 0; level <= 2; ++level)
        {
            if (level == 1 && bottom - top < 35) continue;
            double y = bottom - (bottom - top) * level / 2;
            dc.DrawLine(pen, new(left, y), new(right, y));
            string tick = level == 0 ? "0" : hours ? $"{upper * level / 2 / 60:0}{Text.Of("分钟|min")}" : $"{upper * level / 2 / 3600:0.#}{Text.Of("小时|h")}";
            Label(dc, tick, right + 7, Math.Max(0, y - 8), 10);
        }
        double step = (right - left) / Buckets.Count;
        string[] weekdays = Text.Language == "zh-CN" ? ["日", "一", "二", "三", "四", "五", "六"] : ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        for (int i = 0; i < Buckets.Count; ++i)
        {
            var bucket = Buckets[i]; var time = TimeZoneInfo.ConvertTime(bucket.StartTime, TimeZone);
            double x = left + i * step, h = (bottom - top) * Math.Max(0, bucket.DurationSeconds) / upper;
            var brush = days && time.Date != SelectedDay?.Date ? (Brush)(TryFindResource("ChartMuted") ?? SystemColors.GrayTextBrush) : SystemColors.HighlightBrush;
            double barWidth = Math.Max(1, Math.Min(48, step * 0.60));
            var rectangle = new Rect(x + (step - barWidth) / 2, bottom - h, barWidth, h);
            if (h > 0) dc.DrawRoundedRectangle(brush, null, rectangle, 2, 2);
            var hit = new Rect(x, top, step, bottom - top + 23);
            _hits.Add((hit, $"{time:yyyy-MM-dd HH:mm} · {Text.Duration(bucket.DurationSeconds, true)}")); _bucketHits.Add((hit, bucket));
            if (i % Math.Max(1, (int)Math.Ceiling(Buckets.Count / (hours ? 4.0 : 7.0))) == 0)
            {
                string label = hours ? $"{time.Hour}{Text.Of("时|h")}" : days && Buckets.Count <= 7 ? weekdays[(int)time.DayOfWeek] : time.ToString("M/d", CultureInfo.InvariantCulture);
                Label(dc, label, x + Math.Max(0, (step - 24) / 2), bottom + 3, 10);
            }
        }
        if (ShowAverage && maximum > 0)
        {
            double average = Buckets.Average(x => x.DurationSeconds), y = bottom - (bottom - top) * average / upper;
            var averagePen = new Pen(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.MediumSeaGreen, 1) { DashStyle = DashStyles.Dash }; averagePen.Freeze();
            dc.DrawLine(averagePen, new(left, y), new(right, y));
            ToolTip = null;
        }
    }
    private void Hover(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this); var hit = _hits.FirstOrDefault(x => x.Bounds.Contains(point)); ToolTip = hit.Text ?? _sectors.FirstOrDefault(x => x.Shape.FillContains(point)).Text;
    }
}

public sealed class BucketSelectionEventArgs(UsageBucket bucket) : EventArgs { public UsageBucket Bucket { get; } = bucket; }
