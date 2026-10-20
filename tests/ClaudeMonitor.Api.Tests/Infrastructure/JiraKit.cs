using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A workspace admin who connected a fake Jira site (ADR-0015): the admin is the connector and owns the connected account.</summary>
public sealed record JiraWorld(ApiFactory Api, TestUser Admin, FakeSite Site, FakeAccount Account, Guid ConnectionId)
{
    public Guid WorkspaceId => Admin.WorkspaceId;
    public string Base => $"/api/integrations/{ConnectionId}";
}

/// <summary>Setup and reading helpers for the work tracker tests; every call goes through the public API or reads the test database.</summary>
public static class JiraKit
{
    public static string Param(string url, string name) => HttpUtility.ParseQueryString(new Uri(url).Query)[name] ?? "";

    /// <summary>Asks to connect a site and returns the address Atlassian would show the person.</summary>
    public static async Task<string> StartAsync(TestUser admin, string subdomain, Guid? workspace = null)
    {
        var response = await admin.PostAsync($"/api/workspaces/{workspace ?? admin.WorkspaceId}/integrations/jira/connect", new { subdomain });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    /// <summary>The browser coming back from Atlassian: the account approves, Atlassian sends a code for this sign-in, the API redirects to the page.</summary>
    public static Task<HttpResponseMessage> ApproveAsync(TestUser user, FakeJira jira, FakeAccount account, string authorizeUrl) =>
        CallbackAsync(user, jira.NewCode(account, Param(authorizeUrl, "code_challenge"), Param(authorizeUrl, "redirect_uri")), Param(authorizeUrl, "state"));

    public static Task<HttpResponseMessage> CallbackAsync(TestUser user, string code, string state) =>
        user.Http.GetAsync($"/api/integrations/jira/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");

    /// <summary>The <c>result</c> code of the redirect the callback answers with.</summary>
    public static string ResultOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Param(response.Headers.Location!.ToString(), "result");
    }

    /// <summary>A new workspace whose admin connected a new fake site with a new Atlassian account that shows its e-mail.</summary>
    public static async Task<JiraWorld> ConnectedAsync(ApiFactory api, Action<FakeSite>? seed = null, string tag = "jira-admin")
    {
        var admin = await api.NewClient().SignedUpAsync(tag);
        var site = api.Jira.Site();
        seed?.Invoke(site);
        var account = api.Jira.Account("Connector", admin.Email, site);
        var result = ResultOf(await ApproveAsync(admin, api.Jira, account, await StartAsync(admin, site.Subdomain)));
        Assert.Equal("ok", result);
        var list = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/integrations");
        return new JiraWorld(api, admin, site, account, list.GetProperty("connections")[0].GetProperty("id").GetGuid());
    }

    /// <summary>Imports spaces as the admin and returns the answer.</summary>
    public static async Task<JsonElement> ImportAsync(JiraWorld w, params string[] keys)
    {
        var response = await w.Admin.PostAsync($"{w.Base}/spaces/import", new { keys });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> ImportSprintsAsync(JiraWorld w, string key, params long[] sprints)
    {
        var response = await w.Admin.PostAsync($"{w.Base}/spaces/{key}/sprints/import", new { sprintIds = sprints.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A pass now, as the admin; returns the run.</summary>
    public static async Task<JsonElement> PassAsync(JiraWorld w)
    {
        var response = await w.Admin.PostAsync($"{w.Base}/sync");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<Guid> ProjectIdAsync(JiraWorld w, string key)
    {
        await using var db = w.Api.Db();
        return await db.IntegrationLinks.AsNoTracking().Where(l => l.ConnectionId == w.ConnectionId && l.Kind == LinkKinds.Project && l.ExternalKey == key)
            .Select(l => l.LocalId).SingleAsync();
    }

    public static async Task<List<Issue>> IssuesAsync(JiraWorld w, Guid project)
    {
        await using var db = w.Api.Db();
        return await db.Issues.AsNoTracking().Where(i => i.ProjectId == project).OrderBy(i => i.Number).ToListAsync();
    }

    public static async Task<Issue> IssueAsync(JiraWorld w, string key)
    {
        await using var db = w.Api.Db();
        return await db.Issues.AsNoTracking().SingleAsync(i => i.ExternalKey == key && db.IntegrationLinks.Any(l => l.ConnectionId == w.ConnectionId && l.LocalId == i.Id));
    }

    public static async Task<List<Sprint>> SprintsAsync(JiraWorld w, Guid project)
    {
        await using var db = w.Api.Db();
        return await db.Sprints.AsNoTracking().Where(s => s.ProjectId == project).OrderBy(s => s.Number).ToListAsync();
    }

    public static async Task<IntegrationConnection> ConnectionAsync(JiraWorld w)
    {
        await using var db = w.Api.Db();
        return await db.IntegrationConnections.AsNoTracking().SingleAsync(c => c.Id == w.ConnectionId);
    }

    /// <summary>Makes the person's sign-in older than the re-authentication window (the window is judged from the login session's start).</summary>
    public static async Task AgeSignInAsync(ApiFactory api, TestUser user)
    {
        await using var db = api.Db();
        var old = api.Clock.GetUtcNow().AddMinutes(-11);
        await db.LoginSessions.Where(s => s.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, old));
    }

    /// <summary>The API with its configuration changed (for example a public https address, or no Jira app).</summary>
    public static WebApplicationFactory<Program> With(ApiFactory api, Func<ApiConfig, ApiConfig> change) => api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
    {
        var current = (ApiConfig)services.Single(d => d.ServiceType == typeof(ApiConfig)).ImplementationInstance!;
        services.RemoveAll<ApiConfig>();
        services.AddSingleton(change(current));
    }));

    public static TestUser ClientOf(ApiFactory api, WebApplicationFactory<Program> factory) => new(api, factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    }));

    /// <summary>No token, code or secret that Atlassian issued, and not the app's secret, is in what the API returns or what the database keeps in clear.</summary>
    public static async Task AssertNoSecretAnywhereAsync(JiraWorld w)
    {
        var secrets = w.Api.Jira.IssuedSecrets.Append(FakeJira.ClientSecret).ToList();
        var list = (await w.Admin.Http.GetStringAsync($"/api/workspaces/{w.WorkspaceId}/integrations")) + await w.Admin.Http.GetStringAsync($"{w.Base}/runs");
        await using var db = w.Api.Db();
        var rows = JsonSerializer.Serialize(new
        {
            connections = await db.IntegrationConnections.AsNoTracking().Where(c => c.WorkspaceId == w.WorkspaceId).ToListAsync(),
            people = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToListAsync(),
            runs = await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == w.ConnectionId).ToListAsync(),
            audit = (await db.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == w.WorkspaceId).ToListAsync())
                .Select(a => new { a.Action, a.TargetType, a.TargetId, Detail = a.Detail?.RootElement.ToString() }).ToList(),
        });
        var sealedColumns = await db.IntegrationAccounts.AsNoTracking().Where(a => a.ConnectionId == w.ConnectionId).Select(a => a.RefreshToken + "|" + a.AccessToken).ToListAsync();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, list, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rows, StringComparison.Ordinal);
            Assert.All(sealedColumns, c => Assert.DoesNotContain(secret, c, StringComparison.Ordinal));
        }
    }
    /// <summary>Waits until the condition holds (polling; fails after ten seconds).</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail("timed out waiting for " + what);
    }
}
