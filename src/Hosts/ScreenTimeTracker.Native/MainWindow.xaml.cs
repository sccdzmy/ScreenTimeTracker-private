using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Native.Controls;
using ScreenTimeTracker.Native.ViewModels;
using ScreenTimeTracker.Native.Views;
namespace ScreenTimeTracker.Native;
public partial class MainWindow : Window, IDisposable
{
    private static readonly string[] PageLabels = ["概览|Overview", "详细统计|Details", "应用|Apps", "网站|Websites", "分类|Categories", "数据|Data", "设置|Settings"];
    private readonly TrackerClient _client;
    private readonly IconCache _icons = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<int, StatsFilter> _filters = [];
    private PageModel? _page;
    private ManagementView? _view;
    private OverviewView? _overview;
    private int _current = -1;
    private bool _wasMaximized;
    private string _theme = "System";
    private readonly string _placement = Path.Combine(AppContext.BaseDirectory, "NativeWindow.json");
    private readonly bool _persist;
    public MainWindow(TrackerClient client, bool persist = true)
    {
        _client = client; _persist = persist; InitializeComponent(); ApplyLabels();
        if (persist) RestorePlacement();
        Loaded += (_, _) => WindowVisibility.EnsureVisible(this);
        SystemParameters.StaticPropertyChanged += MonitorChanged;
        _timer.Tick += async (_, _) => { if (_page is StatisticsModel { NeedsRefresh: true, Busy: false } page && IsVisible && WindowState != WindowState.Minimized) await page.RefreshAsync(); };
        StateChanged += async (_, _) => { if (WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized; if (WindowState == WindowState.Minimized) _timer.Stop(); else { _timer.Start(); if (_page is StatisticsModel { NeedsRefresh: true, Busy: false } page) await page.RefreshAsync(); } };
        IsVisibleChanged += (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) _timer.Start(); else _timer.Stop(); };
        Closed += (_, _) => { SystemParameters.StaticPropertyChanged -= MonitorChanged; _timer.Stop(); SavePlacement(); _view?.Dispose(); _overview?.Dispose(); _page?.Dispose(); _icons.Clear(); PageContent.Content = null; };
        Navigation.SelectedIndex = 0;
    }
    public void Dispose()
    {
        _overview?.Dispose(); _overview = null;
        _view?.Dispose(); _view = null;
        _page?.Dispose(); _page = null;
        _timer.Stop(); _icons.Clear();
        SystemParameters.StaticPropertyChanged -= MonitorChanged;
        GC.SuppressFinalize(this);
    }
    private void ApplyLabels()
    {
        int selection = Navigation.SelectedIndex;
        Navigation.ItemsSource = PageLabels.Select(Text.Of).ToArray();
        TrayHint.Text = Text.Of("关闭窗口后，托盘继续采集。|Tracking continues in the tray after closing.");
        if (selection >= 0) Navigation.SelectedIndex = selection;
    }
    private async void Navigate(object sender, SelectionChangedEventArgs e)
    {
        int index = Navigation.SelectedIndex; if (index < 0) return;
        await NavigateAsync(index);
    }
    internal async Task NavigateAsync(int index)
    {
        if (_page is StatisticsModel old) _filters[_current] = new(old.StartDate, old.EndDate, old.TimelineDate, old.Kind, old.TimeZoneId, old.SelectedId, old.Daily, old.TopN, old.FilterIds, old.ExcludeFilter);
        _view?.Dispose(); _view = null; _overview?.Dispose(); _overview = null; _page?.Dispose(); PageContent.Content = null; _current = index;
        PageTitle.Text = Text.Of(PageLabels[index]);
        PageTitle.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        FrameworkElement view;
        if (index < 2)
        {
            var model = new StatisticsModel(_client, index == 1);
            if (_filters.TryGetValue(index, out var f)) { model.StartDate = f.Start; model.EndDate = f.End; model.TimelineDate = f.Day; model.Kind = f.Kind; model.TimeZoneId = f.Zone; model.SelectedId = f.Id; model.Daily = f.Daily; model.TopN = f.Top; model.FilterIds = f.FilterIds; model.ExcludeFilter = f.Exclude; }
            _page = model;
            if (model.Details) view = new StatisticsView(model);
            else { _overview = new OverviewView(model, _client, _icons); view = _overview; }
        }
        else if (index < 5)
        {
            var model = new ManagementModel(_client, index == 2 ? "apps" : index == 3 ? "websites" : "app-categories");
            _page = model; var management = new ManagementView(model, _client, _icons); _view = management; view = management;
        }
        else if (index == 5) { var model = new DataModel(_client); _page = model; view = new DataView(model); }
        else { var model = new SettingsModel(_client); _page = model; view = new SettingsView(model, () => { ApplyLabels(); }, ChangeTheme, _theme); }
        PageContent.Content = view;
        var current = _page; await current.RefreshAsync();
    }
    private void MonitorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.InvokeAsync(() => WindowVisibility.EnsureVisible(this));
    internal bool LastPageSucceeded => _page is DataModel || _page?.LastOperationSucceeded == true;
    internal void RefreshChartPalette()
    {
        static void Walk(DependencyObject root)
        {
            if (root is ChartSurface chart) chart.Redraw();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); ++i) Walk(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }
        if (PageContent.Content is DependencyObject content) Walk(content);
    }
    private void ChangeTheme(string mode) { _theme = mode; ((App)Application.Current).SetTheme(mode); SavePlacement(); }
    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(_placement)) return;
            var value = JsonSerializer.Deserialize<Placement>(File.ReadAllText(_placement)); if (value is null) return;
            if (!double.IsFinite(value.Width) || !double.IsFinite(value.Height) || !double.IsFinite(value.Left) || !double.IsFinite(value.Top)) return;
            Width = Math.Clamp(value.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth)); Height = Math.Clamp(value.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
            Left = Math.Clamp(value.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(value.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            WindowStartupLocation = WindowStartupLocation.Manual; _theme = value.Theme is "Light" or "Dark" ? value.Theme : "System"; ((App)Application.Current).SetTheme(_theme);
            if (value.Maximized) { _wasMaximized = true; WindowState = WindowState.Maximized; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void SavePlacement()
    {
        if (!_persist) return;
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight) return;
            var value = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, _wasMaximized, _theme);
            File.WriteAllText(_placement + ".tmp", JsonSerializer.Serialize(value)); File.Move(_placement + ".tmp", _placement, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized, string Theme);
    private sealed record StatsFilter(DateTime Start, DateTime End, DateTime Day, string Kind, string Zone, Guid? Id, bool Daily, int Top, Guid[] FilterIds, bool Exclude);
}
