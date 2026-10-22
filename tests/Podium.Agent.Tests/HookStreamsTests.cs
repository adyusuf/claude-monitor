using System.Text;
using Podium.Agent;

namespace Podium.Agent.Tests;

/// <summary>A hook's JSON is UTF-8 whatever the console code page is: Turkish letters must arrive and leave unchanged (a Turkish Windows console is not UTF-8).</summary>
public sealed class HookStreamsTests
{
    private const string Turkish = "{\"prompt\":\"Şişli'de İstanbul ığüöç ĞÜŞİÖÇ\"}";

    [Fact]
    public async Task Reads_the_payload_as_UTF8()
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(Turkish));
        var (reader, _) = HookStreams.Open(stdin, new MemoryStream());

        Assert.Equal(Turkish, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Does_not_read_a_byte_order_mark_as_text()
    {
        using var stdin = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Turkish)]);
        var (reader, _) = HookStreams.Open(stdin, new MemoryStream());

        Assert.Equal(Turkish, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Writes_UTF8_without_a_byte_order_mark_and_flushes_at_once()
    {
        using var stdout = new MemoryStream();
        var (_, writer) = HookStreams.Open(new MemoryStream(), stdout);

        await writer.WriteAsync(Turkish);

        Assert.Equal(Encoding.UTF8.GetBytes(Turkish), stdout.ToArray());
    }

    [Fact]
    public void Refuses_a_missing_stream()
    {
        Assert.Throws<ArgumentNullException>(() => HookStreams.Open(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => HookStreams.Open(new MemoryStream(), null!));
    }
}
