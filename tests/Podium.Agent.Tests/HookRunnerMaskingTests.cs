using Podium.Agent.Capture;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

// What a permission request stores for the web (the same test class, in its own file so neither passes 300 lines).
public sealed partial class HookRunnerTests
{
    [Fact]
    public async Task A_permission_requests_tool_input_is_masked_even_when_the_workspace_turned_masking_off()
    {
        // GET /permission-requests shows the stored input to every member, so it is masked whatever the capture switch says.
        LogIn(fresh: true);
        TestWorkspace.Set(home.Config, store, Recorder.MaskSecretsKey, "false");
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var token = "ghp_" + new string('a', 36);
        var running = Run("PermissionRequest", new
        {
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "echo " + token + " | gh auth login --with-token", description = "push the tag" },
        });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        daemon.PermissionSent(asked.LocalId, "r-mask");
        daemon.PermissionAnswered("r-mask", PermissionDecisions.Deny, null);
        await running;

        Assert.DoesNotContain(token, asked.ToolInput, StringComparison.Ordinal);
        Assert.Contains("[masked:github_token]", asked.ToolInput, StringComparison.Ordinal);
        Assert.Contains("push the tag", asked.ToolInput, StringComparison.Ordinal); // ordinary fields stay readable
    }

    [Fact]
    public async Task A_tool_input_over_the_cap_is_kept_as_a_marker_with_a_preview_and_one_at_the_cap_whole()
    {
        LogIn(fresh: true);
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var big = new string('x', PermissionToolInputs.MaxBytes);
        var running = Run("PermissionRequest", new { session_id = "s1", tool_name = "Write", tool_input = new { file_path = "/w/a.txt", content = big } });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        daemon.PermissionSent(asked.LocalId, "r-cap");
        daemon.PermissionAnswered("r-cap", PermissionDecisions.Deny, null);
        await running;

        var kept = System.Text.Json.Nodes.JsonNode.Parse(asked.ToolInput)!;
        Assert.True(kept["truncated"]!.GetValue<bool>());
        Assert.True(kept["bytes"]!.GetValue<int>() > PermissionToolInputs.MaxBytes);
        Assert.Equal(PermissionToolInputs.PreviewChars, kept["preview"]!.GetValue<string>().Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(asked.ToolInput) < PermissionToolInputs.MaxBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_cap_keeps_an_input_at_its_size_whole_and_cuts_one_byte_more(int over)
    {
        var json = "{\"c\":\"" + new string('y', PermissionToolInputs.MaxBytes - 8 + over) + "\"}";
        Assert.Equal(PermissionToolInputs.MaxBytes + over, System.Text.Encoding.UTF8.GetByteCount(json));

        var kept = PermissionToolInputs.Capped(json);

        Assert.Equal(over == 0, ReferenceEquals(json, kept));
    }
}
