using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>One person with one machine and one Claude session in the default project, set up for permission requests and approval rules (ADR-0019).</summary>
public sealed record RuleWorld(TestUser Owner, TestAgent Agent, string Session)
{
    public Guid WorkspaceId => Owner.WorkspaceId;
    public Guid AgentId => Agent.Tokens.AgentId;
}

/// <summary>Setup, calls and reads the approval-rule tests share; requests and answers go through the public API, rows are seeded or read in the database.</summary>
public static class ApprovalRuleKit
{
    public const string Project = "repo-key";
    public const string OtherProject = "other-repo";

    public static async Task<RuleWorld> WorldAsync(ApiFactory api, string tag = "ar")
    {
        ArgumentNullException.ThrowIfNull(api);
        var owner = await api.NewClient().SignedUpAsync(tag);
        var agent = await owner.ConnectAgentAsync();
        return new RuleWorld(owner, agent, await StartSessionAsync(api, agent));
    }

    public static async Task<string> StartSessionAsync(ApiFactory api, TestAgent agent, string? project = Project)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(agent);
        var external = "sess-" + Guid.NewGuid();
        (await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, api.Clock.GetUtcNow(), project))).EnsureSuccessStatusCode();
        return external;
    }

    public static async Task<PermissionRequestCreated> AskAsync(TestAgent agent, string session, string tool, object input, bool inline = false,
        string? accessScope = null, int waitSeconds = 120)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var response = await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, session, tool, JsonSerializer.SerializeToElement(input), waitSeconds, inline, accessScope), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!;
    }

    public static Task<HttpResponseMessage> AnswerAsync(TestUser owner, Guid requestId, string decision = PermissionDecisions.Allow, string? remember = null,
        string? code = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.PostAsync($"/api/permission-requests/{requestId}/answer", new { decision, remember, code });
    }

    /// <summary>The request is made and the owner allows it with a rule of this length (the answer must be accepted).</summary>
    public static async Task<Guid> RememberAsync(RuleWorld w, string tool, object input, string remember, string? accessScope = null,
        string? session = null)
    {
        ArgumentNullException.ThrowIfNull(w);
        var request = await AskAsync(w.Agent, session ?? w.Session, tool, input, accessScope: accessScope);
        Assert.Equal(HttpStatusCode.NoContent, (await AnswerAsync(w.Owner, request.Id, remember: remember)).StatusCode);
        return request.Id;
    }

    public static async Task<List<ApprovalRule>> RulesAsync(ApiFactory api, Guid workspaceId)
    {
        ArgumentNullException.ThrowIfNull(api);
        await using var db = api.Db();
        return await db.ApprovalRules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync();
    }

    public static async Task<PermissionRequest> RequestRowAsync(ApiFactory api, Guid id)
    {
        ArgumentNullException.ThrowIfNull(api);
        await using var db = api.Db();
        return await db.PermissionRequests.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    public static async Task<List<AuditEvent>> AuditAsync(ApiFactory api, Guid workspaceId, string action)
    {
        ArgumentNullException.ThrowIfNull(api);
        await using var db = api.Db();
        return await db.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.Action == action).OrderBy(a => a.Id).ToListAsync();
    }

    public static async Task<string> SessionStatusAsync(ApiFactory api, TestAgent agent, string session)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(agent);
        await using var db = api.Db();
        return await db.HarnessSessions.AsNoTracking().Where(s => s.AgentId == agent.Tokens.AgentId && s.ExternalId == session).Select(s => s.Status).SingleAsync();
    }

    public static async Task<Guid> RepoIdAsync(ApiFactory api, Guid workspaceId, string key = Project)
    {
        ArgumentNullException.ThrowIfNull(api);
        await using var db = api.Db();
        return await db.Repos.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.Key == key).Select(r => r.Id).SingleAsync();
    }

    /// <summary>A rule as an answer would have left it; the dates are given, so no test has to wait for a rule to end.</summary>
    public static ApprovalRule Build(ApiFactory api, RuleWorld w, Guid repoId, string command, string duration = ApprovalDurations.Always, DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null, DateTimeOffset? lastUsedAt = null, Guid? userId = null, Guid? agentId = null, string tool = "Bash")
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(w);
        var now = api.Clock.GetUtcNow();
        return new ApprovalRule
        {
            WorkspaceId = w.WorkspaceId,
            UserId = userId ?? w.Owner.Id,
            AgentId = agentId ?? w.AgentId,
            RepoId = repoId,
            ToolName = tool,
            Command = command,
            Duration = duration,
            CreatedAt = createdAt ?? now,
            ExpiresAt = duration == ApprovalDurations.Always ? null : expiresAt ?? now + (ApprovalDurations.Length(duration) ?? TimeSpan.Zero),
            RevokedAt = revokedAt,
            LastUsedAt = lastUsedAt,
            CreatedFromRequest = Guid.CreateVersion7(),
        };
    }

    /// <summary>Puts a rule in the database in the world's default project, unless <paramref name="repoId"/> names another.</summary>
    public static async Task<ApprovalRule> SeedAsync(ApiFactory api, RuleWorld w, string command, string duration = ApprovalDurations.Always, DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null, DateTimeOffset? lastUsedAt = null, Guid? userId = null, Guid? agentId = null, Guid? repoId = null,
        string tool = "Bash")
    {
        ArgumentNullException.ThrowIfNull(w);
        var rule = Build(api, w, repoId ?? await RepoIdAsync(api, w.WorkspaceId), command, duration, createdAt, expiresAt, revokedAt, lastUsedAt, userId, agentId, tool);
        await using var db = api.Db();
        db.ApprovalRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    /// <summary>The same rule written without awaiting, for a callback that runs inside a database command.</summary>
    public static void SeedNow(ApiFactory api, ApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(api);
        using var db = api.Db();
        db.ApprovalRules.Add(rule);
        db.SaveChanges();
    }

    /// <summary>A number of live rules of one person (the commands differ), as many as the cap counts.</summary>
    public static async Task SeedLiveAsync(ApiFactory api, RuleWorld w, int count)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(w);
        var repo = await RepoIdAsync(api, w.WorkspaceId);
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        db.ApprovalRules.AddRange(Enumerable.Range(0, count).Select(i => new ApprovalRule
        {
            WorkspaceId = w.WorkspaceId,
            UserId = w.Owner.Id,
            AgentId = w.AgentId,
            RepoId = repo,
            ToolName = "Bash",
            Command = "seeded " + i.ToString(CultureInfo.InvariantCulture),
            Duration = ApprovalDurations.Always,
            CreatedAt = now,
            CreatedFromRequest = Guid.CreateVersion7(),
        }));
        await db.SaveChangesAsync();
    }

    public static async Task<JsonElement> ListRulesAsync(TestUser user, Guid workspaceId) => await user.GetJsonAsync($"/api/workspaces/{workspaceId}/approval-rules");

    public static async Task<JsonElement> ListRequestsAsync(TestUser user, Guid workspaceId, string? status = null) =>
        await user.GetJsonAsync($"/api/workspaces/{workspaceId}/permission-requests" + (status is null ? "" : "?status=" + status));
}
