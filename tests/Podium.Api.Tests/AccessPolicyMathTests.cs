using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The lexical half of access enforcement (ADR-0020 §4): what each layer kind limits, which refusal a run meets and in which order. Pure; the OS is a
/// parameter, so every OS is judged from any machine. The agent resolves real paths first and then asks exactly this.
/// </summary>
public sealed class AccessPolicyMathTests
{
    private const string Linux = OsKinds.Linux;
    private const string Mac = OsKinds.MacOs;
    private const string Win = OsKinds.Windows;
    private const string Tail = "/usr/bin/tail";
    private static readonly string Sha = new('a', 64);
    private static readonly string OtherSha = new('b', 64);

    private static AccessLayer Layer(string kind = AccessRefusedBy.Policy, int? version = 3, string[]? allowed = null, string[]? denied = null,
        AccessApp[]? apps = null, bool shell = false) => new(kind, version, allowed, denied ?? [], apps, shell);

    private static AccessCheck Argv(string? cwd = "/srv/app", string[]? paths = null, string? exe = Tail, string? sha = null) =>
        new(cwd, paths ?? [], exe, sha, false, false);

    // ---- under / inside ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/srv/app", "/srv/app", Linux, true)]
    [InlineData("/srv/app/logs/a.log", "/srv/app", Linux, true)]
    [InlineData("/srv/application", "/srv/app", Linux, false)]
    [InlineData("/SRV/app/a", "/srv/app", Linux, false)]
    [InlineData("/SRV/app/a", "/srv/app", Mac, true)]
    [InlineData(@"C:\Work\a.txt", @"c:\work", Win, true)]
    [InlineData(@"C:\Workshop", @"C:\Work", Win, false)]
    [InlineData("/srv/app", "/srv/app", "freebsd", false)]
    [InlineData("", "/srv", Linux, false)]
    [InlineData("/srv", "", Linux, false)]
    public void A_path_is_under_a_folder_only_at_a_separator_boundary_with_the_case_rule_of_its_os(string path, string folder, string os, bool under) =>
        Assert.Equal(under, AccessPolicyMath.IsUnder(path, folder, os));

    [Fact]
    public void Inside_needs_an_allowed_folder_and_no_denied_one_and_an_empty_allowed_list_allows_nothing()
    {
        Assert.True(AccessPolicyMath.IsInside("/srv/app/a", ["/srv"], ["/srv/secret"], Linux));
        Assert.False(AccessPolicyMath.IsInside("/srv/secret/a", ["/srv"], ["/srv/secret"], Linux));
        Assert.False(AccessPolicyMath.IsInside("/srv/app/a", [], [], Linux));
    }

    // ---- the layers -------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_ceiling_list_is_no_limit_while_a_listed_one_limits()
    {
        var open = AccessPolicyMath.Ceiling([], [], [], shell: true);
        var listed = AccessPolicyMath.Ceiling(["/srv"], ["/srv/x"], [new AccessApp(Tail, null)], shell: false);

        Assert.Equal((AccessRefusedBy.Ceiling, (int?)null, true), (open.Kind, open.Version, open.Shell));
        Assert.Null(open.AllowedFolders);
        Assert.Null(open.Applications);
        Assert.Equal(["/srv"], listed.AllowedFolders!);
        Assert.Equal([Tail], listed.Applications!.Select(a => a.Path));
        Assert.False(listed.Shell);
    }

    [Fact]
    public void A_policy_that_includes_the_project_folder_limits_even_when_no_folder_is_known_and_one_without_lists_does_not()
    {
        var none = new AccessOsRules([], [], []);

        var withProject = AccessPolicyMath.Policy(4, none, includeProjectFolder: true, [], shell: false);
        var withFolders = AccessPolicyMath.Policy(4, new AccessOsRules(["/srv"], [], []), includeProjectFolder: true, ["/home/dev/shop"], shell: true);
        var open = AccessPolicyMath.Policy(4, none, includeProjectFolder: false, ["/home/dev/shop"], shell: false);

        Assert.Equal((AccessRefusedBy.Policy, (int?)4), (withProject.Kind, withProject.Version));
        Assert.Empty(withProject.AllowedFolders!);
        Assert.Equal(["/srv", "/home/dev/shop"], withFolders.AllowedFolders!);
        Assert.True(withFolders.Shell);
        Assert.Null(open.AllowedFolders);
        Assert.Null(open.Applications);
    }

