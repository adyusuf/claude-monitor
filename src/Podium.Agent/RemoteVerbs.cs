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
public static partial class RemoteVerbs
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
