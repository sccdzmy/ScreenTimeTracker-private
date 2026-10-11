using System.Text.Json.Serialization;
using ScreenTimeTracker.BuildingBlocks.Security;
namespace ScreenTimeTracker.ScreenTime.Features.AppCategories.GetAppCategoryUsageDistribution;

public record GetAppCategoryUsageDistributionResponse(
    List<AppCategoryUsageDistributionItem> Items,
    int TotalCount,
    long TotalDurationSeconds,
    int OthersCount,
    long OthersDurationSeconds
);

public record AppCategoryUsageDistributionItem(
    Guid Id,
    string Name,
    string Color,
    [property: JsonPropertyName("iconId"), JsonConverter(typeof(IconReferenceConverter))] string? IconPath,
    DateTimeOffset IconPathLastUpdatedAt,
    long DurationSeconds
);
