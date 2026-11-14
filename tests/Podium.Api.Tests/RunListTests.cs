using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The workspace's run list on the web: newest first, paged by <c>before</c>, cut by <c>limit</c> and narrowed to one target by <c>agent</c>.</summary>
[Collection(ApiGroup.Name)]
public sealed class RunListTests(ApiFactory api)
{
    private async Task<Guid> AskAsync(RemoteTeam team, Guid? target = null)
    {
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        return (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(target ?? team.TargetId, RemoteKit.NewKey(), cwd: "/tmp"))).Id;
    }

    private static async Task<List<Guid>> ListAsync(TestUser user, Guid workspace, string query = "") =>
        (await user.GetJsonAsync($"/api/workspaces/{workspace}/runs{query}")).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task The_list_is_newest_first()
    {
        var team = await RemoteKit.TeamAsync(api);
        var first = await AskAsync(team);
        var second = await AskAsync(team);
        var third = await AskAsync(team);

        var listed = await ListAsync(team.Admin, team.WorkspaceId);

        Assert.Equal([third, second, first], listed);
    }

    [Fact]
    public async Task Before_returns_only_the_runs_created_earlier_than_the_given_time()
    {
        var team = await RemoteKit.TeamAsync(api);
        var oldest = await AskAsync(team);
        var middle = await AskAsync(team);
        var newest = await AskAsync(team);
        var at = (await RemoteKit.RunRowAsync(api, newest)).CreatedAt;

        var listed = await ListAsync(team.Admin, team.WorkspaceId, "?before=" + Uri.EscapeDataString(at.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal([middle, oldest], listed);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task Limit_cuts_the_list_and_a_value_below_one_still_returns_one_run(int? limit, int expected)
    {
        var team = await RemoteKit.TeamAsync(api);
        await AskAsync(team);
        await AskAsync(team);
        await AskAsync(team);

        var listed = await ListAsync(team.Admin, team.WorkspaceId, limit is { } n ? $"?limit={n}" : "");

        Assert.Equal(expected, listed.Count);
    }

    [Fact]
    public async Task The_agent_filter_returns_only_the_runs_aimed_at_that_machine()
    {
        var team = await RemoteKit.TeamAsync(api);
        var second = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(second, ExecLevels.Argv);
        await AskAsync(team);
        var aimedAtSecond = await AskAsync(team, second.Tokens.AgentId);

        var listed = await ListAsync(team.Admin, team.WorkspaceId, $"?agent={second.Tokens.AgentId}");

        Assert.Equal([aimedAtSecond], listed);
    }

    [Fact]
    public async Task The_agent_filter_with_a_machine_that_has_no_runs_returns_an_empty_list()
    {
        var team = await RemoteKit.TeamAsync(api);
        await AskAsync(team);

        var listed = await ListAsync(team.Admin, team.WorkspaceId, $"?agent={Guid.NewGuid()}");

        Assert.Empty(listed);
    }

    [Fact]
    public async Task A_run_of_another_workspace_never_appears_in_the_list()
    {
        var team = await RemoteKit.TeamAsync(api);
        var other = await RemoteKit.TeamAsync(api);
        var mine = await AskAsync(team);
        await AskAsync(other);

        var listed = await ListAsync(team.Admin, team.WorkspaceId);

        Assert.Equal([mine], listed);
    }
}
