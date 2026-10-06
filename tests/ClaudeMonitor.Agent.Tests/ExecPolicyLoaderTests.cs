using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ExecPolicyLoaderTests : IDisposable
{
    private readonly ExecFixture fx = new();
    private string PolicyPath => Path.Combine(fx.Dir, "exec.json");

    public void Dispose() => fx.Dispose();

    private TempHome Home(bool service) => new(c => c with { ServiceMode = service, ExecConfigPath = PolicyPath });

    [Theory]
    [InlineData("off")]
    [InlineData("argv")]
    [InlineData("shell")]
    public void An_interactive_agent_takes_the_level_the_user_saved(string level)
    {
        using var home = Home(service: false);
        Assert.Equal(new ExecPolicy(level, [], []), ExecPolicyLoader.Load(home.Config, level));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ARGV")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_missing_level_is_off(string? level)
    {
        using var home = Home(service: false);
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.Load(home.Config, level));
    }

    [Fact]
    public void A_service_agent_ignores_the_level_in_its_own_identity_file()
    {
        using var home = Home(service: true);
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.Load(home.Config, "shell")); // no admin file at all
    }

    [Fact]
    public void A_service_policy_file_owned_by_the_current_user_is_off_though_the_same_file_reads_for_a_reinstall()
    {
        File.WriteAllText(PolicyPath, """{"level":"shell","allowedExecutables":[],"allowedRoots":[]}""");
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.ReadServiceFile(PolicyPath));
        Assert.Equal(ExecLevels.Shell, ExecPolicyLoader.ReadExisting(PolicyPath)!.Level);

        using var home = Home(service: true);
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.Load(home.Config, "shell"));
    }

    [Fact]
    public void A_missing_service_policy_file_is_off()
    {
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.ReadServiceFile(PolicyPath));
        Assert.Null(ExecPolicyLoader.ReadExisting(PolicyPath));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"allowedRoots":[]}""")]
    [InlineData("""{"level":"root"}""")]
    [InlineData("""{"level":"argv","allowedExecutables":["bin/tool"]}""")]
    [InlineData("""{"level":"argv","allowedRoots":["/ok","relative/dir"]}""")]
    [InlineData("""{"level":"argv","allowedRoots":[""]}""")]
    [InlineData("""{"level":5}""")]
    public void An_existing_policy_that_is_invalid_or_has_a_relative_path_is_not_read(string json)
    {
        File.WriteAllText(PolicyPath, json);
        Assert.Null(ExecPolicyLoader.ReadExisting(PolicyPath));
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.ReadServiceFile(PolicyPath));
    }

    [Fact]
    public void An_existing_policy_is_read_with_its_ceiling_and_missing_lists_are_empty()
    {
        File.WriteAllText(PolicyPath, """{"level":"argv","allowedExecutables":["/usr/bin/true"],"allowedRoots":["/srv/app","/var/log/app"]}""");
        var read = ExecPolicyLoader.ReadExisting(PolicyPath)!;
        Assert.Equal(ExecLevels.Argv, read.Level);
        Assert.Equal(["/usr/bin/true"], read.AllowedExecutables);
        Assert.Equal(["/srv/app", "/var/log/app"], read.AllowedRoots);

        File.WriteAllText(PolicyPath, """{"level":"off"}""");
        var bare = ExecPolicyLoader.ReadExisting(PolicyPath)!;
        Assert.Equal((ExecLevels.Off, 0, 0), (bare.Level, bare.AllowedExecutables.Count, bare.AllowedRoots.Count));
    }

    [Fact]
    public void An_existing_policy_that_is_a_symlink_is_not_read()
    {
        var real = Path.Combine(fx.Outside, "real.json");
        File.WriteAllText(real, """{"level":"shell"}""");
        File.CreateSymbolicLink(PolicyPath, real);
        Assert.Null(ExecPolicyLoader.ReadExisting(PolicyPath));
    }

    [Fact]
    public void Save_writes_the_level_and_the_ceiling_as_indented_camel_case_json_readable_by_all_and_leaves_no_temp_file()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, ["/usr/bin/true"], ["/srv/app"]));

        var json = JsonNode.Parse(File.ReadAllText(PolicyPath))!.AsObject();
        Assert.Equal(["level", "allowedExecutables", "allowedRoots"], json.Select(p => p.Key));
        Assert.Equal("argv", (string?)json["level"]);
        Assert.Equal(["/usr/bin/true"], json["allowedExecutables"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["/srv/app"], json["allowedRoots"]!.AsArray().Select(n => (string?)n));
        Assert.Contains('\n', File.ReadAllText(PolicyPath)); // indented
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(PolicyPath));
        Assert.False(File.Exists(PolicyPath + ".tmp"));
    }

    [Fact]
    public void Save_creates_the_folder_replaces_an_older_file_and_round_trips()
    {
        var nested = Path.Combine(fx.Dir, "etc", "cm-agent", "exec.json");
        ExecPolicyLoader.Save(nested, new ExecPolicy(ExecLevels.Shell, [], []));
        ExecPolicyLoader.Save(nested, new ExecPolicy(ExecLevels.Argv, [], ["/srv/app"]));
        Assert.Equal(new[] { "/srv/app" }, ExecPolicyLoader.ReadExisting(nested)!.AllowedRoots);
        Assert.Equal(ExecLevels.Argv, ExecPolicyLoader.ReadExisting(nested)!.Level);
    }

    [Fact]
    public void Save_refuses_an_unknown_level_or_a_relative_path_and_keeps_the_file_it_had()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, [], []));
        Assert.Throws<ArgumentException>(() => ExecPolicyLoader.Save(PolicyPath, new ExecPolicy("root", [], [])));
        Assert.Throws<ArgumentException>(() => ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, ["tool"], [])));
        Assert.Throws<ArgumentException>(() => ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, [], ["../up"])));
        Assert.Equal(ExecLevels.Argv, ExecPolicyLoader.ReadExisting(PolicyPath)!.Level);
        Assert.False(File.Exists(PolicyPath + ".tmp"));
    }

    [Fact]
    public void Describe_gives_the_level_and_counts_and_never_a_path()
    {
        var line = ExecPolicyLoader.Describe(new ExecPolicy(ExecLevels.Argv, ["/usr/bin/secret-tool"], ["/srv/private", "/srv/other"]));
        Assert.Equal("exec level argv, 1 allowed executables, 2 allowed roots", line);
        Assert.DoesNotContain("/", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_as_root_is_judged_by_the_effective_uid()
    {
        Assert.Equal(UnixFiles.Euid() == 0, ExecPolicyLoader.RunningAsRoot());
    }

    [Theory]
    [InlineData("shell", 0, 0, true, true)]
    [InlineData("shell", 1, 0, true, false)]
    [InlineData("shell", 0, 1, true, false)]
    [InlineData("argv", 1, 1, true, false)]
    [InlineData("off", 1, 1, false, false)]
    public void A_policy_with_a_ceiling_never_allows_shell_but_still_allows_argv_by_its_level(string level, int executables, int roots, bool argv, bool shell)
    {
        var policy = new ExecPolicy(level, Enumerable.Repeat("/usr/bin/true", executables).ToList(), Enumerable.Repeat("/srv/app", roots).ToList());
        Assert.Equal(executables + roots > 0, policy.HasCeiling);
        Assert.Equal(argv && level != "off", policy.Allows(RunModes.Argv));
        Assert.Equal(shell, policy.Allows(RunModes.Shell));
    }

    [Fact]
    public void The_guard_refuses_a_shell_run_under_a_ceiling_even_at_level_shell()
    {
        using var fx2 = new ExecFixture();
        var ceiling = ExecFixture.Level(ExecLevels.Shell, roots: [fx2.Root]);
        Assert.Equal(ExecErrors.ExecLevel, fx2.Check(ExecFixture.Shell("echo hi"), ceiling).Error);
        Assert.True(fx2.Check(ExecFixture.Shell("echo hi"), ExecFixture.Level(ExecLevels.Shell)).Allowed);
        Assert.True(fx2.Check(ExecFixture.Argv(["/usr/bin/true"]), ceiling).Allowed);
    }
}
