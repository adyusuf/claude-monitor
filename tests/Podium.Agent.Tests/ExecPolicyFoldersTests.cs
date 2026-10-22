using System.Runtime.Versioning;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The admin-listed project folders of a service agent ride in the policy file (ADR-0008 §3a′) and survive a reinstall.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ExecPolicyFoldersTests : IDisposable
{
    private readonly ExecFixture fx = new();
    private string PolicyPath => Path.Combine(fx.Dir, "exec.json");

    public void Dispose() => fx.Dispose();

    [Fact]
    public void Saved_folders_come_back_and_a_relative_one_is_dropped()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, [], [], ProjectFolders: ["/srv/shop", "relative/blog", "/srv/shop", "/srv/api"]));

        var read = ExecPolicyLoader.ReadExisting(PolicyPath)!;

        Assert.Equal(["/srv/shop", "/srv/api"], read.ProjectFolders);
    }

    [Fact]
    public void A_policy_without_folders_writes_no_projects_key_and_reads_none()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, [], []));

        Assert.DoesNotContain("projects", File.ReadAllText(PolicyPath), StringComparison.Ordinal);
        Assert.Null(ExecPolicyLoader.ReadExisting(PolicyPath)!.ProjectFolders);
    }

    [Fact]
    public void A_reinstall_that_reads_the_old_policy_and_saves_it_again_keeps_the_folders()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, ["/usr/bin/true"], [], ProjectFolders: ["/srv/shop"]));
        var old = ExecPolicyLoader.ReadExisting(PolicyPath)!;

        ExecPolicyLoader.Save(PolicyPath, old with { Level = ExecLevels.Shell });

        Assert.Equal(["/srv/shop"], ExecPolicyLoader.ReadExisting(PolicyPath)!.ProjectFolders);
    }

    [Fact]
    public void An_interactive_agent_takes_its_folders_from_the_identity_list()
    {
        using var home = new TempHome(c => c with { ServiceMode = false });
        var policy = ExecPolicyLoader.Load(home.Config, ExecLevels.Argv, null, null, ["/srv/shop"]);
        Assert.Equal(["/srv/shop"], policy.ProjectFolders);
    }
}
