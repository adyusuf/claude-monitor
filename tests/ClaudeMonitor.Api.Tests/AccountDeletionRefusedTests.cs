using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// A deletion refused because the user is the only owner of a workspace that others use writes nothing: the sole-owner check
/// is judged under the locks before the first write and the transaction ends without a commit.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccountDeletionRefusedTests(ApiFactory api)
{
    [Fact]
    public async Task A_refused_deletion_leaves_the_account_its_ways_in_and_its_content_untouched()
    {
        var owner = await api.NewClient().SignedUpAsync("refused-owner", "Refused Owner");
        await RemoteKit.MemberAsync(api, owner, "refused-member");
        var other = await api.NewClient().SignedUpAsync("refused-other");
        (await other.PostAsync($"/api/workspaces/{other.WorkspaceId}/invitations", new { email = owner.Email, role = Roles.Admin })).EnsureSuccessStatusCode();
        (await owner.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(owner.Email) })).EnsureSuccessStatusCode();
        var agent = await owner.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        (await agent.SendAsync(TestAgent.Hook(external, "UserPromptSubmit", new { prompt = "kept plan" }, api.Clock.GetUtcNow()))).EnsureSuccessStatusCode();

        var before = await SnapshotAsync(owner);
        Assert.NotEqual("[[]]", before["events"]);
        Assert.Equal("[0]", before["accountDeletedAudits"]);

        var refused = await owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sole_owner", body.GetProperty("title").GetString());
        Assert.Equal(owner.WorkspaceId, Assert.Single(body.GetProperty("workspaces").EnumerateArray()).GetGuid());
        Assert.Equal(before.OrderBy(kv => kv.Key), (await SnapshotAsync(owner)).OrderBy(kv => kv.Key));

        // Still signed in with the same cookie, and the agent's token still works.
        var me = await owner.GetJsonAsync("/api/me");
        Assert.Equal(owner.Email, me.GetProperty("email").GetString());
        Assert.Equal("Refused Owner", me.GetProperty("displayName").GetString());
        (await agent.SendAsync(TestAgent.Hook(external, "Stop", new { }, api.Clock.GetUtcNow()))).EnsureSuccessStatusCode();
    }

    /// <summary>Every row the deletion would have changed, as it is in the database now (one comparable text per kind of row).</summary>
    private async Task<Dictionary<string, string>> SnapshotAsync(TestUser owner)
    {
        await using var db = api.Db();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == owner.Id);
        var agentIds = await db.Agents.AsNoTracking().Where(a => a.UserId == owner.Id).Select(a => a.Id).ToListAsync();
        var sessionIds = await db.HarnessSessions.AsNoTracking().Where(s => agentIds.Contains(s.AgentId)).Select(s => s.Id).ToListAsync();
        return new()
        {
            ["user"] = Text(user.Email, user.EmailNormalized, user.DisplayName, user.DisplayNameSearch, user.Status,
                user.PasswordHash is null ? "no password" : "password", user.UpdatedAt, user.EmailVerifiedAt, user.TotpEnabledAt),
            ["loginSessions"] = Text(await db.LoginSessions.AsNoTracking().Where(s => s.UserId == owner.Id).OrderBy(s => s.Id)
                .Select(s => s.RevokedAt).ToListAsync()),
            ["agents"] = Text(await db.Agents.AsNoTracking().Where(a => a.UserId == owner.Id).OrderBy(a => a.Id)
                .Select(a => a.Status + "/" + a.RevokedAt + "/" + a.RevokedBy).ToListAsync()),
            ["agentTokens"] = Text(await db.AgentTokens.AsNoTracking().Where(t => agentIds.Contains(t.AgentId)).OrderBy(t => t.Id)
                .Select(t => t.RevokedAt).ToListAsync()),
            ["memberships"] = Text(await db.WorkspaceMembers.AsNoTracking().Where(m => m.UserId == owner.Id).OrderBy(m => m.WorkspaceId)
                .Select(m => m.WorkspaceId + "/" + m.Role + "/" + m.RemovedAt).ToListAsync()),
            ["sessions"] = Text(await db.HarnessSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).OrderBy(s => s.Id)
                .Select(s => s.Id + "/" + s.Title + "/" + s.TitleSearch).ToListAsync()),
            ["events"] = Text(await db.SessionEvents.AsNoTracking().Where(e => sessionIds.Contains(e.SessionId)).OrderBy(e => e.Id)
                .Select(e => e.Id).ToListAsync()),
            ["workspace"] = Text(await db.Workspaces.AsNoTracking().Where(w => w.Id == owner.WorkspaceId).Select(w => w.Status).SingleAsync()),
            ["accountDeletedAudits"] = Text(await db.AuditEvents.AsNoTracking()
                .CountAsync(e => e.Action == AuditActions.AccountDeleted && e.ActorUserId == owner.Id)),
        };
    }

    private static string Text(params object?[] values) => JsonSerializer.Serialize(values);
}
