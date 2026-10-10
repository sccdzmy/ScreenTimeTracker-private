using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteByHost;

public record GetWebsiteByHostResponse(
    Guid Id,
    string Name,
    string Color,
    string Host,
    bool AllowMetadataAutoRefresh,
    DateTimeOffset MetadataLastRefreshedAt,
    Guid CategoryId,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    bool IsSystem
);
