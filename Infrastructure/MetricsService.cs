using System.Diagnostics;

namespace WinsAlt.Infrastructure;

/// <summary>
/// Self-monitoring: process CPU/RAM plus protocol throughput. A background timer samples every
/// 2s - turning the monotonically increasing <see cref="WinsCounters"/> into per-second rates  - 
/// so the live reading is decoupled from how often the dashboard polls. Each sample is kept in a
/// 10-minute ring for the throughput chart.
/// </summary>
public sealed class MetricsService : IDisposable
{
    public sealed record Snapshot(
        double CpuPercent,
        long MemoryBytes,
        int Threads,
        double UptimeSeconds,
        double PacketsPerSec,
        double QueriesPerSec,
        double HitsPerSec,
        double MissesPerSec,
        double RegistrationsPerSec,
        long AllocatedBytes,
        int Gen0Collections);

    /// <summary>One chart point. T = unix seconds (UTC).</summary>
    public sealed record HistoryPoint(long T, double Queries, double Hits, double Misses, double Registrations);

    private const int HistoryCapacity = 300;            // 10 min at one point per 2s
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly WinsCounters _counters;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private readonly Queue<HistoryPoint> _history = new(HistoryCapacity + 1);

    private TimeSpan _lastCpu;
    private long _lastTimestamp;
    private long _lastPackets, _lastQueries, _lastHits, _lastMisses, _lastRegistrations;
    private volatile Snapshot _snapshot;

    public MetricsService(WinsCounters counters)
    {
        _counters = counters;
        _lastCpu = _proc.TotalProcessorTime;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _snapshot = new Snapshot(0, _proc.WorkingSet64, 0, 0, 0, 0, 0, 0, 0, GC.GetTotalAllocatedBytes(), GC.CollectionCount(0));
        _timer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
    }

    public Snapshot Current => _snapshot;

    public IReadOnlyList<HistoryPoint> History()
    {
        lock (_lock) return _history.ToArray();
    }

    private void Sample()
    {
        try
        {
            lock (_lock)
            {
                _proc.Refresh();
                long timestamp = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds;
                if (seconds <= 0) return;
                _lastTimestamp = timestamp;

                var cpuNow = _proc.TotalProcessorTime;
                double cpuPct = (cpuNow - _lastCpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100.0;
                _lastCpu = cpuNow;

                long hits = WinsCounters.Read(ref _counters.QueryHits) + WinsCounters.Read(ref _counters.DnsHits) + WinsCounters.Read(ref _counters.PartnerHits) + WinsCounters.Read(ref _counters.ReplicaHits);
                long registrations = WinsCounters.Read(ref _counters.Registrations) + WinsCounters.Read(ref _counters.Refreshes);

                var snap = new Snapshot(
                    Math.Round(Math.Clamp(cpuPct, 0, 100), 1),
                    _proc.WorkingSet64,
                    _proc.Threads.Count,
                    Math.Round((DateTime.UtcNow - _proc.StartTime.ToUniversalTime()).TotalSeconds, 0),
                    Rate(WinsCounters.Read(ref _counters.PacketsReceived), ref _lastPackets, seconds),
                    Rate(WinsCounters.Read(ref _counters.Queries), ref _lastQueries, seconds),
                    Rate(hits, ref _lastHits, seconds),
                    Rate(WinsCounters.Read(ref _counters.QueryMisses), ref _lastMisses, seconds),
                    Rate(registrations, ref _lastRegistrations, seconds),
                    // Managed bytes allocated since start, and gen-0 GCs: under a pure query load both stay flat,
                    // which is the evidence that the packet path does not allocate (see `WinsAlt.exe --bench`).
                    GC.GetTotalAllocatedBytes(), GC.CollectionCount(0));
                _snapshot = snap;

                _history.Enqueue(new HistoryPoint(DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    snap.QueriesPerSec, snap.HitsPerSec, snap.MissesPerSec, snap.RegistrationsPerSec));
                while (_history.Count > HistoryCapacity) _history.Dequeue();
            }
        }
        catch
        {
            // Sampling is best-effort; never let it crash the timer thread.
        }
    }

    private static double Rate(long current, ref long last, double seconds)
    {
        double rate = (current - last) / seconds;
        last = current;
        return Math.Round(Math.Max(rate, 0), 1);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _proc.Dispose();
    }
}
