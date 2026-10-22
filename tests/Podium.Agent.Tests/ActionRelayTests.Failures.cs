using System.Net;
using System.Text.Json;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The fixed actions of the web, part two: failures that never reach the stream, the result cap and the stream's own event.</summary>
public sealed partial class ActionRelayTests
{
    // ---- failures never reach the stream -------------------------------------------------------------------------

    [Fact]
    public async Task A_step_that_throws_reports_failed_with_only_the_exception_type_name()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);

        await NewRelay(status: () => throw new InvalidOperationException("secret /home/person/project path")).PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:failed"], Order);
        Assert.Equal((AgentActionStatuses.Failed, "failed in the agent: InvalidOperationException"), (Final.Status, Final.Result));
        var written = Log();
        Assert.Contains("action status failed in the agent: InvalidOperationException", written, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_failure_cannot_be_reported_either_both_are_logged_and_nothing_is_thrown()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action, onFinal: new HttpRequestException("api down"));

        await NewRelay(status: () => throw new InvalidOperationException("boom")).PerformAsync(action, CancellationToken.None);

        var written = Log();
        Assert.Contains("action status failed in the agent: InvalidOperationException", written, StringComparison.Ordinal);
        Assert.Contains("action status failure not reported: HttpRequestException", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_whose_report_cannot_be_sent_is_logged_not_thrown_and_still_starts_the_restart()
    {
        var action = Message(AgentActionKinds.Restart);
        Routes(action, onFinal: new HttpRequestException("api down"));

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered", "report:succeeded", "spawn:restart"], Order); // the report came first and failed; the owner still gets the restart
        Assert.Contains("action restart not reported: HttpRequestException", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_error_on_the_first_report_is_logged_and_nothing_is_performed()
    {
        var action = Message(AgentActionKinds.Restart);
        Routes(action, delivered: HttpStatusCode.InternalServerError);

        await NewRelay().PerformAsync(action, CancellationToken.None);

        Assert.Equal(["report:delivered"], Order);
        Assert.Contains("action restart not reported: ApiException", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_longer_than_the_cap_is_cut_to_two_thousand_characters_before_it_is_sent()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);

        await NewRelay(status: () => Task.FromResult(new string('x', AgentActionKinds.ResultMax + 500))).PerformAsync(action, CancellationToken.None);

        Assert.Equal(AgentActionKinds.ResultMax, Final.Result!.Length);
        Assert.All(Final.Result, c => Assert.Equal('x', c));
    }

    [Fact]
    public async Task A_result_exactly_at_the_cap_is_sent_whole()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);

        await NewRelay(status: () => Task.FromResult(new string('y', AgentActionKinds.ResultMax))).PerformAsync(action, CancellationToken.None);

        Assert.Equal(AgentActionKinds.ResultMax, Final.Result!.Length);
    }

    // ---- the stream ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_event_on_the_stream_reaches_the_action_relay_and_the_stream_goes_on()
    {
        var action = Message(AgentActionKinds.Status);
        Routes(action);
        var stream = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Actions = NewRelay() };

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Action, JsonSerializer.SerializeToElement(action, ApiClient.Json), CancellationToken.None));

        await Until.True(() => Order.Contains("report:succeeded"));
        Assert.Equal((AgentActionStatuses.Succeeded, "machine status text"), (Final.Status, Final.Result));
    }

    [Fact]
    public async Task An_action_event_is_ignored_and_the_stream_goes_on_when_the_relay_has_no_action_relay()
    {
        var action = Message(AgentActionKinds.Restart);
        Routes(action);
        var stream = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Action, JsonSerializer.SerializeToElement(action, ApiClient.Json), CancellationToken.None));

        Assert.Empty(fx.Fake.Seen);
        Assert.Empty(order);
    }
}
