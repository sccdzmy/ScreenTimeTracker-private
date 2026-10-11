using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;

namespace ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;

/// <summary>One generation at a time; completed files may be downloaded independently.</summary>
public sealed partial class ExportGenerationGate : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Root { get; }
    public ExportGenerationGate() : this(Path.GetFullPath("./Data/ExportTemporary")) { }
    public ExportGenerationGate(string root) => Root = Path.GetFullPath(root);
    [GeneratedRegex("^export-[a-f0-9]{32}\\.(json\\.tmp|snapshot\\.db(-journal|-wal|-shm)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public bool TryEnter() => _gate.Wait(0);
    public void Exit() => _gate.Release();
    public void Dispose() => _gate.Dispose();
    public bool IsSafe(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !NamePattern().IsMatch(Path.GetFileName(path))) return false;
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public string CreatePath(bool snapshot = false)
    {
        var path = Path.Combine(Root, "export-" + Guid.NewGuid().ToString("N") + (snapshot ? ".snapshot.db" : ".json.tmp"));
        if (!IsSafe(path)) throw new IOException("Unsafe export storage.");
        Directory.CreateDirectory(Root);
        if (!IsSafe(path)) throw new IOException("Unsafe export storage.");
        return path;
    }
    public void Delete(string path)
    {
        if (IsSafe(path)) File.Delete(path);
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root)) return Task.CompletedTask;
        if (!IsSafe(Path.Combine(Root, "export-" + new string('0', 32) + ".json.tmp")))
            throw new IOException("Unsafe export storage.");
        foreach (var file in Directory.EnumerateFiles(Root, "export-*"))
        { cancellationToken.ThrowIfCancellationRequested(); Delete(file); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ExportFile(ExportGenerationGate storage, string path) : IAsyncDisposable
{
    public FileStream Open()
    {
        if (!storage.IsSafe(path)) throw new IOException("Unsafe export storage.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    public ValueTask DisposeAsync() { storage.Delete(path); return ValueTask.CompletedTask; }
}
