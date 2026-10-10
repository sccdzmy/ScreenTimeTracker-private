using System.Text.Json.Serialization;
namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.CreateWebsiteCategory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record CreateWebsiteCategoryRequest(string Name, string Color, string? IconId);
