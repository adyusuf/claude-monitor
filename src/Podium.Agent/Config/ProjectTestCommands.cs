using Podium.Agent.Auth;
using Podium.Contracts;

namespace Podium.Agent.Config;

/// <summary>
/// The test command of a project folder, set by the person at the machine (<c>aipod config projects test &lt;folder&gt; -- &lt;program&gt; [args...]</c>, ADR-0011 §7a).
/// An argv list and no shell. The agent reports it with the folder so Podium can offer "start the test": the run still waits for its owner's approval, which shows
/// exactly this command and folder. Nothing about it can be set from the web.
/// </summary>
public static class ProjectTestCommands
{
    public const int MaxCommands = ProjectFolderList.Max;
    public const int MaxElements = ProjectTestRules.MaxElements;
    public const int MaxElementLength = ProjectTestRules.MaxElementLength;

    /// <summary>True when the list is a usable argv (<see cref="ProjectTestRules.IsValid"/>) whose program is an absolute path, as a run requires.</summary>
    public static bool IsValid(IReadOnlyList<string>? argv) => ProjectTestRules.IsValid(argv) && ProjectFolderList.IsAbsolute(argv![0]);

    public static string Normalize(string folder) => folder.Length > 1 ? folder.TrimEnd('/', '\\') : folder;

    /// <summary>The commands saved in agent.json, by folder; empty when there are none.</summary>
    public static IReadOnlyDictionary<string, List<string>> Saved(AgentConfig config) =>
        Identity.Peek(config)?.ProjectTests ?? new Dictionary<string, List<string>>();

    /// <summary>Sets the command of an existing folder; false when the folder or the command is not valid or the list is full.</summary>
    public static bool Set(AgentConfig config, string folder, IReadOnlyList<string> argv, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!ProjectFolderList.IsAbsolute(folder) || !(exists ?? Directory.Exists)(folder) || !IsValid(argv)) return false;
        var identity = Identity.Load(config);
        var tests = new Dictionary<string, List<string>>(identity.ProjectTests ?? [], StringComparer.Ordinal);
        var key = Normalize(folder);
        if (!tests.ContainsKey(key) && tests.Count >= MaxCommands) return false;
        tests[key] = [.. argv];
        (identity with { ProjectTests = tests }).Save(config);
        return true;
    }

    /// <summary>Removes the command of a folder; false when it had none.</summary>
    public static bool Clear(AgentConfig config, string folder)
    {
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity.Load(config);
        var key = Normalize(folder);
        if (identity.ProjectTests is not { } tests || !tests.ContainsKey(key)) return false;
        var left = tests.Where(t => t.Key != key).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        (identity with { ProjectTests = left.Count > 0 ? left : null }).Save(config);
        return true;
    }
}
