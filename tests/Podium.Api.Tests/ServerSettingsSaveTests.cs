using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Saving the mail settings on the server-wide page (ADR-0017): sealing, sources, clearing, validation and the audit row.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsSaveTests(ApiFactory api) : IAsyncLifetime
{
    private const string Password = "s3cret-mail-pw-91";

    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private IntegrationSettings Settings => api.Services.GetRequiredService<IntegrationSettings>();

    private async Task<ServerSetting?> RowAsync(string key)
    {
        await using var db = api.Db();
        return await db.ServerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key);
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty(field)[0].GetString();
    }

    [Fact]
    public async Task Saved_mail_settings_take_effect_at_once_the_password_is_sealed_and_never_comes_back()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-save");
        var from = Emails.New("ss-from");

        var response = await ServerAdminKit.PutAsync(admin, "smtp", new { host = "mail.example.net", port = "2525", user = "mailer", password = Password, from, tls = "true" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        var view = JsonSerializer.Deserialize<JsonElement>(text);
        (string?, string?) Of(string name) => (ServerAdminKit.FieldOf(view, "smtp", name).GetProperty("source").GetString(),
            ServerAdminKit.FieldOf(view, "smtp", name).GetProperty("value").GetString());
        Assert.Equal(("page", "mail.example.net"), Of("host"));
        Assert.Equal(("page", "2525"), Of("port"));
        Assert.Equal(("page", "mailer"), Of("user"));
        Assert.Equal(("page", null), Of("password"));
        Assert.Equal(("page", from), Of("from"));
        Assert.Equal(("page", "true"), Of("tls"));
        Assert.Equal(new SmtpSettings("mail.example.net", 2525, "mailer", Password, from, true), Settings.Smtp); // in force without a restart
        var row = (await RowAsync("smtp.password"))!;
        Assert.True(row.Secret);
        Assert.NotEqual(Password, row.Value);
        Assert.Equal(Password, Encoding.UTF8.GetString(SecretBox.Open(api.Services.GetRequiredService<ApiConfig>().IntegrationKey!, row.Value)));
        var host = (await RowAsync("smtp.host"))!;
        Assert.Equal((false, admin.Id), (host.Secret, host.UpdatedBy));
        // The view stays the same on the next read.
        var again = await admin.GetJsonAsync("/api/server/integrations");
        Assert.Equal("page", ServerAdminKit.FieldOf(again, "smtp", "password").GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_audit_row_names_the_fields_that_changed_and_never_their_values()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-audit");

        (await ServerAdminKit.PutAsync(admin, "smtp", new { password = Password, user = "audited-user-name" })).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged);
        Assert.Equal("server.settings_changed", audit.Action);
        Assert.Equal((ApiFactory.AdminWorkspaceId, "integration", "smtp"), (audit.WorkspaceId, audit.TargetType, audit.TargetId));
        Assert.Equal(["password", "user"], audit.Detail!.RootElement.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        var raw = audit.Detail.RootElement.GetRawText();
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("audited-user-name", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_value_removes_the_saved_one_and_a_field_left_out_stays_as_it_is()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-clear");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { user = "mailer", password = Password, host = "mail.example.net" })).EnsureSuccessStatusCode();

        var cleared = await ServerAdminKit.PutAsync(admin, "smtp", new { user = "" });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var view = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", ServerAdminKit.FieldOf(view, "smtp", "user").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, ServerAdminKit.FieldOf(view, "smtp", "user").GetProperty("value").ValueKind);
        Assert.Null(await RowAsync("smtp.user"));
        Assert.Equal(["page", "page"], new[] { ServerAdminKit.FieldOf(view, "smtp", "password"), ServerAdminKit.FieldOf(view, "smtp", "host") }
            .Select(f => f.GetProperty("source").GetString()));
        Assert.Equal((null, Password, "mail.example.net"), (Settings.Smtp.User, Settings.Smtp.Password, Settings.Smtp.Host));
        // A JSON null clears as well, and clearing the secret removes its sealed row; the development fallback is back in force.
        (await ServerAdminKit.PutAsync(admin, "smtp", new Dictionary<string, string?> { ["password"] = null, ["host"] = "   " })).EnsureSuccessStatusCode();
        Assert.Null(await RowAsync("smtp.password"));
        Assert.Null(await RowAsync("smtp.host"));
        Assert.Equal(("localhost", (string?)null), (Settings.Smtp.Host, Settings.Smtp.Password));
    }

    [Fact]
    public async Task A_value_is_trimmed_and_a_second_save_replaces_the_first()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-trim");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { host = "  first.example.net  " })).EnsureSuccessStatusCode();
        Assert.Equal("first.example.net", (await RowAsync("smtp.host"))!.Value);

        (await ServerAdminKit.PutAsync(admin, "smtp", new { host = "second.example.net" })).EnsureSuccessStatusCode();

        Assert.Equal("second.example.net", Settings.Smtp.Host);
        await using var db = api.Db();
        Assert.Equal(1, await db.ServerSettings.CountAsync(s => s.Key == "smtp.host"));
    }

    [Theory]
    [InlineData("github", "client_id")]
    [InlineData("google", "client_secret")]
    [InlineData("jira", "client_id")]
    public async Task A_setting_the_environment_gives_cannot_be_changed_here_and_nothing_is_saved(string integration, string name)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-env-" + integration);

        var response = await ServerAdminKit.PutAsync(admin, integration, new Dictionary<string, string?> { [name] = "from-the-page" });

        Assert.Equal("set_on_server", await ErrorOfAsync(response, name));
        Assert.Null(await RowAsync($"{integration}.{name}"));
        Assert.Equal(0, await Audited(admin));
    }

    private async Task<int> Audited(TestUser admin)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged);
    }

    [Theory]
    [InlineData("port", "0")]
    [InlineData("port", "65536")]
    [InlineData("port", "-25")]
    [InlineData("port", "25.5")]
    [InlineData("port", "smtp")]
    [InlineData("tls", "yes")]
    [InlineData("tls", "TRUE")]
    [InlineData("from", "not-an-email")]
    [InlineData("host", "mail example.net")]
    public async Task A_value_the_setting_cannot_take_is_refused_as_invalid_value(string name, string value)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-bad");

        var response = await ServerAdminKit.PutAsync(admin, "smtp", new Dictionary<string, string?> { [name] = value });

        Assert.Equal("invalid_value", await ErrorOfAsync(response, name));
        Assert.Null(await RowAsync("smtp." + name));
        Assert.Equal(0, await Audited(admin));
    }

    [Fact]
    public async Task A_value_longer_than_the_limit_is_refused_and_the_longest_allowed_is_kept()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-long");

        Assert.Equal("invalid_value", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { user = new string('u', IntegrationCatalog.ValueMax + 1) }), "user"));
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(admin, "smtp", new { user = new string('u', IntegrationCatalog.ValueMax) })).StatusCode);
        Assert.Equal(IntegrationCatalog.ValueMax, (await RowAsync("smtp.user"))!.Value.Length);
    }

    [Theory]
    [InlineData("port", "1")]
    [InlineData("port", "65535")]
    [InlineData("tls", "false")]
    public async Task The_edges_of_the_valid_values_are_saved(string name, string value)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-edge");

        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(admin, "smtp", new Dictionary<string, string?> { [name] = value })).StatusCode);
        Assert.Equal(value, (await RowAsync("smtp." + name))!.Value);
    }

    [Fact]
    public async Task One_bad_value_saves_nothing_of_the_request()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-atomic");

        var response = await ServerAdminKit.PutAsync(admin, "smtp", new { host = "ok.example.net", port = "0", user = "mailer" });

        Assert.Equal("invalid_value", await ErrorOfAsync(response, "port"));
        await using var db = api.Db();
        Assert.Equal(0, await db.ServerSettings.CountAsync());
        Assert.Equal("localhost", Settings.Smtp.Host);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("client_id")] // a GitHub setting, not an SMTP one
    [InlineData("Host")]
    public async Task A_setting_the_integration_does_not_have_is_refused_as_invalid_setting(string name)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-unknown");

        var response = await ServerAdminKit.PutAsync(admin, "smtp", new Dictionary<string, string?> { [name] = "x" });

        Assert.Equal("invalid_setting", await ErrorOfAsync(response, name));
    }

    [Fact]
    public async Task A_request_with_no_values_is_invalid_and_an_unknown_integration_is_404()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-empty");

        Assert.Equal("invalid_setting", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", new Dictionary<string, string?>()), "values"));
        Assert.Equal("invalid_setting", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", null), "values"));
        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(admin, "slack", new { host = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ServerAdminKit.PutAsync(admin, "SMTP", new { host = "x" })).StatusCode);
    }
}
