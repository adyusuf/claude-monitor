using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The tool inventory (ADR-0008 §3a′): the probe through fake seams, the candidate lists, the pure helpers and one real run on this machine.</summary>
public sealed class ToolInventoryTests
{
    private const string Profile = "/Users/someone";

    /// <summary>The probe's seams as fakes: which paths exist, which are trusted or self-owned, and every call made.</summary>
    private sealed class Seams
    {
        public HashSet<string> Existing { get; } = [];
        public HashSet<string> Trusted { get; } = [];
        public HashSet<string> SelfOwned { get; } = [];
        public Dictionary<string, string> Real { get; } = [];
        public Dictionary<string, string[]> Folders { get; } = [];
        public List<string> ExistsCalls { get; } = [];
        public List<(string Name, string Path)> VersionCalls { get; } = [];
        public string? VersionAnswer { get; set; } = "tool 1.2.3";

        public IReadOnlyList<ToolInfo> Probe(params ToolCandidate[] candidates) => ToolInventory.Probe(candidates, Profile,
            p => { ExistsCalls.Add(p); return Existing.Contains(p); },
            p => Real.TryGetValue(p, out var real) ? real : p,
            Trusted.Contains, SelfOwned.Contains,
            (name, path) => { VersionCalls.Add((name, path)); return VersionAnswer; },
            parent => Folders.TryGetValue(parent, out var f) ? f : []);
    }

    private static ToolCandidate C(string name, string path) => new(name, path);

    [Fact]
    public void The_first_existing_candidate_of_a_name_wins_and_the_later_ones_are_never_looked_at()
    {
        var s = new Seams();
        s.Existing.UnionWith(["/first/git", "/second/git"]);
        s.Trusted.Add("/first/git");

        var tools = s.Probe(C("git", "/missing/git"), C("git", "/first/git"), C("git", "/second/git"), C("node", "/first/node"));

        Assert.Equal(new ToolInfo("git", "/first/git", "tool 1.2.3", true), Assert.Single(tools));
        Assert.DoesNotContain("/second/git", s.ExistsCalls); // the name was settled: no second probe
        Assert.Equal([("git", "/first/git")], s.VersionCalls);
    }

    [Fact]
    public void A_star_folder_expands_through_the_subfolder_seam_and_the_newest_folder_is_tried_first()
    {
        var s = new Seams();
        s.Folders["/nvm/node"] = ["/nvm/node/v18.2.0", "/nvm/node/v20.1.0", "/nvm/node/v19.0.0", "/nvm/node/other"];
        s.Existing.UnionWith(["/nvm/node/v18.2.0/bin/node", "/nvm/node/v19.0.0/bin/node", "/nvm/node/v20.1.0/bin/node", "/nvm/node/other/bin/node"]);
        s.Trusted.UnionWith(s.Existing);

        var tool = Assert.Single(s.Probe(C("node", "/nvm/node/v*/bin/node")));

        Assert.Equal("/nvm/node/v20.1.0/bin/node", tool.Path);
        Assert.DoesNotContain("/nvm/node/other/bin/node", s.ExistsCalls); // the prefix filter drops a folder that is not a version
    }

    [Fact]
    public void When_the_newest_folder_has_no_such_file_the_next_newest_that_has_it_is_used()
    {
        var s = new Seams();
        s.Folders["/nvm/node"] = ["/nvm/node/v18.2.0", "/nvm/node/v20.1.0"];
        s.Existing.Add("/nvm/node/v18.2.0/bin/node");
        s.Trusted.Add("/nvm/node/v18.2.0/bin/node");

        Assert.Equal("/nvm/node/v18.2.0/bin/node", Assert.Single(s.Probe(C("node", "/nvm/node/v*/bin/node"))).Path);
        Assert.Equal(["/nvm/node/v20.1.0/bin/node", "/nvm/node/v18.2.0/bin/node"], s.ExistsCalls);
    }

    [Fact]
    public void A_star_with_no_folder_to_expand_finds_nothing()
    {
        var s = new Seams();
        Assert.Empty(s.Probe(C("node", "/nowhere/v*/bin/node")));
        Assert.Empty(s.VersionCalls);
    }

