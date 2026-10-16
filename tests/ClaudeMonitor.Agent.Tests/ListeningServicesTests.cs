using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The listening services an agent may report once the owner opted in (ADR-0008 §3a′): ports, a name where the OS shows one, read only.</summary>
public sealed class ListeningServicesTests
{
    private const string Table =
        "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n"
        + "   0: 00000000:1538 00000000:0000 0A 00000000:00000000 00:00000000 00000000   999        0 40001 1 0000000000000000 100 0 0 10 0\n"
        + "   1: 0100007F:0016 0100007F:D5A2 01 00000000:00000000 00:00000000 00000000     0        0 40002 1 0000000000000000 100 0 0 10 0\n"
        + "   2: 00000000:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 40003 1 0000000000000000 100 0 0 10 0\n"
        + "   3: 00000000:01BB 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 0 1 0000000000000000 100 0 0 10 0\n";

    [Fact]
    public void Only_listening_sockets_with_an_inode_are_read_from_the_proc_table()
    {
        var rows = ListeningServices.ParseProcNet(Table).ToList();
        Assert.Equal([(40001L, 5432), (40003L, 80)], rows); // 0x1538 = 5432, an established socket and an inode of 0 are skipped
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("header only\n")]
    [InlineData("a b c\nnot a table row\n")]
    public void A_table_without_listening_rows_gives_nothing(string? text) => Assert.Empty(ListeningServices.ParseProcNet(text));

    [Fact]
    public void The_services_are_one_per_port_lowest_first_with_the_name_where_one_is_known()
    {
        var services = ListeningServices.Probe(() => [8080, 22, 22, 5432, 0, 70000], () => new Dictionary<int, string> { [5432] = "postgres", [8080] = "" });

        Assert.Equal([22, 5432, 8080], services.Select(s => s.Port));
        Assert.All(services, s => Assert.Equal("tcp", s.Proto));
        Assert.Equal([null, "postgres", null], services.Select(s => s.Process));
    }

    [Fact]
    public void At_most_sixty_four_services_are_reported()
    {
        var services = ListeningServices.Probe(() => [.. Enumerable.Range(1000, 200)], () => new Dictionary<int, string>());
        Assert.Equal(ListeningServices.Max, services.Count);
        Assert.Equal(1000, services[0].Port);
    }

    [Fact]
    public void A_process_name_loses_control_characters_and_is_cut_at_sixty_four()
    {
        Assert.Equal("nginx", ListeningServices.Clean("nginx\n"));
        Assert.Equal("ab", ListeningServices.Clean("a\u0007b"));
        Assert.Equal(64, ListeningServices.Clean(new string('x', 100)).Length);
    }

    [Fact]
    public void The_live_probe_reads_this_machine_without_throwing()
    {
        var services = ListeningServices.Live();
        Assert.All(services, s => Assert.InRange(s.Port, 1, 65535));
    }
}
