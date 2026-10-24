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

/// <summary>Edges of the server-wide page (ADR-0017): a test mail for a person with no address, removing a saved value the environment hides, and values measured in UTF-8 bytes.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsEdgeTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private async Task<ServerSetting?> RowAsync(string key)
    {
        await using var db = api.Db();
        return await db.ServerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key);
    }

    private async Task<int> AuditedAsync(TestUser admin)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged);
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty(field)[0].GetString();
    }

    // ---- no_email -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_test_mail_for_an_admin_whose_account_has_no_address_is_refused_as_no_email_and_nothing_is_sent()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "se-noemail");
        await using (var db = api.Db())
        {
            // As after a provider sign-in that gave no address.
            await db.Users.Where(u => u.Id == admin.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, (string?)null).SetProperty(u => u.EmailNormalized, (string?)null));
        }

        var mailsBefore = api.Mail.Sent.Count;
        var response = await admin.PostAsync("/api/server/integrations/smtp/test");

        Assert.Equal("no_email", await ErrorOfAsync(response, "email"));
        Assert.Equal(mailsBefore, api.Mail.Sent.Count);
    }

    // ---- a saved value the environment hides ----------------------------------------------------------------------

    [Fact]
    public async Task A_saved_value_the_environment_now_hides_can_be_removed_but_not_replaced()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "se-hidden");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { user = "page-user" })).EnsureSuccessStatusCode(); // saved while the environment gave none
        await using var host = HostKit.WithConfig(api, c => HostKit.WithEnv(c, "smtp.user", "env-user"));
        var client = await HostKit.SignInAsync(api, host, admin);
        var view = await client.GetJsonAsync("/api/server/integrations");
        var shown = ServerAdminKit.FieldOf(view, "smtp", "user");
        Assert.Equal(("server", "env-user"), (shown.GetProperty("source").GetString(), shown.GetProperty("value").GetString())); // the environment wins
        Assert.Equal("page-user", (await RowAsync("smtp.user"))!.Value); // the row is still there, hidden
        var audited = await AuditedAsync(admin);

        Assert.Equal("set_on_server", await ErrorOfAsync(await ServerAdminKit.PutAsync(client, "smtp", new { user = "x" }), "user"));
        Assert.Equal("page-user", (await RowAsync("smtp.user"))!.Value);
        Assert.Equal(audited, await AuditedAsync(admin));

        var removed = await ServerAdminKit.PutAsync(client, "smtp", new { user = "" });

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(await RowAsync("smtp.user"));
        var after = ServerAdminKit.FieldOf(await removed.Content.ReadFromJsonAsync<JsonElement>(), "smtp", "user");
        Assert.Equal(("server", "env-user"), (after.GetProperty("source").GetString(), after.GetProperty("value").GetString())); // still the environment's
        Assert.Equal(audited + 1, await AuditedAsync(admin));
        // Nothing left to remove is not an error either, and a blank counts as empty.
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(client, "smtp", new { user = "   " })).StatusCode);
    }

    [Fact]
    public async Task Removing_a_hidden_value_next_to_a_valid_one_saves_both_and_a_refused_value_in_the_same_request_saves_neither()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "se-hidden-mix");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { user = "page-user", host = "page.example.net" })).EnsureSuccessStatusCode();
        await using var host = HostKit.WithConfig(api, c => HostKit.WithEnv(c, "smtp.user", "env-user"));
        var client = await HostKit.SignInAsync(api, host, admin);

        Assert.Equal("set_on_server", await ErrorOfAsync(await ServerAdminKit.PutAsync(client, "smtp", new { user = "x", host = "other.example.net" }), "user"));
        Assert.Equal(("page-user", "page.example.net"), ((await RowAsync("smtp.user"))!.Value, (await RowAsync("smtp.host"))!.Value));

        (await ServerAdminKit.PutAsync(client, "smtp", new { user = "", host = "other.example.net" })).EnsureSuccessStatusCode();

        Assert.Null(await RowAsync("smtp.user"));
        Assert.Equal("other.example.net", (await RowAsync("smtp.host"))!.Value);
    }

    // ---- values are limited in bytes ------------------------------------------------------------------------------

    [Fact]
    public async Task A_value_of_1000_three_byte_characters_is_refused_as_invalid_value_not_a_500_and_nothing_is_saved()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "se-bytes");
        var tooMuch = new string('€', IntegrationCatalog.ValueMax); // 1000 characters, 3000 bytes: sealed it would not fit the column

        Assert.Equal("invalid_value", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { password = tooMuch }), "password"));
        Assert.Equal("invalid_value", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { user = tooMuch }), "user"));
        Assert.Equal("invalid_value", await ErrorOfAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { password = new string('€', 967) }), "password")); // 2901 bytes

        await using var db = api.Db();
        Assert.Equal(0, await db.ServerSettings.CountAsync());
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged));
    }

    [Theory]
    [InlineData(900)] // 2700 bytes
    [InlineData(966)] // 2898 bytes: the most that fits
    public async Task A_secret_of_many_multi_byte_characters_is_saved_sealed_and_opens_to_the_same_text(int characters)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "se-bytes-ok-" + characters);
        var text = new string('€', characters);

        var response = await ServerAdminKit.PutAsync(admin, "smtp", new { password = text });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = (await RowAsync("smtp.password"))!;
        Assert.True(row.Secret);
        Assert.InRange(row.Value.Length, 1, 4000);
        Assert.Equal(text, Encoding.UTF8.GetString(SecretBox.Open(api.Services.GetRequiredService<ApiConfig>().IntegrationKey!, row.Value)));
        Assert.Equal(text, api.Services.GetRequiredService<IntegrationSettings>().Smtp.Password); // and it is the value in force
        Assert.DoesNotContain(text, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
