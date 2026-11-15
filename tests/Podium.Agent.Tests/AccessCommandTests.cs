using System.Runtime.Versioning;
using Podium.Agent.Access;
using Podium.Agent.Auth;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// <c>aipod config access</c> (ADR-0020 §4 rule 2): the machine's admin narrows the ceiling by command, never by editing a file. Entries must exist and
/// are kept as real paths, an allowed folder is never a system or home root, a hash is lowercase hex; a refused change leaves the ceiling as it was.
/// These run on an interactive agent, whose ceiling is in agent.json.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class AccessCommandTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ExecFixture fx = new();

    public void Dispose()
    {
        fx.Dispose();
        home.Dispose();
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] verb)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AccessCommand.RunAsync(["config", AccessCommand.Verb, .. verb], home.Config, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private Identity? Saved => Identity.Peek(home.Config);

    private static string Real(string path) => PathIdentity.Resolve(path, ExecFixture.Os, allowMissing: false)!;

    [Fact]
    public async Task An_existing_folder_is_allowed_or_denied_as_its_real_path_once()
    {
        if (!ExecFixture.Unix) return;

        Assert.Equal(0, (await RunAsync("allow", fx.Root)).Code);
        Assert.Equal(0, (await RunAsync("allow", fx.Root + "/")).Code);
        Assert.Equal(0, (await RunAsync("deny", fx.Outside)).Code);

        Assert.Equal([Real(fx.Root)], Saved!.AccessAllowedRoots!);
        Assert.Equal([Real(fx.Outside)], Saved!.AccessDeniedRoots!);
    }

    [Fact]
    public async Task A_missing_folder_and_a_forbidden_root_are_refused_and_change_nothing()
    {
        if (!ExecFixture.Unix) return;
        var toEtc = Path.Combine(fx.Dir, "etc-link");
        Directory.CreateSymbolicLink(toEtc, "/etc");

        Assert.Equal(2, (await RunAsync("allow", Path.Combine(fx.Dir, "missing"))).Code);
        Assert.Equal(2, (await RunAsync("allow", "/")).Code);
        Assert.Equal(2, (await RunAsync("allow", toEtc)).Code);
        Assert.Equal(0, (await RunAsync("deny", toEtc)).Code); // denying a system folder only narrows

        Assert.Null(Saved!.AccessAllowedRoots);
    }

    [Fact]
    public async Task An_application_is_kept_by_its_real_path_with_a_lowercase_hash_and_a_bad_hash_or_a_non_file_is_refused()
    {
        if (!ExecFixture.Unix) return;
        var exe = ExecFixture.File(fx.Root, "tool");

        Assert.Equal(0, (await RunAsync("app", exe, "--sha256", new string('A', 64))).Code);
        Assert.Equal(2, (await RunAsync("app", exe, "--sha256", new string('g', 64))).Code);
        Assert.Equal(2, (await RunAsync("app", exe, "--sha256", new string('a', 63))).Code);
        Assert.Equal(2, (await RunAsync("app", fx.Root)).Code);
        Assert.Equal(2, (await RunAsync("app", exe, "--md5", "x")).Code);

        Assert.Equal([new AccessApp(Real(exe), new string('a', 64))], Saved!.AccessExecutables!);
    }

    [Fact]
    public async Task Remove_takes_an_entry_out_by_its_path_an_unknown_path_changes_nothing_and_clear_empties_the_ceiling()
    {
        if (!ExecFixture.Unix) return;
        await RunAsync("allow", fx.Root);
        await RunAsync("deny", fx.Outside);

        Assert.Equal(1, (await RunAsync("remove", Path.Combine(fx.Dir, "never-added"))).Code);
        Assert.Equal(0, (await RunAsync("remove", fx.Root)).Code);
        Assert.Null(Saved!.AccessAllowedRoots);
        Assert.Equal([Real(fx.Outside)], Saved!.AccessDeniedRoots!);

        Assert.Equal(0, (await RunAsync("clear")).Code);
        Assert.Null(Saved!.AccessDeniedRoots);
    }

    [Fact]
    public async Task Show_lists_the_ceiling_and_an_unknown_verb_or_a_missing_argument_prints_the_usage()
    {
        if (!ExecFixture.Unix) return;
        await RunAsync("allow", fx.Root);

        var show = await RunAsync("show");

        Assert.Equal(0, show.Code);
        Assert.Contains("  + " + Real(fx.Root), show.Out, StringComparison.Ordinal);
        Assert.Equal(2, (await RunAsync("widen", fx.Root)).Code);
        Assert.Equal(2, (await RunAsync("allow")).Code);
        Assert.Equal(2, (await RunAsync()).Code);
    }
}
