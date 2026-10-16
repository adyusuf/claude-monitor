using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What the agent reports about listed project folders and how the folder list is kept (ADR-0008 §3a′).</summary>
public sealed class ProjectFactsTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly TempHome home = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "cm-pf-test-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryCredentials credentials = new();

    public void Dispose()
    {
        home.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public Dictionary<string, string> Items { get; } = [];
        public string? Read(string account) => Items.GetValueOrDefault(account);
        public void Write(string account, string secret) => Items[account] = secret;
        public void Delete(string account) => Items.Remove(account);
    }

    private sealed class Broken : ICredentialStore
    {
        public string? Read(string account) => throw new IOException("locked");
        public void Write(string account, string secret) => throw new IOException("locked");
        public void Delete(string account) => throw new IOException("locked");
    }

    private string Repo(string name, string remote = "git@host.example:team/shop.git")
    {
        var folder = Path.Combine(root, name);
        var git = Path.Combine(folder, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), Sha + "\n");
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = {remote}\n");
        return folder;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(AgentConfig config, params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(args, config, new StringReader(""), stdout, stderr, TimeProvider.System);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_listed_folder_is_reported_with_the_key_a_session_would_use_the_git_facts_and_an_hmac_not_the_path()
    {
        var folder = Repo("checkout");

        var fact = Assert.Single(new ProjectFacts(home.Config, credentials).Read([folder]));

        Assert.Equal(("git:host.example/team/shop", "shop", "main", Sha, ProjectSources.Listed), (fact.Key, fact.Name, fact.Branch, fact.HeadSha, fact.Source));
        Assert.Matches("^[0-9a-f]{64}$", fact.PathHmac!);
        Assert.DoesNotContain("checkout", fact.PathHmac, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_made_once_kept_in_the_credential_store_and_gives_the_same_hmac_next_time()
    {
        var folder = Repo("checkout");
        var first = new ProjectFacts(home.Config, credentials).Read([folder]).Single().PathHmac;
        var again = new ProjectFacts(home.Config, credentials).Read([folder]).Single().PathHmac;

        Assert.Equal(first, again);
        Assert.Matches("^[0-9a-f]{64}$", credentials.Items[ProjectFacts.KeyAccount]);
    }

    [Fact]
    public void Two_machines_with_different_keys_give_different_hmacs_for_the_same_path()
    {
        var folder = Repo("checkout");
        Assert.NotEqual(new ProjectFacts(home.Config, credentials).Read([folder]).Single().PathHmac,
            new ProjectFacts(home.Config, new MemoryCredentials()).Read([folder]).Single().PathHmac);
    }

    [Fact]
    public void A_missing_or_relative_folder_is_skipped_and_nothing_listed_reports_nothing()
    {
        var facts = new ProjectFacts(home.Config, credentials);
        Assert.Empty(facts.Read(null));
        Assert.Empty(facts.Read([]));
        Assert.Empty(facts.Read([Path.Combine(root, "missing"), "relative/path"]));
    }

    [Fact]
    public void A_credential_store_that_cannot_hold_the_key_means_no_facts_rather_than_a_crash()
    {
        Assert.Empty(new ProjectFacts(home.Config, new Broken()).Read([Repo("checkout")]));
    }

    [Fact]
    public void The_folder_list_keeps_absolute_existing_folders_once_and_removes_them_again()
    {
        var a = Repo("a");
        Assert.True(ProjectFolderList.Add(home.Config, a));
        Assert.False(ProjectFolderList.Add(home.Config, a)); // already listed
        Assert.False(ProjectFolderList.Add(home.Config, "relative")); // not absolute
        Assert.False(ProjectFolderList.Add(home.Config, Path.Combine(root, "missing"))); // does not exist
        Assert.Equal([a], ProjectFolderList.Saved(home.Config));

        Assert.True(ProjectFolderList.Remove(home.Config, a));
        Assert.False(ProjectFolderList.Remove(home.Config, a));
        Assert.Empty(ProjectFolderList.Saved(home.Config));
    }

    [Fact]
    public void The_list_holds_at_most_fifty_folders()
    {
        for (var i = 0; i < ProjectFolderList.Max; i++) Assert.True(ProjectFolderList.Add(home.Config, "/folder/" + i, _ => true));
        Assert.False(ProjectFolderList.Add(home.Config, "/folder/extra", _ => true));
    }

    [Fact]
    public async Task Config_projects_adds_lists_and_removes_through_the_command_line()
    {
        var folder = Repo("cli");
        Assert.Equal(0, (await RunAsync(home.Config, "config", "projects", "add", folder)).Code);
        Assert.Contains(folder, (await RunAsync(home.Config, "config", "projects", "list")).Out, StringComparison.Ordinal);
        Assert.Equal(2, (await RunAsync(home.Config, "config", "projects", "add", "relative")).Code);
        Assert.Equal(0, (await RunAsync(home.Config, "config", "projects", "remove", folder)).Code);
        Assert.DoesNotContain(folder, (await RunAsync(home.Config, "config", "projects", "list")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_agent_does_not_list_folders_from_the_command_line()
    {
        using var service = new TempHome(c => c with { ServiceMode = true });
        var (code, _, error) = await RunAsync(service.Config, "config", "projects", "add", Repo("svc"));
        Assert.Equal(2, code);
        Assert.Contains("policy file", error, StringComparison.Ordinal);
    }
}
