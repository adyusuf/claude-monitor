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

/// <summary>The verbs that start, ask and read a run (<c>job-run</c>, <c>run</c>, <c>ask</c>, <c>result</c>): the second half of <see cref="RemoteVerbs"/>, split off to keep each file short.</summary>
public static partial class RemoteVerbs
{
    private static async Task<int> JobRunAsync(RemoteClient client, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) return await Usage(stderr, "aipod job-run <job id> [--reason <why>]");
        var list = await client.JobsAsync();
        if (!list.Ok) return await Failed(stderr, list.Error!);
        var job = list.Value!.FirstOrDefault(j => j.Id == id);
        if (job is null) return await Say(stderr, Strings.T(Msg.RemoteNoJob, id));
        if (job.Status != JobStatuses.Active) return await Say(stderr, Strings.T(Msg.RemoteJobNotActive, id, job.Status));
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
            if (cut < 1 || cut == spec.Length - 1) return await Fail(stderr, Strings.T(Msg.RemoteFileSpec, spec));
            var local = spec[(cut + 1)..];
            if (!File.Exists(local) || new FileInfo(local).Length > ScriptFiles.FileMax * 4L) return await Fail(stderr, Strings.T(Msg.RemoteFileMissing, local));
            files.Add(new ScriptFile(spec[..cut], await File.ReadAllTextAsync(local)));
        }

        string? stdin = null;
        if (Option(args, "--stdin") is { } stdinFile)
        {
            if (!File.Exists(stdinFile) || new FileInfo(stdinFile).Length > ScriptFiles.StdinMax * 4L) return await Fail(stderr, Strings.T(Msg.RemoteStdinMissing, stdinFile));
            stdin = await File.ReadAllTextAsync(stdinFile);
        }

        if (ScriptFiles.Validate(files, stdin) is not null) return await Fail(stderr, Strings.T(Msg.RemoteAttachmentsBad));
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
            if (!File.Exists(file) || new FileInfo(file).Length > ScriptInterpreters.BodyCeiling * 4L) return await Say(stderr, Strings.T(Msg.RemoteScriptMissing, file));
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
            await stdout.WriteLineAsync(Strings.T(Msg.RemoteQueued, create.ClientKey, create.ClientKey));
            return 0;
        }

        if (!outcome.Ok) return await Failed(stderr, outcome.Error!);
        var run = outcome.Value!;
        await stdout.WriteLineAsync(Strings.T(Msg.RemoteRunQueued, run.Id, run.Status, run.Status == RunStatuses.PendingApproval ? Strings.T(Msg.RemoteOwnerApproves, run.ExpiresAt.ToString("u", CultureInfo.InvariantCulture)) : ""));
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
            await stdout.WriteLineAsync(Strings.T(Msg.RemoteQueued, clientKey, clientKey + " --wait 30"));
            return 0;
        }

        if (!outcome.Ok) return await Failed(stderr, outcome.Error!);
        await stdout.WriteLineAsync(Strings.T(Msg.RemoteAskSent, outcome.Value!.Id, RemoteEnvelope.Label(target.Hostname)));
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
        if (id is null) return await Say(stderr, Strings.T(Msg.RemoteAskNotCreated, typed));
        var view = await client.WaitAskAsync(id.Value, wait);
        if (view is null) return await Say(stderr, Strings.T(Msg.RemoteAskUnknown, id));
        await stdout.WriteLineAsync(Strings.T(Msg.RemoteAskLine, id, RemoteEnvelope.Label(view.TargetHostname), view.Status));
        if (view.Answer is { } text) await stdout.WriteLineAsync(text[Math.Min(offset, text.Length)..]);
        return view.Status is AskStatuses.Queued or AskStatuses.Delivered or AskStatuses.Answered ? 0 : 1;
    }

    private static async Task<int> RunResultAsync(RemoteClient client, Guid typed, int wait, int offset, TextWriter stdout, TextWriter stderr)
    {
        var key = await client.RunIdAsync(typed.ToString());
        if (key is null) return await Say(stderr, Strings.T(Msg.RemoteRunNotCreated, typed));
        if (await client.ReadRunAsync(key, wait) is not { } snapshot) return await Say(stderr, Strings.T(Msg.RemoteRunUnknown, typed));
        var (run, view) = (snapshot.Run, snapshot.View);
        await stdout.WriteLineAsync(view is null ? Strings.T(Msg.RemoteRunLine, key, run.Status)
            : Strings.T(Msg.RemoteRunLineOn, key, RemoteEnvelope.Label(view.TargetHostname), view.Status,
                view.ExitCode is { } x ? Strings.T(Msg.RemoteExitCode, x) : "", view.Error is { } e ? Strings.T(Msg.RemoteErrorCode, RemoteEnvelope.Code(e)) : ""));
        if (snapshot.Completeness is { } completeness) await stdout.WriteLineAsync(completeness);
        var text = new StringBuilder();
        foreach (var c in snapshot.Chunks)
        {
            if (c.Gap) text.Append(RemoteTools.GapMarker);
            text.Append(c.Body);
        }

        if (text.Length > 0) await stdout.WriteAsync(text.ToString(Math.Min(offset, text.Length), text.Length - Math.Min(offset, text.Length)));
        if (!run.Done) await stdout.WriteLineAsync(Strings.T(run.Status == RunStatuses.PendingApproval ? Msg.RemoteWaitingApproval : Msg.RemoteNotFinished));
        return view?.Status switch { RunStatuses.Succeeded => 0, RunStatuses.Failed or RunStatuses.TimedOut or RunStatuses.Denied or RunStatuses.Expired or RunStatuses.Cancelled => 1, _ => 0 };
    }
}
