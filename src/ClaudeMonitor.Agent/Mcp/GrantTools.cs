using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>
/// Standing permissions (grants) and named jobs on other machines (ADR-0004). Asking for either only creates a request;
/// it does nothing until the machine's owner approves it on the web. A shell command is never grantable.
/// </summary>
[McpServerToolType]
public sealed class GrantTools(AgentConfig config, TimeProvider clock)
{
    private readonly RemoteRequests requests = new(config, clock);

    [McpServerTool(Name = "monitor_request_grant")]
    [Description("Asks a machine's owner for a standing permission to run one command template without asking each time. The template is a fixed list: an absolute executable, literal arguments, and placeholders {int:1..5000}, {word}, {path:/allowed/root/}, {enum:a|b}. The working directory is fixed by the grant. Shells, interpreters (python, node, git, dotnet...) and options that execute (-exec, --to-command...) are refused. Nothing is allowed until the owner approves it on the web.")]
    public async Task<string> RequestGrant(
        [Description("Host name or agent id.")] string machine,
        [Description("The argv template, e.g. [\"/usr/bin/tail\", \"-n\", \"{int:1..5000}\", \"{path:/var/log/app/}\"].")] string[] template,
        [Description("The fixed working directory (absolute).")] string cwd,
        [Description("Why, shown to the owner (at most 500 characters).")] string reason,
        [Description("Longest timeout of a run under this grant, 1-3600 seconds (default 120).")] int maxTimeoutSeconds = 120,
        [Description("Days until it expires, 1-90 (default 30).")] int days = 30)
    {
        var target = await Targets.ResolveAsync(requests, machine);
        if (target.Error is { } error) return error;
        var problem = GrantMatcher.Validate(new GrantTemplate(template ?? [], cwd ?? "", maxTimeoutSeconds), target.Os, true);
        if (problem is not null) return $"Refused before asking: {problem}. A grant must be a fixed argv template without shells or interpreters.";
        var answer = await requests.AskAsync("grant", HttpMethod.Post, "api/agent/grants",
            new GrantRequest(target.AgentId!.Value, template!, cwd!, maxTimeoutSeconds, days, reason), RemoteRequests.ReadWait);
        if (!answer.Ok) return MachineTools.Failure(answer.Error!);
        var grant = answer.Json!.Value.Deserialize<GrantView>(ApiClient.Json)!;
        return $"Grant {grant.Id} on {target.Hostname}: {grant.Status}. It does nothing until the owner approves it on the web.";
    }

