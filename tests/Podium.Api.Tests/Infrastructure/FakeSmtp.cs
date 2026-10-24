using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>One message the fake server took: the envelope, the whole data part and the sign-in it was given (if it was asked for one).</summary>
public sealed record SmtpMail(string From, IReadOnlyList<string> To, string Data, string? User, string? Password);

/// <summary>
/// A mail server on the loopback address that speaks just enough SMTP to take one message (greeting, EHLO/HELO, AUTH LOGIN, MAIL, RCPT, DATA,
/// QUIT). No test mail leaves the machine. Each server has its own random port, so a test can show which server a mail went to.
/// </summary>
public sealed partial class FakeSmtp : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Channel<SmtpMail> mails = Channel.CreateUnbounded<SmtpMail>();
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;
    private readonly bool auth;

    /// <param name="auth">Whether the server offers AUTH LOGIN (a client with credentials then uses it).</param>
    public FakeSmtp(bool auth = false)
    {
        this.auth = auth;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>The port as the settings page takes it: a plain number in text.</summary>
    public string PortText => Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The next message the server took; fails the test when none comes.</summary>
    public async Task<SmtpMail> NextAsync()
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await mails.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Whether a message is waiting that was not read yet.</summary>
    public bool HasMail => mails.Reader.TryPeek(out _);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try
        {
            await accepting;
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

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
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            string? from = null;
            string? user = null;
            string? password = null;
            var to = new List<string>();
            await writer.WriteLineAsync("220 fake ready");
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                var parts = line.Split(' ');
                switch (parts[0].ToUpperInvariant())
                {
                    case "EHLO":
                        await writer.WriteLineAsync(auth ? "250-fake\r\n250 AUTH LOGIN" : "250 fake");
                        break;
                    case "AUTH":
                        // AUTH LOGIN with the user name as the first answer, or asked for.
                        if (parts.Length > 2) user = Decode(parts[2]);
                        else
                        {
                            await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                            user = Decode(await reader.ReadLineAsync(stop.Token) ?? "");
                        }

                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        password = Decode(await reader.ReadLineAsync(stop.Token) ?? "");
                        await writer.WriteLineAsync("235 ok");
                        break;
                    case "MAIL":
                        from = Address(line);
                        await writer.WriteLineAsync("250 ok");
                        break;
                    case "RCPT":
                        to.Add(Address(line));
                        await writer.WriteLineAsync("250 ok");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go on");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(stop.Token) is { } row && row != ".") data.AppendLine(row);
                        // The message is kept before the server says it took it: the sender returns only after this.
                        await mails.Writer.WriteAsync(new SmtpMail(from ?? "", [.. to], data.ToString(), user, password), stop.Token);
                        await writer.WriteLineAsync("250 queued");
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
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the client went away or the server was stopped
        }
    }

    private static string Address(string line) => AngleBrackets().Match(line) is { Success: true } m ? m.Groups[1].Value : "";

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [GeneratedRegex("<([^>]*)>")]
    private static partial Regex AngleBrackets();
}
