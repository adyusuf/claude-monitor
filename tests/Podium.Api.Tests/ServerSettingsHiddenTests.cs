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

/// <summary>The page tells a saved value that the environment now hides (savedHidden, ADR-0017) so that it can be removed: true only for source "server" with a saved row.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsHiddenTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private static (string Source, bool Hidden, JsonValueKind Value) Of(JsonElement view, string id, string name)
    {
        var field = ServerAdminKit.FieldOf(view, id, name);
        return (field.GetProperty("source").GetString()!, field.GetProperty("savedHidden").GetBoolean(), field.GetProperty("value").ValueKind);
    }

    private async Task SaveRowAsync(TestUser by, string key, string value, bool secret)
    {
        await using (var db = api.Db())
        {
            db.ServerSettings.Add(new ServerSetting
            {
                Key = key,
                Value = secret ? SecretBox.Seal(api.Services.GetRequiredService<ApiConfig>().IntegrationKey!, Encoding.UTF8.GetBytes(value)) : value,
                Secret = secret,
                UpdatedAt = api.Clock.GetUtcNow(),
                UpdatedBy = by.Id,
            });
            await db.SaveChangesAsync();
        }

        await api.Services.GetRequiredService<IntegrationSettings>().RefreshAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_saved_value_the_environment_hides_is_marked_and_every_other_field_is_not()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sh-mark");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { user = "page-user", host = "page.example.net" })).EnsureSuccessStatusCode();
        await using var host = HostKit.WithConfig(api, c => HostKit.WithEnv(HostKit.WithEnv(c, "smtp.user", "env-user"), "smtp.port", "2525"));
        var client = await HostKit.SignInAsync(api, host, admin);

        var view = await client.GetJsonAsync("/api/server/integrations");

        Assert.Equal(("server", true, JsonValueKind.String), Of(view, "smtp", "user")); // the environment's value is shown, a saved one is hidden
        Assert.Equal(("server", false, JsonValueKind.String), Of(view, "smtp", "port")); // the environment's, nothing saved
        Assert.Equal(("page", false, JsonValueKind.String), Of(view, "smtp", "host")); // saved and in force
        Assert.Equal(("none", false, JsonValueKind.String), Of(view, "smtp", "tls")); // the development fallback
        Assert.Equal(("none", false, JsonValueKind.Null), Of(view, "smtp", "password"));
        Assert.Equal(("server", false, JsonValueKind.String), Of(view, "github", "client_id"));

        // Removing it clears the mark in the answer to the same request and in the next read.
        var removed = await ServerAdminKit.PutAsync(client, "smtp", new { user = "" });
        removed.EnsureSuccessStatusCode();
        Assert.False(Of(await removed.Content.ReadFromJsonAsync<JsonElement>(), "smtp", "user").Hidden);
        Assert.Equal(("server", false, JsonValueKind.String), Of(await client.GetJsonAsync("/api/server/integrations"), "smtp", "user"));
    }

    [Fact]
    public async Task A_saved_secret_the_environment_hides_is_marked_too_and_its_value_is_still_not_sent()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sh-secret");
        await SaveRowAsync(admin, "github.client_secret", "hidden-saved-secret", secret: true); // the test environment gives GitHub's app
        await SaveRowAsync(admin, "smtp.password", "page-password", secret: true); // nothing in the environment for this one

        var response = await ServerAdminKit.GetAsync(admin);

        var text = await response.Content.ReadAsStringAsync();
        var view = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(("server", true, JsonValueKind.Null), Of(view, "github", "client_secret"));
        Assert.Equal(("server", false, JsonValueKind.String), Of(view, "github", "client_id"));
        Assert.Equal(("page", false, JsonValueKind.Null), Of(view, "smtp", "password"));
        Assert.DoesNotContain("hidden-saved-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("page-password", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mark_goes_when_the_row_is_removed_even_for_a_secret()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sh-gone");
        await SaveRowAsync(admin, "google.client_secret", "hidden-saved-secret", secret: true);
        Assert.True(Of(await admin.GetJsonAsync("/api/server/integrations"), "google", "client_secret").Hidden);

        var removed = await ServerAdminKit.PutAsync(admin, "google", new { client_secret = "" });

        Assert.Equal(("server", false, JsonValueKind.Null), Of(await removed.Content.ReadFromJsonAsync<JsonElement>(), "google", "client_secret"));
        await using var db = api.Db();
        Assert.False(await db.ServerSettings.AnyAsync(s => s.Key == "google.client_secret"));
    }
}
