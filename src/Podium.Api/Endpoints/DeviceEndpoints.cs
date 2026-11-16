using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

public sealed record DeviceLookupResponse(string UserCode, string Hostname, string Os, string Arch, string AgentVersion,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record DeviceDecision(string? UserCode, Guid? WorkspaceId);

/// <summary>Connecting an agent (RFC 8628 device authorisation): the agent polls, a signed-in person approves.</summary>
public static class DeviceEndpoints
{
    /// <summary>The poll of <c>/device/token</c> has its own, looser per-IP budget (<see cref="ApiConfig.DeviceTokenRequestsPerMinute"/>).</summary>
    public const string TokenRateLimitPolicy = "device-token";

    /// <summary>The audit detail's reason when a re-login of the same machine into another workspace revoked an agent (F-211).</summary>
    public const string ReplacedByReloginReason = "relogin";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/device/code", Code).RequireRateLimiting(AuthEndpoints.RateLimitPolicy).AllowAnonymous();
        api.MapPost("/device/token", Token).RequireRateLimiting(TokenRateLimitPolicy).AllowAnonymous();
        var web = api.MapGroup("/device").RequireAuthorization(Schemes.Session).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        web.MapGet("/lookup/{userCode}", Lookup);
        web.MapPost("/approve", Approve);
        web.MapPost("/deny", Deny);
    }

    private static async Task<IResult> Code(DeviceCodeRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        if (req.MachineKey is not { Length: >= 16 and <= 200 }) return Http.Invalid("machineKey", "invalid");
        if (!IsMachineFact(req.Hostname, 255)) return Http.Invalid("hostname", "invalid");
        if (!OsKinds.All.Contains(req.Os)) return Http.Invalid("os", "invalid");
        if (!IsMachineFact(req.Arch, 20)) return Http.Invalid("arch", "invalid");
        if (!Version.TryParse(req.AgentVersion, out var version)) return Http.Invalid("agentVersion", "invalid");
        if (version < config.MinimumAgentVersion) return UpgradeRequired();

        var now = clock.GetUtcNow();
        var deviceCode = Secrets.NewToken();
        string userCode;
        do
        {
            userCode = Secrets.NewUserCode();
        }
        while (await db.DeviceAuthorizations.AnyAsync(
                   d => d.UserCode == userCode && d.Status == DeviceStatuses.Pending, http.RequestAborted));

        db.DeviceAuthorizations.Add(new DeviceAuthorization
        {
            DeviceCodeHash = Secrets.Hash(deviceCode),
            UserCode = userCode,
            MachineKeyHash = Secrets.Hash("machine:" + req.MachineKey),
            RequestedHostname = req.Hostname.Trim(),
            RequestedOs = req.Os,
            RequestedArch = req.Arch.Trim(),
            AgentVersion = req.AgentVersion,
            CreatedAt = now,
            ExpiresAt = now + config.DeviceCodeLifetime,
        });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new DeviceCodeResponse(deviceCode, userCode, config.PublicOrigin + "/device",
            config.DevicePollSeconds, (int)config.DeviceCodeLifetime.TotalSeconds));
    }

    /// <summary>A hostname or arch is shown to the approver and to every member and reaches agent tools: one plain line, so no
    /// format character either (bidi overrides, zero-width marks), on top of the control characters <see cref="Http.IsName"/> refuses.</summary>
    private static bool IsMachineFact(string? value, int max) =>
        Http.IsName(value, max) && !value!.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private static async Task<IResult> Token(DeviceTokenRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(req.DeviceCode ?? "");
        var device = await db.DeviceAuthorizations.FirstOrDefaultAsync(d => d.DeviceCodeHash == hash, http.RequestAborted);
        if (device is null || device.Status is DeviceStatuses.Consumed or DeviceStatuses.Expired) return Error(DeviceTokenErrors.Expired);
        if (device.ExpiresAt <= now)
        {
            device.Status = DeviceStatuses.Expired;
            await db.SaveChangesAsync(http.RequestAborted);
            return Error(DeviceTokenErrors.Expired);
        }

        var tooSoon = device.LastPolledAt is { } last && now - last < TimeSpan.FromSeconds(config.DevicePollSeconds - 1);
        device.LastPolledAt = now;
        switch (device.Status)
        {
            case DeviceStatuses.Denied:
                await db.SaveChangesAsync(http.RequestAborted);
                return Error(DeviceTokenErrors.Denied);
            case DeviceStatuses.Approved:
                break;
            default:
                await db.SaveChangesAsync(http.RequestAborted);
                return Error(tooSoon ? DeviceTokenErrors.SlowDown : DeviceTokenErrors.Pending);
        }

        // The approval was given under a membership that may be gone by now: it is asked again before the agent exists.
        if (await Access.MemberAsync(db, device.ApprovedBy!.Value, device.WorkspaceId!.Value, Roles.Viewer, http.RequestAborted) is null)
        {
            device.Status = DeviceStatuses.Denied;
            await db.SaveChangesAsync(http.RequestAborted);
            return Error(DeviceTokenErrors.Denied);
        }

        // Single use under a race: the code is claimed by a compare-and-set on Status = approved, and the claim, the agent and its
        // tokens commit together. A parallel poll waits on the claimed row, changes nothing and hears that the code is used up.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var claimed = await db.DeviceAuthorizations.Where(d => d.Id == device.Id && d.Status == DeviceStatuses.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatuses.Consumed), http.RequestAborted);
        if (claimed == 0) return Error(DeviceTokenErrors.Expired);
        var (agent, replaced) = await EnrolAsync(db, http, clock, device, now, http.RequestAborted);
        var tokens = AgentTokens.Issue(db, config, agent, now);
        await db.SaveChangesAsync(http.RequestAborted);
        // The agents a re-login replaced in other workspaces end like a revoked one: their remote work ends with them (ADR-0005).
        var notices = new List<Action>();
        if (replaced.Count > 0) await RemoteCleanup.ForAgentsAsync(db, broker, replaced, agent.UserId, now, http.RequestAborted, notices);
        await tx.CommitAsync(http.RequestAborted);
        notices.ForEach(n => n());
        replaced.ForEach(id => broker.Publish(Broker.Agent(id), new StreamMessage(AgentStreamEvents.Revoked, new { })));
        return Results.Ok(tokens);
    }

    /// <summary>The machine row (one per installation per workspace) and a fresh agent for the approving user.
    /// An earlier agent of the same user on the same machine is revoked: one active agent per user per machine. So is one of the same
    /// user and machine key in another workspace (F-211): its tokens end and its workspace's audit log names the agent that replaced it.
    /// Returns the new agent and the ids of those other-workspace agents.</summary>
    private static async Task<(Agent Agent, List<Guid> Replaced)> EnrolAsync(PodiumDb db, HttpContext http, TimeProvider clock,
        DeviceAuthorization device, DateTimeOffset now, CancellationToken ct)
    {
        var workspaceId = device.WorkspaceId!.Value;
        var userId = device.ApprovedBy!.Value;
        var machine = await db.Machines.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.MachineKeyHash == device.MachineKeyHash, ct);
        if (machine is null)
        {
            machine = new Machine { WorkspaceId = workspaceId, MachineKeyHash = device.MachineKeyHash, FirstSeenAt = now };
            db.Machines.Add(machine);
        }

        machine.Hostname = device.RequestedHostname;
        machine.Os = device.RequestedOs;
        machine.Arch = device.RequestedArch;
        machine.LastSeenAt = now;

        var previous = await db.Agents.Where(a => a.MachineId == machine.Id && a.UserId == userId && a.Status == AgentStatuses.Active)
            .ToListAsync(ct);
        // A machine row is per workspace, so the same installation's agents in the user's other workspaces are found by the key.
        var elsewhere = await (from a in db.Agents
                               join m in db.Machines on a.MachineId equals m.Id
                               where a.UserId == userId && a.Status == AgentStatuses.Active
                                     && m.MachineKeyHash == device.MachineKeyHash && m.WorkspaceId != workspaceId
                               select a).ToListAsync(ct);
        var agent = new Agent
        {
            MachineId = machine.Id,
            UserId = userId,
            WorkspaceId = workspaceId,
            Version = device.AgentVersion,
            EnrolledAt = now,
            LastHeartbeatAt = now,
        };
        foreach (var old in previous.Concat(elsewhere))
        {
            old.Status = AgentStatuses.Revoked;
            old.RevokedAt = now;
            old.RevokedBy = userId;
        }

        foreach (var old in elsewhere)
        {
            await AgentTokens.RevokeAllAsync(db, old.Id, now, ct);
            Audit.Add(db, http, clock, AuditActions.AgentRevoked, old.WorkspaceId, userId, targetType: "agent", targetId: old.Id,
                detail: new
                {
                    reason = ReplacedByReloginReason, oldAgentId = old.Id, oldWorkspaceId = old.WorkspaceId, newAgentId = agent.Id,
                    newWorkspaceId = workspaceId,
                });
        }

        // The old agent's standing approvals (ADR-0019) end with it.
        var oldIds = previous.Select(a => a.Id).ToList();
        await db.ApprovalRules.Where(r => oldIds.Contains(r.AgentId) && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);

        // The partial unique index (one active agent per machine and user) is checked per statement, so the
        // revocations must reach the database before the new agent is inserted.
        await db.SaveChangesAsync(ct);
        db.Agents.Add(agent);
        return (agent, elsewhere.Select(a => a.Id).ToList());
    }

    private static async Task<IResult> Lookup(string userCode, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var device = await PendingAsync(db, userCode, clock, http.RequestAborted);
        return device is null
            ? Http.NotFound()
            : Results.Ok(new DeviceLookupResponse(device.UserCode, device.RequestedHostname, device.RequestedOs,
                device.RequestedArch, device.AgentVersion, device.CreatedAt, device.ExpiresAt));
    }

    private static async Task<IResult> Approve(DeviceDecision req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await DecidingWorkspaceAsync(db, userId, req, http.RequestAborted) is not { } workspaceId)
        {
            return Http.Invalid("workspaceId", "not_a_member");
        }

        var device = await PendingAsync(db, req.UserCode, clock, http.RequestAborted, track: true);
        if (device is null) return Http.Invalid("userCode", "invalid_code");
        device.Status = DeviceStatuses.Approved;
        device.ApprovedBy = userId;
        device.WorkspaceId = workspaceId;
        Audit.Add(db, http, clock, AuditActions.DeviceApproved, workspaceId, userId, targetType: "device", targetId: device.Id,
            detail: new { device.RequestedHostname, device.RequestedOs });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>
    /// Denying takes the same authority as approving. A pending code belongs to no one until it is approved: holding
    /// the user code is the credential (RFC 8628), so the decider must at least be a member of the workspace the
    /// device was offered to. Guessing a code to deny someone else's enrolment is out of reach: about 34 bits, minutes
    /// long, rate-limited per address; and a denied enrolment simply starts again (docs/security.md, A01).
    /// </summary>
    private static async Task<IResult> Deny(DeviceDecision req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await DecidingWorkspaceAsync(db, userId, req, http.RequestAborted) is not { } workspaceId)
        {
            return Http.Invalid("workspaceId", "not_a_member");
        }

        var device = await PendingAsync(db, req.UserCode, clock, http.RequestAborted, track: true);
        if (device is null) return Http.Invalid("userCode", "invalid_code");
        device.Status = DeviceStatuses.Denied;
        Audit.Add(db, http, clock, AuditActions.DeviceDenied, workspaceId, userId, targetType: "device", targetId: device.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>The decision's workspace when the caller is at least a member of it; otherwise null.</summary>
    private static async Task<Guid?> DecidingWorkspaceAsync(PodiumDb db, Guid userId, DeviceDecision req, CancellationToken ct) =>
        req.WorkspaceId is { } workspaceId && await Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is not null
            ? workspaceId
            : null;

    private static async Task<DeviceAuthorization?> PendingAsync(PodiumDb db, string? userCode, TimeProvider clock,
        CancellationToken ct, bool track = false)
    {
        var code = Secrets.NormalizeUserCode(userCode);
        var now = clock.GetUtcNow();
        var query = track ? db.DeviceAuthorizations : db.DeviceAuthorizations.AsNoTracking();
        return await query.FirstOrDefaultAsync(d => d.UserCode == code && d.Status == DeviceStatuses.Pending && d.ExpiresAt > now, ct);
    }

    private static IResult Error(string code) => Results.BadRequest(new DeviceTokenError(code));

    public static IResult UpgradeRequired() =>
        Results.Problem(statusCode: StatusCodes.Status426UpgradeRequired, title: "upgrade_required");
}
