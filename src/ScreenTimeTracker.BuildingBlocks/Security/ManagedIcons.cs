using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScreenTimeTracker.BuildingBlocks.Types;
using SkiaSharp;

namespace ScreenTimeTracker.BuildingBlocks.Security;

public static partial class ManagedIcons
{
    private static readonly AsyncLocal<List<string>?> Writes = new();
    public static IDisposable TrackWrites(List<string> paths)
    {
        var previous = Writes.Value; Writes.Value = paths; return new WriteScope(previous);
    }
    private sealed class WriteScope(List<string>? previous) : IDisposable { public void Dispose() => Writes.Value = previous; }
    public static string? TrustedBuiltIn(byte[] data)
    {
        if (data.Length > MaxBytes) return null;
        foreach (var name in BuiltInNames)
        {
            var path = Path.Combine(BuiltIns, name);
            if (IsSafePath(path) && File.Exists(path) && new FileInfo(path).Length <= MaxBytes && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return path;
        }
        return null;
    }
    public const int MaxBytes = 2 * 1024 * 1024;
    public static string Root => Path.GetFullPath("./Data/ManagedIcons");
    private static string BuiltIns => Path.GetFullPath("./PrePreparedCategoryIcons");
    [GeneratedRegex("^[a-f0-9]{64}\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedName();
    private static readonly HashSet<string> BuiltInNames = ["game.svg", "relax.svg", "social.svg", "study.svg", "video.svg", "work.svg"];

    private static readonly string[] LegacyRoots = ["./Data/AppIcons", "./Data/WebsiteIcons", "./Data/AppCategoryIcons", "./Data/WebsiteCategoryIcons"];

    public static bool IsSafePath(string? path, bool legacy = false)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var roots = new List<string> { Root, BuiltIns };
            if (legacy) roots.AddRange(LegacyRoots.Select(Path.GetFullPath));
            var directory = Path.GetDirectoryName(full);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (!roots.Contains(directory, comparer)) return false;
            if (!legacy && comparer.Equals(directory, Root) && !ManagedName().IsMatch(Path.GetFileName(full))) return false;
            if (comparer.Equals(directory, BuiltIns) && !BuiltInNames.Contains(Path.GetFileName(full))) return false;
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static string? Id(string? path) => IsSafePath(path) ? Path.GetFileName(path) : null;
    public static string? Resolve(string? id)
    {
        if (id is null) return null;
        if (!ManagedName().IsMatch(id) && !BuiltInNames.Contains(id)) throw new ArgumentException("Invalid icon identifier.");
        var path = Path.Combine(BuiltInNames.Contains(id) ? BuiltIns : Root, id);
        if (!IsSafePath(path) || !File.Exists(path)) throw new ArgumentException("Icon not found.");
        return path;
    }
    public static OptionalValue<string?> Resolve(OptionalValue<string?> id) => id.HasValue ? new(Resolve(id.Value)) : default;

    public static async Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!IsSafePath(path)) throw new ArgumentException("Invalid icon reference.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsSafePath(path) || stream.Length > MaxBytes) throw new ArgumentException("Invalid icon reference.");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    public static byte[] Normalize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > MaxBytes) throw new ArgumentException("Icon exceeds size limit.");
        try
        {
            // ICO is a container. Decode each embedded PNG or uncompressed DIB frame;
            // never trust the extension supplied by a client.
            bool ico = data.Length >= 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;
            if (ico) data = ExtractIco(data);
            using var image = Decode(data, ico);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded.Size > MaxBytes) throw new ArgumentException("Normalized icon exceeds size limit.");
            return encoded.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or OverflowException) { throw new ArgumentException("Invalid image content.", ex); }
    }

    private static SKBitmap Decode(byte[] data, bool ico)
    {
        using var buffer = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(buffer) ?? throw new ArgumentException("Invalid image content.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg) && !(ico && codec.EncodedFormat == SKEncodedImageFormat.Bmp)) throw new ArgumentException("Unsupported icon format.");
        var info = codec.Info;
        if (info.Width is < 1 or > 4096 || info.Height is < 1 or > 4096) throw new ArgumentException("Icon dimensions exceed limit.");
        var image = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(image.Info, image.GetPixels()) != SKCodecResult.Success) { image.Dispose(); throw new ArgumentException("Invalid image content."); }
        return image;
    }

    private static byte[] ExtractIco(byte[] data)
    {
        var count = BitConverter.ToUInt16(data, 4);
        if (count is < 1 or > 256 || data.Length < 6 + count * 16) throw new ArgumentException("Invalid ICO.");
        byte[]? selected = null;
        long decodedPixels = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var size = BitConverter.ToUInt32(data, entry + 8);
            var offset = BitConverter.ToUInt32(data, entry + 12);
            if (offset < 6 + count * 16 || size < 8 || (long)offset + size > data.Length) throw new ArgumentException("Invalid ICO.");
            var frame = data.AsSpan((int)offset, (int)size).ToArray();
            if (frame[0] != 137 || frame[1] != 80 || frame[2] != 78 || frame[3] != 71)
            {
                if (frame.Length < 40 || BitConverter.ToUInt32(frame, 0) != 40) throw new ArgumentException("Unsupported ICO frame.");
                int width = BitConverter.ToInt32(frame, 4), doubleHeight = BitConverter.ToInt32(frame, 8);
                int bits = BitConverter.ToUInt16(frame, 14);
                if (width is < 1 or > 4096 || doubleHeight is < 2 or > 8192 || doubleHeight % 2 != 0 || bits is not (1 or 4 or 8 or 16 or 24 or 32) || BitConverter.ToUInt32(frame, 16) != 0) throw new ArgumentException("Unsupported ICO frame.");
                int height = doubleHeight / 2;
                int bytes = checked(((width * bits + 31) / 32 * 4) * height);
                uint usedColors = BitConverter.ToUInt32(frame, 32);
                int paletteEntries = bits <= 8 ? (usedColors == 0 ? 1 << bits : checked((int)usedColors)) : 0;
                if (bits <= 8 && paletteEntries > 1 << bits || bits > 8 && usedColors != 0) throw new ArgumentException("Invalid ICO palette.");
                int imageOffset = checked(40 + paletteEntries * 4);
                if (frame.Length < imageOffset + bytes) throw new ArgumentException("Truncated ICO frame.");
                var bmp = new byte[14 + imageOffset + bytes]; bmp[0] = 66; bmp[1] = 77;
                BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2); BitConverter.GetBytes(14 + imageOffset).CopyTo(bmp, 10);
                frame.AsSpan(0, imageOffset + bytes).CopyTo(bmp.AsSpan(14));
                BitConverter.GetBytes(height).CopyTo(bmp, 22); BitConverter.GetBytes(bytes).CopyTo(bmp, 34);
                frame = bmp;
            }
            // Validate all frames, including frames not selected for display.
            using (var bytes = SKData.CreateCopy(frame))
            using (var codec = SKCodec.Create(bytes) ?? throw new ArgumentException("Invalid ICO frame."))
            {
                decodedPixels += (long)codec.Info.Width * codec.Info.Height;
                if (decodedPixels > 4096L * 4096) throw new ArgumentException("ICO frames exceed pixel budget.");
            }
            using var decoded = Decode(frame, true);
            selected ??= frame;
        }
        return selected!;
    }

    public static async Task<string> SaveAsync(byte[] data, CancellationToken ct = default)
    {
        var png = Normalize(data);
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Writes.Value is null ? png : png.Concat(RandomNumberGenerator.GetBytes(32)).ToArray())) + ".png");
        if (!IsSafePath(path)) throw new ArgumentException("Unsafe icon directory.");
        if (File.Exists(path)) return path;
        var temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await file.WriteAsync(png, ct);
            if (!IsSafePath(path)) throw new ArgumentException("Unsafe icon directory.");
            try { File.Move(temporary, path, false); Writes.Value?.Add(path); } catch (IOException) when (File.Exists(path)) { }
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public class IconReferenceConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(ManagedIcons.Id(value));
}
