#if !LEGACY_WEB_UI
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTimeTracker.Desktop.Hosting;
using ScreenTimeTracker.Desktop.Security;

namespace ScreenTimeTracker.Desktop.UI.Services;

public sealed partial class AppUIManager : IAppUIManager, IDisposable
{
    private readonly LocalAccessService _access;
    private readonly IServerUrlProvider _urls;
    private readonly ILogger<AppUIManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Process? _process;
    private string? _owner;
    private readonly CancellationTokenRegistration _registration;

    public AppUIManager(LocalAccessService access, IServerUrlProvider urls, ILogger<AppUIManager> logger, IHostApplicationLifetime lifetime)
    {
        _access = access; _urls = urls; _logger = logger;
        _registration = lifetime.ApplicationStopping.Register(() => _ = StopAsync());
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Native UI launch failed: {ErrorType}")]
    private static partial void LogLaunchFailed(ILogger logger, string errorType);
    public void OpenUI() => _ = OpenAsync();
    public void OpenUIInWindow() => OpenUI();
    public void OpenUIInBrowser() => OpenUI(); // Old settings never launch a browser in the native build.
    private async Task OpenAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping.IsCancellationRequested) return;
            if (_process is { HasExited: false }) { await WriteAsync(_process, new { type = "activate" }).ConfigureAwait(false); return; }
            var path = Path.Combine(AppContext.BaseDirectory, "ScreenTimeTracker.Native.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("Publish the native UI beside the backend executable.");
            var owner = Guid.NewGuid().ToString("N");
            var process = new Process { StartInfo = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory } };
            if (!process.Start()) throw new InvalidOperationException("Native UI could not start.");
            _process = process; _owner = owner;
            _ = ObserveAsync(process, owner);
            // Drain diagnostics without logging potentially sensitive child output.
            _ = process.StandardError.ReadToEndAsync();
            await WriteAsync(process, new { type = "initialize", baseUrl = _urls.GetServerUrl(), token = _access.CreateNativeBootstrap(owner) }).ConfigureAwait(false);
        }
        catch (Exception ex) { LogLaunchFailed(_logger, ex.GetType().Name); }
        finally { _gate.Release(); }
    }
    private async Task ObserveAsync(Process process, string owner)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 4096) break;
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.GetProperty("type").GetString() == "reauthenticate")
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(_process, process) && !_stopping.IsCancellationRequested)
                            await WriteAsync(process, new { type = "credential", token = _access.CreateNativeBootstrap(owner) }).ConfigureAwait(false);
                    }
                    finally { _gate.Release(); }
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { }
        finally
        {
            _access.RevokeNativeOwner(owner);
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (ReferenceEquals(_process, process)) { _process = null; _owner = null; } }
            finally { _gate.Release(); }
            process.Dispose();
        }
    }
    private static async Task WriteAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
    }
    private async Task StopAsync()
    {
        _stopping.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_owner is not null) _access.RevokeNativeOwner(_owner);
            if (_process is { HasExited: false } process)
            {
                try { await WriteAsync(process, new { type = "shutdown" }).ConfigureAwait(false); process.StandardInput.Close(); }
                catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(); }
            }
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _registration.Dispose(); StopAsync().GetAwaiter().GetResult(); _stopping.Dispose(); }
}
#endif
