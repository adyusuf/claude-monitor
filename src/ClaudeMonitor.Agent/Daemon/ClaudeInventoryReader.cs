using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// What Claude Code has on this machine, read-only (ADR-0011 §7a, step E): the sessions it keeps in <c>sessions/&lt;pid&gt;.json</c>, the plugins <c>claude plugin list --json</c> names,
/// the skill folders of the user and of the installed plugins (folder names only: a skill's text is never read), and the adyusuf plugin's small files in each project folder
/// (<c>.claude/mode</c>, <c>mode-model</c>, <c>adyusuf.json</c>). Nothing is written, nothing is started but `claude plugin list`, and every text is checked against a closed shape or cut
/// and stripped of control characters before it leaves the machine. The plugin list is asked at most every <see cref="PluginsEvery"/>; an unreadable part is reported as unknown (null).
/// </summary>
public sealed partial class ClaudeInventoryReader(AgentConfig config, IClaudeCli cli, Func<ClaudeInstall>? locate = null, Func<int, bool>? alive = null)
{
    public static readonly TimeSpan PluginsEvery = TimeSpan.FromMinutes(10);
    private const int FileMax = 16 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string[] Statuses = ["idle", "busy", "waiting"];
    private readonly Func<ClaudeInstall> locator = locate ?? (() => ClaudeLocator.Locate(config));
    private (List<InventoryPlugin>? Plugins, List<(string Id, string Path)> Paths)? plugins;
    private DateTimeOffset pluginsAt;

