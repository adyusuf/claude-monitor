using System.Buffers.Binary;
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

    // ---- macOS: the system's lsof, parsed ----------------------------------------------------------------------------

    [Fact]
    public void The_lsof_field_output_gives_each_listening_port_the_name_of_its_process_and_the_first_process_wins()
    {
        const string output = "p101\ncpostgres\nn*:5432\nn[::]:5432\np202\ncnode\nn127.0.0.1:8080\nn[::1]:8081\np303\ncimposter\nn*:5432\n";
        var names = ListeningServices.ParseLsof(output);
        Assert.Equal(("postgres", "node", "node"), (names[5432], names[8080], names[8081]));
        Assert.Equal(3, names.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p1\nn*:80\n")] // an address before a name
    [InlineData("p1\ncx\nn*:notaport\nn*:0\nn*:70000\n")]
    public void Output_without_a_usable_name_or_port_gives_nothing(string? output) => Assert.Empty(ListeningServices.ParseLsof(output));

    [Fact]
    public void A_control_character_in_a_process_name_is_removed_before_it_is_kept()
    {
        Assert.Equal("evil", ListeningServices.ParseLsof("p1\nce\u0007vil\nn*:80\n")[80]);
    }

    // ---- Windows: the IP helper's listener table ---------------------------------------------------------------------

    private static byte[] Table(bool ipv6, params (int Port, int Pid)[] rows)
    {
        var size = ipv6 ? 56 : 24;
        var bytes = new byte[4 + (rows.Length * size)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var at = 4 + (i * size);
            var network = (uint)(((rows[i].Port & 0xFF) << 8) | ((rows[i].Port >> 8) & 0xFF)); // network byte order in the low 16 bits
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + (ipv6 ? 20 : 8)), network);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + (ipv6 ? 52 : 20)), rows[i].Pid);
        }

        return bytes;
    }

    [Fact]
    public void The_listener_table_of_both_families_gives_port_and_owning_process()
    {
        Assert.Equal([(5432, 4120), (443, 4)], ListeningServices.ParseTcpTable(Table(false, (5432, 4120), (443, 4)), ipv6: false));
        Assert.Equal([(8080, 900)], ListeningServices.ParseTcpTable(Table(true, (8080, 900)), ipv6: true));
    }

    [Fact]
    public void A_truncated_or_empty_table_gives_the_rows_that_are_whole_and_never_throws()
    {
        var whole = Table(false, (22, 1), (80, 2));
        Assert.Equal([(22, 1)], ListeningServices.ParseTcpTable(whole.AsSpan(0, 4 + 24 + 10), ipv6: false)); // the second row is cut
        Assert.Empty(ListeningServices.ParseTcpTable([], ipv6: false));
        Assert.Empty(ListeningServices.ParseTcpTable(new byte[2], ipv6: true));
        var lying = Table(false, (22, 1));
        BinaryPrimitives.WriteInt32LittleEndian(lying, 1000); // a count larger than the buffer
        Assert.Single(ListeningServices.ParseTcpTable(lying, ipv6: false));
    }
}