    [Fact]
    public void The_default_is_the_project_folders_only_any_application_and_no_shell()
    {
        var layer = AccessPolicyMath.Default(["/home/dev/shop"]);

        Assert.Equal((AccessRefusedBy.Default, (int?)null, false), (layer.Kind, layer.Version, layer.Shell));
        Assert.Equal(["/home/dev/shop"], layer.AllowedFolders!);
        Assert.Empty(layer.DeniedFolders);
        Assert.Null(layer.Applications);
        Assert.Equal(AccessErrors.CwdNotAllowed, AccessPolicyMath.Evaluate([AccessPolicyMath.Default([])], Argv(), Linux)?.Code);
    }

    // ---- evaluate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_layer_allows_everything_and_an_unknown_os_is_refused_by_the_first_layer()
    {
        Assert.Null(AccessPolicyMath.Evaluate([], Argv(cwd: "/anywhere"), Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.Unresolved, AccessRefusedBy.Ceiling, null),
            AccessPolicyMath.Evaluate([Layer(AccessRefusedBy.Ceiling, null), Layer()], Argv(), "freebsd"));
    }

    [Fact]
    public void A_shell_or_script_run_needs_every_layer_to_allow_shell_and_then_meets_no_folder_or_application_limit()
    {
        var shell = new AccessCheck("/elsewhere", ["/elsewhere/x"], "/bin/sh", null, Shell: true, Script: false);
        var script = shell with { Shell = false, Script = true };
        var allOpen = new[] { Layer(AccessRefusedBy.Ceiling, null, allowed: ["/srv"], shell: true), Layer(allowed: ["/srv"], apps: [new(Tail, null)], shell: true) };
        var policyClosed = new[] { allOpen[0], Layer(version: 7, shell: false) };

        Assert.Null(AccessPolicyMath.Evaluate(allOpen, shell, Linux));
        Assert.Null(AccessPolicyMath.Evaluate(allOpen, script, Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.ShellRefused, AccessRefusedBy.Policy, 7), AccessPolicyMath.Evaluate(policyClosed, shell, Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.ScriptRefused, AccessRefusedBy.Policy, 7), AccessPolicyMath.Evaluate(policyClosed, script, Linux));
    }

    [Fact]
    public void The_working_directory_must_be_under_every_layers_allowed_folders_and_a_denied_folder_wins_within_a_layer()
    {
        var layers = new[] { Layer(allowed: ["/srv"], denied: ["/srv/secret"]) };

        Assert.Null(AccessPolicyMath.Evaluate(layers, Argv("/srv/app"), Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Policy, 3), AccessPolicyMath.Evaluate(layers, Argv("/opt"), Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.CwdDenied, AccessRefusedBy.Policy, 3), AccessPolicyMath.Evaluate(layers, Argv("/srv/secret/x"), Linux));
    }

    // F-300, a known limit (ADR-0020 §4, docs/security.md): a run that names no working directory has no folder to judge, and free argument paths are not placeholders.
    [Fact]
    public void An_argv_run_with_no_working_directory_meets_no_folder_check_and_a_free_argument_is_not_a_path_to_judge()
    {
        var layers = new[] { Layer(allowed: ["/srv"], denied: ["/srv/secret"]) };

        Assert.Equal(AccessErrors.CwdNotAllowed, AccessPolicyMath.Evaluate(layers, Argv("/opt"), Linux)?.Code);
        Assert.Null(AccessPolicyMath.Evaluate(layers, Argv(cwd: null), Linux));
        Assert.Null(AccessPolicyMath.Evaluate([AccessPolicyMath.Default(["/srv/app"])], Argv(cwd: null), Linux));
    }

