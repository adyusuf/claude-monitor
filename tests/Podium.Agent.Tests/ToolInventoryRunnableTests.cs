using Podium.Agent.Exec;
using Podium.Agent.Mcp;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The rule that decides whether a binary is started for its version (ADR-0008 §3a′), the short version Claude is shown, and the role line
/// of aipod_machines (the qa pass of 16/10/2026 asked for all three).
/// </summary>
public sealed class ToolInventoryRunnableTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void An_untrusted_foreign_owned_binary_is_never_started_and_has_no_version()
    {
        var started = 0;
        var version = ToolInventory.VersionIfRunnable(ToolNames.Claude, "/opt/homebrew/bin/claude", Timeout, OsKinds.MacOs,
            trusted: _ => false, selfOwned: _ => false, read: (_, _, _) => { started++; return "2.1.300"; });
        Assert.Null(version);
        Assert.Equal(0, started);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_trusted_or_self_owned_binary_is_started_once_and_its_first_line_is_the_version(bool trusted, bool selfOwned)
    {
        var started = new List<(string Name, string Real)>();
        var version = ToolInventory.VersionIfRunnable(ToolNames.Node, "/usr/local/bin/node", Timeout, OsKinds.Linux,
            trusted: _ => trusted, selfOwned: _ => selfOwned, read: (name, real, _) => { started.Add((name, real)); return "v22.1.0"; });
        Assert.Equal("v22.1.0", version);
        Assert.Equal([(ToolNames.Node, "/usr/local/bin/node")], started);
    }

    [Theory]
    [InlineData("Docker version 29.8.2, build 7fc2dff", "29.8.2")]
    [InlineData("git version 2.39.5 (Apple Git-154)", "2.39.5")]
    [InlineData("openjdk 21.0.1 2023-10-17", "21.0.1")]
    [InlineData("2.1.300 (Claude Code)", "2.1.300")]
    [InlineData("GNU bash, version 3.2.57(1)-release (arm64-apple-darwin23)", "3.2.57")]
    [InlineData("v22.1.0", "22.1.0")]
    [InlineData("PowerShell 7.4.1", "7.4.1")]
    [InlineData("just-words here", "just-words")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_short_version_is_the_first_dotted_number_else_the_first_word(string? line, string? expected) =>
        Assert.Equal(expected, ToolInventory.ShortVersion(line));

    private static MachineView View(string? setEnv = null, string? setFn = null, string? claimEnv = null, string? claimFn = null,
        IReadOnlyList<ToolInfo>? tools = null, IReadOnlyList<ToolInfo>? interpreters = null) =>
        new(Guid.NewGuid(), "web01", OsKinds.Linux, "ops", ExecLevels.Argv, false, true, null, null, 0,
            setEnv, setFn, claimEnv, claimFn, MachineRoles.EffectiveEnvironment(setEnv, claimEnv), MachineRoles.EffectiveFunction(setFn, claimFn),
            Tools: tools, Interpreters: interpreters);

    [Fact]
    public void A_machine_nobody_classified_and_that_claims_nothing_is_just_not_set() => Assert.Equal("not set", MachineTools.Role(View()));

    [Fact]
    public void A_role_an_admin_set_says_so_and_a_matching_claim_is_shown_apart() =>
        Assert.Equal("production/application (admin; machine says production/not set)",
            MachineTools.Role(View(MachineEnvironments.Production, MachineFunctions.Application, MachineEnvironments.Production)));

    [Fact]
    public void A_claim_alone_never_reads_as_set_by_an_admin() =>
        Assert.Equal("production/not set (admin: not set; machine says production/not set)", MachineTools.Role(View(claimEnv: MachineEnvironments.Production)));

    [Fact]
    public void A_database_function_without_an_environment_is_never_hidden_behind_not_set()
    {
        Assert.Equal("not set/database (admin)", MachineTools.Role(View(setFn: MachineFunctions.Database)));
        Assert.Equal("not set/database", MachineRoles.Code(null, MachineFunctions.Database));
        Assert.Equal("database server, environment not set", MachineRoles.Describe(null, MachineFunctions.Database));
        Assert.True(MachineRoles.IsStrict(null, MachineFunctions.Database));
    }

    [Fact]
    public void A_claim_that_tightened_the_role_shows_the_admins_value_apart() =>
        Assert.Equal("production/database (admin: test/database; machine says production/not set)",
            MachineTools.Role(View(MachineEnvironments.Test, MachineFunctions.Database, MachineEnvironments.Production)));

    [Fact]
    public void Tools_are_listed_with_their_short_version_and_an_untrusted_mark_and_the_interpreters_as_scripts()
    {
        var tools = new List<ToolInfo>
        {
            new(ToolNames.Docker, "/usr/local/bin/docker", "Docker version 29.8.2, build 7fc2dff", true),
            new(ToolNames.Node, "~/.volta/bin/node", "v22.1.0", false),
            new(ToolNames.Bash, "/bin/bash", "GNU bash, version 5.2.26(1)-release", true),
            new(ToolNames.Git, "/usr/bin/git", null, true),
        };
        var line = MachineTools.Tools(View(tools: tools, interpreters: [tools[2]]));
        Assert.Equal(" tools=docker 29.8.2,node 22.1.0(untrusted),bash 5.2.26,git scripts=bash", line);
    }

    [Fact]
    public void The_block_line_names_a_database_only_role_and_cuts_a_hostile_list()
    {
        var many = Enumerable.Range(0, 32).Select(i => new ToolInfo($"tool{i}", "/x", new string('v', 100), true)).ToList();
        var line = Capture.MachineContext.Line(View(setFn: MachineFunctions.Database, tools: many, interpreters: many), self: false);
        Assert.Contains("role=not set/database (database server, environment not set)", line, StringComparison.Ordinal);
        Assert.True(line.Length <= Capture.MachineContext.LineMax);
        Assert.Equal(Capture.MachineContext.NamesMax, line.Split("tools=")[1].Split(' ')[0].Split(',').Length);
    }
}
