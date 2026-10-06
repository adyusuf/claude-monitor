using System.Text.Json;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>A machine named by the model, resolved to one agent; Error is the answer to give when it cannot be.</summary>
public sealed record Target(Guid? AgentId, string Hostname, string Os, string? Error);

/// <summary>
/// Resolves "machine" (an agent id, or a host name) to exactly one agent of the workspace. A host name with several
/// agents (a user agent and a service agent) is refused with the candidates; an unknown name and another workspace's
/// look the same. Host names are compared case-insensitively (they are DNS names, not user text).
/// </summary>
public static class Targets
{
    public static async Task<Target> ResolveAsync(RemoteRequests requests, string? machine)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (string.IsNullOrWhiteSpace(machine)) return new Target(null, "", "", "Name the machine: a host name or an agent id from monitor_machines.");
        var answer = await requests.AskAsync("machines", HttpMethod.Get, "api/agent/machines", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return new Target(null, "", "", MachineTools.Failure(answer.Error!));
        var machines = answer.Json!.Value.Deserialize<List<MachineView>>(ApiClient.Json) ?? [];
        var name = machine.Trim();
        var found = Guid.TryParse(name, out var id)
            ? machines.Where(m => m.AgentId == id).ToList()
            : machines.Where(m => string.Equals(m.Hostname, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count switch
        {
            1 => new Target(found[0].AgentId, found[0].Hostname, found[0].Os, null),
            0 => new Target(null, "", "", $"Not found: no machine '{RemoteEnvelope.Label(name)}' in this workspace (see monitor_machines)."),
            _ => new Target(null, "", "", $"{RemoteErrors.Ambiguous}: '{RemoteEnvelope.Label(name)}' has several agents; name one by id: "
                + string.Join(", ", found.Select(m => $"{m.AgentId} (owner {RemoteEnvelope.Label(m.UserName)}, service={(m.ServiceMode ? "yes" : "no")}, exec={m.ExecLevel})"))),
        };
    }
}
