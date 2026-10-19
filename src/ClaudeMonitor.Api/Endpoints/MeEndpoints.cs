using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record MeWorkspace(Guid Id, string Name, string Role);
public sealed record MeResponse(Guid Id, string? Email, string DisplayName, bool HasPassword, IReadOnlyList<string> Providers,
    IReadOnlyList<MeWorkspace> Workspaces, bool MfaEnabled = false, string? Language = null);
public sealed record LanguageRequest(string? Language);
public sealed record ProvidersResponse(IReadOnlyList<string> Available);

/// <summary>The signed-in user, and which sign-in providers this deployment offers.</summary>
public static class MeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/me", Me).RequireAuthorization(Schemes.Session);
        api.MapPut("/me/language", SetLanguage).RequireAuthorization(Schemes.Session);
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
        return Results.Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.PasswordHash is not null, providers, workspaces,
            user.TotpEnabledAt is not null, user.Language));
    }

    /// <summary>The language the person picked in the menu, kept on the account so the mails to them follow it (en, tr or de; anything else is refused).</summary>
    private static async Task<IResult> SetLanguage(LanguageRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Language is not { } language || !Mail.MailTemplates.Languages.Contains(language)) return Http.Invalid("language", "invalid_language");
        var userId = http.User.UserId();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language).SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), http.RequestAborted);
        return Results.NoContent();
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
