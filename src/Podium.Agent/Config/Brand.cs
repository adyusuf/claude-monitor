namespace Podium.Agent.Config;

/// <summary>The program's name as people see it and as its file is called (ADR-0016): <c>aipod</c>.</summary>
public static class Brand
{
    public const string Name = "aipod";

    /// <summary>True for the name of a process that is this program: the file name with or without its extension.</summary>
    public static bool IsOurProcess(string? processName) =>
        processName is { Length: > 0 } && processName.StartsWith(Name, StringComparison.OrdinalIgnoreCase);
}
