using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The workspace's consent to updating Claude Code belongs to the workspace the machine was in: login and logout forget it.</summary>
public sealed class ClaudeWorkspaceForgetTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private void Local(Action<LocalStore> act)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        act(store);
    }

    private string? Workspace()
    {
        string? value = null;
        Local(s => value = s.Get(ClaudePolicy.WorkspaceKey));
        return value;
    }

    private bool Allowed()
    {
        var allowed = false;
        Local(s => allowed = ClaudePolicy.Allowed(home.Config, s));
        return allowed;
    }

    private Login NewLogin(StringWriter output) => new(home.Config, output, new FastClock(clock), _ => true, "macos");

    private static FakeApi Api(HttpStatusCode tokenStatus, object tokenBody) => new FakeApi()
        .On("POST /api/device/code", HttpStatusCode.OK, new DeviceCodeResponse("dev", "BCDF-GHJK", "https://m.invalid/device", 0, 600))
        .On("POST /api/device/token", tokenStatus, tokenBody);

    // ---- LocalStore.Remove -------------------------------------------------------------------------------------

    [Fact]
    public void Remove_makes_a_key_read_as_never_set_and_leaves_the_others_alone()
    {
        Local(s =>
        {
            s.Set("kept", "1");
            s.Set(ClaudePolicy.WorkspaceKey, "true");
            s.Remove(ClaudePolicy.WorkspaceKey);
            Assert.Null(s.Get(ClaudePolicy.WorkspaceKey));
            Assert.Equal("1", s.Get("kept"));
            s.Remove("never-there"); // forgetting what is not there is not an error
        });
    }

    // ---- logout ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_forgets_the_workspaces_claude_update_consent_so_the_updater_is_off_again()
    {
        SetWorkspace("true");
        SavedSettings.SaveClaudeUpdate(home.Config, true);
        Assert.True(Allowed());

        Assert.Equal(0, await NewLogin(new StringWriter()).LogoutAsync());

        Assert.Null(Workspace());
        Assert.False(Allowed()); // this machine still consents, the workspace's answer is unread: off
    }

    [Fact]
    public async Task Logout_keeps_what_else_the_machine_remembers()
    {
        Local(s =>
        {
            s.Set(ClaudePolicy.WorkspaceKey, "true");
            s.Set("other.key", "stays");
        });
        await NewLogin(new StringWriter()).LogoutAsync();
        Local(s => Assert.Equal("stays", s.Get("other.key")));
    }

    [Fact]
    public async Task Logout_works_on_a_machine_that_never_had_a_workspace_answer()
    {
        Assert.Equal(0, await NewLogin(new StringWriter()).LogoutAsync());
        Assert.Null(Workspace());
    }

    // ---- login -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_login_forgets_the_previous_workspaces_claude_update_consent()
    {
        SetWorkspace("true");
        var tokens = new TokenResponse("a", "r", clock.GetUtcNow(), clock.GetUtcNow(), Guid.NewGuid(), Guid.NewGuid());

        var code = await NewLogin(new StringWriter()).RunAsync("https://monitor.invalid", Api(HttpStatusCode.OK, tokens), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("r", Credentials.For(home.Config).Read(Credentials.Refresh)); // it really did log in
        Assert.Null(Workspace());
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task A_login_that_does_not_succeed_leaves_the_workspace_answer_as_it_was(string error)
    {
        SetWorkspace("true");
        var code = await NewLogin(new StringWriter()).RunAsync("https://monitor.invalid", Api(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error })), CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Equal("true", Workspace());
    }

    private void SetWorkspace(string value) => Local(s => s.Set(ClaudePolicy.WorkspaceKey, value));
}
