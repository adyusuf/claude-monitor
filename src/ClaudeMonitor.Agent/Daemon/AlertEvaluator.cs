using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// Turns samples into alert reports (ADR-0005, "Resources and alerts"): per kind and subject, a value at or above the
/// threshold for the whole sustain time opens one alert, and it resolves once the value is below the threshold minus
/// <see cref="HysteresisPct"/>. Pure: time comes from the samples, nothing is read or written, and the agent never acts on an alert.
/// </summary>
public sealed class AlertEvaluator(AlertThresholds thresholds)
{
    /// <summary>An open alert stays open until the value is this many points below its threshold.</summary>
    public const double HysteresisPct = 5;

    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Track> _tracks = [];
    private AlertThresholds _thresholds = thresholds;

    private readonly record struct Key(string Kind, string Subject);

    private sealed class Track
    {
        public DateTimeOffset? BreachSince;
        public bool Open;
        public double LastValue;
    }

    /// <summary>The new thresholds apply from the next sample; open alerts and running breach timers are kept.</summary>
    public void Update(AlertThresholds thresholds)
    {
        lock (_gate) _thresholds = thresholds;
    }

    /// <summary>Reports to send for this sample: an "open" the moment a breach has lasted long enough, a "resolved" when it ends.</summary>
    public IReadOnlyList<AlertReport> Observe(MetricSample sample)
    {
        lock (_gate)
        {
            var reports = new List<AlertReport>();
            var seen = new HashSet<Key>();
            Check(new Key(AlertKinds.Cpu, ""), sample.CpuPct, _thresholds.CpuPct, sample.SampledAt, seen, reports);
            if (sample.MemTotalBytes > 0)
                Check(new Key(AlertKinds.Memory, ""), Percent(sample.MemUsedBytes, sample.MemTotalBytes), _thresholds.MemoryPct, sample.SampledAt, seen, reports);
            foreach (var disk in sample.Disks)
            {
                if (disk.TotalBytes <= 0) continue;
                Check(new Key(AlertKinds.Disk, disk.Mount), Percent(disk.UsedBytes, disk.TotalBytes), _thresholds.DiskPct, sample.SampledAt, seen, reports);
            }

            ResolveVanished(seen, sample.SampledAt, reports);
            return reports;
        }
    }

    private void Check(Key key, double value, int threshold, DateTimeOffset at, HashSet<Key> seen, List<AlertReport> reports)
    {
        if (!seen.Add(key)) return; // two disks with one mount name count once
        if (!_tracks.TryGetValue(key, out var track)) _tracks[key] = track = new Track();
        track.LastValue = value;

        if (track.Open)
        {
            if (value >= ResolveBelow(threshold)) return;
            track.Open = false;
            track.BreachSince = null;
            reports.Add(Report(key, AlertStates.Resolved, value, threshold, at));
            return;
        }

        if (value < threshold)
        {
            track.BreachSince = null;
            return;
        }

        track.BreachSince ??= at;
        if (at - track.BreachSince.Value < TimeSpan.FromSeconds(_thresholds.SustainSeconds)) return;
        track.Open = true;
        reports.Add(Report(key, AlertStates.Open, value, threshold, at));
    }

    // A disk or reading that is gone ends its alert at the next sample; one that was never open is forgotten.
    private void ResolveVanished(HashSet<Key> seen, DateTimeOffset at, List<AlertReport> reports)
    {
        foreach (var key in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var track = _tracks[key];
            _tracks.Remove(key);
            if (track.Open) reports.Add(Report(key, AlertStates.Resolved, track.LastValue, ThresholdOf(key.Kind), at));
        }
    }

    private int ThresholdOf(string kind) => kind switch
    {
        AlertKinds.Cpu => _thresholds.CpuPct,
        AlertKinds.Memory => _thresholds.MemoryPct,
        AlertKinds.Disk => _thresholds.DiskPct,
        _ => _thresholds.DiskPct,
    };

    // With a threshold of 5 or less there is no room for the band, so the alert resolves below the threshold itself.
    private static double ResolveBelow(int threshold) => threshold > HysteresisPct ? threshold - HysteresisPct : threshold;

    private static double Percent(long used, long total) => (double)used / total * 100;

    private static AlertReport Report(Key key, string state, double value, int threshold, DateTimeOffset at) =>
        new(key.Kind, key.Subject, state, Math.Round(value, 1), threshold, at);
}