    [McpServerTool(Name = "monitor_grants", ReadOnly = true)]
    [Description("Your grants (requested, active, ended) on one machine or on all of them.")]
    public async Task<string> Grants([Description("Host name or agent id; empty for all.")] string? machine = null)
    {
        var filter = await FilterAsync(machine);
        if (filter.Error is { } error) return error;
        var answer = await requests.AskAsync("grants", HttpMethod.Get, $"api/agent/grants{filter.Query}", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return MachineTools.Failure(answer.Error!);
        var grants = answer.Json!.Value.Deserialize<List<GrantView>>(ApiClient.Json) ?? [];
        if (grants.Count == 0) return "No grants.";
        var b = new StringBuilder();
        foreach (var g in grants)
        {
            b.AppendLine(CultureInfo.InvariantCulture,
                $"{g.Id} on agent {g.TargetAgentId}: {g.Status}, [{string.Join(", ", g.Template)}] in {g.Cwd}, timeout ≤{g.MaxTimeoutSeconds}s, expires {g.ExpiresAt:O}, used {g.UseCount}x");
        }

        return RemoteEnvelope.Wrap("grants", "workspace", machine ?? "all", b.ToString().TrimEnd());
    }

    [McpServerTool(Name = "monitor_jobs", ReadOnly = true)]
    [Description("Named jobs (approved and proposed) on one machine or on all of them, e.g. a test run on a test machine.")]
    public async Task<string> Jobs([Description("Host name or agent id; empty for all.")] string? machine = null)
    {
        var filter = await FilterAsync(machine);
        if (filter.Error is { } error) return error;
        var answer = await requests.AskAsync("jobs", HttpMethod.Get, $"api/agent/jobs{filter.Query}", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return MachineTools.Failure(answer.Error!);
        var jobs = answer.Json!.Value.Deserialize<List<JobView>>(ApiClient.Json) ?? [];
        if (jobs.Count == 0) return "No jobs.";
        var b = new StringBuilder();
        foreach (var j in jobs)
        {
            b.AppendLine(CultureInfo.InvariantCulture,
                $"{j.Id} \"{j.Name}\" on agent {j.TargetAgentId}: {j.Status}, [{string.Join(", ", j.Argv)}] in {j.Cwd}, timeout {j.TimeoutSeconds}s");
        }

        return RemoteEnvelope.Wrap("jobs", "workspace", machine ?? "all", b.ToString().TrimEnd());
    }

    [McpServerTool(Name = "monitor_job_propose")]
    [Description("Proposes a named job on a machine: one fixed argv command (for example an admin-owned script that fetches and runs the tests). The owner approves it once on the web; after that monitor_job_run runs it without asking. Shell commands cannot be jobs.")]
    public async Task<string> ProposeJob(
        [Description("Host name or agent id.")] string machine,
        [Description("The job's name, unique on that machine.")] string name,
        [Description("The command: an absolute executable and its arguments.")] string[] argv,
        [Description("The working directory (absolute).")] string cwd,
        [Description("Why, shown to the owner.")] string reason,
        [Description("Timeout in seconds, 1-3600 (default 600).")] int timeoutSeconds = 600)
    {
        var target = await Targets.ResolveAsync(requests, machine);
        if (target.Error is { } error) return error;
        var answer = await requests.AskAsync("job", HttpMethod.Post, "api/agent/jobs",
            new JobProposal(target.AgentId!.Value, name, argv ?? [], cwd, timeoutSeconds, reason), RemoteRequests.ReadWait);
        if (!answer.Ok) return MachineTools.Failure(answer.Error!);
        var job = answer.Json!.Value.Deserialize<JobView>(ApiClient.Json)!;
        return $"Job {job.Id} \"{job.Name}\" on {target.Hostname}: {job.Status}. It runs only after the owner approves it on the web.";
    }

    [McpServerTool(Name = "monitor_job_run", Destructive = true)]
    [Description("Runs an approved job (see monitor_jobs). Returns the run id; read it with monitor_run_result.")]
    public async Task<string> RunJob(
        [Description("The job id.")] string jobId,
        [Description("Why now, shown on the web.")] string reason,
        [Description("This session's id.")] string? sessionId = null)
    {
        if (!Guid.TryParse(jobId, out var id)) return "Give the job id from monitor_jobs.";
        var list = await requests.AskAsync("jobs", HttpMethod.Get, "api/agent/jobs", null, RemoteRequests.ReadWait);
        if (!list.Ok) return MachineTools.Failure(list.Error!);
        var job = (list.Json!.Value.Deserialize<List<JobView>>(ApiClient.Json) ?? []).FirstOrDefault(j => j.Id == id);
        if (job is null) return $"No job {id} in this workspace.";
        if (job.Status != JobStatuses.Active) return $"Job {id} is {job.Status}; only an approved job runs.";
        var clientKey = Guid.NewGuid().ToString();
        var create = new RunCreate(clientKey, job.TargetAgentId, RunModes.Argv, job.Argv, null, job.Cwd, job.TimeoutSeconds, reason, sessionId, job.Id);
        var answer = await requests.AskAsync(RemoteRelay.RunKind, HttpMethod.Post, "api/agent/runs", create, RemoteRequests.ReadWait, clientKey);
        if (answer.Error == RemoteAnswer.Pending) return $"Queued as request {clientKey}. Call monitor_run_result with runId {clientKey}.";
        if (!answer.Ok) return MachineTools.Failure(answer.Error!);
        var run = answer.Json!.Value.Deserialize<RunCreated>(ApiClient.Json)!;
        return $"Job \"{job.Name}\" started as run {run.Id}: {run.Status}. Call monitor_run_result with runId {run.Id}.";
    }

    private async Task<(string Query, string? Error)> FilterAsync(string? machine)
    {
        if (string.IsNullOrWhiteSpace(machine)) return ("", null);
        var target = await Targets.ResolveAsync(requests, machine);
        return target.Error is { } error ? ("", error) : ($"?target={target.AgentId}", null);
    }
}
