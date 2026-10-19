using System.ComponentModel;
using Aipod.Tray;

namespace Aipod.Tray.Tests;

public sealed class ActionsTests : IDisposable
{
    private readonly AgentHome home = new();
    private readonly FakeCli cli = new();

    public void Dispose() => home.Dispose();

    private TrayActions Actions() => new(home.Config, cli);

    [Theory]
    [InlineData(TrayCommand.Reconnect, "restart")]
    [InlineData(TrayCommand.Disconnect, "logout")]
    [InlineData(TrayCommand.CheckForUpdate, "update --check")]
    [InlineData(TrayCommand.UpdateNow, "update")]
    public async Task A_command_runs_the_matching_aipod_command(TrayCommand command, string expected)
    {
        cli.Answer = _ => Task.FromResult(new CliResult(0, "  said something \n"));

        var result = await Actions().RunAsync(command, null, CancellationToken.None);

        Assert.Equal(expected, string.Join(' ', Assert.Single(cli.Runs)));
        Assert.Equal((true, "said something"), (result.Ok, result.Text));
    }

    [Fact]
    public async Task A_command_that_says_nothing_gets_a_default_sentence_or_the_exit_code()
    {
        cli.Answer = _ => Task.FromResult(new CliResult(0, ""));
        Assert.Equal(new ActionResult(true, "Reconnecting"), await Actions().RunAsync(TrayCommand.Reconnect, null, CancellationToken.None));
        cli.Answer = _ => Task.FromResult(new CliResult(3, ""));
        Assert.Equal(new ActionResult(false, "aipod restart failed (exit 3)."), await Actions().RunAsync(TrayCommand.Reconnect, null, CancellationToken.None));
        cli.Answer = _ => Task.FromResult(new CliResult(1, "usage: aipod update"));
        Assert.Equal(new ActionResult(false, "usage: aipod update"), await Actions().RunAsync(TrayCommand.UpdateNow, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    public async Task A_command_that_cannot_run_says_so_with_the_error_type(Type error)
    {
        cli.Answer = _ => throw (Exception)Activator.CreateInstance(error)!;

        var result = await Actions().RunAsync(TrayCommand.Reconnect, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(error.Name, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logging_in_needs_an_http_address_from_the_window_or_the_saved_one()
    {
        var none = await Actions().RunAsync(TrayCommand.Login, "  ", CancellationToken.None);
        Assert.False(none.Ok);
        Assert.Contains("Give the server's address", none.Text, StringComparison.Ordinal);
        var bad = await Actions().RunAsync(TrayCommand.Login, "ftp://x.invalid", CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.Empty(cli.Runs);

        await Actions().RunAsync(TrayCommand.Login, " https://aipod.example.test ", CancellationToken.None);
        Assert.Equal("connect --server https://aipod.example.test", string.Join(' ', cli.Runs[0]));

        home.Connect("https://saved.example.test");
        await Actions().RunAsync(TrayCommand.Login, null, CancellationToken.None);
        Assert.Equal("connect --server https://saved.example.test", string.Join(' ', cli.Runs[1]));
    }

    [Theory]
    [InlineData("https://a.example.test", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("ftp://a.example.test", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("a.example.test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_addresses_count(string? text, bool ok) => Assert.Equal(ok, TrayActions.IsWebAddress(text));

    [Fact]
    public async Task The_web_app_and_the_log_open_with_the_system_and_only_when_there_is_something_to_open()
    {
        var noServer = await Actions().RunAsync(TrayCommand.OpenWeb, null, CancellationToken.None);
        Assert.Equal(new ActionResult(false, "No server is saved yet: log in first."), noServer);
        var noLog = await Actions().RunAsync(TrayCommand.OpenLog, null, CancellationToken.None);
        Assert.Equal(new ActionResult(false, "There is no log yet."), noLog);

        home.Connect("https://saved.example.test");
        File.WriteAllText(home.Config.LogPath, "log");
        Assert.True((await Actions().RunAsync(TrayCommand.OpenWeb, null, CancellationToken.None)).Ok);
        Assert.True((await Actions().RunAsync(TrayCommand.OpenLog, null, CancellationToken.None)).Ok);
        Assert.Equal(["https://saved.example.test", home.Config.LogPath], cli.Opened);
    }

    [Fact]
    public async Task A_saved_address_with_another_scheme_is_never_opened_and_a_failed_open_is_reported()
    {
        home.Connect("ftp://saved.example.test");
        Assert.Equal(new ActionResult(false, "Only http and https addresses are opened."), await Actions().RunAsync(TrayCommand.OpenWeb, null, CancellationToken.None));
        Assert.Empty(cli.Opened);

        home.Connect("https://saved.example.test");
        cli.OpenFails = new Win32Exception("no browser");
        var failed = await Actions().RunAsync(TrayCommand.OpenWeb, null, CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.Contains("Win32Exception", failed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Showing_the_window_and_quitting_are_not_the_actions_business()
    {
        Assert.False((await Actions().RunAsync(TrayCommand.Quit, null, CancellationToken.None)).Ok);
        Assert.False((await Actions().RunAsync(TrayCommand.ShowWindow, null, CancellationToken.None)).Ok);
        Assert.Empty(cli.Runs);
    }
}
