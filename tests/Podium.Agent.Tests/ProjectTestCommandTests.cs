using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The test command of a project folder, set at the machine and reported with the folder (ADR-0011 §7a).</summary>
public sealed class ProjectTestCommandTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string[] Command = ["/usr/bin/dotnet", "test", "--no-build"];
    private readonly TempHome home = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "ap-ptc-test-" + Guid.NewGuid().ToString("N"));

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

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(args, home.Config, new StringReader(""), stdout, stderr, TimeProvider.System);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_command_is_set_read_back_and_cleared_by_folder()
    {
        var folder = Repo("checkout");

        Assert.True(ProjectTestCommands.Set(home.Config, folder + "/", Command));
        Assert.Equal(Command, ProjectTestCommands.Saved(home.Config)[folder]);
        Assert.True(ProjectTestCommands.Clear(home.Config, folder));
        Assert.Empty(ProjectTestCommands.Saved(home.Config));
        Assert.False(ProjectTestCommands.Clear(home.Config, folder));
    }

    [Fact]
    public void A_command_that_is_not_an_argv_with_an_absolute_program_in_an_existing_folder_is_refused()
    {
        var folder = Repo("checkout");

        Assert.False(ProjectTestCommands.Set(home.Config, folder, ["dotnet", "test"])); // not an absolute program
        Assert.False(ProjectTestCommands.Set(home.Config, folder, []));
        Assert.False(ProjectTestCommands.Set(home.Config, folder, ["/usr/bin/dotnet", "te\nst"]));
        Assert.False(ProjectTestCommands.Set(home.Config, folder, [.. Enumerable.Repeat("/usr/bin/x", ProjectTestRules.MaxElements + 1)]));
        Assert.False(ProjectTestCommands.Set(home.Config, Path.Combine(root, "gone"), Command));
        Assert.False(ProjectTestCommands.Set(home.Config, "relative/dir", Command));
        Assert.Empty(ProjectTestCommands.Saved(home.Config));
    }

    [Fact]
    public void The_facts_of_a_folder_carry_its_command_and_path_and_other_folders_carry_neither()
    {
        var shop = Repo("checkout");
        var blog = Repo("blog", "git@host.example:team/blog.git");
        var tests = new Dictionary<string, List<string>> { [shop] = [.. Command] };

        var facts = new ProjectFacts(home.Config, new MemoryCredentials()).Read([shop], [blog], tests);

        var withTest = Assert.Single(facts, f => f.TestArgv is not null);
        Assert.Equal(Command, withTest.TestArgv);
        Assert.Equal(shop, withTest.TestCwd);
        Assert.Matches("^[0-9a-f]{64}$", withTest.PathHmac!);
        var other = Assert.Single(facts, f => f.TestArgv is null);
        Assert.Null(other.TestCwd);
    }

    [Fact]
    public async Task The_config_verb_sets_and_clears_a_command_and_refuses_a_bad_one()
    {
        var folder = Repo("checkout");

        var set = await RunAsync("config", "projects", "test", folder, "--", "/usr/bin/dotnet", "test");
        Assert.Equal(0, set.Code);
        Assert.Equal(["/usr/bin/dotnet", "test"], ProjectTestCommands.Saved(home.Config)[folder]);

        var bad = await RunAsync("config", "projects", "test", folder, "--", "dotnet", "test");
        Assert.Equal(2, bad.Code);
        Assert.Contains("not set", bad.Err, StringComparison.Ordinal);
        Assert.Equal(["/usr/bin/dotnet", "test"], ProjectTestCommands.Saved(home.Config)[folder]); // unchanged

        Assert.Equal(0, (await RunAsync("config", "projects", "test", folder, "--clear")).Code);
        Assert.Empty(ProjectTestCommands.Saved(home.Config));
        Assert.Equal(0, (await RunAsync("config", "projects", "test", folder, "--clear")).Code); // nothing left to clear: says so, not an error
    }
}
