using Podium.Agent.Capture;

namespace Podium.Agent.Tests;

/// <summary>Reading a checkout's branch, commit and tag from its files, without running git (ADR-0008 §3a′).</summary>
public sealed class GitFactsTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba98";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ap-git-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string Repo(string name, string head, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(root, name);
        var git = Path.Combine(folder, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), head + "\n");
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(git, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return folder;
    }

    [Fact]
    public void A_branch_with_a_loose_ref_gives_the_branch_and_its_commit()
    {
        var facts = GitFacts.Read(Repo("a", "ref: refs/heads/feature/x", ("refs/heads/feature/x", Sha + "\n")));
        Assert.Equal(new GitHeadFacts("feature/x", Sha, null), facts);
    }

    [Fact]
    public void A_detached_head_has_a_commit_and_no_branch()
    {
        Assert.Equal(new GitHeadFacts(null, Sha, null), GitFacts.Read(Repo("b", Sha)));
    }

    [Fact]
    public void A_ref_that_only_exists_in_packed_refs_is_found_and_a_lightweight_tag_is_named()
    {
        var folder = Repo("c", "ref: refs/heads/main", ("packed-refs", $"# pack-refs\n{Sha} refs/heads/main\n{Other} refs/heads/old\n"), ("refs/tags/v1.0.0", Sha + "\n"));
        Assert.Equal(new GitHeadFacts("main", Sha, "v1.0.0"), GitFacts.Read(folder));
    }

    [Fact]
    public void A_packed_annotated_tag_is_found_through_its_peeled_line()
    {
        var folder = Repo("d", "ref: refs/heads/main", ("refs/heads/main", Sha), ("packed-refs", $"{Sha} refs/heads/main\n{Other} refs/tags/v2.0.0\n^{Sha}\n"));
        Assert.Equal("v2.0.0", GitFacts.Read(folder)!.Tag);
    }

    [Fact]
    public void When_two_tags_point_at_the_commit_the_last_by_name_is_reported()
    {
        var folder = Repo("e", Sha, ("refs/tags/v1", Sha), ("refs/tags/v2", Sha));
        Assert.Equal("v2", GitFacts.Read(folder)!.Tag);
    }

    [Fact]
    public void A_linked_worktree_reads_its_head_and_the_shared_refs_through_commondir()
    {
        var main = Repo("main", "ref: refs/heads/main", ("refs/heads/topic", Sha), ("refs/tags/t1", Sha));
        var gitdir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/topic\n");
        File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: " + gitdir + "\n");

        Assert.Equal(new GitHeadFacts("topic", Sha, "t1"), GitFacts.Read(linked));
    }

    [Theory]
    [InlineData("not a sha at all")]
    [InlineData("ref: refs/heads/../../../outside/file")]
    [InlineData("ref: heads/main")]
    [InlineData("")]
    public void A_head_that_is_not_what_the_format_says_gives_no_commit(string head)
    {
        var facts = GitFacts.Read(Repo("f", head));
        Assert.Null(facts?.HeadSha);
    }

    [Fact]
    public void A_ref_file_that_is_not_a_commit_is_dropped()
    {
        var facts = GitFacts.Read(Repo("g", "ref: refs/heads/main", ("refs/heads/main", "echo hacked; exit\n")));
        Assert.Equal(new GitHeadFacts("main", null, null), facts);
    }

    [Fact]
    public void A_folder_without_git_and_a_missing_folder_give_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "plain"));
        Assert.Null(GitFacts.Read(Path.Combine(root, "plain")));
        Assert.Null(GitFacts.Read(Path.Combine(root, "missing")));
        Assert.Null(GitFacts.Read(""));
    }

    [Fact]
    public void A_symbolic_link_in_place_of_a_ref_is_not_followed()
    {
        var folder = Repo("h", "ref: refs/heads/main");
        var outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, Sha);
        var link = Path.Combine(folder, ".git", "refs", "heads", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, outside);

        Assert.Equal(new GitHeadFacts("main", null, null), GitFacts.Read(folder));
    }

    [Fact]
    public void A_hostile_repository_configuration_is_never_executed()
    {
        var marker = Path.Combine(root, "marker");
        var folder = Repo("hostile", "ref: refs/heads/main", ("refs/heads/main", Sha),
            ("config", $"[core]\n\tfsmonitor = touch {marker}\n\thooksPath = {root}\n[alias]\n\tx = !touch {marker}\n"));
        File.WriteAllText(Path.Combine(folder, ".git", "hooks-post-checkout"), $"#!/bin/sh\ntouch {marker}\n");

        var facts = GitFacts.Read(folder);
        var info = ProjectInfo.Resolve(folder);

        Assert.Equal(new GitHeadFacts("main", Sha, null), facts);
        Assert.NotNull(info);
        Assert.False(File.Exists(marker)); // no process was started: the facts come from reading files only
    }
}
