using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace ScreenTimeTracker.ApiClient;
public sealed class ApiException(HttpStatusCode statusCode) : Exception($"API returned HTTP {(int)statusCode}.")
{ public HttpStatusCode StatusCode { get; } = statusCode; }
public sealed class TrackerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _renewal = new(1, 1);
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
    public Func<CancellationToken, Task<string>>? RequestCredentialAsync { get; set; }
    public TrackerClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        if (!baseUri.IsLoopback || baseUri.Scheme != "http") throw new ArgumentException("Only a loopback HTTP server is supported.", nameof(baseUri));
        _http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.BaseAddress = baseUri; _http.Timeout = TimeSpan.FromSeconds(30);
    }
    public async Task AuthenticateAsync(string credential, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/api/auth/native-session", new { token = credential }, JsonOptions, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ApiException(response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<NativeSession>(JsonOptions, ct).ConfigureAwait(false) ?? throw new JsonException();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
    }
    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false) ?? throw new JsonException();
    }
    public async Task ChangeAsync(HttpMethod method, string path, object? body, CancellationToken ct = default)
    { using var response = await SendAsync(method, path, body is null ? null : JsonContent.Create(body, options: JsonOptions), ct).ConfigureAwait(false); }
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, JsonContent.Create(body, options: JsonOptions), ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false) ?? throw new JsonException();
    }
    public async Task UploadIconAsync(string kind, Guid id, Stream file, CancellationToken ct = default)
    {
        using var content = new StreamContent(file); content.Headers.ContentType = new("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Post, "/api/icons", content, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<IconUpload>(JsonOptions, ct).ConfigureAwait(false) ?? throw new JsonException();
        await ChangeAsync(HttpMethod.Patch, $"/api/screen-time/{kind}/{id}", new { iconId = result.IconId }, ct).ConfigureAwait(false);
    }
    public async Task<byte[]> IconBytesAsync(string kind, Guid id, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/screen-time/{kind}/{id}/icon", null, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
    public async Task ExportAsync(Stream destination, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/screen-time/data/export", null, ct).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }
    public async Task ImportAsync(Stream source, CancellationToken ct = default)
    {
        using var content = new StreamContent(source); content.Headers.ContentType = new("application/json");
        using var response = await SendAsync(HttpMethod.Post, "/api/screen-time/data/import", content, ct).ConfigureAwait(false);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new ArgumentException("Only local API paths are allowed.", nameof(path));
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var authorization = _http.DefaultRequestHeaders.Authorization;
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && RequestCredentialAsync is not null)
        {
            response.Dispose();
            await _renewal.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(authorization, _http.DefaultRequestHeaders.Authorization))
                    await AuthenticateAsync(await RequestCredentialAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            finally { _renewal.Release(); }
            // Only reads are safe to retry; mutations must be explicitly resubmitted by the user.
            if (method == HttpMethod.Get)
            {
                using var retry = new HttpRequestMessage(method, path);
                response = await _http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            else throw new ApiException(HttpStatusCode.Unauthorized);
        }
        if (!response.IsSuccessStatusCode) { var status = response.StatusCode; response.Dispose(); throw new ApiException(status); }
        return response;
    }
    public static string UsageQuery(DateOnly start, DateOnly end, string zone, Guid? id = null) =>
        $"startDate={start:yyyy-MM-dd}&endDate={end:yyyy-MM-dd}&timeZoneId={Uri.EscapeDataString(zone)}" + (id is null ? "" : $"&includedIds={id}");
    public void Dispose() { _http.Dispose(); _renewal.Dispose(); }
}
