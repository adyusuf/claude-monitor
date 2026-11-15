using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A name a provider supplies goes through the same rule as a typed one (F-041): one that fails it is replaced by the e-mail address.</summary>
[Collection(ApiGroup.Name)]
public sealed class ExternalAuthNameTests(ApiFactory api)
{
    private async Task<(string DisplayName, string WorkspaceName, string Email)> SignUpAsync(string tag, string name)
    {
        var client = api.NewClient();
        var email = Emails.New(tag);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin");
        request.Headers.Add(TestExternal.Header, $"gh-{Guid.NewGuid()}|{email}|true|{name}");
        Assert.Equal(ApiFactory.Origin + "/", (await client.Http.SendAsync(request)).Headers.Location!.ToString());
        var me = await client.GetJsonAsync("/api/me");
        var id = me.GetProperty("id").GetGuid();
        await using var db = api.Db();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        var workspace = await db.WorkspaceMembers.AsNoTracking().Where(m => m.UserId == id)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (_, w) => w.Name).SingleAsync();
        return (user.DisplayName, workspace, email);
    }

    [Fact]
    public async Task A_whitespace_only_provider_name_falls_back_to_the_email()
    {
        var (display, workspace, email) = await SignUpAsync("gh-blank-name", "     ");

        Assert.Equal(email, display);
        Assert.Equal(email, workspace);
    }

    [Fact]
    public async Task A_provider_name_over_the_limit_falls_back_to_the_email()
    {
        var (display, workspace, email) = await SignUpAsync("gh-long-name", new string('x', 101));

        Assert.Equal(email, display);
        Assert.Equal(email, workspace);
    }

    [Fact]
    public async Task A_valid_provider_name_is_kept_trimmed()
    {
        var (display, workspace, _) = await SignUpAsync("gh-ok-name", "  Provider Person  ");

        Assert.Equal("Provider Person", display);
        Assert.Equal("Provider Person", workspace);
    }
}
