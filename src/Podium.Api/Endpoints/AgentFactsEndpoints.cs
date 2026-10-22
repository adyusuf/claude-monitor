using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>An admin's tags for a machine; null or empty clears them.</summary>
public sealed record AgentTagsRequest(Dictionary<string, string>? Tags);

/// <summary>An admin's runbook note for a machine; null clears it.</summary>
public sealed record AgentNoteRequest(string? Note);

/// <summary>The machine owner's choice to report listening services. Code: a two-step code when enabling needs a recent sign-in.</summary>
public sealed record AgentServicesRequest(bool Share, string? Code = null);

public sealed record AgentServicesView(Guid AgentId, bool Share, DateTimeOffset? SetAt, Guid? SetBy);

/// <summary>The machine owner's choice to report what Claude Code has on the machine (ADR-0011 §7a, step E). Code: a two-step code when enabling needs a recent sign-in.</summary>
public sealed record AgentInventoryRequest(bool Share, string? Code = null);

public sealed record AgentInventoryView(Guid AgentId, bool Share, DateTimeOffset? SetAt, Guid? SetBy);

/// <summary>
/// What a person types about a machine (ADR-0008 §3a′). Tags and the note: only a workspace admin (anyone else gets 404, as for a
/// workspace they cannot see), every change audited; the note is untrusted text and only its length is audited. Listening services:
/// only the machine's owner decides whether the agent reports them, enabling needs the re-authentication of the grants, and turning
/// it off drops what was stored.
/// </summary>
public static class AgentFactsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents").RequireAuthorization(Schemes.Session);
        g.MapPut("/{id:guid}/tags", PutTags);
        g.MapPut("/{id:guid}/note", PutNote);
        g.MapPut("/{id:guid}/services", PutServices);
        g.MapPut("/{id:guid}/inventory", PutInventory);
    }

    private static async Task<IResult> PutTags(Guid id, AgentTagsRequest req, HttpContext http, PodiumDb db, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await AdminAgentAsync(db, http, userId, id);
        if (agent is null) return Http.NotFound();
        var tags = req.Tags is { Count: > 0 } t ? t : null;
        if (!MachineTags.IsValid(tags)) return Http.Invalid("tags", RemoteErrors.InvalidTag);
        var now = clock.GetUtcNow();
        var before = MachineFacts.TagsFrom(agent.Tags);
        agent.Tags = tags is null ? null : JsonSerializer.Serialize(tags.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), Json);
        agent.TagsSetAt = now;
        agent.TagsSetBy = userId;
        Audit.Add(db, http, clock, AuditActions.AgentTagsChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { before, after = MachineFacts.TagsFrom(agent.Tags) });
        await db.SaveChangesAsync(http.RequestAborted);
        Changed(broker, agent);
        return Results.Ok(new { agentId = id, tags = MachineFacts.TagsFrom(agent.Tags) ?? new Dictionary<string, string>() });
    }

    private static async Task<IResult> PutNote(Guid id, AgentNoteRequest req, HttpContext http, PodiumDb db, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await AdminAgentAsync(db, http, userId, id);
        if (agent is null) return Http.NotFound();
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note;
        if (!MachineTags.IsNote(note)) return Http.Invalid("note", RemoteErrors.InvalidNote);
        var before = agent.AdminNote?.Length ?? 0;
        agent.AdminNote = note;
        agent.AdminNoteSetAt = clock.GetUtcNow();
        agent.AdminNoteSetBy = userId;
        Audit.Add(db, http, clock, AuditActions.AgentNoteChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { beforeLength = before, afterLength = note?.Length ?? 0 });
        await db.SaveChangesAsync(http.RequestAborted);
        Changed(broker, agent);
        return Results.Ok(new { agentId = id, note = agent.AdminNote });
    }

    private static async Task<IResult> PutServices(Guid id, AgentServicesRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock,
        Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        if (req.Share && !agent.ShareServices && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code))
        {
            return RemoteChecks.ReauthRefused();
        }

        var was = agent.ShareServices;
        agent.ShareServices = req.Share;
        agent.ShareServicesSetAt = clock.GetUtcNow();
        agent.ShareServicesSetBy = userId;
        if (!req.Share)
        {
            agent.Services = null;
            agent.ServicesAt = null;
        }

        Audit.Add(db, http, clock, AuditActions.AgentServicesChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { before = was, after = req.Share });
        await db.SaveChangesAsync(http.RequestAborted);
        Changed(broker, agent);
        return Results.Ok(new AgentServicesView(id, agent.ShareServices, agent.ShareServicesSetAt, agent.ShareServicesSetBy));
    }

    /// <summary>The owner's switch for the Claude inventory (ADR-0011 §7a, step E): owner only, a recent sign-in to turn it on, an audit row, and turning it off drops what was stored.</summary>
    private static async Task<IResult> PutInventory(Guid id, AgentInventoryRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        if (req.Share && !agent.ShareInventory && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code))
        {
            return RemoteChecks.ReauthRefused();
        }

        var was = agent.ShareInventory;
        agent.ShareInventory = req.Share;
        agent.ShareInventorySetAt = clock.GetUtcNow();
        agent.ShareInventorySetBy = userId;
        if (!req.Share)
        {
            agent.ClaudeInventory = null;
            agent.ClaudeInventoryAt = null;
        }

        Audit.Add(db, http, clock, AuditActions.AgentInventoryChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id, detail: new { before = was, after = req.Share });
        await db.SaveChangesAsync(http.RequestAborted);
        Changed(broker, agent);
        return Results.Ok(new AgentInventoryView(id, agent.ShareInventory, agent.ShareInventorySetAt, agent.ShareInventorySetBy));
    }

    private static async Task<Agent?> AdminAgentAsync(PodiumDb db, HttpContext http, Guid userId, Guid id)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        return agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Admin, http.RequestAborted) is null ? null : agent;
    }

    private static void Changed(Broker broker, Agent agent) =>
        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(AgentRoleEndpoints.MachineEvent, new { agentId = agent.Id }));
}
