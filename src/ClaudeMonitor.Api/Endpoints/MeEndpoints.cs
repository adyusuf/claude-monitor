using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record MeWorkspace(Guid Id, string Name, string Role);
public sealed record MeResponse(Guid Id, string? Email, string DisplayName, bool HasPassword, IReadOnlyList<string> Providers,
    IReadOnlyList<MeWorkspace> Workspaces);
public sealed record ProvidersResponse(IReadOnlyList<string> Available);

/// <summary>The signed-in user, and which sign-in providers this deployment offers.</summary>
public static class MeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/me", Me).RequireAuthorization(Schemes.Session);
        api.MapGet("/auth/providers", Available);
    }

    private static async Task<IResult> Me(HttpContext http, MonitorDb db)
    {
        var userId = http.User.UserId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, http.RequestAborted);
        var providers = await db.UserLogins.AsNoTracking().Where(l => l.UserId == userId)
            .Select(l => l.Provider).OrderBy(p => p).ToListAsync(http.RequestAborted);
        var workspaces = await (from m in db.WorkspaceMembers.AsNoTracking()
                                join w in db.Workspaces.AsNoTracking() on m.WorkspaceId equals w.Id
                                where m.UserId == userId && m.RemovedAt == null && w.Status == "active"
                                orderby w.Name
                                select new MeWorkspace(w.Id, w.Name, m.Role)).ToListAsync(http.RequestAborted);
        return Results.Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.PasswordHash is not null, providers, workspaces));
    }

    private static async Task<IResult> Available(IAuthenticationSchemeProvider schemes)
    {
        var available = new List<string>();
        foreach (var provider in Providers.All.Order(StringComparer.Ordinal))
        {
            if (await schemes.GetSchemeAsync(provider) is not null) available.Add(provider);
        }

        return Results.Ok(new ProvidersResponse(available));
    }
}
