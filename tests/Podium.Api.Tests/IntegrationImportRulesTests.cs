using System.Net;
using System.Net.Http.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Who may import and with what list (ADR-0015 §8), and what happens when the connector must sign in again.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationImportRulesTests(ApiFactory api)
{
    private static string[] Keys(int count) => [.. Enumerable.Range(0, count).Select(i => $"K{i}")];

    [Fact]
    public async Task The_list_of_keys_must_have_between_one_and_twenty_usable_keys()
    {
        var w = await JiraKit.ConnectedAsync(api);
        async Task<(string, string)> Refused(object body) => await ProjectKit.ProblemAsync(await w.Admin.PostAsync($"{w.Base}/spaces/import", body));

        Assert.Equal(("keys", "invalid_list"), await Refused(new { keys = Array.Empty<string>() }));
        Assert.Equal(("keys", "invalid_list"), await Refused(new { keys = (string[]?)null }));
        Assert.Equal(("keys", "invalid_list"), await Refused(new { keys = new[] { "", "   " } }));
        Assert.Equal(("keys", "invalid_list"), await Refused(new { keys = new[] { new string('K', 65) } }));
        Assert.Equal(("keys", "invalid_list"), await Refused(new { keys = Keys(21) }));
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.PostAsync($"{w.Base}/spaces/import", new { keys = Keys(20) })).StatusCode);
        var twice = await JiraKit.ImportAsync(w, " ONE ", "ONE", "TWO");
        Assert.Equal(["ONE", "TWO"], twice.EnumerateArray().Select(r => r.GetProperty("key").GetString())); // trimmed and listed once
    }

    [Fact]
    public async Task Only_an_admin_of_the_workspace_may_import_a_space()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ir-member");
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "ir-viewer", "viewer");
        var otherWorkspace = await JiraKit.ConnectedAsync(api);
        var anonymous = api.CreateClient();

        foreach (var user in new[] { member, viewer, otherWorkspace.Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"{w.Base}/spaces/import", new { keys = new[] { "ABC" } })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{w.Base}/spaces/import", new { keys = new[] { "ABC" } })).StatusCode);
        await using var db = api.Db();
        Assert.Equal(0, await db.Projects.CountAsync(p => p.SourceConnectionId == w.ConnectionId));
    }

    [Fact]
    public async Task A_connector_who_must_sign_in_again_cannot_import_or_list_until_somebody_connects_again()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC", "Alpha").Issue("One"));
        await ExpireAccessAsync(w);
        w.Account.Revoked = true; // the person took the app's access away at Atlassian

        var first = await JiraKit.ImportAsync(w, "ABC");
        var second = await JiraKit.ImportAsync(w, "ABC");
        var listing = await w.Admin.Http.GetAsync($"{w.Base}/spaces");

        Assert.Equal("tracker_unauthorized", first[0].GetProperty("outcome").GetString()); // the refresh was refused: the account is marked
        Assert.Equal("reauthorize", second[0].GetProperty("outcome").GetString()); // and now nothing is tried
        await RemoteKit.AssertProblemAsync(listing, HttpStatusCode.Conflict, "reauthorize");
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((ConnectionStatuses.Reauthorize, "tracker_unauthorized"), (connection.Status, connection.LastError));
        await using (var db = api.Db())
        {
            Assert.Equal(AccountStatuses.Reauthorize, (await db.IntegrationAccounts.SingleAsync(a => a.ConnectionId == w.ConnectionId)).Status);
            Assert.Equal(0, await db.Projects.CountAsync(p => p.SourceConnectionId == w.ConnectionId));
        }

        var mine = (await w.Admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0];
        Assert.Equal(("reauthorize", "reauthorize"), (mine.GetProperty("status").GetString(), mine.GetProperty("myAccountStatus").GetString()));

        // Connecting again (with an account that works) repairs it, and the import goes through.
        var back = api.Jira.Account("Back", w.Admin.Email, w.Site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(w.Admin, api.Jira, back, await JiraKit.StartAsync(w.Admin, w.Site.Subdomain))));
        Assert.Equal((ConnectionStatuses.Active, null), ((await JiraKit.ConnectionAsync(w)).Status, (await JiraKit.ConnectionAsync(w)).LastError));
        Assert.Equal("ok", (await JiraKit.ImportAsync(w, "ABC"))[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_site_that_refuses_the_connectors_read_is_a_forbidden_answer_not_a_server_error()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        api.Jira.Fail(w.Site, "/rest/api/3/project/search", HttpStatusCode.Forbidden);
        api.Jira.Fail(w.Site, "/rest/api/3/project/search", HttpStatusCode.InternalServerError);

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Http.GetAsync($"{w.Base}/spaces")).StatusCode);
        var broken = await w.Admin.Http.GetAsync($"{w.Base}/spaces");
        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode);
        Assert.Equal("tracker_unavailable", await RemoteKit.TitleAsync(broken));
        Assert.DoesNotContain("secret detail", await broken.Content.ReadAsStringAsync(), StringComparison.Ordinal); // the tool's text never reaches the web
    }

    [Fact]
    public async Task A_rate_limit_that_clears_is_waited_out_and_one_that_does_not_is_reported()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        api.Jira.Fail(w.Site, "/rest/api/3/project/search", HttpStatusCode.TooManyRequests, times: 1, retryAfter: "0");

        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Http.GetAsync($"{w.Base}/spaces")).StatusCode);
        api.Jira.Fail(w.Site, "/rest/api/3/project/search", HttpStatusCode.TooManyRequests, times: 3, retryAfter: "0");
        var limited = await w.Admin.Http.GetAsync($"{w.Base}/spaces");
        Assert.Equal(HttpStatusCode.BadGateway, limited.StatusCode);
        Assert.Equal("tracker_rate_limited", await RemoteKit.TitleAsync(limited));
    }

    private async Task ExpireAccessAsync(JiraWorld w)
    {
        await using var db = api.Db();
        var past = api.Clock.GetUtcNow().AddMinutes(-1);
        await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AccessExpiresAt, past));
    }
}
