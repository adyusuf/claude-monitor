using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Hiding an ended session from the lists: the owner's right alone, ended sessions only, undone by the session's next event.</summary>
[Collection(ApiGroup.Name)]
public sealed class SessionHideTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private static async Task<List<Guid>> ListedAsync(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    /// <summary>The owner, their agent and one session that has started and, when asked, ended at +1 second.</summary>
    private async Task<(TestUser Owner, TestAgent Agent, string External, Guid Id)> SessionAsync(bool ended = true)
    {
        var owner = await api.NewClient().SignedUpAsync("hide-owner");
        var agent = await owner.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, T(0)));
        if (ended) await agent.SendAsync(TestAgent.Hook(external, "SessionEnd", new { }, T(1)));
        return (owner, agent, external, (await ListedAsync(owner)).Single());
    }

    [Fact]
    public async Task The_owner_hides_an_ended_session_and_it_leaves_the_list_but_its_page_stays_readable()
    {
        var (owner, _, _, id) = await SessionAsync();

        var response = await owner.PostAsync($"/api/sessions/{id}/hide");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListedAsync(owner));
        Assert.Equal(id, (await owner.GetJsonAsync($"/api/sessions/{id}")).GetProperty("session").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task Another_member_of_the_workspace_cannot_hide_the_session_and_it_stays_listed(string role)
    {
        var (owner, _, _, id) = await SessionAsync();
        var other = await RemoteKit.MemberAsync(api, owner, "hide-other", role);

        var response = await other.PostAsync($"/api/sessions/{id}/hide");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal([id], await ListedAsync(owner));
    }

    [Fact]
    public async Task A_user_outside_the_workspace_gets_404_and_the_session_stays_listed()
    {
        var (owner, _, _, id) = await SessionAsync();
        var stranger = await api.NewClient().SignedUpAsync("hide-stranger");

        var response = await stranger.PostAsync($"/api/sessions/{id}/hide");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal([id], await ListedAsync(owner));
    }

    [Fact]
    public async Task An_unknown_session_gets_404()
    {
        var owner = await api.NewClient().SignedUpAsync("hide-unknown");

        var response = await owner.PostAsync($"/api/sessions/{Guid.NewGuid()}/hide");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_running_session_cannot_be_hidden_and_the_refusal_is_session_not_ended()
    {
        var (owner, _, _, id) = await SessionAsync(ended: false);

        var response = await owner.PostAsync($"/api/sessions/{id}/hide");

        Assert.Equal(("session", "session_not_ended"), await ProjectKit.ProblemAsync(response));
        Assert.Equal([id], await ListedAsync(owner));
    }

    [Fact]
    public async Task A_hidden_session_comes_back_to_the_list_when_it_reports_again()
    {
        var (owner, agent, external, id) = await SessionAsync();
        (await owner.PostAsync($"/api/sessions/{id}/hide")).EnsureSuccessStatusCode();

        await agent.SendAsync(TestAgent.Hook(external, "UserPromptSubmit", new { prompt = "back again" }, T(5)));

        Assert.Equal([id], await ListedAsync(owner));
    }

    [Fact]
    public async Task A_hidden_session_stays_hidden_when_an_event_older_than_its_last_one_arrives_late()
    {
        var (owner, agent, external, id) = await SessionAsync();
        (await owner.PostAsync($"/api/sessions/{id}/hide")).EnsureSuccessStatusCode();

        await agent.SendAsync(TestAgent.Hook(external, "UserPromptSubmit", new { prompt = "stale" }, T(0)));

        Assert.Empty(await ListedAsync(owner));
    }

    [Fact]
    public async Task Hiding_is_audited_with_the_owner_as_actor_and_the_session_as_target()
    {
        var (owner, _, _, id) = await SessionAsync();

        (await owner.PostAsync($"/api/sessions/{id}/hide")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "session.hidden" && a.TargetId == id.ToString());
        Assert.Equal((owner.Id, owner.WorkspaceId), (audit.ActorUserId, audit.WorkspaceId));
    }

    [Fact]
    public async Task Hiding_an_already_hidden_session_answers_204_again_but_writes_no_second_audit_row()
    {
        var (owner, _, _, id) = await SessionAsync();
        (await owner.PostAsync($"/api/sessions/{id}/hide")).EnsureSuccessStatusCode();

        var again = await owner.PostAsync($"/api/sessions/{id}/hide");

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        await using var db = api.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == "session.hidden" && a.TargetId == id.ToString()));
        Assert.Empty(await ListedAsync(owner));
    }

    [Fact]
    public async Task Hiding_tells_the_workspace_stream_so_open_lists_update()
    {
        var (owner, _, _, id) = await SessionAsync();
        using var stream = await RemoteKit.OpenAsync(owner.Http, $"/api/workspaces/{owner.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(stream, "ready");

        (await owner.PostAsync($"/api/sessions/{id}/hide")).EnsureSuccessStatusCode();

        var message = await RemoteKit.NextEventAsync(stream, "session", e => e.GetProperty("sessionId").GetGuid() == id);
        Assert.Equal("ended", message.GetProperty("status").GetString());
    }
}
