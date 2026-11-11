using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The workspace dashboard: who may read it, the window, how permission answers are bucketed and what the counts cover.</summary>
[Collection(ApiGroup.Name)]
public sealed class DashboardTests(ApiFactory api)
{
    private static string Url(Guid workspace, int? days = null) => $"/api/workspaces/{workspace}/dashboard" + (days is { } d ? $"?days={d}" : "");

    /// <summary>One section of the dashboard as state to count.</summary>
    private static Dictionary<string, int> Section(JsonElement dashboard, string name) =>
        dashboard.GetProperty(name).EnumerateArray().ToDictionary(r => r.GetProperty("state").GetString()!, r => r.GetProperty("count").GetInt32());

    private async Task<(TestUser Owner, TestAgent Agent, string External)> SessionAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("dash-owner");
        var agent = await owner.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, api.Clock.GetUtcNow()));
        return (owner, agent, external);
    }

    private static async Task<Guid> AskAsync(TestAgent agent, string external, int waitSeconds)
    {
        var response = await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, "Bash", JsonSerializer.SerializeToElement(new { command = "ls" }), waitSeconds), TestUser.Json);
        return (await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!.Id;
    }

    private static async Task AnswerAsync(TestUser owner, Guid request, string decision) =>
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/permission-requests/{request}/answer", new { decision })).StatusCode);

    [Fact]
    public async Task The_dashboard_needs_a_signed_in_user()
    {
        var owner = await api.NewClient().SignedUpAsync("dash-anon");

        var response = await api.NewClient().Http.GetAsync(Url(owner.WorkspaceId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_outside_the_workspace_gets_404()
    {
        var owner = await api.NewClient().SignedUpAsync("dash-owner");
        var stranger = await api.NewClient().SignedUpAsync("dash-stranger");

        var response = await stranger.Http.GetAsync(Url(owner.WorkspaceId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_workspace_viewer_reads_the_dashboard()
    {
        var owner = await api.NewClient().SignedUpAsync("dash-owner");
        var viewer = await RemoteKit.MemberAsync(api, owner, "dash-viewer", "viewer");

        var response = await viewer.Http.GetAsync(Url(owner.WorkspaceId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(7, 7)]
    [InlineData(90, 90)]
    [InlineData(91, 90)]
    [InlineData(5000, 90)]
    public async Task The_window_defaults_to_30_days_and_is_clamped_between_1_and_90(int? asked, int expected)
    {
        var owner = await api.NewClient().SignedUpAsync("dash-days");

        var dashboard = await owner.GetJsonAsync(Url(owner.WorkspaceId, asked));

        Assert.Equal(expected, dashboard.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task Permission_answers_are_bucketed_as_allow_deny_auto_allowed_open_and_expired_with_a_missing_decision_counted_as_deny()
    {
        var (owner, agent, external) = await SessionAsync();
        var allowed = await AskAsync(agent, external, 120);
        var denied = await AskAsync(agent, external, 120);
        var byRule = await AskAsync(agent, external, 120);
        var noDecision = await AskAsync(agent, external, 120);
        await AskAsync(agent, external, 120); // still open
        await AskAsync(agent, external, 5); // open, then runs out of time
        await AnswerAsync(owner, allowed, "allow");
        await AnswerAsync(owner, denied, "deny");
        await AnswerAsync(owner, byRule, "allow");
        await AnswerAsync(owner, noDecision, "allow");
        await using (var db = api.Db())
        {
            await db.PermissionRequests.Where(p => p.Id == byRule).ExecuteUpdateAsync(s => s.SetProperty(p => p.AnsweredByRule, Guid.NewGuid()));
            await db.PermissionRequests.Where(p => p.Id == noDecision).ExecuteUpdateAsync(s => s.SetProperty(p => p.Decision, (string?)null));
        }

        api.Clock.Advance(TimeSpan.FromSeconds(6));
        var dashboard = await owner.GetJsonAsync(Url(owner.WorkspaceId));

        Assert.Equal(new Dictionary<string, int> { ["allow"] = 1, ["deny"] = 2, ["auto_allowed"] = 1, ["open"] = 1, ["expired"] = 1 }, Section(dashboard, "approvals"));
    }

    [Fact]
    public async Task An_open_request_past_its_wait_counts_as_expired_before_the_housekeeper_has_marked_it()
    {
        var (owner, agent, external) = await SessionAsync();
        var request = await AskAsync(agent, external, 5);

        api.Clock.Advance(TimeSpan.FromSeconds(6));
        var dashboard = await owner.GetJsonAsync(Url(owner.WorkspaceId));

        await using var db = api.Db();
        Assert.Equal(PermissionStatuses.Open, (await db.PermissionRequests.AsNoTracking().SingleAsync(p => p.Id == request)).Status);
        Assert.Equal(new Dictionary<string, int> { ["expired"] = 1 }, Section(dashboard, "approvals"));
    }

    [Fact]
    public async Task Permission_answers_older_than_the_window_are_left_out_until_the_window_is_widened()
    {
        var (owner, agent, external) = await SessionAsync();
        var recent = await AskAsync(agent, external, 120);
        var old = await AskAsync(agent, external, 120);
        await AnswerAsync(owner, recent, "allow");
        await AnswerAsync(owner, old, "allow");
        await using (var db = api.Db())
        {
            var past = api.Clock.GetUtcNow().AddDays(-45);
            await db.PermissionRequests.Where(p => p.Id == old).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, past));
        }

        var narrow = await owner.GetJsonAsync(Url(owner.WorkspaceId));
        var wide = await owner.GetJsonAsync(Url(owner.WorkspaceId, 60));

        Assert.Equal((1, 2), (Section(narrow, "approvals")["allow"], Section(wide, "approvals")["allow"]));
    }

    [Fact]
    public async Task Runs_are_counted_by_status_inside_the_window_only()
    {
        var team = await RemoteKit.TeamAsync(api);
        await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp"));
        var approvedReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp");
        var approved = await RemoteKit.CreateAsync(team.Requester, approvedReq);
        var old = await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp"));
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, approved.Id, approvedReq)).StatusCode);
        await using (var db = api.Db())
        {
            var past = api.Clock.GetUtcNow().AddDays(-45);
            await db.RemoteRuns.Where(r => r.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, past));
        }

        var dashboard = await team.Admin.GetJsonAsync(Url(team.WorkspaceId));

        Assert.Equal(new Dictionary<string, int> { [RunStatuses.PendingApproval] = 1, [RunStatuses.Approved] = 1 }, Section(dashboard, "runs"));
    }

    [Fact]
    public async Task Projects_are_counted_by_status_and_sprints_only_for_active_projects_and_never_the_archived_ones()
    {
        var owner = await api.NewClient().SignedUpAsync("dash-sprints");
        var live = (await ProjectKit.CreateProjectAsync(owner, "Live")).GetProperty("id").GetGuid();
        var shelved = (await ProjectKit.CreateProjectAsync(owner, "Shelved")).GetProperty("id").GetGuid();
        var running = await SprintKit.SprintAsync(owner, live);
        await SprintKit.SprintAsync(owner, live);
        var dropped = await SprintKit.SprintAsync(owner, live);
        await SprintKit.SprintAsync(owner, shelved);
        (await SprintKit.StartAsync(owner, running)).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{dropped}")).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/projects/{shelved}")).EnsureSuccessStatusCode();

        var dashboard = await owner.GetJsonAsync(Url(owner.WorkspaceId));

        Assert.Equal((new Dictionary<string, int> { ["active"] = 1, ["archived"] = 1 }, new Dictionary<string, int> { ["planned"] = 1, ["active"] = 1 }),
            (Section(dashboard, "projects"), Section(dashboard, "sprints")));
    }

    [Fact]
    public async Task Another_workspaces_projects_sprints_and_answers_are_not_counted()
    {
        var (other, agent, external) = await SessionAsync();
        var elsewhere = (await ProjectKit.CreateProjectAsync(other, "Elsewhere")).GetProperty("id").GetGuid();
        await SprintKit.SprintAsync(other, elsewhere);
        await AskAsync(agent, external, 120);
        var owner = await api.NewClient().SignedUpAsync("dash-empty");

        var dashboard = await owner.GetJsonAsync(Url(owner.WorkspaceId));

        Assert.Equal((0, 0, 0, 0), (Section(dashboard, "projects").Count, Section(dashboard, "sprints").Count, Section(dashboard, "runs").Count, Section(dashboard, "approvals").Count));
    }
}
