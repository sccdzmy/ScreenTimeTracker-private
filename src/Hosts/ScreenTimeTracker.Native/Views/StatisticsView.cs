using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Native.Controls;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;
public sealed class StatisticsView : UserControl
{
    private readonly StatisticsModel _model;
    private readonly ChartSurface _chart = new();
    private readonly ChartSurface _pie = new() { Mode = "distribution" };
    public StatisticsView(StatisticsModel model)
    {
        _model = model; DataContext = model;
        var root = new DockPanel { Margin = new Thickness(24) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var filters = new WrapPanel(); top.Children.Add(filters);
        var kinds = new[] { new Choice("apps", "应用|Apps"), new Choice("websites", "网站|Websites"), new Choice("app-categories", "应用分类|App categories"), new Choice("website-categories", "网站分类|Website categories") };
        var kind = new ComboBox { ItemsSource = kinds, Width = 140, SelectedIndex = Array.FindIndex(kinds, x => x.Value == model.Kind) };
        kind.SelectionChanged += async (_, _) => { if (kind.SelectedItem is Choice choice) await model.ChangeDimensionAsync(choice.Value); }; filters.Children.Add(kind);
        filters.Children.Add(DateInput(nameof(model.StartDate), model.StartDate)); filters.Children.Add(DateInput(nameof(model.EndDate), model.EndDate));
        filters.Children.Add(UI.Button("今天|Today", async () => { model.StartDate = model.EndDate = model.TimelineDate = DateTime.Today; await model.RefreshAsync(); RebindDates(top); }));
        filters.Children.Add(UI.Button("近7天|Last 7 days", async () => { model.StartDate = DateTime.Today.AddDays(-6); model.EndDate = model.TimelineDate = DateTime.Today; model.Daily = true; await model.RefreshAsync(); RebindDates(top); }));
        var objects = new ComboBox { Width = 200, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        objects.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Objects)));
        objects.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(model.SelectedId)) { Mode = BindingMode.TwoWay });
        filters.Children.Add(UI.Label("对象筛选|Object filter")); filters.Children.Add(objects);
        filters.Children.Add(UI.Button("全部|All", async () => { model.SelectedId = null; model.FilterIds = []; objects.SelectedIndex = -1; await model.RefreshAsync(); }));
        filters.Children.Add(UI.Button("更多对象|More objects", model.MoreObjectsAsync));
        filters.Children.Add(UI.Button("多选筛选|Multiple filters", async () => { FilterDialog.Show(model); await model.RefreshAsync(); }));
        filters.Children.Add(UI.Input(model, nameof(model.TimeZoneId), 190));
        filters.Children.Add(UI.Button("刷新|Refresh", model.RefreshAsync));
        if (!model.Details)
        {
            var daily = new CheckBox { Content = Text.Of("每日汇总|Daily totals"), Margin = new Thickness(8) };
            daily.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(model.Daily)) { Mode = BindingMode.TwoWay }); filters.Children.Add(daily);
            filters.Children.Add(UI.Label("排行数量|Top N")); filters.Children.Add(UI.Input(model, nameof(model.TopN), 60));
            var total = UI.Label("", 30); total.SetBinding(TextBlock.TextProperty, new Binding(nameof(model.Total))); top.Children.Add(total);
            var other = UI.Label(""); other.SetBinding(TextBlock.TextProperty, new Binding(nameof(model.Other))); top.Children.Add(other);
        }
        else
        {
            var nav = new WrapPanel(); top.Children.Add(nav);
            nav.Children.Add(UI.Label("时间线日期|Timeline day")); nav.Children.Add(DateInput(nameof(model.TimelineDate), model.TimelineDate));
            nav.Children.Add(UI.Button("前一天|Previous day", async () => { if (model.TimelineDate > model.StartDate) { model.TimelineDate = model.TimelineDate.AddDays(-1); await model.RefreshAsync(); RebindDates(top); } }));
            nav.Children.Add(UI.Button("后一天|Next day", async () => { if (model.TimelineDate < model.EndDate) { model.TimelineDate = model.TimelineDate.AddDays(1); await model.RefreshAsync(); RebindDates(top); } }));
            nav.Children.Add(UI.Button("上一页|Previous page", model.PreviousAsync)); nav.Children.Add(UI.Button("下一页|Next page", model.NextAsync));
            nav.Children.Add(UI.Button("上移图表|Chart up", () => { _chart.FirstRow = Math.Max(0, _chart.FirstRow - 5); _chart.Redraw(); return Task.CompletedTask; }));
            nav.Children.Add(UI.Button("下移图表|Chart down", () => { _chart.FirstRow = Math.Min(Math.Max(0, model.Timeline.Select(x => x.Id).Distinct().Count() - 1), _chart.FirstRow + 5); _chart.Redraw(); return Task.CompletedTask; }));
        }
        top.Children.Add(UI.Status(model));
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body);
        if (model.Details)
        {
            _chart.Mode = "timeline"; body.Children.Add(_chart);
            var grid = UI.Grid(("名称|Name", "Name"), ("开始|Start", "StartTime"), ("结束|End", "EndTime"), ("时长|Duration", "Duration"));
            grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Timeline))); Grid.SetRow(grid, 1); body.Children.Add(grid);
        }
        else
        {
            var upper = new TabControl(); body.Children.Add(upper);
            upper.Items.Add(new TabItem { Header = Text.Of("趋势图|Trend"), Content = _chart });
            var values = UI.Grid(("开始|Start", "StartTime"), ("结束|End", "EndTime"), ("秒|Seconds", "DurationSeconds")); values.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Buckets)));
            upper.Items.Add(new TabItem { Header = Text.Of("图表明细|Chart data"), Content = values });
            var bottom = new Grid(); bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(bottom, 1); body.Children.Add(bottom);
            var grid = UI.Grid(("名称|Name", "Name"), ("时长|Duration", "Duration")); grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(model.Ranking))); bottom.Children.Add(grid);
            grid.SelectionChanged += async (_, _) => { if (grid.SelectedItem is RankItem item) { model.SelectedId = item.Id; await model.RefreshAsync(); } };
            Grid.SetColumn(_pie, 1); bottom.Children.Add(_pie);
        }
        model.PropertyChanged += ModelChanged;
        Unloaded += (_, _) => model.PropertyChanged -= ModelChanged;
        Content = root;
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(StatisticsModel.Buckets) or nameof(StatisticsModel.ChartRanking) or nameof(StatisticsModel.Timeline))) return;
        _chart.Buckets = _model.Buckets; _chart.Timeline = _model.Timeline;
        try { _chart.TimeZone = _pie.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(_model.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return; }
        if (_model.Details)
        {
            _chart.FirstRow = 0;
            var date = DateOnly.FromDateTime(_model.TimelineDate); _chart.WindowStart = DayStart(date); _chart.WindowEnd = DayStart(date.AddDays(1));
        }
        _chart.Redraw(); _pie.Ranking = _model.ChartRanking; _pie.Redraw();
    }
    private DateTimeOffset DayStart(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(_model.DayCutoffHour, 0), DateTimeKind.Unspecified);
        if (_chart.TimeZone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = _chart.TimeZone.IsAmbiguousTime(local) ? _chart.TimeZone.GetAmbiguousTimeOffsets(local)[0] : _chart.TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
    private static DatePicker DateInput(string property, DateTime value)
    {
        var p = new DatePicker { Width = 135, SelectedDate = value }; p.SetBinding(DatePicker.SelectedDateProperty, new Binding(property) { Mode = BindingMode.TwoWay }); return p;
    }
    private static void RebindDates(DependencyObject root)
    {
        if (root is DatePicker picker) picker.GetBindingExpression(DatePicker.SelectedDateProperty)?.UpdateTarget();
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) RebindDates(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    private sealed record Choice(string Value, string Label) { public override string ToString() => Text.Of(Label); }
}
