using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenTimeTracker.BuildingBlocks.Security;

public static partial class InputRules
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
    public static bool Host(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 259 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#')) return false;
        return Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6 && uri.Port is >= 1 and <= 65535;
    }
    public static bool Color(string? color) => color is not null && HexColor().IsMatch(color);
    public static void Json(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) Json(child); return; }
        if (element.ValueKind != JsonValueKind.Object) return;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateObject())
        {
            if (!keys.Add(item.Name)) throw new ArgumentException("Duplicate JSON property.");
            var key = item.Name.ToLowerInvariant(); var value = item.Value;
            if (key == "icon" && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid icon.");
                var extension = value.EnumerateObject().FirstOrDefault(x => x.Name.Equals("extension", StringComparison.OrdinalIgnoreCase)).Value;
                var data = value.EnumerateObject().FirstOrDefault(x => x.Name.Equals("data", StringComparison.OrdinalIgnoreCase)).Value;
                if (extension.ValueKind != JsonValueKind.String || extension.GetString()?.ToLowerInvariant() is not ("png" or ".png" or "jpg" or ".jpg" or "jpeg" or ".jpeg" or "ico" or ".ico" or "svg" or ".svg") || data.ValueKind != JsonValueKind.String || data.GetString()!.Length > ((ManagedIcons.MaxBytes + 2) / 3) * 4 || !data.TryGetBytesFromBase64(out var bytes) || bytes.Length == 0 || bytes.Length > ManagedIcons.MaxBytes) throw new ArgumentException("Invalid icon data.");
            }
            if (key is "iconpath" or "icondirectory") throw new ArgumentException("Filesystem paths are not accepted.");
            if (key == "color" && (value.ValueKind != JsonValueKind.String || !Color(value.GetString()))) throw new ArgumentException("Invalid color.");
            if (key is "name" or "processname" or "appname" or "appcategoryname" or "websitecategoryname" or "appprocessname")
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 256) throw new ArgumentException("Invalid name.");
            if (key is "host" or "websitehost" && (value.ValueKind != JsonValueKind.String || !Host(value.GetString()))) throw new ArgumentException("Invalid host.");
            if (key == "durationmilliseconds" && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var duration) || duration is < 1 or > 5000)) throw new ArgumentException("Invalid report duration.");
            if (key == "daycutoffhour" && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var hour) || hour is < 0 or > 23)) throw new ArgumentException("Invalid cutoff.");
            if (key.EndsWith("seconds", StringComparison.Ordinal) || key.EndsWith("minutes", StringComparison.Ordinal))
            {
                var minimum = key.StartsWith("minvalid", StringComparison.Ordinal) || key.Contains("tolerance", StringComparison.Ordinal) ? 0 : 1;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > 86400) throw new ArgumentException("Invalid interval.");
            }
            Json(value);
        }
    }
}
