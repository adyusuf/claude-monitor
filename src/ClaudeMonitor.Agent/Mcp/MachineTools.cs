using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>
/// The workspace's machines as the model sees them (ADR-0004): who is there, how loaded they are, and which alerts are
/// open. Read-only; the agent never acts on an alert, the model decides what to ask for.
/// </summary>
[McpServerToolType]
public sealed class MachineTools(AgentConfig config, TimeProvider clock)
{
    private readonly RemoteRequests requests = new(config, clock);

    [McpServerTool(Name = "monitor_machines", ReadOnly = true)]
    [Description("Lists the machines (agents) of this Claude Monitor workspace: host name, agent id, OS, owner, whether it accepts remote runs (exec level off/argv/shell), whether it runs as a service, online, latest CPU/memory/disk and open alerts. Service agents come first. Use the agent id to address a machine whose host name has several agents.")]
    public async Task<string> Machines()
    {
        var answer = await requests.AskAsync("machines", HttpMethod.Get, "api/agent/machines", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return Failure(answer.Error!);
        var machines = answer.Json!.Value.Deserialize<List<MachineView>>(ApiClient.Json) ?? [];
        if (machines.Count == 0) return "No machines in this workspace.";
        var b = new StringBuilder();
        foreach (var m in machines) b.AppendLine(Line(m));
        return RemoteEnvelope.Wrap("machines", "workspace", "all", b.ToString().TrimEnd());
    }

    [McpServerTool(Name = "monitor_metrics", ReadOnly = true)]
    [Description("CPU, memory and disk samples of one machine over the last minutes (one sample a minute).")]
    public async Task<string> Metrics(
        [Description("Host name or agent id (from monitor_machines).")] string machine,
        [Description("How many minutes back, 1-1440 (default 60).")] int minutes = 60)
    {
        var target = await Targets.ResolveAsync(requests, machine);
        if (target.Error is { } error) return error;
        var answer = await requests.AskAsync("metrics", HttpMethod.Get,
            $"api/agent/machines/{target.AgentId}/metrics?minutes={Math.Clamp(minutes, 1, 1440)}", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return Failure(answer.Error!);
        var samples = answer.Json!.Value.Deserialize<List<MetricSample>>(ApiClient.Json) ?? [];
        if (samples.Count == 0) return $"No samples from {RemoteEnvelope.Label(target.Hostname)} in the last {minutes} minutes.";
        var b = new StringBuilder();
        foreach (var s in samples) b.AppendLine(CultureInfo.InvariantCulture, $"{s.SampledAt:HH:mm} {Sample(s)}");
        return RemoteEnvelope.Wrap("metrics", target.AgentId.ToString()!, target.Hostname, b.ToString().TrimEnd());
    }

    [McpServerTool(Name = "monitor_alerts", ReadOnly = true)]
    [Description("Open resource alerts (cpu, memory, disk, offline) of the workspace or of one machine. The agent only reports them; deciding what to do is yours, and anything with side effects needs the user.")]
    public async Task<string> Alerts(
        [Description("Host name or agent id; empty for every machine.")] string? machine = null,
        [Description("Include resolved alerts too.")] bool includeResolved = false)
    {
        var filter = "";
        if (!string.IsNullOrWhiteSpace(machine))
        {
            var target = await Targets.ResolveAsync(requests, machine);
            if (target.Error is { } error) return error;
            filter = $"agent={target.AgentId}&";
        }

        var answer = await requests.AskAsync("alerts", HttpMethod.Get, $"api/agent/alerts?{filter}resolved={(includeResolved ? "true" : "false")}",
            null, RemoteRequests.ReadWait);
        if (!answer.Ok) return Failure(answer.Error!);
        var alerts = answer.Json!.Value.Deserialize<List<AlertView>>(ApiClient.Json) ?? [];
        if (alerts.Count == 0) return includeResolved ? "No alerts." : "No open alerts.";
        var b = new StringBuilder();
        foreach (var a in alerts)
        {
            b.AppendLine(CultureInfo.InvariantCulture,
                $"{a.State} {a.Kind}{(a.Subject.Length > 0 ? " " + a.Subject : "")} on {a.Hostname} (agent {a.AgentId}): {a.LastValue:0.#} (threshold {a.ThresholdPct:0.#}, peak {a.PeakValue:0.#}) since {a.OpenedAt:O}{(a.ResolvedAt is { } r ? $", resolved {r:O}" : "")}");
        }

        return RemoteEnvelope.Wrap("alerts", "workspace", machine ?? "all", b.ToString().TrimEnd());
    }

    internal static string Failure(string error) => error switch
    {
        RemoteAnswer.Pending => "The local agent has not answered yet (is the daemon running and connected? see monitor_status). Try again shortly.",
        Daemon.RemoteRelay.Unavailable => "This Claude Monitor server does not support remote work yet.",
        RemoteErrors.Disabled => "Remote runs are switched off for this workspace (an admin turns them on in the workspace settings).",
        _ => $"Refused: {RemoteEnvelope.Code(error)}.",
    };

    private static string Line(MachineView m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Hostname} (agent {m.AgentId}) os={m.Os} owner={m.UserName} exec={m.ExecLevel} service={(m.ServiceMode ? "yes" : "no")} online={(m.Online ? "yes" : "no")} alerts={m.OpenAlerts}{(m.Latest is { } s ? " " + Sample(s) : "")}");

    private static string Sample(MetricSample s) => string.Create(CultureInfo.InvariantCulture,
        $"cpu={s.CpuPct:0.#}% mem={Pct(s.MemUsedBytes, s.MemTotalBytes)}% {string.Join(' ', s.Disks.Select(d => $"disk[{d.Mount}]={Pct(d.UsedBytes, d.TotalBytes)}%"))}");

    private static string Pct(long used, long total) =>
        total <= 0 ? "?" : (100.0 * used / total).ToString("0.#", CultureInfo.InvariantCulture);
}
