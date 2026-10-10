using System.Text.Json.Serialization;
using FastEndpoints;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.PatchWebsiteCategory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record PatchWebsiteCategoryRequest(
    [property: RouteParam] Guid WebsiteCategoryId,
    OptionalValue<string> Name,
    OptionalValue<string> Color,
    OptionalValue<string?> IconId
);
