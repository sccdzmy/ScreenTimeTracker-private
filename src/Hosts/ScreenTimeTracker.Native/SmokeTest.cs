using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native;
internal static class SmokeTest
{
    private static readonly string[] Themes = ["Light", "Dark"];
    private static readonly string[] Languages = ["zh-CN", "en-US"];
    // Runs the real WPF views against synthetic HTTP responses, without a collector or a user database.
    public static async Task RunAsync(App app)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "smoke-results"); Directory.CreateDirectory(directory);
        try
        {
            using var client = new TrackerClient(new Uri("http://127.0.0.1:5124"), new FixtureHandler());
            app.SetTheme("Dark"); Text.Language = "zh-CN"; var window = new MainWindow(client, false); app.MainWindow = window; window.Show();
            for (int i = 0; i < 7; ++i)
            {
                await window.NavigateAsync(i); if (!window.LastPageSucceeded) throw new InvalidOperationException($"Page {i} could not load."); window.UpdateLayout();
                await app.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"page-{i}.png")); encoder.Save(file);
            }
            foreach (string mode in Themes)
            {
                app.SetTheme(mode); Text.Language = mode == "Dark" ? "en-US" : "zh-CN";
                for (int page = 0; page < 7; ++page)
                {
                    await window.NavigateAsync(page); if (!window.LastPageSucceeded) throw new InvalidOperationException($"Page {page} failed in {mode}."); window.UpdateLayout();
                }
            }
            window.Width = 760; window.Height = 560;
            for (int page = 0; page < 7; ++page) { await window.NavigateAsync(page); window.UpdateLayout(); }
            await window.NavigateAsync(0);
            await app.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Capture(window, Path.Combine(directory, "overview-minimum.png"));
            app.SetTheme("Light"); Text.Language = "zh-CN"; window.Width = 960; window.Height = 700;
            await window.NavigateAsync(0); await app.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Capture(window, Path.Combine(directory, "overview-light.png"));
            await ValidateModelsAsync(client);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = true, pages = 7, themes = Themes, languages = Languages, realCollector = false }));
            window.Close();
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); app.Shutdown(1); }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static async Task ValidateModelsAsync(TrackerClient client)
    {
        using var statistics = new ViewModels.StatisticsModel(client, false);
        foreach (string kind in new[] { "apps", "websites", "app-categories", "website-categories" })
        {
            await statistics.ChangeDimensionAsync(kind);
            if (!statistics.LastOperationSucceeded || statistics.Ranking.Count != 2 || statistics.Buckets.Sum(x => x.DurationSeconds) != 5400) throw new InvalidOperationException("Statistic model mismatch.");
        }
        if (statistics.TrendBuckets.Count != 7 || statistics.HourBuckets.Count != 24) throw new InvalidOperationException("Overview must show a week and one day of hours.");
        var previous = statistics.StartDate;
        await statistics.ShiftPeriodAsync(-1);
        if (statistics.StartDate != previous.AddDays(-1) || statistics.HourBuckets.Count != 24) throw new InvalidOperationException("Date navigation failed.");
        await statistics.Last7DaysAsync();
        if (statistics.TrendBuckets.Count != 7 || statistics.HourBuckets.Count != 24 || statistics.Buckets.Count != 7) throw new InvalidOperationException("Range and selected-day queries were mixed.");
        using var management = new ViewModels.ManagementModel(client, "apps");
        await management.RefreshAsync(); management.Select(management.Items[0]); management.Name = "Edited synthetic"; await management.SaveAsync();
        if (!management.LastOperationSucceeded) throw new InvalidOperationException("Management save failed.");
        await management.RemoveIconAsync(); if (!management.LastOperationSucceeded) throw new InvalidOperationException("Icon removal failed.");
        using var settings = new ViewModels.SettingsModel(client); await settings.RefreshAsync(); settings.Entries.First(x => x.Key == "dayCutoffHour").Value = "4"; await settings.SaveAsync();
        if (!settings.LastOperationSucceeded) throw new InvalidOperationException("Settings save failed.");
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private static readonly Guid Id = Guid.Parse("12345678-1234-1234-1234-123456789012");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            object data;
            if (path.EndsWith("user-settings", StringComparison.Ordinal)) data = new UserSettings(new(60, 15, 5, 6, 10), new(15, 5, 6, 10), new(true, 60, 5), new(0));
            else if (path.EndsWith("local-settings", StringComparison.Ordinal)) data = new LocalSettings("Window", false, true, "zh-CN");
            else if (path.EndsWith("timeline/page", StringComparison.Ordinal)) data = new PageResult<TimelineItem>([new(Id, "示例应用 Example", "#3984D6", now.AddHours(-1), now)], null);
            else if (path.EndsWith("distribution", StringComparison.Ordinal)) data = new Distribution([new(Id, "示例应用 Example", "#3984D6", null, 3600), new(Guid.Empty, "阅读 Reading", "#55A779", null, 1800)], 2, 5400, 0, 0);
            else if (path.Contains("/usage/", StringComparison.Ordinal))
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                var start = DateOnly.Parse(query["startDate"], System.Globalization.CultureInfo.InvariantCulture);
                var end = DateOnly.Parse(query["endDate"], System.Globalization.CultureInfo.InvariantCulture);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(query["timeZoneId"]);
                bool daily = query["granularity"] == "Day";
                var buckets = new List<UsageBucket>();
                for (var day = start; day <= end; day = day.AddDays(1))
                {
                    var local = day.ToDateTime(TimeOnly.MinValue);
                    var origin = new DateTimeOffset(local, zone.GetUtcOffset(local));
                    if (daily) buckets.Add(new(origin, origin.AddDays(1), 5400));
                    else for (int hour = 0; hour < 24; hour++) buckets.Add(new(origin.AddHours(hour), origin.AddHours(hour + 1), hour == 9 ? 3600 : hour == 15 ? 1800 : 0));
                }
                data = buckets;
            }
            else if (path.EndsWith("/page", StringComparison.Ordinal)) data = new PageResult<EntityItem>([new(Id, "示例应用 Example", "#3984D6", null, AppCategoryId: Id, ProcessName: "example")], null);
            else data = new List<EntityItem> { new(Id, "工作 Work", "#3984D6", null) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data, options: TrackerClient.JsonOptions) });
        }
    }
}
