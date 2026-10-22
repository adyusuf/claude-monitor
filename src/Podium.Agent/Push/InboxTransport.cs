using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Podium.Agent.Push;

/// <summary>Writes lines to a session's inbox (ADR-0007). A seam: tests never open a real Claude Code socket.</summary>
public interface IInboxTransport
{
    /// <summary>Connects to the endpoint, writes the lines in order (each ends with a newline) and closes. Throws when it cannot.</summary>
    Task SendAsync(string endpoint, IReadOnlyList<string> lines, TimeSpan connectTimeout, CancellationToken ct);
}

/// <summary>
/// The real inbox: a Unix domain socket on macOS and Linux, a named pipe on Windows (<c>\\.\pipe\name</c>). Claude Code answers nothing
/// on either, so a send that returned means "written", never "read".
/// </summary>
public sealed class SocketInboxTransport : IInboxTransport
{
    public const string PipePrefix = @"\\.\pipe\";

    /// <summary>True when the endpoint is something this transport may connect to: an absolute socket path, or a local named pipe.</summary>
    public static bool IsEndpoint(string? endpoint) =>
        endpoint is { Length: > 0 } && endpoint.All(c => c != '\0')
        && (endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? OperatingSystem.IsWindows() && endpoint.Length > PipePrefix.Length
            : !OperatingSystem.IsWindows() && Path.IsPathRooted(endpoint));

    /// <summary>
    /// A socket path is only written to when it is not a symlink and belongs to this user: a stale <c>/tmp/cc-socks/&lt;pid&gt;.sock</c> that another local
    /// user recreated would otherwise receive the session's key and the prompt. Ownership that cannot be read counts as ours (the permission bits still apply).
    /// </summary>
    internal static bool IsOurs(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (OperatingSystem.IsWindows()) return true; // a path is not what is connected to there
        return Auth.UnixFiles.OwnerUid(path) is not { } owner || Auth.UnixFiles.Euid() is not { } me || owner == me;
    }

    public async Task SendAsync(string endpoint, IReadOnlyList<string> lines, TimeSpan connectTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!IsEndpoint(endpoint)) throw new InvalidOperationException("not an inbox endpoint");
        if (!endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) && !IsOurs(endpoint)) throw new InvalidOperationException("the socket is not this user's own");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(connectTimeout);
        try
        {
            if (endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
            {
                await using var pipe = new NamedPipeClientStream(".", endpoint[PipePrefix.Length..], PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(limit.Token);
                await pipe.WriteAsync(bytes, limit.Token);
                await pipe.FlushAsync(limit.Token);
                return;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), limit.Token);
            await socket.SendAsync(bytes, SocketFlags.None, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("the inbox did not answer in time"); // the session's own limit, not a stop of the daemon
        }
    }
}
