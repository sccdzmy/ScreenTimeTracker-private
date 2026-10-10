using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenTimeTracker.Desktop.Security;

public sealed class LocalAccessService(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _bootstraps = [];
    private readonly Dictionary<string, (DateTimeOffset Expires, string Csrf)> _sessions = [];
    private readonly Dictionary<string, DateTimeOffset> _pairings = [];
    private readonly HashSet<string> _extensions = [];
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _rates = [];
    private bool _loaded;
    private static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    private static string Store => Path.GetFullPath("./Data/Security/extension-hashes.json");

    public string CreateBootstrap()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var key in _bootstraps.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _bootstraps.Remove(key);
            if (_bootstraps.Count >= 64) _bootstraps.Remove(_bootstraps.Keys.First());
            var token = Token(); _bootstraps[Hash(token)] = now.AddSeconds(60); return token;
        }
    }
    public (string Token, string Csrf)? RedeemBootstrap(string? token)
    {
        lock (_gate)
        {
            if (token is not { Length: 64 } || !_bootstraps.Remove(Hash(token), out var expires) || expires <= timeProvider.GetUtcNow()) return null;
            foreach (var key in _sessions.Where(x => x.Value.Expires <= timeProvider.GetUtcNow()).Select(x => x.Key).ToArray()) _sessions.Remove(key);
            if (_sessions.Count >= 128) return null;
            var session = Token(); var csrf = Token();
            _sessions[Hash(session)] = (timeProvider.GetUtcNow().AddHours(12), csrf);
            return (session, csrf);
        }
    }
    public string? SessionCsrf(string? token)
    {
        lock (_gate)
            return token is { Length: 64 } && _sessions.TryGetValue(Hash(token), out var value) && value.Expires > timeProvider.GetUtcNow() ? value.Csrf : null;
    }
    public string CreatePairing()
    {
        lock (_gate)
        {
            foreach (var key in _pairings.Where(x => x.Value <= timeProvider.GetUtcNow()).Select(x => x.Key).ToArray()) _pairings.Remove(key);
            if (_pairings.Count >= 32) _pairings.Clear();
            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            _pairings[Hash(code)] = timeProvider.GetUtcNow().AddMinutes(5); return code;
        }
    }
    public string? RedeemPairing(string? code)
    {
        lock (_gate)
        {
            Load();
            if (code is not { Length: 16 } || !_pairings.Remove(Hash(code), out var expires) || expires <= timeProvider.GetUtcNow() || _extensions.Count >= 32) return null;
            var token = Token(); _extensions.Add(Hash(token)); Save(); return token;
        }
    }
    public bool IsExtension(string token)
    {
        lock (_gate) { Load(); return token.Length == 64 && _extensions.Contains(Hash(token)); }
    }
    public void RevokeExtensions()
    {
        lock (_gate) { Load(); _extensions.Clear(); _pairings.Clear(); Save(); }
    }
    public bool Allow(string key, int limit, TimeSpan window)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var expired in _rates.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray()) _rates.Remove(expired);
            if (!_rates.TryGetValue(key, out var value) || now - value.Start >= window) value = (now, 0);
            value.Count++; _rates[key] = value;
            return value.Count <= limit;
        }
    }
    private void Load()
    {
        if (_loaded) return;
        CheckStore();
        if (File.Exists(Store))
        {
            if (new FileInfo(Store).Length > 8192) throw new InvalidOperationException("Invalid security store.");
            foreach (var hash in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Store)) ?? [])
                if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) _extensions.Add(hash);
        }
        _loaded = true;
    }
    private static void CheckStore()
    {
        for (var path = Store; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe security store.");
    }
    private void Save()
    {
        CheckStore();
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(Store)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Store + ".tmp";
        if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe security store.");
        File.WriteAllText(temporary, JsonSerializer.Serialize(_extensions));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, Store, true);
    }
}
