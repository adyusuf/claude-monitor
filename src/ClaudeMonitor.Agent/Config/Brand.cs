namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The product's name as people see it (global rename, 19/10/2026). <c>aipod</c> is the name in every message, usage line and document; the old
/// names keep working: the binary <c>cm-agent</c> (the file the installers, the services and the updater use), the <c>CM_*</c> variables, and
/// the old plugin and tool names. Names a machine stores for itself (service, account, unit, launchd label, paths) do not change here, because
/// changing them would orphan the installs that exist.
/// </summary>
public static class Brand
{
    public const string Name = "aipod";

    /// <summary>The binary's name before the rename; it stays the installed file's name, and <see cref="Name"/> is an alias beside it.</summary>
    public const string LegacyName = "cm-agent";

    /// <summary>The prefix of the environment variables that replace <c>CM_</c> (<c>AIPOD_SERVER</c> for <c>CM_SERVER</c>); the old one is the fallback.</summary>
    public const string EnvPrefix = "AIPOD_";

    public const string LegacyEnvPrefix = "CM_";

    /// <summary>True for the name of a process that is this program: the file name with or without its extension, under either name.</summary>
    public static bool IsOurProcess(string? processName) =>
        processName is { Length: > 0 }
        && (processName.StartsWith(LegacyName, StringComparison.OrdinalIgnoreCase) || processName.StartsWith(Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a variable under its new name first, then under the old one (<c>CM_X</c> is also asked as <c>AIPOD_X</c>). Other names pass through.</summary>
    public static Func<string, string?> WithAliases(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return name =>
        {
            if (name.StartsWith(LegacyEnvPrefix, StringComparison.Ordinal) && read(EnvPrefix + name[LegacyEnvPrefix.Length..]) is { Length: > 0 } renamed) return renamed;
            return read(name);
        };
    }
}
