using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsites;

public record GetWebsitesResponseItem(
    Guid Id,
    string Name,
    string Color,
    string Host,
    bool AllowMetadataAutoRefresh,
    DateTimeOffset MetadataLastRefreshedAt,
    Guid WebsiteCategoryId,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    bool IsSystem
);
