using System.Net;
using System.Text.Json;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The agent's report on a fixed action (ApiClient.ActionStatusAsync) and the detached start the restart and update actions use (DaemonControl.Spawn).</summary>
public sealed class ActionStatusClientTests : IDisposable
{
    private readonly RemoteFixture fx = new();

    public void Dispose() => fx.Dispose();

    [Fact]
    public async Task A_report_is_posted_to_the_actions_status_route_with_the_status_and_result_and_204_is_true()
    {
        var id = Guid.NewGuid();
        fx.Fake.On($"POST /api/agent/actions/{id}/status", RemoteFixture.NoContent);

        Assert.True(await fx.Api.ActionStatusAsync(id, AgentActionStatuses.Succeeded, "all good", CancellationToken.None));

        var seen = Assert.Single(fx.Fake.Seen);
        Assert.Equal(("POST", $"/api/agent/actions/{id}/status", "access-1"), (seen.Method, seen.Path, seen.Auth));
        Assert.Equal(new AgentActionReport(AgentActionStatuses.Succeeded, "all good"), JsonSerializer.Deserialize<AgentActionReport>(seen.Body, ApiClient.Json));
    }

    [Fact]
    public async Task A_delivered_report_carries_no_result()
    {
        var id = Guid.NewGuid();
        fx.Fake.On($"POST /api/agent/actions/{id}/status", RemoteFixture.NoContent);

        await fx.Api.ActionStatusAsync(id, AgentActionStatuses.Delivered, null, CancellationToken.None);

        var report = JsonSerializer.Deserialize<AgentActionReport>(Assert.Single(fx.Fake.Seen).Body, ApiClient.Json)!;
        Assert.Equal((AgentActionStatuses.Delivered, null), (report.Status, report.Result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_action_already_taken_finished_expired_or_not_this_agents_is_false_not_an_error(HttpStatusCode answer)
    {
        var id = Guid.NewGuid();
        fx.Fake.On($"POST /api/agent/actions/{id}/status", answer, "{}");

        Assert.False(await fx.Api.ActionStatusAsync(id, AgentActionStatuses.Delivered, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Any_other_failure_is_an_api_exception_with_the_status(HttpStatusCode answer)
    {
        var id = Guid.NewGuid();
        fx.Fake.On($"POST /api/agent/actions/{id}/status", answer, """{"title":"nope"}""");

        var e = await Assert.ThrowsAsync<ApiException>(() => fx.Api.ActionStatusAsync(id, AgentActionStatuses.Failed, "x", CancellationToken.None));

        Assert.Equal(answer, e.Status);
    }

    // ---- DaemonControl.Spawn ------------------------------------------------------------------------------------

    private static string Script(TempHome home, string body)
    {
        var path = Path.Combine(home.Dir, "script");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void A_start_from_a_binary_that_is_missing_returns_false_and_names_the_verb_in_the_log()
    {
        Assert.False(File.Exists(fx.Home.Config.BinaryPath));

        Assert.False(DaemonControl.Spawn(fx.Home.Config, new AgentLog(fx.Home.Config, TimeProvider.System), "restart"));

        Assert.Contains("could not start aipod restart:", File.ReadAllText(fx.Home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_binary_for_the_update_verb_keeps_the_original_log_wording()
    {
        Assert.False(DaemonControl.Spawn(fx.Home.Config, new AgentLog(fx.Home.Config, TimeProvider.System), UpdateCommand.Verb));

        var written = File.ReadAllText(fx.Home.Config.LogPath);
        Assert.Contains("could not start the update:", written, StringComparison.Ordinal);
        Assert.DoesNotContain("aipod update", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_is_handed_to_the_installed_binary_as_its_only_argument()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(fx.Home.Dir, "marker");
        Directory.CreateDirectory(Path.GetDirectoryName(fx.Home.Config.BinaryPath)!);
        File.Move(Script(fx.Home, $"echo \"$@\" > \"{marker}\""), fx.Home.Config.BinaryPath, overwrite: true);

        Assert.True(DaemonControl.Spawn(fx.Home.Config, new AgentLog(fx.Home.Config, TimeProvider.System), "restart"));

        await Until.True(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
        Assert.Equal("restart", (await File.ReadAllTextAsync(marker)).Trim());
    }
}
