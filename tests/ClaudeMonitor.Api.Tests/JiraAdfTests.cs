using System.Text.Json;
using ClaudeMonitor.Api.Integrations;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Jira's rich text to plain text and back (ADR-0015 §4): what is kept, what is dropped, and the limits.</summary>
public sealed class JiraAdfTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    private static string Text(string line) => $$"""{"type":"text","text":"{{line}}"}""";

    private static string Paragraph(params string[] children) => $$"""{"type":"paragraph","content":[{{string.Join(',', children)}}]}""";

    private static string Document(params string[] blocks) => $$"""{"type":"doc","version":1,"content":[{{string.Join(',', blocks)}}]}""";

    [Fact]
    public void Paragraphs_become_lines()
    {
        var text = JiraAdf.ToText(Doc(Document(Paragraph(Text("Hello")), Paragraph(Text("World")))), 1000);

        Assert.Equal("Hello\nWorld", text);
    }

    [Fact]
    public void List_items_get_a_dash_and_a_mention_keeps_its_text()
    {
        var list = """{"type":"bulletList","content":[{"type":"listItem","content":[PARA1]},{"type":"listItem","content":[PARA2]}]}"""
            .Replace("PARA1", Paragraph(Text("one")), StringComparison.Ordinal).Replace("PARA2", Paragraph(Text("two")), StringComparison.Ordinal);
        var mention = Paragraph(Text("Hi "), """{"type":"mention","attrs":{"id":"x","text":"@Ada"}}""", Text("!"));

        var lines = JiraAdf.ToText(Doc(Document(list, mention)), 1000)!.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["- one", "- two", "Hi @Ada!"], lines);
    }

    [Fact]
    public void A_hard_break_is_a_line_break_and_media_and_unknown_nodes_are_dropped()
    {
        var withBreak = Paragraph(Text("a"), """{"type":"hardBreak"}""", Text("b"));
        var media = """{"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"f","type":"file"}}]}""";
        var emoji = Paragraph("""{"type":"emoji","attrs":{"shortName":":smile:"}}""", Text("ok"));

        Assert.Equal("a\nb\nok", JiraAdf.ToText(Doc(Document(withBreak, media, emoji)), 1000));
    }

    [Fact]
    public void A_nested_document_is_read_to_a_depth_limit_and_no_further()
    {
        static string Nest(int levels, string leaf)
        {
            var json = leaf;
            for (var i = 0; i < levels; i++) json = $$"""{"type":"blockquote","content":[{{json}}]}""";
            return Document(json);
        }

        // The text node sits two levels below the last block quote (the quote, then the paragraph, then the text).
        Assert.Equal("deep enough", JiraAdf.ToText(Doc(Nest(5, Paragraph(Text("deep enough")))), 1000));
        Assert.Equal("last one read", JiraAdf.ToText(Doc(Nest(JiraAdf.DepthMax - 2, Paragraph(Text("last one read")))), 1000));
        Assert.Null(JiraAdf.ToText(Doc(Nest(JiraAdf.DepthMax - 1, Paragraph(Text("one too deep")))), 1000));
        Assert.Null(JiraAdf.ToText(Doc(Nest(JiraAdf.DepthMax + 2, Paragraph(Text("far too deep")))), 1000)); // dropped, and no stack overflow
    }

    [Fact]
    public void The_text_is_cut_to_the_cap_cleaned_of_control_characters_and_trimmed()
    {
        var long100 = new string('x', 100);

        Assert.Equal(10, JiraAdf.ToText(Doc(Document(Paragraph(Text(long100)))), 10)!.Length);
        Assert.Equal("a b", JiraAdf.ToText(Doc(Document(Paragraph(Text("  a\\u0000 b  ")))), 100));
        Assert.Null(JiraAdf.ToText(Doc(Document(Paragraph(Text("   ")))), 100));
        Assert.Null(JiraAdf.ToText(Doc(Document()), 100));
    }

    [Fact]
    public void A_plain_string_passes_through_and_anything_else_is_nothing()
    {
        Assert.Equal("plain text", JiraAdf.ToText(Doc("\"plain text\""), 100));
        Assert.Equal("plai", JiraAdf.ToText(Doc("\"plain text\""), 4));
        Assert.Null(JiraAdf.ToText(Doc("42"), 100));
        Assert.Null(JiraAdf.ToText(Doc("null"), 100));
        Assert.Null(JiraAdf.ToText(Doc("[]"), 100));
        Assert.Null(JiraAdf.ToText(Doc("""{"type":"doc","content":"not an array"}"""), 100));
    }

    [Fact]
    public void Text_becomes_one_paragraph_per_line()
    {
        var doc = JsonSerializer.SerializeToElement(JiraAdf.FromText("first\r\nsecond\n\nfourth"));

        Assert.Equal("doc", doc.GetProperty("type").GetString());
        Assert.Equal(1, doc.GetProperty("version").GetInt32());
        var paragraphs = doc.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(4, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.Equal("paragraph", p.GetProperty("type").GetString()));
        Assert.Equal("first", paragraphs[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(paragraphs[2].TryGetProperty("content", out _)); // a blank line is an empty paragraph
        Assert.Equal("fourth", paragraphs[3].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void An_empty_or_missing_text_is_an_empty_document_and_text_survives_the_round_trip()
    {
        Assert.Equal(1, JsonSerializer.SerializeToElement(JiraAdf.FromText(null)).GetProperty("content").GetArrayLength());
        Assert.Equal(1, JsonSerializer.SerializeToElement(JiraAdf.FromText("")).GetProperty("content").GetArrayLength());
        const string original = "line one\n\nline three";

        var back = JiraAdf.ToText(JsonSerializer.SerializeToElement(JiraAdf.FromText(original)), 1000);

        Assert.Equal(original, back);
    }
}
