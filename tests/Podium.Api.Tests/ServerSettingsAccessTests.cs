using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Mail;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Who reaches the server-wide Settings → Integrations page (ADR-0017), what it shows, and the test mail.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsAccessTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));

    private async Task<int> SavedAsync()
    {
        await using var db = api.Db();
        return await db.ServerSettings.CountAsync();
    }

    [Fact]
    public async Task Everyone_outside_the_admin_workspaces_owners_and_admins_gets_404_on_all_three_calls_and_nothing_happens()
    {
        var member = await ServerAdminKit.AdminAsync(api, "ss-member", Roles.Member);
        var viewer = await ServerAdminKit.AdminAsync(api, "ss-viewer", Roles.Viewer);
        var removed = await ServerAdminKit.AdminAsync(api, "ss-removed");
        await ServerAdminKit.RemoveAsync(api, removed);
        var outsider = await api.NewClient().SignedUpAsync("ss-outsider"); // owner of a workspace of their own, not of the admin one
        var mailsBefore = api.Mail.Sent.Count;

        foreach (var who in new[] { member, viewer, removed, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.GetAsync(who)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(who, "smtp", new { host = "mail.example.net" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.PostAsync("/api/server/integrations/smtp/test")).StatusCode);
        }

        Assert.Equal(0, await SavedAsync());
        Assert.Equal(mailsBefore, api.Mail.Sent.Count); // no test mail went out
    }

    [Fact]
    public async Task Somebody_who_is_not_signed_in_is_refused()
    {
        var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await ServerAdminKit.GetAsync(anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ServerAdminKit.PutAsync(anonymous, "smtp", new { host = "mail.example.net" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/server/integrations/smtp/test")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Owner)]
    public async Task An_admin_or_owner_of_the_admin_workspace_sees_the_page_and_me_says_so(string role)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-seen", role);
        var plain = await api.NewClient().SignedUpAsync("ss-plain");

        var response = await ServerAdminKit.GetAsync(admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await admin.GetJsonAsync("/api/me")).GetProperty("serverAdmin").GetBoolean());
        Assert.False((await plain.GetJsonAsync("/api/me")).GetProperty("serverAdmin").GetBoolean());
    }

    [Theory]
    [InlineData(Roles.Member)]
    [InlineData(Roles.Viewer)]
    public async Task A_member_or_viewer_of_the_admin_workspace_is_not_a_server_admin_in_me(string role)
    {
        var user = await ServerAdminKit.AdminAsync(api, "ss-me-" + role, role);

        Assert.False((await user.GetJsonAsync("/api/me")).GetProperty("serverAdmin").GetBoolean());
    }

    [Fact]
    public async Task Somebody_removed_from_the_admin_workspace_is_no_longer_a_server_admin()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-gone");
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.GetAsync(admin)).StatusCode);

        await ServerAdminKit.RemoveAsync(api, admin);

        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.GetAsync(admin)).StatusCode);
        Assert.False((await admin.GetJsonAsync("/api/me")).GetProperty("serverAdmin").GetBoolean());
    }

    [Fact]
    public async Task The_view_lists_the_four_integrations_with_their_statuses_callback_addresses_and_no_secret_value()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-view");

        var view = await admin.GetJsonAsync("/api/server/integrations");

        Assert.True(view.GetProperty("canSave").GetBoolean());
        Assert.Equal(["smtp", "github", "google", "jira"], view.GetProperty("integrations").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.All(new[] { "github", "google", "jira" }, id => Assert.Equal("ready", ServerAdminKit.IntegrationOf(view, id).GetProperty("status").GetString()));
        Assert.Equal("ready", ServerAdminKit.IntegrationOf(view, "smtp").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, ServerAdminKit.IntegrationOf(view, "smtp").GetProperty("callbackUrl").ValueKind);
        Assert.Equal(ApiFactory.Origin + "/api/auth/callback/github", ServerAdminKit.IntegrationOf(view, "github").GetProperty("callbackUrl").GetString());
        Assert.Equal(ApiFactory.Origin + "/api/auth/callback/google", ServerAdminKit.IntegrationOf(view, "google").GetProperty("callbackUrl").GetString());
        Assert.Equal(ApiFactory.Origin + "/api/integrations/jira/callback", ServerAdminKit.IntegrationOf(view, "jira").GetProperty("callbackUrl").GetString());
        Assert.Empty(ServerAdminKit.IntegrationOf(view, "jira").GetProperty("notes").EnumerateArray());
        // The test environment gives the sign-in apps and Jira through the environment: source "server", and a secret's value is never sent.
        var id = ServerAdminKit.FieldOf(view, "github", "client_id");
        Assert.Equal(("server", "gh-id", "AIPOD_GITHUB_CLIENT_ID", false), (id.GetProperty("source").GetString(), id.GetProperty("value").GetString(),
            id.GetProperty("envName").GetString(), id.GetProperty("secret").GetBoolean()));
        var secret = ServerAdminKit.FieldOf(view, "github", "client_secret");
        Assert.Equal(("server", true), (secret.GetProperty("source").GetString(), secret.GetProperty("secret").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
        Assert.DoesNotContain("gh-secret", await (await ServerAdminKit.GetAsync(admin)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mail_settings_show_the_development_values_in_force_and_where_they_come_from()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-smtp-view");

        var view = await admin.GetJsonAsync("/api/server/integrations");

        (string?, string?) Of(string name) => (ServerAdminKit.FieldOf(view, "smtp", name).GetProperty("source").GetString(),
            ServerAdminKit.FieldOf(view, "smtp", name).GetProperty("value").GetString());
        Assert.Equal(("none", "localhost"), Of("host"));
        Assert.Equal(("none", "1025"), Of("port"));
        Assert.Equal(("none", "podium@localhost"), Of("from"));
        Assert.Equal(("none", "false"), Of("tls"));
        Assert.Equal(("none", null), Of("user"));
        Assert.Equal(("none", null), Of("password"));
        Assert.Equal((false, false, true, true), (ServerAdminKit.FieldOf(view, "smtp", "user").GetProperty("required").GetBoolean(),
            ServerAdminKit.FieldOf(view, "smtp", "tls").GetProperty("required").GetBoolean(),
            ServerAdminKit.FieldOf(view, "smtp", "host").GetProperty("required").GetBoolean(),
            ServerAdminKit.FieldOf(view, "smtp", "password").GetProperty("secret").GetBoolean()));
    }

    [Fact]
    public async Task The_test_mail_goes_to_the_callers_own_address_and_only_to_it()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-mail");
        var other = await ServerAdminKit.AdminAsync(api, "ss-mail-other");
        var before = (MailsTo(admin.Email), MailsTo(other.Email));

        var response = await admin.PostAsync("/api/server/integrations/smtp/test");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((before.Item1 + 1, before.Item2), (MailsTo(admin.Email), MailsTo(other.Email)));
        var mail = api.Mail.Sent.Last(m => string.Equals(m.To, admin.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MailTemplates.Build("en", MailTemplates.Test, admin.Email).Subject, mail.Subject);
    }

    [Fact]
    public async Task A_mail_server_that_is_down_answers_mail_failed_without_a_detail_of_the_failure()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-down");
        var mailer = new FlakyMailer(api.Mail) { Down = true };
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var client = JiraKit.ClientOf(api, host);
        (await client.PostAsync("/api/auth/login", new { email = admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        var before = MailsTo(admin.Email);

        var down = await client.PostAsync("/api/server/integrations/smtp/test");

        Assert.Equal(HttpStatusCode.BadRequest, down.StatusCode);
        var body = await down.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mail_failed", body.GetProperty("errors").GetProperty("smtp")[0].GetString());
        Assert.DoesNotContain("the mail server is down", body.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, MailsTo(admin.Email));
        mailer.Down = false;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/server/integrations/smtp/test")).StatusCode);
        Assert.Equal(before + 1, MailsTo(admin.Email));
    }

    [Fact]
    public async Task The_sign_in_providers_offered_follow_the_apps_in_force_and_stay_github_and_google()
    {
        var client = api.NewClient();

        var available = await client.GetJsonAsync("/api/auth/providers");

        Assert.Equal(["github", "google"], available.GetProperty("available").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.Redirect, (await client.Http.GetAsync("/api/auth/external/google")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/external/jira")).StatusCode);
    }
}
