using Podium.Agent.Capture;
using Podium.Agent.Mcp;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-340: text cut at a fixed length never leaves half of an emoji (a surrogate pair) at the cut.</summary>
public sealed class SurrogateSafeCutTests : IDisposable
{
    private const string Emoji = "😀";
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private static bool HasHalf(string text) => text.Where((c, i) => char.IsHighSurrogate(c) ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]) : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))).Any();

    [Fact]
    public void The_cut_helper_steps_back_from_the_middle_of_a_pair_and_leaves_other_text_alone()
    {
        Assert.Equal("ab", SafeText.Cut("ab" + Emoji + "c", 3));
        Assert.Equal("ab" + Emoji, SafeText.Cut("ab" + Emoji + "c", 4));
        Assert.Equal("abc", SafeText.Cut("abcdef", 3));
        Assert.Equal("abc", SafeText.Cut("abc", 3));
        Assert.Equal("", SafeText.Cut(Emoji, 1));
        Assert.Equal("", SafeText.Cut("abc", 0));
        Assert.Equal("a\uD83D", SafeText.Cut("a\uD83Dz", 2)); // a lone half is not a pair: nothing to protect
    }

    [Fact]
    public void The_note_a_model_posts_is_cut_before_a_pair_that_straddles_the_limit()
    {
        var tools = new AipodTools(home.Config, clock);
        Assert.Equal("Posted.", tools.Note("s1", new string('a', AipodTools.NoteMax - 1) + Emoji + "tail"));

        using var store = new LocalStore(home.Config.DatabasePath);
        var text = store.NextBatch(10, int.MaxValue)!.Value.Rows.Single().Event.Payload.GetProperty("text").GetString()!;
        Assert.Equal(new string('a', AipodTools.NoteMax - 1), text);
    }

    [Fact]
    public void The_preview_of_an_oversized_event_never_ends_in_half_a_pair()
    {
        // The event JSON is ASCII-escaped, so a pair is two escapes and the cut cannot split a character: this pins that it stays so.
        var config = home.Config with { EventMaxBytesDefault = 100 };
        using var store = new LocalStore(config.DatabasePath);
        new Recorder(config, store).Record(EventKinds.Note, "s1",
            new System.Text.Json.Nodes.JsonObject { ["text"] = new string('a', Recorder.PreviewChars - 10) + Emoji + Emoji + Emoji }, null, clock.GetUtcNow());

        var preview = store.NextBatch(10, int.MaxValue)!.Value.Rows.Single().Event.Payload.GetProperty("preview").GetString()!;
        Assert.False(HasHalf(preview));
        Assert.True(preview.Length <= Recorder.PreviewChars);
    }

    [Fact]
    public void The_machine_text_is_cut_before_a_pair_that_straddles_its_limit()
    {
        string Render(string note) => MachineDetailText.Render(new MachineDetail(MachineViewKit.View("web-1"), note, null, false, null, null, []));
        var before = Render("Z");
        var at = before.IndexOf("\nZ", StringComparison.Ordinal) + 1; // where the note starts
        var text = Render(new string('n', MachineDetailText.TotalMax - 1 - at) + Emoji + new string('n', 200));

        Assert.False(HasHalf(text));
        Assert.EndsWith("\n[cut]", text, StringComparison.Ordinal);
        Assert.Equal(MachineDetailText.TotalMax - 1 + "\n[cut]".Length, text.Length);
    }

    [Fact]
    public void An_envelope_attribute_is_cut_before_a_pair_that_straddles_its_limit()
    {
        var attr = RemoteEnvelope.Attr(new string('a', 199) + Emoji + "tail");
        Assert.Equal(new string('a', 199), attr);
    }

    [Fact]
    public void An_envelope_label_holds_only_ascii_so_it_cannot_be_cut_in_a_pair()
    {
        var label = RemoteEnvelope.Label(new string('a', RemoteEnvelope.LabelMax - 1) + Emoji);
        Assert.DoesNotContain(label, char.IsSurrogate);
        Assert.Equal(RemoteEnvelope.LabelMax, label.Length);
    }
}
