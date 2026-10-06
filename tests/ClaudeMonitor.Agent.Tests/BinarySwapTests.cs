using System.Text;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

public sealed class BinarySwapTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cm-swap-test-" + Guid.NewGuid().ToString("N"));
    private readonly string current;
    private readonly string staged;
    private readonly string previous;

    public BinarySwapTests()
    {
        Directory.CreateDirectory(dir);
        current = Path.Combine(dir, "cm-agent");
        staged = BinarySwap.Staged(current);
        previous = BinarySwap.Previous(current);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    public static TheoryData<SwapStyle> Styles => new() { SwapStyle.Atomic, SwapStyle.RenameAside };

    private static string Text(string path) => File.ReadAllText(path, Encoding.ASCII);

    private void Setup(string old = "OLD", string? next = "NEW")
    {
        File.WriteAllText(current, old);
        if (next is not null) File.WriteAllText(staged, next);
    }

    [Fact]
    public void The_paths_sit_beside_the_binary()
    {
        Assert.Equal(current + ".prev", previous);
        Assert.Equal(current + ".new", staged);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Install_puts_the_new_bytes_in_place_and_keeps_the_old_as_prev(SwapStyle style)
    {
        Setup();
        BinarySwap.Install(current, staged, style);
        Assert.Equal("NEW", Text(current));
        Assert.Equal("OLD", Text(previous));
        Assert.False(File.Exists(staged), "the staged file was moved, not copied");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Install_keeps_the_mode_of_the_new_file_and_of_the_old_one(SwapStyle style)
    {
        if (OperatingSystem.IsWindows()) return; // no unix mode there
        Setup();
        var oldMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var newMode = oldMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(current, oldMode);
        File.SetUnixFileMode(staged, newMode);
        BinarySwap.Install(current, staged, style);
        Assert.Equal(newMode, File.GetUnixFileMode(current));
        Assert.Equal(oldMode, File.GetUnixFileMode(previous));
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Install_replaces_a_previous_version_left_by_an_earlier_update(SwapStyle style)
    {
        Setup();
        File.WriteAllText(previous, "STALE");
        BinarySwap.Install(current, staged, style);
        Assert.Equal("OLD", Text(previous));
        Assert.Equal("NEW", Text(current));
        Assert.Empty(Directory.EnumerateFiles(dir, "cm-agent.old*"));
    }

    [Fact]
    public void A_failed_rename_aside_puts_the_old_binary_back_and_rethrows()
    {
        Setup(next: null); // there is no staged file to move in
        Assert.ThrowsAny<IOException>(() => BinarySwap.Install(current, staged, SwapStyle.RenameAside));
        Assert.Equal("OLD", Text(current));
        Assert.False(File.Exists(previous));
    }

    [Fact]
    public void A_failed_atomic_install_leaves_the_binary_as_it_was()
    {
        Setup(next: null);
        Assert.ThrowsAny<IOException>(() => BinarySwap.Install(current, staged, SwapStyle.Atomic));
        Assert.Equal("OLD", Text(current));
    }

    [Fact]
    public void Restore_atomic_puts_the_previous_bytes_back()
    {
        Setup();
        BinarySwap.Install(current, staged, SwapStyle.Atomic);
        BinarySwap.Restore(current, SwapStyle.Atomic);
        Assert.Equal("OLD", Text(current));
        Assert.False(File.Exists(previous));
    }

    [Fact]
    public void Restore_rename_aside_puts_the_previous_back_and_sets_the_bad_one_aside()
    {
        Setup();
        BinarySwap.Install(current, staged, SwapStyle.RenameAside);
        BinarySwap.Restore(current, SwapStyle.RenameAside);
        Assert.Equal("OLD", Text(current));
        Assert.Equal("NEW", Text(current + ".bad"));
        Assert.False(File.Exists(previous));
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void Restore_without_a_previous_version_throws_and_changes_nothing(SwapStyle style)
    {
        Setup(next: null);
        Assert.Throws<FileNotFoundException>(() => BinarySwap.Restore(current, style));
        Assert.Equal("OLD", Text(current));
        Assert.False(File.Exists(current + ".bad"));
    }

    [Fact]
    public void Clean_leftovers_deletes_what_an_update_left_but_never_prev_the_binary_or_other_files()
    {
        Setup("BINARY", "STAGED");
        File.WriteAllText(previous, "PREV");
        File.WriteAllText(current + ".bad", "x");
        File.WriteAllText(current + ".old1a2b3c4d", "x");
        File.WriteAllText(current + ".old", "x");
        File.WriteAllText(current + ".other", "keep");
        File.WriteAllText(Path.Combine(dir, "unrelated.new"), "keep");
        File.WriteAllText(Path.Combine(dir, "cm-agent-extra.bad"), "keep");

        BinarySwap.CleanLeftovers(current);

        Assert.Equal("BINARY", Text(current));
        Assert.Equal("PREV", Text(previous));
        Assert.Equal("keep", Text(current + ".other"));
        Assert.Equal("keep", Text(Path.Combine(dir, "unrelated.new")));
        Assert.Equal("keep", Text(Path.Combine(dir, "cm-agent-extra.bad")));
        Assert.False(File.Exists(staged));
        Assert.False(File.Exists(current + ".bad"));
        Assert.False(File.Exists(current + ".old1a2b3c4d"));
        Assert.False(File.Exists(current + ".old"));
    }

    [Fact]
    public void Clean_leftovers_in_a_folder_that_does_not_exist_is_a_no_op()
    {
        BinarySwap.CleanLeftovers(Path.Combine(dir, "missing", "cm-agent"));
    }

    [Fact]
    public void The_native_style_is_rename_aside_on_windows_and_atomic_elsewhere()
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(SwapStyle.RenameAside, BinarySwap.Native);
        else Assert.Equal(SwapStyle.Atomic, BinarySwap.Native);
    }
}
