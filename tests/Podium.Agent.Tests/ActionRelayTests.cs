using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The fixed actions asked for on the web (ADR-0005, "Agent actions from the web"), as the daemon performs them: the first report is the one
/// and only start, the outcome is reported before restart or update end the daemon, and nothing is thrown into the stream.
/// </summary>
public sealed partial class ActionRelayTests : IDisposable
{
    private static readonly UpdateOffer Offer = new("9.9.9", "https://aipod.invalid/aipod-9.9.9.zip", "sha", "sig", "0.1.0", "stable");

    private readonly RemoteFixture fx = new();
    private readonly ConcurrentQueue<string> order = new();
    private readonly ConcurrentQueue<AgentActionReport> reports = new();
    private readonly AgentLog log;
    private int statusCalls;
    private int checkCalls;

    public ActionRelayTests() => log = new AgentLog(fx.Home.Config, TimeProvider.System);

    public void Dispose() => fx.Dispose();

    private static AgentActionMessage Message(string kind) => new(Guid.NewGuid(), kind, DateTimeOffset.UtcNow.AddMinutes(10));

    /// <summary>The API's side: records every report in order; "delivered" is answered with the given status, every later report with 204.</summary>
    private void Routes(AgentActionMessage action, HttpStatusCode delivered = HttpStatusCode.NoContent, Exception? onFinal = null) =>
        fx.Fake.On($"POST /api/agent/actions/{action.Id}/status", body =>
        {
            var report = JsonSerializer.Deserialize<AgentActionReport>(body, ApiClient.Json)!;
            order.Enqueue("report:" + report.Status);
            reports.Enqueue(report);
            if (report.Status == AgentActionStatuses.Delivered) return (delivered, "");
            if (onFinal is not null) throw onFinal;
            return (HttpStatusCode.NoContent, "");
        });

    private ActionRelay NewRelay(Func<Task<string>>? status = null, Func<CancellationToken, Task<UpdateCheck>>? check = null, bool service = false,
        bool spawnWorks = true) =>
        new(fx.Api, log,
            status ?? (() =>
            {
                Interlocked.Increment(ref statusCalls);
                return Task.FromResult("machine status text");
            }),
            check ?? (_ =>
            {
                Interlocked.Increment(ref checkCalls);
                return Task.FromResult(new UpdateCheck(UpdateCodes.UpToDate, "this agent is up to date"));
            }),
            verb =>
            {
                order.Enqueue("spawn:" + verb);
                return spawnWorks;
            }, service);

    private static Func<CancellationToken, Task<UpdateCheck>> Check(UpdateCheck seen, Action? counted = null) => _ =>
    {
        counted?.Invoke();
        return Task.FromResult(seen);
    };

    private AgentActionReport Final => reports.Last();
    private string[] Order => [.. order];

    private string Log() => File.Exists(fx.Home.Config.LogPath) ? File.ReadAllText(fx.Home.Config.LogPath) : "";

