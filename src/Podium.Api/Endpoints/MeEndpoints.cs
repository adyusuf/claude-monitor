using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

public sealed record MeWorkspace(Guid Id, string Name, string Role);
public sealed record MeResponse(Guid Id, string? Email, string DisplayName, bool HasPassword, IReadOnlyList<string> Providers,
    IReadOnlyList<MeWorkspace> Workspaces, bool MfaEnabled = false, string? Language = null, bool ServerAdmin = false);
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

    private static async Task<IResult> Me(HttpContext http, PodiumDb db, ApiConfig config)
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
            user.TotpEnabledAt is not null, user.Language, ServerSettingsEndpoints.IsServerAdmin(config, workspaces)));
    }

    /// <summary>The language the person picked in the menu, kept on the account so the mails to them follow it (en, tr or de; anything else is refused).</summary>
    private static async Task<IResult> SetLanguage(LanguageRequest req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Language is not { } language || !Mail.MailTemplates.Languages.Contains(language)) return Http.Invalid("language", "invalid_language");
        var userId = http.User.UserId();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language).SetProperty(u => u.UpdatedAt, clock.GetUtcNow()), http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>The providers whose app is set, in the environment or on the Integrations page (ADR-0017).</summary>
    private static IResult Available(IntegrationSettings settings) =>
        Results.Ok(new ProvidersResponse([.. Providers.All.Order(StringComparer.Ordinal).Where(p => settings.Provider(p) is not null)]));
}
