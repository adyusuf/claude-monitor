using System.Text.Json;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The CLI verbs of a person on a standalone machine (ADR-0008 §3g): thin wrappers over the same local request rows the MCP tools write.</summary>
public sealed class RemoteVerbsTests : IDisposable
{
    private readonly TempHome home = RemoteFakeDaemon.QuickHome();
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();
    private int woke;
    private bool connected = true;

    public void Dispose() => home.Dispose();

    private async Task<int> RunAsync(params string[] args) =>
        await RemoteVerbs.RunAsync(args, home.Config, stdout, stderr, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), () => woke++, () => connected);

    [Fact]
    public async Task A_machine_that_is_not_connected_says_how_to_connect_and_does_not_wake_the_daemon()
    {
        connected = false;
        Assert.Equal(1, await RunAsync("machines"));
        Assert.Contains("aipod login --server", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, woke);
    }

    [Fact]
    public async Task Machines_prints_one_line_per_machine_raw_and_wakes_the_daemon_once()
    {
        using var daemon = new RemoteFakeDaemon(home).Machines(RemoteFakeDaemon.Machine("web01"), RemoteFakeDaemon.Machine("db01"));

        Assert.Equal(0, await RunAsync("machines"));

        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("web01 (agent ", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("<<<aipod-output", stdout.ToString(), StringComparison.Ordinal); // no model envelope for a person
        Assert.Equal(1, woke);
    }

    [Fact]
    public async Task An_unknown_machine_is_reported_and_exits_one()
    {
        using var daemon = new RemoteFakeDaemon(home).Machines(RemoteFakeDaemon.Machine("web01"));
        Assert.Equal(1, await RunAsync("machine", "nope"));
        Assert.Contains("Not found", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_prints_the_detail_with_its_projects_and_note_as_plain_text()
    {
        var web = RemoteFakeDaemon.Machine("web01");
        var detail = new MachineDetail(web with { Tags = new Dictionary<string, string> { ["region"] = "eu" } }, "Restart the database first.", null, false, null, null, []);
        using var daemon = new RemoteFakeDaemon(home).Machines(web).Answer("machine", detail);

        Assert.Equal(0, await RunAsync("machine", "web01"));

        Assert.Contains("tags: region=eu", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Restart the database first.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal($"api/agent/machines/{web.AgentId}/detail", Assert.Single(daemon.OfKind("machine")).Url);
    }

    [Fact]
    public async Task A_run_with_argv_sends_everything_after_argv_unchanged_with_the_project_and_kind_and_prints_where_the_approval_happens()
    {
        var web = RemoteFakeDaemon.Machine("web01");
        var created = new RunCreated(Guid.NewGuid(), RunStatuses.PendingApproval, null, DateTimeOffset.UtcNow.AddMinutes(15));
        using var daemon = new RemoteFakeDaemon(home).Machines(web).Answer(RemoteRelay.RunKind, created);

        Assert.Equal(0, await RunAsync("run", "--machine", "web01", "--timeout", "99999", "--project", "git:host/shop", "--kind", "test", "--reason", "run the tests", "--argv", "/usr/bin/dotnet", "test", "--no-build"));

        var body = JsonSerializer.Deserialize<RunCreate>(Assert.Single(daemon.OfKind("run")).Body!, ApiClient.Json)!;
        Assert.Equal((RunModes.Argv, 3600, "git:host/shop", "test", "run the tests"), (body.Mode, body.TimeoutSeconds, body.ProjectKey, body.Kind, body.Reason));
        Assert.Equal(["/usr/bin/dotnet", "test", "--no-build"], body.Argv);
        Assert.Contains($"Run {created.Id}: pending_approval", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("approves it on the web", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains($"aipod result {created.Id}", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_run_reads_the_file_names_the_interpreter_and_passes_each_arg()
    {
        var web = RemoteFakeDaemon.Machine("web01");
        var created = new RunCreated(Guid.NewGuid(), RunStatuses.PendingApproval, null, DateTimeOffset.UtcNow.AddMinutes(15));
        var script = Path.Combine(home.Dir, "check.sh");
        Directory.CreateDirectory(home.Dir);
        await File.WriteAllTextAsync(script, "echo \"$1\"\n");
        using var daemon = new RemoteFakeDaemon(home).Machines(web).Answer(RemoteRelay.RunKind, created);

        Assert.Equal(0, await RunAsync("run", "--machine", "web01", "--script", script, "--interpreter", "sh", "--arg", "one", "--arg", "two words"));

        var body = JsonSerializer.Deserialize<RunCreate>(Assert.Single(daemon.OfKind("run")).Body!, ApiClient.Json)!;
        Assert.Equal((RunModes.Script, "sh", "echo \"$1\"\n"), (body.Mode, body.ScriptInterpreter, body.ScriptBody));
        Assert.Equal(["one", "two words"], body.ScriptArgs);
        Assert.Null(body.Argv);
    }

    [Fact]
    public async Task A_script_run_reads_its_files_and_its_stdin_from_local_files_and_refuses_what_cannot_be_read_or_breaks_the_rules()
    {
        var web = RemoteFakeDaemon.Machine("web01");
        var created = new RunCreated(Guid.NewGuid(), RunStatuses.PendingApproval, null, DateTimeOffset.UtcNow.AddMinutes(15));
        Directory.CreateDirectory(home.Dir);
        var script = Path.Combine(home.Dir, "main.py");
        var helper = Path.Combine(home.Dir, "helper-local.py");
        var input = Path.Combine(home.Dir, "in.txt");
        await File.WriteAllTextAsync(script, "import helper\n");
        await File.WriteAllTextAsync(helper, "VALUE = 1\n");
        await File.WriteAllTextAsync(input, "typed\n");
        using var daemon = new RemoteFakeDaemon(home).Machines(web).Answer(RemoteRelay.RunKind, created);

        Assert.Equal(0, await RunAsync("run", "--machine", "web01", "--script", script, "--interpreter", "python", "--file", $"helper.py={helper}", "--stdin", input));
        var body = JsonSerializer.Deserialize<RunCreate>(Assert.Single(daemon.OfKind("run")).Body!, ApiClient.Json)!;
        Assert.Equal(new ScriptFile("helper.py", "VALUE = 1\n"), Assert.Single(body.ScriptFiles!));
        Assert.Equal("typed\n", body.ScriptStdin);

        foreach (var bad in new[] { new[] { "--file", "no-equals" }, new[] { "--file", $"helper.py={Path.Combine(home.Dir, "missing")}" }, new[] { "--file", $"../x={helper}" }, new[] { "--stdin", Path.Combine(home.Dir, "missing") } })
        {
            stderr.GetStringBuilder().Clear();
            Assert.Equal(1, await RunAsync(["run", "--machine", "web01", "--script", script, "--interpreter", "python", .. bad]));
            Assert.NotEmpty(stderr.ToString());
        }

        Assert.Single(daemon.OfKind("run")); // none of the refused ones was sent
    }

    [Theory]
    [InlineData("run", "--machine", "web01")]
    [InlineData("run", "--machine", "web01", "--script", "/no/such/file", "--interpreter", "sh")]
    [InlineData("run", "--script", "x", "--interpreter", "sh")]
    [InlineData("ask", "--machine", "web01")]
    [InlineData("result")]
    [InlineData("result", "not-a-guid")]
    [InlineData("job-run", "nope")]
    [InlineData("machine")]
    [InlineData("metrics")]
    public async Task A_verb_without_what_it_needs_prints_its_usage_or_the_problem_and_sends_nothing(params string[] args)
    {
        using var daemon = new RemoteFakeDaemon(home).Machines(RemoteFakeDaemon.Machine("web01"));
        var code = await RunAsync(args);
        Assert.True(code is 1 or 2);
        Assert.NotEmpty(stderr.ToString());
        Assert.Empty(daemon.OfKind("run"));
        Assert.Empty(daemon.OfKind("ask"));
    }

    [Fact]
    public async Task Ask_sends_the_masked_question_and_prints_the_id_and_how_to_read_the_answer()
    {
        var web = RemoteFakeDaemon.Machine("web01");
        var created = new AskCreated(Guid.NewGuid(), AskStatuses.Queued, DateTimeOffset.UtcNow.AddMinutes(2));
        using var daemon = new RemoteFakeDaemon(home).Machines(web).Answer("ask", created);
        var token = "ghp_" + new string('b', 36);

        Assert.Equal(0, await RunAsync("ask", "--machine", "web01", $"is {token} valid?"));

        var body = JsonSerializer.Deserialize<AskCreate>(Assert.Single(daemon.OfKind("ask")).Body!, ApiClient.Json)!;
        Assert.DoesNotContain(token, body.Question, StringComparison.Ordinal);
        Assert.Contains("[masked:github_token]", body.Question, StringComparison.Ordinal);
        Assert.Contains($"aipod result {created.Id}", stdout.ToString(), StringComparison.Ordinal);
    }

    private void SeedAsk(AskView view)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var now = DateTimeOffset.UtcNow;
        store.FollowAsk(view.Id.ToString(), AskStatuses.Queued, now);
        store.AskFetched(view.Id.ToString(), view.Status, JsonSerializer.Serialize(view, ApiClient.Json), AskStatuses.Final.Contains(view.Status), now);
    }

    [Fact]
    public async Task Result_of_an_ask_prints_its_status_and_the_answer_and_exits_zero_only_for_a_live_or_answered_ask()
    {
        var id = Guid.NewGuid();
        var answered = new AskView(id, Guid.NewGuid(), "web01", AskStatuses.Answered, "q", "The build is on main.", false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
        SeedAsk(answered);
        Assert.Equal(0, await RunAsync("result", id.ToString()));
        Assert.Contains("The build is on main.", stdout.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        SeedAsk(answered with { Status = AskStatuses.NoIdleSession, Answer = null });
        Assert.Equal(1, await RunAsync("result", id.ToString()));
        Assert.Contains("no_idle_session", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_lists_the_jobs_and_job_run_refuses_a_job_that_is_not_approved()
    {
        var jobId = Guid.NewGuid();
        var job = new JobView(jobId, Guid.NewGuid(), "tests", ["/opt/ci/run"], "/opt/ci", 600, JobStatuses.Proposed, DateTimeOffset.UtcNow);
        using var daemon = new RemoteFakeDaemon(home).Answer("jobs", new[] { job });

        Assert.Equal(0, await RunAsync("jobs"));
        Assert.Contains($"{jobId} \"tests\"", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await RunAsync("job-run", jobId.ToString()));
        Assert.Contains("only an approved job runs", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(daemon.OfKind("run"));
    }
}
