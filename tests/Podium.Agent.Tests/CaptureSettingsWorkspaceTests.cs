using System.Net;
using System.Text.Json.Nodes;
using Podium.Agent.Auth;
using Podium.Agent.Capture;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The capture switches (mask secrets, event size) are the workspace's, like every other switch: `aipod login` forgets them,
/// and one read for another workspace counts as unread, so capture masks and uses the default size until the new workspace answers.
/// </summary>
public sealed class CaptureSettingsWorkspaceTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private void Local(Action<LocalStore> act)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        act(store);
    }

    /// <summary>The old workspace allowed unmasked capture and capped events at 200 bytes.</summary>
    private void OldWorkspaceSwitches() => Local(s =>
    {
        TestWorkspace.Set(home.Config, s, Recorder.MaskSecretsKey, "false");
        TestWorkspace.Set(home.Config, s, Recorder.EventMaxBytesKey, "200");
    });

    /// <summary>Records a note holding a secret; returns its payload as queued (a marker when it was over the size).</summary>
    private (string Text, bool Truncated) RecordNote()
    {
        var text = "";
        var truncated = true;
        Local(s =>
        {
            new Recorder(home.Config, s).Record(EventKinds.Note, "s1", new JsonObject { ["text"] = "password=" + "hunter2hunter2 " + new string('x', 300) }, null, clock.GetUtcNow());
            var e = s.NextBatch(10, int.MaxValue)!.Value.Rows.Single().Event;
            truncated = e.Truncated;
            text = e.Payload.GetRawText();
        });
        return (text, truncated);
    }

    [Fact]
    public async Task Login_forgets_mask_secrets_and_event_max_bytes_and_capture_masks_until_read()
    {
        OldWorkspaceSwitches();
        var tokens = new TokenResponse("a", "r", clock.GetUtcNow(), clock.GetUtcNow(), Guid.NewGuid(), Guid.NewGuid());
        var api = new FakeApi()
            .On("POST /api/device/code", HttpStatusCode.OK, new DeviceCodeResponse("dev", "BCDF-GHJK", "https://m.invalid/device", 0, 600))
            .On("POST /api/device/token", HttpStatusCode.OK, tokens);

        var login = new Login(home.Config, new StringWriter(), new FastClock(clock), _ => true, "macos");
        Assert.Equal(0, await login.RunAsync("https://aipod.invalid", api, CancellationToken.None));

        Local(s => Assert.All(new[] { Recorder.MaskSecretsKey, Recorder.EventMaxBytesKey }, key => Assert.Null(s.Get(key))));
        var (text, truncated) = RecordNote();
        Assert.False(truncated); // the default size, not the old workspace's 200 bytes
        Assert.DoesNotContain("hunter2hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Switches_read_for_another_workspace_count_as_unread_so_capture_masks()
    {
        OldWorkspaceSwitches();
        (Identity.Peek(home.Config)! with { WorkspaceId = Guid.NewGuid() }).Save(home.Config); // agent.json now names another workspace

        var (text, truncated) = RecordNote();
        Assert.False(truncated);
        Assert.DoesNotContain("hunter2hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Switches_read_for_the_current_workspace_still_apply()
    {
        OldWorkspaceSwitches();
        var (text, truncated) = RecordNote();
        Assert.True(truncated);
        Assert.Contains("hunter2hunter2", text, StringComparison.Ordinal); // the 4000-char preview of the unmasked payload
    }
}
