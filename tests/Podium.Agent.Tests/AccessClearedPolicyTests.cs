using System.Text.Json;
using Podium.Agent.Access;
using Podium.Agent.Approvals;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// A policy the web took back (F-304, decided to stay as it is): the web answers "no policy covers this repository", but the machine still holds the
/// tombstone, reports it as the default and applies the default layer even with the enforcement switch off (project folder only, no shell). Whoever
/// reads the web's line must know the default is what runs, which is why the two answers are pinned side by side.
/// </summary>
public sealed class AccessClearedPolicyTests
{
    private static readonly Guid Workspace = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70");
    private static readonly Guid Project = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e71");
    private const string Key = "git:git.example.invalid/team/shop";
    private static readonly AccessOsRules None = new([], [], []);
    private static readonly AccessLayer Ceiling = AccessPolicyMath.Ceiling([], [], [], shell: true);

    private static IReadOnlyList<string> FoldersOf(IEnumerable<string> keys) => [.. keys.Select(k => "/home/dev/" + k[(k.LastIndexOf('/') + 1)..])];

    private static StoredAccessPolicy Tombstone()
    {
        var document = new AccessPolicyDocument(AccessPolicyRules.Format, [Key], false, None, None, None, false, true);
        var message = new AccessPolicyMessage(Project, Workspace, 4, document, AccessPolicySigning.Hash(document), null, null);
        return new(Project.ToString("D"), Workspace.ToString("D"), message.Version, JsonSerializer.Serialize(message, ApiClient.Json), false,
            new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero), null, null);
    }

    [Fact]
    public void A_cleared_policy_still_holds_its_repository_to_the_default_layer_with_the_switch_off_where_a_repository_never_covered_gets_the_ceiling_alone()
    {
        var judged = AccessPolicyVerifier.Judge([Tombstone()], ApproversPolicy.Off, Workspace);
        var covering = AccessLayers.Covering(judged, [Key]);

        var cleared = AccessLayers.Compose(Ceiling, covering, defaultApplies: false, FoldersOf, [Key], OsKinds.Linux);
        var never = AccessLayers.Compose(Ceiling, AccessLayers.Covering(judged, ["git:git.example.invalid/team/other"]), defaultApplies: false, FoldersOf,
            ["git:git.example.invalid/team/other"], OsKinds.Linux);

        Assert.Equal(AccessPolicyStates.Default, Assert.Single(judged).State);
        Assert.Equal([AccessRefusedBy.Ceiling, AccessRefusedBy.Default], cleared.Select(l => l.Kind));
        Assert.Equal(["/home/dev/shop"], cleared[1].AllowedFolders!);
        Assert.False(cleared[1].Shell);
        Assert.Equal([AccessRefusedBy.Ceiling], never.Select(l => l.Kind));
    }
}
