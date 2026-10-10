using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using ScreenTimeTracker.BuildingBlocks.Security;

namespace ScreenTimeTracker.Desktop.Security;

public sealed class LocalSecurityMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context, LocalAccessService access)
    {
        var host = context.Request.Host.Host;
        if (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) || host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        var path = context.Request.Path.Value!;
        var origin = context.Request.Headers.Origin.ToString();
        var ownOrigin = context.Request.Scheme + "://" + context.Request.Host;
        bool sameOrigin = origin == ownOrigin;
        bool developmentOrigin = environment.IsDevelopment() && origin == "http://127.0.0.1:5173";
        if (context.Request.Method == "OPTIONS" && developmentOrigin)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PATCH, PUT, DELETE";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-STT-CSRF";
            context.Response.StatusCode = 204; return;
        }
        if (path is "/api/auth/session" or "/api/auth/pair")
        {
            if (context.Request.Method != "POST" || !access.Allow(path, 5, TimeSpan.FromMinutes(1)) || context.Request.ContentLength > 4096)
            { context.Response.StatusCode = 429; return; }
            if (path == "/api/auth/session" && !sameOrigin && !developmentOrigin) { context.Response.StatusCode = 403; return; }
        }
        else
        {
            string? csrf = access.SessionCsrf(context.Request.Cookies["stt-session"]);
            var bearer = context.Request.Headers.Authorization.ToString();
            bool extension = bearer.StartsWith("Bearer ", StringComparison.Ordinal) && access.IsExtension(bearer[7..]);
            if (extension)
            {
                bool allowed = path == "/api/screen-time/tracking/track-website-usage/record-activity" && context.Request.Method == "POST"
                    || path.StartsWith("/api/screen-time/websites/by-host/", StringComparison.Ordinal) && context.Request.Method == "GET"
                    || path.StartsWith("/api/screen-time/websites/", StringComparison.Ordinal) && path.EndsWith("/refresh-metadata", StringComparison.Ordinal) && context.Request.Method == "POST";
                if (!allowed) { context.Response.StatusCode = 403; return; }
                if (!access.Allow(bearer[7..], 10, TimeSpan.FromSeconds(1))) { context.Response.StatusCode = 429; return; }
                context.Items["ExtensionIdentity"] = bearer[7..];
            }
            else
            {
                if (csrf is null) { context.Response.StatusCode = 401; return; }
                if (origin.Length != 0 && !sameOrigin && !developmentOrigin) { context.Response.StatusCode = 403; return; }
                if (context.Request.Method is not ("GET" or "HEAD") && ((!sameOrigin && !developmentOrigin) || context.Request.Headers["X-STT-CSRF"] != csrf))
                { context.Response.StatusCode = 403; return; }
            }
        }
        try
        {
            if (context.Request.Method is "POST" or "PATCH" or "PUT" && path != "/api/icons")
            {
                if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                { context.Response.StatusCode = 415; return; }
                var limit = path is "/api/auth/session" or "/api/auth/pair" ? 4096 : path.Contains("import", StringComparison.OrdinalIgnoreCase) ? 64 * 1024 * 1024 : 4 * 1024 * 1024;
                if (context.Request.ContentLength > limit) { context.Response.StatusCode = 413; return; }
                context.Request.EnableBuffering(8192, limit);
                using var bounded = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
                {
                    if (bounded.Length + count > limit) { context.Response.StatusCode = 413; return; }
                    bounded.Write(buffer, 0, count);
                }
                context.Request.Body.Position = 0;
                using var document = JsonDocument.Parse(bounded.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Object) { context.Response.StatusCode = 400; return; }
                InputRules.Json(document.RootElement);
            }
            await next(context);
        }
        catch (IOException) when (!context.Response.HasStarted)
        { context.Response.StatusCode = 413; }
        catch (ArgumentException) when (!context.Response.HasStarted)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request." }); }
        catch (JsonException) when (!context.Response.HasStarted)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid JSON." }); }
    }
}
