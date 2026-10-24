using System.Globalization;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Mail;
using Podium.Api.Remote;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

/// <summary>
/// The server's Settings → Integrations page (ADR-0017): SMTP, GitHub and Google sign-in and the Jira app in one place. Only the owners and
/// admins of the admin workspace (AIPOD_ADMIN_WORKSPACE) reach it; everyone else gets 404, and without that variable nobody does (fail-closed).
/// Secrets are sealed with AIPOD_INTEGRATION_KEY, never sent back, never logged and never audited; an environment value cannot be changed here.
/// </summary>
public static class ServerSettingsEndpoints
{
    public const string InvalidSetting = "invalid_setting";
    public const string InvalidValue = "invalid_value";
    public const string SetOnServer = "set_on_server";
    public const string MailFailed = "mail_failed";
    public const string NoEmail = "no_email";
    public const string SettingsConflict = "settings_conflict";

    /// <summary>The longest value in bytes: it is sealed (28 bytes more, base64) into a column of 4000 characters, whatever the characters are.</summary>
    public const int ValueMaxBytes = 2900;

    public static void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/server/integrations").RequireAuthorization(Schemes.Session);
        group.MapGet("", View);
        group.MapPut("/{id}", Save);
        group.MapPost("/smtp/test", TestMail);
    }

    /// <summary>Whether the signed-in user (with these memberships) manages the server-wide settings; /me says so to the web.</summary>
    public static bool IsServerAdmin(ApiConfig config, IEnumerable<MeWorkspace> workspaces)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AdminWorkspace is { } admin
               && workspaces.Any(w => w.Id == admin && Roles.Rank(w.Role) >= Roles.Rank(Roles.Admin));
    }

    private static async Task<bool> AdminAsync(HttpContext http, PodiumDb db, ApiConfig config) =>
        config.AdminWorkspace is { } admin && await Access.MemberAsync(db, http.User.UserId(), admin, Roles.Admin, http.RequestAborted) is not null;

    private static async Task<IResult> View(HttpContext http, PodiumDb db, ApiConfig config, IntegrationSettings settings) =>
        await AdminAsync(http, db, config) ? Results.Ok(ServerSettingsViews.Of(settings, config)) : Http.NotFound();

    private static async Task<IResult> Save(string id, ServerSettingsRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        IntegrationSettings settings, TimeProvider clock)
    {
        var ct = http.RequestAborted;
        if (!await AdminAsync(http, db, config) || !IntegrationIds.All.Contains(id)) return Http.NotFound();
        if (req?.Values is not { Count: > 0 } values) return Http.Invalid("values", InvalidSetting);
        if (!settings.CanSave) return Http.Invalid("values", ServerSettingsViews.NoteIntegrationKey);

        var changes = new List<(SettingField Field, string? Value)>();
        foreach (var (name, raw) in values)
        {
            if (IntegrationCatalog.Find(id, name) is not { } field) return Http.Invalid(name, InvalidSetting);
            var value = raw?.Trim();
            // The environment's value wins and is not changed here; a value saved earlier that it now hides can still be removed, so it
            // does not come back when the environment's value goes (ADR-0017).
            if (settings.Source(field) == IntegrationSettings.SourceServer && !string.IsNullOrEmpty(value)) return Http.Invalid(name, SetOnServer);
            if (!string.IsNullOrEmpty(value) && !Valid(field, value)) return Http.Invalid(name, InvalidValue);
            changes.Add((field, string.IsNullOrEmpty(value) ? null : value));
        }

        var me = http.User.UserId();
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, me, req.Code)) return RemoteChecks.ReauthRefused();

        var now = clock.GetUtcNow();
        var keys = changes.Select(c => c.Field.Key).ToList();
        var rows = await db.ServerSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        foreach (var (field, value) in changes)
        {
            rows.TryGetValue(field.Key, out var row);
            if (value is null)
            {
                if (row is not null) db.ServerSettings.Remove(row);
                continue;
            }

            if (row is null)
            {
                row = new ServerSetting { Key = field.Key };
                db.ServerSettings.Add(row);
            }

            row.Value = field.Secret ? SecretBox.Seal(config.IntegrationKey!, System.Text.Encoding.UTF8.GetBytes(value)) : value;
            row.Secret = field.Secret;
            row.UpdatedAt = now;
            row.UpdatedBy = me;
        }

        // Which settings changed, never their values.
        Audit.Add(db, http, clock, AuditActions.ServerSettingsChanged, config.AdminWorkspace, me, targetType: "integration", targetId: id,
            detail: new { fields = changes.Select(c => c.Field.Name).Order(StringComparer.Ordinal).ToList() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e is DbUpdateConcurrencyException || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two first saves of the same setting at once (the second insert meets the first one's key), or two removals of the same row.
            // Any other failure of the save (a database that went away, a constraint) is not "someone else saved": it reaches the logged 500.
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: SettingsConflict);
        }

        // The rows are committed: the refresh is not cancelled with the request (an admin who closes the tab must not leave this instance on the old values).
        await settings.RefreshAsync(CancellationToken.None);
        return Results.Ok(ServerSettingsViews.Of(settings, config));
    }

    /// <summary>Sends a test mail to the caller's own address with the SMTP values in force; says only whether it went.</summary>
    private static async Task<IResult> TestMail(HttpContext http, PodiumDb db, ApiConfig config, IMailer mailer, ILoggerFactory logs)
    {
        if (!await AdminAsync(http, db, config)) return Http.NotFound();
        var userId = http.User.UserId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, http.RequestAborted);
        if (string.IsNullOrEmpty(user.Email)) return Http.Invalid("email", NoEmail);
        try
        {
            await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http, user.Language), MailTemplates.Test, user.Email), http.RequestAborted);
        }
        catch (Exception e) when (e is System.Net.Mail.SmtpException or InvalidOperationException or IOException
                                      or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            logs.CreateLogger(typeof(ServerSettingsEndpoints)).LogWarning("Test mail failed: {Type}", e.GetType().Name);
            return Http.Invalid("smtp", MailFailed);
        }

        return Results.NoContent();
    }

    internal static bool Valid(SettingField field, string value)
    {
        if (value.Length > IntegrationCatalog.ValueMax || System.Text.Encoding.UTF8.GetByteCount(value) > ValueMaxBytes) return false;
        return field.Name switch
        {
            IntegrationCatalog.Port => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535,
            IntegrationCatalog.Tls => value is "true" or "false",
            IntegrationCatalog.From => Http.IsEmail(value),
            IntegrationCatalog.Host or IntegrationCatalog.ClientId => !value.Any(char.IsWhiteSpace),
            _ => true,
        };
    }
}
