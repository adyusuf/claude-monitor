using System.Linq.Expressions;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

/// <summary>
/// What happens to an ask after it was created (ADR-0008 §3f): the target's daemon reports it delivered (with the session) or not taken, the answer
/// comes back once from that session, the requester may cancel, and the cascades cancel the open ones. Every transition is a compare-and-set on the
/// status, so a late report never overrides an answer that came first. The answer is masked on the target and arrives as text; here it is cut at
/// <see cref="ApiConfig.AskAnswerMax"/> and marked. Nothing here logs a question or an answer.
/// </summary>
public sealed class AskReports(PodiumDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    private static readonly string[] Open = [AskStatuses.Queued, AskStatuses.Delivered];

    /// <summary>The target's agent: received (it holds the ask), delivered (to a session), no idle session, or not accepting.</summary>
    public async Task<IResult> StatusAsync(Guid id, AskStatusUpdate req, Guid agentId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        if (!AskStatuses.FromTarget.Contains(req.Status ?? "")) return Http.Invalid("status", "invalid_status");
        var ask = await db.MachineAsks.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.TargetAgentId == agentId, ct);
        if (ask is null) return Http.NotFound();
        if (AskStatuses.Final.Contains(ask.Status)) return Results.Conflict();
        var now = clock.GetUtcNow();
        int changed;
        if (req.Status == AskStatuses.Received)
        {
            // A report, not a state: the ask stays queued. Said again (the daemon retries until it is heard) it is not an error.
            changed = await db.MachineAsks.Where(a => a.Id == id && a.ReceivedAt == null && Open.Contains(a.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReceivedAt, now), ct);
            if (changed == 1) Updated(ask);
            return Results.NoContent();
        }

        if (req.Status == AskStatuses.Delivered)
        {
            var session = req.SessionExternalId is { Length: > 0 and <= 200 } s && !s.Any(char.IsControl) ? s : null;
            changed = await db.MachineAsks.Where(a => a.Id == id && a.Status == AskStatuses.Queued && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AskStatuses.Delivered).SetProperty(a => a.DeliveredAt, now).SetProperty(a => a.TargetSession, session), ct);
        }
        else
        {
            var error = req.Error is { Length: > 0 } e ? Code(e) : req.Status;
            var status = req.Status!;
            changed = await db.MachineAsks.Where(a => a.Id == id && Open.Contains(a.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status).SetProperty(a => a.EndedAt, now).SetProperty(a => a.Error, error), ct);
        }

        if (changed != 1) return Results.Conflict();
        ask.Status = req.Status!;
        Updated(ask);
        return Results.NoContent();
    }

    /// <summary>The answer, once, from the session that received the ask. Both users are checked again, as at create and delivery.</summary>
    public async Task<IResult> AnswerAsync(Guid id, AskAnswer req, Guid agentId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        if (req.Text is not { Length: >= 1 } text || text.Contains('\0')) return Http.Invalid("text", AskErrors.InvalidAnswer);
        var ask = await db.MachineAsks.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.TargetAgentId == agentId, ct);
        if (ask is null) return Http.NotFound();
        if (ask.Status != AskStatuses.Delivered) return Conflict(AskErrors.NotOpen);
        if (ask.TargetSession is not null && req.SessionExternalId != ask.TargetSession) return Conflict(AskErrors.Mismatch);
        var target = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, ct);
        if (target is null || !target.AcceptsAsks || await Access.MemberAsync(db, ask.TargetUserId, ask.WorkspaceId, Roles.Member, ct) is null) return Conflict(AskErrors.NotOpen);
        var now = clock.GetUtcNow();
        var truncated = text.Length > config.AskAnswerMax;
        var stored = truncated ? text[..config.AskAnswerMax] : text;
        var changed = await db.MachineAsks.Where(a => a.Id == id && a.Status == AskStatuses.Delivered)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AskStatuses.Answered).SetProperty(a => a.Answer, stored).SetProperty(a => a.AnswerTruncated, truncated)
                .SetProperty(a => a.AnsweredAt, now).SetProperty(a => a.EndedAt, now), ct);
        if (changed != 1) return Conflict(AskErrors.NotOpen);
        Audit.Add(db, http, clock, AuditActions.AskAnswered, ask.WorkspaceId, ask.TargetUserId, agentId, targetType: "ask", targetId: id);
        await db.SaveChangesAsync(ct);
        ask.Status = AskStatuses.Answered;
        Updated(ask);
        return Results.NoContent();
    }

    /// <summary>The requester's agent withdraws an ask that is still open; the target's daemon hears it.</summary>
    public async Task<IResult> CancelAsync(Guid id, Guid userId, Guid agentId, HttpContext http)
    {
        var ct = http.RequestAborted;
        var ask = await db.MachineAsks.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.RequesterAgentId == agentId && a.RequesterUserId == userId, ct);
        if (ask is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var changed = await db.MachineAsks.Where(a => a.Id == id && Open.Contains(a.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AskStatuses.Cancelled).SetProperty(a => a.EndedAt, now), ct);
        if (changed != 1) return Conflict(AskErrors.NotOpen);
        Audit.Add(db, http, clock, AuditActions.AskCancelled, ask.WorkspaceId, userId, agentId, targetType: "ask", targetId: id);
        await db.SaveChangesAsync(ct);
        var was = ask.Status;
        ask.Status = AskStatuses.Cancelled;
        Updated(ask);
        if (was == AskStatuses.Delivered || was == AskStatuses.Queued) broker.Publish(Broker.Agent(ask.TargetAgentId), new StreamMessage(AgentStreamEvents.AskCancel, new AskCancelMessage(ask.Id)));
        return Results.NoContent();
    }

    /// <summary>
    /// Cancels every open ask the filter selects (a member removed, an agent moved or revoked, the switch or an opt-in turned off), one compare-and-set
    /// each in id order; the notices go to <paramref name="after"/> when the caller has a transaction to commit first.
    /// </summary>
    public static async Task CancelOpenAsync(PodiumDb db, Broker broker, DateTimeOffset now, Expression<Func<MachineAsk, bool>> filter, CancellationToken ct,
        List<Action>? after = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        var asks = await db.MachineAsks.AsNoTracking().Where(a => Open.Contains(a.Status)).Where(filter).OrderBy(a => a.Id).ToListAsync(ct);
        foreach (var ask in asks)
        {
            var changed = await db.MachineAsks.Where(a => a.Id == ask.Id && a.Status == ask.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AskStatuses.Cancelled).SetProperty(a => a.EndedAt, now), ct);
            if (changed != 1) continue;
            ask.Status = AskStatuses.Cancelled;
            void Notify()
            {
                broker.Publish(Broker.Agent(ask.RequesterAgentId), new StreamMessage(AgentStreamEvents.AskUpdate, new AskUpdateMessage(ask.Id, AskStatuses.Cancelled)));
                broker.Publish(Broker.Agent(ask.TargetAgentId), new StreamMessage(AgentStreamEvents.AskCancel, new AskCancelMessage(ask.Id)));
            }

            if (after is null) Notify();
            else after.Add(Notify);
        }
    }

    /// <summary>The ask's open asks of an agent, as requester or target (the opt-in turned off, the agent moved or revoked).</summary>
    public static Task CancelOpenForAgentAsync(PodiumDb db, Broker broker, Guid agentId, DateTimeOffset now, CancellationToken ct, List<Action>? after = null) =>
        CancelOpenAsync(db, broker, now, a => a.RequesterAgentId == agentId || a.TargetAgentId == agentId, ct, after);

    private void Updated(MachineAsk ask) =>
        broker.Publish(Broker.Agent(ask.RequesterAgentId), new StreamMessage(AgentStreamEvents.AskUpdate, new AskUpdateMessage(ask.Id, ask.Status)));

    /// <summary>A short code for an error the target reported: lowercase letters, digits and underscore, at most 50.</summary>
    private static string Code(string value) =>
        value.Length <= 50 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ? value : "unknown";

    private static IResult Conflict(string error) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: error);
}
