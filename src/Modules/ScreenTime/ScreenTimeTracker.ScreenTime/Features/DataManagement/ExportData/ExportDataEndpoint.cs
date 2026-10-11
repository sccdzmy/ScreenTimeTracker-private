using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace ScreenTimeTracker.ScreenTime.Features.DataManagement.ExportData;

public class ExportDataEndpoint(IMediator mediator) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("data/export");
        Group<ScreenTimeGroup>();
        AllowAnonymous(); // Authentication is enforced by the local-security middleware.
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var file = await mediator.Send(new ExportDataQuery(), ct);
        if (file is null)
        {
            HttpContext.Response.Headers.RetryAfter = "1";
            await Send.ResultAsync(Results.Problem(statusCode: 409, title: "An export is already being generated."));
            return;
        }
        await using var stream = file.Open();
        HttpContext.Response.ContentType = "application/json; charset=utf-8";
        HttpContext.Response.ContentLength = stream.Length;
        await stream.CopyToAsync(HttpContext.Response.Body, ct);
    }
}
