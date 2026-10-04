using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class PrivacyTests(ApiFactory api)
{
    private async Task<(TestUser User, TestAgent Agent, Guid SessionId)> WithSessionAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var agent = await user.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        await agent.SendAsync(
            TestAgent.Hook(external, "UserPromptSubmit", new { prompt = "secret plan for " + tag }, api.Clock.GetUtcNow()),
            TestAgent.Hook(external, "PostToolUse", new { tool_name = "TaskCreate", tool_input = new { subject = "private task" }, tool_response = new { task = new { id = "1" } } }, api.Clock.GetUtcNow()));
        var id = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        await user.PostAsync($"/api/sessions/{id}/commands", new { kind = "prompt", body = "do the private thing" });
        await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, "Bash", JsonSerializer.SerializeToElement(new { command = "cat ~/.ssh/id_rsa" }), 60), TestUser.Json);
        return (user, agent, id);
    }

    [Fact]
    public async Task The_export_holds_the_users_own_data_and_nobody_elses()
    {
        var (user, _, _) = await WithSessionAsync("export");
        var other = await WithSessionAsync("export-other");
        var response = await user.Http.GetAsync("/api/me/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(user.Email, export.GetProperty("user").GetProperty("email").GetString());
        Assert.Single(export.GetProperty("machines").EnumerateArray());
        var session = Assert.Single(export.GetProperty("sessions").EnumerateArray());
        Assert.Equal("private task", session.GetProperty("tasks")[0].GetProperty("subject").GetString());
        Assert.Equal(2, session.GetProperty("events").GetArrayLength());
        Assert.Equal("do the private thing", export.GetProperty("commandsSent")[0].GetProperty("body").GetString());
        Assert.Contains(export.GetProperty("auditTrail").EnumerateArray(), a => a.GetProperty("action").GetString() == "user.register");
        Assert.DoesNotContain("export-other", export.ToString(), StringComparison.Ordinal);
        Assert.NotNull(other.User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().Http.GetAsync("/api/me/export")).StatusCode);
    }

    [Fact]
    public async Task Deleting_needs_the_password_and_the_confirm_word()
    {
        var user = await api.NewClient().SignedUpAsync("delete-checks");
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "delete" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/delete", new { password = "wrong password!", confirm = "DELETE" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/delete", new { confirm = "DELETE" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_sole_owner_of_a_shared_workspace_must_hand_it_over_first()
    {
        var owner = await api.NewClient().SignedUpAsync("sole-owner");
        var member = await api.NewClient().SignedUpAsync("sole-member");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = member.Email })).EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        var blocked = await owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sole_owner", body.GetProperty("title").GetString());
        Assert.Equal(owner.WorkspaceId, body.GetProperty("workspaces")[0].GetGuid());

        // The member, owner of nothing shared, may leave the platform; the owner workspace keeps going.
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        var members = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/members");
        Assert.Equal(1, members.GetArrayLength());
    }

    [Fact]
    public async Task Deleting_clears_the_person_and_their_captured_content_and_closes_every_way_in()
    {
        var (user, agent, sessionId) = await WithSessionAsync("delete");
        var bystander = await WithSessionAsync("bystander");
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);

        await using var db = api.Db();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal((null, null, "Deleted user", "deleted"), (row.Email, row.PasswordHash, row.DisplayName, row.Status));
        Assert.False(await db.SessionEvents.AnyAsync(e => e.SessionId == sessionId));
        Assert.False(await db.SessionTasks.AnyAsync(t => t.SessionId == sessionId));
        Assert.Null((await db.HarnessSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).Title);
        Assert.All(await db.SessionCommands.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync(), c => Assert.Null(c.Body));
        Assert.All(await db.PermissionRequests.AsNoTracking().Where(p => p.SessionId == sessionId).ToListAsync(),
            p => Assert.Equal("{}", p.ToolInput.RootElement.GetRawText()));
        Assert.Equal("archived", (await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == user.WorkspaceId)).Status);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.ActorUserId == user.Id && a.Action == "user.account_deleted"));

        // Someone else's session is untouched.
        Assert.True(await db.SessionEvents.AnyAsync(e => e.SessionId == bystander.SessionId));
        Assert.Equal(HttpStatusCode.OK, (await bystander.User.Http.GetAsync("/api/me")).StatusCode);

        // The address is free again: the person may sign up anew.
        Assert.Equal(HttpStatusCode.Accepted,
            (await api.NewClient().PostAsync("/api/auth/register", new { email = user.Email, password = TestUser.Password, displayName = "Again" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_also_takes_the_users_events_out_of_the_archived_day_files()
    {
        var owner = await api.NewClient().SignedUpAsync("archive-owner");
        var leaver = await api.NewClient().SignedUpAsync("archive-leaver");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = leaver.Email })).EnsureSuccessStatusCode();
        (await leaver.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(leaver.Email) })).EnsureSuccessStatusCode();
        await owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{owner.WorkspaceId}/settings", new { retentionDays = 1 });
        var ownerAgent = await owner.ConnectAgentAsync();
        var leaverAgent = await leaver.ConnectAgentAsync(owner.WorkspaceId);
        await ownerAgent.SendAsync(TestAgent.Hook("owner-session", "UserPromptSubmit", new { prompt = "owner work" }, api.Clock.GetUtcNow()));
        await leaverAgent.SendAsync(TestAgent.Hook("leaver-session", "UserPromptSubmit", new { prompt = "leaver secret" }, api.Clock.GetUtcNow()));
        api.Clock.Advance(TimeSpan.FromDays(2));
        await using (var db = api.Db())
        {
            await Background.Archiver.RunOnceAsync(db, api.ArchiveDir, api.Clock.GetUtcNow(), CancellationToken.None);
        }

        string ArchiveText()
        {
            using var db = api.Db();
            var archive = db.EventArchives.AsNoTracking().Single(a => a.WorkspaceId == owner.WorkspaceId);
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive.Path);
            using var reader = new StreamReader(zip.Entries.Single().Open());
            return reader.ReadToEnd();
        }

        Assert.Contains("leaver secret", ArchiveText(), StringComparison.Ordinal);
        var login = api.NewClient();
        (await login.PostAsync("/api/auth/login", new { email = leaver.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await login.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        var text = ArchiveText();
        Assert.DoesNotContain("leaver secret", text, StringComparison.Ordinal);
        Assert.Contains("owner work", text, StringComparison.Ordinal);
        await using var check = api.Db();
        var row = await check.EventArchives.AsNoTracking().SingleAsync(a => a.WorkspaceId == owner.WorkspaceId);
        Assert.Equal(1, row.EventCount);
        await using var file = File.OpenRead(row.Path);
        Assert.Equal(row.Sha256, Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file)));
    }

    [Fact]
    public async Task An_archive_left_empty_is_removed_with_its_row()
    {
        var user = await api.NewClient().SignedUpAsync("archive-solo");
        await user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/settings", new { retentionDays = 1 });
        var agent = await user.ConnectAgentAsync();
        await agent.SendAsync(TestAgent.Hook("solo-session", "Stop", new { }, api.Clock.GetUtcNow()));
        api.Clock.Advance(TimeSpan.FromDays(2));
        string path;
        await using (var db = api.Db())
        {
            await Background.Archiver.RunOnceAsync(db, api.ArchiveDir, api.Clock.GetUtcNow(), CancellationToken.None);
            path = db.EventArchives.AsNoTracking().Single(a => a.WorkspaceId == user.WorkspaceId).Path;
        }

        var login = api.NewClient();
        (await login.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await login.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        Assert.False(File.Exists(path));
        await using var check = api.Db();
        Assert.False(await check.EventArchives.AnyAsync(a => a.WorkspaceId == user.WorkspaceId));
    }
}
