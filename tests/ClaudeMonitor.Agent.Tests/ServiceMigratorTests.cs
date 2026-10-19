using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// `aipod service migrate` (ADR-0013, tier 2), on temp folders with a fake systemctl: what is copied and what is not, the order of the tool calls, the health wait, the undo of a failed run,
/// going back, and the cleanup. Only the Linux flow runs here; the macOS and Windows flows share every step except the tool calls, and were not run on those systems.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ServiceMigratorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir = Path.Combine(Path.GetTempPath().StartsWith("/var/folders", StringComparison.Ordinal) ? "/tmp" : Path.GetTempPath(), "cm-mig-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly StringWriter output = new();
    private readonly ConcurrentQueue<string> calls = new();
    private readonly ManualClock clock = new(Start);
    private readonly ServiceLayout from;
    private readonly ServiceLayout to;
    private bool serverReached = true;
    private Func<string, int> answer;

    public ServiceMigratorTests()
    {
        Directory.CreateDirectory(dir);
        from = new ServiceLayout(OsKinds.Linux, $"{dir}/old/bin", $"{dir}/old/bin/cm-agent", $"{dir}/old/home", "cm-agent", $"{dir}/old-etc/exec.json")
        {
            Names = new ServiceNames("cm-agent", $"{dir}/old.service", "old.label", $"{dir}/old.plist", "cm-agent-run"),
        };
        to = new ServiceLayout(OsKinds.Linux, $"{dir}/new/bin", $"{dir}/new/bin/cm-agent", $"{dir}/new/home", "aipod", $"{dir}/new-etc/exec.json")
        {
            Names = new ServiceNames("aipod", $"{dir}/new.service", "aipod.agent", $"{dir}/new.plist", "aipod-run"),
        };
        answer = line => line.StartsWith("/usr/bin/id", StringComparison.Ordinal) ? 1 : 0; // the aipod account does not exist yet
        Directory.CreateDirectory($"{dir}/run-systemd");
        Directory.CreateDirectory($"{dir}/sudoers");
        FillOld();
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static AgentConfig ConfigAt(string home) => AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? home : null);

    private void FillOld(bool connected = true)
    {
        Directory.CreateDirectory(from.BinaryDir);
        Directory.CreateDirectory(from.Home);
        Directory.CreateDirectory(Path.GetDirectoryName(from.ExecConfigPath)!);
        File.WriteAllText(from.Binary, "binary");
        File.WriteAllText(from.Names.UnitPath, "[Unit]\n");
        new Identity(new string('k', 24), "https://monitor.invalid", connected ? Guid.NewGuid() : null, connected ? Guid.NewGuid() : null).Save(ConfigAt(from.Home));
        using (var store = new LocalStore(Path.Combine(from.Home, "agent.db"))) store.Set(Relay.LastContactKey, Start.AddHours(-1).ToString("O"));
        Directory.CreateDirectory(Path.Combine(from.Home, ".net"));
        File.WriteAllText(Path.Combine(from.Home, ".net", "extracted"), "x");
        foreach (var skipped in new[] { "daemon.lock", "daemon.pid", "login-code.txt" }) File.WriteAllText(Path.Combine(from.Home, skipped), "x");
        Directory.CreateDirectory(RunAccount.WorkFolder(from.Home));
        File.WriteAllText(Path.Combine(RunAccount.WorkFolder(from.Home), "scratch"), "x");
        Directory.CreateDirectory(Path.Combine(from.Home, "sub"));
        File.WriteAllText(Path.Combine(from.Home, "sub", "kept.txt"), "kept");
        ExecPolicyLoader.Save(from.ExecConfigPath, new ExecPolicy(ExecLevels.Argv, ["/usr/bin/git"], ["/srv"]));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(from.ExecConfigPath)!, "approvers.json"), "{\"approvers\":[]}");
    }

    private int Run(string tool, IReadOnlyList<string> args)
    {
        var line = $"{tool} {string.Join(' ', args)}";
        calls.Enqueue(line);
        // The new service, once started, reaches the server: its database gets a contact stamp newer than the migration's start.
        if (serverReached && line == $"/usr/bin/systemctl enable --now {to.Names.ServiceName}")
        {
            using var store = new LocalStore(Path.Combine(to.Home, "agent.db"));
            store.Set(Relay.LastContactKey, Start.AddSeconds(5).ToString("O"));
        }

        return answer(line);
    }

    private ServiceMigrator Migrator(bool admin = true, string os = OsKinds.Linux, Func<ServiceLayout, DateTimeOffset, bool>? reached = null) =>
        new(output, Run, os, admin, from, to, null, clock, reached, _ => { }, $"{dir}/sudoers", healthWaitSeconds: 3, systemdRunDir: $"{dir}/run-systemd");

    private string Text => output.ToString();

    private static string PolicyDir(ServiceLayout l) => Path.GetDirectoryName(l.ExecConfigPath)!;

    // ---- a migration that works --------------------------------------------------------------------------------------

    [Fact]
    public void A_dry_run_says_what_it_would_do_and_changes_nothing_not_even_a_tool_call()
    {
        Assert.Equal(0, Migrator().Migrate(dryRun: true));

        Assert.Contains("Dry run: nothing is changed", Text, StringComparison.Ordinal);
        Assert.Contains($"disable (not remove) the old service {from.Names.ServiceName}", Text, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.False(Directory.Exists(to.Home));
        Assert.False(File.Exists(to.Names.UnitPath));
    }

    [Fact]
    public void A_dry_run_needs_no_administrator_and_a_real_run_without_one_stops_with_the_sudo_hint_before_anything_is_touched()
    {
        Assert.Equal(0, Migrator(admin: false).Migrate(dryRun: true));
        Assert.Equal(2, Migrator(admin: false).Migrate(dryRun: false));

        Assert.Contains("needs root", Text, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.False(Directory.Exists(to.Home));
    }

    [Fact]
    public void A_migration_stops_the_old_service_copies_the_home_and_the_policy_installs_the_new_service_waits_for_the_server_and_only_then_disables_the_old_one()
    {
        Assert.Equal(0, Migrator().Migrate(dryRun: false));

        var all = calls.ToList();
        int At(string prefix) => all.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(At("/usr/bin/systemctl stop cm-agent") >= 0);
        Assert.True(At("/usr/bin/systemctl stop cm-agent") < At("/usr/sbin/useradd"));
        Assert.True(At("/usr/sbin/useradd") < At("/bin/chown -R aipod:aipod"));
        Assert.True(At("/bin/chown -R aipod:aipod") < At("/usr/bin/systemctl enable --now aipod"));
        Assert.True(At("/usr/bin/systemctl enable --now aipod") < At("/usr/bin/systemctl disable cm-agent"));
        Assert.Equal("/usr/bin/systemctl disable cm-agent", all[^1]); // nothing after the disable: the old service is kept, not removed
        Assert.True(File.Exists(to.Names.UnitPath));
        Assert.True(File.Exists(from.Names.UnitPath));
        Assert.Contains("Migrated.", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_home_is_copied_without_the_locks_the_login_code_the_bundle_folder_and_the_run_scratch_and_the_old_home_is_untouched()
    {
        Migrator().Migrate(dryRun: false);

        Assert.True(File.Exists(Path.Combine(to.Home, "agent.json")));
        Assert.True(File.Exists(Path.Combine(to.Home, "agent.db")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(to.Home, "sub", "kept.txt")));
        foreach (var skipped in new[] { ".net", "daemon.lock", "daemon.pid", "login-code.txt", RunAccount.WorkFolderName })
        {
            Assert.False(File.Exists(Path.Combine(to.Home, skipped)) || Directory.Exists(Path.Combine(to.Home, skipped)), skipped);
        }

        Assert.True(File.Exists(Path.Combine(from.Home, "daemon.lock")));
        Assert.True(File.Exists(Path.Combine(from.Home, "login-code.txt")));
        Assert.Equal("binary", File.ReadAllText(from.Binary));
    }

    [Fact]
    public void The_exec_policy_and_the_approvers_are_copied_to_the_new_place_the_level_is_kept_and_the_binary_is_the_old_one()
    {
        Migrator().Migrate(dryRun: false);

        Assert.Equal(ExecLevels.Argv, ExecPolicyLoader.ReadExisting(to.ExecConfigPath)!.Level);
        Assert.Equal("{\"approvers\":[]}", File.ReadAllText(Path.Combine(PolicyDir(to), "approvers.json")));
        Assert.Equal("binary", File.ReadAllText(to.Binary));
        Assert.True(File.Exists(from.ExecConfigPath)); // the old policy stays where it was
    }

    [Fact]
    public void A_marker_with_the_time_is_left_beside_the_new_policy_not_in_the_home_the_service_account_owns()
    {
        Migrator().Migrate(dryRun: false);

        var marker = Path.Combine(PolicyDir(to), ServiceMigrator.MarkerFile);
        using (var markerDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker))) Assert.Equal(Start, markerDoc.RootElement.GetProperty("migratedAt").GetDateTimeOffset());
        Assert.False(File.Exists(Path.Combine(to.Home, ServiceMigrator.MarkerFile)));
    }

    // ---- a migration that does not ------------------------------------------------------------------------------------

    [Fact]
    public void A_new_service_that_never_reaches_the_server_puts_everything_back_and_starts_the_old_service_again()
    {
        serverReached = false;

        Assert.Equal(1, Migrator().Migrate(dryRun: false));

        Assert.Contains("did not reach the server", Text, StringComparison.Ordinal);
        Assert.False(File.Exists(to.Names.UnitPath));
        Assert.False(Directory.Exists(to.Home));
        Assert.False(File.Exists(to.Binary));
        Assert.False(File.Exists(to.ExecConfigPath));
        Assert.True(File.Exists(from.Names.UnitPath));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(from.Home, "sub", "kept.txt")));
        Assert.Equal("/usr/bin/systemctl start cm-agent", calls.ToList()[^1]); // the old service runs again, last
        Assert.DoesNotContain("/usr/bin/systemctl disable cm-agent", calls);
        Assert.Contains("Everything was put back", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_of_the_new_install_that_fails_puts_everything_back_too()
    {
        answer = line => line.StartsWith("/usr/bin/id", StringComparison.Ordinal) ? 1 : line == "/usr/bin/systemctl enable --now aipod" ? 1 : 0;

        Assert.Equal(1, Migrator().Migrate(dryRun: false));

        Assert.False(File.Exists(to.Names.UnitPath));
        Assert.False(Directory.Exists(to.Home));
        Assert.False(File.Exists(to.ExecConfigPath));
        Assert.Equal("/usr/bin/systemctl start cm-agent", calls.ToList()[^1]);
        Assert.DoesNotContain("/usr/bin/systemctl disable cm-agent", calls);
    }

    [Fact]
    public void A_service_that_cannot_be_stopped_changes_nothing()
    {
        answer = line => line == "/usr/bin/systemctl stop cm-agent" ? 1 : 0;

        Assert.Equal(1, Migrator().Migrate(dryRun: false));

        Assert.False(Directory.Exists(to.Home));
        Assert.DoesNotContain(calls, c => c.StartsWith("/usr/sbin/useradd", StringComparison.Ordinal));
    }

    [Fact]
    public void It_refuses_without_touching_anything_when_there_is_nothing_to_migrate_or_it_is_unsafe()
    {
        File.Delete(from.Names.UnitPath);
        Assert.Equal(0, Migrator().Migrate(dryRun: false));
        Assert.Contains("Nothing to migrate", Text, StringComparison.Ordinal);

        File.WriteAllText(from.Names.UnitPath, "[Unit]\n");
        File.WriteAllText(to.Names.UnitPath, "[Unit]\n");
        Assert.Equal(0, Migrator().Migrate(dryRun: false));
        Assert.Contains("already exists", Text, StringComparison.Ordinal);
        File.Delete(to.Names.UnitPath);

        FillOld(connected: false);
        Assert.Equal(1, Migrator().Migrate(dryRun: false));
        Assert.Contains("not connected", Text, StringComparison.Ordinal);
        FillOld();

        Directory.CreateDirectory(to.Home);
        File.WriteAllText(Path.Combine(to.Home, "something"), "x");
        Assert.Equal(1, Migrator().Migrate(dryRun: false));
        Assert.Contains("already has files", Text, StringComparison.Ordinal);

        Assert.Equal(2, Migrator(os: "freebsd").Migrate(dryRun: false));
        Assert.Empty(calls);
    }

    // ---- going back and cleaning up ----------------------------------------------------------------------------------

    [Fact]
    public void Going_back_disables_the_new_service_brings_the_new_data_and_policy_to_the_old_places_enables_the_old_service_and_drops_the_marker()
    {
        Migrator().Migrate(dryRun: false);
        foreach (var leftover in new[] { "agent.db-wal", "agent.db-shm" }) File.Delete(Path.Combine(to.Home, leftover)); // this process still holds the new database open; a stopped daemon would have closed it and left no wal
        File.WriteAllText(Path.Combine(from.Home, "agent.db-wal"), "stale wal of the old database"); // must not meet the new database
        File.WriteAllText(Path.Combine(to.Home, "agent.json"), "{\"identity\":\"newer\"}");
        ExecPolicyLoader.Save(to.ExecConfigPath, new ExecPolicy(ExecLevels.Shell, [], []));
        calls.Clear();

        Assert.Equal(0, Migrator().GoBack());

        Assert.Equal("{\"identity\":\"newer\"}", File.ReadAllText(Path.Combine(from.Home, "agent.json")));
        Assert.False(File.Exists(Path.Combine(from.Home, "agent.db-wal")));
        Assert.Equal(ExecLevels.Shell, ExecPolicyLoader.ReadExisting(from.ExecConfigPath)!.Level);
        Assert.False(File.Exists(to.ExecConfigPath)); // an agent that reads the new place first must find nothing there
        Assert.False(File.Exists(Path.Combine(PolicyDir(to), ServiceMigrator.MarkerFile)));
        var all = calls.ToList();
        Assert.True(all.IndexOf("/usr/bin/systemctl stop aipod") < all.IndexOf("/usr/bin/systemctl disable aipod"));
        Assert.True(all.IndexOf("/usr/bin/systemctl disable aipod") < all.IndexOf("/usr/bin/systemctl enable --now cm-agent"));
        Assert.Contains(all, c => c.StartsWith("/bin/chown cm-agent:cm-agent ", StringComparison.Ordinal));
        Assert.True(File.Exists(to.Names.UnitPath)); // kept: the aipod service is disabled, not removed
    }

    [Fact]
    public void Going_back_needs_the_marker_and_both_services()
    {
        Assert.Equal(1, Migrator().GoBack());
        Assert.Contains("nothing to go back to", Text, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public void Finish_keeps_the_old_service_for_seven_days_and_then_removes_only_the_old_places()
    {
        Migrator().Migrate(dryRun: false);
        File.WriteAllText(Path.Combine($"{dir}/sudoers", from.Names.SudoersFileName), "old sudoers");
        calls.Clear();

        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, Migrator().Finish(now: false));
        Assert.Contains("days are left", Text, StringComparison.Ordinal);
        Assert.True(File.Exists(from.Names.UnitPath));
        Assert.Empty(calls);

        clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(0, Migrator().Finish(now: false));

        Assert.False(File.Exists(from.Names.UnitPath));
        Assert.False(Directory.Exists(from.Home));
        Assert.False(Directory.Exists(from.BinaryDir));
        Assert.False(File.Exists(from.ExecConfigPath));
        Assert.False(File.Exists(Path.Combine(PolicyDir(from), "approvers.json")));
        Assert.False(File.Exists(Path.Combine($"{dir}/sudoers", from.Names.SudoersFileName)));
        Assert.Contains("/usr/sbin/userdel cm-agent", calls);
        Assert.True(File.Exists(to.Names.UnitPath) && Directory.Exists(to.Home) && File.Exists(to.Binary) && File.Exists(to.ExecConfigPath));
        Assert.False(File.Exists(Path.Combine(PolicyDir(to), ServiceMigrator.MarkerFile)));
    }

    [Fact]
    public void Finish_now_does_not_wait_and_never_deletes_a_folder_the_new_layout_uses()
    {
        Migrator().Migrate(dryRun: false);
        Assert.Equal(0, Migrator().Finish(now: true));

        var overlapping = to with { Home = from.Home }; // a layout whose new home IS the old one: nothing may be deleted
        var guarded = new ServiceMigrator(output, Run, OsKinds.Linux, true, from, overlapping, null, clock, null, _ => { }, $"{dir}/sudoers", 3, $"{dir}/run-systemd");
        FillOld();
        File.WriteAllText(to.Names.UnitPath, "[Unit]\n");
        File.WriteAllText(Path.Combine(PolicyDir(to), ServiceMigrator.MarkerFile), "{\"migratedAt\":\"" + Start.AddDays(-30).ToString("O") + "\"}");

        calls.Clear();
        Assert.Equal(1, guarded.Finish(now: true));
        Assert.True(Directory.Exists(from.Home));
        Assert.True(File.Exists(from.Names.UnitPath));
        Assert.Empty(calls); // nothing was done before the check failed
    }

    [Fact]
    public void Finish_without_a_migration_has_nothing_to_clean_up()
    {
        Assert.Equal(1, Migrator().Finish(now: true));
        Assert.Contains("no finished migration", Text, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    // ---- the command ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("service")]
    [InlineData("service", "migrate", "--undo", "--finish")]
    [InlineData("service", "migrate", "--now")]
    [InlineData("service", "migrate", "--wrong")]
    [InlineData("service", "other")]
    public void The_command_refuses_a_shape_it_does_not_know_with_exit_2_before_doing_anything(params string[] args)
    {
        var stderr = new StringWriter();
        Assert.Equal(2, ServiceCommands.Service(args, output, stderr, Run, isAdmin: false));
        Assert.Contains("usage: service migrate", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public void The_command_runs_a_dry_run_without_an_administrator_and_names_the_new_services_in_the_plan()
    {
        Assert.Equal(0, ServiceCommands.Service(["service", "migrate", "--dry-run"], output, new StringWriter(), Run, isAdmin: false));
        Assert.True(Text.Contains("Dry run", StringComparison.Ordinal) || Text.Contains("Nothing to migrate", StringComparison.Ordinal)); // this machine has no real service either way
    }

    // ---- the names -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_two_name_sets_differ_in_every_identifier_and_the_old_one_is_what_it_always_was()
    {
        Assert.Equal(("cm-agent", "/etc/systemd/system/cm-agent.service", "com.claudemonitor.agent", "/Library/LaunchDaemons/com.claudemonitor.agent.plist", "cm-agent-run"),
            (ServiceNames.Legacy.ServiceName, ServiceNames.Legacy.UnitPath, ServiceNames.Legacy.Label, ServiceNames.Legacy.PlistPath, ServiceNames.Legacy.SudoersFileName));
        Assert.Equal(("aipod", "/etc/systemd/system/aipod.service", "aipod.agent", "/Library/LaunchDaemons/aipod.agent.plist", "aipod-run"),
            (ServiceNames.Aipod.ServiceName, ServiceNames.Aipod.UnitPath, ServiceNames.Aipod.Label, ServiceNames.Aipod.PlistPath, ServiceNames.Aipod.SudoersFileName));
        Assert.Equal(ServiceNames.Legacy, ServiceLayout.For(OsKinds.Linux).Names);
        Assert.Equal(ServiceNames.Aipod, ServiceLayout.ForAipod(OsKinds.Linux).Names);
    }

    [Theory]
    [InlineData(OsKinds.Linux, "/usr/local/lib/aipod", "/usr/local/lib/aipod/cm-agent", "/var/lib/aipod", "aipod", "/etc/aipod/exec.json")]
    [InlineData(OsKinds.MacOs, "/Library/Application Support/aipod/bin", "/Library/Application Support/aipod/bin/cm-agent", "/Library/Application Support/aipod/home", "_aipod", "/Library/Application Support/aipod/exec.json")]
    public void The_aipod_layout_has_its_own_folders_account_and_policy_but_the_binary_file_keeps_its_name(string os, string binaryDir, string binary, string home, string account, string exec)
    {
        var l = ServiceLayout.ForAipod(os);
        Assert.Equal((binaryDir, binary, home, account, exec), (l.BinaryDir, l.Binary, l.Home, l.Account, l.ExecConfigPath));
    }

    [Fact]
    public void The_windows_aipod_layout_is_under_its_own_program_folders_with_its_own_virtual_account()
    {
        var l = ServiceLayout.ForAipod(OsKinds.Windows, @"C:\Program Files", @"C:\ProgramData");
        Assert.Equal((@"C:\Program Files\aipod", @"C:\Program Files\aipod\cm-agent.exe", @"C:\ProgramData\aipod\service", @"NT SERVICE\aipod"), (l.BinaryDir, l.Binary, l.Home, l.Account));
        Assert.StartsWith(@"HKLM\SYSTEM\CurrentControlSet\Services\aipod", WindowsServiceSetup.Environment(l)[1], StringComparison.Ordinal);
        Assert.Equal(["create", "aipod", "binPath=", "\"C:\\Program Files\\aipod\\cm-agent.exe\" daemon", "obj=", @"NT SERVICE\aipod", "start=", "delayed-auto", "DisplayName=", "aipod agent"], WindowsServiceSetup.Create(l));
        Assert.Equal(["config", "cm-agent", "start=", "disabled"], WindowsServiceSetup.StartType("cm-agent", disabled: true));
        Assert.Equal(["config", "aipod", "start=", "delayed-auto"], WindowsServiceSetup.StartType("aipod", disabled: false));
    }

    [Fact]
    public void The_exec_policy_is_read_from_the_new_place_first_and_the_old_one_second()
    {
        var present = new HashSet<string>();
        Assert.Equal("/etc/cm-agent/exec.json", AgentConfig.ResolveExecConfigPath(OsKinds.Linux, present.Contains));
        present.Add("/etc/cm-agent/exec.json");
        Assert.Equal("/etc/cm-agent/exec.json", AgentConfig.ResolveExecConfigPath(OsKinds.Linux, present.Contains));
        present.Add("/etc/aipod/exec.json");
        Assert.Equal("/etc/aipod/exec.json", AgentConfig.ResolveExecConfigPath(OsKinds.Linux, present.Contains));
        Assert.Equal("/Library/Application Support/ClaudeMonitor/exec.json", AgentConfig.ResolveExecConfigPath(OsKinds.MacOs, _ => false));
        Assert.Equal("/Library/Application Support/aipod/exec.json", AgentConfig.ResolveExecConfigPath(OsKinds.MacOs, p => p.Contains("aipod", StringComparison.Ordinal)));
    }
}
