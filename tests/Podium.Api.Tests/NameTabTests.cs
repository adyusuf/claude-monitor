using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// F-174 (pin): a tab is a control character, so every field checked with <see cref="Http.IsName"/> refuses it, whatever its length limit:
/// display and workspace names (NameControlTests), and here project, stakeholder, application, tech-stack, target, component, release,
/// issue and test-case names and a machine's host name and arch. A provider's name with a tab is replaced by the address.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class NameTabTests(ApiFactory api)
{
    private const string Tabbed = "Na\tme";

    private static JsonObject With(object body, string field, JsonNode? value)
    {
        var node = JsonSerializer.SerializeToNode(body, TestUser.Json)!.AsObject();
        node[field] = value;
        return node;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field, string code)
    {
        Assert.Equal((field, code), await ProjectKit.ProblemAsync(response));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(255)]
    [InlineData(300)]
    public void Is_name_refuses_a_tab_at_every_length_limit(int max)
    {
        Assert.False(Http.IsName(Tabbed, max));
        Assert.False(Http.IsName("\tName", max));
        Assert.True(Http.IsName("Na me", max));
    }

    [Fact]
    public async Task Project_and_application_fields_refuse_a_tab()
    {
        var owner = await api.NewClient().SignedUpAsync("tab-project");
        var projects = $"/api/workspaces/{owner.WorkspaceId}/projects";
        await AssertRefusedAsync(await owner.PostAsync(projects, With(ProjectKit.Project("P"), "name", Tabbed)), "name", "invalid_name");
        await AssertRefusedAsync(await owner.PostAsync(projects, With(ProjectKit.Project("P"), "stakeholders",
            new JsonArray(new JsonObject { ["name"] = Tabbed, ["role"] = "Reviewer" }))), "stakeholders", "invalid_list");

        var project = (await ProjectKit.CreateProjectAsync(owner, "Tabs")).GetProperty("id").GetGuid();
        var applications = $"/api/projects/{project}/applications";
        await AssertRefusedAsync(await owner.PostAsync(applications, With(ProjectKit.Application("A"), "name", Tabbed)), "name", "invalid_name");
        await AssertRefusedAsync(await owner.PostAsync(applications, With(ProjectKit.Application("A"), "techStack", new JsonArray(Tabbed))),
            "techStack", "invalid_list");
        await AssertRefusedAsync(await owner.PostAsync(applications, With(ProjectKit.Application("A"), "nonFunctionalTargets",
            new JsonArray(new JsonObject { ["kind"] = "sla", ["target"] = Tabbed }))), "nonFunctionalTargets", "invalid_list");

        var app = (await ProjectKit.CreateApplicationAsync(owner, project, "Web")).GetProperty("id").GetGuid();
        await AssertRefusedAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = Tabbed }), "name", "invalid_name");
        await AssertRefusedAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = Tabbed }), "name", "invalid_name");
    }

    [Fact]
    public async Task Issue_and_test_case_titles_refuse_a_tab()
    {
        var owner = await api.NewClient().SignedUpAsync("tab-issue");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Tab issues")).GetProperty("id").GetGuid();
        await AssertRefusedAsync(await owner.PostAsync($"/api/projects/{project}/issues", new { title = Tabbed, type = "story" }), "title", "invalid_name");

        var issue = await IssueKit.IssueAsync(owner, project, "Plain");
        await AssertRefusedAsync(await owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = Tabbed }), "title", "invalid_name");
    }

    [Theory]
    [InlineData("laptop\t1", "arm64", "hostname")]
    [InlineData("laptop-1", "arm\t64", "arch")]
    public async Task A_machine_host_name_or_arch_with_a_tab_is_refused(string hostname, string arch, string field)
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/device/code",
            new DeviceCodeRequest("machine-" + Guid.NewGuid().ToString("N"), hostname, OsKinds.MacOs, arch, "0.3.0"), TestUser.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_provider_name_with_a_tab_is_replaced_by_the_address()
    {
        var client = api.NewClient();
        var email = Emails.New("tab-provider");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin");
        request.Headers.Add(TestExternal.Header, $"gh-{Guid.NewGuid()}|{email}|true|Provider\tPerson");
        Assert.Equal(HttpStatusCode.Redirect, (await client.Http.SendAsync(request)).StatusCode);

        Assert.Equal(email, (await client.GetJsonAsync("/api/me")).GetProperty("displayName").GetString());
    }
}
