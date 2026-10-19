using System.Collections.Frozen;
using System.Globalization;

namespace ClaudeMonitor.Agent.Config;

/// <summary>One message in the three languages. The placeholders ({0}, {1}...) must be the same in all three: a test holds every key to that.</summary>
public sealed record Entry(string Key, string En, string Tr, string De)
{
    public string For(string language) => language switch { "tr" => Tr, "de" => De, _ => En };
}

/// <summary>
/// The agent's own words for the person at the terminal (ADR-0014, Part A): usage lines, `status`, the login, install, update and migration messages, errors, and the tray's menu and window.
/// One typed key per message (<see cref="Msg"/>), three languages (English is the source and the fallback). The language is, in order: a scope (tests), the process setting (a test assembly
/// pins English), the variable <c>AIPOD_LANG</c> (en, tr or de), the operating system's UI language, else English. What the MODEL reads (tool names and texts, notices, envelopes, the machines
/// block), logs, the outcome lines of fixed actions and wire strings stay English on purpose and are not here.
/// </summary>
public static partial class Strings
{
    public static readonly IReadOnlyList<string> Languages = ["en", "tr", "de"];
    public const string LanguageVariable = "AIPOD_LANG";

    private static readonly AsyncLocal<string?> Scoped = new();
    private static string? processLanguage;
    private static readonly Lazy<FrozenDictionary<string, Entry>> TableOnce = new(Build);

    /// <summary>Every message by key; built once from the per-area tables.</summary>
    public static IReadOnlyDictionary<string, Entry> Table => TableOnce.Value;

    /// <summary>The language this call speaks.</summary>
    public static string Language => Scoped.Value ?? processLanguage ?? Resolve(Environment.GetEnvironmentVariable(LanguageVariable), CultureInfo.CurrentUICulture);

    /// <summary>The variable if it names one of our languages, else the UI culture's language if it is one of ours, else English.</summary>
    public static string Resolve(string? variable, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Normalize(variable) ?? Normalize(culture.TwoLetterISOLanguageName) ?? "en";
    }

    /// <summary>"tr", "TR", "tr-TR" and "tr_TR.UTF-8" are Turkish; anything that is not one of ours is null.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var primary = tag.Trim().Split('-', '_', '.')[0].ToLowerInvariant();
        return Languages.Contains(primary) ? primary : null;
    }

    /// <summary>Makes every call in this async flow speak <paramref name="language"/> until the scope is disposed (tests; a command that was asked for a language).</summary>
    public static IDisposable Scope(string language)
    {
        var previous = Scoped.Value;
        Scoped.Value = Normalize(language) ?? "en";
        return new Restore(() => Scoped.Value = previous);
    }

    /// <summary>Pins the language of the whole process (null: back to the variable and the system). A test assembly sets English here so the machine's own language cannot change what its tests read.</summary>
    public static void UseForProcess(string? language) => processLanguage = language is null ? null : Normalize(language) ?? "en";

    /// <summary>The message for a key in the current language with its placeholders filled; the key itself when there is none (a test finds that).</summary>
    public static string T(string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Table.TryGetValue(key, out var entry)) return key;
        var text = entry.For(Language);
        return args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, text, args) : text;
    }

    /// <summary>The text of a key in each of the three languages (English first): for a marker that a process of one language looks for in the output of another.</summary>
    public static IReadOnlyList<string> InAllLanguages(string key) => Table.TryGetValue(key, out var entry) ? [entry.En, entry.Tr, entry.De] : [key];

    private static FrozenDictionary<string, Entry> Build()
    {
        var all = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in Entries())
        {
            if (!all.TryAdd(entry.Key, entry)) throw new InvalidOperationException($"the message key {entry.Key} is defined twice");
        }

        return all.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static IEnumerable<Entry> Entries() => [.. CliEntries(), .. LoginEntries(), .. InstallEntries(), .. MigrationEntries(), .. TrayEntries()];

    private static Entry E(string key, string en, string tr, string de) => new(key, en, tr, de);

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
