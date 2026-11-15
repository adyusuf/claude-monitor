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
}
