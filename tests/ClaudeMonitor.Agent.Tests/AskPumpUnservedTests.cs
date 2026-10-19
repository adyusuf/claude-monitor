using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The pump tells automatic mode about an ask no session could take, once per pass, and about nothing else (ADR-0014 P3).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class AskPumpUnservedTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox });
    private readonly ManualClock clock = new(Start);
    private readonly LocalStore store;
    private readonly List<AskIn> unserved = [];

    public AskPumpUnservedTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private sealed class Transport : IInboxTransport
    {
        public int Sent { get; private set; }

        public Task SendAsync(string endpoint, IReadOnlyList<string> lines, TimeSpan connectTimeout, CancellationToken ct)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }

    private AskPump Pump(Transport transport, HashSet<int> idle, bool strict = false) => new(() => home.Config, store, transport,
        (_, _, _) => Task.CompletedTask, () => strict, clock, _ => idle, (ask, _) =>
        {
            unserved.Add(ask);
            return Task.CompletedTask;
        });

    private void Ask(int n, string? project) =>
        store.SaveAskIn(new AskMessage(Guid.Parse($"0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e{n:00}"), "what is the state?", "build-box", project, Start.AddMinutes(2)), Start);

    [Fact]
    public async Task An_ask_with_no_idle_session_is_handed_to_the_hook_on_every_pass_with_its_project()
    {
        Ask(1, "git:host/shop");
        var pump = Pump(new Transport(), []);

        await pump.PumpAsync(CancellationToken.None);
        await pump.PumpAsync(CancellationToken.None);

        Assert.Equal(2, unserved.Count);
        Assert.All(unserved, a => Assert.Equal("git:host/shop", a.Project));
    }

    [Fact]
    public async Task An_ask_an_idle_session_took_is_not_handed_to_the_hook()
    {
        Ask(1, null);
        store.SaveInbox("s1", "/tmp/s1.sock", "aaaa-bbbb-test", Start);
        store.BindSession("s1", 100, Start);
        var transport = new Transport();

        await Pump(transport, [100]).PumpAsync(CancellationToken.None);

        Assert.Equal(1, transport.Sent);
        Assert.Empty(unserved);
    }

    [Fact]
    public async Task A_machine_that_does_not_accept_asks_hands_nothing_to_the_hook()
    {
        Ask(1, "git:host/shop");

        await Pump(new Transport(), [], strict: true).PumpAsync(CancellationToken.None);

        Assert.Empty(unserved);
    }
}
