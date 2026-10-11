using System.IO;
using System.Windows.Media.Imaging;
using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.Controls;
public sealed class IconCache
{
    private const long Budget = 16 * 1024 * 1024;
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image, long Cost)>> _entries = [];
    private readonly LinkedList<(string Key, BitmapSource Image, long Cost)> _lru = [];
    private long _bytes;
    public async Task<BitmapSource?> GetAsync(TrackerClient client, string kind, EntityItem item, int size, CancellationToken ct)
    {
        if (item.IconId is null) return null;
        string key = $"{kind}/{item.Id}/{item.IconId}/{size}";
        if (_entries.TryGetValue(key, out var found)) { _lru.Remove(found); _lru.AddFirst(found); return found.Value.Image; }
        try
        {
            var bytes = await client.IconBytesAsync(kind, item.Id, ct); ct.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = size; image.EndInit(); image.Freeze();
            long cost = (long)image.PixelWidth * image.PixelHeight * Math.Max(4, (image.Format.BitsPerPixel + 7) / 8);
            if (cost > Budget) return image;
            while (_bytes + cost > Budget && _lru.Last is { } last) { _entries.Remove(last.Value.Key); _bytes -= last.Value.Cost; _lru.RemoveLast(); }
            var node = _lru.AddFirst((key, image, cost)); _entries.Add(key, node); _bytes += cost; return image;
        }
        catch (Exception ex) when (ex is ApiException or NotSupportedException or FileFormatException or IOException) { return null; }
    }
    public void Clear() { _entries.Clear(); _lru.Clear(); _bytes = 0; }
}
