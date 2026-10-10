using FastEndpoints;
using FluentValidation;
using ScreenTimeTracker.BuildingBlocks.Security;

namespace ScreenTimeTracker.ScreenTime.Features.Tracking.TrackWebsiteUsage.RecordActivity;

public sealed class RecordActivityRequestValidator : Validator<RecordActivityRequest>
{
    public RecordActivityRequestValidator()
    {
        RuleFor(x => x.Host).Must(InputRules.Host);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
        RuleFor(x => x.DurationMilliseconds).InclusiveBetween(1, 5000);
    }
}
