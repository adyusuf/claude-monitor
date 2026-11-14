using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The machine's own access ceiling (the folders and programs its admin allows a run, ADR-0020 §4) as the machine page's detail serves it:
/// to the machine's owner and the workspace's admins only; any other member gets no path that came from the machine.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MachineAccessCeilingTests(ApiFactory api)
{
    private const string AllowedFolder = "/srv/ceiling-secret-folder";
    private const string DeniedFolder = "/srv/ceiling-secret-folder/private";
    private const string Program = "/usr/local/bin/ceiling-secret-tool";

    private static async Task ReportCeilingAsync(RemoteTeam team)
    {
        var ceiling = new AccessCeilingReport([AllowedFolder], [DeniedFolder], [new AccessApp(Program, null)], false);
        var profile = new AgentProfile(ExecLevels.Argv, false, null, null, 2, Access: new AccessReport(ceiling, []));
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", profile, TestUser.Json)).EnsureSuccessStatusCode();
    }

    private static async Task<(JsonElement Access, string Text)> AccessOfAsync(TestUser reader, RemoteTeam team)
    {
        var response = await reader.Http.GetAsync($"/api/agents/{team.TargetId}/detail");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(text).RootElement.GetProperty("access"), text);
    }

    [Fact]
    public async Task The_machines_owner_sees_the_ceiling_the_machine_reported()
    {
        var team = await RemoteKit.TeamAsync(api);
        await ReportCeilingAsync(team);

        var (access, _) = await AccessOfAsync(team.Owner, team);

        var ceiling = access.GetProperty("ceiling");
        Assert.Equal([AllowedFolder], ceiling.GetProperty("allowedFolders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal([DeniedFolder], ceiling.GetProperty("deniedFolders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal([Program], ceiling.GetProperty("applications").EnumerateArray().Select(a => a.GetProperty("path").GetString()));
        Assert.False(ceiling.GetProperty("shell").GetBoolean());
    }

    [Fact]
    public async Task A_workspace_admin_who_is_not_the_owner_sees_the_ceiling()
    {
        var team = await RemoteKit.TeamAsync(api);
        var admin = await RemoteKit.MemberAsync(api, team.Admin, "cl-admin", "admin");
        await ReportCeilingAsync(team);

        var (access, _) = await AccessOfAsync(admin, team);

        Assert.Equal([AllowedFolder], access.GetProperty("ceiling").GetProperty("allowedFolders").EnumerateArray().Select(f => f.GetString()));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task Another_member_gets_no_ceiling_and_none_of_its_paths_appear_anywhere_in_the_answer(string role)
    {
        var team = await RemoteKit.TeamAsync(api);
        var reader = await RemoteKit.MemberAsync(api, team.Admin, "cl-reader", role);
        await ReportCeilingAsync(team);

        var (access, text) = await AccessOfAsync(reader, team);

        Assert.Equal(JsonValueKind.Null, access.GetProperty("ceiling").ValueKind);
        Assert.DoesNotContain("ceiling-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_that_reported_no_ceiling_shows_none_even_to_its_owner_and_no_report_time()
    {
        var team = await RemoteKit.TeamAsync(api);

        var (access, _) = await AccessOfAsync(team.Owner, team);

        Assert.Equal(JsonValueKind.Null, access.GetProperty("ceiling").ValueKind);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("reportedAt").ValueKind);
    }
}
