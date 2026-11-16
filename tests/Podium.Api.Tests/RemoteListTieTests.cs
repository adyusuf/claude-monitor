using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The jobs and grants lists of a machine page by (created time, id) as the run list does (F-341, after F-167): rows created at the very same
/// moment are neither skipped nor repeated across a page boundary, and <c>before</c> alone still means strictly older.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RemoteListTieTests(ApiFactory api)
{
    public static TheoryData<string> Lists => new() { "jobs", "grants" };

    private static async Task<List<JsonElement>> ListAsync(TestUser user, Guid agent, string list, string query) =>
        (await user.GetJsonAsync($"/api/agents/{agent}/{list}{query}")).EnumerateArray().ToList();

    private static List<Guid> Ids(IEnumerable<JsonElement> rows) => rows.Select(r => r.GetProperty("id").GetGuid()).ToList();

    /// <summary>Five jobs or grants on the target, all created at the same moment (in the past, so no clamp touches them).</summary>
    private async Task<RemoteTeam> TeamWithTiedRowsAsync(string list, int count)
    {
        var team = await RemoteKit.TeamAsync(api);
        var at = api.Clock.GetUtcNow().AddMinutes(-10);
        await using var db = api.Db();
        for (var i = 0; i < count; i++)
        {
            if (list == "jobs")
            {
                db.MachineJobs.Add(new MachineJob
                {
                    WorkspaceId = team.WorkspaceId,
                    TargetAgentId = team.TargetId,
                    OwnerUserId = team.Owner.Id,
                    Name = $"job {i}",
                    NameSearch = $"job {i}",
                    Argv = ["make"],
                    Cwd = "/tmp",
                    TimeoutSeconds = 60,
                    Status = JobStatuses.Retired,
                    ProposedByUserId = team.Owner.Id,
                    CreatedAt = at,
                    RetiredAt = at,
                });
            }
            else
            {
                db.MachineGrants.Add(new MachineGrant
                {
                    WorkspaceId = team.WorkspaceId,
                    TargetAgentId = team.TargetId,
                    OwnerUserId = team.Owner.Id,
                    GranteeUserId = team.Owner.Id,
                    Template = ["make"],
                    Cwd = "/tmp",
                    MaxTimeoutSeconds = 60,
                    TemplateHash = $"hash-{i}",
                    Status = GrantStatuses.Revoked,
                    CreatedAt = at,
                    ExpiresAt = at,
                    RevokedAt = at,
                });
            }
        }

        await db.SaveChangesAsync();
        return team;
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Before_with_the_last_rows_id_continues_inside_a_group_with_the_same_created_time(string list)
    {
        var team = await TeamWithTiedRowsAsync(list, 5);
        var all = Ids(await ListAsync(team.Owner, team.TargetId, list, ""));

        var pages = new List<Guid>();
        var query = "?limit=2";
        for (var page = 0; page < 5; page++)
        {
            var rows = await ListAsync(team.Owner, team.TargetId, list, query);
            if (rows.Count == 0) break;
            pages.AddRange(Ids(rows));
            var last = rows[^1];
            query = $"?limit=2&before={Uri.EscapeDataString(last.GetProperty("createdAt").GetString()!)}&beforeId={last.GetProperty("id").GetGuid()}";
        }

        Assert.Equal(5, all.Count);
        Assert.Equal(all, pages);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Before_alone_still_means_strictly_older_so_an_older_client_gets_what_it_got_before(string list)
    {
        var team = await TeamWithTiedRowsAsync(list, 3);
        var first = (await ListAsync(team.Owner, team.TargetId, list, "?limit=1"))[0];

        var older = await ListAsync(team.Owner, team.TargetId, list, $"?before={Uri.EscapeDataString(first.GetProperty("createdAt").GetString()!)}");

        Assert.Empty(older);
    }
}
