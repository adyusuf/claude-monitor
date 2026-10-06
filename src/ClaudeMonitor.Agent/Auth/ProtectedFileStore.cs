using System.Runtime.Versioning;
using System.Text;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// Credentials of Linux and of macOS/Linux service accounts (ADR-0005): one 0600 file per account in the home, written
/// through a temp file and an atomic rename. A read refuses (null) a file that is a link, is open to group or other, or is
/// not owned by this account; it never throws.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ProtectedFileStore(string home) : ICredentialStore
{
    private const string FilePrefix = "cred-";
    private const string TempSuffix = ".tmp";
    private const UnixFileMode FileCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode HomeMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UTF8Encoding Utf8 = new(false);

    public string? Read(string account)
    {
        if (!IsValidAccount(account)) return null;
        var path = PathOf(account);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return null;
            if ((File.GetUnixFileMode(path) & UnixFiles.GroupOrOther) != 0) return null;
            if (UnixFiles.OwnerUid(path) is not { } owner || UnixFiles.Euid() != owner) return null;
            return File.ReadAllText(path, Utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // unreadable is the same as absent: the caller asks the user to log in again
            return null;
        }
    }

    public void Write(string account, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!IsValidAccount(account)) throw new ArgumentException("not a valid credential name", nameof(account));
        EnsureHome();

        var target = PathOf(account);
        var temp = target + TempSuffix;
        File.Delete(temp);
        var moved = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = FileCreateMode,
                Options = FileOptions.WriteThrough,
            };
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(Utf8.GetBytes(secret));
                stream.Flush(true);
            }

            File.Move(temp, target, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved) File.Delete(temp);
        }
    }

    public void Delete(string account)
    {
        if (!IsValidAccount(account)) return;
        File.Delete(PathOf(account));
    }

    private string PathOf(string account) => Path.Combine(home, FilePrefix + account);

    /// <summary>An account name becomes part of a file name, so it must not climb out of the home.</summary>
    private static bool IsValidAccount(string account) =>
        !string.IsNullOrEmpty(account) && account.IndexOfAny(['/', '\\', '\0']) < 0 && account != "." && account != "..";

    /// <summary>The home must be a real folder of this account, 0700; a mode that is wrong on a folder we own is fixed.</summary>
    private void EnsureHome()
    {
        if (new DirectoryInfo(home).LinkTarget is not null) throw new InvalidOperationException("the credential folder is a link");
        if (!Directory.Exists(home)) Directory.CreateDirectory(home, HomeMode);
        if (UnixFiles.OwnerUid(home) is not { } owner || UnixFiles.Euid() != owner)
        {
            throw new InvalidOperationException("the credential folder is not owned by this account");
        }

        if (File.GetUnixFileMode(home) != HomeMode) File.SetUnixFileMode(home, HomeMode);
    }
}
