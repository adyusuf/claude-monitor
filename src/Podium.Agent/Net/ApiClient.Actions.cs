using System.Net;
using Podium.Contracts;

namespace Podium.Agent.Net;

// Fixed actions asked for on the web (ADR-0005, "Agent actions from the web").
public sealed partial class ApiClient
{
    /// <summary>
    /// The agent's report on an action. False when the API says it is not in the state the report needs (409: already taken, finished or expired)
    /// or is not this agent's (404): "delivered" answered false means the action is not to be performed.
    /// </summary>
    public Task<bool> ActionStatusAsync(Guid id, string status, string? result, CancellationToken ct, string? sessionRef = null) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, $"api/agent/actions/{id}/status", new AgentActionReport(status, result, sessionRef)), t);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        await EnsureAsync(response, t);
        return true;
    }, ct);
}
