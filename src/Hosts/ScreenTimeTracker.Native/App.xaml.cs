using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native;
public partial class App : Application, IDisposable
{
    private TrackerClient? _client;
    private readonly Channel<string> _credentials = Channel.CreateBounded<string>(4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _output = new(1, 1);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SystemParameters.StaticPropertyChanged += PaletteChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        try
        {
            if (e.Args.Contains("--smoke-test", StringComparer.Ordinal)) { await SmokeTest.RunAsync(this); return; }
            var input = await Console.In.ReadLineAsync(_lifetime.Token);
            if (input is null) { Shutdown(); return; }
            var init = JsonSerializer.Deserialize<ControlMessage>(input ?? "", TrackerClient.JsonOptions);
            if (init is not { Type: "initialize", BaseUrl: not null, Token: not null }) throw new InvalidOperationException("Launch this interface from the tray.");
            _client = new TrackerClient(new Uri(init.BaseUrl));
            _client.RequestCredentialAsync = RequestCredentialAsync;
            _ = Task.Run(ReadControlAsync);
            await _client.AuthenticateAsync(init.Token, _lifetime.Token);
            var info = await _client.GetAsync<SystemInfo>("/api/system/info", _lifetime.Token);
            info.EnsureNativeCompatible();
            var settings = await _client.GetAsync<LocalSettings>("/api/desktop-settings/local-settings", _lifetime.Token);
            Text.Language = settings.Language;
            SetTheme("System");
            var window = new MainWindow(_client); MainWindow = window; window.Show();
        }
        catch (OperationCanceledException) { Shutdown(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show("无法打开界面 / Cannot open interface.\n" + (ex is InvalidOperationException ? ex.Message : ex.GetType().Name), "UsageTrack");
            Shutdown(1);
        }
    }
    private async Task<string> RequestCredentialAsync(CancellationToken ct)
    {
        await _output.WaitAsync(ct);
        try { await Console.Out.WriteLineAsync("{\"type\":\"reauthenticate\"}"); await Console.Out.FlushAsync(ct); }
        finally { _output.Release(); }
        return await _credentials.Reader.ReadAsync(ct);
    }
    private async Task ReadControlAsync()
    {
        try
        {
            while (await Console.In.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length > 4096) break;
                var message = JsonSerializer.Deserialize<ControlMessage>(line, TrackerClient.JsonOptions);
                if (message?.Type == "credential" && message.Token is { } token) await _credentials.Writer.WriteAsync(token, _lifetime.Token);
                else if (message?.Type == "shutdown") break;
                else if (message?.Type == "navigate" && message.Page is >= 0 and < 7) await Dispatcher.InvokeAsync(async () => { if (MainWindow is MainWindow main) await main.NavigateAsync(message.Page.Value); });
                else if (message?.Type == "activate") await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is not null) { if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal; MainWindow.Show(); MainWindow.Activate(); }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException) { }
        finally { _credentials.Writer.TryComplete(); if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => Shutdown()); }
    }
    private string _themeMode = "System";
    public void SetTheme(string mode)
    {
        _themeMode = mode;
        var name = mode == "Light" ? "Fluent.Light" : mode == "Dark" ? "Fluent.Dark" : "Fluent";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/{name}.xaml") };
        UpdatePalette();
    }
    private void UpdatePalette()
    {
        bool dark = _themeMode == "Dark" || _themeMode == "System" && Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        static System.Windows.Media.Brush Brush(string color)
        {
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
        }
        Resources["AppBackground"] = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush(dark ? "#1D1D1D" : "#F5F5F5");
        Resources["AppForeground"] = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#E8E8E8" : "#202020");
        Resources["AppSurface"] = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush(dark ? "#272727" : "#FFFFFF");
        Resources["AppSurfaceAlt"] = SystemParameters.HighContrast ? SystemColors.ControlBrush : Brush(dark ? "#333333" : "#F4F4F4");
        Resources["AppSecondary"] = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#B0B0B0" : "#686868");
        Resources["AppCardBorder"] = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#303030" : "#E5E5E5");
        Resources["ChartGrid"] = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#414141" : "#DDDDDD");
        Resources["ChartMuted"] = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(dark ? "#5E5E5E" : "#B9B9B9");
        if (MainWindow is MainWindow main) main.RefreshChartPalette();
    }
    private void UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(UpdatePalette);
    private void PaletteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Invoke(UpdatePalette);
    public void Dispose() { SystemParameters.StaticPropertyChanged -= PaletteChanged; Microsoft.Win32.SystemEvents.UserPreferenceChanged -= UserPreferenceChanged; _lifetime.Cancel(); _client?.Dispose(); _lifetime.Dispose(); _output.Dispose(); GC.SuppressFinalize(this); }
    protected override void OnExit(ExitEventArgs e) { Dispose(); base.OnExit(e); }
}
