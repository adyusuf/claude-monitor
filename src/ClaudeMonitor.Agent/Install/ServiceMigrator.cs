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
            Strings.T(Msg.MigPlanCopy, from.Names.ServiceName, from.Home, to.Home) + (os == OsKinds.Windows ? Strings.T(Msg.MigPlanNoTokens) : ""),
            Strings.T(Msg.MigPlanPolicy, Path.GetDirectoryName(from.ExecConfigPath), Path.GetDirectoryName(to.ExecConfigPath)),
            Strings.T(Msg.MigPlanCreate, to.Account, to.Names.ServiceName, to.Binary, to.Home),
        };
        if (os != OsKinds.Windows) plan.Add(Strings.T(Msg.MigPlanWait, healthWaitSeconds));
        plan.Add(Strings.T(Msg.MigPlanDisable, from.Names.ServiceName, KeepOld.TotalDays));
        plan.Add(Strings.T(Msg.MigPlanLater));
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

        output.WriteLine(Strings.T(dryRun ? Msg.MigDryRun : Msg.MigMigrating));
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
            output.WriteLine(Strings.T(Msg.MigFailed, e.Message));
            RollBack();
            output.WriteLine(Strings.T(Msg.MigPutBack));
            return 1;
        }

        undo.Clear();
        output.WriteLine(Strings.T(Msg.MigDone, target.Names.ServiceName, KeepOld.TotalDays));
        output.WriteLine(os == OsKinds.Windows ? Strings.T(Msg.MigLoginAgain, target.LoginCodePath) : Strings.T(Msg.MigToGoBack, KeepOld.TotalDays));
        return 0;
    }

    /// <summary>Goes back: stops and disables the new service, brings its data home, enables and starts the old one.</summary>
    public int GoBack()
    {
        if (Unsupported() is { } unsupported) return unsupported;
        if (!isAdmin) return NeedAdmin("service migrate --undo");
        if (!File.Exists(MarkerPath(to)) || !ServiceInstaller.IsInstalled(os, from, run) || !ServiceInstaller.IsInstalled(os, to, run))
        {
            output.WriteLine(Strings.T(Msg.MigNothingBack));
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
            output.WriteLine(Strings.T(Msg.MigBackFailed, e.Message));
            return 1;
        }

        output.WriteLine(Strings.T(Msg.MigBackDone, from.Names.ServiceName));
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
            output.WriteLine(Strings.T(Msg.MigNoFinished));
            return 1;
        }

        var age = time.GetUtcNow() - marker.Value;
        if (!now && age < KeepOld)
        {
            output.WriteLine(Strings.T(Msg.MigKept, KeepOld.TotalDays, (KeepOld - age).TotalDays));
            return 1;
        }

        try
        {
            RemoveOldFiles();
            File.Delete(MarkerPath(to));
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine(Strings.T(Msg.MigCleanFailed, e.Message));
            return 1;
        }

        output.WriteLine(Strings.T(Msg.MigCleaned));
        return 0;
    }

    private (int Code, string Text)? Refusal()
    {
        if (ServiceInstaller.IsInstalled(os, to, run)) return (0, Strings.T(Msg.MigAlreadyThere, to.Names.ServiceName));
        if (!ServiceInstaller.IsInstalled(os, from, run)) return (0, Strings.T(Msg.MigNoOldService, from.Names.ServiceName));
        var problem = os switch
        {
            OsKinds.Linux => SystemdUnit.Check(to),
            OsKinds.Windows => WindowsServiceSetup.Check(to),
            _ => null,
        };
        if (problem is not null) return (1, Strings.T(Msg.MigNotMigrated, problem));
        if (!File.Exists(Path.Combine(from.Home, "agent.json")) || IdentityOf(from) is not { Connected: true })
        {
            return (1, Strings.T(Msg.MigNotConnected, from.Home));
        }

        foreach (var folder in new[] { to.Home, to.BinaryDir, Path.GetDirectoryName(to.ExecConfigPath)! }.Distinct(StringComparer.Ordinal))
        {
            if (HasFiles(folder)) return (1, Strings.T(Msg.MigHasFiles, folder));
        }

        return null;
    }

    /// <summary>The exit code when this OS has no service, else null.</summary>
    private int? Unsupported()
    {
        if (os is OsKinds.Linux or OsKinds.MacOs or OsKinds.Windows) return null;
        output.WriteLine(Strings.T(Msg.SvcUnsupportedOs, os));
        return 2;
    }

    private int NeedAdmin(string verb)
    {
        var command = $"{Environment.ProcessPath ?? "cm-agent"} {verb}";
        output.WriteLine(Strings.T(os == OsKinds.Windows ? Msg.MigNeedAdminWindows : Msg.MigNeedRoot, command));
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
                output.WriteLine(Strings.T(Msg.Undone, undo[i].What));
            }
            catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(Strings.T(Msg.CouldNotUndo, undo[i].What, e.Message));
            }
        }

        undo.Clear();
    }
}
