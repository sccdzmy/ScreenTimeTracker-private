using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Photino.NET;
using ScreenTimeTracker.ApiClient;
using ScreenTimeTracker.Desktop.Hosting;
using ScreenTimeTracker.Desktop.Security;
using ScreenTimeTracker.Desktop.UI.Services;

namespace ScreenTimeTracker.NativeHarness;
internal static partial class Program
{
    private static readonly ConcurrentDictionary<string, int> Requests = new();
    private static readonly Guid Id = Guid.Parse("12345678-1234-1234-1234-123456789012");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Scenarios = ["overview", "timeline"];
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--legacy-ui", var url])
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(AppContext.BaseDirectory, "synthetic-webview-profile"));
            var thread = new Thread(() => new PhotinoWindow().SetTitle("UsageTrack legacy synthetic benchmark").SetUseOsDefaultSize(false).SetSize(960, 700).Load(url).WaitForClose());
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return 0;
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ScreenTimeTracker.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found.");
        var uiBuild = Path.Combine(root.FullName, "src/Hosts/ScreenTimeTracker.Native/bin/Release/net10.0-windows");
        foreach (string file in Directory.GetFiles(uiBuild, "ScreenTimeTracker.Native.*")) File.Copy(file, Path.Combine(AppContext.BaseDirectory, Path.GetFileName(file)), true);
        Console.WriteLine("Isolated synthetic API. No collector or user database is loaded.");
        var output = Path.Combine(root.FullName, "artifacts/native-validation"); Directory.CreateDirectory(output);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var clock = new BenchmarkClock(); builder.Services.AddSingleton<TimeProvider>(clock); builder.Services.AddSingleton<LocalAccessService>(); builder.Services.AddSingleton<IServerUrlProvider, ServerUrlProvider>();
        await using var app = builder.Build(); app.UseMiddleware<LocalSecurityMiddleware>();
        app.Use(async (context, next) => { Requests.AddOrUpdate(context.Request.Path.ToString(), 1, (_, n) => n + 1); await next(context); });
        var access = app.Services.GetRequiredService<LocalAccessService>();
        app.MapPost("/api/auth/native-session", (HttpContext c, Credential b) => access.RedeemNativeBootstrap(b.Token) is { } token ? Results.Ok(new NativeSession(token, 43200)) : Results.Unauthorized());
        app.MapPost("/api/auth/session", (HttpContext c, Credential b) =>
        {
            var result = access.RedeemBootstrap(b.Token); if (result is null) return Results.Unauthorized();
            c.Response.Cookies.Append("stt-session", result.Value.Token); return Results.Ok(new { csrf = result.Value.Csrf });
        });
        app.MapGet("/api/system/info", () => new SystemInfo(1, ["native-session", "paged-management", "paged-timeline", "icons", "extension-pairing", "import-export"]));
        app.MapGet("/api/desktop-settings/local-settings", () => new LocalSettings("Window", false, true, "zh-CN"));
        app.MapGet("/api/screen-time/user-settings", () => new UserSettings(new(60, 15, 5, 6, 10), new(15, 5, 6, 10), new(true, 60, 5), new(0)));
        app.MapGet("/api/screen-time/{**path}", (HttpContext context, string path) => Results.Json(Fixture(path, context.Request), TrackerClient.JsonOptions));
        var frontend = Path.Combine(root.FullName, "src/frontend/dist");
        if (Directory.Exists(frontend))
        {
            var provider = new PhysicalFileProvider(frontend); app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapFallback(async context => { context.Response.ContentType = "text/html"; await context.Response.SendFileAsync(Path.Combine(frontend, "index.html")); });
        }
        await app.StartAsync(); var baseUrl = app.Services.GetRequiredService<IServerUrlProvider>().GetServerUrl();
        bool probe = args.Contains("--probe-legacy", StringComparer.Ordinal);
        bool extra = args.Contains("--lifecycle-extra", StringComparer.Ordinal);
        int cycles = probe || extra || args.Contains("--benchmark-only", StringComparer.Ordinal) ? 0 : 20;
        var metrics = new List<object>();
        using var manager = new AppUIManager(access, app.Services.GetRequiredService<IServerUrlProvider>(), app.Services.GetRequiredService<ILogger<AppUIManager>>(), app.Lifetime);
        var field = typeof(AppUIManager).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int iteration = 0; iteration < cycles; ++iteration)
        {
            Requests.Clear(); manager.OpenUI(); var process = await WaitForNativeAsync(manager, field);
            for (int click = 0; click < 5; ++click) manager.OpenUI();
            await Task.Delay(1000);
            if (!Requests.ContainsKey("/api/screen-time/user-settings")) throw new InvalidOperationException("Native main page did not initialize.");
            if (!ReferenceEquals(process, field.GetValue(manager))) throw new InvalidOperationException("Repeated tray opens launched duplicate UI processes.");
            int pid = process.Id; await CloseAndWaitAsync(process);
            for (int retry = 0; retry < 50 && field.GetValue(manager) is not null; ++retry) await Task.Delay(20);
            if (field.GetValue(manager) is not null) throw new InvalidOperationException("UI process was not released.");
            metrics.Add(new { scenario = "lifecycle", iteration, exited = true, processId = pid });
        }
        if (cycles > 0) Console.WriteLine("20 repeated-launch and close cycles passed.");
        foreach (string scenario in extra ? Array.Empty<string>() : probe ? new[] { "overview" } : Scenarios)
        for (int run = 0; run < (probe ? 1 : 5); ++run)
        {
            Console.WriteLine($"Measuring {scenario} run {run + 1}/5...");
            clock.Advance(); Requests.Clear();
            var watch = Stopwatch.StartNew(); manager.OpenUI(); var native = await WaitForNativeAsync(manager, field);
            if (scenario == "timeline") { await native.StandardInput.WriteLineAsync("{\"type\":\"navigate\",\"page\":1}"); await native.StandardInput.FlushAsync(); }
            var metric = await MeasureAsync(native, watch.Elapsed.TotalMilliseconds); metrics.Add(new { scenario, run, engine = "wpf", metric, requests = Requests.ToDictionary(x => x.Key, x => x.Value) });
            if (run == 0) Capture(native, Path.Combine(output, "wpf-" + scenario + ".png"));
            await CloseAndWaitAsync(native);
            for (int retry = 0; retry < 50 && field.GetValue(manager) is not null; ++retry) await Task.Delay(20);
            if (Directory.Exists(frontend))
            {
                clock.Advance(); Requests.Clear();
                string date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string route = scenario == "timeline" ? $"/usage/details?id={Id}&startDate={date}&endDate={date}" : "/usage/summary";
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ScreenTimeTracker.NativeHarness.exe")) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--legacy-ui"); start.ArgumentList.Add(baseUrl + route + "#bootstrap=" + access.CreateBootstrap());
                watch.Restart(); using var legacy = Process.Start(start)!; _ = legacy.StandardOutput.ReadToEndAsync(); _ = legacy.StandardError.ReadToEndAsync(); await WaitForWindowAsync(legacy);
                var legacyMetric = await MeasureAsync(legacy, watch.Elapsed.TotalMilliseconds);
                bool valid = Requests.Keys.Any(x => x.StartsWith("/api/screen-time/usage/", StringComparison.Ordinal) && (scenario != "timeline" || x.EndsWith("timeline", StringComparison.Ordinal)));
                metrics.Add(new { scenario, run, engine = "photino-webview", valid, metric = legacyMetric, requests = Requests.ToDictionary(x => x.Key, x => x.Value) });
                if (!valid) Console.WriteLine("Legacy page did not load business data; this measurement is invalid for comparison.");
                if (run == 0) Capture(legacy, Path.Combine(output, "legacy-" + scenario + ".png"));
                Close(legacy); await legacy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        if (extra)
        {
            // A crash must release the launcher slot, and the next tray open must recover.
            manager.OpenUI(); var crashed = await WaitForNativeAsync(manager, field);
            using (var handle = Process.GetProcessById(crashed.Id)) { crashed.Kill(); await handle.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await WaitForReleaseAsync(manager, field);
            manager.OpenUI(); var disconnected = await WaitForNativeAsync(manager, field);
            using (var handle = Process.GetProcessById(disconnected.Id)) { disconnected.StandardInput.Close(); await handle.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await WaitForReleaseAsync(manager, field);
            // EOF before initialization must exit silently instead of leaving a modal error window.
            using (var orphan = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ScreenTimeTracker.Native.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!)
            { orphan.StandardInput.Close(); await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); if (orphan.ExitCode != 0) throw new InvalidOperationException("Startup EOF did not exit cleanly."); }
            manager.OpenUI(); var shutdown = await WaitForNativeAsync(manager, field);
            using (var handle = Process.GetProcessById(shutdown.Id)) { app.Lifetime.StopApplication(); await handle.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await WaitForReleaseAsync(manager, field);
            Console.WriteLine("Crash recovery, pipe EOF, startup EOF and backend shutdown passed.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, extra ? "lifecycle-extra.json" : probe ? "legacy-probe.json" : "performance.json"), JsonSerializer.Serialize(new { syntheticData = true, realCollector = false, lifecycleCycles = cycles, extraLifecyclePassed = extra, metrics, limitations = "Private bytes and working set include UI child processes. CPU covers a 3-second idle sample. GPU is a short peak-per-engine counter sample, not sustained utilization; open time is until a window handle appears, not first meaningful paint." }, Json));
        await app.StopAsync(); return 0;
    }
    private static object Fixture(string path, HttpRequest request)
    {
        var day = DateTimeOffset.Now.Date; var start = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        List<TimelineItem> timeline = Enumerable.Range(0, 5000).Select(i => new TimelineItem(Id, "Synthetic App", "#3984D6", start.AddSeconds(i * 12), start.AddSeconds(i * 12 + 10))).ToList();
        if (path.EndsWith("timeline/page", StringComparison.Ordinal))
        { int offset = int.TryParse(request.Query["offset"], out int value) ? value : 0; return new PageResult<TimelineItem>(timeline.Skip(offset).Take(500).ToList(), offset + 500 < timeline.Count ? offset + 500 : null); }
        if (path.EndsWith("timeline", StringComparison.Ordinal)) return timeline;
        if (path.EndsWith("distribution", StringComparison.Ordinal)) return new { items = new[] { new { id = Id, name = "Synthetic App", color = "#3984D6", iconId = (string?)null, iconPathLastUpdatedAt = start, durationSeconds = 50000 } }, totalCount = 1, totalDurationSeconds = 50000, othersCount = 0, othersDurationSeconds = 0 };
        if (path.StartsWith("usage/", StringComparison.Ordinal)) return Enumerable.Range(0, 24).Select(i => new UsageBucket(start.AddHours(i), start.AddHours(i + 1), i < 16 ? 3000 : i == 16 ? 2000 : 0)).ToList();
        var entities = new[] { new { id = Id, name = "Synthetic App", color = "#3984D6", iconId = (string?)null, isSystem = false, appCategoryId = Id, websiteCategoryId = Id, processName = "synthetic", host = "synthetic.test", allowMetadataAutoRefresh = false, metadataLastRefreshedAt = start, iconPathLastUpdatedAt = start, executablePath = (string?)null } };
        return path.EndsWith("/page", StringComparison.Ordinal) ? new { items = entities, nextOffset = (int?)null } : (object)entities;
    }
    private static async Task WaitForReleaseAsync(AppUIManager manager, FieldInfo field)
    {
        for (int retry = 0; retry < 100 && field.GetValue(manager) is not null; ++retry) await Task.Delay(20);
        if (field.GetValue(manager) is not null) throw new InvalidOperationException("Launcher did not release its UI slot.");
    }
    private static async Task<Process> WaitForNativeAsync(AppUIManager manager, FieldInfo field)
    {
        for (int i = 0; i < 200; ++i) { if (field.GetValue(manager) is Process process) { await WaitForWindowAsync(process); return process; } await Task.Delay(50); }
        throw new TimeoutException("Native launch timed out.");
    }
    private static async Task WaitForWindowAsync(Process process)
    {
        for (int i = 0; i < 200; ++i) { process.Refresh(); if (process.HasExited) throw new InvalidOperationException("UI exited before opening."); if (process.MainWindowHandle != 0) return; await Task.Delay(50); }
        throw new TimeoutException("Window did not open.");
    }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);
[StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out WindowRectangle rectangle);
    private static void Capture(Process process, string path)
    {
        GetWindowRect(process.MainWindowHandle, out var rectangle);
        using var bitmap = new System.Drawing.Bitmap(Math.Max(1, rectangle.Right - rectangle.Left), Math.Max(1, rectangle.Bottom - rectangle.Top));
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { PrintWindow(process.MainWindowHandle, dc, 2); }
        finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    private static async Task CloseAndWaitAsync(Process process)
    {
        using var handle = Process.GetProcessById(process.Id); Close(process); await handle.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void Close(Process process) { process.Refresh(); PostMessage(process.MainWindowHandle, 0x0010, 0, 0); }
    private static async Task<object> MeasureAsync(Process process, double openMilliseconds)
    {
        await Task.Delay(2000);
        using var query = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "-NoProfile", "-Command", $"Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Compress" } })!;
        var result = await query.StandardOutput.ReadToEndAsync(); await query.WaitForExitAsync();
        using var json = JsonDocument.Parse(result); var ids = new HashSet<int> { process.Id }; bool added;
        do { added = false; foreach (var row in json.RootElement.EnumerateArray()) if (ids.Contains(row.GetProperty("ParentProcessId").GetInt32())) added |= ids.Add(row.GetProperty("ProcessId").GetInt32()); } while (added);
        var processes = new List<Process>();
        foreach (int id in ids) try { processes.Add(Process.GetProcessById(id)); } catch (ArgumentException) { }
        var gpuCommand = @"$ErrorActionPreference='Stop'; try { $r=Get-Counter '\GPU Engine(*)\Utilization Percentage' -SampleInterval 1 -MaxSamples 2; $s=@($r.CounterSamples | Where-Object { $_.InstanceName -match 'pid_(" + string.Join('|', ids) + ")_' } | Select-Object -ExpandProperty CookedValue); @{available=$true;peakEngineUtilPercent=($s | Measure-Object -Maximum).Maximum} | ConvertTo-Json -Compress } catch { @{available=$false;reason='GPU counters unavailable'} | ConvertTo-Json -Compress }";
        using var gpuQuery = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "-NoProfile", "-Command", gpuCommand } })!;
        var gpuResult = await gpuQuery.StandardOutput.ReadToEndAsync(); await gpuQuery.WaitForExitAsync();
        using var gpu = JsonDocument.Parse(gpuResult);
        double cpuBefore = processes.Sum(p => p.TotalProcessorTime.TotalMilliseconds); await Task.Delay(3000);
        foreach (var p in processes) p.Refresh();
        var metrics = new { processCount = processes.Count, privateBytes = processes.Sum(p => p.PrivateMemorySize64), workingSet = processes.Sum(p => p.WorkingSet64), gpu = gpu.RootElement.Clone(), idleCpuMilliseconds = processes.Sum(p => p.TotalProcessorTime.TotalMilliseconds) - cpuBefore, openMilliseconds };
        foreach (var p in processes) p.Dispose(); return metrics;
    }
    private sealed class BenchmarkClock : TimeProvider
    {
        private long _minutes;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(Interlocked.Read(ref _minutes));
        public void Advance() => Interlocked.Increment(ref _minutes);
    }
    private sealed record Credential(string Token);
}
