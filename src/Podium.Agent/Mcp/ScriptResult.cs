using System.Globalization;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Mcp;

/// <summary>
/// The line `aipod_run_result` adds for a script run (ADR-0008 §3d): either "complete" with the size, or exactly what is missing and why: the
/// bytes cut on the target (before which chunk), the bytes the server refused over its cap (from which chunk, under which cap) and a stop at the
/// read limit. A run of another mode has no such line.
/// </summary>
public static class ScriptResult
{
    public static string? Completeness(RunView? view, IEnumerable<StoredChunk> chunks)
    {
        if (view is null || view.OutputComplete is null) return null;
        if (view.OutputComplete == true) return string.Create(CultureInfo.InvariantCulture, $"Output complete, {view.OutputReadBytes ?? view.OutputBytes} bytes.");
        var parts = new List<string>();
        if (view.OutputDroppedBytes is > 0 and var dropped)
        {
            var gap = chunks.FirstOrDefault(c => c.Gap);
            parts.Add(gap is null
                ? string.Create(CultureInfo.InvariantCulture, $"{dropped} bytes cut on the target")
                : string.Create(CultureInfo.InvariantCulture, $"{dropped} bytes cut on the target before chunk {gap.Seq}"));
        }

        if (view.OutputRefusedBytes is > 0 and var refused)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{refused} bytes from chunk {view.OutputMissingFromSeq} on refused by the server cap of {view.OutputCap} bytes"));
        }

        if (view.OutputTruncated && parts.Count == 0) parts.Add("output was cut (the run was stopped at the read limit)");
        else if (view.OutputTruncated && view.Error == "output_limit") parts.Add("the run was stopped at the read limit");
        return parts.Count == 0 ? "Output may be incomplete." : "Output incomplete: " + string.Join("; ", parts) + ".";
    }
}
