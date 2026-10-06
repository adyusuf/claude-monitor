using System.Globalization;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>`cm-agent update`, `cm-agent config` and the update lines of `cm-agent status`, driven the way the command line drives them.</summary>
public sealed class UpdateCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 30, 0, TimeSpan.Zero);

    private UpdateKit kit = new(clock: new VirtualClock(Now));

    public void Dispose() => kit.Dispose();

    private async Task<(int Code, string Out, string Err)> Cli_(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(args, kit.Config, new StringReader(""), stdout, stderr, kit.Clock);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<(int Code, string Out, string Err)> Update_(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await UpdateCommand.UpdateAsync(args, kit.Config, stdout, stderr, kit.Clock, kit.Host, kit.Runner, kit.Daemon);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void Workspace(string mode)
    {
        using var store = new LocalStore(kit.Config.DatabasePath);
        TestWorkspace.Set(kit.Config, store, UpdatePolicy.WorkspaceKey, mode);
    }

    private static string Stamp => Now.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    // ---- config ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Config_shows_that_auto_update_is_off_by_default()
    {
        var (code, output, _) = await Cli_("config");
        Assert.Equal(0, code);
        Assert.Equal("auto-update: off" + Environment.NewLine, output);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("check")]
    [InlineData("off")]
    public async Task Config_saves_a_mode_and_shows_it_afterwards(string mode)
    {
        var (code, output, _) = await Cli_("config", "auto-update", mode);
        Assert.Equal(0, code);
        Assert.Equal($"auto-update: {mode}{Environment.NewLine}", output);
        Assert.Equal($"auto-update: {mode}{Environment.NewLine}", (await Cli_("config")).Out);
        Assert.Equal(mode, Auth.Identity.Peek(kit.Config)!.AutoUpdate);
    }

    [Theory]
    [InlineData("config", "auto-update", "bogus")]
    [InlineData("config", "auto-update", "ON")]
    [InlineData("config", "auto-update")]
    [InlineData("config", "foo")]
    [InlineData("config", "foo", "bar")]
    [InlineData("config", "auto-update", "on", "extra")]
    public async Task Config_with_anything_else_prints_the_usage_and_exits_2_without_saving(params string[] args)
    {
        var (code, output, error) = await Cli_(args);
        Assert.Equal(2, code);
        Assert.Contains("usage: cm-agent config [auto-update off|check|on]", error, StringComparison.Ordinal);
        Assert.Equal("", output);
        Assert.Null(Auth.Identity.Peek(kit.Config)?.AutoUpdate);
    }

    [Fact]
    public async Task The_usage_text_lists_the_new_commands()
    {
        var usage = (await Cli_()).Out;
        Assert.Contains("cm-agent update [--check]", usage, StringComparison.Ordinal);
        Assert.Contains("cm-agent config [auto-update off|check|on]", usage, StringComparison.Ordinal);
    }

    // ---- status ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("off", null, "auto-update: off (this machine: off, workspace: off)")]
    [InlineData("on", null, "auto-update: off (this machine: on, workspace: off)")]
    [InlineData("on", "on", "auto-update: on (this machine: on, workspace: on)")]
    [InlineData("on", "check", "auto-update: check (this machine: on, workspace: check)")]
    [InlineData("check", "on", "auto-update: check (this machine: check, workspace: on)")]
    [InlineData("off", "on", "auto-update: off (this machine: off, workspace: on)")]
    public async Task Status_shows_the_effective_mode_and_both_sides(string machine, string? workspace, string expected)
    {
        await Cli_("config", "auto-update", machine);
        if (workspace is not null) Workspace(workspace);
        var (_, status, _) = await Cli_("status");
        Assert.Contains(expected + Environment.NewLine, status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_without_any_update_activity_has_no_update_noise()
    {
        var (_, status, _) = await Cli_("status");
        Assert.DoesNotContain("last update check", status, StringComparison.Ordinal);
        Assert.DoesNotContain("update available", status, StringComparison.Ordinal);
        Assert.DoesNotContain("rolled back", status, StringComparison.Ordinal);
    }

    // ---- update --check ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_check_reports_an_available_build_and_installs_nothing()
    {
        kit.Connect();
        kit.InstallOld();
        var offer = kit.PublishGood();

        var (code, output, error) = await Update_("update", "--check");

        Assert.Equal(0, code);
        Assert.Contains($"update available: {offer.Version}", output, StringComparison.Ordinal);
        Assert.Equal("", error);
        kit.AssertUntouched();
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls);
        Assert.Empty(kit.Daemon.Calls);
        Assert.Contains($"update available: {offer.Version} (cm-agent update)", (await Cli_("status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_check_says_when_the_running_version_is_below_the_servers_minimum()
    {
        kit.Connect();
        kit.Publish(kit.Offer(kit.Zip(), minSupported: UpdateKit.Newer));
        var (code, output, _) = await Update_("update", "--check");
        Assert.Equal(0, code);
        Assert.Contains("below the server's minimum supported version", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_offer_exits_1_with_the_reason_on_stderr_and_status_remembers_it_with_its_date()
    {
        kit.Connect();
        kit.Publish(kit.Offer(kit.Zip()) with { Sha256 = UpdateKit.Sha256([9]) }); // changed after signing

        var (code, output, error) = await Update_("update", "--check");

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("update refused: bad-signature", error, StringComparison.Ordinal);
        var line = (await Cli_("status")).Out.Split(Environment.NewLine).Single(l => l.StartsWith("last update check:", StringComparison.Ordinal));
        Assert.StartsWith("last update check: bad-signature (", line, StringComparison.Ordinal);
        Assert.EndsWith(" " + Stamp, line, StringComparison.Ordinal); // dd/MM/yyyy HH:mm
    }

    [Fact]
    public async Task Up_to_date_exits_0()
    {
        kit.Connect();
        kit.Publish(kit.Offer(kit.Zip(), AgentConfig.Version));
        var (code, output, error) = await Update_("update", "--check");
        Assert.Equal(0, code);
        Assert.Contains($"newest version ({AgentConfig.Version})", output, StringComparison.Ordinal);
        Assert.Equal("", error);
        Assert.Contains("last update check: up-to-date", (await Cli_("status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_published_exits_0()
    {
        kit.Connect();
        var (code, output, _) = await Update_("update", "--check");
        Assert.Equal(0, code);
        Assert.Contains("no update is published", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_connection_there_is_no_server_to_ask()
    {
        var (code, _, error) = await Update_("update", "--check");
        Assert.Equal(1, code);
        Assert.Contains("not connected", error, StringComparison.Ordinal);
        Assert.Empty(kit.Api.Seen);
        var viaCli = await Cli_("update");
        Assert.Equal(1, viaCli.Code);
        Assert.Contains("not connected", viaCli.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("update", "--frobnicate")]
    [InlineData("update", "now")]
    [InlineData("update", "--check", "--auto")]
    [InlineData("update", "--auto", "--check")]
    [InlineData("update", "--check", "--force")]
    public async Task Unknown_flags_and_check_with_auto_exit_2_without_touching_the_server(params string[] args)
    {
        kit.Connect();
        var (code, _, error) = await Update_(args);
        Assert.Equal(2, code);
        Assert.Contains("usage: cm-agent update [--check]", error, StringComparison.Ordinal);
        Assert.Empty(kit.Api.Seen);
        Assert.Equal(2, (await Cli_(args)).Code);
    }

    // ---- update -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_installs_the_build_even_though_auto_update_is_off()
    {
        kit.Connect();
        kit.InstallOld();
        var offer = kit.PublishGood();
        Assert.Equal(UpdateModes.Off, kit.Config.AutoUpdate); // an explicit update is the person's own decision

        var (code, output, error) = await Update_("update");

        Assert.Equal(0, code);
        Assert.StartsWith("updated:", output, StringComparison.Ordinal);
        Assert.Contains(offer.Version, output, StringComparison.Ordinal);
        Assert.Equal("", error);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Previous));
        var status = (await Cli_("status")).Out;
        Assert.Contains("last update check: installed", status, StringComparison.Ordinal);
        Assert.DoesNotContain("update available", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_daemons_own_update_run_installs_the_same_way()
    {
        kit.Connect();
        kit.InstallOld();
        kit.PublishGood();
        var (code, output, _) = await Update_("update", "--auto");
        Assert.Equal(0, code);
        Assert.StartsWith("updated:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_install_exits_1_and_names_the_code()
    {
        kit.Connect();
        kit.InstallOld();
        kit.PublishGood();
        kit.Runner.VersionOutput = "9.9.9";
        var (code, output, error) = await Update_("update");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("update bad-binary:", error, StringComparison.Ordinal);
        kit.AssertUntouched();
    }

    [Fact]
    public async Task A_rolled_back_build_is_shown_by_status_as_not_retried_by_itself()
    {
        kit.Dispose();
        kit = new UpdateKit(clock: new VirtualClock(Now), healthy: false);
        kit.Connect();
        kit.InstallOld();
        var offer = kit.PublishGood();

        var (code, _, error) = await Update_("update");

        Assert.Equal(1, code);
        Assert.Contains("update rolled-back:", error, StringComparison.Ordinal);
        var status = (await Cli_("status")).Out;
        Assert.Contains($"update {offer.Version} was rolled back and is not retried by itself", status, StringComparison.Ordinal);
        Assert.Contains("last update check: rolled-back", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_in_progress_is_shown_by_status()
    {
        UpdateState.Change(kit.Config, s => s with { Phase = UpdateState.PendingHealth, From = "1.0.0", To = "1.1.0" });
        Assert.Contains("update: 1.0.0 -> 1.1.0 is being checked", (await Cli_("status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_line_whose_date_cannot_be_read_shows_a_question_mark()
    {
        UpdateState.Change(kit.Config, s => s with { Result = UpdateCodes.Unreachable, Detail = "down", CheckedAt = "yesterday-ish" });
        Assert.Contains("last update check: unreachable (down) ?", (await Cli_("status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_state_file_reads_as_nothing_happened()
    {
        await File.WriteAllTextAsync(kit.Config.UpdateStatePath, "{ not json");
        var (_, status, _) = await Cli_("status");
        Assert.DoesNotContain("last update check", status, StringComparison.Ordinal);
        Assert.Equal(new UpdateState(), UpdateState.Load(kit.Config));
    }
}
