using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The layers a run is checked against (ADR-0020 §4): always the machine's ceiling first, then every covering policy that applies, and the default
/// where one does not apply or none covers and the default applies. Which stored policy covers a key is decided here too.
/// </summary>
public sealed class AccessLayersTests
{
    private const string Shop = "git:git.example.invalid/team/shop";
    private const string Api = "git:git.example.invalid/team/api";
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();
    private static readonly AccessOsRules None = new([], [], []);
    private static readonly AccessLayer Ceiling = AccessPolicyMath.Ceiling([], [], [], shell: true);

    private static AccessPolicyDocument Doc(string[] keys, string[] linuxAllowed, bool includeProjectFolder = false, bool shell = false) =>
        new(AccessPolicyRules.Format, keys, includeProjectFolder, new AccessOsRules(linuxAllowed, [], []), None, None, shell, false);

    private static AppliedAccessPolicy Applying(Guid project, int version, AccessPolicyDocument doc) =>
        new(project, version, AccessPolicyStates.Applied, false, null, doc, doc.ProjectKeys);

    private static AppliedAccessPolicy NotApplying(Guid project, string state, params string[] keys) => new(project, 2, state, false, null, null, keys);

    /// <summary>Each repository's folder on this machine, by its name: what the guard would hold the run to.</summary>
    private static IReadOnlyList<string> FoldersOf(IEnumerable<string> keys) => [.. keys.Select(k => "/home/dev/" + k[(k.LastIndexOf('/') + 1)..])];

    private static IReadOnlyList<AccessLayer> Compose(IReadOnlyList<AppliedAccessPolicy> covering, bool defaultApplies, string os = OsKinds.Linux) =>
        AccessLayers.Compose(Ceiling, covering, defaultApplies, FoldersOf, [Shop], os);

    [Fact]
    public void With_no_covering_policy_the_ceiling_alone_holds_a_run_unless_the_default_applies()
    {
        Assert.Equal([Ceiling], Compose([], defaultApplies: false));

        var layers = Compose([], defaultApplies: true);

        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Default], layers.Select(l => l.Kind));
        Assert.Equal(["/home/dev/shop"], layers[1].AllowedFolders!);
        Assert.False(layers[1].Shell);
    }

    [Fact]
    public void An_applying_policy_follows_the_ceiling_with_its_version_its_folders_and_its_projects_folders()
    {
        var layers = Compose([Applying(ProjectA, 5, Doc([Shop], ["/srv/app"], includeProjectFolder: true, shell: true))], defaultApplies: true);

        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Policy], layers.Select(l => l.Kind));
        Assert.Equal(5, layers[1].Version);
        Assert.Equal(["/srv/app", "/home/dev/shop"], layers[1].AllowedFolders!);
        Assert.True(layers[1].Shell);
    }

    [Theory]
    [InlineData(AccessPolicyStates.Invalid)]
    [InlineData(AccessPolicyStates.UnsignedIgnored)]
    [InlineData(AccessPolicyStates.KeyUnknown)]
    [InlineData(AccessPolicyStates.RollbackRefused)]
    [InlineData(AccessPolicyStates.Default)]
    public void A_covering_policy_that_does_not_apply_gives_the_default_even_with_the_switch_off(string state)
    {
        var layers = Compose([NotApplying(ProjectA, state, Shop)], defaultApplies: false);

        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Default], layers.Select(l => l.Kind));
    }

    [Fact]
    public void One_applying_and_one_failing_covering_policy_give_both_the_policy_and_the_default()
    {
        var layers = AccessLayers.Compose(Ceiling, [Applying(ProjectA, 5, Doc([Shop], ["/srv/app"])), NotApplying(ProjectB, AccessPolicyStates.Invalid, Api)],
            false, FoldersOf, [Shop, Api], OsKinds.Linux);

        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Policy, AccessRefusedBy.Default], layers.Select(l => l.Kind));
        Assert.Equal(["/home/dev/shop", "/home/dev/api"], layers[2].AllowedFolders!);
    }

    [Fact]
    public void A_policy_with_no_section_for_the_machines_os_is_not_a_layer_so_the_default_applies()
    {
        var layers = Compose([Applying(ProjectA, 5, Doc([Shop], ["/srv/app"]))], defaultApplies: false, os: "freebsd");

        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Default], layers.Select(l => l.Kind));
    }

    [Fact]
    public void A_policy_covers_a_key_in_any_case_or_unicode_form()
    {
        var policy = Applying(ProjectA, 1, Doc([Shop], []));

        Assert.Single(AccessLayers.Covering([policy], ["git:git.example.invalid/Team/SHOP"]));
        Assert.Empty(AccessLayers.Covering([policy], [Api]));
    }

    [Fact]
    public void A_tombstone_does_not_hold_a_key_another_projects_live_policy_took_over_but_still_holds_one_nobody_else_covers()
    {
        var tombstone = NotApplying(ProjectA, AccessPolicyStates.Default, Shop, Api);
        var live = Applying(ProjectB, 1, Doc([Shop], ["/srv/shop"]));

        Assert.Equal([ProjectB], AccessLayers.Covering([tombstone, live], [Shop]).Select(p => p.ProjectId));
        Assert.Equal([ProjectA], AccessLayers.Covering([tombstone, live], [Api]).Select(p => p.ProjectId));
    }
}
