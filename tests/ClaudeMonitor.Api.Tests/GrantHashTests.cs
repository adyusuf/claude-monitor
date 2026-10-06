using System.Security.Cryptography;
using System.Text;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// The grant hash is stored (it dedupes live grants) and the run hash binds an owner's approval to the exact command, so
/// both encodings are pinned against an independent encoder: a changed byte layout would orphan stored grants.
/// </summary>
public sealed class GrantHashTests
{
    private static readonly Guid Target = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly GrantTemplate Base = new(["/usr/bin/tail", "-n", "{int:1..5000}"], "/var/log/app", 60);

    private static string Run(string mode = "argv", string[]? argv = null, string? shell = null, string? cwd = "/tmp", int timeout = 30,
        Guid? target = null) =>
        GrantMatcher.RunHash(mode, argv ?? ["/bin/ls", "-l"], shell, cwd, timeout, target ?? Target);

    // present flag 1, big-endian length, UTF-8 bytes; null is a single 0 byte
    private static void Text(List<byte> bytes, string? value)
    {
        if (value is null)
        {
            bytes.Add(0);
            return;
        }

        bytes.Add(1);
        var data = Encoding.UTF8.GetBytes(value);
        Int(bytes, data.Length);
        bytes.AddRange(data);
    }

    private static void Int(List<byte> bytes, int value) =>
        bytes.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void Strings(List<byte> bytes, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            bytes.Add(0);
            return;
        }

        bytes.Add(1);
        Int(bytes, values.Count);
        foreach (var v in values) Text(bytes, v);
    }

    private static string Sha(List<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();

    [Fact]
    public void The_grant_hash_is_the_sha256_of_a_length_prefixed_encoding_of_argv_cwd_and_timeout()
    {
        var bytes = new List<byte>();
        Text(bytes, "grant1");
        Strings(bytes, Base.Argv);
        Text(bytes, Base.Cwd);
        Int(bytes, Base.MaxTimeoutSeconds);
        Assert.Equal(Sha(bytes), GrantMatcher.Hash(Base));
        Assert.Matches("^[0-9a-f]{64}$", GrantMatcher.Hash(Base));
    }

    [Fact]
    public void The_run_hash_is_the_sha256_of_a_length_prefixed_encoding_of_the_whole_run()
    {
        var bytes = new List<byte>();
        Text(bytes, "run1");
        Text(bytes, "shell");
        Text(bytes, Target.ToString("D"));
        Strings(bytes, null);
        Text(bytes, "ls -l | wc");
        Text(bytes, null);
        Int(bytes, 45);
        Assert.Equal(Sha(bytes), GrantMatcher.RunHash("shell", null, "ls -l | wc", null, 45, Target));
    }

    [Fact]
    public void The_same_template_always_hashes_the_same_and_every_field_changes_the_hash()
    {
        var hash = GrantMatcher.Hash(Base);
        Assert.Equal(hash, GrantMatcher.Hash(new GrantTemplate([.. Base.Argv], Base.Cwd, Base.MaxTimeoutSeconds)));
        Assert.NotEqual(hash, GrantMatcher.Hash(Base with { Cwd = "/var/log/apps" }));
        Assert.NotEqual(hash, GrantMatcher.Hash(Base with { MaxTimeoutSeconds = 61 }));
        Assert.NotEqual(hash, GrantMatcher.Hash(Base with { Argv = ["/usr/bin/tail", "-n", "{int:1..5001}"] }));
        Assert.NotEqual(hash, GrantMatcher.Hash(Base with { Argv = ["/usr/bin/tail", "-n"] }));
        Assert.NotEqual(hash, GrantMatcher.Hash(Base with { Argv = ["/usr/bin/tail", "{int:1..5000}", "-n"] }));
    }

    [Fact]
    public void Two_different_splits_of_the_same_characters_do_not_collide()
    {
        Assert.NotEqual(GrantMatcher.Hash(Base with { Argv = ["ab", "c"] }), GrantMatcher.Hash(Base with { Argv = ["a", "bc"] }));
        Assert.NotEqual(GrantMatcher.Hash(Base with { Argv = ["abc"] }), GrantMatcher.Hash(Base with { Argv = ["ab", "c"] }));
        Assert.NotEqual(GrantMatcher.Hash(Base with { Argv = ["/x", "a"], Cwd = "/b" }), GrantMatcher.Hash(Base with { Argv = ["/x"], Cwd = "/ab" }));
        Assert.NotEqual(Run(argv: ["ab", "c"]), Run(argv: ["a", "bc"]));
        Assert.NotEqual(Run(argv: ["a", ""]), Run(argv: ["a"]));
    }

    [Fact]
    public void Every_field_of_a_run_changes_its_hash()
    {
        var hash = Run();
        Assert.Equal(hash, Run());
        Assert.NotEqual(hash, Run(mode: "shell"));
        Assert.NotEqual(hash, Run(argv: ["/bin/ls", "-a"]));
        Assert.NotEqual(hash, Run(argv: ["/bin/ls"]));
        Assert.NotEqual(hash, Run(shell: "ls"));
        Assert.NotEqual(hash, Run(cwd: "/tmp2"));
        Assert.NotEqual(hash, Run(cwd: null));
        Assert.NotEqual(hash, Run(timeout: 31));
        Assert.NotEqual(hash, Run(target: Guid.NewGuid()));
    }

    [Fact]
    public void A_missing_value_and_an_empty_one_hash_differently()
    {
        Assert.NotEqual(Run(cwd: null), Run(cwd: ""));
        Assert.NotEqual(Run(shell: null), Run(shell: ""));
        Assert.NotEqual(GrantMatcher.RunHash("argv", null, null, null, 30, Target), GrantMatcher.RunHash("argv", [], null, null, 30, Target));
    }

    [Fact]
    public void A_grant_hash_and_a_run_hash_never_stand_for_each_other()
    {
        var grant = GrantMatcher.Hash(new GrantTemplate(["/bin/ls"], "/tmp", 30));
        Assert.NotEqual(grant, GrantMatcher.RunHash("argv", ["/bin/ls"], null, "/tmp", 30, Target));
    }

    [Fact]
    public void Non_ascii_text_hashes_by_its_utf8_bytes()
    {
        Assert.NotEqual(Run(argv: ["/bin/echo", "é"]), Run(argv: ["/bin/echo", "é"]));
        var bytes = new List<byte>();
        Text(bytes, "run1");
        Text(bytes, "argv");
        Text(bytes, Target.ToString("D"));
        Strings(bytes, ["/bin/echo", "é"]);
        Text(bytes, null);
        Text(bytes, "/tmp");
        Int(bytes, 30);
        Assert.Equal(Sha(bytes), Run(argv: ["/bin/echo", "é"]));
    }
}
