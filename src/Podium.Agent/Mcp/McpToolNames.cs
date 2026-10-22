using System.Text.RegularExpressions;

namespace Podium.Agent.Mcp;

/// <summary>
/// The tool names in one place (ADR-0016): every tool is called <c>aipod_&lt;name&gt;</c>. Text kept in the database by a process that does not know the
/// session that will read it names a tool by a slot (<see cref="Slot"/>), which the SessionStart hook fills in (<see cref="Fill"/>), so a session never
/// reads the name of a tool it does not have.
/// </summary>
public static class McpToolNames
{
    public const string Prefix = "aipod_";

    /// <summary>The word the agent's store keeps beside a session's inbox to say which tool names that session has; only one exists now.</summary>
    public const string StoredWord = "aipod";

    /// <summary>The tools, without their prefix. A test keeps this list equal to what is registered.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "status", "machines", "machine", "metrics", "alerts", "ask", "ask_result", "answer", "grants", "request_grant", "jobs", "job_propose", "job_run",
        "run", "run_result", "run_cancel", "run_script", "note", "inbox_read",
    ];

    private static readonly Regex Placeholder = new(
        $@"\{{tool:({string.Join('|', All.OrderByDescending(n => n.Length).Select(Regex.Escape))})\}}", RegexOptions.CultureInvariant);

    /// <summary>The name of a tool ("ask_result").</summary>
    public static string Name(string tool) => Prefix + tool;

    /// <summary>A placeholder for a tool name, for text kept in the database.</summary>
    public static string Slot(string tool) => "{tool:" + tool + "}";

    /// <summary>Fills the placeholders of <see cref="Slot"/>. Other braces are left alone.</summary>
    public static string Fill(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Placeholder.Replace(text, m => Name(m.Groups[1].Value));
    }
}
