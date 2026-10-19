using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.StatusLine;

/// <summary>
/// <c>aipod config claude-statusline on|off|status</c> (ADR-0011 §7a, limits): the one place aipod edits Claude Code's own settings, and only when the person runs this verb at the machine.
/// <c>on</c> keeps the person's current <c>statusLine</c> (or the fact that there was none), sets it to <c>aipod statusline</c> (which prints the old one, so nothing changes on screen) and
/// leaves every other key alone; <c>off</c> puts the old one back, only if the current one is still aipod's. A settings file that is not a JSON object is never touched. Only the user settings
/// of the Claude config folder are changed: a project's own <c>statusLine</c> overrides them and is not touched.
/// </summary>
public static class StatusLineSetup
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string CommandLine(AgentConfig config) => $"\"{config.BinaryPath}\" statusline";

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        var path = Path.Combine(config.ClaudeConfigDir, "settings.json");
        if (args.Length == 3 && args[2] == "status") return await StatusAsync(config, path, stdout);
        if (args.Length == 3 && args[2] is "on" or "off")
        {
            try
            {
                return args[2] == "on" ? await OnAsync(config, path, stdout, stderr) : await OffAsync(config, path, stdout, stderr);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await stderr.WriteLineAsync($"claude-statusline: the settings file could not be changed ({e.GetType().Name}); nothing was changed.");
                return 1;
            }
        }

        await stderr.WriteLineAsync("usage: aipod config claude-statusline on | off | status");
        return 2;
    }

    private static bool IsOurs(JsonNode? statusLine, AgentConfig config) =>
        statusLine is JsonObject o && o["type"]?.GetValue<string>() == "command" && o["command"]?.GetValue<string>() == CommandLine(config);

    private static async Task<int> OnAsync(AgentConfig config, string path, TextWriter stdout, TextWriter stderr)
    {
        if (config.ServiceMode)
        {
            await stderr.WriteLineAsync("claude-statusline: a service agent has no Claude login or user folder to set this up in.");
            return 2;
        }

        if (!TryRead(path, out var root, out var problem))
        {
            await stderr.WriteLineAsync($"claude-statusline: {problem}; nothing was changed.");
            return 2;
        }

        if (IsOurs(root["statusLine"], config))
        {
            await stdout.WriteLineAsync("claude-statusline: already on");
            return 0;
        }

        var original = root["statusLine"]?.DeepClone();
        var identity = Identity.Load(config);
        (identity with { StatusLineBackup = original?.ToJsonString() ?? "null" }).Save(config);
        var line = new JsonObject { ["type"] = "command", ["command"] = CommandLine(config) };
        if (original is JsonObject old && old["padding"] is { } padding) line["padding"] = padding.DeepClone();
        root["statusLine"] = line;
        Write(path, root);
        await stdout.WriteLineAsync("claude-statusline: on. Claude Code now runs `aipod statusline`, which keeps the usage limits it is given and still prints "
            + (original is null ? "a short limits line" : "your previous status line") + ". Sessions pick it up on their next update; `off` puts things back.");
        return 0;
    }

    private static async Task<int> OffAsync(AgentConfig config, string path, TextWriter stdout, TextWriter stderr)
    {
        var identity = Identity.Load(config);
        if (identity.StatusLineBackup is not { } backup)
        {
            await stdout.WriteLineAsync("claude-statusline: was not on");
            return 0;
        }

        if (!TryRead(path, out var root, out var problem))
        {
            await stderr.WriteLineAsync($"claude-statusline: {problem}; nothing was changed.");
            return 2;
        }

        if (IsOurs(root["statusLine"], config))
        {
            var original = backup == "null" ? null : JsonNode.Parse(backup);
            if (original is null) root.Remove("statusLine");
            else root["statusLine"] = original;
            Write(path, root);
            await stdout.WriteLineAsync("claude-statusline: off, your previous status line is back");
        }
        else
        {
            await stdout.WriteLineAsync("claude-statusline: the status line in the settings is no longer aipod's, so it was left alone");
        }

        (identity with { StatusLineBackup = null }).Save(config);
        return 0;
    }

    private static async Task<int> StatusAsync(AgentConfig config, string path, TextWriter stdout)
    {
        var on = TryRead(path, out var root, out _) && IsOurs(root["statusLine"], config);
        await stdout.WriteLineAsync($"claude-statusline: {(on ? "on" : "off")}");
        if (LimitsSnapshot.Load(config) is { } limits) await stdout.WriteLineAsync($"last limits seen {limits.At:u}: {limits.Text()}");
        else await stdout.WriteLineAsync("no limits seen yet (they appear after a claude.ai Pro or Max session has answered once)");
        return 0;
    }

    /// <summary>The settings as a JSON object (an empty one when there is no file); false, with the reason, when the file is not one.</summary>
    private static bool TryRead(string path, out JsonObject root, out string problem)
    {
        root = [];
        problem = "";
        if (!File.Exists(path)) return true;
        if (new FileInfo(path).LinkTarget is not null)
        {
            problem = "the settings file is a link";
            return false;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject parsed)
            {
                root = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // falls through to the message below
        }

        problem = "the settings file is not a JSON object";
        return false;
    }

    private static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
