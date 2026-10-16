using System.Buffers.Binary;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// Turns a Windows console program into a GUI-subsystem one by changing one field of its PE header (ADR-0008 §9.12): the same program, started by the
/// logon task without a console window. A published single-file agent is a PE file with the bundle appended, so only the Subsystem word of the optional
/// header changes; the code is untouched. The release zip carries one binary, so the installer makes <c>cm-agentw.exe</c> from the one it runs.
/// </summary>
public static class PeSubsystem
{
    public const ushort Gui = 2;
    public const ushort Console = 3;
    private const int ELfanewOffset = 0x3C;
    private const int OptionalHeaderOffset = 4 + 20; // "PE\0\0" and the COFF header
    private const int SubsystemOffset = 68; // the same in PE32 and PE32+

    /// <summary>Sets the subsystem to GUI in a header buffer; false when it is not a PE header or is not a console program to begin with.</summary>
    public static bool SetGui(Span<byte> header)
    {
        if (!TryFind(header, out var at)) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[at..]) != Console) return false;
        BinaryPrimitives.WriteUInt16LittleEndian(header[at..], Gui);
        return true;
    }

    /// <summary>Makes <paramref name="path"/> a GUI-subsystem program in place; false (file untouched) when it is not a console PE file.</summary>
    public static bool TrySetGui(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[4096];
            var read = stream.Read(header, 0, header.Length);
            if (read < ELfanewOffset + 4 || !TryFind(header.AsSpan(0, read), out var at)) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(at)) != Console) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at), Gui);
            stream.Position = at;
            stream.Write(header, at, 2);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryFind(ReadOnlySpan<byte> header, out int subsystemAt)
    {
        subsystemAt = 0;
        if (header.Length < ELfanewOffset + 4 || header[0] != (byte)'M' || header[1] != (byte)'Z') return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[ELfanewOffset..]);
        if (pe < 0 || pe > header.Length - (OptionalHeaderOffset + SubsystemOffset + 2)) return false;
        if (header[pe] != (byte)'P' || header[pe + 1] != (byte)'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return false;
        subsystemAt = pe + OptionalHeaderOffset + SubsystemOffset;
        return true;
    }
}
