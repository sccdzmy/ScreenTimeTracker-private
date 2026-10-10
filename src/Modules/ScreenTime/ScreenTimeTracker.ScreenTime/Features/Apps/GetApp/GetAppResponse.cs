using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.Apps.GetApp;

public record GetAppResponse(
    Guid Id,
    string Name,
    string Color,
    string ProcessName,
    bool AllowMetadataAutoRefresh,
    DateTimeOffset MetadataLastRefreshedAt,
    Guid CategoryId,
    string? ExecutablePath,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    bool IsSystem
);
