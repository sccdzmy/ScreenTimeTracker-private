using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.Websites.GetWebsiteUsageDistribution;

public record GetWebsiteUsageDistributionResponse(
    List<WebsiteUsageDistributionItem> Items,
    int TotalCount,
    long TotalDurationSeconds,
    int OthersCount,
    long OthersDurationSeconds
);

public record WebsiteUsageDistributionItem(
    Guid Id,
    string Name,
    string Color,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    long DurationSeconds
);
