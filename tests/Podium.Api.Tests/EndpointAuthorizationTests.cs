using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// Authorization is fail-closed by construction (global #6, F-086): an endpoint that names no policy falls to a fallback policy that wants a
/// signed-in user, and the endpoints anyone may call are marked AllowAnonymous and listed here. A new anonymous endpoint turns this red
/// until it is added to the list on purpose; a listed one that is gone or protected turns it red too, so the list stays the truth.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class EndpointAuthorizationTests(ApiFactory api)
{
    /// <summary>"METHOD /pattern" of every endpoint anyone may call. A method of "*" is an endpoint that answers every method (the /api fallback, a 404).</summary>
    private static readonly string[] Anonymous =
    [
        "GET /api/version",
        "POST /api/auth/register",
        "POST /api/auth/verify-email",
        "POST /api/auth/login",
        "POST /api/auth/password/forgot",
        "POST /api/auth/password/reset",
        "POST /api/auth/mfa",
        "GET /api/auth/providers",
        "GET /api/auth/external/{provider}",
        "GET /api/auth/external/{provider}/done",
        "POST /api/device/code",
        "POST /api/device/token",
        "POST /api/agent/token/refresh",
        "POST /api/integrations/jira/webhook/{id:guid}",
        "* /api/{*path:nonfile}",
        "* /downloads/{**rest}",
        "GET,HEAD /{*path:regex(^(?!api/|downloads/).*$)}",
    ];

    /// <summary>Anonymous too, but mapped only with settings the test server does not have: AIPOD_DOWNLOADS_URL (DownloadsRedirect), no web root (the 404 fallback).</summary>
    private static readonly string[] AnonymousWhenConfigured = ["GET /downloads/{file}", "* /{**path}"];

    private static string Key(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return $"{(methods is { Count: > 0 } ? string.Join(",", methods) : "*")} /{e.RoutePattern.RawText?.TrimStart('/')}";
    }

    private List<RouteEndpoint> Endpoints() =>
        api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    [Fact]
    public void The_fallback_policy_wants_a_signed_in_user()
    {
        var fallback = api.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void Every_endpoint_names_a_policy_or_is_on_the_anonymous_list_and_marked_so()
    {
        var unprotected = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null || e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(e => (Key: Key(e), Marked: e.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .ToList();

        var unlisted = unprotected.Where(e => !Anonymous.Contains(e.Key) && !AnonymousWhenConfigured.Contains(e.Key)).Select(e => e.Key).ToList();
        var unmarked = unprotected.Where(e => !e.Marked).Select(e => e.Key).ToList();

        Assert.Empty(unlisted);
        Assert.Empty(unmarked);
    }

    [Fact]
    public void Every_listed_anonymous_endpoint_exists_and_is_marked_anonymous()
    {
        var marked = Endpoints().Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => Key(e)).ToHashSet();

        var missing = Anonymous.Where(a => !marked.Contains(a)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task An_anonymous_call_to_a_protected_endpoint_is_401_and_to_a_listed_one_is_not()
    {
        var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/no-such-endpoint")).StatusCode);
    }
}
