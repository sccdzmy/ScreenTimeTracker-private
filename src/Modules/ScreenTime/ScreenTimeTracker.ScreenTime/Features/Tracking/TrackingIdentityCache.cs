namespace ScreenTimeTracker.ScreenTime.Features.Tracking;

public sealed record AppIdentity(Guid Id, string? ExecutablePath, bool AllowRefresh, DateTimeOffset RefreshedAt);

public sealed class TrackingIdentityCache
{
    private readonly object _gate = new();
    private readonly Lru<AppIdentity> _apps = new();
    private readonly Lru<Guid> _websites = new();
    private long _appVersion, _websiteVersion;
    public long AppVersion { get { lock (_gate) return _appVersion; } }
    public long WebsiteVersion { get { lock (_gate) return _websiteVersion; } }
    public bool TryApp(string key, out AppIdentity identity) { lock (_gate) return _apps.TryGet(key, out identity!); }
    public bool TryWebsite(string key, out Guid id) { lock (_gate) return _websites.TryGet(key, out id); }
    public void PutApp(string key, AppIdentity value, long version) { lock (_gate) { if (version == _appVersion) _apps.Put(key, value); } }
    public void PutWebsite(string key, Guid value, long version) { lock (_gate) { if (version == _websiteVersion) _websites.Put(key, value); } }
    public void InvalidateApps() { lock (_gate) { _appVersion++; _apps.Clear(); } }
    public void InvalidateWebsites() { lock (_gate) { _websiteVersion++; _websites.Clear(); } }
    public void InvalidateAll() { InvalidateApps(); InvalidateWebsites(); }

    private sealed class Lru<T>
    {
        private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, T Value)> _order = new();
        public bool TryGet(string key, out T value)
        {
            if (!_entries.TryGetValue(key, out var node)) { value = default!; return false; }
            _order.Remove(node); _order.AddFirst(node); value = node.Value.Value; return true;
        }
        public void Put(string key, T value)
        {
            if (_entries.Remove(key, out var existing)) _order.Remove(existing);
            var node = _order.AddFirst((key, value)); _entries.Add(key, node);
            if (_entries.Count <= 512) return;
            var last = _order.Last!; _entries.Remove(last.Value.Key); _order.RemoveLast();
        }
        public void Clear() { _entries.Clear(); _order.Clear(); }
    }
}
