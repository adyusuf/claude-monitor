using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Background;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>
/// A workspace set up for remote work (ADR-0004): Admin owns the workspace and asks for work from Requester;
/// Owner is a member who runs Target; Other is a member with no part in the run. The switch is on and Target is at
/// the exec level the test asked for.
/// </summary>
public sealed record RemoteTeam(TestUser Admin, TestUser Owner, TestUser Other, TestAgent Requester, TestAgent Target)
{
    public Guid WorkspaceId => Admin.WorkspaceId;
    public Guid TargetId => Target.Tokens.AgentId;
    public Guid RequesterId => Requester.Tokens.AgentId;
}

/// <summary>Setup and reading helpers the remote-work tests share; every call goes through the public API.</summary>
public static class RemoteKit
{
    public const string Tail = "/usr/bin/tail";

    public static async Task<RemoteTeam> TeamAsync(ApiFactory api, string execLevel = ExecLevels.Argv, bool enabled = true)
    {
        var admin = await api.NewClient().SignedUpAsync("rk-admin");
        var owner = await MemberAsync(api, admin, "rk-owner");
        var other = await MemberAsync(api, admin, "rk-other");
        if (enabled) await SetSwitchAsync(admin, true);
        var requester = await admin.ConnectAgentAsync();
        var target = await owner.ConnectAgentAsync(admin.WorkspaceId);
        await ProfileAsync(target, execLevel);
        return new RemoteTeam(admin, owner, other, requester, target);
    }

