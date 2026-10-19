using System.Globalization;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// "aipod service migrate [--dry-run | --undo | --finish]" (ADR-0013, tier 2): moves a boot service from the old identifiers (cm-agent, _cmagent, com.claudemonitor.agent, the ClaudeMonitor
/// folders) to the aipod ones. Explicit, never automatic, and never destructive until <c>--finish</c>:
///  1. it refuses unless the old service is installed and connected, the aipod names are free and it runs as an administrator;
///  2. it stops the old service, copies its home and its exec policy (and approvers) to the new places, and installs the new service with the same code as a fresh install;
///  3. it waits for the new service to reach the server (Unix; on Windows the DPAPI tokens belong to the old account and cannot be read by the new one, so the machine logs in again and an administrator approves a code);
///  4. only then it DISABLES the old service (it keeps its files) and leaves a marker with the time;
///  5. <c>--undo</c> re-enables the old one and disables the new; <c>--finish</c>, no sooner than seven days after (or with <c>--now</c>), removes the old service, account, folders and policy.
/// A failure before step 4 puts everything back as it was: the new files and service are removed and the old service is started again. Every external tool goes through <c>run</c>.
/// </summary>
public sealed partial class ServiceMigrator(TextWriter output, Func<string, IReadOnlyList<string>, int> run, string os, bool isAdmin, ServiceLayout from, ServiceLayout to,
    Func<string, IReadOnlyList<string>, string?>? read = null, TimeProvider? clock = null, Func<ServiceLayout, DateTimeOffset, bool>? reachedServer = null,
    Action<TimeSpan>? sleep = null, string? sudoersFolder = null, int healthWaitSeconds = ServiceMigrator.DefaultHealthWaitSeconds, string? systemdRunDir = null)
{
    public const int DefaultHealthWaitSeconds = 90;
    public const string MarkerFile = "migrated-from.json";
    public static readonly TimeSpan KeepOld = TimeSpan.FromDays(7);

    private const string Systemctl = "/usr/bin/systemctl";
    private const string Launchctl = "/bin/launchctl";
    private const string Dscl = "/usr/bin/dscl";
    private const string UserDel = "/usr/sbin/userdel";

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly List<(string What, Action Undo)> undo = [];

    private sealed class StepFailedException(string message) : Exception(message);

    /// <summary>What <see cref="Migrate"/> would do, one line each: the same text for <c>--dry-run</c> and the start of a real run.</summary>
    public IReadOnlyList<string> Plan()
    {
        var plan = new List<string>
        {
            $"stop the service {from.Names.ServiceName} and copy its home {from.Home} to {to.Home}" + (os == OsKinds.Windows ? " (without its encrypted tokens: this machine logs in again)" : ""),
            $"copy the exec policy and the approvers from {Path.GetDirectoryName(from.ExecConfigPath)} to {Path.GetDirectoryName(to.ExecConfigPath)}",
            $"create the account {to.Account} and the service {to.Names.ServiceName} (binary {to.Binary}, home {to.Home}) and start it",
        };
        if (os != OsKinds.Windows) plan.Add($"wait up to {healthWaitSeconds} s for the new service to reach the server; if it does not, put everything back");
        plan.Add($"disable (not remove) the old service {from.Names.ServiceName}; its files stay for {KeepOld.TotalDays:0} days");
        plan.Add("later: `service migrate --undo` goes back, `service migrate --finish` removes the old service, account, folders and policy");
        return plan;
    }

    public int Migrate(bool dryRun)
    {
        if (Unsupported() is { } unsupported) return unsupported;
        if (Refusal() is { } refusal)
        {
            output.WriteLine(refusal.Text);
            return refusal.Code;
        }

        output.WriteLine(dryRun ? "Dry run: nothing is changed. A real run would:" : "Migrating the service to the aipod names:");
        foreach (var line in Plan()) output.WriteLine($"  - {line}");
        if (dryRun) return 0;
        if (!isAdmin) return NeedAdmin("service migrate");

        var started = time.GetUtcNow();
        var policy = ExecPolicyLoader.ReadExisting(from.ExecConfigPath);
        var level = policy?.Level ?? ExecLevels.Off;
        var identity = IdentityOf(from);
        var target = to with { Server = identity?.Server is { } s && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? s.TrimEnd('/') : null };
        try
        {
            StopService(from, "the old service");
            undo.Add(("old service stopped", () => StartService(from)));
            if (os != OsKinds.Windows) CopyHome(); // the Windows home is copied after the service exists: its ACL must be there first
            CopyPolicy();
            var installer = new ServiceInstaller(output, run, os, isAdmin, target, systemdRunDir: systemdRunDir, read: read, sleep: sleep) { SudoersFolder = sudoersFolder ?? "/etc/sudoers.d", ChownHomeRecursively = true };
            if (installer.Install(from.Binary, level, allowRoot: false, policy?.RunAccount) != 0) throw new StepFailedException("the new service could not be installed (its own steps were undone)");
            undo.Add(("new service installed", () => RemoveNewService(target)));
            if (os == OsKinds.Windows) CopyHomeOntoRunningWindowsService(target);
            else if (!WaitForServer(target, started)) throw new StepFailedException($"the new service did not reach the server within {healthWaitSeconds} s");
            DisableService(from, "the old service");
            WriteMarker(target);
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Migration failed: {e.Message}");
            RollBack();
            output.WriteLine("Everything was put back: the old service runs as before and the aipod files were removed.");
            return 1;
        }

        undo.Clear();
        output.WriteLine($"Migrated. The service is now {target.Names.ServiceName}; the old one is disabled and kept: its files stay for {KeepOld.TotalDays:0} days.");
        output.WriteLine(os == OsKinds.Windows
            ? "This machine logs in again: read the code with `type \"" + target.LoginCodePath + "\"` in an elevated prompt and approve it on the web."
            : "To go back:  service migrate --undo     To remove the old service for good (after " + KeepOld.TotalDays.ToString("0", CultureInfo.InvariantCulture) + " days):  service migrate --finish");
        return 0;
    }

    /// <summary>Goes back: stops and disables the new service, brings its data home, enables and starts the old one.</summary>
    public int GoBack()
    {
        if (Unsupported() is { } unsupported) return unsupported;
        if (!isAdmin) return NeedAdmin("service migrate --undo");
        if (!File.Exists(MarkerPath(to)) || !ServiceInstaller.IsInstalled(os, from, run) || !ServiceInstaller.IsInstalled(os, to, run))
        {
            output.WriteLine("There is nothing to go back to: both the aipod service and the disabled old one, and the migration marker, are needed.");
            return 1;
        }

        try
        {
            StopService(to, "the aipod service");
            DisableService(to, "the aipod service");
            if (os != OsKinds.Windows) BringDataBack();
            BringPolicyBack();
            EnableService(from, "the old service");
            File.Delete(MarkerPath(to));
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Going back failed: {e.Message}");
            return 1;
        }

        output.WriteLine($"Back on the old service {from.Names.ServiceName}. The aipod service is disabled and its files are kept; `service migrate` can be run again.");
        return 0;
    }

    /// <summary>Removes the old service for good: its definition, account, folders, exec policy and sudoers drop-in. Not before seven days unless <paramref name="now"/>.</summary>
    public int Finish(bool now)
    {
        if (Unsupported() is { } unsupported) return unsupported;
        if (!isAdmin) return NeedAdmin("service migrate --finish");
        var marker = ReadMarker();
        if (marker is null || !ServiceInstaller.IsInstalled(os, to, run))
        {
            output.WriteLine("There is no finished migration to clean up: the aipod service and its migration marker are needed.");
            return 1;
        }

        var age = time.GetUtcNow() - marker.Value;
        if (!now && age < KeepOld)
        {
            output.WriteLine($"The old service is kept for {KeepOld.TotalDays:0} days after the migration; {(KeepOld - age).TotalDays:0.0} days are left. `service migrate --finish --now` removes it anyway.");
            return 1;
        }

        try
        {
            RemoveOldFiles();
            File.Delete(MarkerPath(to));
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Cleaning up failed: {e.Message}");
            return 1;
        }

        output.WriteLine("The old service, its account, folders and policy are removed. The aipod service is all that is left.");
        return 0;
    }

    private (int Code, string Text)? Refusal()
    {
        if (ServiceInstaller.IsInstalled(os, to, run)) return (0, $"Nothing to migrate: the service {to.Names.ServiceName} already exists.");
        if (!ServiceInstaller.IsInstalled(os, from, run)) return (0, $"Nothing to migrate: no service named {from.Names.ServiceName} is installed.");
        var problem = os switch
        {
            OsKinds.Linux => SystemdUnit.Check(to),
            OsKinds.Windows => WindowsServiceSetup.Check(to),
            _ => null,
        };
        if (problem is not null) return (1, $"Not migrated: {problem}");
        if (!File.Exists(Path.Combine(from.Home, "agent.json")) || IdentityOf(from) is not { Connected: true })
        {
            return (1, $"Not migrated: the old service in {from.Home} is not connected to the server yet. Connect it first (read its login code and approve it), then migrate.");
        }

        foreach (var folder in new[] { to.Home, to.BinaryDir, Path.GetDirectoryName(to.ExecConfigPath)! }.Distinct(StringComparer.Ordinal))
        {
            if (HasFiles(folder)) return (1, $"Not migrated: {folder} already has files. Look at it and move or remove them first.");
        }

        return null;
    }

    /// <summary>The exit code when this OS has no service, else null.</summary>
    private int? Unsupported()
    {
        if (os is OsKinds.Linux or OsKinds.MacOs or OsKinds.Windows) return null;
        output.WriteLine($"A service is not supported on this OS ({os}).");
        return 2;
    }

    private int NeedAdmin(string verb)
    {
        var command = $"{Environment.ProcessPath ?? "cm-agent"} {verb}";
        output.WriteLine(os == OsKinds.Windows
            ? $"Migrating a service needs an administrator. Run this from an elevated prompt:  \"{command}\""
            : $"Migrating a service needs root. Run:  sudo \"{command}\"");
        return 2;
    }

    private static bool HasFiles(string folder) => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();

    private static Identity? IdentityOf(ServiceLayout l) =>
        Identity.Peek(AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? l.Home : null));

    /// <summary>Beside the exec policy, in the admin-owned folder: on Windows the home's ACL lets an administrator read but not write, and a marker the service account could edit would mean nothing.</summary>
    private static string MarkerPath(ServiceLayout l) => Path.Combine(Path.GetDirectoryName(l.ExecConfigPath)!, MarkerFile);

    private void WriteMarker(ServiceLayout target)
    {
        File.WriteAllText(MarkerPath(target), JsonSerializer.Serialize(new { migratedAt = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), from = from.Names.ServiceName }));
    }

    private DateTimeOffset? ReadMarker()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(MarkerPath(to)));
            return doc.RootElement.TryGetProperty("migratedAt", out var at) && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the new service has contacted the server since <paramref name="since"/>: read from its own database, read-only, so nothing in its home changes owner.</summary>
    private bool WaitForServer(ServiceLayout target, DateTimeOffset since)
    {
        for (var i = 0; i < healthWaitSeconds; i++)
        {
            if ((reachedServer ?? ReachedServer)(target, since)) return true;
            (sleep ?? Thread.Sleep)(TimeSpan.FromSeconds(1));
        }

        return (reachedServer ?? ReachedServer)(target, since);
    }

    private static bool ReachedServer(ServiceLayout target, DateTimeOffset since)
    {
        try
        {
            var path = Path.Combine(target.Home, "agent.db");
            if (!File.Exists(path)) return false;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT value FROM kv WHERE key = $k";
            command.Parameters.AddWithValue("$k", Relay.LastContactKey);
            return command.ExecuteScalar() is string text
                   && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at >= since;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RollBack()
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                undo[i].Undo();
                output.WriteLine($"Undone: {undo[i].What}.");
            }
            catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"Could not undo \"{undo[i].What}\": {e.Message}");
            }
        }

        undo.Clear();
    }
}
