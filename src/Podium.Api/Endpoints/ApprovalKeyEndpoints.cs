using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

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
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code)) return RemoteChecks.ReauthRefused();
        var fingerprint = ApprovalSigning.Fingerprint(spki);
        var live = await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == userId && k.RetiredAt == null).ToListAsync(ct);
        if (live.Any(k => k.Fingerprint == fingerprint)) return RemoteChecks.Conflict("approval_key_exists");
        if (live.Count >= ActiveMax) return RemoteChecks.Conflict(RemoteErrors.ApprovalKeyLimit);
        var key = new ApprovalKey { UserId = userId, Name = name, PublicKey = Convert.ToBase64String(spki), Fingerprint = fingerprint, CreatedAt = clock.GetUtcNow() };
        db.ApprovalKeys.Add(key);
        Audit.Add(db, http, clock, AuditActions.ApprovalKeyRegistered, userId: userId, targetType: "approval_key", targetId: key.Id, detail: new { fingerprint });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ApprovalKeyView(key.Id, key.Name, key.Fingerprint, key.PublicKey, key.CreatedAt, null));
    }

    private static async Task<IResult> Retire(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var changed = await db.ApprovalKeys.Where(k => k.Id == id && k.UserId == userId && k.RetiredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RetiredAt, clock.GetUtcNow()), http.RequestAborted);
        if (changed != 1) return Http.NotFound();
        Audit.Add(db, http, clock, AuditActions.ApprovalKeyRetired, userId: userId, targetType: "approval_key", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }
}
