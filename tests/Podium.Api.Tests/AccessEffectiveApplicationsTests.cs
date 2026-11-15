using Podium.Api.AccessPolicies;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The applications a machine page lists as effective (ADR-0020 §4): the agent checks the policy and the machine's own ceiling one after the other, and each
/// pins an executable by SHA-256 when it lists one, so an entry the two pin differently is refused by one of them and must not be listed.
/// </summary>
public sealed class AccessEffectiveApplicationsTests
{
    private const string Make = "/usr/bin/make";
    private static readonly string Pin = new('a', 64);
    private static readonly string OtherPin = new('b', 64);

    [Fact]
    public void An_entry_the_policy_and_the_ceiling_pin_to_different_hashes_is_not_listed()
    {
        var applications = AccessEffective.Applications([new AccessApp(Make, Pin)], [new AccessApp(Make, OtherPin)], "linux");
        Assert.Empty(applications);
    }

    [Fact]
    public void An_entry_both_pin_to_the_same_hash_is_listed_with_it()
    {
        var applications = AccessEffective.Applications([new AccessApp(Make, Pin)], [new AccessApp(Make, Pin)], "linux");
        Assert.Equal([new AccessApp(Make, Pin)], applications);
    }

    [Fact]
    public void A_hash_only_one_side_pins_is_listed_because_the_agent_enforces_it()
    {
        Assert.Equal([new AccessApp(Make, Pin)], AccessEffective.Applications([new AccessApp(Make, Pin)], [new AccessApp(Make, null)], "linux"));
        Assert.Equal([new AccessApp(Make, OtherPin)], AccessEffective.Applications([new AccessApp(Make, null)], [new AccessApp(Make, OtherPin)], "linux"));
    }

    [Fact]
    public void A_path_the_ceiling_does_not_list_is_not_listed()
    {
        Assert.Empty(AccessEffective.Applications([new AccessApp(Make, null)], [new AccessApp("/usr/bin/cc", null)], "linux"));
    }

    [Fact]
    public void An_empty_side_is_no_limit_there()
    {
        var policy = new[] { new AccessApp(Make, Pin) };
        Assert.Equal(policy, AccessEffective.Applications(policy, [], "linux"));
        Assert.Equal(policy, AccessEffective.Applications([], policy, "linux"));
    }
}
