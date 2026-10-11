using System.IO;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.ViewModels;
public sealed class DataModel(TrackerClient client) : PageModel(client)
{
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public override Task RefreshAsync() => Task.CompletedTask;
    public Task ExportAsync(string path) => RunAsync(async ct =>
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await using (var stream = File.Create(temporary)) await Client.ExportAsync(stream, ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, false);
    public Task ImportAsync(string path) => RunAsync(async ct => { await using var stream = File.OpenRead(path); await Client.ImportAsync(stream, ct); }, false);
    public Task ClipboardExportAsync() => RunAsync(async ct =>
    {
        // Clipboard necessarily needs one string; file exports stream directly instead.
        string temporary = Path.GetTempFileName();
        try { await using (var stream = File.Create(temporary)) await Client.ExportAsync(stream, ct); var text = await File.ReadAllTextAsync(temporary, ct); ct.ThrowIfCancellationRequested(); System.Windows.Clipboard.SetText(text); }
        finally { File.Delete(temporary); }
    }, false);
    public Task ClipboardImportAsync() => RunAsync(async ct =>
    {
        string temporary = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(temporary, System.Windows.Clipboard.GetText(), ct); await using var stream = File.OpenRead(temporary); await Client.ImportAsync(stream, ct); }
        finally { File.Delete(temporary); }
    }, false);
    public Task DeleteAsync() => RunAsync(async ct =>
    {
        if (StartDate > EndDate) throw new ArgumentException("Invalid date range.");
        var query = TrackerClient.UsageQuery(DateOnly.FromDateTime(StartDate), DateOnly.FromDateTime(EndDate), TimeZoneId);
        await Client.ChangeAsync(HttpMethod.Delete, "/api/screen-time/data?" + query, null, ct);
    }, false);
}
