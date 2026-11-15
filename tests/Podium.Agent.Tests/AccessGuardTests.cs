using System.Runtime.Versioning;
using System.Security.Cryptography;
using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The guard holds a run to its layers on real paths (ADR-0020 §4): a layer's folders and applications are resolved first (what does not resolve, or
/// resolves to a forbidden root, is dropped, so the list allows less), a folder named through a link still counts as the folder, a missing path is
/// judged by its deepest existing part, an interpreter counts as a script, and a listed SHA-256 is checked against the file itself.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class AccessGuardTests : IDisposable
{
    private static readonly string Os = ExecFixture.Os;
    private readonly ExecFixture fx = new();

    public void Dispose() => fx.Dispose();

    private static string Real(string path) => PathIdentity.Resolve(path, Os, allowMissing: true)!;

    private static AccessLayer Policy(string[]? allowed = null, string[]? denied = null, AccessApp[]? apps = null, bool shell = false) =>
        new(AccessRefusedBy.Policy, 4, allowed, denied ?? [], apps, shell);

    private static AccessCheck Argv(string cwd, string exe = ExecFixture.TrustedExe, string[]? paths = null) => new(cwd, paths ?? [], exe, null, false, false);

    [Fact]
    public void A_run_in_an_allowed_folder_passes_and_one_outside_it_is_refused_by_that_layer()
    {
        if (!ExecFixture.Unix) return;
        AccessLayer[] layers = [Policy([fx.Root])];

        Assert.Null(AccessGuard.Check(layers, Argv(Real(fx.Root)), Os));
        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Policy, 4), AccessGuard.Check(layers, Argv(Real(fx.Outside)), Os));
    }

    [Fact]
    public void A_path_that_does_not_exist_yet_is_judged_by_its_deepest_existing_folder()
    {
        if (!ExecFixture.Unix) return;
        AccessLayer[] layers = [Policy([fx.Root])];

        Assert.Null(AccessGuard.Check(layers, Argv(Real(fx.Root), paths: [Real(Path.Combine(fx.Root, "new", "deeper.log"))]), Os));
        Assert.Equal(AccessErrors.PathNotAllowed, AccessGuard.Check(layers, Argv(Real(fx.Root), paths: [Real(Path.Combine(fx.Outside, "new.log"))]), Os)?.Code);
    }

    [Fact]
    public void An_allowed_folder_named_through_a_link_counts_as_its_real_folder_and_a_path_named_through_one_too()
    {
        if (!ExecFixture.Unix) return;
        var link = Path.Combine(fx.Dir, "root-link");
        Directory.CreateSymbolicLink(link, fx.Root);
        AccessLayer[] layers = [Policy([link])];

        Assert.Null(AccessGuard.Check(layers, Argv(Real(fx.Root)), Os));
        Assert.Null(AccessGuard.Check([Policy([Real(fx.Root)])], Argv(link), Os)); // the link's identity is the folder's
    }

    [Fact]
    public void A_denied_folder_named_through_a_link_still_denies_its_real_folder()
    {
        if (!ExecFixture.Unix) return;
        var secret = Directory.CreateDirectory(Path.Combine(fx.Root, "secret")).FullName;
        var link = Path.Combine(fx.Dir, "secret-link");
        Directory.CreateSymbolicLink(link, secret);

        var refusal = AccessGuard.Check([Policy([fx.Root], [link])], Argv(Real(Path.Combine(secret, "x"))), Os);

        Assert.Equal(AccessErrors.CwdDenied, refusal?.Code);
    }

    [Fact]
    public void An_allowed_folder_that_does_not_exist_or_is_a_link_to_a_forbidden_root_is_dropped_so_the_layer_allows_less_never_all()
    {
        if (!ExecFixture.Unix) return;
        var toRoot = Path.Combine(fx.Dir, "to-root");
        Directory.CreateSymbolicLink(toRoot, "/");

        Assert.Equal(AccessErrors.CwdNotAllowed, AccessGuard.Check([Policy([Path.Combine(fx.Dir, "missing")])], Argv(Real(fx.Root)), Os)?.Code);
        Assert.Equal(AccessErrors.CwdNotAllowed, AccessGuard.Check([Policy([toRoot])], Argv(Real(fx.Root)), Os)?.Code);
        Assert.Equal(AccessErrors.CwdNotAllowed, AccessGuard.Check([Policy([toRoot])], Argv("/"), Os)?.Code);
    }

    [Fact]
    public void An_interpreter_counts_as_a_script_for_every_layer_but_the_ceiling_and_for_a_ceiling_with_lists_that_does_not_name_it()
    {
        if (!ExecFixture.Unix) return;
        var cwd = Real(fx.Root);
        var openCeiling = AccessPolicyMath.Ceiling([], [], [], shell: false);
        var listedCeiling = AccessPolicyMath.Ceiling([fx.Root], [], [new AccessApp(ExecFixture.TrustedExe, null)], shell: false);

        Assert.Equal(new AccessRefusal(AccessErrors.ScriptRefused, AccessRefusedBy.Policy, 4),
            AccessGuard.Check([openCeiling, Policy([fx.Root])], Argv(cwd, "/bin/sh"), Os, interpreter: true));
        Assert.Null(AccessGuard.Check([openCeiling, Policy([fx.Root], shell: true)], Argv(cwd, "/bin/sh"), Os, interpreter: true));
        Assert.Equal(new AccessRefusal(AccessErrors.ScriptRefused, AccessRefusedBy.Ceiling, null),
            AccessGuard.Check([listedCeiling, Policy([fx.Root], shell: true)], Argv(cwd, "/bin/sh"), Os, interpreter: true));
    }

    [Fact]
    public void A_listed_sha256_is_checked_against_the_file_itself()
    {
        if (!ExecFixture.Unix) return;
        var exe = Real(ExecFixture.File(fx.Root, "tool", "#!/bin/true\n"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(exe)));

        Assert.Null(AccessGuard.Check([Policy(apps: [new AccessApp(exe, sha)])], Argv(Real(fx.Root), exe), Os));
        Assert.Equal(AccessErrors.ExeHash, AccessGuard.Check([Policy(apps: [new AccessApp(exe, new string('0', 64))])], Argv(Real(fx.Root), exe), Os)?.Code);
        Assert.Equal(AccessErrors.ExeNotAllowed, AccessGuard.Check([Policy(apps: [new AccessApp(exe, sha)])], Argv(Real(fx.Root)), Os)?.Code);
    }

    [Fact]
    public void An_unknown_os_is_refused_as_unresolved_and_no_layer_allows_everything()
    {
        if (!ExecFixture.Unix) return;

        Assert.Equal(AccessErrors.Unresolved, AccessGuard.Check([Policy([fx.Root])], Argv(Real(fx.Root)), "freebsd")?.Code);
        Assert.Null(AccessGuard.Check([], Argv("/"), Os));
    }
}
