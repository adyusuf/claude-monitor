using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class CaptureTests
{
    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "private_key")]
    // Synthetic values, split so that no secret-shaped string sits in the source (the secret scan stays on).
    [InlineData("key AKIA" + "ABCDEFGHIJKLMNOP here", "aws_key")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV", "github_token")]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "anthropic_key")]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz", "openai_key")]
    [InlineData("xoxb-1234567890-abcdef", "slack_token")]
    [InlineData("AIza" + "SyA1234567890abcdefghijklmnopqrstuv", "google_key")]
    [InlineData("eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0" + "." + "dozjgNryP4J3jVmNHl0w5N", "jwt")]
    [InlineData("Host=db;Password=s3cretpw;Database=x", "connection_password")]
    [InlineData("api_key: \"abcdef0123456789\"", "assigned_secret")]
    public void Known_secret_shapes_are_masked(string text, string kind)
    {
        var masked = Masker.MaskText(text);
        Assert.Contains($"[masked:{kind}]", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_and_non_strings_pass_unchanged()
    {
        var deep = "tok" + "en=" + "abcdefgh12345";
        var node = JsonNode.Parse("{\"a\":\"just text\",\"n\":42,\"b\":true,\"list\":[\"ok\",{\"deep\":\"" + deep + "\"}],\"none\":null}");
        var masked = Masker.Mask(node)!;
        Assert.Equal("just text", masked["a"]!.GetValue<string>());
        Assert.Equal(42, masked["n"]!.GetValue<int>());
        Assert.Equal("token=[masked:assigned_secret]", masked["list"]![1]!["deep"]!.GetValue<string>());
        Assert.Null(masked["none"]);
    }

    [Theory]
    [InlineData("git@GitHub.com:Org/Repo.git", "github.com/Org/Repo")]
    [InlineData("https://user:pw@gitlab.example.com/group/sub/proj", "gitlab.example.com/group/sub/proj")]
    [InlineData("ssh://git@host.example:2222/a/b.git", "host.example/a/b")]
    [InlineData("not a url", null)]
    [InlineData("https://host.example/", null)]
    public void Remotes_normalise_to_host_and_path(string url, string? expected) =>
        Assert.Equal(expected, ProjectInfo.NormalizeRemote(url));

    [Fact]
    public void A_checkout_gives_its_remote_name_and_branch_and_a_worktree_finds_the_common_config()
    {
        var root = Directory.CreateTempSubdirectory("cm-proj-").FullName;
        try
        {
            var git = Directory.CreateDirectory(Path.Combine(root, "main", ".git")).FullName;
            File.WriteAllText(Path.Combine(git, "config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@github.com:acme/widgets.git\n");
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/feature/x\n");
            var sub = Directory.CreateDirectory(Path.Combine(root, "main", "src", "deep")).FullName;
            var info = ProjectInfo.Resolve(sub)!;
            Assert.Equal(("git:github.com/acme/widgets", "widgets", "feature/x"), (info.Key, info.Name, info.Branch));

            var wtGit = Directory.CreateDirectory(Path.Combine(git, "worktrees", "wt")).FullName;
            File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(wtGit, "HEAD"), "0123456789abcdef\n");
            var wt = Directory.CreateDirectory(Path.Combine(root, "wt")).FullName;
            File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {wtGit}\n");
            var worktree = ProjectInfo.Resolve(wt)!;
            Assert.Equal(("git:github.com/acme/widgets", "0123456"), (worktree.Key, worktree.Branch));

            var plain = Directory.CreateDirectory(Path.Combine(root, "plain-folder")).FullName;
            Assert.Null(ProjectInfo.OriginUrl(Path.Combine(plain, "config")));
            Assert.Null(ProjectInfo.HeadBranch(Path.Combine(plain, "HEAD")));
            Assert.Null(ProjectInfo.Resolve(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_checkout_without_origin_is_keyed_by_a_hash_of_its_folder()
    {
        var root = Directory.CreateTempSubdirectory("cm-proj-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var info = ProjectInfo.Resolve(root)!;
            Assert.StartsWith("dir:", info.Key, StringComparison.Ordinal);
            Assert.DoesNotContain(root, info.Key, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_tailer_takes_new_complete_lines_and_counts_usage_once_per_message()
    {
        using var home = new TempHome();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var store = new LocalStore(home.Config.DatabasePath);
        var transcript = Path.Combine(home.Dir, "t.jsonl");
        var assistant = """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":3,"cache_creation":{"ephemeral_5m_input_tokens":7,"ephemeral_1h_input_tokens":2}}}}""";
        File.WriteAllText(transcript, assistant + "\n" + assistant + "\nnot json\n[1]\n{\"type\":\"user\"");
        store.TrackTranscript(new TranscriptCursor("s1", HarnessKinds.ClaudeCode, transcript, 0, "k", "n", "main"), clock.GetUtcNow());
        var tailer = new TranscriptTailer(home.Config, store, clock);
        Assert.Equal(2, tailer.RunOnce());
        var events = store.NextBatch(100, int.MaxValue)!.Value.Rows.Select(r => r.Event).ToList();
        Assert.Equal([EventKinds.Transcript, EventKinds.Usage, EventKinds.Transcript], events.Select(e => e.Kind));
        var usage = events[1].Payload;
        Assert.Equal(7, usage.GetProperty("cacheWrite5mTokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("cacheWrite1hTokens").GetInt64());

        File.AppendAllText(transcript, "}\n");
        Assert.Equal(1, tailer.RunOnce()); // the partial last line, now complete
        File.WriteAllText(transcript, "{\"type\":\"summary\"}\n"); // replaced by a shorter file
        Assert.Equal(1, tailer.RunOnce());
        File.Delete(transcript);
        Assert.Equal(0, tailer.RunOnce());
    }

    [Fact]
    public void Old_style_usage_counts_cache_creation_as_five_minute_writes()
    {
        var usage = TranscriptTailer.Usage("m", "x", JsonNode.Parse("""{"input_tokens":1,"cache_creation_input_tokens":9}""")!.AsObject());
        Assert.Equal(9, usage["cacheWrite5mTokens"]!.GetValue<long>());
        Assert.Equal(0, usage["cacheWrite1hTokens"]!.GetValue<long>());
    }
}
