using System.Text.Json;
using Podium.Agent.Capture;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Metrics;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The commit and tag of a folder a session worked in, not only of a listed one (ADR-0008 §3a′): the hook remembers the folder, the daemon reads it.</summary>
public sealed class VisitedFoldersTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(Start);
    private readonly LocalStore store;
    private readonly string root = Path.Combine(Path.GetTempPath(), "ap-visited-" + Guid.NewGuid().ToString("N"));

    public VisitedFoldersTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string Repo(string name, string remote)
    {
        var folder = Path.Combine(root, name);
        var git = Path.Combine(folder, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), Sha + "\n");
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = {remote}\n");
        return folder;
    }

    private sealed class MemoryCredentials : Auth.ICredentialStore
    {
        private readonly Dictionary<string, string> items = [];
        public string? Read(string account) => items.GetValueOrDefault(account);
        public void Write(string account, string secret) => items[account] = secret;
        public void Delete(string account) => items.Remove(account);
    }

    [Fact]
    public void The_root_of_a_working_directory_is_the_folder_with_the_dot_git_and_nothing_outside_git()
    {
        var repo = Repo("shop", "git@host.example:team/shop.git");
        var inner = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(inner);
        Assert.Equal(repo, ProjectInfo.RootOf(inner));
        Assert.Equal(repo, ProjectInfo.RootOf(repo));
        Assert.Null(ProjectInfo.RootOf(null));
        Assert.Null(ProjectInfo.RootOf(" "));
    }

    [Fact]
    public void A_visited_folder_is_kept_once_per_project_the_newest_first_and_forgotten_after_the_time()
    {
        store.SeeFolder("git:host/a", "/srv/a", Start);
        store.SeeFolder("git:host/b", "/srv/b", Start.AddMinutes(1));
        store.SeeFolder("git:host/a", "/srv/a", Start.AddMinutes(2)); // seen again within the hour: nothing written

        Assert.Equal(["/srv/b", "/srv/a"], store.VisitedFolders(Start.AddDays(-1)));

        store.SeeFolder("git:host/a", "/srv/a-moved", Start.AddMinutes(3)); // the project moved
        Assert.Equal("/srv/a-moved", store.VisitedFolders(Start.AddDays(-1))[0]);
        Assert.Equal(1, store.ForgetFolders(Start.AddMinutes(2)));
        Assert.Equal(["/srv/a-moved"], store.VisitedFolders(Start.AddDays(-1)));
    }

    [Fact]
    public void At_most_fifty_visited_folders_are_returned()
    {
        for (var i = 0; i < 70; i++) store.SeeFolder($"git:host/p{i}", $"/srv/p{i}", Start.AddSeconds(i));
        Assert.Equal(LocalStore.FoldersMax, store.VisitedFolders(Start.AddDays(-1)).Count);
    }

    [Fact]
    public async Task A_hook_remembers_the_folder_of_the_sessions_project_and_a_working_directory_outside_git_remembers_nothing()
    {
        var repo = Repo("shop", "git@host.example:team/shop.git");
        var runner = new HookRunner(home.Config, store, clock);

        await runner.RunAsync("PreToolUse", JsonSerializer.Serialize(new { session_id = "s-1", cwd = Path.Combine(repo) }), CancellationToken.None);
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        await runner.RunAsync("PreToolUse", JsonSerializer.Serialize(new { session_id = "s-2", cwd = plain }), CancellationToken.None);

        Assert.Equal([repo], store.VisitedFolders(Start.AddDays(-1)).Where(f => f.StartsWith(root, StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void A_visited_folder_is_reported_with_the_source_session_and_a_listed_one_wins_when_both_name_the_project()
    {
        var shop = Repo("shop", "git@host.example:team/shop.git");
        var blog = Repo("blog", "git@host.example:team/blog.git");
        var shopCopy = Repo("shop-copy", "git@host.example:team/shop.git");

        var facts = new ProjectFacts(home.Config, new MemoryCredentials()).Read([shop], [shopCopy, blog]);

        Assert.Equal(2, facts.Count);
        var listed = facts.Single(f => f.Key == "git:host.example/team/shop");
        Assert.Equal((ProjectSources.Listed, Sha, "main"), (listed.Source, listed.HeadSha, listed.Branch));
        Assert.Equal(ProjectSources.Session, facts.Single(f => f.Key == "git:host.example/team/blog").Source);
        Assert.All(facts, f => Assert.Matches("^[0-9a-f]{64}$", f.PathHmac!));
    }

    [Fact]
    public void Visited_folders_alone_are_enough_and_a_vanished_one_is_skipped()
    {
        var blog = Repo("blog", "git@host.example:team/blog.git");
        var facts = new ProjectFacts(home.Config, new MemoryCredentials());
        Assert.Single(facts.Read(null, [blog, Path.Combine(root, "gone")]));
        Assert.Empty(facts.Read(null, null));
        Assert.Empty(facts.Read([], []));
    }

    [Fact]
    public async Task The_profile_carries_the_facts_of_a_folder_a_session_visited_and_forgets_the_folder_after_a_month()
    {
        var blog = Repo("blog", "git@host.example:team/blog.git");
        using var fx = new RemoteFixture();
        fx.Fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);
        fx.Store.SeeFolder("git:host.example/team/blog", blog, fx.Clock.GetUtcNow());
        var monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => new ExecPolicy(ExecLevels.Off, [], []), () => (null, []));

        await monitor.ProfileAsync(CancellationToken.None);

        var profile = JsonSerializer.Deserialize<AgentProfile>(Assert.Single(fx.Fake.Seen, s => s.Path == "/api/agent/profile").Body, ApiClient.Json)!;
        var fact = Assert.Single(profile.Projects!);
        Assert.Equal(("git:host.example/team/blog", ProjectSources.Session, Sha), (fact.Key, fact.Source, fact.HeadSha));

        fx.Clock.Advance(MachineMonitor.VisitedFor * 3);
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Empty(fx.Store.VisitedFolders(fx.Clock.GetUtcNow() - MachineMonitor.VisitedFor));
    }

    private sealed class NoMetrics : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => null;
    }
}
