using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Finding the `claude` and telling an npm or native CLI install (updated by `claude update`) from everything else (left alone).</summary>
public sealed class ClaudeLocatorTests : IDisposable
{
    private const string NpmLayout = "lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe";

    private readonly SessionsDir tree = new(); // only used for its throw-away root folder
    private readonly TempHome home = new();

    public void Dispose()
    {
        home.Dispose();
        tree.Dispose();
    }

    private string Make(string relative)
    {
        var path = Path.Combine(tree.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private AgentConfig WithPath(params string[] dirs) => home.Config with { ClaudeBinary = null, PathVariable = string.Join(Path.PathSeparator, dirs) };

    [Theory]
    [InlineData("/home/u/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe", ClaudeKind.Npm)]
    [InlineData("/usr/local/lib/node_modules/@anthropic-ai/claude-code/cli.js", ClaudeKind.Npm)]
    [InlineData(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", ClaudeKind.Npm)]
    [InlineData("/Home/U/Lib/Node_Modules/@Anthropic-AI/Claude-Code/bin/claude", ClaudeKind.Npm)]
    [InlineData("/home/u/.local/share/claude/versions/2.1.0/claude", ClaudeKind.Native)]
    [InlineData("/home/u/.claude/local/claude", ClaudeKind.Native)]
    [InlineData(@"C:\Users\u\.claude\local\claude.exe", ClaudeKind.Native)]
    public void A_recognised_cli_install_is_npm_or_native(string path, ClaudeKind expected) => Assert.Equal(expected, ClaudeLocator.Classify(path));

    [Theory]
    [InlineData("/Applications/Claude.app/Contents/Resources/app/node_modules/@anthropic-ai/claude-code/bin/claude")]
    [InlineData("/Applications/Claude.app/Contents/MacOS/claude")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0_x64\app\node_modules\@anthropic-ai\claude-code\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0_x64\.claude\local\claude.exe")]
    [InlineData("/opt/homebrew/Caskroom/claude-code/2.1.0/node_modules/@anthropic-ai/claude-code/bin/claude")]
    [InlineData("/opt/homebrew/Caskroom/claude-code/2.1.0/claude")]
    public void The_desktop_app_and_package_manager_copies_are_unsupported_even_when_the_path_also_looks_like_npm_or_native(string path) =>
        Assert.Equal(ClaudeKind.Unsupported, ClaudeLocator.Classify(path));

    [Theory]
    [InlineData("/usr/local/bin/claude")]
    [InlineData("/home/u/bin/claude")]
    [InlineData("/home/u/node_modules/other-package/claude")]
    [InlineData("/home/u/node_modules/@anthropic-ai/claude-code-extras/claude")]
    [InlineData("/home/u/.local/share/other/claude")]
    [InlineData("claude")]
    [InlineData("")]
    public void Whatever_is_not_positively_recognised_is_unsupported(string path) => Assert.Equal(ClaudeKind.Unsupported, ClaudeLocator.Classify(path));

    [Fact]
    public void A_cmd_shim_beside_the_npm_folder_is_npm_and_one_without_it_is_not()
    {
        var shim = Make("npmprefix/claude.cmd");
        Assert.Equal(ClaudeKind.Unsupported, ClaudeLocator.Classify(shim));
        Directory.CreateDirectory(Path.Combine(tree.Root, "npmprefix", "node_modules", "@anthropic-ai", "claude-code"));
        Assert.Equal(ClaudeKind.Npm, ClaudeLocator.Classify(shim));
    }

    [Fact]
    public void An_explicit_binary_is_classified_by_where_it_really_is()
    {
        var path = Make(NpmLayout);
        var install = ClaudeLocator.Locate(home.Config with { ClaudeBinary = path });
        Assert.Equal(ClaudeKind.Npm, install.Kind);
        Assert.True(install.Updatable);
        Assert.Equal(path, install.Path);
        Assert.Contains("npm", install.Detail, StringComparison.Ordinal);

        var native = ClaudeLocator.Locate(home.Config with { ClaudeBinary = Make(ClaudeKit.Native) });
        Assert.Equal(ClaudeKind.Native, native.Kind);
        Assert.True(native.Updatable);
    }

    [Fact]
    public void An_unrecognised_or_desktop_install_is_found_but_not_updatable_and_says_why()
    {
        foreach (var layout in new[] { ClaudeKit.Desktop, "usr/local/bin/claude" })
        {
            var install = ClaudeLocator.Locate(home.Config with { ClaudeBinary = Make(layout) });
            Assert.Equal(ClaudeKind.Unsupported, install.Kind);
            Assert.False(install.Updatable);
            Assert.NotNull(install.Path);
            Assert.Contains("does not update", install.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_explicit_binary_that_is_missing_is_not_found_even_when_the_path_has_a_claude()
    {
        var onPath = Make("bin/claude");
        var install = ClaudeLocator.Locate(home.Config with { ClaudeBinary = Path.Combine(tree.Root, "nowhere", "claude"), PathVariable = Path.GetDirectoryName(onPath)! });
        Assert.Equal(ClaudeKind.NotFound, install.Kind);
        Assert.False(install.Updatable);
        Assert.Null(install.Path);
    }

    [Fact]
    public void No_claude_on_the_path_is_not_found()
    {
        Make("bin/other-tool");
        Directory.CreateDirectory(Path.Combine(tree.Root, "empty"));
        foreach (var path in new[] { "", Path.Combine(tree.Root, "bin"), Path.Combine(tree.Root, "empty"), Path.Combine(tree.Root, "missing") })
        {
            Assert.Equal(ClaudeKind.NotFound, ClaudeLocator.Locate(WithPath(path)).Kind);
        }
    }

    [Fact]
    public void The_first_claude_on_the_path_wins()
    {
        var npm = Make("a/node_modules/@anthropic-ai/claude-code/bin/claude");
        var native = Make("b/.claude/local/claude");
        var first = ClaudeLocator.Locate(WithPath(Path.GetDirectoryName(npm)!, Path.GetDirectoryName(native)!));
        Assert.Equal((ClaudeKind.Npm, npm), (first.Kind, first.Path));
        var swapped = ClaudeLocator.Locate(WithPath(Path.GetDirectoryName(native)!, Path.GetDirectoryName(npm)!));
        Assert.Equal((ClaudeKind.Native, native), (swapped.Kind, swapped.Path));
    }

    [Fact]
    public void A_claude_on_the_path_is_found_in_a_later_entry_when_earlier_ones_have_none()
    {
        var real = Make("later/claude");
        var install = ClaudeLocator.Locate(WithPath(Path.Combine(tree.Root, "missing"), Path.Combine(tree.Root, "empty"), Path.GetDirectoryName(real)!));
        Assert.Equal(real, install.Path);
    }

    [Fact]
    public void A_symlink_named_claude_on_the_path_is_judged_by_its_target()
    {
        if (OperatingSystem.IsWindows()) return; // symbolic links need a privilege there; the Windows install layouts are not testable here
        var target = Make(NpmLayout);
        var link = Path.Combine(tree.Root, "bin", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        var install = ClaudeLocator.Locate(WithPath(Path.GetDirectoryName(link)!));
        Assert.Equal(ClaudeKind.Npm, install.Kind);
        Assert.Equal(target, install.Path);
    }

    [Fact]
    public void A_symlink_chain_is_followed_to_the_end()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Make(ClaudeKit.Native);
        var middle = Path.Combine(tree.Root, "mid", "claude");
        var link = Path.Combine(tree.Root, "bin", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(middle)!);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(middle, target);
        File.CreateSymbolicLink(link, middle);

        var install = ClaudeLocator.Locate(WithPath(Path.GetDirectoryName(link)!));
        Assert.Equal(ClaudeKind.Native, install.Kind);
        Assert.Equal(target, install.Path);
    }

    [Fact]
    public void A_symlink_into_the_desktop_app_is_unsupported_and_a_link_that_only_looks_like_npm_is_too()
    {
        if (OperatingSystem.IsWindows()) return;
        var app = Make(ClaudeKit.Desktop);
        var appLink = Path.Combine(tree.Root, "bin", "claude");
        var elsewhere = Make("elsewhere/claude");
        var npmLookingLink = Path.Combine(tree.Root, "npm-looking", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(appLink)!);
        Directory.CreateDirectory(Path.GetDirectoryName(npmLookingLink)!);
        File.CreateSymbolicLink(appLink, app);
        File.CreateSymbolicLink(npmLookingLink, elsewhere);

        Assert.Equal(ClaudeKind.Unsupported, ClaudeLocator.Locate(WithPath(Path.GetDirectoryName(appLink)!)).Kind);
        Assert.Equal(ClaudeKind.Unsupported, ClaudeLocator.Locate(home.Config with { ClaudeBinary = npmLookingLink }).Kind);
    }
}
