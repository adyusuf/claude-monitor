using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>How a <see cref="RefusingSmtp"/> lets a send fail.</summary>
public enum SmtpRefusal
{
    /// <summary>Takes the connection and never says a word, as a mail server that hangs.</summary>
    Silent,

    /// <summary>Greets, then refuses the recipient with a reply that repeats the address (as real servers do).</summary>
    Recipient,
}

/// <summary>A mail server on the loopback address that never takes a message: for the failure paths of the SMTP mailer. No mail leaves the machine.</summary>
public sealed class RefusingSmtp : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly SmtpRefusal refusal;
    private readonly Task accepting;

    public RefusingSmtp(SmtpRefusal refusal)
    {
        this.refusal = refusal;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }

    public int Port { get; }

    public string PortText => Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await accepting;
        stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        var connections = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }

        await Task.WhenAll(connections);
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            if (refusal == SmtpRefusal.Silent)
            {
                await Task.Delay(Timeout.Infinite, stop.Token);
                return;
            }

            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 refusing ready");
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                var verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "RCPT":
                        await writer.WriteLineAsync($"550 5.1.1 {line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()} no such mailbox");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 ok");
                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            // the client went away or the server stopped
        }
    }
}
