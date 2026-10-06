namespace ClaudeMonitor.Contracts;

// The lint a grant that a Claude session asks for goes through (ADR-0004): no literal option that runs another program.

internal static class GrantOptionLint
{
    private const string RsyncName = "rsync";
    private const string ProxyCommandOption = "proxycommand";

    // Options that run another program. A grant a Claude session asks for may not carry one as a literal.
    private static readonly string[] ExecOptions =
    [
        "-exec", "-execdir", "-ok", "-okdir", "-delete", "--to-command", "--checkpoint-action", "--upload-pack",
        "--receive-pack", "-oProxyCommand",
    ];

    private static readonly string[] RsyncOptions = ["-e", "--rsh"];

    /// <summary>True when a literal piece of a template is an option that runs another program.</summary>
    public static bool IsExecOption(string literal, string argv0Key)
    {
        if (literal.Contains(ProxyCommandOption, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var equals = literal.IndexOf('=');
        var name = equals >= 0 ? literal[..equals] : literal;
        if (IsOptionName(name, ExecOptions))
        {
            return true;
        }

        if (argv0Key != RsyncName)
        {
            return false;
        }

        // rsync -e is --rsh; it may also hide in a cluster of short options (-avze).
        return IsOptionName(name, RsyncOptions) || (name.Length > 1 && name[0] == '-' && name[1] != '-' && name.Contains('e'));
    }

    // A long option may be abbreviated (--to-com), so a prefix of a listed long option counts.
    private static bool IsOptionName(string name, string[] options) =>
        options.Any(option => name == option || (name.Length >= 3 && name.StartsWith("--", StringComparison.Ordinal)
            && option.StartsWith(name, StringComparison.Ordinal)));
}
