using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ScreenTimeTracker.BuildingBlocks.Security;
using ScreenTimeTracker.ScreenTime.Infrastructure.Persistence;

namespace ScreenTimeTracker.Desktop.Security;

public static class SecurityEndpoints
{
    public static void MapLocalSecurity(this WebApplication app)
    {
        app.MapPost("/api/auth/session", (BootstrapRequest request, HttpContext context, LocalAccessService access) =>
        {
            var session = access.RedeemBootstrap(request.Token);
            if (session is null) return Results.Unauthorized();
            context.Response.Cookies.Append("stt-session", session.Value.Token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/", MaxAge = TimeSpan.FromHours(12) });
            return Results.Ok(new { csrf = session.Value.Csrf });
        });
        app.MapGet("/api/auth/csrf", (HttpContext context, LocalAccessService access) => Results.Ok(new { csrf = access.SessionCsrf(context.Request.Cookies["stt-session"]) }));
        app.MapPost("/api/auth/pairing-code", (LocalAccessService access) => Results.Ok(new { code = access.CreatePairing(), expiresInSeconds = 300 }));
        app.MapPost("/api/auth/pair", (PairRequest request, LocalAccessService access) => access.RedeemPairing(request.Code) is { } token ? Results.Ok(new { token }) : Results.Unauthorized());
        app.MapDelete("/api/auth/extensions", (LocalAccessService access) => { access.RevokeExtensions(); return Results.NoContent(); });
        app.MapPost("/api/icons", async (HttpContext context, CancellationToken ct) =>
        {
            if (context.Request.ContentLength > ManagedIcons.MaxBytes) return Results.StatusCode(413);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int length;
            while ((length = await context.Request.Body.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + length > ManagedIcons.MaxBytes) return Results.StatusCode(413);
                output.Write(buffer, 0, length);
            }
            var path = await ManagedIcons.SaveAsync(output.ToArray(), ct);
            return Results.Ok(new { iconId = ManagedIcons.Id(path) });
        });
        app.MapDelete("/api/screen-time/{kind}/{id:guid}/icon", async (string kind, Guid id, ScreenTimeDbContext database, CancellationToken ct) =>
        {
            object? entity = kind switch { "apps" => await database.Apps.FindAsync([id], ct), "websites" => await database.Websites.FindAsync([id], ct), "app-categories" => await database.AppCategories.FindAsync([id], ct), "website-categories" => await database.WebsiteCategories.FindAsync([id], ct), _ => null };
            if (entity is null) return Results.NotFound();
            database.Entry(entity).Property("IconPath").CurrentValue = null;
            database.Entry(entity).Property("IconPathLastUpdatedAt").CurrentValue = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(ct);
            // Content-addressed files can be shared; deleting a reference must not break others.
            return Results.NoContent();
        });
    }
    public sealed record BootstrapRequest(string Token);
    public sealed record PairRequest(string Code);
}
