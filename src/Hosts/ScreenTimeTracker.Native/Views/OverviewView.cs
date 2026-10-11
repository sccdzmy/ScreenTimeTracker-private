using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Native.Controls;
using ScreenTimeTracker.Native.ViewModels;
namespace ScreenTimeTracker.Native.Views;

public sealed class OverviewView : UserControl, IDisposable
{
    private readonly StatisticsModel _model;
    private readonly TrackerClient _client;
    private readonly IconCache _icons;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ChartSurface _days = new() { AxisMode = "days", ShowAverage = true, MinHeight = 42 };
    private readonly ChartSurface _hours = new() { AxisMode = "hours", MinHeight = 42 };
    private readonly ChartSurface _distribution = new() { Mode = "distribution", MinHeight = 70 };
    private readonly DataGrid _ranking;
    private string _search = "";
    private ICollectionView? _rankingView;
    private bool _released;
    internal ChartSurface DayChart => _days;
    internal ChartSurface HourChart => _hours;
    internal DataGrid RankingTable => _ranking;

    public OverviewView(StatisticsModel model, TrackerClient client, IconCache icons)
    {
        _model = model; _client = client; _icons = icons; DataContext = model;
        var root = new DockPanel { Margin = new Thickness(16, 12, 16, 16) };
        var filters = new Expander { Header = Text.Of("筛选与日期范围|Filters & date range"), Margin = new Thickness(0, 0, 0, 8) };
        filters.Content = new ScrollViewer { Content = BuildFilters(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 170 };
        DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters);
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = new GridLength(1.8, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body);
        var usage = Card(); usage.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(usage);
        var content = new DockPanel(); usage.Child = content;
        var heading = new DockPanel { LastChildFill = true }; DockPanel.SetDock(heading, Dock.Top); content.Children.Add(heading);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(navigation, Dock.Right); heading.Children.Add(navigation);
        var date = BoundText(nameof(model.DateLabel), 14); date.VerticalAlignment = VerticalAlignment.Center; date.Margin = new Thickness(0, 0, 12, 0); navigation.Children.Add(date);
        navigation.Children.Add(NavigationButton("‹", "上一个日期范围|Previous period", () => model.ShiftPeriodAsync(-1)));
        navigation.Children.Add(NavigationButton(Text.Of("今天|Today"), "返回今天|Go to today", model.TodayAsync));
        navigation.Children.Add(NavigationButton("›", "下一个日期范围|Next period", () => model.ShiftPeriodAsync(1)));
        var title = new TextBlock { Text = Text.Of("使用时间|Usage time"), FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title);
        var totalRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; DockPanel.SetDock(totalRow, Dock.Top); content.Children.Add(totalRow);
        var status = UI.Status(model); status.SetResourceReference(ForegroundProperty, "AppSecondary"); status.Margin = new Thickness(8, 0, 0, 0); status.VerticalAlignment = VerticalAlignment.Center; status.MaxWidth = 190;
        DockPanel.SetDock(status, Dock.Right); totalRow.Children.Add(status); totalRow.Children.Add(BoundText(nameof(model.Total), 30));
        var tabs = new TabControl { BorderThickness = new Thickness(0) }; content.Children.Add(tabs);
        var trends = new Grid(); trends.RowDefinitions.Add(new()); trends.RowDefinitions.Add(new());
        trends.Children.Add(ChartSection(_days, nameof(model.TrendLabel)));
        var hours = ChartSection(_hours, nameof(model.HourLabel)); Grid.SetRow(hours, 1); trends.Children.Add(hours);
        tabs.Items.Add(new TabItem { Header = Text.Of("使用趋势|Usage trend"), Content = trends });
        tabs.Items.Add(new TabItem { Header = Text.Of("分布|Distribution"), Content = _distribution });
        var tables = new TabControl { BorderThickness = new Thickness(0) };
        tables.Items.Add(new TabItem { Header = Text.Of("每日|Daily"), Content = BucketTable(nameof(model.TrendBuckets)) });
        tables.Items.Add(new TabItem { Header = Text.Of("小时|Hourly"), Content = BucketTable(nameof(model.HourBuckets)) });
        tables.Items.Add(new TabItem { Header = Text.Of("所选范围|Selected range"), Content = BucketTable(nameof(model.Buckets)) });
        tabs.Items.Add(new TabItem { Header = Text.Of("图表明细|Chart data"), Content = tables });
        _days.BucketSelected += async (_, e) => await SelectDayAsync(e.Bucket);

        var rankCard = Card(); Grid.SetRow(rankCard, 1); body.Children.Add(rankCard);
        var rankContent = new DockPanel(); rankCard.Child = rankContent;
        var rankHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(rankHeader, Dock.Top); rankContent.Children.Add(rankHeader);
        var search = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(search, Dock.Right); rankHeader.Children.Add(search);
        search.Children.Add(new TextBlock { Text = Text.Of("搜索|Search"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var input = new TextBox { Width = 130, Margin = new Thickness(0), ToolTip = Text.Of("搜索已加载的排行|Search loaded ranking") }; AutomationProperties.SetName(input, Text.Of("搜索排行|Search ranking"));
        input.TextChanged += (_, _) => { _search = input.Text; _rankingView?.Refresh(); }; search.Children.Add(input);
        var dimensions = new[] { new Choice("apps", "显示应用|Show apps"), new Choice("websites", "显示网站|Show websites"), new Choice("app-categories", "应用分类|App categories"), new Choice("website-categories", "网站分类|Website categories") };
        var dimension = new ComboBox { ItemsSource = dimensions, SelectedIndex = Array.FindIndex(dimensions, x => x.Value == model.Kind), Width = 152, Margin = new Thickness(0) };
        dimension.SelectionChanged += async (_, _) => { if (dimension.SelectedItem is Choice choice) await model.ChangeDimensionAsync(choice.Value); }; AutomationProperties.SetName(dimension, Text.Of("统计维度|Statistics dimension")); rankHeader.Children.Add(dimension);
        var other = BoundText(nameof(model.Other), 11); other.SetResourceReference(TextBlock.ForegroundProperty, "AppSecondary"); other.Margin = new Thickness(0, 4, 0, 0); DockPanel.SetDock(other, Dock.Bottom); rankContent.Children.Add(other);
        _ranking = BuildRanking(); rankContent.Children.Add(_ranking);
        _ranking.MouseDoubleClick += async (_, _) => { if (_ranking.SelectedItem is RankItem item) { model.SelectedId = item.Id; model.FilterIds = []; await model.RefreshAsync(); } };
        _ranking.ToolTip = Text.Of("双击对象仅查看该对象；也可在筛选中选择多个对象。|Double-click to filter an object, or select several objects in Filters.");
        model.PropertyChanged += ModelChanged;
        Unloaded += (_, _) => Dispose();
        Content = root;
    }
    public void Dispose()
    {
        if (_released) return; _released = true; _model.PropertyChanged -= ModelChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
        if (_rankingView is not null) _rankingView.Filter = null; _ranking.ItemsSource = null; _rankingView = null;
        _days.Buckets = []; _hours.Buckets = []; _distribution.Ranking = [];
        _days.Redraw(); _hours.Redraw(); _distribution.Redraw(); GC.SuppressFinalize(this);
    }
    private Border Card() => new() { Style = (Style)FindResource("StatisticsCard") };
    private static TextBlock BoundText(string property, double size)
    {
        var text = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(TextBlock.TextProperty, new Binding(property)); return text;
    }
    private static Button NavigationButton(string content, string label, Func<Task> action)
    {
        var button = UI.Button(content, action); button.Margin = new Thickness(0, 0, 6, 0); button.Padding = new Thickness(10, 2, 10, 2); button.MinHeight = 30; button.ToolTip = Text.Of(label); AutomationProperties.SetName(button, Text.Of(label)); return button;
    }
    private static DockPanel ChartSection(ChartSurface chart, string titleProperty)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 0) }; var title = BoundText(titleProperty, 11); title.SetResourceReference(TextBlock.ForegroundProperty, "AppSecondary"); title.Margin = new Thickness(0, 0, 0, 2); DockPanel.SetDock(title, Dock.Top); dock.Children.Add(title); dock.Children.Add(chart); return dock;
    }
    private DataGrid BucketTable(string property)
    {
        var grid = UI.Grid(("开始|Start", "StartTime"), ("结束|End", "EndTime"));
        grid.Columns.Add(new DataGridTextColumn { Header = Text.Of("时长|Duration"), Binding = new Binding("DurationSeconds") { Converter = DurationConverter.Instance, ConverterParameter = true }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(property));
        if (property == nameof(StatisticsModel.TrendBuckets))
        {
            grid.MouseDoubleClick += async (_, _) => { if (grid.SelectedItem is UsageBucket bucket) await SelectDayAsync(bucket); };
            grid.PreviewKeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && grid.SelectedItem is UsageBucket bucket) { e.Handled = true; await SelectDayAsync(bucket); } };
            grid.ToolTip = Text.Of("双击或按 Enter 切换日期|Double-click or press Enter to select a day");
        }
        return grid;
    }
    private WrapPanel BuildFilters()
    {
        var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        filters.Children.Add(DateInput(nameof(_model.StartDate))); filters.Children.Add(DateInput(nameof(_model.EndDate)));
        filters.Children.Add(UI.Button("近7天|Last 7 days", _model.Last7DaysAsync));
        var objects = new ComboBox { Width = 180, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        objects.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(_model.Objects))); objects.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(_model.SelectedId)) { Mode = BindingMode.TwoWay }); AutomationProperties.SetName(objects, Text.Of("对象筛选|Object filter")); filters.Children.Add(objects);
        filters.Children.Add(UI.Button("全部|All", async () => { _model.SelectedId = null; _model.FilterIds = []; objects.SelectedIndex = -1; await _model.RefreshAsync(); }));
        filters.Children.Add(UI.Button("多选筛选|Multiple filters", async () => { FilterDialog.Show(_model); await _model.RefreshAsync(); }));
        filters.Children.Add(UI.Button("更多对象|More objects", _model.MoreObjectsAsync));
        filters.Children.Add(UI.Label("时区|Time zone")); filters.Children.Add(UI.Input(_model, nameof(_model.TimeZoneId), 180));
        filters.Children.Add(UI.Label("排行数量|Top N")); filters.Children.Add(UI.Input(_model, nameof(_model.TopN), 55));
        var daily = new CheckBox { Content = Text.Of("范围明细按日汇总|Daily range data"), Margin = new Thickness(8) }; daily.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(_model.Daily)) { Mode = BindingMode.TwoWay }); filters.Children.Add(daily);
        filters.Children.Add(UI.Button("应用筛选 / 刷新|Apply / refresh", _model.RefreshAsync)); return filters;
    }
    private static DatePicker DateInput(string property)
    {
        var picker = new DatePicker { Width = 140 }; picker.SetBinding(DatePicker.SelectedDateProperty, new Binding(property) { Mode = BindingMode.TwoWay }); AutomationProperties.SetName(picker, Text.Of(property == nameof(_model.StartDate) ? "开始日期|Start date" : "结束日期|End date")); return picker;
    }
    private DataGrid BuildRanking()
    {
        var grid = new DataGrid { RowHeight = 40, ColumnHeaderHeight = 28, BorderThickness = new Thickness(0), SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, RowHeaderWidth = 0 };
        grid.SetResourceReference(BackgroundProperty, "AppSurface"); grid.SetResourceReference(Control.ForegroundProperty, "AppForeground"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "AppSurfaceAlt");
        grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 44, CellTemplate = IconTemplate() });
        grid.Columns.Add(new DataGridTextColumn { Header = Text.Of("名称|Name"), Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = Text.Of("时间|Time"), Binding = new Binding("DurationSeconds") { Converter = DurationConverter.Instance }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var cell = new Style(typeof(TextBlock)); cell.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center)); cell.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0))); cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>()) column.ElementStyle = cell;
        return grid;
    }
    private DataTemplate IconTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(FrameworkElement.WidthProperty, 28.0); border.SetValue(FrameworkElement.HeightProperty, 28.0); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); border.SetResourceReference(Border.BackgroundProperty, "AppSurfaceAlt");
        var content = new FrameworkElementFactory(typeof(Grid)); border.AppendChild(content);
        var initial = new FrameworkElementFactory(typeof(TextBlock)); initial.SetBinding(TextBlock.TextProperty, new Binding("Name") { Converter = InitialConverter.Instance }); initial.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); initial.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); content.AppendChild(initial);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.AddHandler(LoadedEvent, new RoutedEventHandler(async (sender, _) =>
        {
            var target = (Image)sender;
            if (target.Tag is null) { target.Tag = true; target.DataContextChanged += async (_, _) => { if (target.IsLoaded) await LoadIconAsync(target); }; }
            await LoadIconAsync(target);
        }));
        image.AddHandler(UnloadedEvent, new RoutedEventHandler((sender, _) => ((Image)sender).Source = null)); content.AppendChild(image);
        return new DataTemplate { VisualTree = border };
    }
    private async Task LoadIconAsync(Image image)
    {
        if (_released || image.DataContext is not RankItem row) return;
        image.Source = null;
        try
        {
            var item = new EntityItem(row.Id, row.Name, row.Color, row.IconId);
            var bitmap = await _icons.GetAsync(_client, _model.Kind, item, 32, _lifetime.Token);
            if (!_released && image.IsLoaded && ReferenceEquals(image.DataContext, row)) image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
    }
    private Task SelectDayAsync(UsageBucket bucket) => _model.SelectTrendDayAsync(TimeZoneInfo.ConvertTime(bucket.StartTime, _days.TimeZone).Date);
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatisticsModel.Ranking))
        {
            _rankingView = CollectionViewSource.GetDefaultView(_model.Ranking); _rankingView.Filter = item => item is RankItem row && row.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase); _ranking.ItemsSource = _rankingView;
        }
        if (e.PropertyName is not (nameof(StatisticsModel.TrendBuckets) or nameof(StatisticsModel.HourBuckets) or nameof(StatisticsModel.ChartRanking))) return;
        try { _days.TimeZone = _hours.TimeZone = _distribution.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(_model.TimeZoneId); } catch (TimeZoneNotFoundException) { return; }
        _days.Buckets = _model.TrendBuckets; _days.SelectedDay = _model.TimelineDate; _days.Redraw(); _hours.Buckets = _model.HourBuckets; _hours.Redraw(); _distribution.Ranking = _model.ChartRanking; _distribution.Redraw();
    }
    private sealed record Choice(string Value, string Label) { public override string ToString() => Text.Of(Label); }
    private sealed class DurationConverter : IValueConverter
    {
        public static readonly DurationConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long seconds ? Text.Duration(seconds, parameter is true) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private sealed class InitialConverter : IValueConverter
    {
        public static readonly InitialConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string name && name.Length > 0 ? StringInfo.GetNextTextElement(name) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
