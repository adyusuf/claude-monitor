using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.Config;
using Xunit;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The terminal-text catalog (ADR-0014, Part A): every key has all three languages with the same placeholders, and the language is chosen as documented.</summary>
public sealed partial class StringsTests
{
    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static List<string> Keys() =>
        [.. typeof(Msg).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    private static List<int> Slots(string text) => [.. Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];

    [Fact]
    public void Every_key_has_an_entry_and_every_entry_has_a_key()
    {
        var keys = Keys();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(keys.Except(Strings.Table.Keys, StringComparer.Ordinal));
        Assert.Empty(Strings.Table.Keys.Except(keys, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_entry_is_written_in_all_three_languages()
    {
        foreach (var entry in Strings.Table.Values)
        {
            foreach (var language in Strings.Languages)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.For(language)), $"{entry.Key} has no {language} text");
            }
        }
    }

    [Fact]
    public void The_three_languages_take_the_same_placeholders_and_format_cleanly()
    {
        object?[] arguments = [.. Enumerable.Range(0, 8).Select(i => (object?)("v" + i))];
        foreach (var entry in Strings.Table.Values)
        {
            var slots = Slots(entry.En);
            Assert.True(slots.SequenceEqual(Slots(entry.Tr)), $"{entry.Key}: the Turkish text takes other placeholders");
            Assert.True(slots.SequenceEqual(Slots(entry.De)), $"{entry.Key}: the German text takes other placeholders");
            if (slots.Count == 0) continue;
            foreach (var language in Strings.Languages) Assert.NotEmpty(string.Format(CultureInfo.InvariantCulture, entry.For(language), arguments));
        }
    }

    [Fact]
    public void The_usage_text_in_English_is_the_one_the_tests_and_the_docs_know()
    {
        Assert.Equal(Cli.Usage, Strings.Table[Msg.Usage].En);
    }

    [Fact]
    public void A_login_marker_is_part_of_the_line_it_marks_in_every_language()
    {
        foreach (var language in Strings.Languages)
        {
            Assert.Contains(Strings.Table[Msg.LoginCodeMarker].For(language), Strings.Table[Msg.LoginOpen].For(language), StringComparison.Ordinal);
            Assert.Contains(Strings.Table[Msg.LoginRetryMarker].For(language), Strings.Table[Msg.LoginRetry].For(language), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, "en", "en")]
    [InlineData("tr", "en", "tr")]
    [InlineData("TR", "en", "tr")]
    [InlineData("de_DE.UTF-8", "tr", "de")]
    [InlineData("fr", "de", "de")]
    [InlineData("", "tr", "tr")]
    [InlineData("klingon", "ja", "en")]
    public void The_variable_beats_the_system_language_and_English_is_the_fallback(string? variable, string system, string expected)
    {
        Assert.Equal(expected, Strings.Resolve(variable, CultureInfo.GetCultureInfo(system)));
    }

    [Fact]
    public void This_test_assembly_speaks_English_and_a_scope_changes_one_flow_only()
    {
        Assert.Equal("en", Strings.Language);
        Assert.Equal("Nothing was moved: x.", Strings.T(Msg.HmNothingMoved, "x"));
        using (Strings.Scope("tr"))
        {
            Assert.Equal("tr", Strings.Language);
            Assert.Equal("Hiçbir şey taşınmadı: x.", Strings.T(Msg.HmNothingMoved, "x"));
        }

        using (Strings.Scope("de")) Assert.Equal("Nichts wurde verschoben: x.", Strings.T(Msg.HmNothingMoved, "x"));
        Assert.Equal("en", Strings.Language);
    }

    [Fact]
    public void An_unknown_key_comes_back_as_itself_and_the_markers_come_in_every_language()
    {
        Assert.Equal("no.such.key", Strings.T("no.such.key", 1));
        Assert.Equal(["enter the code", "kodu girin", "geben Sie den Code ein"], Strings.InAllLanguages(Msg.LoginCodeMarker));
    }

    [Fact]
    public void The_log_and_the_model_stay_English_while_the_person_gets_their_language()
    {
        using var turkish = Strings.Scope("tr");
        Assert.Contains("no reason given", Auth.AuthRefusal.Message(null), StringComparison.Ordinal);
        Assert.Contains("neden verilmedi", Auth.AuthRefusal.Describe(null), StringComparison.Ordinal);
        Assert.Contains("imzalı onaylar: kapalı", Approvals.ApproversStore.Describe(new Approvals.ApproversPolicy(false, [])), StringComparison.Ordinal);
    }
}