    [GeneratedRegex(@"^[A-Za-z0-9._@:+-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdShape();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex WordShape();

    [GeneratedRegex(@"^[a-z0-9_]{1,30}(\.[a-z0-9_]{1,30}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex GateShape();

    public async Task<ClaudeInventory> ReadAsync(IReadOnlyList<string> configDirs, IReadOnlyList<string> projectFolders, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configDirs);
        ArgumentNullException.ThrowIfNull(projectFolders);
        var pluginState = await PluginsAsync(now, ct);
        return new ClaudeInventory(Sessions(configDirs, alive), pluginState.Plugins, Skills(pluginState.Paths), Settings(projectFolders), now);
    }

    private static string? Clean(string? text, int max)
    {
        if (text is null) return null;
        var cleaned = new string(text.Where(ch => !char.IsControl(ch) && ch is not ('\u202e' or '\u202d' or '\u200b')).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length > max ? cleaned[..max] : cleaned;
    }

    private static List<InventorySession> Sessions(IReadOnlyList<string> configDirs, Func<int, bool>? alive)
    {
        var list = new List<InventorySession>();
        foreach (var dir in configDirs)
        {
            foreach (var s in ClaudeSessions.Read(dir, alive).Live)
            {
                string? key = null;
                if (ProjectFolderList.IsAbsolute(s.Cwd) && Directory.Exists(s.Cwd)) key = Clean(ProjectInfo.Resolve(s.Cwd)?.Key, InventoryLimits.KeyMax);
                list.Add(new InventorySession(Clean(s.Name, InventoryLimits.NameMax), key, s.Kind is { } k && WordShape().IsMatch(k) ? k : null,
                    s.Status is { } st && Statuses.Contains(st) ? st : null, s.StartedAt));
            }
        }

        return [.. list.OrderBy(s => s.StartedAt ?? DateTimeOffset.MaxValue).Take(InventoryLimits.SessionsMax)];
    }

    private async Task<(List<InventoryPlugin>? Plugins, List<(string Id, string Path)> Paths)> PluginsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (plugins is { } cached && now - pluginsAt < PluginsEvery && now >= pluginsAt) return cached;
        plugins = await ListPluginsAsync(ct);
        pluginsAt = now;
        return plugins.Value;
    }

    private async Task<(List<InventoryPlugin>? Plugins, List<(string Id, string Path)> Paths)> ListPluginsAsync(CancellationToken ct)
    {
        var none = ((List<InventoryPlugin>?)null, new List<(string Id, string Path)>());
        var claude = locator();
        if (claude.Path is null) return none;
        ClaudeRun run;
        try
        {
            run = await cli.RunAsync(claude.Path, ["plugin", "list", "--json"], null, Wait, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return none;
        }

        if (run.ExitCode != 0) return none;
        var items = new List<InventoryPlugin>();
        var paths = new List<(string Id, string Path)>();
        try
        {
            using var doc = JsonDocument.Parse(run.Output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return none;
            foreach (var p in doc.RootElement.EnumerateArray().Take(InventoryLimits.PluginsMax))
            {
                if (p.ValueKind != JsonValueKind.Object || Str(p, "id") is not { } id || !IdShape().IsMatch(id)) continue;
                var scope = Str(p, "scope");
                items.Add(new InventoryPlugin(id, Clean(Str(p, "version"), 64), p.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                    scope is not null && WordShape().IsMatch(scope) ? scope : null));
                if (Str(p, "installPath") is { } path) paths.Add((id, path));
            }
        }
        catch (JsonException)
        {
            return none;
        }

        return (items, paths);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Folder names with a SKILL.md: the user's, and "plugin:name" for each installed plugin whose folder lies under this Claude config folder.</summary>
    private List<string> Skills(List<(string Id, string Path)> pluginPaths)
    {
        var names = new List<string>();
        void Add(string folder, string prefix)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(dir);
                    if (IdShape().IsMatch(name) && File.Exists(Path.Combine(dir, "SKILL.md"))) names.Add(prefix + name);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder that cannot be read has no skills to list
            }
        }

        Add(Path.Combine(config.ClaudeConfigDir, "skills"), "");
        var root = Path.GetFullPath(Path.Combine(config.ClaudeConfigDir, "plugins")) + Path.DirectorySeparatorChar;
        foreach (var (id, path) in pluginPaths)
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)) continue;
            Add(Path.Combine(path, "skills"), id.Split('@')[0] + ":");
        }

        return [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(InventoryLimits.SkillsMax)];
    }

    private static List<InventorySetting> Settings(IReadOnlyList<string> folders)
    {
        var list = new List<InventorySetting>();
        foreach (var folder in folders.Where(ProjectFolderList.IsAbsolute).Distinct(StringComparer.Ordinal))
        {
            if (list.Count >= InventoryLimits.SettingsMax) break;
            if (!Directory.Exists(folder) || ProjectInfo.Resolve(folder)?.Key is not { } key || list.Any(s => s.ProjectKey == key)) continue;
            var mode = OneLine(folder, "mode") is { } m && m.Length == 1 && m[0] is >= 'A' and <= 'E' ? m : null;
            var model = OneLine(folder, "mode-model") is { } mm && WordShape().IsMatch(mm) ? mm : null;
            var gates = Gates(folder);
            if (mode is not null || model is not null || gates is not null) list.Add(new InventorySetting(Clean(key, InventoryLimits.KeyMax) ?? key, mode, model, gates));
        }

        return list;
    }

    private static string? OneLine(string folder, string file) => ReadSmall(Path.Combine(folder, ".claude", file))?.Trim();

    private static string? ReadSmall(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= FileMax ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The yes/no values of adyusuf.json, flattened to dotted names (only booleans, at most four levels deep); anything else in the file is ignored.</summary>
    private static Dictionary<string, bool>? Gates(string folder)
    {
        if (ReadSmall(Path.Combine(folder, ".claude", "adyusuf.json")) is not { } text) return null;
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(text);
            void Walk(JsonElement e, string prefix, int depth)
            {
                if (depth > 4 || gates.Count >= InventoryLimits.GatesMax) return;
                foreach (var p in e.EnumerateObject())
                {
                    var name = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        if (GateShape().IsMatch(name) && gates.Count < InventoryLimits.GatesMax) gates[name] = p.Value.GetBoolean();
                    }
                    else if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        Walk(p.Value, name, depth + 1);
                    }
                }
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object) Walk(doc.RootElement, "", 1);
        }
        catch (JsonException)
        {
            return null;
        }

        return gates.Count == 0 ? null : gates;
    }
}
