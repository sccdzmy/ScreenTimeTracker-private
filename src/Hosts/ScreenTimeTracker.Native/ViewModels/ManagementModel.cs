using ScreenTimeTracker.ApiClient;
namespace ScreenTimeTracker.Native.ViewModels;
public sealed class ManagementModel(TrackerClient client, string kind) : PageModel(client)
{
    public string Kind { get; set; } = kind;
    public string Search { get; set; } = "";
    public List<EntityItem> Items { get; private set; } = [];
    public List<EntityItem> Categories { get; private set; } = [];
    public EntityItem? Selected { get; private set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4080D0";
    public Guid? CategoryId { get; set; }
    public bool AllowMetadataAutoRefresh { get; set; }
    private int? _next;
    public bool IsCategories => Kind.EndsWith("categories", StringComparison.Ordinal);
    public void Select(EntityItem? item)
    {
        Selected = item; Name = item?.Name ?? ""; Color = item?.Color ?? "#4080D0"; CategoryId = item?.CategoryId; AllowMetadataAutoRefresh = item?.AllowMetadataAutoRefresh ?? false;
        Changed(nameof(Name)); Changed(nameof(Color)); Changed(nameof(CategoryId)); Changed(nameof(AllowMetadataAutoRefresh)); Changed(nameof(Selected));
    }
    public override Task RefreshAsync() => LoadAsync(false);
    public Task MoreAsync() => _next is null ? Task.CompletedTask : LoadAsync(true);
    private Task LoadAsync(bool append) => RunAsync(async ct =>
    {
        List<EntityItem> items; int? next = null;
        if (IsCategories)
        { items = await Client.GetAsync<List<EntityItem>>($"/api/screen-time/{Kind}", ct); if (Search.Length > 0) items = items.Where(x => x.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)).ToList(); }
        else
        {
            var page = await Client.GetAsync<PageResult<EntityItem>>($"/api/screen-time/{Kind}/page?offset={(append ? _next : 0)}&search={Uri.EscapeDataString(Search)}", ct);
            items = page.Items; next = page.NextOffset;
        }
        var categories = IsCategories ? new List<EntityItem>() : await Client.GetAsync<List<EntityItem>>($"/api/screen-time/{(Kind == "apps" ? "app-categories" : "website-categories")}", ct);
        ct.ThrowIfCancellationRequested(); Items = append ? [.. Items, .. items] : items; _next = next; Categories = categories;
        Changed(nameof(Items)); Changed(nameof(Categories)); Select(null);
    });
    public Task SaveAsync() => RunAsync(async ct =>
    {
        if (Selected is null) throw new InvalidOperationException("Select an object first.");
        if (IsCategories) await Client.ChangeAsync(HttpMethod.Patch, $"/api/screen-time/{Kind}/{Selected.Id}", new { Name, Color }, ct);
        else
        {
            var body = new Dictionary<string, object?> { ["name"] = Name, ["color"] = Color, ["allowMetadataAutoRefresh"] = AllowMetadataAutoRefresh };
            if (CategoryId is { } id) body[Kind == "apps" ? "appCategoryId" : "websiteCategoryId"] = id;
            await Client.ChangeAsync(HttpMethod.Patch, $"/api/screen-time/{Kind}/{Selected.Id}", body, ct);
        }
    }, false);
    public Task CreateAsync() => RunAsync(async ct => { if (IsCategories) await Client.ChangeAsync(HttpMethod.Post, $"/api/screen-time/{Kind}", new { Name, Color, iconId = (string?)null }, ct); }, false);
    public Task DeleteAsync() => RunAsync(async ct => { if (Selected is { IsSystem: false } item) await Client.ChangeAsync(HttpMethod.Delete, $"/api/screen-time/{Kind}/{item.Id}", null, ct); }, false);
    public Task IconAsync(Stream stream) => RunAsync(async ct => { if (Selected is { } item) await Client.UploadIconAsync(Kind, item.Id, stream, ct); }, false);
    public Task RemoveIconAsync() => RunAsync(async ct => { if (Selected is { } item) await Client.ChangeAsync(HttpMethod.Patch, $"/api/screen-time/{Kind}/{item.Id}", new { iconId = (string?)null }, ct); }, false);
    public override void Dispose() { base.Dispose(); Items.Clear(); Categories.Clear(); }
}
