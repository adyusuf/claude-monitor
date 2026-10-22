using System.Net.Http.Json;
using Podium.Contracts;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>Setup for script runs (ADR-0008 §3c): a target at the shell level with a trusted interpreter and a role an admin set.</summary>
public static class ScriptKit
{
    public const string Body = "echo hello\n";

    public static readonly ToolInfo Sh = new("sh", "/bin/sh", "bash 3.2", true);

    public static async Task<RemoteTeam> TeamAsync(ApiFactory api, string? environment = "test", string? function = "application",
        string execLevel = ExecLevels.Shell, IReadOnlyList<ToolInfo>? interpreters = null, bool files = false)
    {
        var team = await RemoteKit.TeamAsync(api, execLevel);
        await ProfileAsync(team, execLevel, interpreters ?? [Sh], capabilities: files ? [AgentCapabilities.Script, AgentCapabilities.ScriptFiles] : null);
        if (environment is not null || function is not null) await SetRoleAsync(team, environment, function);
        return team;
    }

    public static async Task ProfileAsync(RemoteTeam team, string execLevel, IReadOnlyList<ToolInfo> interpreters, string? claimedEnvironment = null,
        IReadOnlyList<string>? capabilities = null) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(execLevel, false, null, null, 2, ClaimedEnvironment: claimedEnvironment,
            Interpreters: interpreters, Tools: interpreters, Capabilities: capabilities), TestUser.Json)).EnsureSuccessStatusCode();

    public static async Task SetRoleAsync(RemoteTeam team, string? environment, string? function, string? code = null) =>
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment, function, code })).EnsureSuccessStatusCode();

    public static RunCreate Script(Guid target, string? key = null, string interpreter = ScriptInterpreters.Sh, string? body = null, int timeout = 30,
        IReadOnlyList<string>? args = null, IReadOnlyList<ScriptFile>? files = null, string? stdin = null) =>
        new(key ?? RemoteKit.NewKey(), target, RunModes.Script, null, null, null, timeout, "run it", ScriptInterpreter: interpreter, ScriptBody: body ?? Body, ScriptArgs: args,
            ScriptFiles: files, ScriptStdin: stdin);

    /// <summary>The hash the approval card carries: it covers the interpreter, the exact text, the arguments and the role the card showed.</summary>
    public static string HashOf(RunCreate req, string? environment, string? function) =>
        GrantMatcher.ScriptHash(req.ScriptInterpreter!, req.ScriptBody!, req.ScriptArgs, req.TimeoutSeconds, req.TargetAgentId, environment, function, req.ScriptFiles, req.ScriptStdin);

    public static Task<HttpResponseMessage> ApproveAsync(TestUser owner, Guid runId, RunCreate req, string? environment, string? function, string? code = null) =>
        owner.PostAsync($"/api/runs/{runId}/approve", new { hash = HashOf(req, environment, function), code });

    public static Task<HttpResponseMessage> ChunksAsync(TestAgent target, Guid runId, params RunOutputChunk[] chunks) =>
        target.Http.PostAsJsonAsync($"/api/agent/runs/{runId}/output", chunks, TestUser.Json);

    public static RunOutputChunk Chunk(int seq, int bytes) => new(seq, RunStreams.Stdout, new string('x', bytes));
}
