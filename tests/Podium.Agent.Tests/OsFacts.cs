namespace Podium.Agent.Tests;

/// <summary>
/// Tests that can only run on one OS. Elsewhere they are reported as skipped with the reason, never counted as passed by returning early
/// before any assertion (F-329).
/// </summary>
public static class OsOnly
{
    public const string MacOs = "runs on macOS only";
    public const string Linux = "runs on Linux only";
}

/// <summary>A fact that runs on macOS and is reported skipped elsewhere.</summary>
public sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = OsOnly.MacOs;
    }
}

/// <summary>A theory that runs on macOS and is reported skipped elsewhere.</summary>
public sealed class MacOsTheoryAttribute : TheoryAttribute
{
    public MacOsTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = OsOnly.MacOs;
    }
}

/// <summary>A fact that runs on Linux and is reported skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = OsOnly.Linux;
    }
}
