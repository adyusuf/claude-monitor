namespace ClaudeMonitor.Contracts;

// Machine roles and facts (ADR-0008 §2, §3a). Closed sets: the database holds the same strings behind a CHECK, a new value
// is a migration, and code that switches on them always has a default branch (global #11).

/// <summary>Where a machine stands in the delivery chain. Unset (null) means "not set", never "test".</summary>
public static class MachineEnvironments
{
    public const string Dev = "dev";
    public const string Test = "test";
    public const string Stage = "stage";
    public const string Preprod = "preprod";
    public const string Production = "production";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Dev, Test, Stage, Preprod, Production, Other];
}

/// <summary>What a machine is for.</summary>
public static class MachineFunctions
{
    public const string Development = "development";
    public const string Application = "application";
    public const string Web = "web";
    public const string Database = "database";
    public const string Ci = "ci";
    public const string Storage = "storage";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Development, Application, Web, Database, Ci, Storage, Other];
}

/// <summary>How Claude Code is installed on a machine, as its agent reports it; null in the database means an older agent (unknown).</summary>
public static class ClaudeKinds
{
    public const string None = "none";
    public const string Npm = "npm";
    public const string Native = "native";
    public const string Unsupported = "unsupported";
    public static readonly IReadOnlyList<string> All = [None, Npm, Native, Unsupported];
}

/// <summary>What an agent says it can do beyond ADR-0005's runs; an older agent reports none.</summary>
public static class AgentCapabilities
{
    public const string Script = "script";
    public const string Ask = "ask";
    public static readonly IReadOnlyList<string> All = [Script, Ask];
}

/// <summary>The names of the tools an agent probes for its inventory (ADR-0008 §3a′), as they are reported.</summary>
public static class ToolNames
{
    public const string Sh = "sh";
    public const string Bash = "bash";
    public const string Python = "python3";
    public const string PowerShell = "pwsh";
    public const string WindowsPowerShell = "powershell";
    public const string Git = "git";
    public const string Dotnet = "dotnet";
    public const string Node = "node";
    public const string Npm = "npm";
    public const string Docker = "docker";
    public const string Psql = "psql";
    public const string Java = "java";
    public const string Claude = "claude";
    public static readonly IReadOnlyList<string> All = [Sh, Bash, Python, PowerShell, WindowsPowerShell, Git, Dotnet, Node, Npm, Docker, Psql, Java, Claude];

    /// <summary>The tools that can run a script (ADR-0008 §3c); only a trusted one is an interpreter.</summary>
    public static readonly IReadOnlySet<string> Interpreters = new HashSet<string> { Sh, Bash, Python, PowerShell, WindowsPowerShell };
}

/// <summary>
/// The pure rules of a role (ADR-0008 §2): what an admin set, what the agent claims, and the effective role the API serves. A
/// claim only tightens: it can make a machine production or database, never anything looser, and it never counts as "set".
/// </summary>
public static class MachineRoles
{
    public static bool IsEnvironment(string? value) => value is null || MachineEnvironments.All.Contains(value);

    public static bool IsFunction(string? value) => value is null || MachineFunctions.All.Contains(value);

    /// <summary>Production when the set value or the claim says so, otherwise the set value (a claim never fills an unset value with anything else).</summary>
    public static string? EffectiveEnvironment(string? set, string? claimed) =>
        set == MachineEnvironments.Production || claimed == MachineEnvironments.Production ? MachineEnvironments.Production : set;

    /// <summary>Database when the set value or the claim says so, otherwise the set value.</summary>
    public static string? EffectiveFunction(string? set, string? claimed) =>
        set == MachineFunctions.Database || claimed == MachineFunctions.Database ? MachineFunctions.Database : set;

    /// <summary>A strict target: the rules that need a code or refuse asks key on these two values only.</summary>
    public static bool IsStrict(string? environment, string? function) =>
        environment == MachineEnvironments.Production || function == MachineFunctions.Database;

    /// <summary>True when the SET role exists; a claim alone never counts (role_not_set keys on the set environment).</summary>
    public static bool IsSet(string? setEnvironment) => setEnvironment is not null;

    /// <summary>Leaving production or database for anything else: the change that needs a recent sign-in (ADR-0008 §3a).</summary>
    public static bool IsLowering(string? oldEnvironment, string? oldFunction, string? newEnvironment, string? newFunction) =>
        (oldEnvironment == MachineEnvironments.Production && newEnvironment != MachineEnvironments.Production)
        || (oldFunction == MachineFunctions.Database && newFunction != MachineFunctions.Database);

    /// <summary>The maintainer's four names, for people and for the session-start block; "not set" when there is no environment.</summary>
    public static string Describe(string? environment, string? function) => (environment, function) switch
    {
        (null, _) => "not set",
        (MachineEnvironments.Production, MachineFunctions.Database) => "live database server",
        (_, MachineFunctions.Database) => $"{environment} database server",
        (MachineEnvironments.Production, _) => "live server",
        (MachineEnvironments.Test, MachineFunctions.Development) => "test machine",
        (MachineEnvironments.Test, MachineFunctions.Application or MachineFunctions.Web) => "test server",
        (_, null) => $"{environment} machine",
        _ => $"{environment} {function} machine",
    };

    /// <summary>"production/database", "test/not set", "not set".</summary>
    public static string Code(string? environment, string? function) =>
        environment is null ? "not set" : $"{environment}/{function ?? "not set"}";
}
