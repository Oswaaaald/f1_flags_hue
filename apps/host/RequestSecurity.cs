using System.Net;
using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Host;

public static class RequestSecurity
{
    public static void Configure(WebApplication app, Auth auth, CommandCoordinator commands, bool desktopMode, int port)
    {
        var configuredHosts = (Environment.GetEnvironmentVariable("F1_HUE_ALLOWED_HOSTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        app.Use(async (ctx, next) =>
        {
            async Task Reject(int status, string message, string? code = null)
            {
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(new ApiError(code ?? "http_" + status, message, ctx.TraceIdentifier));
            }
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (ctx.Request.Path.StartsWithSegments("/api"))
                ctx.Response.Headers.CacheControl = "no-store";
            else if (!Path.HasExtension(ctx.Request.Path) || ctx.Request.Path.Value?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true)
                ctx.Response.Headers.CacheControl = "no-cache";
            var host = ctx.Request.Host.Host;
            if (desktopMode && (host != "127.0.0.1" || ctx.Request.Host.Port != port || ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)))
            {
                await Reject(403, "Adresse de connexion non autorisée en mode bureau.");
                return;
            }
            if (!(host == "localhost" || IPAddress.TryParse(host, out _) || configuredHosts.Contains(host, StringComparer.OrdinalIgnoreCase)))
            {
                await Reject(403, "Nom d’hôte non autorisé.");
                return;
            }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site" || origin.Length > 0 && origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}")
            {
                await Reject(403, "Origine de la requête non autorisée.");
                return;
            }
            var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
            if (unsafeMethod && ctx.Request.Headers["X-F1Hue-Request"] != "1")
            {
                await Reject(403, "En-tête de modification requis.");
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth") && !auth.Valid(ctx.Request.Cookies["f1hue_session"]))
            {
                await Reject(401, "Connecte-toi pour accéder à F1 Hue Sync.");
                return;
            }
            // Capture cancellation before binding a potentially slow body, without taking
            // the control lock. A request received before Stop must not start afterwards.
            using var operation = unsafeMethod && !ctx.Request.Path.StartsWithSegments("/api/auth") && ctx.Request.Path != "/api/stop"
                ? commands.Request(ctx.RequestAborted) : null;
            if (operation is not null)
                ctx.Items["command-token"] = operation.Token;
            try
            {
                if (unsafeMethod && ctx.Request.Path is var authPath && (authPath == "/api/auth/login" || authPath == "/api/auth/setup"))
                    auth.Limit(ctx.Connection.RemoteIpAddress?.ToString() ?? "local");
                await next(ctx);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (OperationCanceledException) when (operation?.IsCancellationRequested == true && !ctx.Response.HasStarted)
            {
                await Reject(409, "Opération annulée par Stop.", "operation_cancelled");
            }
            catch (Exception e) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = e switch
                {
                    RateLimitException => 429,
                    UnauthorizedAccessException => 401,
                    BadHttpRequestException bad => bad.StatusCode,
                    ArgumentException or JsonException => 400,
                    InvalidOperationException => 409,
                    HttpRequestException or OperationCanceledException => 502,
                    _ => 500
                };
                if (e is RateLimitException)
                    ctx.Response.Headers.RetryAfter = "60";
                var message = ctx.Response.StatusCode == 500 ? "Une erreur interne est survenue. Consulte les journaux du service."
                    : e is BadHttpRequestException ? ctx.Response.StatusCode switch
                    {
                        413 => "Requête trop volumineuse (64 Kio maximum).",
                        415 => "Un corps JSON avec Content-Type application/json est requis.",
                        _ => "Requête JSON invalide : vérifie les champs requis et leur type."
                    } : e.Message;
                await Reject(ctx.Response.StatusCode, message, e is SettingsConflictException ? "settings_conflict" : null);
                if (ctx.Response.StatusCode == 500)
                    app.Logger.LogError("Erreur interne {TraceId} : {Type}\n{Stack}", ctx.TraceIdentifier, e.GetType().Name, e.StackTrace);
            }
        });
    }
}
