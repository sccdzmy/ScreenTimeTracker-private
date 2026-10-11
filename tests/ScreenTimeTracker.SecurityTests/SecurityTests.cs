using ScreenTimeTracker.ScreenTime.Domain.Aggregates.Apps;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastEndpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.BuildingBlocks.Security;
using ScreenTimeTracker.BuildingBlocks.Types;
using ScreenTimeTracker.Desktop.Security;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.UserSettings;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteCategories;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.ValueObjects;
using ScreenTimeTracker.ScreenTime.Features.Apps;
using ScreenTimeTracker.ScreenTime.Features.DataManagement;
using ScreenTimeTracker.ScreenTime.Features.DataManagement.ImportData;
using ScreenTimeTracker.ScreenTime.Features.Tracking;
using ScreenTimeTracker.ScreenTime.Features.Tracking.TrackWebsiteUsage;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;
using SkiaSharp;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace ScreenTimeTracker.SecurityTests;

public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

public sealed class SecurityTests : IAsyncLifetime
{
    private readonly string _previous = Environment.CurrentDirectory;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "stt-security-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private SqliteConnection _connection = null!;
    private static readonly string[] ForwardedHeaders = ["Origin", "Cookie", "X-STT-CSRF"];
    private readonly FakeTime _clock = new();
    private LocalAccessService Access => _app.Services.GetRequiredService<LocalAccessService>();
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_temporary); Environment.CurrentDirectory = _temporary;
        _connection = new SqliteConnection("Data Source=:memory:"); await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = _temporary });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<LocalAccessService>();
        builder.Services.AddDbContext<ScreenTimeDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<ActiveAppUsageSessionStore>();
        builder.Services.AddSingleton<ActiveWebsiteUsageSessionStore>();
        builder.Services.AddSingleton<TrackingCoordinator>();
        builder.Services.AddSingleton<TrackingIdentityCache>();
        builder.Services.AddSingleton<UserSettingsSnapshot>();
        builder.Services.AddSingleton<UserIdleStore>();
        builder.Services.AddSingleton<SystemSuspendStore>();
        builder.Services.AddSingleton<WebsiteActivityProcessor>(); // Deliberately no real OS collector.
        builder.Services.AddMediator(o => { o.Namespace = "SecurityTestMediator"; o.ServiceLifetime = ServiceLifetime.Scoped; });
        builder.Services.AddFastEndpoints(o => o.Assemblies = [typeof(ScreenTimeDbContext).Assembly]);
        _app = builder.Build();
        _app.UseMiddleware<LocalSecurityMiddleware>(); _app.MapLocalSecurity(); _app.MapNativeQueries();
        _app.UseFastEndpoints(c => { c.Endpoints.RoutePrefix = "api"; c.Serializer.Options.Converters.Add(new JsonStringEnumConverter(null, false)); c.Serializer.Options.Converters.Add(new OptionalValueJsonConverterFactory()); });
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.UserSettings.Add(UserSettings.CreateDefault());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await _app.StartAsync(); _client = _app.GetTestClient(); _client.BaseAddress = new Uri("http://127.0.0.1:5124");
    }
    public async ValueTask DisposeAsync()
    {
        _client.Dispose(); await _app.DisposeAsync(); await _connection.DisposeAsync();
        Environment.CurrentDirectory = _previous; Directory.Delete(_temporary, true);
    }
    private async Task Login()
    {
        _client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5124");
        var response = await _client.PostAsJsonAsync("/api/auth/session", new { token = Access.CreateBootstrap() }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        _client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        _client.DefaultRequestHeaders.Add("X-STT-CSRF", json.GetProperty("csrf").GetString());
    }
    private async Task ExtensionLogin()
    {
        var code = Access.CreatePairing();
        var response = await _client.PostAsJsonAsync("/api/auth/pair", new { code }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString());
    }
    [Theory]
    [InlineData("GET", "/api/screen-time/apps")]
    [InlineData("GET", "/api/screen-time/data/export")]
    [InlineData("DELETE", "/api/screen-time/data")]
    [InlineData("PATCH", "/api/screen-time/user-settings")]
    [InlineData("POST", "/api/icons")]
    [InlineData("GET", "/api/auth/csrf")]
    public async Task AnonymousCannotAccessSensitiveApi(string method, string path)
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Theory]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1.evil.example")]
    public async Task RebindingHostRejected(string host)
    {
        _client.DefaultRequestHeaders.Host = host;
        using var response = await _client.GetAsync("/api/auth/csrf", cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public void BootstrapAndPairingAreSingleUseAndExpire()
    {
        var token = Access.CreateBootstrap(); Assert.NotNull(Access.RedeemBootstrap(token)); Assert.Null(Access.RedeemBootstrap(token));
        token = Access.CreateBootstrap(); _clock.Advance(TimeSpan.FromSeconds(61)); Assert.Null(Access.RedeemBootstrap(token));
        var code = Access.CreatePairing(); var extension = Access.RedeemPairing(code)!; Assert.Null(Access.RedeemPairing(code)); Assert.True(Access.IsExtension(extension));
        var stored = File.ReadAllText("Data/Security/extension-hashes.json"); Assert.DoesNotContain(extension, stored);
        Access.RevokeExtensions(); Assert.False(Access.IsExtension(extension));
        code = Access.CreatePairing(); _clock.Advance(TimeSpan.FromMinutes(6)); Assert.Null(Access.RedeemPairing(code));
        Assert.Null(Access.RedeemBootstrap(null)); Assert.Null(Access.RedeemPairing(null));
    }
    [Fact]
    public async Task OriginCsrfAndExpiredSessionRejected()
    {
        await Login(); _client.DefaultRequestHeaders.Remove("Origin"); _client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/screen-time/apps", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        _client.DefaultRequestHeaders.Remove("Origin"); _client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5124");
        _client.DefaultRequestHeaders.Remove("X-STT-CSRF"); Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/auth/pairing-code", new { }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        _clock.Advance(TimeSpan.FromHours(13)); Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/screen-time/apps", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task ExtensionScopeAndRateLimitEnforced()
    {
        await ExtensionLogin(); Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/screen-time/data/export", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        for (int i = 0; i < 10; i++) await _client.GetAsync("/api/screen-time/websites/by-host/example.test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.GetAsync("/api/screen-time/websites/by-host/example.test", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Access.RevokeExtensions(); Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/screen-time/websites/by-host/example.test", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task InvalidReportDurationRejected(int duration)
    {
        await ExtensionLogin(); var r = await _client.PostAsJsonAsync("/api/screen-time/tracking/track-website-usage/record-activity", new { host = "example.test", name = "Example", durationMilliseconds = duration }, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
    [Theory]
    [InlineData("../secret")]
    [InlineData(@"C:\secret")]
    [InlineData("example.test/path")]
    [InlineData("example.test:99999")]
    [InlineData("user@example.test")]
    public void UnsafeHostsRejected(string host) => Assert.False(InputRules.Host(host));
    [Fact]
    public async Task BoundedQueueAndIdleSuspendLimits()
    {
        var processor = _app.Services.GetRequiredService<WebsiteActivityProcessor>(); var now = _clock.GetUtcNow();
        for (int i = 0; i < 256; i++) Assert.True(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(1), now.AddSeconds(i))));
        Assert.False(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(1), now.AddSeconds(256))));
        Assert.False(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(-1), now)));
        await processor.StartAsync(TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);
    }
    private static byte[] Png(int width = 2, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(SKColors.Blue); using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\outside.png")]
    [InlineData("icon.svg")]
    [InlineData("abc.png.html")]
    public void IconIdentifiersCannotEscapeManagedDirectory(string id) => Assert.Throws<ArgumentException>(() => ManagedIcons.Resolve(id));
    [Fact]
    public async Task IconsValidatedNormalizedAndAuthenticated()
    {
        await Login(); using var upload = new ByteArrayContent(Png()); var r = await _client.PostAsync("/api/icons", upload, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken); var path = ManagedIcons.Resolve(json.GetProperty("iconId").GetString())!;
        Assert.True(ManagedIcons.IsSafePath(path)); Assert.Equal(Png(), await ManagedIcons.ReadAsync(path, ct: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ManagedIcons.Normalize(Encoding.UTF8.GetBytes("<svg onload='alert(1)'></svg>")));
        Assert.Throws<ArgumentException>(() => ManagedIcons.Normalize(new byte[ManagedIcons.MaxBytes + 1]));
        Assert.Throws<ArgumentException>(() => ManagedIcons.Normalize(Png(4097, 1)));
        using var bad = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>not a PNG</html>")); Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/icons", bad, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task SymlinkCannotReadOrWriteOutsideManagedRoot()
    {
        var outside = Path.Combine(_temporary, "outside"); Directory.CreateDirectory(outside); var path = Path.Combine(outside, new string('a', 64) + ".png"); await File.WriteAllBytesAsync(path, Png(), cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory("Data");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(ManagedIcons.Root); start.ArgumentList.Add(outside);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(ManagedIcons.Root, outside);
        Assert.False(ManagedIcons.IsSafePath(Path.Combine(ManagedIcons.Root, Path.GetFileName(path))));
        await Assert.ThrowsAsync<ArgumentException>(() => ManagedIcons.SaveAsync(Png(), ct: TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(outside));
        Directory.Delete(ManagedIcons.Root);
    }
    [Theory]
    [InlineData("Hour", "2026-01-01", "2026-02-01")]
    [InlineData("Day", "2000-01-01", "2026-01-01")]
    [InlineData("Hour", "2026-01-05", "2026-01-01")]
    [InlineData("Unexpected", "2026-01-01", "2026-01-01")]
    public async Task UnboundedStatisticsQueryRejected(string granularity, string start, string end)
    {
        await Login(); var r = await _client.GetAsync($"/api/screen-time/usage/apps?granularity={granularity}&startDate={start}&endDate={end}&timeZoneId=UTC", cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
    private static string ExportDocument(object[]? apps = null, object[]? sessions = null) => JsonSerializer.Serialize(new { version = 4, apps = apps ?? [], appCategories = Array.Empty<object>(), appUsageSessions = sessions ?? [], websites = Array.Empty<object>(), websiteCategories = Array.Empty<object>(), websiteUsageSessions = Array.Empty<object>() });
    private static object FictionalApp(object? icon = null) => new { name = "Example", processName = "example.exe", color = "#123456", appCategoryName = "Uncategorized", allowMetadataAutoRefresh = true, icon };
    [Fact]
    public async Task ValidImportExportAndCrossMidnightHourStatistics()
    {
        await Login(); var json = ExportDocument([FictionalApp()], [new { appProcessName = "example.exe", startTime = "2026-01-01T23:30:00Z", endTime = "2026-01-02T00:30:00Z" }]);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/screen-time/data/import", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        var usage = await _client.GetAsync("/api/screen-time/usage/apps?granularity=Hour&startDate=2026-01-01&endDate=2026-01-02&timeZoneId=UTC", cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        var rows = await usage.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(3600, rows.EnumerateArray().Sum(x => x.GetProperty("durationSeconds").GetInt64()));
        Assert.Equal(2, rows.EnumerateArray().Count(x => x.GetProperty("durationSeconds").GetInt64() == 1800));
        var export = await _client.GetAsync("/api/screen-time/data/export", cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.OK, export.StatusCode); Assert.DoesNotContain(_temporary, await export.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ImportValidationPrecedesWritesAndAcceptsOldFormat()
    {
        await Login(); var json = ExportDocument([FictionalApp(), FictionalApp(new { extension = "../svg", data = Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg/>")) })]);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/screen-time/data/import", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        using var scope = _app.Services.CreateScope(); Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>().Apps.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        const string legacy = "{\"version\":1,\"usageSessions\":[{\"appName\":\"Example\",\"appProcessName\":\"example.exe\",\"startTime\":\"2026-01-01T10:00:00\",\"endTime\":\"2026-01-01T10:01:00\"}]}";
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/screen-time/data/import", new StringContent(legacy, Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task DangerousPathsColorsAndNullCollectionsRejected()
    {
        await Login(); foreach (var json in new[] { "{\"iconPath\":\"C:\\secret\"}", "{\"color\":\"red;background:url(x)\"}", "{\"version\":4,\"apps\":null}" })
        { var r = await _client.PostAsync("/api/screen-time/data/import", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); }
    }
    [Fact]
    public void LargeIntervalIndexAvoidsQuadraticOverlapScanning()
    {
        var start = _clock.GetUtcNow(); var index = new IntervalIndex(Enumerable.Range(0, 100000).Select(i => new TimeRange(start.AddSeconds(i * 4), start.AddSeconds(i * 4 + 1))));
        for (int i = 0; i < 100000; i++) { var gap = new TimeRange(start.AddSeconds(i * 4 + 1), start.AddSeconds(i * 4 + 2)); Assert.False(index.Overlaps(gap)); index.Add(gap); Assert.True(index.Overlaps(gap)); }
    }
    [Fact]
    public void LogicalDayHandlesTimezoneAndDst()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var spring = UsageTimeCalculator.GetLogicalDayStartInUtc(new DateOnly(2026, 3, 8), 2, eastern); Assert.Equal(7, spring.Hour);
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); var midnight = UsageTimeCalculator.GetLogicalDayStartInUtc(new DateOnly(2026, 1, 2), 0, shanghai); Assert.Equal(new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.Zero), midnight);
    }
    [Theory]
    [InlineData("chrome")]
    [InlineData("firefox")]
    public async Task BrowserBootstrapSettingsPairingAndTextSafety(string browserName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = _temporary });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<TimeProvider>(_clock); builder.Services.AddSingleton(Access);
        builder.Services.AddDbContext<ScreenTimeDbContext>(o => o.UseSqlite(_connection));
        await using var web = builder.Build();
        web.UseMiddleware<LocalSecurityMiddleware>(); web.MapLocalSecurity();
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "ScreenTimeTracker.slnx"))) repo = repo.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var frontend = Path.Combine(repo.FullName, "src", "frontend", "dist");
        web.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(frontend) });
        web.MapGet("/api/desktop-settings/local-settings", () => Results.Ok(new { defaultUIOpenMode = "Browser", isAutoStartEnabled = false, isSilentStartEnabled = false, language = "en-US" }));
        web.Map("/api/{**path}", async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}");
            if (context.Request.Method != "GET") { var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body, TestContext.Current.CancellationToken); body.Position = 0; request.Content = new StreamContent(body); request.Content.Headers.ContentType = new("application/json"); }
            foreach (var header in ForwardedHeaders) if (context.Request.Headers.TryGetValue(header, out var value)) request.Headers.TryAddWithoutValidation(header, value.ToArray());
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, TestContext.Current.CancellationToken);
        });
        web.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(frontend) });
        await web.StartAsync(TestContext.Current.CancellationToken);
        using var probeClient = new HttpClient();
        using var probe = await probeClient.GetAsync(web.Urls.Single() + "/", TestContext.Current.CancellationToken);
        Assert.True(probe.IsSuccessStatusCode, $"Homepage returned {(int)probe.StatusCode}: {await probe.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        if (Directory.Exists(Path.Combine(repo.FullName, "artifacts", "test-browsers"))) Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(repo.FullName, "artifacts", "test-browsers"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = browserName == "chrome" ? await playwright.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true }) : await playwright.Firefox.LaunchAsync(new() { Headless = true });
        const string hostileName = "<img src=x onerror=window.__sttXss=1>";
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            db.Apps.Add(App.Import(hostileName, "#123456", "hostile-example.exe", true, AppCategory.UncategorizedId, null));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var page = await browser.NewPageAsync();
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        var address = web.Urls.Single();
        await page.GotoAsync(address + "/#bootstrap=" + Access.CreateBootstrap());
        await page.WaitForFunctionAsync("location.hash === ''");
        await page.GotoAsync(address + "/apps");
        await page.WaitForFunctionAsync("value => Array.from(document.querySelectorAll(\"input\")).some(el => el.value === value)", hostileName);
        Assert.Equal(0, await page.Locator("img[src=x]").CountAsync());
        Assert.True(await page.EvaluateAsync<bool>("window.__sttXss === undefined"));
        await page.GotoAsync(address + "/settings");
        var button = page.GetByRole(AriaRole.Button, new() { Name = "Generate pairing code / 生成配对码", Exact = true });
        await button.ClickAsync();
        await page.GetByText("Paste into the extension settings", new() { Exact = false }).WaitForAsync();
        Assert.Empty(errors);
        var cookies = await page.Context.CookiesAsync(); Assert.Contains(cookies, c => c.Name == "stt-session" && c.HttpOnly && c.SameSite == SameSiteAttribute.Strict);
        // The actual generated chart output uses escaped HTML for hostile names.
        Assert.Equal(0, await page.Locator("script:not([src])").CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Revoke all extensions / 撤销全部扩展", Exact = true }).ClickAsync();
        await page.GetByText("Extensions revoked", new() { Exact = false }).WaitForAsync();
        await web.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ImportSizeAndRecordLimitsRejectBeforeWriting()
    {
        await Login();
        using var huge = new StringContent("{}", Encoding.UTF8, "application/json"); huge.Headers.ContentLength = 64L * 1024 * 1024 + 1;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/screen-time/data/import", huge, TestContext.Current.CancellationToken)).StatusCode);
        var session = new { appProcessName = "example.exe", startTime = "2026-01-01T10:00:00Z", endTime = "2026-01-01T10:01:00Z" };
        var json = ExportDocument(sessions: Enumerable.Repeat<object>(session, 100001).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/screen-time/data/import", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken)).StatusCode);
    }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected transaction failure.");
    }
    [Fact]
    public async Task FailedTransactionCleansOnlyItsOwnIcons()
    {
        var retained = await ManagedIcons.SaveAsync(Png(), TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ScreenTimeDbContext>().UseSqlite(_connection).AddInterceptors(new FailSave()).Options;
        await using var db = new ScreenTimeDbContext(options);
        var handler = new ImportDataHandler(_app.Services.GetRequiredService<TrackingCoordinator>(), _app.Services.GetRequiredService<UserSettingsSnapshot>(), _app.Services.GetRequiredService<TrackingIdentityCache>(), db, _clock, new ActiveAppUsageSessionStore(), new ActiveWebsiteUsageSessionStore());
        var json = ExportDocument([FictionalApp(new { extension = "png", data = Convert.ToBase64String(Png()) })]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new ImportDataCommand(json), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(retained, Directory.GetFiles(ManagedIcons.Root).Single());
        Assert.Equal(2, await db.Apps.CountAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task LegacyIconMigrationPreservesHistoryAndExternalFiles()
    {
        Directory.CreateDirectory("Data/AppIcons"); var legacy = Path.GetFullPath("Data/AppIcons/example.png"); await File.WriteAllBytesAsync(legacy, Png(), TestContext.Current.CancellationToken);
        var outside = Path.Combine(_temporary, "outside.png"); await File.WriteAllBytesAsync(outside, Png(), TestContext.Current.CancellationToken);
        using var scope = _app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        var app = App.Import("Example", "#123456", "example.exe", true, AppCategory.UncategorizedId, legacy);
        var other = App.Import("Other", "#123456", "other.exe", true, AppCategory.UncategorizedId, outside);
        db.Apps.AddRange(app, other); db.AppUsageSessions.Add(AppUsageSession.Import(app.Id, new TimeRange(_clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow())));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ManagedIconMigration.RunAsync(db, TestContext.Current.CancellationToken);
        Assert.True(ManagedIcons.IsSafePath(app.IconPath)); Assert.Null(other.IconPath);
        Assert.True(File.Exists(outside)); Assert.True(File.Exists(legacy)); Assert.Equal(1, await db.AppUsageSessions.CountAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task IdleAndSuspendReportsCannotCreateUsageOrBackfillTime()
    {
        var idle = _app.Services.GetRequiredService<UserIdleStore>(); var suspend = _app.Services.GetRequiredService<SystemSuspendStore>();
        var processor = _app.Services.GetRequiredService<WebsiteActivityProcessor>();
        idle.Current = new(true); Assert.True(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(5), _clock.GetUtcNow())));
        idle.Current = new(false); suspend.Current = new(true); _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(5), _clock.GetUtcNow())));
        suspend.Current = new(false); await processor.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(processor.Enqueue(new("example.test", "Example", TimeSpan.FromSeconds(5), _clock.GetUtcNow())));
        var sessions = _app.Services.GetRequiredService<ActiveWebsiteUsageSessionStore>();
        for (int i = 0; i < 100 && sessions.Current is null; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.NotNull(sessions.Current); Assert.Equal(TimeSpan.FromMilliseconds(1500), sessions.Current.LastActiveAt - sessions.Current.StartTime);
        await processor.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public void JpegAndIcoContentAreNormalizedButBadContainersRejected()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red); using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        Assert.NotEmpty(ManagedIcons.Normalize(jpeg.ToArray()));
        var png = Png(); var ico = new byte[22 + png.Length]; ico[2] = 1; ico[4] = 1; ico[6] = 2; ico[7] = 2;
        BitConverter.GetBytes(png.Length).CopyTo(ico, 14); BitConverter.GetBytes(22).CopyTo(ico, 18); png.CopyTo(ico, 22);
        Assert.NotEmpty(ManagedIcons.Normalize(ico)); ico[18] = 1; Assert.Throws<ArgumentException>(() => ManagedIcons.Normalize(ico));
        var paletteIco = new byte[22 + 64]; paletteIco[2]=1; paletteIco[4]=1; paletteIco[6]=2; paletteIco[7]=2;
        BitConverter.GetBytes(64).CopyTo(paletteIco,14); BitConverter.GetBytes(22).CopyTo(paletteIco,18);
        BitConverter.GetBytes(40).CopyTo(paletteIco,22); BitConverter.GetBytes(2).CopyTo(paletteIco,26); BitConverter.GetBytes(4).CopyTo(paletteIco,30);
        paletteIco[34]=1; paletteIco[36]=1; paletteIco[66]=255; paletteIco[67]=255; paletteIco[68]=255;
        paletteIco[70]=128; paletteIco[74]=128;
        Assert.NotEmpty(ManagedIcons.Normalize(paletteIco));

    }

    [Fact]
    public async Task WebsiteHistoryRankingTimelineAndIconCrudRemainCompatible()
    {
        await Login();
        var json = JsonSerializer.Serialize(new { version = 4, apps = Array.Empty<object>(), appCategories = Array.Empty<object>(), appUsageSessions = Array.Empty<object>(), websiteCategories = Array.Empty<object>(), websites = new[] { new { name = "Example Website", color = "#123456", host = "example.test:8443", allowMetadataAutoRefresh = true, websiteCategoryName = "Uncategorized", icon = (object?)null } }, websiteUsageSessions = new[] { new { websiteHost = "example.test:8443", startTime = "2026-01-01T23:30:00Z", endTime = "2026-01-02T00:30:00Z" } } });
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/screen-time/data/import", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken)).StatusCode);
        const string query = "?startDate=2026-01-01&endDate=2026-01-02&timeZoneId=UTC";
        var hour = await _client.GetFromJsonAsync<JsonElement>("/api/screen-time/usage/websites" + query + "&granularity=Hour", TestContext.Current.CancellationToken);
        Assert.Equal(3600, hour.EnumerateArray().Sum(x => x.GetProperty("durationSeconds").GetInt64()));
        var rank = await _client.GetFromJsonAsync<JsonElement>("/api/screen-time/usage/websites/distribution" + query, TestContext.Current.CancellationToken); Assert.Equal(3600, rank.GetProperty("totalDurationSeconds").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/screen-time/usage/websites/timeline" + query, TestContext.Current.CancellationToken)).StatusCode);
        var site = await _client.GetFromJsonAsync<JsonElement>("/api/screen-time/websites/by-host/example.test%3A8443", TestContext.Current.CancellationToken); var id = site.GetProperty("id").GetString();
        using var content = new ByteArrayContent(Png()); var upload = await _client.PostAsync("/api/icons", content, TestContext.Current.CancellationToken); var icon = await upload.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var patch = await _client.PatchAsJsonAsync($"/api/screen-time/websites/{id}", new { iconId = icon.GetProperty("iconId").GetString() }, TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/screen-time/websites/{id}/icon", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/screen-time/websites/{id}/icon", TestContext.Current.CancellationToken)).StatusCode);
        await _client.PatchAsJsonAsync($"/api/screen-time/websites/{id}", new { allowMetadataAutoRefresh = false }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/screen-time/websites/{id}/refresh-metadata", new { icon = (object?)null }, TestContext.Current.CancellationToken)).StatusCode);
    }
    [Theory]
    [InlineData("{\"durationMilliseconds\":\"5000\"}")]
    [InlineData("{\"durationMilliseconds\":1,\"DurationMilliseconds\":5001}")]
    [InlineData("{\"icon\":{\"extension\":\"png\",\"data\":null}}")]
    public void MalformedScalarAndDuplicatePropertiesRejected(string json)
    {
        using var document = JsonDocument.Parse(json); Assert.Throws<ArgumentException>(() => InputRules.Json(document.RootElement));
    }

    [Fact]
    public void WindowsCaseAliasesCannotBypassIconNameAllowlist()
    {
        Assert.False(ManagedIcons.IsSafePath(Path.Combine(ManagedIcons.Root.ToUpperInvariant(), "unsafe.html")));
        Assert.False(ManagedIcons.IsSafePath(Path.Combine(Path.GetFullPath("PrePreparedCategoryIcons").ToUpperInvariant(), "unsafe.svg")));
    }

    [Fact]
    public void NativeCredentialsAreIsolatedSingleUseExpiringAndRevocable()
    {
        var launch = Access.CreateNativeBootstrap("ui-1");
        Assert.Null(Access.RedeemBootstrap(launch));
        var token = Access.RedeemNativeBootstrap(launch)!;
        Assert.True(Access.IsNativeSession(token)); Assert.False(Access.IsExtension(token));
        Assert.Null(Access.RedeemNativeBootstrap(launch));
        Assert.Null(Access.RedeemNativeBootstrap(Access.CreateBootstrap()));
        var second = Access.RedeemNativeBootstrap(Access.CreateNativeBootstrap("ui-2"))!;
        Access.RevokeNativeOwner("ui-1"); Assert.False(Access.IsNativeSession(token)); Assert.True(Access.IsNativeSession(second));
        var pending = Access.CreateNativeBootstrap("ui-2"); _clock.Advance(TimeSpan.FromSeconds(61)); Assert.Null(Access.RedeemNativeBootstrap(pending));
        _clock.Advance(TimeSpan.FromHours(12)); Assert.False(Access.IsNativeSession(second));
    }
    [Fact]
    public async Task NativeBootstrapAllowsRepeatedWindowReopensAndStillEnforcesRateLimit()
    {
        for (int i = 0; i < 120; ++i)
        {
            using var response = await _client.PostAsJsonAsync("/api/auth/native-session", new { token = Access.CreateNativeBootstrap("reopen") }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Access.RevokeNativeOwner("reopen");
        }
        using var limited = await _client.PostAsJsonAsync("/api/auth/native-session", new { token = Access.CreateNativeBootstrap("reopen") }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
    private async Task NativeLogin()
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/native-session", new { token = Access.CreateNativeBootstrap("integration-ui") }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<ScreenTimeTracker.ApiClient.NativeSession>(TestContext.Current.CancellationToken);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", session!.Token);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
    [Fact]
    public async Task NativeSessionWorksWithoutBrowserHeadersAndRevocationRejectsIt()
    {
        await NativeLogin();
        var info = await _client.GetFromJsonAsync<ScreenTimeTracker.ApiClient.SystemInfo>("/api/system/info", TestContext.Current.CancellationToken);
        Assert.Equal(1, info!.ProtocolVersion);
        using var patch = await _client.PatchAsJsonAsync("/api/screen-time/user-settings", new { timeBoundary = new { dayCutoffHour = 4 } }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        Access.RevokeNativeOwner("integration-ui");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/system/info", TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task NativeTokenCannotBeUsedFromBrowserAndExtensionCannotReadSystemInfo()
    {
        await NativeLogin(); _client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5124");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/system/info", TestContext.Current.CancellationToken)).StatusCode);
        _client.DefaultRequestHeaders.Remove("Origin"); await ExtensionLogin();
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/system/info", TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task PagedListsAndTimelinesKeepExactLegacyRecords()
    {
        await NativeLogin();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
            var app = App.Import("Synthetic", "#123456", "synthetic", false, AppCategory.UncategorizedId, null);
            db.Apps.Add(app);
            db.AppUsageSessions.Add(AppUsageSession.Import(app.Id, new TimeRange(DateTimeOffset.Parse("2026-01-01T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-01-02T00:30:00Z", System.Globalization.CultureInfo.InvariantCulture))));
            db.AppUsageSessions.Add(AppUsageSession.Import(app.Id, new TimeRange(DateTimeOffset.Parse("2026-01-02T03:30:00Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-01-02T04:30:00Z", System.Globalization.CultureInfo.InvariantCulture))));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var all = await _client.GetFromJsonAsync<List<ScreenTimeTracker.ApiClient.EntityItem>>("/api/screen-time/apps", TestContext.Current.CancellationToken);
        var paged = new List<ScreenTimeTracker.ApiClient.EntityItem>(); int? offset = 0;
        do
        {
            var page = await _client.GetFromJsonAsync<ScreenTimeTracker.ApiClient.PageResult<ScreenTimeTracker.ApiClient.EntityItem>>($"/api/screen-time/apps/page?pageSize=1&offset={offset}", TestContext.Current.CancellationToken);
            paged.AddRange(page!.Items); offset = page.NextOffset;
        } while (offset is not null);
        Assert.Equal(all!.Select(x => x.Id), paged.Select(x => x.Id));
        foreach (int cutoff in new[] { 0, 4 })
        {
            await _client.PatchAsJsonAsync("/api/screen-time/user-settings", new { timeBoundary = new { dayCutoffHour = cutoff } }, TestContext.Current.CancellationToken);
            const string query = "startDate=2026-01-02&endDate=2026-01-02&timeZoneId=UTC";
            var legacy = await _client.GetFromJsonAsync<List<ScreenTimeTracker.ApiClient.TimelineItem>>("/api/screen-time/usage/apps/timeline?" + query, TestContext.Current.CancellationToken);
            var records = new List<ScreenTimeTracker.ApiClient.TimelineItem>(); offset = 0;
            do
            {
                var page = await _client.GetFromJsonAsync<ScreenTimeTracker.ApiClient.PageResult<ScreenTimeTracker.ApiClient.TimelineItem>>($"/api/screen-time/usage/apps/timeline/page?{query}&pageSize=1&offset={offset}", TestContext.Current.CancellationToken);
                records.AddRange(page!.Items); offset = page.NextOffset;
            } while (offset is not null);
            Assert.Equal(legacy, records);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/screen-time/apps/page?pageSize=501", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/screen-time/usage/apps/timeline/page?startDate=2026-01-01&endDate=2026-01-02&timeZoneId=UTC", TestContext.Current.CancellationToken)).StatusCode);
    }

}
