using System.Globalization;
using System.Text;
using System.Text.Json;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Mcp;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;
using Podium.Agent.Requests;

namespace Podium.Agent;

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
            await stderr.WriteLineAsync("This machine is not connected: run aipod login --server <url> (or install --standalone --server <url>) first.");
            return 1;
        }

        if (wake is not null) wake();
        else DaemonHost.EnsureRunning(config, log);
        var client = new RemoteClient(config, clock);
        var requests = client.Requests;
        try
        {
            return args[0] switch
            {
                "machines" => await MachinesAsync(client, stdout, stderr),
                "machine" => await DetailAsync(client, args, stdout, stderr),
                "metrics" => await MetricsAsync(requests, args, stdout, stderr),
                "alerts" => await AlertsAsync(requests, args, stdout, stderr),
                "jobs" => await JobsAsync(client, args, stdout, stderr),
                "job-run" => await JobRunAsync(client, args, stdout, stderr),
                "run" => await StartRunAsync(client, args, stdout, stderr),
                "result" => await ResultAsync(client, config, args, stdout, stderr),
                "ask" => await AskAsync(client, args, stdout, stderr),
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

    private static async Task<int> MachinesAsync(RemoteClient client, TextWriter stdout, TextWriter stderr)
    {
        var listed = await client.MachinesAsync();
        if (!listed.Ok) return await Failed(stderr, listed.Error!);
        foreach (var m in listed.Value!) await stdout.WriteLineAsync(MachineTools.Line(m));
        return 0;
    }

    private static async Task<int> DetailAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) return await Usage(stderr, "aipod machine <host name or agent id>");
        var target = await Targets.ResolveAsync(client.Requests, args[1]);
        if (target.Error is { } error) return await Say(stderr, error);
        var outcome = await client.DetailAsync(target.AgentId!.Value);
        if (!outcome.Ok) return await Failed(stderr, outcome.Error!);
        await stdout.WriteLineAsync(MachineDetailText.Render(outcome.Value!));
        return 0;
    }

    private static async Task<int> MetricsAsync(RemoteRequests requests, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) return await Usage(stderr, "aipod metrics <host name or agent id> [--timeout <minutes>]");
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

    private static async Task<int> JobsAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        Guid? target = null;
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            var resolved = await Targets.ResolveAsync(client.Requests, args[1]);
            if (resolved.Error is { } error) return await Say(stderr, error);
            target = resolved.AgentId;
        }

        var jobs = await client.JobsAsync(target);
        if (!jobs.Ok) return await Failed(stderr, jobs.Error!);
        foreach (var j in jobs.Value!)
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{j.Id} \"{j.Name}\" on agent {j.TargetAgentId}: {j.Status}, [{string.Join(", ", j.Argv)}] in {j.Cwd}, timeout {j.TimeoutSeconds}s"));
        }

        return 0;
    }

    private static async Task<int> JobRunAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) return await Usage(stderr, "aipod job-run <job id> [--reason <why>]");
        var list = await client.JobsAsync();
        if (!list.Ok) return await Failed(stderr, list.Error!);
        var job = list.Value!.FirstOrDefault(j => j.Id == id);
        if (job is null) return await Say(stderr, $"No job {id} in this workspace.");
        if (job.Status != JobStatuses.Active) return await Say(stderr, $"Job {id} is {job.Status}; only an approved job runs.");
        var clientKey = Guid.NewGuid().ToString();
        var create = new RunCreate(clientKey, job.TargetAgentId, RunModes.Argv, job.Argv, null, job.Cwd, job.TimeoutSeconds, Option(args, "--reason") ?? "started from the command line", null, job.Id);
        return await SubmitRunAsync(client, create, stdout, stderr);
    }

    /// <summary>The files (<c>--file path=local</c>) and the stdin (<c>--stdin local</c>) of a script run, read here so the owner approves exactly this text; null (and a message) when one cannot be read.</summary>
    private static async Task<(IReadOnlyList<ScriptFile>? Files, string? Stdin)?> ReadAttachmentsAsync(string[] args, TextWriter stderr)
    {
        var files = new List<ScriptFile>();
        foreach (var spec in Values(args, "--file"))
        {
            var cut = spec.IndexOf('=', StringComparison.Ordinal);
            if (cut < 1 || cut == spec.Length - 1) return await Fail(stderr, $"--file takes <path in the run's folder>=<local file>, not \"{spec}\".");
            var local = spec[(cut + 1)..];
            if (!File.Exists(local) || new FileInfo(local).Length > ScriptFiles.FileMax * 4L) return await Fail(stderr, $"The file {local} is missing or too large.");
            files.Add(new ScriptFile(spec[..cut], await File.ReadAllTextAsync(local)));
        }

        string? stdin = null;
        if (Option(args, "--stdin") is { } stdinFile)
        {
            if (!File.Exists(stdinFile) || new FileInfo(stdinFile).Length > ScriptFiles.StdinMax * 4L) return await Fail(stderr, $"The stdin file {stdinFile} is missing or too large.");
            stdin = await File.ReadAllTextAsync(stdinFile);
        }

        if (ScriptFiles.Validate(files, stdin) is not null) return await Fail(stderr, "The files or the stdin are not acceptable: at most 8 text files of 64 KiB (256 KiB together) at relative paths of letters, digits, dot, underscore and dash, and a stdin of at most 65536 characters.");
        return (files.Count > 0 ? files : null, stdin);
    }

    private static async Task<(IReadOnlyList<ScriptFile>? Files, string? Stdin)?> Fail(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync(message);
        return null;
    }

    private static async Task<int> StartRunAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        const string usage = "aipod run --machine <x> [--timeout <s>] [--reason <why>] [--project <key>] [--kind test|build|deploy|other] (--script <file> --interpreter sh|bash|python|powershell [--arg <value>]... [--file <path>=<local file>]... [--stdin <local file>] | --argv <exe> [args...])";
        if (Option(args, "--machine") is not { } machine) return await Usage(stderr, usage);
        var target = await Targets.ResolveAsync(client.Requests, machine);
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
            var attached = await ReadAttachmentsAsync(args, stderr);
            if (attached is null) return 1;
            create = new RunCreate(clientKey, target.AgentId!.Value, RunModes.Script, null, null, null, timeout, reason, null, null, common.Project, common.Kind, interpreter, body, scriptArgs.Count > 0 ? scriptArgs : null,
                attached.Value.Files, attached.Value.Stdin);
        }
        else if (Array.IndexOf(args, "--argv") is var at and >= 0 && at + 1 < args.Length)
        {
            create = new RunCreate(clientKey, target.AgentId!.Value, RunModes.Argv, args[(at + 1)..], null, Option(args, "--cwd"), timeout, reason, null, null, common.Project, common.Kind);
        }
        else
        {
            return await Usage(stderr, usage);
        }

        return await SubmitRunAsync(client, create, stdout, stderr);
    }

    private static async Task<int> SubmitRunAsync(RemoteClient client, RunCreate create, TextWriter stdout, TextWriter stderr)
    {
        var outcome = await client.CreateRunAsync(create);
        if (outcome.Pending)
        {
            await stdout.WriteLineAsync($"Queued as request {create.ClientKey}. Read it with: aipod result {create.ClientKey}");
            return 0;
        }

        if (!outcome.Ok) return await Failed(stderr, outcome.Error!);
        var run = outcome.Value!;
        await stdout.WriteLineAsync($"Run {run.Id}: {run.Status}.{(run.Status == RunStatuses.PendingApproval ? " Its owner approves it on the web (until " + run.ExpiresAt.ToString("u", CultureInfo.InvariantCulture) + ")." : "")} Read it with: aipod result {run.Id} --wait 30");
        return 0;
    }

    private static async Task<int> AskAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (Option(args, "--machine") is not { } machine || args.Skip(1).LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != machine) is not { Length: > 0 } question)
        {
            return await Usage(stderr, "aipod ask --machine <x> \"<question>\"");
        }

        var target = await Targets.ResolveAsync(client.Requests, machine);
        if (target.Error is { } error) return await Say(stderr, error);
        var clientKey = Guid.NewGuid().ToString();
        var outcome = await client.CreateAskAsync(new AskCreate(clientKey, target.AgentId!.Value, TextMask.Mask(question)));
        if (outcome.Pending)
        {
            await stdout.WriteLineAsync($"Queued as request {clientKey}. Read it with: aipod result {clientKey} --wait 30");
            return 0;
        }

        if (!outcome.Ok) return await Failed(stderr, outcome.Error!);
        await stdout.WriteLineAsync($"Ask {outcome.Value!.Id} sent to {RemoteEnvelope.Label(target.Hostname)}. Read the answer with: aipod result {outcome.Value.Id} --wait 30");
        return 0;
    }

    /// <summary>A run or an ask: the local rows say which (a key or an id this machine followed); anything else is looked up as an ask first and read as a run otherwise.</summary>
    private static async Task<int> ResultAsync(RemoteClient client, AgentConfig config, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) return await Usage(stderr, "aipod result <run or ask id> [--wait <seconds>] [--offset <chars>]");
        var wait = int.TryParse(Option(args, "--wait"), out var w) ? Math.Clamp(w, 0, AgentConfig.RemoteWaitMaxSeconds) : 0;
        var offset = int.TryParse(Option(args, "--offset"), out var o) ? Math.Max(0, o) : 0;
        string? kind;
        using (var store = new LocalStore(config.DatabasePath))
        {
            kind = store.Followed(id.ToString()) is not null ? RemoteRelay.RunKind : store.AskOut(id.ToString()) is not null ? RemoteRelay.AskKind : store.Request(id.ToString())?.Kind;
        }

        // An id this machine never followed is looked up as an ask (and followed from here on); a key or run it followed is read as what it is.
        return kind is RemoteRelay.AskKind or null ? await AskResultAsync(client, id, wait, offset, stdout, stderr) : await RunResultAsync(client, id, wait, offset, stdout, stderr);
    }

    private static async Task<int> AskResultAsync(RemoteClient client, Guid typed, int wait, int offset, TextWriter stdout, TextWriter stderr)
    {
        var id = await client.AskIdAsync(typed.ToString());
        if (id is null) return await Say(stderr, $"Ask {typed} was not created.");
        var view = await client.WaitAskAsync(id.Value, wait);
        if (view is null) return await Say(stderr, $"Ask {id} is not known to this agent yet; try again shortly.");
        await stdout.WriteLineAsync($"Ask {id} to {RemoteEnvelope.Label(view.TargetHostname)}: {view.Status}");
        if (view.Answer is { } text) await stdout.WriteLineAsync(text[Math.Min(offset, text.Length)..]);
        return view.Status is AskStatuses.Queued or AskStatuses.Delivered or AskStatuses.Answered ? 0 : 1;
    }

    private static async Task<int> RunResultAsync(RemoteClient client, Guid typed, int wait, int offset, TextWriter stdout, TextWriter stderr)
    {
        var key = await client.RunIdAsync(typed.ToString());
        if (key is null) return await Say(stderr, $"Run {typed} was not created.");
        if (await client.ReadRunAsync(key, wait) is not { } snapshot) return await Say(stderr, $"Run {typed} is not known to this agent yet; try again shortly.");
        var (run, view) = (snapshot.Run, snapshot.View);
        await stdout.WriteLineAsync(view is null ? $"Run {key}: {run.Status}"
            : string.Create(CultureInfo.InvariantCulture, $"Run {key} on {RemoteEnvelope.Label(view.TargetHostname)}: {view.Status}{(view.ExitCode is { } x ? $", exit code {x}" : "")}{(view.Error is { } e ? $", error {RemoteEnvelope.Code(e)}" : "")}"));
        if (snapshot.Completeness is { } completeness) await stdout.WriteLineAsync(completeness);
        var text = new StringBuilder();
        foreach (var c in snapshot.Chunks)
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
