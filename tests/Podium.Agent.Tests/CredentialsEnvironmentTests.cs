using Podium.Agent.Config;

namespace Podium.Agent.Tests;

/// <summary>F-331: AP_CREDENTIALS=file is a tests-only plaintext store; a build without an explicit AP_AGENT_HOME never honours it.</summary>
public sealed class CredentialsEnvironmentTests
{
    private static string Store(string? credentials, string? home) => AgentConfig.FromEnvironment(key => key switch
    {
        "AP_CREDENTIALS" => credentials,
        "AP_AGENT_HOME" => home,
        _ => null,
    }).CredentialStore;

    [Fact]
    public void The_file_store_needs_an_explicit_home()
    {
        Assert.Equal("keychain", Store("file", null));
        Assert.Equal("keychain", Store("file", ""));
        Assert.Equal("file", Store("file", "/tmp/ap-explicit"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keychain")]
    [InlineData("FILE-ish")]
    public void Anything_but_file_stays_on_the_keychain(string? credentials)
    {
        Assert.Equal("keychain", Store(credentials, "/tmp/ap-explicit"));
    }
}
