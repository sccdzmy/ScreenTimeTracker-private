using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategory;

public record GetWebsiteCategoryResponse(
    Guid Id,
    string Name,
    string Color,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    bool IsSystem
);
