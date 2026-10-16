using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent;

/// <summary>
/// The command-line verbs of a person on a machine without Claude Code (ADR-0008 §3g): <c>machines</c>, <c>machine</c>, <c>metrics</c>, <c>alerts</c>, <c>jobs</c>,
/// <c>job-run</c>, <c>run</c>, <c>result</c> and <c>ask</c>. They are thin wrappers over the same local request rows the MCP tools write: only the daemon talks to
/// the API (ADR-0005), and each verb wakes it the way the hooks do. They print for a person, not for a model: no envelope, host names through <c>Label</c>,
/// and what comes from another machine is what the target already masked. An approval still happens on the web; a verb never approves anything.
/// </summary>
public static class RemoteVerbs
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string> { "machines", "machine", "metrics", "alerts", "jobs", "job-run", "run", "result", "ask" };
    private static readonly string[] ValueOptions = ["--machine", "--timeout", "--reason", "--project", "--kind", "--script", "--interpreter", "--arg", "--wait", "--offset"];

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock, AgentLog log,
        Action? wake = null, Func<bool>? connected = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        if (!(connected?.Invoke() ?? Identity_Connected(config)))
        {
            await stderr.WriteLineAsync("This machine is not connected: run cm-agent login --server <url> (or install --standalone --server <url>) first.");
            return 1;
        }

        if (wake is not null) wake();
        else DaemonHost.EnsureRunning(config, log);
        var requests = new RemoteRequests(config, clock);
        try
        {
            return args[0] switch
            {
                "machines" => await MachinesAsync(requests, stdout, stderr),
                "machine" => await DetailAsync(requests, args, stdout, stderr),
                "metrics" => await MetricsAsync(requests, args, stdout, stderr),
                "alerts" => await AlertsAsync(requests, args, stdout, stderr),
                "jobs" => await JobsAsync(requests, args, stdout, stderr),
                "job-run" => await JobRunAsync(requests, args, stdout, stderr),
                "run" => await StartRunAsync(requests, args, stdout, stderr),
                "result" => await ResultAsync(requests, config, clock, args, stdout, stderr),
                "ask" => await AskAsync(requests, args, stdout, stderr),
                _ => 2,
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            await stderr.WriteLineAsync($"Unexpected answer: {e.GetType().Name}.");
            return 1;
        }
    }

    private static bool Identity_Connected(AgentConfig config) => Auth.Identity.Peek(config)?.Connected == true;

    private static async Task<int> MachinesAsync(RemoteRequests requests, TextWriter stdout, TextWriter stderr)
    {
        var answer = await requests.AskAsync("machines", HttpMethod.Get, "api/agent/machines", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        foreach (var m in answer.Json!.Value.Deserialize<List<MachineView>>(ApiClient.Json) ?? []) await stdout.WriteLineAsync(MachineTools.Line(m));
        return 0;
    }

    private static async Task<int> DetailAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) return await Usage(stderr, "cm-agent machine <host name or agent id>");
        var target = await Targets.ResolveAsync(requests, args[1]);
        if (target.Error is { } error) return await Say(stderr, error);
        var answer = await requests.AskAsync("machine", HttpMethod.Get, $"api/agent/machines/{target.AgentId}/detail", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        await stdout.WriteLineAsync(MachineDetailText.Render(answer.Json!.Value.Deserialize<MachineDetail>(ApiClient.Json)!));
        return 0;
    }

    private static async Task<int> MetricsAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) return await Usage(stderr, "cm-agent metrics <host name or agent id> [--timeout <minutes>]");
        var target = await Targets.ResolveAsync(requests, args[1]);
        if (target.Error is { } error) return await Say(stderr, error);
        var minutes = int.TryParse(Option(args, "--timeout"), out var m) ? Math.Clamp(m, 1, 1440) : 60;
        var answer = await requests.AskAsync("metrics", HttpMethod.Get, $"api/agent/machines/{target.AgentId}/metrics?minutes={minutes}", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        foreach (var s in answer.Json!.Value.Deserialize<List<MetricSample>>(ApiClient.Json) ?? [])
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{s.SampledAt:u} cpu={s.CpuPct:0.#}% mem={Pct(s.MemUsedBytes, s.MemTotalBytes)}% {string.Join(' ', s.Disks.Select(d => $"disk[{d.Mount}]={Pct(d.UsedBytes, d.TotalBytes)}%"))}"));
        }

        return 0;
    }

    private static async Task<int> AlertsAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var filter = "";
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            var target = await Targets.ResolveAsync(requests, args[1]);
            if (target.Error is { } error) return await Say(stderr, error);
            filter = $"agent={target.AgentId}&";
        }

        var answer = await requests.AskAsync("alerts", HttpMethod.Get, $"api/agent/alerts?{filter}resolved=false", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        foreach (var a in answer.Json!.Value.Deserialize<List<AlertView>>(ApiClient.Json) ?? [])
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{a.State} {a.Kind} on {RemoteEnvelope.Label(a.Hostname)}: {a.LastValue:0.#} (threshold {a.ThresholdPct:0.#}) since {a.OpenedAt:u}"));
        }

        return 0;
    }

    private static async Task<int> JobsAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var query = "";
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            var target = await Targets.ResolveAsync(requests, args[1]);
            if (target.Error is { } error) return await Say(stderr, error);
            query = $"?target={target.AgentId}";
        }

        var answer = await requests.AskAsync("jobs", HttpMethod.Get, $"api/agent/jobs{query}", null, RemoteRequests.ReadWait);
        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        foreach (var j in answer.Json!.Value.Deserialize<List<JobView>>(ApiClient.Json) ?? [])
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{j.Id} \"{j.Name}\" on agent {j.TargetAgentId}: {j.Status}, [{string.Join(", ", j.Argv)}] in {j.Cwd}, timeout {j.TimeoutSeconds}s"));
        }

        return 0;
    }

    private static async Task<int> JobRunAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) return await Usage(stderr, "cm-agent job-run <job id> [--reason <why>]");
        var list = await requests.AskAsync("jobs", HttpMethod.Get, "api/agent/jobs", null, RemoteRequests.ReadWait);
        if (!list.Ok) return await Failed(stderr, list.Error!);
        var job = (list.Json!.Value.Deserialize<List<JobView>>(ApiClient.Json) ?? []).FirstOrDefault(j => j.Id == id);
        if (job is null) return await Say(stderr, $"No job {id} in this workspace.");
        if (job.Status != JobStatuses.Active) return await Say(stderr, $"Job {id} is {job.Status}; only an approved job runs.");
        var clientKey = Guid.NewGuid().ToString();
        var create = new RunCreate(clientKey, job.TargetAgentId, RunModes.Argv, job.Argv, null, job.Cwd, job.TimeoutSeconds, Option(args, "--reason") ?? "started from the command line", null, job.Id);
        return await SubmitRunAsync(requests, create, clientKey, stdout, stderr);
    }

    private static async Task<int> StartRunAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        const string usage = "cm-agent run --machine <x> [--timeout <s>] [--reason <why>] [--project <key>] [--kind test|build|deploy|other] (--script <file> --interpreter sh|bash|python|powershell [--arg <value>]... | --argv <exe> [args...])";
        if (Option(args, "--machine") is not { } machine) return await Usage(stderr, usage);
        var target = await Targets.ResolveAsync(requests, machine);
        if (target.Error is { } error) return await Say(stderr, error);
        var timeout = int.TryParse(Option(args, "--timeout"), out var t) ? Math.Clamp(t, 1, 3600) : 120;
        var clientKey = Guid.NewGuid().ToString();
        var reason = Option(args, "--reason") ?? "started from the command line";
        var common = (Project: Option(args, "--project"), Kind: Option(args, "--kind"));
        RunCreate create;
        if (Option(args, "--script") is { } file)
        {
            if (Option(args, "--interpreter") is not { } interpreter) return await Usage(stderr, usage);
            if (!File.Exists(file) || new FileInfo(file).Length > ScriptInterpreters.BodyCeiling * 4L) return await Say(stderr, $"The script file {file} is missing or too large.");
            var body = await File.ReadAllTextAsync(file);
            var scriptArgs = Values(args, "--arg");
            create = new RunCreate(clientKey, target.AgentId!.Value, RunModes.Script, null, null, null, timeout, reason, null, null, common.Project, common.Kind, interpreter, body, scriptArgs.Count > 0 ? scriptArgs : null);
        }
        else if (Array.IndexOf(args, "--argv") is var at and >= 0 && at + 1 < args.Length)
        {
            create = new RunCreate(clientKey, target.AgentId!.Value, RunModes.Argv, args[(at + 1)..], null, Option(args, "--cwd"), timeout, reason, null, null, common.Project, common.Kind);
        }
        else
        {
            return await Usage(stderr, usage);
        }

        return await SubmitRunAsync(requests, create, clientKey, stdout, stderr);
    }

    private static async Task<int> SubmitRunAsync(RemoteRequests requests, RunCreate create, string clientKey, TextWriter stdout, TextWriter stderr)
    {
        var answer = await requests.AskAsync(RemoteRelay.RunKind, HttpMethod.Post, "api/agent/runs", create, RemoteRequests.ReadWait, clientKey);
        if (answer.Error == RemoteAnswer.Pending)
        {
            await stdout.WriteLineAsync($"Queued as request {clientKey}. Read it with: cm-agent result {clientKey}");
            return 0;
        }

        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        var run = answer.Json!.Value.Deserialize<RunCreated>(ApiClient.Json)!;
        await stdout.WriteLineAsync($"Run {run.Id}: {run.Status}.{(run.Status == RunStatuses.PendingApproval ? " Its owner approves it on the web (until " + run.ExpiresAt.ToString("u", CultureInfo.InvariantCulture) + ")." : "")} Read it with: cm-agent result {run.Id} --wait 30");
        return 0;
    }

    private static async Task<int> AskAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (Option(args, "--machine") is not { } machine || args.Skip(1).LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != machine) is not { Length: > 0 } question)
        {
            return await Usage(stderr, "cm-agent ask --machine <x> \"<question>\"");
        }

        var target = await Targets.ResolveAsync(requests, machine);
        if (target.Error is { } error) return await Say(stderr, error);
        var clientKey = Guid.NewGuid().ToString();
        var answer = await requests.AskAsync(AskTools.AskKind, HttpMethod.Post, "api/agent/asks", new AskCreate(clientKey, target.AgentId!.Value, TextMask.Mask(question)), RemoteRequests.ReadWait, clientKey);
        if (answer.Error == RemoteAnswer.Pending)
        {
            await stdout.WriteLineAsync($"Queued as request {clientKey}. Read it with: cm-agent result {clientKey} --wait 30");
            return 0;
        }

        if (!answer.Ok) return await Failed(stderr, answer.Error!);
        var ask = answer.Json!.Value.Deserialize<AskCreated>(ApiClient.Json)!;
        await stdout.WriteLineAsync($"Ask {ask.Id} sent to {RemoteEnvelope.Label(target.Hostname)}. Read the answer with: cm-agent result {ask.Id} --wait 30");
        return 0;
    }

    /// <summary>A run or an ask: the local rows say which; an id the daemon does not know yet is asked of the API as an ask first.</summary>
    private static async Task<int> ResultAsync(RemoteRequests requests, AgentConfig config, TimeProvider clock, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) return await Usage(stderr, "cm-agent result <run or ask id> [--wait <seconds>] [--offset <chars>]");
        var wait = int.TryParse(Option(args, "--wait"), out var w) ? Math.Clamp(w, 0, AgentConfig.RemoteWaitMaxSeconds) : 0;
        var offset = int.TryParse(Option(args, "--offset"), out var o) ? Math.Max(0, o) : 0;
        string? kind;
        using (var store = new LocalStore(config.DatabasePath)) kind = store.Followed(id.ToString()) is not null ? RemoteRelay.RunKind : store.Request(id.ToString())?.Kind;
        if (kind == AskTools.AskKind) return await AskResultAsync(requests, clock, id, wait, offset, stdout, stderr);
        if (kind == RemoteRelay.RunKind) return await RunResultAsync(requests, config, clock, id, wait, offset, stdout, stderr);
        var probe = await requests.AskAsync(AskTools.AskKind + "-result", HttpMethod.Get, $"api/agent/asks/{id}", null, RemoteRequests.ReadWait);
        return probe.Ok ? await AskResultAsync(requests, clock, id, wait, offset, stdout, stderr) : await RunResultAsync(requests, config, clock, id, wait, offset, stdout, stderr);
    }

    private static async Task<int> AskResultAsync(RemoteRequests requests, TimeProvider clock, Guid id, int wait, int offset, TextWriter stdout, TextWriter stderr)
    {
        var until = clock.GetUtcNow().AddSeconds(wait);
        AskView? view;
        while (true)
        {
            var answer = await requests.AskAsync(AskTools.AskKind + "-result", HttpMethod.Get, $"api/agent/asks/{id}", null, RemoteRequests.ReadWait);
            if (!answer.Ok) return await Failed(stderr, answer.Error!);
            view = answer.Json!.Value.Deserialize<AskView>(ApiClient.Json);
            if (view is null || AskStatuses.Final.Contains(view.Status) || clock.GetUtcNow() >= until) break;
            await Task.Delay(TimeSpan.FromSeconds(2), clock);
        }

        if (view is null) return await Say(stderr, $"No ask {id}.");
        await stdout.WriteLineAsync($"Ask {id} to {RemoteEnvelope.Label(view.TargetHostname)}: {view.Status}");
        if (view.Answer is { } text) await stdout.WriteLineAsync(text[Math.Min(offset, text.Length)..]);
        return view.Status is AskStatuses.Queued or AskStatuses.Delivered or AskStatuses.Answered ? 0 : 1;
    }

    private static async Task<int> RunResultAsync(RemoteRequests requests, AgentConfig config, TimeProvider clock, Guid id, int wait, int offset, TextWriter stdout, TextWriter stderr)
    {
        var key = id.ToString();
        using (var store = new LocalStore(config.DatabasePath))
        {
            if (store.Followed(key) is null && store.Request(key) is { Kind: RemoteRelay.RunKind })
            {
                var created = await requests.WaitAsync(key, RemoteRequests.ReadWait);
                if (!created.Ok || created.Json?.Deserialize<RunCreated>(ApiClient.Json) is not { } c) return await Say(stderr, $"Run {id} was not created.");
                key = c.Id.ToString();
            }
        }

        var until = clock.GetUtcNow().AddSeconds(wait);
        FollowedRun? run;
        while (true)
        {
            using (var store = new LocalStore(config.DatabasePath))
            {
                run = store.Followed(key);
                if (run is not null && !run.Done && run.Status != RunStatuses.PendingApproval) store.RunChanged(key);
            }

            if ((run is not null && run.Done) || clock.GetUtcNow() >= until) break;
            await Task.Delay(TimeSpan.FromSeconds(1), clock);
        }

        if (run is null) return await Say(stderr, $"Run {id} is not known to this agent yet; try again shortly.");
        var view = run.View is { } v ? JsonSerializer.Deserialize<RunView>(v, ApiClient.Json) : null;
        await stdout.WriteLineAsync(view is null ? $"Run {key}: {run.Status}"
            : string.Create(CultureInfo.InvariantCulture, $"Run {key} on {RemoteEnvelope.Label(view.TargetHostname)}: {view.Status}{(view.ExitCode is { } x ? $", exit code {x}" : "")}{(view.Error is { } e ? $", error {RemoteEnvelope.Code(e)}" : "")}"));
        using var s = new LocalStore(config.DatabasePath);
        var stored = s.RunOutput(key).ToList();
        if (run.Done && ScriptResult.Completeness(view, stored) is { } completeness) await stdout.WriteLineAsync(completeness);
        var text = new StringBuilder();
        foreach (var c in stored)
        {
            if (c.Gap) text.Append(RemoteTools.GapMarker);
            text.Append(c.Body);
        }

        if (text.Length > 0) await stdout.WriteAsync(text.ToString(Math.Min(offset, text.Length), text.Length - Math.Min(offset, text.Length)));
        if (!run.Done) await stdout.WriteLineAsync(run.Status == RunStatuses.PendingApproval ? "\nWaiting for the owner's approval on the web." : "\nNot finished yet.");
        return view?.Status switch { RunStatuses.Succeeded => 0, RunStatuses.Failed or RunStatuses.TimedOut or RunStatuses.Denied or RunStatuses.Expired or RunStatuses.Cancelled => 1, _ => 0 };
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Values(string[] args, string name)
    {
        var list = new List<string>();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name && !(ValueOptions.Contains(args[i + 1]) && args[i + 1] != name)) list.Add(args[i + 1]);
        }

        return list;
    }

    private static string Pct(long used, long total) => total <= 0 ? "?" : (100.0 * used / total).ToString("0.#", CultureInfo.InvariantCulture);

    private static async Task<int> Failed(TextWriter stderr, string error)
    {
        await stderr.WriteLineAsync(MachineTools.Failure(error));
        return 1;
    }

    private static async Task<int> Say(TextWriter stderr, string text)
    {
        await stderr.WriteLineAsync(text);
        return 1;
    }

    private static async Task<int> Usage(TextWriter stderr, string usage)
    {
        await stderr.WriteLineAsync("usage: " + usage);
        return 2;
    }
}
