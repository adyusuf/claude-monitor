using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Fail-closed access to every integration endpoint (global #6): somebody outside the workspace, a removed member and a caller with no session get nothing, and learn nothing.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationAccessTests(ApiFactory api)
{
    private sealed record Call(string Name, HttpMethod Method, string Url, object? Body);

    /// <summary>Every endpoint of the integrations that takes a session, aimed at this world (and at a given connection id, to compare with one that does not exist).</summary>
    private static List<Call> Calls(Guid workspace, Guid connection, Guid person) =>
    [
        new("list", HttpMethod.Get, $"/api/workspaces/{workspace}/integrations", null),
        new("connect", HttpMethod.Post, $"/api/workspaces/{workspace}/integrations/jira/connect", new { subdomain = "someone" }),
        new("account", HttpMethod.Post, $"/api/integrations/{connection}/account/connect", null),
        new("settings", HttpMethod.Put, $"/api/integrations/{connection}", new { autoInvite = true }),
        new("disconnect", HttpMethod.Delete, $"/api/integrations/{connection}", null),
        new("spaces", HttpMethod.Get, $"/api/integrations/{connection}/spaces", null),
        new("import", HttpMethod.Post, $"/api/integrations/{connection}/spaces/import", new { keys = new[] { "SPR" } }),
        new("sprints", HttpMethod.Get, $"/api/integrations/{connection}/spaces/SPR/sprints", null),
        new("sprint import", HttpMethod.Post, $"/api/integrations/{connection}/spaces/SPR/sprints/import", new { sprintIds = new[] { "1" } }),
        new("people", HttpMethod.Get, $"/api/integrations/{connection}/people", null),
        new("person", HttpMethod.Put, $"/api/integrations/people/{person}", new { email = "someone@example.invalid" }),
        new("sync", HttpMethod.Post, $"/api/integrations/{connection}/sync", null),
        new("runs", HttpMethod.Get, $"/api/integrations/{connection}/runs", null),
    ];

    private async Task<Guid> PersonOfAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationPeople.Where(p => p.ConnectionId == w.ConnectionId).Select(p => p.Id).SingleAsync();
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, Call call)
    {
        using var request = new HttpRequestMessage(call.Method, call.Url);
        if (call.Body is not null) request.Content = JsonContent.Create(call.Body, options: TestUser.Json);
        request.Headers.Add("X-CSRF", "1");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Somebody_outside_the_workspace_and_a_removed_member_get_a_404_on_every_endpoint_and_nothing_changes()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var w = s.World;
        var person = await PersonOfAsync(w);

        var stranger = await api.NewClient().SignedUpAsync("acc-stranger");
        var removed = await RemoteKit.MemberAsync(api, w.Admin, "acc-removed", "admin");
        (await w.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{w.WorkspaceId}/members/{removed.Id}")).EnsureSuccessStatusCode();

        foreach (var (who, user) in new[] { ("stranger", stranger), ("removed", removed) })
        {
            foreach (var call in Calls(w.WorkspaceId, w.ConnectionId, person))
            {
                var real = await SendAsync(user.Http, call);
                var made = await SendAsync(user.Http, call with { Url = call.Url.Replace(w.ConnectionId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal) });
                Assert.True(real.Status == HttpStatusCode.NotFound, $"{who} / {call.Name}: {real.Status}");
                if (call.Name is not ("list" or "connect")) Assert.Equal(made, real); // a real connection cannot be told from one that does not exist
            }
        }

        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((ConnectionStatuses.Active, false), (connection.Status, connection.AutoInvite));
        await using var db = api.Db();
        Assert.Equal(1, await db.IntegrationAccounts.CountAsync(a => a.ConnectionId == w.ConnectionId));
        Assert.Equal(1, await db.Projects.CountAsync(p => p.SourceConnectionId == w.ConnectionId));
        Assert.Equal(0, await db.IntegrationOAuthStates.CountAsync(x => x.UserId == stranger.Id || x.UserId == removed.Id));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => (a.ActorUserId == stranger.Id || a.ActorUserId == removed.Id) && EF.Functions.Like(a.Action, "integration.%")));
    }

    [Fact]
    public async Task A_caller_with_no_session_gets_401_on_every_endpoint_including_the_return_from_atlassian()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        using var anonymous = api.CreateClient();

        foreach (var call in Calls(s.World.WorkspaceId, s.World.ConnectionId, Guid.NewGuid()))
        {
            Assert.True((await SendAsync(anonymous, call)).Status == HttpStatusCode.Unauthorized, call.Name);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/integrations/jira/callback?code=x&state=y")).StatusCode);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(s.World)).Status);
    }

    [Fact]
    public async Task A_member_has_the_rights_of_the_role_and_no_more()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var w = s.World;
        var member = await RemoteKit.MemberAsync(api, w.Admin, "acc-member");
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "acc-viewer", "viewer");
        var person = await PersonOfAsync(w);
        var adminOnly = new[] { "connect", "settings", "disconnect", "import", "people", "person", "sync" };

        foreach (var call in Calls(w.WorkspaceId, w.ConnectionId, person))
        {
            var asMember = (await SendAsync(member.Http, call)).Status;
            var asViewer = (await SendAsync(viewer.Http, call)).Status;
            if (adminOnly.Contains(call.Name))
            {
                Assert.True(asMember == HttpStatusCode.NotFound && asViewer == HttpStatusCode.NotFound, $"{call.Name}: member {asMember}, viewer {asViewer}");
            }
        }

        // What they may do: read; and linking one's own account is for a member, not for a viewer.
        Assert.Equal(HttpStatusCode.OK, (await member.Http.GetAsync($"{w.Base}/spaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Http.GetAsync($"{w.Base}/spaces/SPR/sprints")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsync($"{w.Base}/account/connect")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"{w.Base}/account/connect")).StatusCode);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(w)).Status);
    }

    [Fact]
    public async Task The_return_from_atlassian_for_somebody_else_shows_nothing_of_the_workspace()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("acc-callback");
        var url = await JiraKit.StartAsync(w.Admin, w.Site.Subdomain);

        var response = await JiraKit.ApproveAsync(stranger, api.Jira, api.Jira.Account("Thief", stranger.Email, w.Site), url);

        Assert.Equal("invalid_state", JiraKit.ResultOf(response));
        Assert.DoesNotContain(w.WorkspaceId.ToString(), response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no app")]
    [InlineData("no key")]
    public async Task Without_the_jira_app_or_the_key_the_page_says_it_is_not_available_and_connecting_is_refused(string missing)
    {
        var w = await JiraKit.ConnectedAsync(api);
        await using var off = JiraKit.With(api, c => missing == "no app" ? c with { Jira = null } : c with { IntegrationKey = null });
        var admin = JiraKit.ClientOf(api, off);
        (await admin.PostAsync("/api/auth/login", new { email = w.Admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();

        var list = await admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations");
        var connect = await admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/integrations/jira/connect", new { subdomain = w.Site.Subdomain });
        var account = await admin.PostAsync($"{w.Base}/account/connect");

        Assert.False(list.GetProperty("available").GetBoolean());
        Assert.Empty(list.GetProperty("trackerProviders").EnumerateArray());
        Assert.Single(list.GetProperty("connections").EnumerateArray()); // what exists stays visible
        Assert.Equal(("provider", "integration_unavailable"), await ProjectKit.ProblemAsync(connect));
        Assert.Equal(("provider", "integration_unavailable"), await ProjectKit.ProblemAsync(account));
        await using var db = api.Db();
        Assert.Equal(0, await db.IntegrationOAuthStates.CountAsync(x => x.UserId == w.Admin.Id));
        // The webhook address is shut as well: not even a good token gets an answer other than 404.
        var delivery = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-1"));
        using var client = off.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/jira/webhook/{w.ConnectionId}") { Content = new StringContent(WebhookKit.IssueEvent("ABC-1")) };
        request.Headers.TryAddWithoutValidation("Authorization", WebhookKit.Token(api));
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode); // the ordinary host still takes it
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task With_the_app_available_the_list_says_so()
    {
        var admin = await api.NewClient().SignedUpAsync("acc-available");

        var list = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/integrations");

        Assert.True(list.GetProperty("available").GetBoolean());
        Assert.Empty(list.GetProperty("connections").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, list.GetProperty("trackerProviders").ValueKind);
    }
}
