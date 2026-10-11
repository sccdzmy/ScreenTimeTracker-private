using System.Text.Json.Serialization;
using FastEndpoints;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.AppCategories.PatchAppCategory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record PatchAppCategoryRequest(
    [property: RouteParam] Guid AppCategoryId,
    OptionalValue<string> Name = default,
    OptionalValue<string> Color = default,
    OptionalValue<string?> IconId = default
);
