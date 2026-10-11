using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text.Json;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScreenTimeTracker.BuildingBlocks.Security;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;

public partial class ExportDataHandler(ILogger<ExportDataHandler> logger, ScreenTimeDbContext context,
    ExportGenerationGate storage) : IRequestHandler<ExportDataQuery, ExportFile?>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async ValueTask<ExportFile?> Handle(ExportDataQuery request, CancellationToken cancellationToken)
    {
        if (!storage.TryEnter()) return null;
        string? path = null;
        string? snapshotPath = null;
        try
        {
            snapshotPath = storage.CreatePath(snapshot: true);
            // Reserve our generated file before SQLite opens it; no caller-controlled paths.
            using (new FileStream(snapshotPath, FileOptionsForExport())) { }
            if (!storage.IsSafe(snapshotPath)) throw new IOException("Unsafe export storage.");
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = snapshotPath, Pooling = false }.ToString());
                await destination.OpenAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ((SqliteConnection)context.Database.GetDbConnection()).BackupDatabase(destination);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { await context.Database.CloseConnectionAsync(); }
            // Retain configured interceptors for tests, but read a frozen copy without retaining a live DB lock.
            var originalOptions = (DbContextOptions<ScreenTimeDbContext>)context.GetService<IDbContextOptions>();
            var snapshotOptions = new DbContextOptionsBuilder<ScreenTimeDbContext>(originalOptions)
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options;
            await using var snapshot = new ScreenTimeDbContext(snapshotOptions);
            path = storage.CreatePath();
            var options = FileOptionsForExport();
            await using (var stream = new FileStream(path, options))
            {
                if (!storage.IsSafe(path)) throw new IOException("Unsafe export storage.");
                using var json = new Utf8JsonWriter(stream);
                json.WriteStartObject();
                json.WriteNumber("version", 4);
                json.WritePropertyName("apps"); json.WriteStartArray();
                var apps = snapshot.Apps.AsNoTracking().Join(snapshot.AppCategories.AsNoTracking(), a => a.AppCategoryId,
                    c => c.Id, (a, c) => new { a.Name, a.Color, a.ProcessName, a.AllowMetadataAutoRefresh, Category = c.Name, a.IconPath });
                await foreach (var a in apps.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    JsonSerializer.Serialize(json, new ExportDataResponse.App(a.Name, a.Color, a.ProcessName,
                        a.AllowMetadataAutoRefresh, a.Category, await GetIconAsync(a.IconPath, cancellationToken)), JsonOptions);
                    await json.FlushAsync(cancellationToken);
                }
                json.WriteEndArray();
                json.WritePropertyName("appCategories"); json.WriteStartArray();
                await foreach (var c in snapshot.AppCategories.AsNoTracking().Select(c => new { c.Name, c.Color, c.IconPath }).AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    JsonSerializer.Serialize(json, new ExportDataResponse.AppCategory(c.Name, c.Color, await GetIconAsync(c.IconPath, cancellationToken)), JsonOptions);
                    await json.FlushAsync(cancellationToken);
                }
                json.WriteEndArray();
                await WriteSessionsAsync(json, "appUsageSessions", snapshot.AppUsageSessions.AsNoTracking().Join(snapshot.Apps.AsNoTracking(),
                    s => s.AppId, a => a.Id, (s, a) => new ExportDataResponse.AppUsageSession(a.ProcessName, s.StartTime, s.EndTime)).AsAsyncEnumerable(), cancellationToken);
                json.WritePropertyName("websites"); json.WriteStartArray();
                var websites = snapshot.Websites.AsNoTracking().Join(snapshot.WebsiteCategories.AsNoTracking(), w => w.WebsiteCategoryId,
                    c => c.Id, (w, c) => new { w.Name, w.Color, w.Host, w.AllowMetadataAutoRefresh, Category = c.Name, w.IconPath });
                await foreach (var w in websites.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    JsonSerializer.Serialize(json, new ExportDataResponse.Website(w.Name, w.Color, w.Host,
                        w.AllowMetadataAutoRefresh, w.Category, await GetIconAsync(w.IconPath, cancellationToken)), JsonOptions);
                    await json.FlushAsync(cancellationToken);
                }
                json.WriteEndArray();
                json.WritePropertyName("websiteCategories"); json.WriteStartArray();
                await foreach (var c in snapshot.WebsiteCategories.AsNoTracking().Select(c => new { c.Name, c.Color, c.IconPath }).AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    JsonSerializer.Serialize(json, new ExportDataResponse.WebsiteCategory(c.Name, c.Color, await GetIconAsync(c.IconPath, cancellationToken)), JsonOptions);
                    await json.FlushAsync(cancellationToken);
                }
                json.WriteEndArray();
                await WriteSessionsAsync(json, "websiteUsageSessions", snapshot.WebsiteUsageSessions.AsNoTracking().Join(snapshot.Websites.AsNoTracking(),
                    s => s.WebsiteId, w => w.Id, (s, w) => new ExportDataResponse.WebsiteUsageSession(w.Host, s.StartTime, s.EndTime)).AsAsyncEnumerable(), cancellationToken);
                json.WriteEndObject(); await json.FlushAsync(cancellationToken);
            }
            return new ExportFile(storage, path);
        }
        catch
        {
            if (path is not null) storage.Delete(path);
            throw;
        }
        finally
        {
            try
            {
                if (snapshotPath is not null)
                    foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) storage.Delete(snapshotPath + suffix);
            }
            finally { storage.Exit(); }
        }
    }

    private static FileStreamOptions FileOptionsForExport()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 65536 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static async Task WriteSessionsAsync<T>(Utf8JsonWriter json, string name, IAsyncEnumerable<T> rows, CancellationToken ct)
    {
        json.WritePropertyName(name); json.WriteStartArray();
        var count = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            JsonSerializer.Serialize(json, row, JsonOptions);
            if (++count % 64 == 0) await json.FlushAsync(ct);
        }
        json.WriteEndArray(); await json.FlushAsync(ct);
    }

    public async Task<ExportDataResponse.Icon?> GetIconAsync(string? iconPath, CancellationToken cancellationToken)
    {
        if (!ManagedIcons.IsSafePath(iconPath) || !File.Exists(iconPath)) return null;
        try { return new ExportDataResponse.Icon(Path.GetExtension(iconPath), await ManagedIcons.ReadAsync(iconPath!, cancellationToken)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { LogReadIconFailed(logger); return null; }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "An export icon could not be read.")]
    private static partial void LogReadIconFailed(ILogger logger);
}
