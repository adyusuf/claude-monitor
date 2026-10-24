using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// Setup for the server-wide Settings → Integrations page (ADR-0017): people who belong to the admin workspace
/// (<see cref="ApiFactory.AdminWorkspaceId"/>) and a way to clear what the page saved, so tests do not see each other's settings.
/// </summary>
public static class ServerAdminKit
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A new signed-up person who is a member of the admin workspace with the given role (default admin).</summary>
    public static async Task<TestUser> AdminAsync(ApiFactory api, string tag = "srv-admin", string role = Roles.Admin)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        await JoinAsync(api, user, role);
        return user;
    }

    /// <summary>Makes the person a member of the admin workspace; the workspace row is created once (idempotent).</summary>
    public static async Task JoinAsync(ApiFactory api, TestUser user, string role = Roles.Admin)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = api.Db();
            var now = api.Clock.GetUtcNow();
            if (!await db.Workspaces.AnyAsync(w => w.Id == ApiFactory.AdminWorkspaceId))
            {
                db.Workspaces.Add(new Workspace
                {
                    Id = ApiFactory.AdminWorkspaceId,
                    Name = "Server administration",
                    NameSearch = "server administration",
                    CreatedBy = user.Id,
                    CreatedAt = now,
                });
            }

            var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ApiFactory.AdminWorkspaceId && m.UserId == user.Id);
            if (member is null)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = ApiFactory.AdminWorkspaceId, UserId = user.Id, Role = role, JoinedAt = now });
            }
            else
            {
                member.Role = role;
                member.RemovedAt = null;
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Takes the person out of the admin workspace (the row stays, removed).</summary>
    public static async Task RemoveAsync(ApiFactory api, TestUser user)
    {
        await using var db = api.Db();
        var now = api.Clock.GetUtcNow();
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == ApiFactory.AdminWorkspaceId && m.UserId == user.Id)
            .ExecuteUpdateAsync(m => m.SetProperty(x => x.RemovedAt, now));
    }

    /// <summary>Deletes every saved setting and makes the running server read the table again.</summary>
    public static async Task ResetSettingsAsync(ApiFactory api)
    {
        await using (var db = api.Db())
        {
            await db.ServerSettings.ExecuteDeleteAsync();
        }

        await api.Services.GetRequiredService<IntegrationSettings>().RefreshAsync(CancellationToken.None);
    }

    public static Task<HttpResponseMessage> GetAsync(TestUser user) => user.Http.GetAsync("/api/server/integrations");

    public static Task<HttpResponseMessage> PutAsync(TestUser user, string id, object? values, string? code = null) =>
        user.SendAsync(HttpMethod.Put, "/api/server/integrations/" + id, new { values, code });

    /// <summary>The integration with this id in the page's view.</summary>
    public static JsonElement IntegrationOf(JsonElement view, string id) =>
        view.GetProperty("integrations").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    /// <summary>The field with this name of an integration in the page's view.</summary>
    public static JsonElement FieldOf(JsonElement view, string id, string name) =>
        IntegrationOf(view, id).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
}
