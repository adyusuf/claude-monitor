using System.Text;

namespace Podium.Agent;

/// <summary>
/// What a hook reads and writes is JSON, which is UTF-8 whatever the machine's console code page is. <c>Console.In</c> and
/// <c>Console.Out</c> use that code page (on a Turkish Windows it is not UTF-8), which turns "ş", "ğ", "İ" in a prompt or a tool input into
/// other characters before they are stored and shows a reply the same way; a hook therefore reads and writes the raw streams as UTF-8.
/// </summary>
public static class HookStreams
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static (TextReader In, TextWriter Out) Open(Stream stdin, Stream stdout)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        return (new StreamReader(stdin, Utf8), new StreamWriter(stdout, Utf8) { AutoFlush = true });
    }
}
