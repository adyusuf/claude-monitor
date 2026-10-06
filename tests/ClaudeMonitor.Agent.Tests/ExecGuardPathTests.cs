using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ExecGuardPathTests : IDisposable
{
    private readonly ExecFixture fx = new();
    private static readonly ExecPolicy Argv = ExecFixture.Level(ExecLevels.Argv);

    public void Dispose() => fx.Dispose();

    private GrantTemplate Grant(string? root = null, string? cwd = null) =>
        new(["/bin/cat", "{path:" + (root ?? fx.Root) + "/}"], cwd ?? fx.Root, 60);

    private ExecDecision Cat(string value, GrantTemplate grant, ExecPolicy? policy = null, string? cwd = null) =>
        fx.Check(ExecFixture.Argv(["/bin/cat", value], grant, cwd ?? grant.Cwd), policy ?? Argv);

    [Fact]
    public void A_file_under_the_root_is_allowed_and_so_is_one_that_does_not_exist_yet()
    {
        if (!ExecFixture.Unix) return;
        var file = ExecFixture.File(fx.Root, "a.log");
        Assert.True(Cat(file, Grant()).Allowed);
        Assert.True(Cat(Path.Combine(fx.Root, "later.log"), Grant()).Allowed);
    }

    [Fact]
    public void A_link_under_the_root_that_leads_to_a_file_outside_it_is_an_escape()
    {
        if (!ExecFixture.Unix) return;
        var target = ExecFixture.File(fx.Outside, "secret");
        var link = Path.Combine(fx.Root, "innocent.log");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(ExecErrors.PathEscape, Cat(link, Grant()).Error);
    }

    [Fact]
    public void A_link_to_a_folder_outside_the_root_is_an_escape_for_a_file_beyond_it_even_when_the_file_is_new()
    {
        if (!ExecFixture.Unix) return;
        ExecFixture.File(fx.Outside, "secret");
        var dir = Path.Combine(fx.Root, "dirlink");
        Directory.CreateSymbolicLink(dir, fx.Outside);
        Assert.Equal(ExecErrors.PathEscape, Cat(Path.Combine(dir, "secret"), Grant()).Error);
        Assert.Equal(ExecErrors.PathEscape, Cat(Path.Combine(dir, "not-yet"), Grant()).Error);
    }

    [Fact]
    public void A_root_that_is_a_link_to_a_forbidden_place_is_refused_after_its_link_is_followed()
    {
        if (!ExecFixture.Unix) return;
        var rootLink = Path.Combine(fx.Dir, "rootlink");
        Directory.CreateSymbolicLink(rootLink, "/etc");
        var decision = Cat(rootLink + "/hosts", Grant(rootLink));
        Assert.Equal(ExecErrors.RootForbidden, decision.Error);
    }

    [Fact]
    public void A_path_inside_the_agent_home_is_refused_even_when_the_grant_root_contains_the_home()
    {
        if (!ExecFixture.Unix) return;
        var secret = ExecFixture.File(fx.Home, "token");
        var grant = Grant(fx.Dir, cwd: fx.Root);
        Assert.Equal(ExecErrors.PathInHome, Cat(secret, grant).Error);
        Assert.Equal(ExecErrors.PathInHome, Cat(Path.Combine(fx.Home, "never-created"), grant).Error);
        Assert.True(Cat(ExecFixture.File(fx.Outside, "fine"), grant).Allowed); // the same root, outside the home
    }

    [Fact]
    public void A_working_directory_in_the_agent_home_is_refused()
    {
        if (!ExecFixture.Unix) return;
        var inHome = new GrantTemplate([ExecFixture.TrustedExe], fx.Home, 60);
        Assert.Equal(ExecErrors.PathInHome, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], inHome, fx.Home), Argv).Error);
    }

    // PRODUCT BUG (left red): ExecPaths.RealPath(allowMissing: false) is documented to return null for a component that does
    // not exist, but FileInfo.LinkTarget does not throw for a missing file, so the path comes back as written.
    [Fact]
    public void A_path_that_does_not_exist_is_unresolved_unless_a_missing_tail_is_allowed()
    {
        if (!ExecFixture.Unix) return;
        var missing = Path.Combine(fx.Dir, "nowhere", "deeper");
        Assert.Null(ExecPaths.RealPath(missing, ExecFixture.Os, allowMissing: false));
        // The part that exists is resolved (on macOS /var is a link to /private/var); the missing tail is kept as written.
        var existing = ExecPaths.RealPath(fx.Dir, ExecFixture.Os, allowMissing: false)!;
        Assert.Equal(Path.Combine(existing, "nowhere", "deeper"), ExecPaths.RealPath(missing, ExecFixture.Os, allowMissing: true));
    }

    [Fact]
    public void A_working_directory_that_does_not_exist_is_refused_as_unresolved()
    {
        if (!ExecFixture.Unix) return;
        var missing = new GrantTemplate([ExecFixture.TrustedExe], Path.Combine(fx.Dir, "nowhere"), 60);
        Assert.Equal(ExecErrors.PathUnresolved, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], missing, missing.Cwd), Argv).Error);
    }

    [Fact]
    public void A_working_directory_given_as_a_link_into_the_home_is_judged_by_its_real_path()
    {
        if (!ExecFixture.Unix) return;
        var cwdLink = Path.Combine(fx.Root, "cwd");
        Directory.CreateSymbolicLink(cwdLink, fx.Home);
        var grant = new GrantTemplate([ExecFixture.TrustedExe], cwdLink, 60);
        Assert.Equal(ExecErrors.PathInHome, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], grant, cwdLink), Argv).Error);
    }

    [Fact]
    public void The_allowed_roots_ceiling_limits_the_working_directory_and_path_values_to_the_listed_folders()
    {
        if (!ExecFixture.Unix) return;
        var ceiling = ExecFixture.Level(ExecLevels.Argv, roots: [fx.Root]);
        var file = ExecFixture.File(fx.Root, "in");
        Assert.True(Cat(file, Grant(), ceiling).Allowed);

        var elsewhere = ExecFixture.Level(ExecLevels.Argv, roots: [fx.Outside]);
        Assert.Equal(ExecErrors.RootNotAllowed, Cat(file, Grant(), elsewhere).Error);

        var cwdOutside = new GrantTemplate([ExecFixture.TrustedExe], fx.Root, 60);
        Assert.Equal(ExecErrors.RootNotAllowed, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], cwdOutside, fx.Root), elsewhere).Error);
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], cwdOutside, fx.Root), ceiling).Allowed);
    }

    [Fact]
    public void A_file_with_more_than_one_hard_link_is_refused_because_it_can_be_reached_from_elsewhere()
    {
        if (!ExecFixture.Unix) return;
        var original = ExecFixture.File(fx.Outside, "original");
        var inside = Path.Combine(fx.Root, "linked");
        ExecFixture.Tool("/bin/ln", original, inside);
        Assert.Equal(ExecErrors.PathHardLink, Cat(inside, Grant()).Error);
        Assert.True(Cat(ExecFixture.File(fx.Root, "single"), Grant()).Allowed);
    }

    [Fact]
    public void A_per_call_run_with_a_working_directory_is_checked_against_the_home_and_the_roots_too()
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(ExecErrors.PathInHome, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], cwd: fx.Home), Argv).Error);
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], cwd: fx.Root), Argv).Allowed);
    }
}
