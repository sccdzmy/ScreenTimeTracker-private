using ScreenTimeTracker.ScreenTime.Domain.Aggregates.AppUsageSessions;
using ScreenTimeTracker.ScreenTime.Domain.Aggregates.WebsiteUsageSessions;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTimeTracker.ScreenTime.Domain.Services;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

public sealed record CompactionRun(int Batches, int ReadRows, int LargestPage);

/// <summary>Only a root and two cursors survive a batch. Unresolved intervening rows remain in SQLite.</summary>
public sealed class SessionCompactionRunner(UserSettingsSnapshot settingsSnapshot, TrackingIdentityCache identities,
    TrackingCoordinator coordinator, ActiveAppUsageSessionStore apps, ActiveWebsiteUsageSessionStore websites,
    TimeProvider clock, IServiceScopeFactory scopes) : IDisposable
{
    public const int PageSize = 1024;
    private readonly SemaphoreSlim[] _gates = [new(1, 1), new(1, 1)];
    private readonly Continuation?[] _states = new Continuation?[2];
    private readonly TrackingSettings?[] _settings = new TrackingSettings?[2];
    private readonly long[] _versions = [-1, -1];
    public void Dispose() { foreach (var gate in _gates) gate.Dispose(); }

    public async Task<CompactionRun> RunAsync(bool website, CancellationToken ct,
        int maxBatches = int.MaxValue)
    {
        var slot = website ? 1 : 0;
        await _gates[slot].WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            var batches = 0; var rows = 0; var largest = 0;
            do
            {
                var (read, more) = await BatchAsync(website, slot, ct);
                if (read == 0 && !more) break;
                batches++; rows += read; largest = Math.Max(largest, read);
                if (!more) break;
                await Task.Yield();
            } while (batches < maxBatches && watch.Elapsed < TimeSpan.FromSeconds(2));
            return new CompactionRun(batches, rows, largest);
        }
        finally { _gates[slot].Release(); }
    }

    private async Task<(int Read, bool More)> BatchAsync(bool website, int slot, CancellationToken ct)
    {
        using var lease = await coordinator.EnterAsync(ct);
        var settings = await settingsSnapshot.GetAsync(ct);
        var version = website ? identities.WebsiteVersion : identities.AppVersion;
        var reset = version != _versions[slot] || !ReferenceEquals(settings, _settings[slot]);
        var state = reset ? null : _states[slot];
        var firstBatch = reset;
        var now = clock.GetUtcNow();
        var activeStart = website ? websites.Current?.StartTime : apps.Current?.StartTime;
        var elapsed = activeStart is { } start ? now - start : TimeSpan.Zero;
        var buffer = elapsed > settings.IdleDetection.InactivityThreshold ? elapsed : settings.IdleDetection.InactivityThreshold;
        var cutoff = (now - TimeSpan.FromMinutes(1) - buffer).ToUnixTimeMilliseconds();
        var tolerance = website ? settings.WebsiteTracking.UsageSessionMergeTolerance : settings.AppTracking.UsageSessionMergeTolerance;
        var minimum = website ? settings.WebsiteTracking.MinValidUsageSessionDuration : settings.AppTracking.MinValidUsageSessionDuration;
        var table = website ? "ScreenTime_WebsiteUsageSessions" : "ScreenTime_AppUsageSessions";
        var owner = website ? "WebsiteId" : "AppId";
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScreenTimeDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var projection = $"SELECT Id, {owner} AS IdentityId, StartTime AS Start, EndTime AS End FROM {table}";
        if (state is not null)
        {
            var root = await Query(db, projection + " WHERE Id = $id AND EndTime <= $cutoff",
                P("$id", state.Root.Id), P("$cutoff", cutoff)).SingleOrDefaultAsync(ct);
            if (root is null || root.Start != state.Root.Start || root.End != state.Root.End || root.IdentityId != state.Root.IdentityId)
            { state = null; firstBatch = true; }
        }
        if (state is null)
        {
            var root = await Query(db, projection + " WHERE NOT IsOptimized AND EndTime <= $cutoff ORDER BY StartTime, Id LIMIT 1", P("$cutoff", cutoff)).FirstOrDefaultAsync(ct);
            if (root is null)
            {
                await tx.CommitAsync(ct); Publish(slot, null, settings, version); lease.Complete();
                return (0, false);
            }
            if (firstBatch)
            {
                var anchor = await Query(db, projection + " WHERE IsOptimized AND EndTime <= $start ORDER BY EndTime DESC, Id DESC LIMIT 1", P("$start", root.Start)).FirstOrDefaultAsync(ct);
                if (anchor is not null)
                    root = await Query(db, projection + " WHERE StartTime >= $start AND EndTime <= $cutoff ORDER BY StartTime, Id LIMIT 1", P("$start", anchor.Start), P("$cutoff", cutoff)).FirstAsync(ct);
            }
            state = new Continuation(root, Cursor.Of(root), Cursor.Of(root));
        }
        var incoming = state;
        var page = await Query(db, projection + " WHERE EndTime <= $cutoff AND (StartTime > $start OR (StartTime = $start AND Id > $id)) ORDER BY StartTime, Id LIMIT 1024",
            P("$cutoff", cutoff), P("$start", state.Scan.Start), P("$id", state.Scan.Id)).ToListAsync(ct);
        var exhausted = page.Count < PageSize;
        var marks = new List<Guid>(); var drops = new List<Guid>(); var writes = new List<Continuation>();
        var index = 0;
        var lastMatchIndex = -1;
        while (true)
        {
            if (index == page.Count && !exhausted) break;
            var closed = index == page.Count || SessionMergeAlgorithm.BeyondTolerance(
                DateTimeOffset.FromUnixTimeMilliseconds(state.Root.End), DateTimeOffset.FromUnixTimeMilliseconds(page[index].Start), tolerance);
            if (closed)
            {
                writes.Add(state);
                if (TimeSpan.FromMilliseconds(state.Root.End - state.Root.Start) < minimum) drops.Add(state.Root.Id);
                else marks.Add(state.Root.Id);
                // A prior page may hold unresolved rows. Re-seek them from SQLite next batch.
                if (lastMatchIndex < 0 && state.LastMatch.CompareTo(incoming.Scan) < 0)
                { state = null; break; }
                var nextIndex = lastMatchIndex + 1;
                if (nextIndex >= page.Count) { state = null; break; }
                var next = page[nextIndex];
                state = new Continuation(next, Cursor.Of(next), Cursor.Of(next));
                index = nextIndex + 1;
                lastMatchIndex = nextIndex;
                continue;
            }
            var row = page[index++];
            var cursor = Cursor.Of(row);
            state = state with { Scan = cursor };
            if (row.IdentityId == state.Root.IdentityId)
            {
                lastMatchIndex = index - 1;
                state = state with { Root = new CompactionRow { Id = state.Root.Id, IdentityId = state.Root.IdentityId, Start = state.Root.Start, End = row.End }, LastMatch = cursor };
            }
        }
        if (state is not null) writes.Add(state);
        foreach (var write in writes)
        {
            if (write.LastMatch == Cursor.Of(write.Root) || (write.Root.Id == incoming.Root.Id && write.LastMatch == incoming.LastMatch && write.Root.End == incoming.Root.End)) continue;
            await ExecuteAsync(db, $"DELETE FROM {table} WHERE EndTime <= $cutoff AND (StartTime > $start OR (StartTime = $start AND Id > $root)) AND (StartTime < $endStart OR (StartTime = $endStart AND Id <= $endId))",
                new object[] { P("$cutoff", cutoff), P("$start", write.Root.Start), P("$root", write.Root.Id), P("$endStart", write.LastMatch.Start), P("$endId", write.LastMatch.Id) }, ct);
            await ExecuteAsync(db, $"UPDATE {table} SET EndTime = $end, IsOptimized = 0 WHERE Id = $id",
                new object[] { P("$end", write.Root.End), P("$id", write.Root.Id) }, ct);
        }
        if (marks.Count != 0)
            await ExecuteAsync(db, $"UPDATE {table} SET IsOptimized = 1 WHERE Id IN (SELECT value FROM json_each($ids))", new object[] { P("$ids", Ids(marks)) }, ct);
        if (drops.Count != 0)
            await ExecuteAsync(db, $"DELETE FROM {table} WHERE Id IN (SELECT value FROM json_each($ids))", new object[] { P("$ids", Ids(drops)) }, ct);
        await tx.CommitAsync(ct);
        Publish(slot, state, settings, version); lease.Complete();
        // Even at EOF, a previous page may have left intervening rows to revisit.
        return (page.Count, state is not null || !exhausted || incoming.Scan != incoming.LastMatch);
    }

    // Identifiers are selected above from two fixed module tables; all data remains parameterized.
    private static IQueryable<CompactionRow> Query(ScreenTimeDbContext db, string sql, params object[] parameters) =>
        db.Database.SqlQueryRaw<CompactionRow>(sql, parameters);
    private static Task<int> ExecuteAsync(ScreenTimeDbContext db, string sql, object[] parameters, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(sql, parameters, ct);

    private void Publish(int slot, Continuation? state, TrackingSettings settings, long version)
    { _states[slot] = state; _settings[slot] = settings; _versions[slot] = version; }
    private static SqliteParameter P(string name, object value) => new(name, value);
    private static string Ids(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.Select(id => id.ToString().ToUpperInvariant()));
    private sealed record Continuation(CompactionRow Root, Cursor Scan, Cursor LastMatch);
    private readonly record struct Cursor(long Start, string Id) : IComparable<Cursor>
    {
        public static Cursor Of(CompactionRow row) => new(row.Start, row.CursorId);
        public int CompareTo(Cursor other) => Start == other.Start ? string.CompareOrdinal(Id, other.Id) : Start.CompareTo(other.Start);
    }
}

public sealed class CompactionRow
{
    private string? _cursorId;
    internal string CursorId => _cursorId ??= Id.ToString().ToUpperInvariant();
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
}
