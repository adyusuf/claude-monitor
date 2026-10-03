using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>Where the agent keeps its tokens: the OS credential store, never a plain file (ADR-0002).</summary>
public interface ICredentialStore
{
    string? Read(string account);

    void Write(string account, string secret);

    void Delete(string account);
}

public static class Credentials
{
    public const string Access = "access-token";
    public const string Refresh = "refresh-token";

    public static ICredentialStore For(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.CredentialStore == "file") return new FileCredentialStore(config.Home);
        if (OperatingSystem.IsMacOS()) return new MacKeychain();
        if (OperatingSystem.IsWindows()) return new WindowsCredentialManager();
        throw new PlatformNotSupportedException("the agent runs on macOS and Windows");
    }
}

/// <summary>Tests only (CM_CREDENTIALS=file): one file per account, readable by the user only.</summary>
public sealed class FileCredentialStore(string home) : ICredentialStore
{
    private string PathOf(string account) => Path.Combine(home, "cred-" + account);

    public string? Read(string account) => File.Exists(PathOf(account)) ? File.ReadAllText(PathOf(account)) : null;

    public void Write(string account, string secret)
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(PathOf(account), secret);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(PathOf(account), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Delete(string account) => File.Delete(PathOf(account));
}

/// <summary>
/// The login keychain through /usr/bin/security. Secrets are written through its interactive mode on stdin, so they
/// never appear in a process's arguments (which other users can list).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychain(string tool = MacKeychain.SecurityTool, string service = AgentConfig.CredentialService) : ICredentialStore
{
    public const string SecurityTool = "/usr/bin/security";

    public string? Read(string account)
    {
        var (code, output) = Run(["find-generic-password", "-s", service, "-a", account, "-w"], null);
        return code == 0 ? output.TrimEnd('\n') : null;
    }

    public void Write(string account, string secret)
    {
        if (secret.Contains('"', StringComparison.Ordinal) || secret.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("a token must not contain quotes or newlines", nameof(secret));
        }

        var line = $"add-generic-password -U -s {service} -a {account} -w \"{secret}\"\n";
        var (code, output) = Run(["-i"], line);
        if (code != 0) throw new InvalidOperationException("the keychain refused the token: " + output.Trim());
    }

    public void Delete(string account) => Run(["delete-generic-password", "-s", service, "-a", account], null);

    private (int, string) Run(string[] args, string? stdin)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

/// <summary>Windows Credential Manager (generic credentials of the current user) through advapi32.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialManager : ICredentialStore
{
    private const int Generic = 1;
    private const int LocalMachinePersist = 2;

    private static string Target(string account) => $"{AgentConfig.CredentialService}/{account}";

    public string? Read(string account)
    {
        if (!CredRead(Target(account), Generic, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return cred.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(string account, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, handle, blob.Length);
            var cred = new Credential
            {
                Type = Generic,
                TargetName = Target(account),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle,
                Persist = LocalMachinePersist,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0)) throw new InvalidOperationException($"CredWrite failed ({Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    public void Delete(string account) => CredDelete(Target(account), Generic, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
