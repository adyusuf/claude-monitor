using System.Runtime.Versioning;
using Podium.Agent.Access;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// Which repositories a run touches and which folders a project has on this machine (ADR-0020 §4), read from the checkouts themselves, never from the
/// run's label alone: a nested clone never hides the repository around it, a label that names none of the working directory's checkouts is a
/// mismatch, and a folder counts for a project only while its checkout still is that project.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class AccessRunKeysTests : IDisposable
{
    private const string Outer = "git:git.example.invalid/team/shop";
    private const string Inner = "git:git.example.invalid/team/theme";
    private readonly ExecFixture fx = new();
    private readonly TempHome home = new();

    public void Dispose()
    {
        home.Dispose();
        fx.Dispose();
    }

    private static string Checkout(string folder, string remote)
    {
        var git = Directory.CreateDirectory(Path.Combine(folder, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = {remote}\n");
        return PathIdentity.Resolve(folder, ExecFixture.Os, allowMissing: false)!;
    }

    private (string Outer, string Inner) Nested()
    {
        var outer = Checkout(Path.Combine(fx.Root, "shop"), "git@git.example.invalid:team/shop.git");
        var inner = Checkout(Path.Combine(outer, "vendor", "theme"), "https://git.example.invalid/team/theme");
        return (outer, inner);
    }

    [Fact]
    public void A_path_in_a_nested_clone_touches_the_clone_and_every_checkout_around_it_innermost_first()
    {
        if (!ExecFixture.Unix) return;
        var (_, inner) = Nested();

        var (judged, keys) = AccessRunKeys.KeysOf(Path.Combine(inner, "src", "not-yet.css"));

        Assert.True(judged);
        Assert.Equal([Inner, Outer], keys);
        Assert.Equal((true, 0), (AccessRunKeys.KeysOf(fx.Outside).Judged, AccessRunKeys.KeysOf(fx.Outside).Keys.Count));
    }

    [Fact]
    public void A_label_is_joined_by_every_checkout_the_run_touches_and_one_naming_none_of_the_working_directorys_is_a_mismatch()
    {
        if (!ExecFixture.Unix) return;
        var (outer, inner) = Nested();

        var labelled = AccessRunKeys.Of("git:git.example.invalid/Team/Shop", outer, []);
        var relabelled = AccessRunKeys.Of("git:git.example.invalid/team/other", outer, [Path.Combine(inner, "a.css")]);
        var outside = AccessRunKeys.Of("git:git.example.invalid/team/other", fx.Outside, []);

        Assert.Equal((false, 1), (labelled.LabelMismatch, labelled.Keys.Count)); // the same repository in another case is one key
        Assert.True(relabelled.LabelMismatch);
        Assert.Equal(["git:git.example.invalid/team/other", Outer, Inner], relabelled.Keys);
        Assert.False(outside.LabelMismatch); // outside any checkout there is nothing to contradict the label
    }

    [Fact]
    public void A_mismatch_is_refused_by_a_covering_policy_when_one_applies_else_by_the_default()
    {
        var policy = new AccessLayer(AccessRefusedBy.Policy, 6, null, [], null, false);

        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Policy, 6), AccessRunKeys.Mismatch([AccessPolicyMath.Ceiling([], [], [], true), policy]));
        Assert.Equal(new AccessRefusal(AccessErrors.CwdNotAllowed, AccessRefusedBy.Default, null), AccessRunKeys.Mismatch([AccessPolicyMath.Default([])]));
    }

    [Fact]
    public void A_projects_folders_are_the_listed_folders_whose_checkout_still_is_that_project_as_real_paths()
    {
        if (!ExecFixture.Unix) return;
        var (outer, inner) = Nested();
        var other = Checkout(Path.Combine(fx.Root, "reclone"), "https://git.example.invalid/team/api");
        using var store = new LocalStore(home.Config.DatabasePath);

        var folders = AccessFolders.For(Outer, [Path.Combine(outer, "src"), outer, other, Path.Combine(fx.Root, "missing")], store, ExecFixture.Os);

        Assert.Equal([outer], folders);
        Assert.Equal([inner], AccessFolders.For(Inner, [inner], store, ExecFixture.Os));
    }
}
