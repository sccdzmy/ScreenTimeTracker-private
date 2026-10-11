using System.Text.Json.Serialization;
namespace ScreenTimeTracker.ScreenTime.Features.AppCategories.CreateAppCategory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record CreateAppCategoryRequest(string Name, string Color, string? IconId);
