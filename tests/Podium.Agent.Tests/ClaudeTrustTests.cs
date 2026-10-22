using Podium.Agent.Config;
using Podium.Agent.Daemon;

namespace Podium.Agent.Tests;

/// <summary>What Claude Code's own trust record says about a folder (ADR-0014 P2): read-only, exact folder, and unknown whenever it cannot be read.</summary>
public sealed class ClaudeTrustTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-trust-").FullName;

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private string Record => ClaudeTrust.RecordPath(home.Config);

    private void Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Record)!);
        File.WriteAllText(Record, json);
    }

    private static string Quote(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    [Fact]
    public void The_record_is_in_the_configuration_folder_when_that_is_not_the_default_and_in_the_home_when_it_is()
    {
        Assert.Equal(Path.Combine(home.Config.ClaudeConfigDir, ".claude.json"), ClaudeTrust.RecordPath(home.Config));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".claude.json"), ClaudeTrust.RecordPath(home.Config with { ClaudeConfigDir = Path.Combine(profile, ".claude") }));
        Assert.Equal(Path.Combine(profile, ".claude.json"), ClaudeTrust.RecordPath(home.Config with { ClaudeConfigDir = Path.Combine(profile, ".claude") + Path.DirectorySeparatorChar }));
    }

    [Fact]
    public void A_folder_with_the_flag_set_is_trusted_and_one_with_the_flag_off_or_missing_is_not()
    {
        Write("{\"projects\":{" + Quote(folder) + ":{\"hasTrustDialogAccepted\":true}}}");
        Assert.True(ClaudeTrust.IsTrusted(home.Config, folder));

        Write("{\"projects\":{" + Quote(folder) + ":{\"hasTrustDialogAccepted\":false}}}");
        Assert.False(ClaudeTrust.IsTrusted(home.Config, folder));

        Write("{\"projects\":{" + Quote(folder) + ":{\"allowedTools\":[]}}}");
        Assert.False(ClaudeTrust.IsTrusted(home.Config, folder));

        Write("{\"projects\":{" + Quote(folder) + ":{\"hasTrustDialogAccepted\":\"true\"}}}"); // only a real true
        Assert.False(ClaudeTrust.IsTrusted(home.Config, folder));
    }

    [Fact]
    public void A_folder_the_record_has_never_heard_of_is_not_trusted_and_trust_in_a_parent_does_not_reach_its_children()
    {
        Write("{\"projects\":{" + Quote(folder) + ":{\"hasTrustDialogAccepted\":true}}}");
        var child = Directory.CreateDirectory(Path.Combine(folder, "child")).FullName;

        Assert.False(ClaudeTrust.IsTrusted(home.Config, child));
        Write("{\"projects\":{}}");
        Assert.False(ClaudeTrust.IsTrusted(home.Config, folder));
        Write("{}");
        Assert.False(ClaudeTrust.IsTrusted(home.Config, folder));
    }

    [Fact]
    public void A_trailing_separator_and_the_other_spelling_of_a_linked_folder_are_the_same_folder()
    {
        Write("{\"projects\":{" + Quote(folder + Path.DirectorySeparatorChar) + ":{\"hasTrustDialogAccepted\":true}}}");
        Assert.True(ClaudeTrust.IsTrusted(home.Config, folder));
        if (OperatingSystem.IsWindows()) return;

        var link = Path.Combine(Path.GetTempPath(), "aipod-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, folder);
        try
        {
            Assert.True(ClaudeTrust.IsTrusted(home.Config, link)); // the record has the real folder, the agent was told the link
            Write("{\"projects\":{" + Quote(link) + ":{\"hasTrustDialogAccepted\":true}}}");
            Assert.True(ClaudeTrust.IsTrusted(home.Config, folder)); // and the other way round
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_record_that_is_missing_unreadable_or_not_an_object_says_nothing_either_way_and_so_does_a_path_that_is_not_absolute()
    {
        Assert.Null(ClaudeTrust.IsTrusted(home.Config, folder)); // no record
        Write("{ not json");
        Assert.Null(ClaudeTrust.IsTrusted(home.Config, folder));
        Write("[1,2]");
        Assert.Null(ClaudeTrust.IsTrusted(home.Config, folder));
        Write("{\"projects\":{}}");
        Assert.Null(ClaudeTrust.IsTrusted(home.Config, "relative/folder"));
        Assert.Null(ClaudeTrust.IsTrusted(home.Config, ""));
    }

    [Fact]
    public void The_record_is_only_ever_read()
    {
        const string text = "{\"projects\":{},\"other\":\"kept\"}";
        Write(text);
        var before = File.GetLastWriteTimeUtc(Record);

        _ = ClaudeTrust.IsTrusted(home.Config, folder);

        Assert.Equal(text, File.ReadAllText(Record));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Record));
    }
}
