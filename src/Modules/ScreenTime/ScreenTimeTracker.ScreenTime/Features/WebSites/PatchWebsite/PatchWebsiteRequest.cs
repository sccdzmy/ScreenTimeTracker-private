using System.Text.Json.Serialization;
using FastEndpoints;
using ScreenTimeTracker.BuildingBlocks.Types;

namespace ScreenTimeTracker.ScreenTime.Features.Websites.PatchWebsite;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record PatchWebsiteRequest(
    [property: RouteParam] Guid Id,
    OptionalValue<string> Name = default,
    OptionalValue<string> Color = default,
    OptionalValue<bool> AllowMetadataAutoRefresh = default,
    OptionalValue<Guid> WebsiteCategoryId = default,
    OptionalValue<string?> IconId = default
);
