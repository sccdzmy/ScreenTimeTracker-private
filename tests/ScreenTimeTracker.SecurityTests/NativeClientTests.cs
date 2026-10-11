using System.Net;
using System.Net.Http.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using ScreenTimeTracker.ApiClient;
using Xunit;
namespace ScreenTimeTracker.SecurityTests;
public sealed class NativeClientTests
{
    [Fact]
    public async Task UnauthorizedReadRetriesButMutationNeverReplays()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Patch })
        {
            using var handler = new ExpiryHandler(); using var client = new TrackerClient(new Uri("http://127.0.0.1:5124"), handler);
            client.RequestCredentialAsync = _ => Task.FromResult("single-use");
            if (method == HttpMethod.Get) Assert.Equal(1, (await client.GetAsync<SystemInfo>("/api/system/info", TestContext.Current.CancellationToken)).ProtocolVersion);
            else await Assert.ThrowsAsync<ApiException>(() => client.ChangeAsync(method, "/api/screen-time/user-settings", new { }, TestContext.Current.CancellationToken));
            Assert.Equal(method == HttpMethod.Get ? 2 : 1, handler.BusinessRequests);
        }
    }
    [Fact]
    public async Task ConcurrentExpiredReadsShareOneCredentialRenewal()
    {
        using var handler = new ParallelExpiryHandler(); using var client = new TrackerClient(new Uri("http://127.0.0.1:5124"), handler);
        int credentials = 0;
        client.RequestCredentialAsync = async ct => { Interlocked.Increment(ref credentials); await Task.Delay(50, ct); return "single-use"; };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync<SystemInfo>("/api/system/info", TestContext.Current.CancellationToken)));
        Assert.Equal(1, credentials);
    }
    private sealed class ParallelExpiryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("native-session", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new NativeSession("renewed", 43200)) });
            return Task.FromResult(request.Headers.Authorization?.Parameter == "renewed" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SystemInfo(1, [])) } : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
    [Fact]
    public async Task IconUploadStreamsBinaryAndThenAssignsReturnedId()
    {
        using var handler = new IconHandler();
        using var client = new TrackerClient(new Uri("http://127.0.0.1:5124"), handler);
        using var stream = new MemoryStream([1, 2, 3]);
        await client.UploadIconAsync("apps", Guid.Empty, stream, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
    }
    private sealed class IconHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ++Requests;
            if (Requests == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/icons", request.RequestUri!.AbsolutePath);
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content.ReadAsByteArrayAsync(ct));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new IconUpload("synthetic-icon")) };
            }
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/api/screen-time/apps/00000000-0000-0000-0000-000000000000", request.RequestUri!.AbsolutePath);
            Assert.Contains("synthetic-icon", await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.NoContent);
        }
    }
    [Fact]
    public void NativeProtocolChecksVersionAndRequiredCapabilities()
    {
        string[] capabilities = ["native-session", "paged-management", "paged-timeline", "icons", "extension-pairing", "import-export", "future-additive-feature"];
        new SystemInfo(1, capabilities).EnsureNativeCompatible();
        Assert.Throws<InvalidOperationException>(() => new SystemInfo(2, capabilities).EnsureNativeCompatible());
        Assert.Throws<InvalidOperationException>(() => new SystemInfo(1, []).EnsureNativeCompatible());
    }
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://127.0.0.1")]
    public void ClientRejectsRemoteServer(string url) => Assert.Throws<ArgumentException>(() => new TrackerClient(new Uri(url)));
    [Fact]
    public void NativeUiAndClientHaveNoBusinessOrDatabaseProjectReferences()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ScreenTimeTracker.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (string project in new[] { "src/Clients/ScreenTimeTracker.ApiClient/ScreenTimeTracker.ApiClient.csproj", "src/Hosts/ScreenTimeTracker.Native/ScreenTimeTracker.Native.csproj" })
        {
            var xml = XDocument.Load(Path.Combine(root.FullName, project));
            foreach (var reference in xml.Descendants("ProjectReference")) Assert.Contains("ScreenTimeTracker.ApiClient", reference.Attribute("Include")!.Value);
            Assert.DoesNotContain(xml.Descendants("PackageReference"), x => x.Attribute("Include")!.Value.Contains("EntityFramework", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(typeof(TrackerClient).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("ScreenTimeTracker.ScreenTime", StringComparison.Ordinal) || x.Name.Contains("PresentationFramework", StringComparison.Ordinal));
    }
    private sealed class ExpiryHandler : HttpMessageHandler
    {
        public int BusinessRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("native-session", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new NativeSession("session", 43200)) });
            BusinessRequests++;
            return Task.FromResult(BusinessRequests == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SystemInfo(1, [])) });
        }
    }
}
