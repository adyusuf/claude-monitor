using System.Threading.RateLimiting;
using ClaudeMonitor.Api.Background;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Ingest;
using ClaudeMonitor.Api.Mail;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace ClaudeMonitor.Api;

public static class Startup
{
    public static IServiceCollection AddMonitorServices(this IServiceCollection services, ApiConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Broker>();
        services.AddDbContext<MonitorDb>(o => o.UseNpgsql(config.DatabaseUrl));
        services.AddSingleton<IMailer, SmtpMailer>();
        services.AddScoped<BatchIngestor>();
        if (config.BackgroundJobs)
        {
            services.AddHostedService<Housekeeper>();
            services.AddHostedService<Archiver>();
        }

        services.AddAuthentication(Schemes.Session)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthHandler>(Schemes.Session, null)
            .AddScheme<AuthenticationSchemeOptions, AgentAuthHandler>(Schemes.Agent, null)
            .AddExternalProviders(config);
        services.AddAuthorizationBuilder()
            .AddPolicy(Schemes.Session, p => p.AddAuthenticationSchemes(Schemes.Session).RequireAuthenticatedUser())
            .AddPolicy(Schemes.Agent, p => p.AddAuthenticationSchemes(Schemes.Agent).RequireAuthenticatedUser());
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = config.AuthRequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
        services.AddProblemDetails();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Forwarded headers count only when they come from these networks (Cloudflare's, on the servers).
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var network in config.ProxyNetworks) o.KnownIPNetworks.Add(network);
        });
        return services;
    }

    public static void UseMonitorPipeline(this WebApplication app, ApiConfig config)
    {
        if (config.TrustProxy) app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.Use(SecurityHeaders);
        app.Use((http, next) => Csrf(http, next, config));
        var files = config.WebRoot is { Length: > 0 } root && Directory.Exists(root)
            ? new PhysicalFileProvider(Path.GetFullPath(root))
            : null;
        if (files is not null)
        {
            // Before routing: once the SPA fallback below has matched a path, the static-file middleware stands
            // aside, and the built scripts would be answered with index.html.
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files, OnPrepareResponse = NoCacheForPages });
        }

        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/api");
        api.MapGet("/version", () => Results.Ok(new { commit = config.Commit }));
        AuthEndpoints.Map(api);
        ExternalAuthEndpoints.Map(api);
        MeEndpoints.Map(api);
        PrivacyEndpoints.Map(api);
        MfaEndpoints.Map(api);
        WorkspaceEndpoints.Map(api);
        InvitationEndpoints.Map(api);
        DeviceEndpoints.Map(api);
        AgentEndpoints.Map(api);
        MachineEndpoints.Map(api);
        SessionEndpoints.Map(api);
        CommandEndpoints.Map(api);
        api.MapFallback(() => Results.NotFound());
        if (files is not null)
        {
            // The single-page app answers every non-API path; /api/* never falls through to it (global #17).
            app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html",
                new StaticFileOptions { FileProvider = files, OnPrepareResponse = NoCacheForPages });
        }
    }

    /// <summary>The page itself is always revalidated, so a new deploy loads at once; the hashed bundles may cache.</summary>
    private static void NoCacheForPages(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext ctx)
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.Ordinal)) ctx.Context.Response.Headers.CacheControl = "no-cache";
    }

    private static Task SecurityHeaders(HttpContext http, Func<Task> next)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Embedder-Policy"] = "require-corp";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        if (http.Request.Path.StartsWithSegments("/api"))
        {
            // Account data, sessions and captured content: never kept by a browser or a proxy cache.
            h.CacheControl = "no-store";
        }

        h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self'; " +
                                  "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        return next();
    }

    /// <summary>
    /// Cookie-authenticated changes must come from our own pages: an unsafe method on /api needs the X-CSRF header
    /// (a cross-site form cannot set it, and a cross-site script cannot without a CORS preflight we never allow) and,
    /// when the browser sends Origin, it must be ours. Agent calls carry a bearer token instead of a cookie.
    /// </summary>
    private static Task Csrf(HttpContext http, Func<Task> next, ApiConfig config)
    {
        var request = http.Request;
        var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
        var cookieCall = request.Path.StartsWithSegments("/api")
                         && (request.Cookies.ContainsKey(ApiConfig.SessionCookie) || request.Cookies.ContainsKey(ApiConfig.MfaCookie))
                         && !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal);
        if (unsafeMethod && cookieCall)
        {
            var origin = request.Headers.Origin.ToString();
            if (request.Headers[ApiConfig.CsrfHeader].ToString() != "1" || (origin.Length > 0 && origin != config.PublicOrigin))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return http.Response.WriteAsJsonAsync(new { title = "csrf" });
            }
        }

        return next();
    }
}
