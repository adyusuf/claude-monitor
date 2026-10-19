using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The steps of <c>install --service --run-account</c> (ADR-0010 §5): a second unprivileged account, a shared work folder and a sudoers drop-in for exactly that.</summary>
public sealed partial class ServiceInstaller
{
    private const string UserMod = "/usr/sbin/usermod";
    private const string GroupEdit = "/usr/sbin/dseditgroup";
    private const string Visudo = "/usr/sbin/visudo";
    private const string SudoersDir = "/etc/sudoers.d";

    /// <summary>Where the drop-in goes (a test points it elsewhere).</summary>
    public string SudoersFolder { get; init; } = SudoersDir;

    private string SudoersPath => Path.Combine(SudoersFolder, Layout().Names.SudoersFileName);

    private void EnsureRunAccount(ServiceLayout l, string account)
    {
        if (!RunAccount.IsValidName(account) || account == l.Account) throw new StepFailedException($"\"{account}\" cannot be the run account: it needs a plain name of its own.");
        var work = RunAccount.WorkFolder(l.Home);
        var accountLayout = l with { Account = account, Home = work };
        if (os == OsKinds.Linux)
        {
            if (run(Id, ["-u", account]) != 0)
            {
                Step($"create the {account} account", UserAdd, SystemdUnit.UserAddArgs(accountLayout));
                undo.Add(("run account created", () => Step("remove the run account", UserDel, [account])));
            }

            Step($"add {l.Account} to the group of {account}", UserMod, ["-aG", account, l.Account]);
        }
        else
        {
            EnsureMacAccount(accountLayout);
            Step($"add {l.Account} to the group of {account}", GroupEdit, ["-o", "edit", "-a", l.Account, "-t", "user", account]);
        }

        var existed = Directory.Exists(work);
        if (new DirectoryInfo(work).LinkTarget is not null) throw new StepFailedException($"{work} is a symbolic link; it is not used.");
        Directory.CreateDirectory(work);
        if (!existed) undo.Add(("run work folder created", () => Directory.Delete(work, recursive: false)));
        Step("hand the work folder to the service and the run account", Chown(), [$"{l.Account}:{account}", work]);
        // setgid: what is made inside belongs to the run account's group, so both accounts can always use and clean it.
        Chmod(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        WriteSudoers(l.Account, account);
    }

    /// <summary>The drop-in is checked with <c>visudo -cf</c> before it is moved into place, so a bad file never reaches sudo.</summary>
    private void WriteSudoers(string serviceAccount, string runAccount)
    {
        Directory.CreateDirectory(SudoersFolder);
        var temp = Path.Combine(SudoersFolder, "." + Layout().Names.SudoersFileName + ".tmp");
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.GroupRead;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(new System.Text.UTF8Encoding(false).GetBytes(RunAccount.SudoersText(serviceAccount, runAccount)));
        }

        try
        {
            Step("check the sudoers drop-in", Visudo, ["-cf", temp]);
            var existed = File.Exists(SudoersPath);
            File.Move(temp, SudoersPath, overwrite: true);
            if (!existed) undo.Add(("sudoers drop-in written", () => File.Delete(SudoersPath)));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Uninstalling the service removes the drop-in too: it is the part that lets one account act as another, and nothing should be left of it.</summary>
    private void RemoveRunAccountAccess()
    {
        if (os is OsKinds.Linux or OsKinds.MacOs && File.Exists(SudoersPath))
        {
            File.Delete(SudoersPath);
            output.WriteLine("Removed the sudoers drop-in that let the service act as the run account.");
        }
    }
}
