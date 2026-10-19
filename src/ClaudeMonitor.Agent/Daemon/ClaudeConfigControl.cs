using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The remote writes the machine's owner may ask for from Podium (ADR-0011 §7a), done here and nowhere else: `claude plugin enable|disable|update|install &lt;id&gt;` with a fixed argument shape
/// (never `--accept-command`: a marketplace that declares a command is installed at the machine by a person), and one working-mode value or gate written to the adyusuf files of a project folder
/// the machine reported. Every value is checked again against <see cref="ClaudeConfigRules"/>, an id is run only when `claude plugin list` shows it (an install: when the machine's own
/// marketplaces offer it), a service agent and a production or database machine refuse, and a file that cannot be read is left exactly as it is.
/// </summary>
public sealed class ClaudeConfigControl(AgentConfig config, IClaudeCli cli, Func<ClaudeInstall>? locate = null)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly Func<ClaudeInstall> locator = locate ?? (() => ClaudeLocator.Locate(config));

    public async Task<ClaudeControlOutcome> PluginAsync(string kind, string? id, CancellationToken ct)
    {
        var verb = kind switch
        {
            AgentActionKinds.PluginEnable => "enable",
            AgentActionKinds.PluginDisable => "disable",
            AgentActionKinds.PluginUpdate => "update",
            AgentActionKinds.PluginInstall => "install",
            _ => null,
        };
        if (verb is null || !ClaudeConfigRules.IsPluginId(id)) return new(false, "refused: that is not a plugin action this agent knows");
        if (Refusal() is { } refused) return new(false, refused);
        var claude = locator();
        if (claude.Path is null) return new(false, "no `claude` was found on this machine's PATH");

        var installed = await IdsAsync(claude.Path, ["plugin", "list", "--json"], listOf: null, "id", ct);
        if (installed is null) return new(false, "could not read the list of installed plugins");
        if (verb == "install")
        {
            if (installed.Contains(id!)) return new(false, $"{id} is already installed");
            var available = await IdsAsync(claude.Path, ["plugin", "list", "--available", "--json"], "available", "pluginId", ct);
            if (available is null) return new(false, "could not read the plugins this machine's marketplaces offer");
            if (!available.Contains(id!)) return new(false, $"{id} is not offered by the marketplaces on this machine (add the marketplace there first)");
        }
        else if (!installed.Contains(id!))
        {
            return new(false, $"{id} is not installed here");
        }

        var run = await RunAsync(claude.Path, ["plugin", verb, id!], ct);
        if (run is null) return new(false, "claude could not be started");
        return run.ExitCode == 0
            ? new(true, $"{verb}: {id} done; sessions that are already running pick it up when they restart")
            : new(false, $"claude plugin {verb} failed (exit {run.ExitCode}): {FirstLine(run.Error + run.Output)}");
    }

    public ClaudeControlOutcome Setting(string? projectKey, string? name, string? value)
    {
        if (projectKey is not { Length: > 0 and <= RunKinds.ProjectKeyMax } || projectKey.Any(char.IsControl)) return new(false, "refused: no project was named");
        if (!ClaudeConfigRules.SettingNames.Contains(name ?? "") || !ClaudeConfigRules.IsSettingValue(name, value)) return new(false, "refused: that is not a setting value this agent may write");
        if (Refusal() is { } refused) return new(false, refused);
        if (ClaudeControl.FolderOf(config, projectKey) is not { } folder) return new(false, "this machine knows no such project");
        if (!Directory.Exists(folder)) return new(false, "the project's folder is gone from this machine");
        try
        {
            var dir = Path.Combine(folder, ".claude");
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is not null) return new(false, "the .claude folder of the project is a link: not changed");
            Directory.CreateDirectory(dir);
            return name switch
            {
                ClaudeConfigRules.ModeName => Write(Path.Combine(dir, "mode"), value + "\n", $"working mode set to {value}"),
                ClaudeConfigRules.ModelName => Write(Path.Combine(dir, "mode-model"), value + "\n", $"model set to {value}"),
                _ => SetGate(Path.Combine(dir, "adyusuf.json"), name!, value == "true"),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, $"the file could not be written: {e.GetType().Name}");
        }
    }

    private string? Refusal()
    {
        if (config.ServiceMode) return "this agent runs as a service: there is no Claude login or user folder here to change";
        return Identity.Peek(config) is { } identity && MachineRoles.IsStrict(identity.ClaimedEnvironment, identity.ClaimedFunction)
            ? "this machine says it is a production or database machine: Claude's setup is never changed from the web"
            : null;
    }

    private static ClaudeControlOutcome Write(string path, string text, string done)
    {
        if (new FileInfo(path).LinkTarget is not null) return new(false, "the file is a link: not changed");
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return new(true, done + "; it is a change in the project folder, so commit it yourself");
    }

    /// <summary>Sets one yes/no gate (a dotted name) in adyusuf.json, keeping everything else in the file; a file that is not a JSON object is left as it is.</summary>
    private static ClaudeControlOutcome SetGate(string path, string name, bool on)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) return new(false, "adyusuf.json is a link: not changed");
            if (info.Length > 64 * 1024) return new(false, "adyusuf.json is too large to change safely: not changed");
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return new(false, "adyusuf.json is not a JSON object: not changed");
            }
        }
        else
        {
            root = [];
        }

        var parts = name.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            if (node[part] is null) node[part] = new JsonObject();
            if (node[part] is not JsonObject next) return new(false, $"{part} in adyusuf.json is not an object: not changed");
            node = next;
        }

        node[parts[^1]] = on;
        return Write(path, root.ToJsonString(Pretty) + "\n", $"{name} set to {(on ? "yes" : "no")}");
    }

    private async Task<HashSet<string>?> IdsAsync(string claude, IReadOnlyList<string> args, string? listOf, string field, CancellationToken ct)
    {
        var run = await RunAsync(claude, args, ct);
        if (run is null || run.ExitCode != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.Output);
            var list = listOf is null ? doc.RootElement : doc.RootElement.TryGetProperty(listOf, out var inner) ? inner : default;
            if (list.ValueKind != JsonValueKind.Array) return null;
            return [.. list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "").Where(s => s.Length > 0)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ClaudeRun?> RunAsync(string claude, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return await cli.RunAsync(claude, args, null, Wait, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";
        var safe = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return safe.Length > 200 ? safe[..200] : safe;
    }
}