    // ---- the kinds ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_is_taken_then_reported_succeeded_with_the_status_text_and_nothing_is_started()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded"], Order);
        Assert.Equal(new AgentActionReport(AgentActionStatuses.Succeeded, "machine status text"), Final);
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, checkCalls);
    }

    [Fact]
    public async Task An_update_check_that_finds_the_agent_current_succeeds_with_the_updaters_message_and_the_running_version()
    {
        var action = Message(AgentActionKinds.UpdateCheck);
        Routes(action);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Succeeded, $"this agent is up to date ({AgentConfig.Version})"), (Final.Status, Final.Result));
        Assert.Equal(["report:delivered", "report:succeeded"], Order);
    }

    [Fact]
    public async Task An_update_check_the_updater_refuses_fails_with_its_code_and_message()
    {
        var action = Message(AgentActionKinds.UpdateCheck);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.BadSignature, "the offer is not signed by our key"))).PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Failed, $"update refused: {UpdateCodes.BadSignature}: the offer is not signed by our key"), (Final.Status, Final.Result));
    }

    [Fact]
    public async Task An_update_check_with_an_offer_succeeds_and_says_an_update_is_available_without_starting_it()
    {
        var action = Message(AgentActionKinds.UpdateCheck);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.Available, "an update is available", Offer))).PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Succeeded, $"update available: 9.9.9 (running {AgentConfig.Version})"), (Final.Status, Final.Result));
        Assert.DoesNotContain(Order, o => o.StartsWith("spawn:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_with_nothing_to_install_succeeds_with_the_up_to_date_text_and_starts_nothing()
    {
        var action = Message(AgentActionKinds.Update);
        Routes(action);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Succeeded, $"this agent is up to date ({AgentConfig.Version})"), (Final.Status, Final.Result));
        Assert.Equal(["report:delivered", "report:succeeded"], Order);
        Assert.Equal(1, checkCalls);
    }

    [Fact]
    public async Task An_update_the_updater_refuses_fails_and_starts_nothing()
    {
        var action = Message(AgentActionKinds.Update);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.Channel, "wrong channel"))).PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Failed, $"update refused: {UpdateCodes.Channel}: wrong channel"), (Final.Status, Final.Result));
        Assert.Equal(["report:delivered", "report:failed"], Order);
    }

    [Fact]
    public async Task An_update_with_an_offer_reports_succeeded_BEFORE_it_starts_ap_agent_update()
    {
        var action = Message(AgentActionKinds.Update);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.Available, "an update is available", Offer))).PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded", "spawn:" + UpdateCommand.Verb], Order);
        Assert.Equal(AgentActionStatuses.Succeeded, Final.Status);
        Assert.StartsWith("updating to 9.9.9", Final.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_reports_succeeded_before_it_starts_ap_agent_restart()
    {
        var action = Message(AgentActionKinds.Restart);
        Routes(action);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded", "spawn:restart"], Order);
        Assert.Equal("restarting the agent", Final.Result);
        Assert.Equal(0, checkCalls);
    }

    [Fact]
    public async Task A_restart_that_cannot_be_started_is_logged_after_the_report_and_does_not_throw()
    {
        var action = Message(AgentActionKinds.Restart);
        Routes(action);

        await NewRelay(spawnWorks: false).PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded", "spawn:restart"], Order);
        Assert.Contains("action restart: aipod restart could not be started", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_this_agent_does_not_know_fails_as_an_unknown_action_and_starts_nothing()
    {
        var action = Message("format_disk");
        Routes(action);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal((AgentActionStatuses.Failed, "unknown action"), (Final.Status, Final.Result));
        Assert.Equal(["report:delivered", "report:failed"], Order);
        Assert.Equal((0, 0), (statusCalls, checkCalls));
    }

    // ---- service mode -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_service_agent_fails_an_update_without_checking_and_without_starting_anything()
    {
        var action = Message(AgentActionKinds.Update);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.Available, "an update is available", Offer), () => Interlocked.Increment(ref checkCalls)), service: true)
            .PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:failed"], Order);
        Assert.Equal(AgentActionStatuses.Failed, Final.Status);
        Assert.StartsWith("this agent runs as a service: an administrator upgrades it", Final.Result, StringComparison.Ordinal);
        Assert.Equal(0, checkCalls);
    }

    [Fact]
    public async Task A_service_agent_still_checks_restarts_and_reports_status_as_before()
    {
        var check = Message(AgentActionKinds.UpdateCheck);
        var restart = Message(AgentActionKinds.Restart);
        var status = Message(AgentActionKinds.Status);
        Routes(check);
        Routes(restart);
        Routes(status);
        var relay = NewRelay(check: Check(new UpdateCheck(UpdateCodes.Available, "an update is available", Offer), () => Interlocked.Increment(ref checkCalls)), service: true);

        await relay.PerformAsync(check, CancellationToken.None);
        Assert.Equal((AgentActionStatuses.Succeeded, $"update available: 9.9.9 (running {AgentConfig.Version})"), (Final.Status, Final.Result));
        await relay.PerformAsync(restart, CancellationToken.None);
        Assert.Equal("spawn:restart", Order.Last());
        await relay.PerformAsync(status, CancellationToken.None);
        Assert.Equal((AgentActionStatuses.Succeeded, "machine status text"), (Final.Status, Final.Result));
        Assert.Equal(1, checkCalls);
    }

    [Fact]
    public async Task An_agent_that_is_not_a_service_still_installs_an_update()
    {
        var action = Message(AgentActionKinds.Update);
        Routes(action);

        await NewRelay(check: Check(new UpdateCheck(UpdateCodes.Available, "an update is available", Offer), () => Interlocked.Increment(ref checkCalls)), service: false)
            .PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded", "spawn:update"], Order);
        Assert.Equal(1, checkCalls);
    }

    // ---- taking an action only once -----------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_action_the_api_will_not_hand_over_is_not_performed_and_nothing_is_started(HttpStatusCode answer)
    {
        foreach (var kind in new[] { AgentActionKinds.Status, AgentActionKinds.Restart, AgentActionKinds.Update, AgentActionKinds.UpdateCheck })
        {
            var action = Message(kind);
            Routes(action, delivered: answer);
            await NewRelay().PerformAsync(action, CancellationToken.None);
        }

        Assert.Equal(["report:delivered", "report:delivered", "report:delivered", "report:delivered"], Order);
        Assert.Equal((0, 0), (statusCalls, checkCalls));
        Assert.Empty(Log());
    }

    [Fact]
    public async Task The_same_action_arriving_twice_while_it_is_in_hand_is_performed_once()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = NewRelay(status: () =>
        {
            Interlocked.Increment(ref statusCalls);
            return release.Task;
        });

        Assert.True(relay.Accept(action, CancellationToken.None));
        await Until.True(() => Volatile.Read(ref statusCalls) == 1);
        Assert.False(relay.Accept(action, CancellationToken.None));
        release.SetResult("late text");
        await Until.True(() => Order.Contains("report:succeeded"));

        Assert.Equal(1, statusCalls);
        Assert.Equal(1, Order.Count(o => o == "report:delivered"));
        Assert.Equal("late text", Final.Result);
    }

    [Fact]
    public void An_action_message_with_no_content_is_refused_before_anything_happens()
    {
        Assert.Throws<ArgumentNullException>(() => NewRelay().Accept(null!, CancellationToken.None));
        Assert.Empty(fx.Fake.Seen);
    }
}
