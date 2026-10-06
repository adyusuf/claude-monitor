using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class AlertEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertThresholds Defaults = new(90, 90, 90, 60);

    private static MetricSample Sample(int seconds, double cpu = 10, long used = 100, long total = 1000, params DiskSample[] disks) =>
        new(T0.AddSeconds(seconds), cpu, used, total, disks);

    [Fact]
    public void A_breach_opens_one_alert_only_after_it_lasted_the_whole_sustain_time()
    {
        var evaluator = new AlertEvaluator(Defaults);
        Assert.Empty(evaluator.Observe(Sample(0, cpu: 95)));
        Assert.Empty(evaluator.Observe(Sample(59, cpu: 96)));

        var opened = Assert.Single(evaluator.Observe(Sample(60, cpu: 97.26)));
        Assert.Equal((AlertKinds.Cpu, "", AlertStates.Open, 97.3, 90d), (opened.Kind, opened.Subject, opened.State, opened.Value, opened.ThresholdPct));
        Assert.Equal(T0.AddSeconds(60), opened.At);
        Assert.Empty(evaluator.Observe(Sample(120, cpu: 99))); // already open: no second report
    }

    [Fact]
    public void A_value_exactly_at_the_threshold_counts_as_a_breach()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        Assert.Empty(evaluator.Observe(Sample(0, cpu: 89.9)));
        Assert.Equal(AlertStates.Open, Assert.Single(evaluator.Observe(Sample(1, cpu: 90))).State);
    }

    [Fact]
    public void An_open_alert_stays_open_inside_the_five_point_band_and_resolves_below_it()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        Assert.Single(evaluator.Observe(Sample(0, cpu: 95)));
        Assert.Empty(evaluator.Observe(Sample(1, cpu: 89)));
        Assert.Empty(evaluator.Observe(Sample(2, cpu: 85))); // threshold - 5 is still inside the band

        var resolved = Assert.Single(evaluator.Observe(Sample(3, cpu: 84.9)));
        Assert.Equal((AlertStates.Resolved, 84.9, 90d), (resolved.State, resolved.Value, resolved.ThresholdPct));
        Assert.Empty(evaluator.Observe(Sample(4, cpu: 10))); // and nothing more
    }

    [Fact]
    public void A_resolved_alert_can_open_again_after_a_new_sustained_breach()
    {
        var evaluator = new AlertEvaluator(Defaults);
        evaluator.Observe(Sample(0, cpu: 95));
        evaluator.Observe(Sample(60, cpu: 95));
        Assert.Equal(AlertStates.Resolved, Assert.Single(evaluator.Observe(Sample(61, cpu: 10))).State);
        Assert.Empty(evaluator.Observe(Sample(62, cpu: 95)));
        Assert.Equal(AlertStates.Open, Assert.Single(evaluator.Observe(Sample(122, cpu: 95))).State);
    }

    [Fact]
    public void A_threshold_of_five_or_less_resolves_below_the_threshold_itself()
    {
        var evaluator = new AlertEvaluator(new AlertThresholds(5, 90, 90, 0));
        Assert.Single(evaluator.Observe(Sample(0, cpu: 6)));
        Assert.Empty(evaluator.Observe(Sample(1, cpu: 5)));
        Assert.Equal(AlertStates.Resolved, Assert.Single(evaluator.Observe(Sample(2, cpu: 4.9))).State);
    }

    [Fact]
    public void A_breach_that_is_interrupted_starts_its_timer_again()
    {
        var evaluator = new AlertEvaluator(Defaults);
        evaluator.Observe(Sample(0, cpu: 95));
        evaluator.Observe(Sample(30, cpu: 20)); // below the threshold: the timer is dropped
        evaluator.Observe(Sample(40, cpu: 95));
        Assert.Empty(evaluator.Observe(Sample(90, cpu: 95))); // 50 s since the new start, 90 s since the first
        Assert.Equal(AlertStates.Open, Assert.Single(evaluator.Observe(Sample(100, cpu: 95))).State);
    }

    [Fact]
    public void A_sustain_time_of_zero_opens_on_the_first_breaching_sample()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        Assert.Equal(AlertStates.Open, Assert.Single(evaluator.Observe(Sample(0, cpu: 91))).State);
    }

    [Fact]
    public void Memory_is_judged_by_its_share_of_the_total_and_a_total_of_zero_is_skipped()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        var memory = Assert.Single(evaluator.Observe(Sample(0, used: 950, total: 1000)));
        Assert.Equal((AlertKinds.Memory, "", 95d), (memory.Kind, memory.Subject, memory.Value));

        var other = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        Assert.Empty(other.Observe(Sample(0, used: 950, total: 0)));
    }

    [Fact]
    public void A_disk_with_no_size_is_skipped_and_two_disks_open_two_alerts()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        var reports = evaluator.Observe(Sample(0, disks: [new DiskSample("/zero", 5, 0), new DiskSample("/a", 95, 100), new DiskSample("/b", 91, 100)]));
        Assert.Equal(["/a", "/b"], reports.Select(r => r.Subject).Order());
        Assert.All(reports, r => Assert.Equal(AlertKinds.Disk, r.Kind));
    }

    [Fact]
    public void Two_disks_with_one_mount_name_count_once()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        var reports = evaluator.Observe(Sample(0, disks: [new DiskSample("/d", 95, 100), new DiskSample("/d", 99, 100)]));
        Assert.Equal(95d, Assert.Single(reports).Value); // the first one
    }

    [Fact]
    public void A_disk_that_vanished_resolves_its_open_alert_with_its_last_value_and_one_never_opened_is_forgotten()
    {
        var evaluator = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        evaluator.Observe(Sample(0, disks: [new DiskSample("/gone", 96, 100), new DiskSample("/quiet", 10, 100)]));

        var resolved = Assert.Single(evaluator.Observe(Sample(1)));
        Assert.Equal(("/gone", AlertStates.Resolved, 96d, 90d), (resolved.Subject, resolved.State, resolved.Value, resolved.ThresholdPct));
        Assert.Empty(evaluator.Observe(Sample(2)));
    }

    [Fact]
    public void A_vanished_disk_with_a_breach_timer_running_loses_it()
    {
        var evaluator = new AlertEvaluator(Defaults);
        evaluator.Observe(Sample(0, disks: [new DiskSample("/d", 95, 100)]));
        evaluator.Observe(Sample(30)); // gone: nothing to resolve, the timer is dropped
        evaluator.Observe(Sample(40, disks: [new DiskSample("/d", 95, 100)]));
        Assert.Empty(evaluator.Observe(Sample(70, disks: [new DiskSample("/d", 95, 100)]))); // 30 s since it came back
    }

    [Fact]
    public void New_thresholds_apply_from_the_next_sample_and_keep_open_alerts_and_running_timers()
    {
        var evaluator = new AlertEvaluator(Defaults);
        evaluator.Observe(Sample(0, cpu: 95));
        evaluator.Observe(Sample(50, used: 950)); // memory timer starts at 50
        evaluator.Update(Defaults with { CpuPct = 99 });

        // The cpu timer survived the update (60 s since 0) but 95 is now below 99: nothing opens.
        Assert.Empty(evaluator.Observe(Sample(60, cpu: 95, used: 950)));
        // The memory timer survived: 60 s after 50 it opens.
        Assert.Equal(AlertKinds.Memory, Assert.Single(evaluator.Observe(Sample(110, cpu: 10, used: 950))).Kind);

        var open = new AlertEvaluator(Defaults with { SustainSeconds = 0 });
        open.Observe(Sample(0, cpu: 95));
        open.Update(Defaults with { CpuPct = 99, SustainSeconds = 0 });
        var resolved = Assert.Single(open.Observe(Sample(1, cpu: 92))); // the open alert is kept, and is judged by the new 99
        Assert.Equal((AlertStates.Resolved, 99d), (resolved.State, resolved.ThresholdPct));
    }
}
