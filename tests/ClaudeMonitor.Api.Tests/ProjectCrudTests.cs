using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Managed projects (ADR-0011): create, read, list, change, archive, restore, who may, and what is audited.</summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectCrudTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Member, TestUser Viewer)> TeamAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-owner", "Olga Owner");
        return (owner, await RemoteKit.MemberAsync(api, owner, "pj-member"), await RemoteKit.MemberAsync(api, owner, "pj-viewer", "viewer"));
    }

    [Fact]
    public async Task A_project_keeps_every_definition_field_and_the_creator_is_its_owner()
    {
        var (owner, member, _) = await TeamAsync();

        var created = await ProjectKit.CreateProjectAsync(member, "Storefront", workspace: owner.WorkspaceId);
        var read = await member.GetJsonAsync($"/api/projects/{created.GetProperty("id").GetGuid()}");

        Assert.Equal("Storefront", read.GetProperty("name").GetString());
        Assert.Equal(member.Id, read.GetProperty("ownerUserId").GetGuid());
        Assert.Equal("Test Person", read.GetProperty("ownerName").GetString());
        Assert.Equal(("Ship the thing", "Head of delivery"), (read.GetProperty("purpose").GetString(), read.GetProperty("sponsor").GetString()));
        Assert.Equal("Ada", read.GetProperty("stakeholders")[0].GetProperty("name").GetString());
        Assert.Equal((12500.5m, "EUR", "2026-11-01", "2027-03-31"),
            (read.GetProperty("budgetAmount").GetDecimal(), read.GetProperty("budgetCurrency").GetString(), read.GetProperty("startDate").GetString(), read.GetProperty("endDate").GetString()));
        Assert.Equal(["epic", "story", "bug"], read.GetProperty("itemTypes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(("moscow", "hours", 10), (read.GetProperty("priorityScale").GetString(), read.GetProperty("estimationUnit").GetString(), read.GetProperty("sprintLengthDays").GetInt32()));
        Assert.Equal(["Reviewed", "Deployed to test"], read.GetProperty("definitionOfDone").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("active", read.GetProperty("status").GetString());
        Assert.True(read.GetProperty("access").GetProperty("canEdit").GetBoolean());
        Assert.Equal(owner.WorkspaceId, read.GetProperty("workspaceId").GetGuid());
    }

    [Fact]
    public async Task A_minimal_project_takes_the_defaults_of_the_settings_the_levels_below_inherit()
    {
        var user = await api.NewClient().SignedUpAsync("pj-min");

        var response = await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", new { name = "Bare" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var read = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["epic", "story", "task", "bug", "spike"], read.GetProperty("itemTypes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(("p0_p3", "story_points", 14), (read.GetProperty("priorityScale").GetString(), read.GetProperty("estimationUnit").GetString(), read.GetProperty("sprintLengthDays").GetInt32()));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("budgetAmount").ValueKind);
        Assert.Empty(read.GetProperty("stakeholders").EnumerateArray());
    }

    [Fact]
    public async Task A_workspace_viewer_reads_but_cannot_create_and_a_stranger_sees_nothing()
    {
        var (owner, _, viewer) = await TeamAsync();
        var project = await ProjectKit.CreateProjectAsync(owner, "Visible");
        var stranger = await api.NewClient().SignedUpAsync("pj-stranger");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", ProjectKit.Project("Nope"))).StatusCode);
        Assert.Equal("Visible", (await viewer.GetJsonAsync($"/api/projects/{project.GetProperty("id").GetGuid()}")).GetProperty("name").GetString());
        Assert.False((await viewer.GetJsonAsync($"/api/projects/{project.GetProperty("id").GetGuid()}")).GetProperty("access").GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/projects/{project.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", ProjectKit.Project("Sneaky"))).StatusCode);
    }

    [Fact]
    public async Task Only_a_member_can_be_the_owner_of_a_project()
    {
        var (owner, member, viewer) = await TeamAsync();
        var stranger = await api.NewClient().SignedUpAsync("pj-outsider");

        Assert.Equal(("ownerUserId", "not_a_member"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", ProjectKit.Project("A", stranger.Id))));
        Assert.Equal(("ownerUserId", "not_a_member"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", ProjectKit.Project("B", viewer.Id))));
        Assert.Equal(member.Id, (await ProjectKit.CreateProjectAsync(owner, "C", member.Id)).GetProperty("ownerUserId").GetGuid());
    }

    [Theory]
    [InlineData("name", "", "invalid_name")]
    [InlineData("purpose", "TOO_LONG_TEXT", "too_long")]
    [InlineData("budgetCurrency", "euro", "invalid_currency")]
    [InlineData("sprintLengthDays", 43, "out_of_range")]
    [InlineData("priorityScale", "nonsense", "invalid_value")]
    [InlineData("estimationUnit", "nonsense", "invalid_value")]
    [InlineData("itemTypes", new string[0], "invalid_list")]
    [InlineData("itemTypes", new[] { "epic", "epic" }, "invalid_list")]
    [InlineData("itemTypes", new[] { "saga" }, "invalid_list")]
    [InlineData("definitionOfDone", new[] { " " }, "invalid_list")]
    public async Task A_project_with_a_bad_field_is_refused_with_a_code_the_web_translates(string field, object value, string code)
    {
        var user = await api.NewClient().SignedUpAsync("pj-bad");
        var body = JsonSerializer.SerializeToElement(ProjectKit.Project("Bad"), TestUser.Json).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);
        body[field] = value is "TOO_LONG_TEXT" ? new string('x', 2001) : value;

        var problem = await ProjectKit.ProblemAsync(await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", body));

        Assert.Equal((field, code), problem);
    }

    [Fact]
    public async Task The_budget_needs_both_an_amount_and_a_currency_and_the_end_cannot_precede_the_start()
    {
        var user = await api.NewClient().SignedUpAsync("pj-pair");

        Assert.Equal(("budget", "amount_and_currency"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", new { name = "X", budgetAmount = 5 })));
        Assert.Equal(("budget", "amount_and_currency"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", new { name = "X", budgetCurrency = "EUR" })));
        Assert.Equal(("endDate", "before_start"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", new { name = "X", startDate = "2026-05-02", endDate = "2026-05-01" })));
    }

    [Fact]
    public async Task Two_active_projects_cannot_share_a_name_even_in_another_case_or_accent_but_an_archived_name_is_free_again()
    {
        var user = await api.NewClient().SignedUpAsync("pj-dup");
        var first = await ProjectKit.CreateProjectAsync(user, "Şişman Proje");

        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/workspaces/{user.WorkspaceId}/projects", ProjectKit.Project("SISMAN proje"))));

        (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{first.GetProperty("id").GetGuid()}")).EnsureSuccessStatusCode();
        var again = await ProjectKit.CreateProjectAsync(user, "Şişman Proje");
        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/projects/{first.GetProperty("id").GetGuid()}/restore")));
        Assert.NotEqual(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_list_pages_newest_first_hides_archived_ones_and_searches_without_case_or_accent()
    {
        var user = await api.NewClient().SignedUpAsync("pj-list");
        foreach (var name in new[] { "Alpha", "İstanbul Portal", "Gamma" }) await ProjectKit.CreateProjectAsync(user, name);
        var gone = await ProjectKit.CreateProjectAsync(user, "Retired");
        await ProjectKit.CreateApplicationAsync(user, (await ProjectKit.CreateProjectAsync(user, "With app")).GetProperty("id").GetGuid(), "Shop");
        (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{gone.GetProperty("id").GetGuid()}")).EnsureSuccessStatusCode();

        var first = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/projects?limit=2");
        var second = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/projects?limit=2&cursor={first.GetProperty("next").GetString()}");
        var found = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/projects?q=istanbul");
        var archived = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/projects?status=archived");
        var all = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/projects?status=all");

        Assert.Equal(["With app", "Gamma"], first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(["İstanbul Portal", "Alpha"], second.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);
        Assert.Equal("İstanbul Portal", Assert.Single(found.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("Retired", Assert.Single(archived.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(5, all.GetProperty("items").GetArrayLength());
        Assert.Equal(1, first.GetProperty("items")[0].GetProperty("applicationCount").GetInt32());
    }

    [Fact]
    public async Task A_manager_changes_the_definition_but_not_the_owner_and_cannot_archive_while_the_owner_does_both()
    {
        var (owner, member, _) = await TeamAsync();
        var manager = await RemoteKit.MemberAsync(api, owner, "pj-manager");
        var id = (await ProjectKit.CreateProjectAsync(owner, "Managed")).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{manager.Id}", new { role = "manager" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await manager.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Renamed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Renamed", manager.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Hijack"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Renamed", member.Id))).StatusCode);
        Assert.Equal(member.Id, (await owner.GetJsonAsync($"/api/projects/{id}")).GetProperty("ownerUserId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_archived_project_is_read_only_until_it_is_restored()
    {
        var user = await api.NewClient().SignedUpAsync("pj-arch");
        var id = (await ProjectKit.CreateProjectAsync(user, "Old")).GetProperty("id").GetGuid();
        (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).EnsureSuccessStatusCode();

        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await user.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Old"))));
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await user.PostAsync($"/api/projects/{id}/applications", ProjectKit.Application("Shop"))));
        Assert.Equal("archived", (await user.GetJsonAsync($"/api/projects/{id}")).GetProperty("status").GetString());

        (await user.PostAsync($"/api/projects/{id}/restore")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Old"))).StatusCode);
    }

    [Fact]
    public async Task Project_members_get_a_role_only_if_they_belong_to_the_workspace_and_leave_when_removed()
    {
        var (owner, member, viewer) = await TeamAsync();
        var stranger = await api.NewClient().SignedUpAsync("pj-mstranger");
        var id = (await ProjectKit.CreateProjectAsync(owner, "Team")).GetProperty("id").GetGuid();

        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{member.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{member.Id}", new { role = "reviewer" })).EnsureSuccessStatusCode();
        Assert.Equal(("userId", "not_a_member"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{stranger.Id}", new { role = "viewer" })));
        Assert.Equal(("role", "invalid_role"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{viewer.Id}", new { role = "boss" })));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{viewer.Id}", new { role = "viewer" })).StatusCode);

        var listed = await viewer.GetJsonAsync($"/api/projects/{id}/members");
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal((member.Id, "reviewer"), (row.GetProperty("userId").GetGuid(), row.GetProperty("role").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/projects/{id}/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Delete, $"/api/projects/{id}/members/{member.Id}")).StatusCode);
        Assert.Empty((await owner.GetJsonAsync($"/api/projects/{id}/members")).EnumerateArray());
    }

    [Fact]
    public async Task A_person_removed_from_the_workspace_loses_the_project_at_once_even_as_its_owner()
    {
        var (owner, member, _) = await TeamAsync();
        var id = (await ProjectKit.CreateProjectAsync(owner, "Leaver", member.Id)).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync($"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Mine"))).StatusCode);
    }

    [Fact]
    public async Task Changes_are_audited_by_id_and_never_carry_the_typed_text()
    {
        var user = await api.NewClient().SignedUpAsync("pj-audit");
        var id = (await ProjectKit.CreateProjectAsync(user, "Secret roadmap")).GetProperty("id").GetGuid();
        (await user.SendAsync(HttpMethod.Put, $"/api/projects/{id}", ProjectKit.Project("Secret roadmap v2"))).EnsureSuccessStatusCode();
        (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).EnsureSuccessStatusCode();
        (await user.PostAsync($"/api/projects/{id}/restore")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "project" && a.TargetId == id.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["project.created", "project.updated", "project.archived", "project.restored"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal((user.WorkspaceId, user.Id), (r.WorkspaceId, r.ActorUserId)));
        Assert.All(rows, r => Assert.DoesNotContain("Secret", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
    }
}
