using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>Which names the MCP tools go by: the old <c>monitor_*</c> (the default, so every plugin and script that exists keeps working) or <c>aipod_*</c>.</summary>
public enum ToolNameMode
{
    Legacy,
    Aipod,
}

/// <summary>
/// The tool names in one place (ADR-0012 step 3). The plugin decides the mode for the processes it starts (`aipod mcp --names aipod`, or
/// <c>AIPOD_TOOL_NAMES=aipod</c> for the hooks); with nothing said the names are the old ones. Every text that names a tool for the model is built here, so a
/// session never reads the name of a tool it does not have. Only the closed list of tool names is ever rewritten: other words that start with
/// <c>monitor_</c> (a database called monitor_prod in a script's output) are never touched.
/// </summary>
public static class McpToolNames
{
    public const string LegacyPrefix = "monitor_";
    public const string AipodPrefix = "aipod_";

    /// <summary>The tools, without their prefix. A test keeps this list equal to what is registered.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "status", "machines", "machine", "metrics", "alerts", "ask", "ask_result", "answer", "grants", "request_grant", "jobs", "job_propose", "job_run",
        "run", "run_result", "run_cancel", "run_script", "note", "inbox_read",
    ];

    private static readonly Regex LegacyName = new(
        $@"\b{Regex.Escape(LegacyPrefix)}({string.Join('|', All.OrderByDescending(n => n.Length).Select(Regex.Escape))})\b", RegexOptions.CultureInvariant);

    private static readonly Regex Placeholder = new(
        $@"\{{tool:({string.Join('|', All.OrderByDescending(n => n.Length).Select(Regex.Escape))})\}}", RegexOptions.CultureInvariant);

    private static readonly AsyncLocal<ToolNameMode?> Scoped = new();
    private static ToolNameMode processMode = ToolNameMode.Legacy;

    /// <summary>The mode of this process, or of the current test's scope (<see cref="Scope"/>).</summary>
    public static ToolNameMode Mode => Scoped.Value ?? processMode;

    /// <summary>Sets the mode of the process; done once, before anything is built.</summary>
    public static void Use(ToolNameMode mode) => processMode = mode;

    /// <summary>Sets the mode for the current logical call only (tests: other tests running in parallel are not affected).</summary>
    public static IDisposable Scope(ToolNameMode mode)
    {
        var before = Scoped.Value;
        Scoped.Value = mode;
        return new Restore(() => Scoped.Value = before);
    }

    /// <summary>"aipod" is the new names; anything else (empty, "legacy", unknown) is the old ones: the safe default.</summary>
    public static ToolNameMode Parse(string? text) =>
        string.Equals(text?.Trim(), "aipod", StringComparison.OrdinalIgnoreCase) ? ToolNameMode.Aipod : ToolNameMode.Legacy;

    /// <summary>The word stored and passed on the command line for a mode.</summary>
    public static string Word(ToolNameMode mode) => mode == ToolNameMode.Aipod ? "aipod" : "legacy";

    public static string Prefix(ToolNameMode mode) => mode == ToolNameMode.Aipod ? AipodPrefix : LegacyPrefix;

    /// <summary>The name of a tool ("ask_result") in the current mode.</summary>
    public static string Name(string tool) => Name(Mode, tool);

    public static string Name(ToolNameMode mode, string tool) => Prefix(mode) + tool;

    /// <summary>
    /// A STATIC text written with the old names, in the current mode. Only for text that holds nothing from outside (a template, a rule, a description);
    /// text with data in it names its tools through <see cref="Name(string)"/> instead.
    /// </summary>
    public static string Text(string template) => Text(Mode, template);

    public static string Text(ToolNameMode mode, string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return mode == ToolNameMode.Aipod ? LegacyName.Replace(template, m => AipodPrefix + m.Groups[1].Value) : template;
    }

    /// <summary>A placeholder for a tool name, for text kept in the database by a process that does not know the mode of the session that will read it.</summary>
    public static string Slot(string tool) => "{tool:" + tool + "}";

    /// <summary>Fills the placeholders of <see cref="Slot"/> in the mode of the reader. Other braces are left alone.</summary>
    public static string Fill(string text) => Fill(Mode, text);

    public static string Fill(ToolNameMode mode, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Placeholder.Replace(text, m => Name(mode, m.Groups[1].Value));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