    public static async Task<TestUser> MemberAsync(ApiFactory api, TestUser admin, string tag, string role = "member")
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        (await admin.PostAsync($"/api/workspaces/{admin.WorkspaceId}/invitations", new { email = user.Email, role })).EnsureSuccessStatusCode();
        (await user.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(user.Email) })).EnsureSuccessStatusCode();
        return user;
    }

    public static async Task SetSwitchAsync(TestUser admin, bool on) =>
        (await admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{admin.WorkspaceId}/remote-settings", new { remoteRunsEnabled = on }))
            .EnsureSuccessStatusCode();

    public static async Task<HttpResponseMessage> ProfileAsync(TestAgent agent, string level, bool service = false) =>
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(level, service, null, null, 2), TestUser.Json))
            .EnsureSuccessStatusCode();

    public static RunCreate Argv(Guid target, string key, string[]? argv = null, string? cwd = null, int timeout = 30, string? reason = null) =>
        new(key, target, RunModes.Argv, argv ?? [Tail, "-n", "5", "/var/log/app/api.log"], null, cwd, timeout, reason);

    public static RunCreate Shell(Guid target, string key, string command = "ls -l", int timeout = 30) =>
        new(key, target, RunModes.Shell, null, command, null, timeout, null);

    /// <summary>A grant for "tail -n 1..max under /var/log/app", as a Claude session on the requester's machine asks for it.</summary>
    public static GrantRequest GrantAsk(Guid target, int max = 100, int days = 30, string? reason = "needs the logs") =>
        new(target, [Tail, "-n", $"{{int:1..{max}}}", "{path:/var/log/app/}"], "/var/log/app", 60, days, reason);

    /// <summary>A run that fits <see cref="GrantAsk"/> unless the arguments say otherwise.</summary>
    public static RunCreate GrantRun(Guid target, string? key = null, int lines = 5, string path = "/var/log/app/api.log", int timeout = 30) =>
        Argv(target, key ?? NewKey(), [Tail, "-n", lines.ToString(System.Globalization.CultureInfo.InvariantCulture), path], "/var/log/app", timeout);

    public static async Task<GrantView> RequestGrantAsync(TestAgent requester, GrantRequest ask)
    {
        var response = await requester.Http.PostAsJsonAsync("/api/agent/grants", ask, TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GrantView>(TestUser.Json))!;
    }

    /// <summary>The requester asks, the target's owner (just signed in) approves.</summary>
    public static async Task<GrantView> ActiveGrantAsync(RemoteTeam team, GrantRequest? ask = null)
    {
        var grant = await RequestGrantAsync(team.Requester, ask ?? GrantAsk(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
        return grant;
    }

    public static string NewKey() => "key-" + Guid.NewGuid().ToString("N");

    public static Task<HttpResponseMessage> PostRunAsync(TestAgent requester, RunCreate req) =>
        requester.Http.PostAsJsonAsync("/api/agent/runs", req, TestUser.Json);

    public static async Task<RunCreated> CreateAsync(TestAgent requester, RunCreate req)
    {
        var response = await PostRunAsync(requester, req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
    }

    public static string HashOf(RunCreate req) =>
        GrantMatcher.RunHash(req.Mode, req.Argv, req.ShellCommand, req.Cwd, req.TimeoutSeconds, req.TargetAgentId);

    public static Task<HttpResponseMessage> ApproveAsync(TestUser owner, Guid runId, RunCreate req, string? code = null) =>
        owner.PostAsync($"/api/runs/{runId}/approve", new { hash = HashOf(req), code });

    public static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(title, await TitleAsync(response));
    }

    public static async Task<HttpResponseMessage> StatusAsync(TestAgent target, Guid runId, RunStatusUpdate update) =>
        await target.Http.PostAsJsonAsync($"/api/agent/runs/{runId}/status", update, TestUser.Json);

    public static async Task<RemoteRun> RunRowAsync(ApiFactory api, Guid runId)
    {
        await using var db = api.Db();
        return await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    /// <summary>One pass of the remote chores at the test clock's now, as the Housekeeper runs them.</summary>
    public static async Task HousekeepAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await RemoteHousekeeping.RunOnceAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), api.Services.GetRequiredService<ApiConfig>(),
            api.Services.GetRequiredService<Broker>(), api.Clock.GetUtcNow(), CancellationToken.None);
    }

    public static async Task<string> StatusOfAsync(ApiFactory api, Guid runId) => (await RunRowAsync(api, runId)).Status;

    /// <summary>The web's run list as one member sees it, newest first.</summary>
    public static async Task<JsonElement> WebRunAsync(TestUser user, Guid workspaceId, Guid runId)
    {
        var list = await user.GetJsonAsync($"/api/workspaces/{workspaceId}/runs");
        return list.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == runId);
    }

    public static async Task<StreamReader> OpenAsync(HttpClient http, string url)
    {
        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    /// <summary>The next event with this name whose data satisfies the filter; fails after 10 seconds, never sleeps.</summary>
    public static async Task<JsonElement> NextEventAsync(StreamReader reader, string name, Func<JsonElement, bool>? where = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? current = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                current = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == name)
            {
                var data = JsonDocument.Parse(line[6..]).RootElement;
                if (where is null || where(data)) return data;
            }
        }

        throw new InvalidOperationException("stream ended before " + name);
    }

    public static Task<JsonElement> RunEventAsync(StreamReader reader, string name, Guid runId) =>
        NextEventAsync(reader, name, e => e.GetProperty("id").GetGuid() == runId);

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    /// <summary>Turns two-step sign-in on for the user and returns the secret their codes come from.</summary>
    public static async Task<byte[]> EnableTotpAsync(ApiFactory api, TestUser user)
    {
        var setup = await (await user.PostAsync("/api/me/mfa/setup")).Content.ReadFromJsonAsync<JsonElement>();
        var secret = FromBase32(setup.GetProperty("secret").GetString()!);
        (await user.PostAsync("/api/me/mfa/enable", new { code = TotpNow(api, secret, -1) })).EnsureSuccessStatusCode();
        return secret;
    }

    public static string TotpNow(ApiFactory api, byte[] secret, int stepOffset = 0) =>
        Totp.Code(secret, Totp.Step(api.Clock.GetUtcNow()) + stepOffset);
}
