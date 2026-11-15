using System.Net;
using System.Text.Json;
using Podium.Agent.Access;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// How the agent keeps the workspace's access policies (ADR-0020 §4 rule 1): it fetches the set only when the stamp moves, an older API (404) leaves
/// what it holds, a delivery at the cap or of another workspace means the default for every run, and a run naming a stamp it cannot catch up with is
/// refused as stale.
/// </summary>
public sealed class AccessPolicySyncTests : IDisposable
{
    private const string Route = "GET /api/agent/access-policies";
    private const string Key = "git:git.example.invalid/team/shop";
    private static readonly AccessOsRules None = new([], [], []);
    private readonly RemoteFixture fx = new();
    private readonly Guid workspace;

    public AccessPolicySyncTests() => workspace = TestWorkspace.Id(fx.Home.Config);

    public void Dispose() => fx.Dispose();

    private AccessPolicySync Sync => new(fx.Home.Config, fx.Store, fx.Api, fx.Clock);

    private AccessPolicyMessage Message(Guid? project = null, Guid? ws = null)
    {
        var doc = new AccessPolicyDocument(AccessPolicyRules.Format, [Key], false, new AccessOsRules(["/srv/app"], [], []), None, None, false, false);
        return new AccessPolicyMessage(project ?? Guid.NewGuid(), ws ?? workspace, 1, doc, AccessPolicySigning.Hash(doc), null, null);
    }

    private void Serve(string stamp, params AccessPolicyMessage[] policies) =>
        fx.Fake.On(Route, _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentAccessPolicies(stamp, policies), ApiClient.Json)));

    private void Stamp(string stamp) => TestWorkspace.Set(fx.Home.Config, fx.Store, AccessPolicySync.StampKey, stamp);

    private string Marker(string stamp) => workspace.ToString("D") + ":" + stamp;

    private RunMessage Run(string? stamp) =>
        new(Guid.NewGuid(), RunModes.Argv, ["/usr/bin/true"], null, null, 30, fx.Clock.GetUtcNow().AddMinutes(5), null, null, AccessStamp: stamp);

    [Fact]
    public async Task The_set_is_fetched_when_the_stamp_moves_and_not_again_while_it_holds()
    {
        var policy = Message();
        Serve("s1", policy);
        Stamp("s1");

        await Sync.SyncAsync(CancellationToken.None);
        await Sync.SyncAsync(CancellationToken.None);

        Assert.Equal(1, fx.Fake.Count(Route));
        Assert.Equal(policy.ProjectId.ToString("D"), Assert.Single(fx.Store.AccessPolicies()).ProjectId);
        Assert.Equal(Marker("s1"), fx.Store.Get(LocalStore.AccessSyncedKey));
        Assert.NotEqual("true", fx.Store.Get(LocalStore.AccessTruncatedKey));
    }

    [Fact]
    public async Task A_policy_the_full_delivery_no_longer_lists_is_dropped_where_signatures_are_not_required()
    {
        var first = Message();
        Serve("s1", first, Message());
        Stamp("s1");
        await Sync.SyncAsync(CancellationToken.None);

        Serve("s2", first);
        Stamp("s2");
        await Sync.SyncAsync(CancellationToken.None);

        Assert.Equal(first.ProjectId.ToString("D"), Assert.Single(fx.Store.AccessPolicies()).ProjectId);
    }

    [Fact]
    public async Task An_api_without_the_call_leaves_what_the_agent_holds_and_is_asked_once_per_stamp()
    {
        Serve("s1", Message());
        Stamp("s1");
        await Sync.SyncAsync(CancellationToken.None);
        fx.Fake.On(Route, _ => (HttpStatusCode.NotFound, ""));
        Stamp("s2");

        await Sync.SyncAsync(CancellationToken.None);
        await Sync.SyncAsync(CancellationToken.None);

        Assert.Single(fx.Store.AccessPolicies());
        Assert.Equal(2, fx.Fake.Count(Route));
    }

    [Fact]
    public async Task A_delivery_at_the_cap_is_taken_as_cut_so_nothing_held_is_dropped_and_the_default_applies()
    {
        var held = Message();
        Serve("s1", held);
        Stamp("s1");
        await Sync.SyncAsync(CancellationToken.None);

        Serve("s2", [.. Enumerable.Range(0, 200).Select(_ => Message())]);
        Stamp("s2");
        await Sync.SyncAsync(CancellationToken.None);

        Assert.Equal("true", fx.Store.Get(LocalStore.AccessTruncatedKey));
        Assert.Contains(fx.Store.AccessPolicies(), p => p.ProjectId == held.ProjectId.ToString("D"));
    }

    [Fact]
    public async Task A_delivery_of_another_workspace_is_not_stored_and_its_stamp_not_recorded_so_the_default_applies_until_the_next_pass()
    {
        Serve("s1", Message(ws: Guid.NewGuid()));
        Stamp("s1");

        await Sync.SyncAsync(CancellationToken.None);

        Assert.Empty(fx.Store.AccessPolicies());
        Assert.Equal("true", fx.Store.Get(LocalStore.AccessTruncatedKey));
        Assert.NotEqual(Marker("s1"), fx.Store.Get(LocalStore.AccessSyncedKey));
    }

    [Fact]
    public async Task A_run_naming_the_held_stamp_goes_on_without_a_fetch()
    {
        Serve("s1", Message());
        Stamp("s1");
        await Sync.SyncAsync(CancellationToken.None);

        Assert.True(await Sync.FreshAsync(Run("s1"), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, fx.Fake.Count(Route));
    }

    [Fact]
    public async Task A_run_naming_a_stamp_the_api_cannot_deliver_is_refused_as_stale_by_the_policy_layer()
    {
        fx.Fake.On(Route, _ => (HttpStatusCode.NotFound, ""));
        var settingsRead = false;
        var run = Run("newer");

        var fresh = await Sync.FreshAsync(run, _ => { settingsRead = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(fresh);
        Assert.True(settingsRead);
        var stored = fx.Store.ExecRunOf(run.Id.ToString())!;
        Assert.Equal((RunStatuses.Failed, AccessErrors.Stale), (stored.FinalStatus, stored.Error));
    }

    [Fact]
    public async Task A_malformed_answer_is_not_swallowed_so_the_server_replays_the_run()
    {
        fx.Fake.On(Route, _ => (HttpStatusCode.OK, "[]"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Sync.FreshAsync(Run("newer"), _ => Task.CompletedTask, CancellationToken.None));
    }
}
