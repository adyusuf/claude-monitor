using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeMonitor.Agent.Net;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A network that hands out IPv6 but drops it must not keep the agent from reaching the server over IPv4.</summary>
public sealed class HappyEyeballsTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4 = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    private sealed class Conn(IPAddress address) : IDisposable
    {
        public IPAddress Address { get; } = address;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void The_families_alternate_starting_with_the_one_dns_gave_first()
    {
        Assert.Equal([V6, V4, V6b, V4b], HappyEyeballs.Interleave([V6, V6b, V4, V4b]));
        Assert.Equal([V4, V6, V4b], HappyEyeballs.Interleave([V4, V4b, V6]));
        Assert.Empty(HappyEyeballs.Interleave([]));
    }

    [Fact]
    public async Task An_ipv6_address_that_never_answers_loses_to_ipv4_after_the_head_start()
    {
        CancellationToken v6Token = default;
        var winner = await HappyEyeballs.RaceAsync([V6, V4], TimeSpan.FromMilliseconds(20), async (address, ct) =>
        {
            if (address.Equals(V4)) return new Conn(address);
            v6Token = ct;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // dropped: no answer, ever
            return new Conn(address);
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(V4, winner.Address);
        Assert.True(v6Token.IsCancellationRequested, "the losing attempt is cancelled");
    }

    [Fact]
    public async Task A_failed_address_starts_the_next_one_without_waiting_for_the_head_start()
    {
        var tried = new List<IPAddress>();
        var winner = await HappyEyeballs.RaceAsync([V6, V4], TimeSpan.FromHours(1), (address, _) =>
        {
            tried.Add(address);
            return address.Equals(V6)
                ? Task.FromException<Conn>(new SocketException((int)SocketError.NetworkUnreachable))
                : Task.FromResult(new Conn(address));
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(V4, winner.Address);
        Assert.Equal([V6, V4], tried);
    }

    [Fact]
    public async Task When_every_address_fails_the_last_error_is_thrown()
    {
        var error = await Assert.ThrowsAsync<SocketException>(() => HappyEyeballs.RaceAsync([V6, V4], TimeSpan.FromMilliseconds(5),
            (address, _) => Task.FromException<Conn>(new SocketException((int)(address.Equals(V4) ? SocketError.ConnectionRefused : SocketError.NetworkUnreachable))),
            CancellationToken.None));
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        Assert.Equal(SocketError.HostNotFound, (await Assert.ThrowsAsync<SocketException>(() =>
            HappyEyeballs.RaceAsync([], TimeSpan.Zero, (a, _) => Task.FromResult(new Conn(a)), CancellationToken.None))).SocketErrorCode);
    }

    [Fact]
    public async Task A_loser_that_connects_after_the_winner_is_closed()
    {
        var late = new TaskCompletionSource<Conn>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = await HappyEyeballs.RaceAsync([V6, V4], TimeSpan.FromMilliseconds(5),
            (address, _) => address.Equals(V6) ? late.Task : Task.FromResult(new Conn(address)), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(V4, winner.Address);

        var straggler = new Conn(V6);
        late.SetResult(straggler);
        await Task.Run(async () =>
        {
            while (!straggler.Disposed) await Task.Delay(5);
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(winner.Disposed);
    }

    [Fact]
    public async Task The_callers_cancellation_ends_the_race()
    {
        using var stop = new CancellationTokenSource();
        var race = HappyEyeballs.RaceAsync([V6, V4], TimeSpan.FromMilliseconds(5), async (address, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Conn(address);
        }, stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => race.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task The_agents_handler_connects_through_the_race_to_a_real_server()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        using var handler = ApiClient.CreateHandler();
        Assert.NotNull(handler.ConnectCallback);
        using var http = new HttpClient(handler);
        // "localhost" resolves to ::1 and 127.0.0.1; only IPv4 listens, so the race has to reach it.
        Assert.Equal("ok", await http.GetStringAsync(new Uri($"http://localhost:{port}/")).WaitAsync(TimeSpan.FromSeconds(10)));
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
