using System.Text.Json.Nodes;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>Reads what the API sent to a fake site, so a test can say exactly what a write put on the wire.</summary>
public static class JiraLog
{
    /// <summary>The requests to this site, as "METHOD route" in the order they were made (the route is the Jira path without the site prefix).</summary>
    public static List<string> Calls(this FakeJira jira, FakeSite site) =>
        [.. jira.Requests.Where(r => IsOf(r, site)).Select(r => $"{r.Method} {Route(r, site)}")];

    /// <summary>The calls that change something at the site (the search is a POST but only reads).</summary>
    public static List<string> Writes(this FakeJira jira, FakeSite site) =>
        [.. jira.Requests.Where(r => IsOf(r, site) && r.Method != HttpMethod.Get && !Route(r, site).StartsWith("/rest/api/3/search", StringComparison.Ordinal))
            .Select(r => $"{r.Method} {Route(r, site)}")];

    /// <summary>The bodies of the calls of this method to exactly this route, in order.</summary>
    public static List<JsonObject> Bodies(this FakeJira jira, FakeSite site, HttpMethod method, string route) =>
        [.. jira.Requests.Where(r => IsOf(r, site) && r.Method == method && Route(r, site) == route).Select(r => JsonNode.Parse(r.Body)!.AsObject())];

    /// <summary>The body of the only call of this method to this route.</summary>
    public static JsonObject Sent(this FakeJira jira, FakeSite site, HttpMethod method, string route) => Assert.Single(jira.Bodies(site, method, route));

    /// <summary>The account whose token made each call of this method to this route (null: a token the fake does not know).</summary>
    public static List<string?> Callers(this FakeJira jira, FakeSite site, HttpMethod method, string route) =>
        [.. jira.Requests.Where(r => IsOf(r, site) && r.Method == method && Route(r, site) == route).Select(r => jira.AccountOf(r.Bearer)?.Id)];

    private static bool IsOf(FakeRequest r, FakeSite site) => r.Path.StartsWith($"/ex/jira/{site.CloudId}/", StringComparison.Ordinal);

    private static string Route(FakeRequest r, FakeSite site) => r.Path[$"/ex/jira/{site.CloudId}".Length..];
}
