using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>The rules for the files and the stdin a script run carries (ADR-0010 §2): what the contracts accept, what the hash covers, and what the API freezes, shows and sends.</summary>
public sealed class ScriptFilesRulesTests
{
    private static ScriptFile F(string path, string text = "x") => new(path, text);

    [Theory]
    [InlineData("data.txt")]
    [InlineData("lib/helper.py")]
    [InlineData("a/b/c/d.txt")]
    [InlineData("README")]
    [InlineData("my-file_1.2.sh")]
    public void A_safe_relative_path_is_accepted(string path) => Assert.True(ScriptFiles.IsSafePath(path));

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../x")]
    [InlineData("a/../x")]
    [InlineData("a/./x")]
    [InlineData("a//x")]
    [InlineData("a/")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    [InlineData("a\\b")]
    [InlineData("C:\\x")]
    [InlineData("C:x")]
    [InlineData("a/b/c/d/e.txt")]
    [InlineData("tmp")]
    [InlineData("TMP/x")]
    [InlineData("script")]
    [InlineData("script.sh")]
    [InlineData("Script.PY")]
    [InlineData("name with space")]
    [InlineData("naïve.txt")]
    [InlineData("a:b")]
    [InlineData("x\0y")]
    public void An_unsafe_path_is_refused(string path) => Assert.False(ScriptFiles.IsSafePath(path));

    [Fact]
    public void A_name_that_only_starts_like_a_reserved_one_is_fine_and_a_long_segment_is_not()
    {
        Assert.True(ScriptFiles.IsSafePath("scripts.txt"));
        Assert.True(ScriptFiles.IsSafePath("tmpfile"));
        Assert.True(ScriptFiles.IsSafePath("a/script.sh")); // only the first segment collides with the run's own names
        Assert.True(ScriptFiles.IsSafePath(new string('a', ScriptFiles.SegmentMax)));
        Assert.False(ScriptFiles.IsSafePath(new string('a', ScriptFiles.SegmentMax + 1)));
    }

    [Fact]
    public void The_limits_are_enforced_on_count_size_total_and_stdin()
    {
        Assert.Null(ScriptFiles.Validate(null, null));
        Assert.Null(ScriptFiles.Validate([], ""));
        Assert.Null(ScriptFiles.Validate(Enumerable.Range(0, ScriptFiles.MaxFiles).Select(i => F($"f{i}.txt")).ToList(), new string('x', ScriptFiles.StdinMax)));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate(Enumerable.Range(0, ScriptFiles.MaxFiles + 1).Select(i => F($"f{i}.txt")).ToList(), null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("a.txt", new string('x', ScriptFiles.FileMax + 1))], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate(null, new string('x', ScriptFiles.StdinMax + 1)));
        var big = Enumerable.Range(0, 5).Select(i => F($"f{i}.txt", new string('x', ScriptFiles.FileMax))).ToList(); // 5 x 64 KiB > 256 KiB
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate(big, null));
    }

    [Fact]
    public void A_nul_a_duplicate_a_case_twin_a_file_that_is_also_a_folder_and_a_null_entry_are_refused()
    {
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("a.txt", "x\0y")], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate(null, "x\0y"));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("a.txt"), F("a.txt")], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("A.txt"), F("a.TXT")], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("lib"), F("lib/x.txt")], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([F("lib/x.txt"), F("lib")], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([null!], null));
        Assert.Equal(RemoteErrors.InvalidFiles, ScriptFiles.Validate([new ScriptFile("a.txt", null!)], null));
    }

    [Fact]
    public void Any_is_true_for_a_file_or_a_stdin_text_only()
    {
        Assert.False(ScriptFiles.Any(null, null));
        Assert.False(ScriptFiles.Any([], ""));
        Assert.True(ScriptFiles.Any([F("a.txt")], null));
        Assert.True(ScriptFiles.Any(null, "x"));
    }

    private static string Hash(IReadOnlyList<ScriptFile>? files = null, string? stdin = null, string body = "echo hi\n") =>
        GrantMatcher.ScriptHash("sh", body, ["a"], 30, Guid.Parse("11111111-1111-1111-1111-111111111111"), "test", "application", files, stdin);

    [Fact]
    public void A_script_without_files_or_stdin_keeps_the_hash_it_always_had()
    {
        var plain = GrantMatcher.ScriptHash("sh", "echo hi\n", ["a"], 30, Guid.Parse("11111111-1111-1111-1111-111111111111"), "test", "application");
        Assert.Equal(plain, Hash());
        Assert.Equal(plain, Hash([], ""));
        Assert.Equal(plain, Hash(null, ""));
    }

    [Fact]
    public void The_hash_covers_every_file_its_path_and_text_and_the_stdin_and_not_the_order_they_were_sent_in()
    {
        var one = Hash([F("a.txt", "1"), F("b/c.txt", "2")]);
        Assert.NotEqual(Hash(), one);
        Assert.Equal(one, Hash([F("b/c.txt", "2"), F("a.txt", "1")]));
        Assert.NotEqual(one, Hash([F("a.txt", "1"), F("b/c.txt", "3")]));
        Assert.NotEqual(one, Hash([F("a.txt", "1"), F("b/d.txt", "2")]));
        Assert.NotEqual(one, Hash([F("a.txt", "1")]));
        Assert.NotEqual(Hash(stdin: "in"), Hash(stdin: "out"));
        Assert.NotEqual(Hash(stdin: "in"), Hash());
        Assert.NotEqual(Hash([F("a.txt", "in")]), Hash(stdin: "in")); // a file and a stdin of the same text are different things
        Assert.NotEqual(Hash([F("a.txt", "1b.txt2")]), Hash([F("a.txt1", "b.txt2")])); // no two splits give the same bytes
    }
}

