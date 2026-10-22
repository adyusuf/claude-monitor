using System.Net;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Push;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The two message counters the tray's lights watch: orange (a message arrived here), green (a message left or reached a session).</summary>
public sealed class LedCounterTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;
    private readonly FakeApi fake = new();
    private readonly HttpClient http;
    private readonly ApiClient api;
    private readonly Relay relay;

    public LedCounterTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        http = ApiClient.CreateHttp("https://aipod.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        relay = new Relay(home.Config, store, api, clock);
        fake.On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "");
    }

    public void Dispose()
    {
        api.Dispose();
        http.Dispose();
        store.Dispose();
        home.Dispose();
    }

    private Task<bool> Stream(string name, object message) => relay.OnStreamAsync(name, JsonSerializer.SerializeToElement(message, ApiClient.Json), CancellationToken.None);

    [Fact]
    public void A_counter_starts_at_zero_counts_up_and_treats_a_damaged_value_as_zero()
    {
        Assert.Equal(0, store.Count("x"));
        store.Bump("x");
        store.Bump("x");
        Assert.Equal(2, store.Count("x"));
        store.Set("x", "not a number");
        Assert.Equal(0, store.Count("x"));
        store.Bump("x");
        Assert.Equal(1, store.Count("x"));
    }

    [Fact]
    public async Task A_command_or_ask_that_arrives_counts_once_even_when_the_stream_sends_it_again_on_reconnect()
    {
        var command = new AgentCommandMessage(Guid.NewGuid(), Guid.NewGuid(), "s1", CommandKinds.Prompt, "hello", clock.GetUtcNow().AddMinutes(5));
        await Stream(AgentStreamEvents.Command, command);
        await Stream(AgentStreamEvents.Command, command);
        Assert.Equal(1, store.Count(PushStatus.InCountKey));

        var ask = new AskMessage(Guid.NewGuid(), "which branch?", "box", null, clock.GetUtcNow().AddMinutes(2));
        await Stream(AgentStreamEvents.Ask, ask);
        await Stream(AgentStreamEvents.Ask, ask);
        Assert.Equal(2, store.Count(PushStatus.InCountKey));
        Assert.Equal(0, store.Count(PushStatus.OutCountKey));
    }

    [Fact]
    public async Task A_message_pushed_into_a_session_counts_out_once_and_not_again_when_it_is_confirmed_and_a_hook_taken_one_counts_when_reported()
    {
        store.SaveCommand(new LocalCommand("c-push", "s1", CommandKinds.Prompt, "pushed", clock.GetUtcNow().AddMinutes(5)));
        store.SaveCommand(new LocalCommand("c-hook", "s1", CommandKinds.Prompt, "taken by a hook", clock.GetUtcNow().AddMinutes(5)));
        store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), CommandTransports.Inbox).RemoveAll(c => c.Id == "c-hook"); // both are taken; only c-push counts below
        store.PushFailed("c-hook"); // back to the queue for a hook
        await relay.ReportCommandsAsync(CancellationToken.None);
        Assert.Equal(1, store.Count(PushStatus.OutCountKey)); // the push

        Assert.True(store.ConfirmPush("c-push", clock.GetUtcNow())); // it showed in the transcript: applied
        await relay.ReportCommandsAsync(CancellationToken.None);
        Assert.Equal(1, store.Count(PushStatus.OutCountKey)); // not counted a second time

        Assert.NotNull(store.TakeCommand("s1", CommandKinds.Prompt, clock.GetUtcNow())); // a hook takes c-hook
        await relay.ReportCommandsAsync(CancellationToken.None);
        Assert.Equal(2, store.Count(PushStatus.OutCountKey));
    }
}
