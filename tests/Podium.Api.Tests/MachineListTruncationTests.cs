using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// F-024: the agents and machines lists of a workspace hold at most 500 rows and say when rows were left out, with the header the work tracker
/// lists use (X-Truncated: true); an optional skip fetches the rest. The order is total (ties broken on the id), so pages neither overlap nor
/// miss a row. The body stays a bare array, and a list of up to 500 rows without skip answers as before.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MachineListTruncationTests(ApiFactory api)
{
    private const int Cap = 500; // the contract, written out so that changing it fails a test on purpose
    public const string Agents = "agents";
    public const string Machines = "machines";

    /// <summary>Active agents on machines that all share one host name and one enrolment time: only the id tells them apart.</summary>
    private async Task<List<Guid>> SeedAsync(TestUser user, int count)
    {
        var now = api.Clock.GetUtcNow() - TimeSpan.FromHours(1);
        await using var db = api.Db();
        var agents = new List<Agent>();
        for (var i = 0; i < count; i++)
        {
            var machine = new Machine { WorkspaceId = user.WorkspaceId, MachineKeyHash = Guid.NewGuid().ToString("N"), Hostname = "same-host", Os = OsKinds.Linux, Arch = "x64", FirstSeenAt = now, LastSeenAt = now };
            db.Machines.Add(machine);
            agents.Add(new Agent { MachineId = machine.Id, UserId = user.Id, WorkspaceId = user.WorkspaceId, Version = "0.3.0", Status = AgentStatuses.Active, EnrolledAt = now });
        }

        db.Agents.AddRange(agents);
        await db.SaveChangesAsync();
        return agents.Select(a => a.Id).ToList();
    }

    private static string Url(TestUser user, string list, string? skip = null) =>
        $"/api/workspaces/{user.WorkspaceId}/{list}" + (skip is null ? "" : $"?skip={skip}");

    private static Guid IdOf(JsonElement row, string list) => row.GetProperty(list == Agents ? "id" : "agentId").GetGuid();

    private static async Task<(List<Guid> Ids, bool Truncated)> PageAsync(TestUser user, string list, string? skip = null)
    {
        var response = await user.Http.GetAsync(Url(user, list, skip));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        var truncated = response.Headers.TryGetValues(IntegrationImportEndpoints.TruncatedHeader, out var values) && values.Single() == "true";
        return (body.EnumerateArray().Select(r => IdOf(r, list)).ToList(), truncated);
    }

    [Theory]
    [InlineData(Agents)]
    [InlineData(Machines)]
    public async Task With_501_rows_the_first_page_holds_500_and_says_so_and_skip_500_brings_the_last_one_without_the_header(string list)
    {
        var user = await api.NewClient().SignedUpAsync("trunc-501-" + list);
        var all = await SeedAsync(user, Cap + 1);

        var first = await PageAsync(user, list);
        var rest = await PageAsync(user, list, "500");

        Assert.Equal((Cap, true), (first.Ids.Count, first.Truncated));
        Assert.Equal((1, false), (rest.Ids.Count, rest.Truncated));
        Assert.Equal(all.Order(), first.Ids.Concat(rest.Ids).Order()); // no row twice, none missed
        Assert.Equal(first.Ids, (await PageAsync(user, list, "0")).Ids); // skip=0 is the first page
    }

    [Theory]
    [InlineData(Agents)]
    [InlineData(Machines)]
    public async Task Exactly_500_rows_are_all_listed_without_the_header(string list)
    {
        var user = await api.NewClient().SignedUpAsync("trunc-500-" + list);
        await SeedAsync(user, Cap);

        var page = await PageAsync(user, list);

        Assert.Equal((Cap, false), (page.Ids.Count, page.Truncated));
    }

    [Theory]
    [InlineData(Agents, "-1")]
    [InlineData(Machines, "-1")]
    [InlineData(Agents, "x")]
    [InlineData(Machines, "x")]
    public async Task A_skip_that_is_not_a_whole_number_of_zero_or_more_is_refused_with_400(string list, string skip)
    {
        var user = await api.NewClient().SignedUpAsync("trunc-bad-" + list);

        var response = await user.Http.GetAsync(Url(user, list, skip));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (skip == "-1") Assert.Equal(("skip", IntegrationImportEndpoints.InvalidPaging), await ProjectKit.ProblemAsync(response));
    }

    [Theory]
    [InlineData(Agents)]
    [InlineData(Machines)]
    public async Task A_viewer_reads_the_pages_and_a_stranger_still_gets_404(string list)
    {
        var owner = await api.NewClient().SignedUpAsync("trunc-acc-" + list);
        var viewer = await RemoteKit.MemberAsync(api, owner, "trunc-viewer-" + list, "viewer");
        var stranger = await api.NewClient().SignedUpAsync("trunc-stranger-" + list);
        await SeedAsync(owner, 2);

        var page = await viewer.Http.GetAsync(Url(owner, list, "1"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Single((await page.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync(Url(owner, list, "0"))).StatusCode);
    }
}
