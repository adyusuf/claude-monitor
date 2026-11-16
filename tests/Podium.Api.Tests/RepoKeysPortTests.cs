using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The repository key leaves out the port and the scheme (F-295, kept as intended, ADR-0020 §4): the same repository over ssh and over https is one key,
/// and so is the same path on two ports of one host, so they share one access policy. Different paths on one host stay different.
/// </summary>
public sealed class RepoKeysPortTests
{
    private const string Shop = "git:git.example.invalid/team/shop";

    [Theory]
    [InlineData("ssh://git@git.example.invalid:2222/team/shop.git")]
    [InlineData("ssh://git@git.example.invalid:22/team/shop")]
    [InlineData("https://git.example.invalid:8443/team/shop")]
    [InlineData("https://git.example.invalid/team/shop")]
    [InlineData("git@git.example.invalid:team/shop.git")]
    public void Every_port_and_scheme_of_one_host_and_path_gives_the_same_key(string url) => Assert.Equal(Shop, RepoKeys.FromRemote(url));

    [Fact]
    public void Two_ports_of_one_host_and_path_count_as_one_repository_and_a_different_path_does_not()
    {
        var onOnePort = RepoKeys.FromRemote("https://git.example.invalid:8443/team/shop");
        var onAnother = RepoKeys.FromRemote("ssh://git@git.example.invalid:2222/team/shop.git");
        var otherPath = RepoKeys.FromRemote("https://git.example.invalid:8443/team/other");

        Assert.True(RepoKeys.Same(onOnePort, onAnother));
        Assert.False(RepoKeys.Same(onOnePort, otherPath));
        Assert.Single(new HashSet<string>(RepoKeys.Comparer) { onOnePort!, onAnother! });
    }
}
