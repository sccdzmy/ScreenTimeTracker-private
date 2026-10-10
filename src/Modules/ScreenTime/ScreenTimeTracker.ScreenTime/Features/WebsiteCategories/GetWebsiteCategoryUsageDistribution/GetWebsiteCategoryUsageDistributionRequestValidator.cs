using FastEndpoints;
using FluentValidation;

namespace ScreenTimeTracker.ScreenTime.Features.WebsiteCategories.GetWebsiteCategoryUsageDistribution;

public class GetWebsiteCategoryUsageDistributionRequestValidator
    : Validator<GetWebsiteCategoryUsageDistributionRequest>
{
    public GetWebsiteCategoryUsageDistributionRequestValidator()
    {
        RuleFor(x => x.TopN).InclusiveBetween(1, 1000);
        RuleFor(x => x).Must(x => x.StartDate > DateOnly.MinValue && x.EndDate < DateOnly.MaxValue.AddDays(-1) && x.StartDate <= x.EndDate && x.EndDate.DayNumber - x.StartDate.DayNumber < 3660).WithMessage("Date range exceeds limit.");
        RuleFor(x => x.TimeZoneId)
            .Must(BeAValidTimeZone)
            .WithMessage(x =>
                $"The Provided TimeZoneId '{x.TimeZoneId}' is invalid or not supported by the system."
            );

        RuleFor(x => x.StartDate)
            .LessThanOrEqualTo(x => x.EndDate)
            .WithMessage(x =>
                $"The StartDate '{x.StartDate}' must be before or equal to the EndDate '{x.EndDate}'."
            );
    }

    private static bool BeAValidTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
