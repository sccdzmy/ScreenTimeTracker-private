using System.Text.Json.Serialization;
using FastEndpoints;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.Apps.PatchApp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record PatchAppRequest(
    [property: RouteParam] Guid AppId,
    OptionalValue<string> Name = default,
    OptionalValue<string> Color = default,
    OptionalValue<bool> AllowMetadataAutoRefresh = default,
    OptionalValue<Guid> AppCategoryId = default,
    OptionalValue<string?> IconId = default
);
