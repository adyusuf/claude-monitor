using System.Net;
using System.Text.Json;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

// The inline answer of a standing approval (ADR-0019 §7-§8): the relay asks for it, and an allow that comes back with the created request is
// stored as that request's answer, with no person present. An answer for another request is never taken (the same test class, in its own file
// so neither passes 300 lines).
public sealed partial class RelayTests
{
    private void AskBash(string localId)
    {
        Queue("s1", 1);
        fake.On("POST /api/agent/batches", HttpStatusCode.OK, new BatchAck(1, false, 1));
        store.AddPermission(new PermissionAsk(localId, HarnessKinds.ClaudeCode, "s1", "Bash", "{\"command\":\"make build\"}", 60, clock.GetUtcNow(), null, null, null,
            PermissionStates.New));
    }

    [Fact]
    public async Task The_relay_asks_for_an_inline_answer_and_stores_a_standing_approvals_allow_as_the_answer()
    {
        AskBash("l1");
        var remote = Guid.NewGuid();
        var allow = new PermissionAnswerMessage(remote, "s1", PermissionDecisions.Allow, "standing approval");
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.OK, new PermissionRequestCreated(remote, clock.GetUtcNow().AddMinutes(2), allow));

        await relay.PermissionsAsync(CancellationToken.None);

        var sent = JsonSerializer.Deserialize<PermissionRequestCreate>(fake.Seen.Single(s => s.Path == "/api/agent/permission-requests").Body, ApiClient.Json)!;
        Assert.True(sent.InlineAnswer);
        var stored = store.Permission("l1")!;
        Assert.Equal((remote.ToString(), PermissionStates.Answered, PermissionDecisions.Allow, "standing approval"),
            (stored.RemoteId, stored.State, stored.Decision, stored.Reason));
    }

    [Fact]
    public async Task An_inline_answer_for_another_request_is_ignored_and_the_request_waits_for_the_owner()
    {
        // Another request of the session was sent earlier and still waits for its owner: an answer naming it must not answer it from here.
        var earlier = Guid.NewGuid();
        store.AddPermission(new PermissionAsk("l0", HarnessKinds.ClaudeCode, "s1", "Bash", "{\"command\":\"rm -rf build\"}", 60, clock.GetUtcNow(), null, null, null,
            PermissionStates.New));
        store.PermissionSent("l0", earlier.ToString());
        AskBash("l1");
        var remote = Guid.NewGuid();
        var other = new PermissionAnswerMessage(earlier, "s1", PermissionDecisions.Allow, null);
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.OK, new PermissionRequestCreated(remote, clock.GetUtcNow().AddMinutes(2), other));

        await relay.PermissionsAsync(CancellationToken.None);

        var stored = store.Permission("l1")!;
        Assert.Equal((remote.ToString(), PermissionStates.Sent, null), (stored.RemoteId, stored.State, stored.Decision));
        var untouched = store.Permission("l0")!;
        Assert.Equal((PermissionStates.Sent, null), (untouched.State, untouched.Decision));
    }

    [Fact]
    public async Task The_relay_sends_the_asks_local_id_as_its_key_so_a_resent_create_finds_the_request_it_made()
    {
        AskBash("l1");
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.OK, new PermissionRequestCreated(Guid.NewGuid(), clock.GetUtcNow().AddMinutes(2)));

        await relay.PermissionsAsync(CancellationToken.None);

        var sent = JsonSerializer.Deserialize<PermissionRequestCreate>(fake.Seen.Single(s => s.Path == "/api/agent/permission-requests").Body, ApiClient.Json)!;
        Assert.Equal("l1", sent.ClientKey);
    }

    [Fact]
    public async Task A_request_created_with_no_inline_answer_waits_for_the_owner()
    {
        AskBash("l1");
        var remote = Guid.NewGuid();
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.OK, new PermissionRequestCreated(remote, clock.GetUtcNow().AddMinutes(2)));

        await relay.PermissionsAsync(CancellationToken.None);

        Assert.Equal(PermissionStates.Sent, store.Permission("l1")!.State);
    }
}
