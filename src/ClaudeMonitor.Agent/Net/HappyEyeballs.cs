using System.Net;
using System.Net.Sockets;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Net;

/// <summary>
/// Connects the way RFC 8305 (Happy Eyeballs) describes. .NET tries a host's addresses strictly one after another, so on
/// a network that hands out IPv6 but drops it, every connection waited on the IPv6 address until the connect timeout and
/// never reached IPv4. Here the families alternate, each attempt gets a short head start before the next one begins
/// beside it, a failure starts the next one at once, and the first to connect wins.
/// </summary>
public static class HappyEyeballs
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var socket = await RaceAsync(Interleave(addresses), AgentConfig.ConnectStagger,
            (address, token) => ConnectSocketAsync(address, context.DnsEndPoint.Port, token), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>The addresses with the families alternating, the first family as DNS gave it (RFC 8305 §4).</summary>
    internal static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0) return [];
        var first = addresses[0].AddressFamily;
        var a = addresses.Where(x => x.AddressFamily == first).ToList();
        var b = addresses.Where(x => x.AddressFamily != first).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) result.Add(a[i]);
            if (i < b.Count) result.Add(b[i]);
        }

        return result;
    }

    /// <summary>The first attempt to connect; the others are cancelled, and one that connects anyway is closed.</summary>
    internal static async Task<T> RaceAsync<T>(IReadOnlyList<IPAddress> addresses, TimeSpan stagger,
        Func<IPAddress, CancellationToken, Task<T>> connect, CancellationToken ct) where T : class, IDisposable
    {
        using var losers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<T>>();
        Exception? last = null;
        var next = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (next < addresses.Count) pending.Add(connect(addresses[next++], losers.Token));
                if (pending.Count == 0) throw last ?? new SocketException((int)SocketError.HostNotFound);

                var headStart = next < addresses.Count ? Task.Delay(stagger, losers.Token) : null;
                var done = await Task.WhenAny(headStart is null ? pending : pending.Cast<Task>().Append(headStart));
                if (done == headStart) continue; // the next address starts beside the ones still trying

                var attempt = (Task<T>)done;
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully) return attempt.Result;
                last = attempt.Exception?.InnerException ?? new OperationCanceledException(ct);
            }
        }
        finally
        {
            await losers.CancelAsync();
            foreach (var loser in pending)
            {
                _ = loser.ContinueWith(t => t.Result.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }
    }

    private static async Task<Socket> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
