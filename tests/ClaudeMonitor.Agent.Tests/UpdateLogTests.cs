using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What the server sends (unverified) never forges a log line or reaches the terminal; the log and the state file survive trouble.</summary>
public sealed class UpdateLogTests : IDisposable
{
    private readonly UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    public static TheoryData<string> HostileChannels() => new()
    {
        "\n2026-01-01T00:00:00Z [1] update installed: x",
        "\r\nforged",
        "\u001b[31mred\u001b[0m",
        "\u001b]0;title\u0007",
        "a\u2028b",
    };

    private string[] LogLines() => File.ReadAllLines(kit.Config.LogPath);

    private static void AssertPlain(string text)
    {
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u2028', text);
        Assert.DoesNotContain('\u0007', text);
    }

    [Theory]
    [MemberData(nameof(HostileChannels))]
    public async Task A_hostile_channel_in_a_check_leaves_one_plain_log_line_and_a_plain_message(string channel)
    {
        kit.Publish(kit.Offer(kit.Zip()) with { Channel = channel });

        var check = await kit.Updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCodes.Channel, check.Code);
        AssertPlain(check.Message);
        AssertPlain(kit.State.Detail!);
        var line = Assert.Single(LogLines()); // the refusal is exactly one line: nothing the server wrote starts another
        Assert.Contains("update refused: channel", line, StringComparison.Ordinal);
        Assert.DoesNotContain("update installed", line, StringComparison.Ordinal);
        AssertPlain((await File.ReadAllTextAsync(kit.Config.LogPath)).TrimEnd('\r', '\n'));
    }

    [Theory]
    [MemberData(nameof(HostileChannels))]
    public async Task A_hostile_channel_in_an_apply_leaves_one_plain_log_line_and_a_plain_result(string channel)
    {
        kit.InstallOld();
        var result = await kit.ApplyRefusedAsync(kit.Offer(kit.Zip()) with { Channel = channel }, UpdateCodes.Channel);

        AssertPlain(result.Message);
        AssertPlain(kit.State.Detail!);
        var line = Assert.Single(LogLines());
        Assert.Contains($"update {UpdateCodes.Channel}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("update installed", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_very_long_channel_is_cut_in_the_message()
    {
        kit.Publish(kit.Offer(kit.Zip()) with { Channel = new string('a', 500) });
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.DoesNotContain(new string('a', 33), check.Message, StringComparison.Ordinal);
        Assert.Contains(new string('a', 32), check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Printable_keeps_letters_digits_and_a_few_marks_only()
    {
        Assert.Equal("prod-1.2_x", Updater.Printable("prod-1.2_x"));
        Assert.Equal("abc", Updater.Printable("a\nb\u001b c"));
        Assert.Equal("", Updater.Printable(null));
    }

    [Fact]
    public void The_log_turns_every_line_break_in_what_it_is_given_into_a_space()
    {
        using var home = new TempHome();
        var log = new AgentLog(home.Config, TimeProvider.System);

        log.Write("a\nb\r\nc\rd");
        log.Write("second");

        var lines = File.ReadAllLines(home.Config.LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("] a b c d", lines[0], StringComparison.Ordinal); // "\r\n" is one break
        Assert.EndsWith("second", lines[1], StringComparison.Ordinal);
    }

    // ---- update-state.json ----------------------------------------------------------------------------------

    [Fact]
    public async Task Two_writers_at_once_never_throw_and_a_reader_never_sees_a_torn_file()
    {
        var config = kit.Config;
        var writers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 150; i++) UpdateState.Change(config, s => s with { Detail = $"writer {w} round {i}", Result = UpdateCodes.Available });
        })).ToArray();
        var torn = 0;
        var reads = 0;
        using var done = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                try
                {
                    if (!File.Exists(config.UpdateStatePath)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(config.UpdateStatePath));
                    reads++;
                }
                catch (JsonException)
                {
                    torn++;
                }
                catch (IOException)
                {
                    // the file was being replaced at that instant
                }
            }
        });

        await Task.WhenAll(writers);
        await done.CancelAsync();
        await reader;

        Assert.Equal(0, torn);
        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(config.UpdateStatePath));
        Assert.StartsWith("writer ", UpdateState.Load(config).Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(config.UpdateStatePath)!, "update-state.json.*.tmp")); // no temp file is left
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_replaced_does_not_throw_out_of_a_check_and_leaves_nothing_behind()
    {
        // A folder where the file belongs: the rename cannot succeed (EnsureHome resets the home's own mode, so that is no way to block it).
        Directory.CreateDirectory(kit.Config.UpdateStatePath);
        var offer = kit.Publish(kit.Offer(kit.Zip()));

        var check = await kit.Updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCodes.Available, check.Code); // the verdict is still returned
        Assert.Equal(offer, check.Offer);
        Assert.True(Directory.Exists(kit.Config.UpdateStatePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(kit.Config.UpdateStatePath)!, "update-state.json.*.tmp"));
        new UpdateState(Result: "x").Save(kit.Config); // and saving directly does not throw either
        Assert.Equal(new UpdateState(), UpdateState.Load(kit.Config)); // an unreadable state reads as empty
    }
}
