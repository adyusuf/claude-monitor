using System.Diagnostics;
using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ProtectedFileStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cm-cred-" + Guid.NewGuid().ToString("N"));
    private readonly ProtectedFileStore store;

    public ProtectedFileStoreTests() => store = new ProtectedFileStore(dir);

    public void Dispose()
    {
        if (!Directory.Exists(dir)) return;
        foreach (var e in Directory.EnumerateFileSystemEntries(dir).Where(e => new FileInfo(e).LinkTarget is not null).ToList()) File.Delete(e);
        Directory.Delete(dir, recursive: true);
    }

    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void A_secret_round_trips_through_a_0600_file_in_a_0700_home_and_no_temp_file_is_left()
    {
        if (OperatingSystem.IsWindows()) return;
        store.Write("access-token", "s3cret-value");

        Assert.Equal("s3cret-value", store.Read("access-token"));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(dir, "cred-access-token")));
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Null(store.Read("refresh-token"));
    }

    [Fact]
    public void A_second_write_replaces_the_first_and_keeps_the_mode_and_delete_removes_it()
    {
        if (OperatingSystem.IsWindows()) return;
        store.Write("t", "one");
        store.Write("t", "two");
        Assert.Equal("two", store.Read("t"));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(dir, "cred-t")));

        store.Delete("t");
        Assert.Null(store.Read("t"));
        store.Delete("t"); // already gone: no error
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherExecute)]
    public void A_file_open_to_group_or_others_is_refused_not_read(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows()) return;
        store.Write("t", "secret");
        var path = Path.Combine(dir, "cred-t");
        File.SetUnixFileMode(path, Private | extra);
        Assert.Null(store.Read("t"));

        File.SetUnixFileMode(path, Private);
        Assert.Equal("secret", store.Read("t")); // the same file, once its mode is right again
    }

    [Fact]
    public void A_link_in_place_of_the_file_is_refused_even_when_it_points_at_a_good_file()
    {
        if (OperatingSystem.IsWindows()) return;
        store.Write("real", "secret");
        File.CreateSymbolicLink(Path.Combine(dir, "cred-linked"), Path.Combine(dir, "cred-real"));
        Assert.Null(store.Read("linked"));
        Assert.Equal("secret", store.Read("real"));
    }

    [Fact]
    public void A_home_that_is_a_link_or_has_a_wrong_mode_is_handled_before_anything_is_written()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Directory.CreateDirectory(dir + "-target").FullName;
        try
        {
            Directory.CreateSymbolicLink(dir, target);
            Assert.Throws<InvalidOperationException>(() => store.Write("t", "secret"));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            File.Delete(dir);
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        store.Write("t", "secret");
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(dir)); // a folder we own is brought back to 0700
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void An_account_name_that_could_leave_the_home_is_never_read_written_or_deleted(string account)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => store.Write(account, "x"));
        Assert.Null(store.Read(account));
        store.Delete(account);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "cred-escape")));
    }

    [Fact]
    public void The_owner_read_from_the_file_system_is_this_account_and_the_system_root_is_root()
    {
        if (OperatingSystem.IsWindows()) return;
        store.Write("t", "x");
        var uid = uint.Parse(IdOutput(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(uid, UnixFiles.Euid());
        Assert.Equal(uid, UnixFiles.OwnerUid(Path.Combine(dir, "cred-t")));
        Assert.Equal(0u, UnixFiles.OwnerUid("/usr/bin/true"));
        Assert.Null(UnixFiles.OwnerUid(Path.Combine(dir, "does-not-exist")));
    }

    private static string IdOutput()
    {
        var info = new ProcessStartInfo("/usr/bin/id", "-u") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }

    [Fact]
    public void A_service_agent_on_mac_or_linux_keeps_its_tokens_in_a_protected_file_and_never_in_a_keychain()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new AgentConfig { Home = dir, ServiceMode = true };
        Assert.IsType<ProtectedFileStore>(Credentials.For(service));
        Assert.IsType<FileCredentialStore>(Credentials.For(service with { CredentialStore = "file" }));
        if (OperatingSystem.IsLinux()) Assert.IsType<ProtectedFileStore>(Credentials.For(service with { ServiceMode = false }));
    }
}
