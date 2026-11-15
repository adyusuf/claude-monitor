using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The repository key (ADR-0008, ADR-0020): the agent reads it from a checkout's origin URL and the API from an application's repository URL, and
/// which access policy holds a run is decided by matching these keys. Pure.
/// </summary>
public sealed class RepoKeysTests
{
    [Theory]
    [InlineData("git@GitHub.com:Org/Repo.git", "github.com/Org/Repo")]
    [InlineData("https://user:pw@github.com/Org/Repo", "github.com/Org/Repo")]
    [InlineData("https://github.com/Org/Repo.GIT/", "github.com/Org/Repo")]
    [InlineData("github.com:Org/Repo", "github.com/Org/Repo")]
    [InlineData("ssh://git@git.example.invalid:2222/team/shop.git", "git.example.invalid/team/shop")]
    [InlineData("https://git.example.invalid:8443/team/shop", "git.example.invalid/team/shop")]
    public void A_remote_url_becomes_its_lowercase_host_and_its_path_without_credentials_port_or_git_suffix(string url, string normalized) =>
        Assert.Equal(normalized, RepoKeys.NormalizeRemote(url));

    [Theory]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/.git")]
    [InlineData("not a url")]
    [InlineData("/home/dev/shop")]
    public void A_url_without_a_host_or_a_path_has_no_key(string url) => Assert.Null(RepoKeys.NormalizeRemote(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Nothing_usable_gives_no_key(string? url) => Assert.Null(RepoKeys.FromRemote(url));

    [Fact]
    public void A_key_is_the_git_prefix_and_the_normalised_remote_so_scp_and_https_forms_of_one_repository_agree()
    {
        Assert.Equal("git:github.com/Org/Repo", RepoKeys.FromRemote("git@github.com:Org/Repo.git"));
        Assert.Equal(RepoKeys.FromRemote("git@github.com:Org/Repo.git"), RepoKeys.FromRemote("https://github.com/Org/Repo"));
    }

    [Theory]
    [InlineData("git:github.com/Org/Shop", "git:github.com/org/shop", true)]
    [InlineData("git:host/café", "git:host/café", true)]
    [InlineData("git:host/CAFÉ", "git:host/café", true)]
    [InlineData("git:github.com/org/shop", "git:github.com/org/shop2", false)]
    [InlineData("git:github.com/org/shop", null, false)]
    [InlineData(null, null, false)]
    public void Two_keys_name_the_same_repository_after_nfc_and_without_case(string? a, string? b, bool same) =>
        Assert.Equal(same, RepoKeys.Same(a, b));

    [Fact]
    public void The_comparer_matches_same_and_gives_equal_keys_one_hash_so_sets_hold_a_repository_once()
    {
        var set = new HashSet<string>(RepoKeys.Comparer) { "git:host/Café", "git:host/café", "git:host/other" };

        Assert.Equal(2, set.Count);
        Assert.True(RepoKeys.Comparer.Equals(null, null));
        Assert.False(RepoKeys.Comparer.Equals("git:a", null));
        Assert.Equal(RepoKeys.Comparer.GetHashCode("git:HOST/x"), RepoKeys.Comparer.GetHashCode("git:host/X"));
    }
}
