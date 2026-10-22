using System.Runtime.Versioning;
using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Agent.Install;

namespace Podium.Agent.Tests;

/// <summary>The rename to aipod keeps the old names working: the binary, the AP_ variables, the process check.</summary>
public sealed class BrandTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("aipod-brand-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Theory]
    [InlineData("aipod", true)]
    [InlineData("aipod.exe", true)]
    [InlineData("aipodw", true)]
    [InlineData("aipod", true)]
    [InlineData("AIPOD.EXE", true)]
    [InlineData("claude", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_process_is_ours_under_either_name(string? name, bool ours) => Assert.Equal(ours, Brand.IsOurProcess(name));

    [Fact]
    public void A_new_variable_wins_over_the_old_one_and_the_old_one_still_works()
    {
        var env = new Dictionary<string, string?> { ["AP_SERVER"] = "https://old.invalid", ["AP_SERVER"] = "https://new.invalid/", ["AP_STOP_WAIT"] = "7", ["CLAUDE_CONFIG_DIR"] = "/x" };
        var config = AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("https://new.invalid", config.ServerOverride);
        Assert.Equal(TimeSpan.FromSeconds(7), config.StopWait); // only the old name is set
        Assert.True(config.StopWaitFromEnvironment);
        Assert.Equal("/x", Brand.WithAliases(k => env.GetValueOrDefault(k))("CLAUDE_CONFIG_DIR")); // not a AP_ name: passed through
    }

    [Fact]
    public void An_empty_new_variable_does_not_hide_the_old_one()
    {
        var env = new Dictionary<string, string?> { ["AP_SERVER"] = "", ["AP_SERVER"] = "https://old.invalid" };
        Assert.Equal("https://old.invalid", AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k)).ServerOverride);
    }

    [Fact]
    public void No_run_may_set_the_new_variables_either() =>
        Assert.Contains("AIPOD_", RunEnvironment.ForbiddenPrefixes);

    [UnsupportedOSPlatform("windows")]
    [Fact]
    public void The_alias_is_a_relative_link_made_once_and_made_again_when_it_points_elsewhere()
    {
        var binary = Path.Combine(dir, "aipod");
        File.WriteAllText(binary, "program");

        var alias = BinaryAlias.Ensure(binary);

        Assert.Equal(Path.Combine(dir, "aipod"), alias);
        Assert.Equal("aipod", new FileInfo(alias!).LinkTarget);
        Assert.Equal("program", File.ReadAllText(alias!));
        Assert.Equal(alias, BinaryAlias.Ensure(binary)); // already right: left alone

        File.Delete(alias!);
        File.CreateSymbolicLink(alias!, "somewhere-else");
        BinaryAlias.Ensure(binary);
        Assert.Equal("aipod", new FileInfo(alias!).LinkTarget);

        File.Delete(alias!);
        File.WriteAllText(alias!, "a stale copy"); // a plain file where the link belongs
        BinaryAlias.Ensure(binary);
        Assert.Equal("program", File.ReadAllText(alias!));
    }

    [Fact]
    public void Without_a_binary_or_where_the_binary_already_has_the_new_name_nothing_is_made()
    {
        Assert.Null(BinaryAlias.Ensure(Path.Combine(dir, "aipod")));
        var already = Path.Combine(dir, "aipod");
        File.WriteAllText(already, "program");
        Assert.Null(BinaryAlias.Ensure(already));
        Assert.Equal("program", File.ReadAllText(already));
    }

    [UnsupportedOSPlatform("windows")]
    [Fact]
    public void A_folder_that_cannot_be_written_is_not_an_error()
    {
        var binary = Path.Combine(dir, "aipod");
        File.WriteAllText(binary, "program");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (Environment.UserName == "root") return; // root writes anywhere
            Assert.Null(BinaryAlias.Ensure(binary));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