    [Fact]
    public void The_first_layer_that_refuses_is_named_so_a_ceiling_refusal_comes_before_a_policy_one()
    {
        var ceiling = Layer(AccessRefusedBy.Ceiling, null, allowed: ["/srv"]);
        var policy = Layer(version: 5, allowed: ["/opt"]);

        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Ceiling, null), AccessPolicyMath.Evaluate([ceiling, policy], Argv("/home"), Linux));
        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Policy, 5), AccessPolicyMath.Evaluate([ceiling, policy], Argv("/srv/a"), Linux));
    }

    [Fact]
    public void Every_placeholder_path_is_judged_after_the_working_directory_and_before_the_executable()
    {
        var layers = new[] { Layer(allowed: ["/srv"], denied: ["/srv/secret"], apps: [new("/usr/bin/cat", null)]) };

        Assert.Equal(AccessErrors.PathNotAllowed, AccessPolicyMath.Evaluate(layers, Argv(paths: ["/srv/a", "/etc/passwd"]), Linux)?.Code);
        Assert.Equal(AccessErrors.PathDenied, AccessPolicyMath.Evaluate(layers, Argv(paths: ["/srv/secret/k"]), Linux)?.Code);
        Assert.Equal(AccessErrors.CwdNotAllowed, AccessPolicyMath.Evaluate(layers, Argv("/opt", ["/etc/passwd"]), Linux)?.Code);
        Assert.Equal(AccessErrors.ExeNotAllowed, AccessPolicyMath.Evaluate(layers, Argv(paths: ["/srv/a"]), Linux)?.Code);
    }

    [Fact]
    public void The_executable_must_be_on_every_application_list_by_path_and_where_given_by_sha256()
    {
        var hashed = new[] { Layer(apps: [new(Tail, Sha)]) };
        var plain = new[] { Layer(apps: [new(Tail, null)]) };

        Assert.Null(AccessPolicyMath.Evaluate(hashed, Argv(sha: Sha), Linux));
        Assert.Equal(AccessErrors.ExeHash, AccessPolicyMath.Evaluate(hashed, Argv(sha: OtherSha), Linux)?.Code);
        Assert.Equal(AccessErrors.ExeHash, AccessPolicyMath.Evaluate(hashed, Argv(sha: null), Linux)?.Code);
        Assert.Null(AccessPolicyMath.Evaluate(plain, Argv(sha: null), Linux));
        Assert.Equal(AccessErrors.ExeNotAllowed, AccessPolicyMath.Evaluate(plain, Argv(exe: "/usr/bin/head"), Linux)?.Code);
        Assert.Equal(AccessErrors.ExeNotAllowed, AccessPolicyMath.Evaluate(plain, Argv(exe: null), Linux)?.Code);
        Assert.Equal(AccessErrors.ExeNotAllowed, AccessPolicyMath.Evaluate(plain, Argv(exe: "/USR/bin/tail"), Linux)?.Code);
        Assert.Null(AccessPolicyMath.Evaluate(plain, Argv(exe: "/USR/bin/tail"), Mac));
    }

    [Fact]
    public void A_hash_is_needed_only_when_every_entry_that_names_the_executable_carries_one()
    {
        AccessLayer[] hashed = [Layer(apps: [new(Tail, Sha)])];
        AccessLayer[] mixed = [Layer(apps: [new(Tail, Sha), new(Tail, null)])];
        AccessLayer[] open = [Layer(apps: null)];

        Assert.True(AccessPolicyMath.NeedsHash(hashed, Tail, Linux));
        Assert.False(AccessPolicyMath.NeedsHash(mixed, Tail, Linux));
        Assert.False(AccessPolicyMath.NeedsHash(hashed, "/usr/bin/head", Linux));
        Assert.False(AccessPolicyMath.NeedsHash(open, Tail, Linux));
    }
}