    [Fact]
    public void The_version_is_not_run_for_an_untrusted_binary_another_account_owns_but_the_tool_is_still_listed()
    {
        var s = new Seams();
        s.Existing.Add("/opt/foreign/python3");

        var tool = Assert.Single(s.Probe(C("python3", "/opt/foreign/python3")));

        Assert.Empty(s.VersionCalls); // running it would hand its owner this agent's credentials
        Assert.Equal(new ToolInfo("python3", "/opt/foreign/python3", null, false), tool);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void The_version_is_run_only_for_a_trusted_or_self_owned_binary(bool trusted, bool selfOwned, bool runs)
    {
        var s = new Seams();
        s.Existing.Add("/opt/x/sh");
        if (trusted) s.Trusted.Add("/opt/x/sh");
        if (selfOwned) s.SelfOwned.Add("/opt/x/sh");

        var tool = Assert.Single(s.Probe(C("sh", "/opt/x/sh")));

        Assert.Equal(runs, s.VersionCalls.Count == 1);
        Assert.Equal(trusted, tool.Trusted); // self-owned makes it runnable for --version, never trusted
        Assert.Equal(runs ? "tool 1.2.3" : null, tool.Version);
    }

    [Fact]
    public void An_empty_or_missing_version_is_null_and_the_version_runs_on_the_resolved_real_path()
    {
        var s = new Seams { VersionAnswer = "" };
        s.Existing.Add("/usr/bin/git");
        s.Real["/usr/bin/git"] = "/opt/real/git";
        s.Trusted.Add("/opt/real/git");

        var tool = Assert.Single(s.Probe(C("git", "/usr/bin/git")));

        Assert.Equal(new ToolInfo("git", "/opt/real/git", null, true), tool);
        Assert.Equal([("git", "/opt/real/git")], s.VersionCalls);
    }

    [Fact]
    public void A_candidate_that_cannot_be_resolved_is_skipped_and_the_next_one_of_the_name_is_tried()
    {
        var s = new Seams();
        s.Existing.UnionWith(["/broken/link/git", "/ok/git"]);
        s.Real["/broken/link/git"] = "";
        s.Trusted.Add("/ok/git");

        Assert.Equal("/ok/git", Assert.Single(s.Probe(C("git", "/broken/link/git"), C("git", "/ok/git"))).Path);
    }

    [Fact]
    public void A_path_under_the_profile_is_reported_home_relative()
    {
        var s = new Seams();
        s.Existing.Add($"{Profile}/.volta/bin/node");
        s.Trusted.Add($"{Profile}/.volta/bin/node");

        Assert.Equal("~/.volta/bin/node", Assert.Single(s.Probe(C("node", $"{Profile}/.volta/bin/node"))).Path);
    }

    [Fact]
    public void Only_the_first_sixty_four_candidates_are_probed()
    {
        var s = new Seams();
        var all = Enumerable.Range(0, ToolInventory.CandidatesMax + 5).Select(i => new ToolCandidate($"tool{i}", $"/bin/tool{i}")).ToArray();
        s.Existing.UnionWith(all.Select(c => c.Path));
        s.Trusted.UnionWith(s.Existing);

        Assert.Equal(ToolInventory.CandidatesMax, s.Probe(all).Count);
    }

    [Fact]
    public void Interpreters_keeps_only_the_trusted_shells_and_pythons()
    {
        ToolInfo T(string name, bool trusted) => new(name, "/x/" + name, "1", trusted);
        var tools = new[]
        {
            T("sh", true), T("bash", false), T("python3", true), T("pwsh", true), T("powershell", true), T("git", true), T("node", true),
        };

        Assert.Equal(["sh", "python3", "pwsh", "powershell"], ToolInventory.Interpreters(tools).Select(t => t.Name));
        Assert.Empty(ToolInventory.Interpreters([T("bash", false), T("git", true)]));
    }

    [Theory]
    [InlineData("/Users/someone/x/y", "/Users/someone", "~/x/y")]
    [InlineData("/Users/someone/x/y", "/Users/someone/", "~/x/y")]
    [InlineData(@"C:\Users\someone\x\y", @"C:\Users\someone", "~/x/y")]
    [InlineData("/usr/bin/git", "/Users/someone", "/usr/bin/git")]
    [InlineData("/Users/someone", "/Users/someone", "/Users/someone")]
    [InlineData("/Users/someone2/x", "/Users/someone", "/Users/someone2/x")]
    [InlineData("/Users/someone/x", "", "/Users/someone/x")]
    public void HomeRelative_turns_only_a_path_inside_the_profile_into_a_tilde_path(string path, string profile, string expected) =>
        Assert.Equal(expected, ToolInventory.HomeRelative(path, profile));

    [Fact]
    public void FirstLine_takes_the_first_non_empty_line_without_control_characters()
    {
        Assert.Equal("git version 2.40.0", ToolInventory.FirstLine("\r\n  \n git version 2.40.0 \r\nsecond line"));
        Assert.Equal("abc", ToolInventory.FirstLine("a\u0001b\u0007c"));
        Assert.Equal("ab", ToolInventory.FirstLine("a\tb"));
        Assert.Null(ToolInventory.FirstLine(null));
        Assert.Null(ToolInventory.FirstLine(" \n\r\n\t"));
    }

    [Fact]
    public void FirstLine_is_capped_at_one_hundred_characters()
    {
        Assert.Equal(new string('v', 100), ToolInventory.FirstLine(new string('v', 250) + "\nnext"));
        Assert.Equal(new string('v', 100), ToolInventory.FirstLine(new string('v', 100)));
    }

    [Fact]
    public void Windows_PowerShell_is_asked_for_its_version_with_a_command_and_every_other_tool_with_dash_dash_version()
    {
        Assert.Equal(["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"], ToolInventory.VersionArgs(ToolNames.WindowsPowerShell));
        foreach (var name in ToolNames.All.Where(n => n != ToolNames.WindowsPowerShell)) Assert.Equal(["--version"], ToolInventory.VersionArgs(name));
    }

    [Fact]
    public void The_macOS_candidates_expand_the_profile_and_list_the_system_shell_and_homebrew()
    {
        var list = ToolInventory.Candidates(OsKinds.MacOs, "/Users/someone");

        Assert.Contains(new ToolCandidate("sh", "/bin/sh"), list);
        Assert.Contains(new ToolCandidate("git", "/opt/homebrew/bin/git"), list);
        Assert.Contains(new ToolCandidate("claude", Path.Combine("/Users/someone", ".local/bin/claude")), list);
        Assert.DoesNotContain(list, c => c.Path.StartsWith('~'));
        Assert.All(list, c => Assert.Contains(c.Name, ToolNames.All));
        Assert.True(list.Count <= ToolInventory.CandidatesMax, "a candidate past the limit would be silently dropped");
    }

    [Fact]
    public void The_Windows_candidates_include_PowerShell_seven_and_Windows_PowerShell()
    {
        var list = ToolInventory.Candidates(OsKinds.Windows, @"C:\Users\someone");

        Assert.Contains(list, c => c.Name == ToolNames.PowerShell && c.Path.EndsWith("pwsh.exe", StringComparison.Ordinal));
        Assert.Contains(list, c => c.Name == ToolNames.WindowsPowerShell && c.Path.EndsWith("powershell.exe", StringComparison.Ordinal));
        Assert.True(list.Count <= ToolInventory.CandidatesMax);
    }

    [Fact]
    public void Live_finds_the_system_shell_as_a_trusted_tool_with_a_version_on_this_machine()
    {
        if (!OperatingSystem.IsMacOS()) return; // the real run is pinned to macOS: /bin/sh there is root-owned and answers --version
        using var home = new TempHome(c => c with { ToolVersionTimeout = TimeSpan.FromSeconds(3) });

        var sh = Assert.Single(ToolInventory.Live(home.Config, OsKinds.MacOs), t => t.Name == ToolNames.Sh);

        Assert.Equal("/bin/sh", sh.Path);
        Assert.True(sh.Trusted);
        Assert.False(string.IsNullOrWhiteSpace(sh.Version)); // macOS /bin/sh --version prints "GNU bash, version 3.2..." and exits 0
    }
}
