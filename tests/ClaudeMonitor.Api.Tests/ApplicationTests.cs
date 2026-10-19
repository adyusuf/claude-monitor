using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Applications of a managed project (ADR-0011): the definition, who may change it and what is refused.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApplicationTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Contributor, TestUser Reviewer, TestUser Viewer, Guid Project)> SetUpAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("ap-owner");
        var contributor = await RemoteKit.MemberAsync(api, owner, "ap-contrib");
        var reviewer = await RemoteKit.MemberAsync(api, owner, "ap-review");
        var viewer = await RemoteKit.MemberAsync(api, owner, "ap-viewer", "viewer");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Shop")).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{contributor.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{reviewer.Id}", new { role = "reviewer" })).EnsureSuccessStatusCode();
        return (owner, contributor, reviewer, viewer, project);
    }

    [Fact]
    public async Task An_application_keeps_every_field_and_its_technical_owner_is_named()
    {
        var (owner, contributor, _, _, project) = await SetUpAsync();

        var created = await ProjectKit.CreateApplicationAsync(owner, project, "Storefront", contributor.Id);
        var read = await owner.GetJsonAsync($"/api/applications/{created.GetProperty("id").GetGuid()}");

        Assert.Equal(("Storefront", "The storefront", "web", "https://git.example.invalid/team/shop.git"),
            (read.GetProperty("name").GetString(), read.GetProperty("description").GetString(), read.GetProperty("kind").GetString(), read.GetProperty("repoUrl").GetString()));
        Assert.Equal(["dev", "prod"], read.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal("https://dev.shop.example.invalid", read.GetProperty("environments")[0].GetProperty("url").GetString());
        Assert.Equal(["React", ".NET"], read.GetProperty("techStack").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(("semver", contributor.Id, "Test Person"), (read.GetProperty("versioningScheme").GetString(), read.GetProperty("techOwnerUserId").GetGuid(), read.GetProperty("techOwnerName").GetString()));
        Assert.Equal(("sla", "99.9% a month"), (read.GetProperty("nonFunctionalTargets")[0].GetProperty("kind").GetString(), read.GetProperty("nonFunctionalTargets")[0].GetProperty("target").GetString()));
        Assert.Equal(("active", project), (read.GetProperty("status").GetString(), read.GetProperty("projectId").GetGuid()));
    }

    [Fact]
    public async Task A_project_may_have_no_application_at_all_and_the_list_pages_and_hides_archived_ones()
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        Assert.Empty((await owner.GetJsonAsync($"/api/projects/{project}/applications")).GetProperty("items").EnumerateArray());
        foreach (var name in new[] { "One", "Two", "Three" }) await ProjectKit.CreateApplicationAsync(owner, project, name);
        var two = (await owner.GetJsonAsync($"/api/projects/{project}/applications?limit=10")).GetProperty("items").EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == "Two").GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{two}")).EnsureSuccessStatusCode();

        var first = await owner.GetJsonAsync($"/api/projects/{project}/applications?limit=1");
        var rest = await owner.GetJsonAsync($"/api/projects/{project}/applications?limit=5&cursor={first.GetProperty("next").GetString()}");
        var archived = await owner.GetJsonAsync($"/api/projects/{project}/applications?status=archived");

        Assert.Equal("Three", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(["One"], rest.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(two, Assert.Single(archived.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_contributor_works_on_applications_but_a_reviewer_a_workspace_viewer_and_a_stranger_cannot()
    {
        var (owner, contributor, reviewer, viewer, project) = await SetUpAsync();
        var stranger = await api.NewClient().SignedUpAsync("ap-stranger");

        var made = await ProjectKit.CreateApplicationAsync(contributor, project, "By contributor");
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await contributor.SendAsync(HttpMethod.Put, $"/api/applications/{id}", ProjectKit.Application("Renamed"))).StatusCode);
        Assert.True((await contributor.GetJsonAsync($"/api/applications/{id}")).GetProperty("canEdit").GetBoolean());

        foreach (var reader in new[] { reviewer, viewer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/projects/{project}/applications", ProjectKit.Application("Nope"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/applications/{id}", ProjectKit.Application("Nope"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/applications/{id}")).StatusCode);
            Assert.False((await reader.GetJsonAsync($"/api/applications/{id}")).GetProperty("canEdit").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/applications/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/projects/{project}/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Put, $"/api/applications/{id}", ProjectKit.Application("Nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/projects/{project}/applications", ProjectKit.Application("By owner"))).StatusCode);
    }

    [Theory]
    [InlineData("https://git.example.invalid/team/shop.git", true)]
    [InlineData("ssh://git@git.example.invalid/team/shop.git", true)]
    [InlineData("git://git.example.invalid/team/shop.git", true)]
    [InlineData("git@git.example.invalid:team/shop.git", true)]
    [InlineData("https://user:secret@git.example.invalid/team/shop.git", false)] // a password would be shown to the whole workspace
    [InlineData("http://git.example.invalid/team/shop.git", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public async Task The_repository_link_is_a_git_address_without_a_password(string url, bool valid)
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        var body = JsonSerializer.SerializeToElement(ProjectKit.Application("Linked"), TestUser.Json).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body["repoUrl"] = url;

        var response = await owner.PostAsync($"/api/projects/{project}/applications", body);

        if (valid) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        else Assert.Equal(("repoUrl", "invalid_url"), await ProjectKit.ProblemAsync(response));
    }

    [Theory]
    [InlineData("environments", """[{"name":"staging"}]""")]
    [InlineData("environments", """[{"name":"dev"},{"name":"dev"}]""")]
    [InlineData("environments", """[{"name":"dev","url":"javascript:alert(1)"}]""")]
    [InlineData("environments", """[{"name":"dev","url":"https://user:pw@host.example.invalid"}]""")]
    [InlineData("techStack", """["React","react"]""")]
    [InlineData("techStack", """[" "]""")]
    [InlineData("nonFunctionalTargets", """[{"kind":"speed","target":"fast"}]""")]
    [InlineData("nonFunctionalTargets", """[{"kind":"sla","target":" "}]""")]
    public async Task Lists_with_a_bad_entry_are_refused(string field, string json)
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        var body = JsonSerializer.SerializeToElement(ProjectKit.Application("Bad"), TestUser.Json).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body[field] = JsonDocument.Parse(json).RootElement;

        Assert.Equal((field, "invalid_list"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", body)));
    }

    [Theory]
    [InlineData("kind", "desktop", "invalid_value")]
    [InlineData("versioningScheme", "date", "invalid_value")]
    [InlineData("name", " ", "invalid_name")]
    public async Task A_scalar_outside_its_closed_set_is_refused(string field, string value, string code)
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        var body = JsonSerializer.SerializeToElement(ProjectKit.Application("Bad"), TestUser.Json).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body[field] = value;

        Assert.Equal((field, code), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", body)));
    }

    [Fact]
    public async Task The_technical_owner_must_be_able_to_work_in_the_workspace()
    {
        var (owner, _, _, viewer, project) = await SetUpAsync();
        var stranger = await api.NewClient().SignedUpAsync("ap-tech");

        Assert.Equal(("techOwnerUserId", "not_a_member"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", ProjectKit.Application("A", stranger.Id))));
        Assert.Equal(("techOwnerUserId", "not_a_member"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", ProjectKit.Application("B", viewer.Id))));
    }

    [Fact]
    public async Task A_name_is_unique_within_a_project_not_across_projects_and_an_archived_one_can_come_back()
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        var other = (await ProjectKit.CreateProjectAsync(owner, "Other")).GetProperty("id").GetGuid();
        var first = (await ProjectKit.CreateApplicationAsync(owner, project, "Portal")).GetProperty("id").GetGuid();

        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", ProjectKit.Application("PORTAL"))));
        await ProjectKit.CreateApplicationAsync(owner, other, "Portal");

        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{first}")).EnsureSuccessStatusCode();
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/applications/{first}", ProjectKit.Application("Portal"))));
        await ProjectKit.CreateApplicationAsync(owner, project, "Portal");
        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{first}/restore")));
    }

    [Fact]
    public async Task Application_changes_are_audited_by_id_without_the_typed_text()
    {
        var (owner, _, _, _, project) = await SetUpAsync();
        var id = (await ProjectKit.CreateApplicationAsync(owner, project, "Hush app")).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Put, $"/api/applications/{id}", ProjectKit.Application("Hush app"))).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{id}")).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/applications/{id}/restore")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "application" && a.TargetId == id.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["application.created", "application.updated", "application.archived", "application.restored"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.DoesNotContain("Hush", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
    }
}
