using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>What an allow asks a rule to be: its scope, the machine's owner and workspace, and how long it stands.</summary>
internal sealed record RuleAsk(Guid WorkspaceId, Guid UserId, Guid AgentId, Guid? RepoId, string Tool, string? Command, string Duration,
    DateTimeOffset Now, Guid FromRequest);

/// <summary>
/// Making the approval rule an owner's allow asks for (ADR-0019), for <see cref="CommandEndpoints"/>' answer: the checks, the new rule or the change
/// to an existing one, and their audit rows. The answer saves them together with itself.
/// </summary>
internal static class ApprovalRuleCreation
{
    /// <summary>
    /// Checks the approval rule an allow asks for (ADR-0019) and puts it in place: never on a strict machine or for a tool that cannot have one;
    /// an <c>always</c> rule needs re-authentication. The rule's scope is the request's own, its project the one the request was made in.
    /// A refusal, or the rule asked for (saved with the answer).
    /// </summary>
    public static async Task<(IResult? Refused, RuleAsk? Ask, Guid? Existing)> RememberAsync(PodiumDb db, HttpContext http, ApiConfig config, TimeProvider clock,
        Guid userId, PermissionRequest request, string remember, string? code, DateTimeOffset now)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AgentId, http.RequestAborted);
        if (agent is null || agent.UserId != userId || agent.Status != AgentStatuses.Active || agent.WorkspaceId != request.WorkspaceId || ApprovalRules.Strict(agent)
            || ApprovalRules.ScopeOf(request.ToolName, request.ToolInput.RootElement) is not { } scope)
        {
            return (Results.Problem(statusCode: StatusCodes.Status409Conflict, title: CommandEndpoints.RuleNotAllowed), null, null);
        }

        if (!await new ApprovalRuleAccess(db).AllowsAsync(agent, request.RepoId, request.ToolName, request.ToolInput.RootElement, request.AccessScope,
                http.RequestAborted))
        {
            return (Results.Problem(statusCode: StatusCodes.Status409Conflict, title: CommandEndpoints.RuleOutsideAccess), null, null);
        }

        if (remember == ApprovalDurations.Always && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, code)) return (RemoteChecks.ReauthRefused(), null, null);
        var ask = new RuleAsk(agent.WorkspaceId, userId, agent.Id, request.RepoId, scope.Tool, scope.Command, remember, now, request.Id);
        var (refused, existing) = await ApplyRuleAsync(db, http, clock, ask);
        return (refused, ask, existing);
    }

    /// <summary>
    /// Puts the rule of one scope in place: a new one is added with its audit row (saved with the answer); an existing one, live or ended, is
    /// named so that <see cref="StandLongerAsync"/> makes it stand as long as asked in the answer's transaction. A refusal instead: the cap.
    /// </summary>
    public static async Task<(IResult? Refused, Guid? Existing)> ApplyRuleAsync(PodiumDb db, HttpContext http, TimeProvider clock, RuleAsk ask)
    {
        var ct = http.RequestAborted;
        var now = ask.Now;
        var rule = await db.ApprovalRules.AsNoTracking().Where(r => r.AgentId == ask.AgentId && r.UserId == ask.UserId && r.WorkspaceId == ask.WorkspaceId
                && r.RepoId == ask.RepoId && r.ToolName == ask.Tool && r.Command == ask.Command && r.RevokedAt == null)
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var ended = rule is null || rule.ExpiresAt is { } end && end <= now;
        if (ended)
        {
            var live = await db.ApprovalRules.CountAsync(r => r.UserId == ask.UserId && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now), ct);
            if (live >= ApprovalRuleLimits.LiveMax) return (Results.Problem(statusCode: StatusCodes.Status409Conflict, title: CommandEndpoints.RuleLimit), null);
        }

        if (rule is null)
        {
            rule = new ApprovalRule
            {
                WorkspaceId = ask.WorkspaceId,
                UserId = ask.UserId,
                AgentId = ask.AgentId,
                RepoId = ask.RepoId,
                ToolName = ask.Tool,
                Command = ask.Command,
                CreatedAt = now,
                CreatedFromRequest = ask.FromRequest,
                Duration = ask.Duration,
                ExpiresAt = EndOf(ask),
            };
            db.ApprovalRules.Add(rule);
        }

        if (db.Entry(rule).State != EntityState.Added) return (null, rule.Id); // audited by the answer's save, when the change is made
        AuditRule(db, http, clock, ask, rule.Id);
        return (null, null);
    }

    public static void AuditRule(PodiumDb db, HttpContext http, TimeProvider clock, RuleAsk ask, Guid ruleId) =>
        Audit.Add(db, http, clock, AuditActions.ApprovalRuleCreated, ask.WorkspaceId, ask.UserId, targetType: "approval_rule", targetId: ruleId,
            detail: new { request = ask.FromRequest, ToolName = ask.Tool, ask.Duration });

    private static DateTimeOffset? EndOf(RuleAsk ask) => ApprovalDurations.Length(ask.Duration) is { } length ? ask.Now + length : null;

    /// <summary>
    /// Makes an existing rule stand as long as asked, in one conditional update: an ended rule starts again, a live one is extended to a later
    /// end or to always, and is never narrowed. The database checks the end again on the locked row, so two answers that extend the same rule
    /// at once leave it at the longer of the two. True when the rule changed (an always rule, a later end, or a rule revoked meanwhile: false).
    /// </summary>
    public static async Task<bool> StandLongerAsync(PodiumDb db, Guid ruleId, RuleAsk ask, CancellationToken ct)
    {
        var now = ask.Now;
        var ends = EndOf(ask);
        return 1 == await db.ApprovalRules
            .Where(r => r.Id == ruleId && r.RevokedAt == null && r.ExpiresAt != null && (r.ExpiresAt <= now || ends == null || r.ExpiresAt < ends))
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.CreatedAt, r => r.ExpiresAt <= now ? now : r.CreatedAt) // an ended rule of the same scope starts again (one row per scope)
                .SetProperty(r => r.CreatedFromRequest, r => r.ExpiresAt <= now ? ask.FromRequest : r.CreatedFromRequest)
                .SetProperty(r => r.Duration, ask.Duration)
                .SetProperty(r => r.ExpiresAt, ends), ct);
    }
}
