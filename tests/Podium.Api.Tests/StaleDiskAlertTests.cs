using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// A disk alert whose disk is no longer reported ends with the next sample that lists the machine's disks: an agent that restarted forgot
/// the alert it had opened, and a disk it now leaves out (a simulator runtime image on a Mac) can never be sampled below its threshold again.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class StaleDiskAlertTests(ApiFactory api)
{
    private const string Gone = "/Library/Developer/CoreSimulator/Volumes/iOS_23F77";

    private DateTimeOffset Now => api.Clock.GetUtcNow();

    private static DiskSample Disk(string mount, long used = 50) => new(mount, used, 100);

    private static MetricSample Sample(DateTimeOffset at, params DiskSample[] disks) => new(at, 10, 400, 1000, disks);

    private async Task<TestAgent> AgentAsync(string tag) => await (await api.NewClient().SignedUpAsync(tag)).ConnectAgentAsync();

    private async Task OpenAsync(TestAgent agent, string kind, string subject, double value = 97)
    {
        var response = await agent.Http.PostAsJsonAsync("/api/agent/alerts", new[] { new AlertReport(kind, subject, AlertStates.Open, value, 90, Now) }, TestUser.Json);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task SendAsync(TestAgent agent, params MetricSample[] samples)
    {
        var response = await agent.Http.PostAsJsonAsync("/api/agent/metrics", new MetricsReport(samples), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<List<(string Kind, string Subject, string State)>> AlertsAsync(TestAgent agent) =>
        [.. (await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts?resolved=true", TestUser.Json))!
            .Where(a => a.AgentId == agent.Tokens.AgentId).OrderBy(a => a.Kind).ThenBy(a => a.Subject).Select(a => (a.Kind, a.Subject, a.State))];

    [Fact]
    public async Task A_disk_the_sample_no_longer_lists_has_its_alert_resolved_and_the_others_stay_open()
    {
        var agent = await AgentAsync("stale-basic");
        await OpenAsync(agent, "disk", Gone);
        await OpenAsync(agent, "disk", "/data", 95);
        await OpenAsync(agent, "cpu", "", 95);

        await SendAsync(agent, Sample(Now.AddSeconds(2), Disk("/data", 95)));

        Assert.Equal([("cpu", "", "open"), ("disk", "/data", "open"), ("disk", Gone, "resolved")], await AlertsAsync(agent));
    }

    [Fact]
    public async Task A_sample_without_disks_says_nothing_about_them()
    {
        var agent = await AgentAsync("stale-empty");
        await OpenAsync(agent, "disk", Gone);

        await SendAsync(agent, Sample(Now.AddSeconds(2))); // the agent could not read its drives

        Assert.Equal([("disk", Gone, "open")], await AlertsAsync(agent));
    }

    [Fact]
    public async Task A_sample_older_than_the_alerts_last_update_does_not_end_it()
    {
        var agent = await AgentAsync("stale-old");
        await OpenAsync(agent, "disk", "/mnt/external");

        await SendAsync(agent, Sample(Now.AddSeconds(-5), Disk("/data"))); // taken before the disk was attached and went over the threshold

        Assert.Equal([("disk", "/mnt/external", "open")], await AlertsAsync(agent));
    }

    [Fact]
    public async Task A_list_cut_at_the_cap_may_be_missing_disks_so_it_ends_nothing_but_a_shorter_one_does()
    {
        var agent = await AgentAsync("stale-cap");
        await OpenAsync(agent, "disk", Gone);
        var cap = 16; // ApiConfig.MetricsDisksMax
        var full = Enumerable.Range(0, cap).Select(i => Disk($"/d{i}")).ToArray();

        await SendAsync(agent, Sample(Now.AddSeconds(2), full));
        Assert.Equal([("disk", Gone, "open")], await AlertsAsync(agent));

        await SendAsync(agent, Sample(Now.AddSeconds(3), full[..(cap - 1)]));
        Assert.Equal([("disk", Gone, "resolved")], await AlertsAsync(agent));
    }

    [Fact]
    public async Task Another_machines_alert_for_the_same_mount_is_not_touched()
    {
        var first = await AgentAsync("stale-one");
        var second = await AgentAsync("stale-two");
        await OpenAsync(first, "disk", Gone);
        await OpenAsync(second, "disk", Gone);

        await SendAsync(first, Sample(Now.AddSeconds(2), Disk("/data")));

        Assert.Equal([("disk", Gone, "resolved")], await AlertsAsync(first));
        Assert.Equal([("disk", Gone, "open")], await AlertsAsync(second));
    }
}
