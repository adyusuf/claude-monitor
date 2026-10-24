using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The server-wide page on hosts started without what it needs (ADR-0017): no integration key means nothing is saved, no admin workspace means nobody gets in.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsUnsetTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private async Task<(int Saved, int Audited)> CountsAsync(TestUser admin)
    {
        await using var db = api.Db();
        return (await db.ServerSettings.CountAsync(), await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged));
    }

    // ---- no AIPOD_INTEGRATION_KEY ---------------------------------------------------------------------------------

    [Fact]
    public async Task Without_an_integration_key_a_save_is_refused_as_integration_key_missing_and_nothing_is_saved_or_audited()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "su-nokey");
        await using var host = HostKit.WithConfig(api, c => c with { IntegrationKey = null });
        var client = await HostKit.SignInAsync(api, host, admin);

        foreach (var values in new object[] { new { host = "mail.example.net" }, new { password = "s3cret-mail-pw" }, new { host = "mail.example.net", password = "s3cret-mail-pw" } })
        {
            var response = await ServerAdminKit.PutAsync(client, "smtp", values);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // never a 500 from sealing with no key
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("integration_key_missing", body.GetProperty("errors").GetProperty("values")[0].GetString());
        }

        Assert.Equal((0, 0), await CountsAsync(admin));
    }

    [Fact]
    public async Task Without_an_integration_key_the_page_says_it_cannot_save_and_a_person_outside_the_admin_workspace_still_gets_404_first()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "su-nokey-view");
        var outsider = await api.NewClient().SignedUpAsync("su-nokey-out");
        await using var host = HostKit.WithConfig(api, c => c with { IntegrationKey = null });
        var client = await HostKit.SignInAsync(api, host, admin);
        var other = await HostKit.SignInAsync(api, host, outsider);

        var view = await client.GetJsonAsync("/api/server/integrations");

        Assert.False(view.GetProperty("canSave").GetBoolean());
        Assert.Equal(["integration_key_missing"], ServerAdminKit.IntegrationOf(view, "jira").GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(other, "smtp", new { host = "mail.example.net" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(client, "slack", new { host = "x" })).StatusCode); // an unknown integration is 404 before the key is looked at
    }

    // ---- no AIPOD_ADMIN_WORKSPACE ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Admin)]
    public async Task Without_an_admin_workspace_nobody_reaches_the_page_not_even_an_owner_of_the_workspace_it_would_have_been(string role)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "su-noadmin-" + role, role); // a member of the workspace the main host names
        await using var host = HostKit.WithConfig(api, c => c with { AdminWorkspace = null });
        var client = await HostKit.SignInAsync(api, host, admin);
        var mailsBefore = api.Mail.Sent.Count;

        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.GetAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(client, "smtp", new { host = "mail.example.net" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/server/integrations/smtp/test")).StatusCode);

        Assert.False((await client.GetJsonAsync("/api/me")).GetProperty("serverAdmin").GetBoolean());
        Assert.Equal((0, 0), await CountsAsync(admin));
        Assert.Equal(mailsBefore, api.Mail.Sent.Count);
        // The same person is an administrator on the host that names the workspace: it is the missing variable that closes the page.
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.GetAsync(admin)).StatusCode);
    }
}
