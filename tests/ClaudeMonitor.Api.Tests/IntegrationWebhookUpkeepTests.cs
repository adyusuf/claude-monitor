using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Registering, extending and removing the webhooks at Jira (ADR-0015 §6): needs a public https address, and a site that refuses them keeps working on the pass alone.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationWebhookUpkeepTests(ApiFactory api) : IAsyncLifetime
{
    private const string PublicOrigin = "https://monitor.test.invalid";
    private WebApplicationFactory<Program>? host;

    public Task InitializeAsync()
    {
        host = JiraKit.With(api, c => c with { PublicOrigin = PublicOrigin });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (host is not null) await host.DisposeAsync();
    }

    /// <summary>The admin of the world, signed in at the host that has a public address (the world itself was made at the ordinary one).</summary>
    private async Task<TestUser> AdminAsync(JiraWorld w)
    {
        var admin = JiraKit.ClientOf(api, host!);
        (await admin.PostAsync("/api/auth/login", new { email = w.Admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        return admin;
    }

    private static async Task<JsonElement> ImportAsync(TestUser admin, JiraWorld w, params string[] keys)
    {
        var response = await admin.PostAsync($"{w.Base}/spaces/import", new { keys });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<long> Registered(JiraWorld w) => [.. w.Site.Webhooks.Select(h => h.Id).Order()];

    private async Task SetExpiryAsync(JiraWorld w, TimeSpan left)
    {
        await using var db = api.Db();
        var at = api.Clock.GetUtcNow() + left;
        await db.IntegrationConnections.Where(c => c.Id == w.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.WebhookExpiresAt, at));
    }

    [Fact]
    public async Task Importing_the_first_space_registers_the_issue_and_the_sprint_webhooks_at_the_public_address()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC").Issue("One"));
        var admin = await AdminAsync(w);

        await ImportAsync(admin, w, "ABC");

        var hooks = w.Site.Webhooks.OrderBy(h => h.Id).ToList();
        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, h => Assert.Equal($"{PublicOrigin}/api/integrations/jira/webhook/{w.ConnectionId}", h.Url));
        Assert.Equal(["jira:issue_created", "jira:issue_updated", "jira:issue_deleted"], hooks[0].Events);
        Assert.Equal("project in (\"ABC\")", hooks[0].Jql);
        Assert.Equal(["sprint_created", "sprint_updated", "sprint_deleted", "sprint_started", "sprint_closed"], hooks[1].Events);
        Assert.Null(hooks[1].Jql);
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal(Registered(w), connection.Ids().Order());
        Assert.Equal(api.Clock.GetUtcNow() + WebhookManager.Lifetime, connection.WebhookExpiresAt);
        var shown = (await admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0];
        Assert.True(shown.GetProperty("webhookActive").GetBoolean());
        Assert.Equal(connection.WebhookExpiresAt, shown.GetProperty("webhookExpiresAt").GetDateTimeOffset());
        Assert.DoesNotContain("webhookIds", shown.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_space_replaces_the_registrations_and_the_same_spaces_again_leave_them_alone()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            site.Space("ABC");
            site.Space("DEF");
        });
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        var first = Registered(w);

        await ImportAsync(admin, w, "DEF");
        var second = Registered(w);
        await ImportAsync(admin, w, "ABC", "DEF");
        await JiraKit.PassAsync(w);

        Assert.Equal(2, second.Count);
        Assert.Empty(first.Intersect(second)); // the old ones were removed, new ones made
        Assert.Equal("project in (\"ABC\", \"DEF\")", w.Site.Webhooks.OrderBy(h => h.Id).First().Jql);
        Assert.Equal(second, Registered(w)); // the same set of spaces: nothing was registered again
        Assert.Equal(4, api.Jira.Requests.Count(r => r.Path == $"/ex/jira/{w.Site.CloudId}/rest/api/3/webhook" && r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task A_pass_extends_the_registrations_only_when_less_than_twenty_days_are_left()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        var ids = Registered(w);
        var extensions = () => api.Jira.Requests.Count(r => r.Path.EndsWith("/rest/api/3/webhook/refresh", StringComparison.Ordinal) && r.Path.Contains(w.Site.CloudId, StringComparison.Ordinal));

        await SetExpiryAsync(w, TimeSpan.FromDays(25));
        (await admin.PostAsync($"{w.Base}/sync")).EnsureSuccessStatusCode();
        Assert.Equal(0, extensions());

        await SetExpiryAsync(w, TimeSpan.FromDays(15));
        (await admin.PostAsync($"{w.Base}/sync")).EnsureSuccessStatusCode();

        Assert.Equal(1, extensions());
        Assert.Equal(ids, Registered(w)); // extended, not replaced
        Assert.Equal(api.Clock.GetUtcNow() + WebhookManager.Lifetime, (await JiraKit.ConnectionAsync(w)).WebhookExpiresAt);
        var sent = api.Jira.Requests.Last(r => r.Path.EndsWith("/rest/api/3/webhook/refresh", StringComparison.Ordinal));
        Assert.Equal(ids, JsonDocument.Parse(sent.Body).RootElement.GetProperty("webhookIds").EnumerateArray().Select(i => i.GetInt64()).Order());
    }

    [Fact]
    public async Task When_jira_no_longer_knows_the_webhooks_a_pass_registers_them_again()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        var before = Registered(w);
        w.Site.RefreshFails = true;
        await SetExpiryAsync(w, TimeSpan.FromDays(5));

        var run = await (await admin.PostAsync($"{w.Base}/sync")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", run.GetProperty("status").GetString());
        var after = Registered(w);
        Assert.Equal(2, after.Count);
        Assert.Empty(before.Intersect(after));
        Assert.Equal(api.Clock.GetUtcNow() + WebhookManager.Lifetime, (await JiraKit.ConnectionAsync(w)).WebhookExpiresAt);
    }

    [Fact]
    public async Task A_failed_extension_that_was_only_a_hiccup_leaves_exactly_one_set_of_registrations()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        await SetExpiryAsync(w, TimeSpan.FromDays(5));
        api.Jira.Fail(w.Site, "/rest/api/3/webhook/refresh", HttpStatusCode.ServiceUnavailable);

        (await admin.PostAsync($"{w.Base}/sync")).EnsureSuccessStatusCode();

        Assert.Equal(2, w.Site.Webhooks.Count);
    }

    [Fact]
    public async Task A_site_that_refuses_webhooks_still_imports_and_stays_on_the_pass_alone()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC").Issue("One"));
        var admin = await AdminAsync(w);
        api.Jira.Fail(w.Site, "/rest/api/3/webhook", HttpStatusCode.Forbidden, times: 20);

        var answer = await ImportAsync(admin, w, "ABC");
        var pass = await (await admin.PostAsync($"{w.Base}/sync")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", answer[0].GetProperty("outcome").GetString());
        Assert.Equal("ok", pass.GetProperty("status").GetString());
        Assert.Empty(w.Site.Webhooks);
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((null, null), (connection.WebhookIds, connection.WebhookExpiresAt));
        Assert.False((await admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0].GetProperty("webhookActive").GetBoolean());
        Assert.Single(await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ABC")));
    }

    [Fact]
    public async Task Disconnecting_takes_the_webhooks_down_at_jira()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        Assert.Equal(2, w.Site.Webhooks.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, w.Base)).StatusCode);

        Assert.Empty(w.Site.Webhooks);
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((null, null, ConnectionStatuses.Disconnected), (connection.WebhookIds, connection.WebhookExpiresAt, connection.Status));
    }

    [Fact]
    public async Task With_no_space_left_to_watch_the_registrations_are_removed_and_a_failed_removal_does_not_matter()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC"));
        var admin = await AdminAsync(w);
        await ImportAsync(admin, w, "ABC");
        using var scope = host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDb>();
        var manager = scope.ServiceProvider.GetRequiredService<WebhookManager>();
        var connection = await db.IntegrationConnections.SingleAsync(c => c.Id == w.ConnectionId);
        Assert.True(manager.CanRegister);
        Assert.Equal($"{PublicOrigin}/api/integrations/jira/webhook/{w.ConnectionId}", manager.CallbackUrl(w.ConnectionId));

        await manager.EnsureAsync(connection, [], CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(w.Site.Webhooks);
        Assert.Null(connection.WebhookIds);
        // And a removal that Jira refuses is not an error of the caller: the registrations expire by themselves.
        await manager.EnsureAsync(connection, ["ABC"], CancellationToken.None);
        api.Jira.Fail(w.Site, "/rest/api/3/webhook", HttpStatusCode.InternalServerError, times: 1);
        await manager.TryRemoveAsync(connection, CancellationToken.None);
        Assert.Equal(2, w.Site.Webhooks.Count);
    }

    [Fact]
    public async Task At_the_ordinary_http_address_nothing_is_registered_and_the_page_says_the_webhook_is_off()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC").Issue("One"));

        await JiraKit.ImportAsync(w, "ABC");
        await JiraKit.PassAsync(w);

        Assert.Empty(w.Site.Webhooks);
        Assert.Equal(0, api.Jira.Count(w.Site, "/rest/api/3/webhook"));
        var shown = (await w.Admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0];
        Assert.False(shown.GetProperty("webhookActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("webhookExpiresAt").ValueKind);
        using var scope = api.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<WebhookManager>().CanRegister);
    }
}
