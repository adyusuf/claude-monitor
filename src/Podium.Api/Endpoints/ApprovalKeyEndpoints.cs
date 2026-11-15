using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

/// <summary>
/// The public halves of a person's approval keys (ADR-0010 §4). The browser makes the key pair and keeps the private half; registering the public half needs a recent
/// sign-in or a code, like every approval of consequence. The API's copy lets it check a signature, but a target trusts only a key its own admin pinned.
/// </summary>
public static class ApprovalKeyEndpoints
{
    public const int ActiveMax = 10;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/me/approval-keys").RequireAuthorization(Schemes.Session);
        g.MapGet("", List);
        g.MapPost("", Register);
        g.MapPost("/{id:guid}/retire", Retire);
    }

    private static async Task<IResult> List(HttpContext http, PodiumDb db) =>
        Results.Ok(await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == http.User.UserId()).OrderByDescending(k => k.CreatedAt).Take(50)
            .Select(k => new ApprovalKeyView(k.Id, k.Name, k.Fingerprint, k.PublicKey, k.CreatedAt, k.RetiredAt)).ToListAsync(http.RequestAborted));

    private static async Task<IResult> Register(ApprovalKeyCreate? req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        if (req?.Name is not { Length: >= 1 and <= 64 } name || name.Any(char.IsControl)) return Http.Invalid("name", "invalid");
        var spki = ApprovalSigning.FromBase64(req.PublicKey);
        if (spki is null || !ApprovalSigning.IsValidPublicKey(spki)) return Http.Invalid("publicKey", RemoteErrors.InvalidApprovalKey);
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code, window: false)) return RemoteChecks.ReauthRefused(http);
        var fingerprint = ApprovalSigning.Fingerprint(spki);
        // One person's registrations run one at a time (their user row is held until commit), so the limit below counts what is really live (F-195).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, u => u.DisplayName), ct);
        var live = await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == userId && k.RetiredAt == null).ToListAsync(ct);
        if (live.Any(k => k.Fingerprint == fingerprint)) return RemoteChecks.Conflict("approval_key_exists");
        if (live.Count >= ActiveMax) return RemoteChecks.Conflict(RemoteErrors.ApprovalKeyLimit);
        var key = new ApprovalKey { UserId = userId, Name = name, PublicKey = Convert.ToBase64String(spki), Fingerprint = fingerprint, CreatedAt = clock.GetUtcNow() };
        db.ApprovalKeys.Add(key);
        Audit.Add(db, http, clock, AuditActions.ApprovalKeyRegistered, userId: userId, targetType: "approval_key", targetId: key.Id, detail: new { fingerprint });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same key was registered a moment ago by another call (ux on the user and fingerprint of a live key): that one stands.
            return RemoteChecks.Conflict("approval_key_exists");
        }

        return Results.Ok(new ApprovalKeyView(key.Id, key.Name, key.Fingerprint, key.PublicKey, key.CreatedAt, null));
    }

    private static async Task<IResult> Retire(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        // The retirement and its audit row commit together or not at all (F-194).
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var changed = await db.ApprovalKeys.Where(k => k.Id == id && k.UserId == userId && k.RetiredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RetiredAt, clock.GetUtcNow()), http.RequestAborted);
        if (changed != 1) return Http.NotFound();
        Audit.Add(db, http, clock, AuditActions.ApprovalKeyRetired, userId: userId, targetType: "approval_key", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }
}