[Collection(ApiGroup.Name)]
public sealed class ScriptFilesRunTests(ApiFactory api)
{
    private static readonly ScriptFile[] Two = [new("data.txt", "one\ntwo\n"), new("lib/helper.sh", "echo helped\n")];

    private static async Task<RunCreated> CreateAsync(RemoteTeam team, RunCreate req)
    {
        var response = await RemoteKit.PostRunAsync(team.Requester, req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
    }

    [Fact]
    public async Task A_script_with_files_and_stdin_is_frozen_sorted_by_path_and_approved_only_with_the_hash_that_covers_them()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var req = ScriptKit.Script(team.TargetId, files: [Two[1], Two[0]], stdin: "typed\n");
        var created = await CreateAsync(team, req);

        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal(["data.txt", "lib/helper.sh"], row.ScriptFiles!.Select(f => f.Path));
        Assert.Equal("typed\n", row.ScriptStdin);

        var without = ScriptKit.Script(team.TargetId, files: null, stdin: null);
        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, without, "test", "application"), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).StatusCode);
    }

    [Fact]
    public async Task An_agent_that_does_not_report_the_capability_is_refused_a_run_with_files_or_stdin_but_not_one_without()
    {
        var team = await ScriptKit.TeamAsync(api, files: false);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, files: Two)), HttpStatusCode.Conflict, RemoteErrors.FilesUnsupported);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, stdin: "x")), HttpStatusCode.Conflict, RemoteErrors.FilesUnsupported);
        Assert.Equal(HttpStatusCode.OK, (await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId))).StatusCode);
    }

    [Fact]
    public async Task Files_that_break_the_rules_are_refused_at_the_door()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        foreach (var files in new[] { new[] { new ScriptFile("../x", "y") }, new[] { new ScriptFile("a", "1"), new ScriptFile("A", "2") }, new[] { new ScriptFile("script.sh", "y") } })
        {
            await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, files: files)), HttpStatusCode.BadRequest, RemoteErrors.InvalidFiles);
        }

        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, stdin: new string('x', ScriptFiles.StdinMax + 1))), HttpStatusCode.BadRequest, RemoteErrors.InvalidFiles);
    }

    [Fact]
    public async Task Only_a_script_run_may_carry_files_or_stdin()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var argv = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ScriptFiles = Two };
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, argv), HttpStatusCode.BadRequest, RunRules.BadCommand);
        var shell = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey()) with { ScriptStdin = "x" };
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, shell), HttpStatusCode.BadRequest, RunRules.BadCommand);
    }

    [Fact]
    public async Task A_retried_request_returns_the_same_run_and_one_with_other_files_is_a_conflict()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var req = ScriptKit.Script(team.TargetId, files: Two, stdin: "a");
        var first = await CreateAsync(team, req);
        Assert.Equal(first.Id, (await CreateAsync(team, req with { ScriptFiles = [Two[1], Two[0]] })).Id); // the same files in another order
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, req with { ScriptStdin = "b" }), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, req with { ScriptFiles = [Two[0]] }), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
    }

    [Fact]
    public async Task The_owner_sees_the_files_and_the_stdin_on_the_card_and_someone_else_does_not()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var created = await CreateAsync(team, ScriptKit.Script(team.TargetId, files: Two, stdin: "typed"));
        var owner = await team.Owner.GetJsonAsync($"/api/runs/{created.Id}");
        Assert.Equal(2, owner.GetProperty("scriptFiles").GetArrayLength());
        Assert.Equal("typed", owner.GetProperty("scriptStdin").GetString());
        var other = await team.Other.GetJsonAsync($"/api/runs/{created.Id}");
        Assert.Equal(JsonValueKind.Null, other.GetProperty("scriptFiles").ValueKind);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("scriptStdin").ValueKind);
    }

    [Fact]
    public async Task The_approved_run_reaches_the_target_with_its_files_and_stdin()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var req = ScriptKit.Script(team.TargetId, files: Two, stdin: "typed");
        var created = await CreateAsync(team, req);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).StatusCode);

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<Api.Data.PodiumDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None,
            api.Services.GetRequiredService<Api.Streaming.Broker>());
        var message = Assert.Single(waiting);
        Assert.Equal(["data.txt", "lib/helper.sh"], message.ScriptFiles!.Select(f => f.Path));
        Assert.Equal("typed", message.ScriptStdin);
    }

    [Fact]
    public async Task A_script_without_files_is_unchanged_on_the_wire_and_in_the_database()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        var created = await CreateAsync(team, ScriptKit.Script(team.TargetId));
        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Null(row.ScriptFilesJson);
        Assert.Null(row.ScriptStdin);
        var view = await team.Owner.GetJsonAsync($"/api/runs/{created.Id}");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("scriptFiles").ValueKind);
    }

    [Fact]
    public async Task The_account_export_includes_the_files_and_the_stdin_of_the_owners_runs()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        await CreateAsync(team, ScriptKit.Script(team.TargetId, files: Two, stdin: "typed"));
        var export = await (await team.Owner.Http.GetAsync("/api/me/export")).Content.ReadAsStringAsync();
        Assert.Contains("helper.sh", export, StringComparison.Ordinal);
        Assert.Contains("typed", export, StringComparison.Ordinal);
    }
}
