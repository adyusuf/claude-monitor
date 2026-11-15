using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// A name is one line of text (Http.IsName): a control character (a line break, NUL, a tab) is refused, so an inviter's display
/// name or a workspace name cannot put its own lines into the invitation mail ahead of the real link (F-174).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class NameControlTests(ApiFactory api)
{
    public static TheoryData<string> Names => new() { "Name\nOpen this link instead", "Name\r\nX", "Na\0me", "Na\tme", "Name\u0085" };

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invalid_name", errors.GetProperty(field)[0].GetString());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_display_name_with_a_control_character_is_refused_at_registration(string name)
    {
        var email = Emails.New("ctl-reg");
        var before = api.Mail.Sent.Count;

        var response = await api.NewClient().PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = name });

        await AssertInvalidAsync(response, "displayName");
        Assert.Equal(before, api.Mail.Sent.Count);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_workspace_name_with_a_control_character_is_refused_on_create_and_on_rename(string name)
    {
        var owner = await api.NewClient().SignedUpAsync("ctl-ws");

        await AssertInvalidAsync(await owner.PostAsync("/api/workspaces", new { name }), "name");
        await AssertInvalidAsync(await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}", new { name }), "name");
    }

    [Fact]
    public async Task A_name_with_letters_of_any_language_and_inner_spaces_is_still_accepted()
    {
        var owner = await api.NewClient().SignedUpAsync("ctl-ok", "  Örnek Şişman  ");

        var created = await owner.PostAsync("/api/workspaces", new { name = "İstanbul ekibi — ön yüz" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
