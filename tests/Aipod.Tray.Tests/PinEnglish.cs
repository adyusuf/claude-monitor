using System.Runtime.CompilerServices;
using ClaudeMonitor.Agent.Config;

namespace Aipod.Tray.Tests;

/// <summary>The machine's own language must not change what these tests read: the whole assembly speaks English (ADR-0014, Part A).</summary>
internal static class PinEnglish
{
#pragma warning disable CA2255 // a test assembly, not a library: the initializer is the point
    [ModuleInitializer]
    internal static void Run() => Strings.UseForProcess("en");
#pragma warning restore CA2255
}
