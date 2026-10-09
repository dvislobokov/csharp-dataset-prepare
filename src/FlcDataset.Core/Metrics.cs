using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlcDataset.Core;

/// <summary>Thread-safe named counters; snapshots are key-sorted for deterministic output.</summary>
public sealed class Counters
{
    readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

    public void Add(string key, long delta = 1) => _values.AddOrUpdate(key, delta, (_, v) => v + delta);
    public long Get(string key) => _values.TryGetValue(key, out var v) ? v : 0;

    public SortedDictionary<string, long> Snapshot(string? prefix = null) =>
        new(_values.Where(kv => prefix is null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                   .ToDictionary(kv => prefix is null ? kv.Key : kv.Key[prefix.Length..], kv => kv.Value), StringComparer.Ordinal);
}

public sealed record LatencySummary(long Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double TotalMs);

public sealed class LatencyRecorder
{
    readonly ConcurrentBag<double> _values = [];
    public void Record(double ms) => _values.Add(ms);
    public void Record(TimeSpan t) => _values.Add(t.TotalMilliseconds);

    public LatencySummary Summarize()
    {
        var v = _values.ToArray();
        Array.Sort(v);
        if (v.Length == 0) return new LatencySummary(0, 0, 0, 0, 0, 0, 0);
        double P(double q) => v[Math.Min(v.Length - 1, (int)Math.Ceiling(q * v.Length) - 1)];
        return new LatencySummary(v.Length, Math.Round(v.Average(), 4), Math.Round(P(0.50), 4), Math.Round(P(0.95), 4),
            Math.Round(P(0.99), 4), Math.Round(v[^1], 4), Math.Round(v.Sum(), 2));
    }
}

/// <summary>Named wall-clock stage timings (in insertion order).</summary>
public sealed class StageTimings
{
    readonly List<KeyValuePair<string, double>> _stages = [];
    readonly Lock _lock = new();

    public T Time<T>(string stage, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        try { return f(); } finally { Add(stage, sw.Elapsed.TotalMilliseconds); }
    }

    public async Task<T> TimeAsync<T>(string stage, Func<Task<T>> f)
    {
        var sw = Stopwatch.StartNew();
        try { return await f(); } finally { Add(stage, sw.Elapsed.TotalMilliseconds); }
    }

    public void Add(string stage, double ms)
    {
        lock (_lock)
        {
            var i = _stages.FindIndex(kv => kv.Key == stage);
            if (i >= 0) _stages[i] = new(stage, _stages[i].Value + ms);
            else _stages.Add(new(stage, ms));
        }
    }

    public Dictionary<string, double> Snapshot()
    {
        lock (_lock) return _stages.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2));
    }
}

public sealed record ResourceSummary(
    double WallMs, double CpuMs, long PeakWorkingSetBytes, long PeakManagedBytes, long TotalAllocatedBytes,
    int Gen0, int Gen1, int Gen2, int SampleIntervalMs);

/// <summary>Samples working set and managed heap in the background to capture peaks.</summary>
public sealed class ResourceSampler : IDisposable
{
    readonly Process _process = Process.GetCurrentProcess();
    readonly Stopwatch _wall = Stopwatch.StartNew();
    readonly TimeSpan _cpuStart;
    readonly long _allocStart;
    readonly int _g0, _g1, _g2;
    readonly Timer _timer;
    readonly int _intervalMs;
    long _peakWs, _peakManaged;

    public ResourceSampler(int intervalMs = 25)
    {
        _intervalMs = intervalMs;
        _process.Refresh();
        _cpuStart = _process.TotalProcessorTime;
        _allocStart = GC.GetTotalAllocatedBytes(precise: false);
        (_g0, _g1, _g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        Sample();
        _timer = new Timer(_ => Sample(), null, intervalMs, intervalMs);
    }

    void Sample()
    {
        try
        {
            _process.Refresh();
            InterlockedMax(ref _peakWs, _process.WorkingSet64);
            InterlockedMax(ref _peakManaged, GC.GetTotalMemory(false));
        }
        catch (InvalidOperationException) { }
    }

    static void InterlockedMax(ref long target, long value)
    {
        long cur;
        while (value > (cur = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }

    public ResourceSummary Stop()
    {
        _timer.Dispose();
        Sample();
        _process.Refresh();
        return new ResourceSummary(
            Math.Round(_wall.Elapsed.TotalMilliseconds, 2),
            Math.Round((_process.TotalProcessorTime - _cpuStart).TotalMilliseconds, 2),
            Interlocked.Read(ref _peakWs), Interlocked.Read(ref _peakManaged),
            GC.GetTotalAllocatedBytes(false) - _allocStart,
            GC.CollectionCount(0) - _g0, GC.CollectionCount(1) - _g1, GC.CollectionCount(2) - _g2, _intervalMs);
    }

    public void Dispose() => _timer.Dispose();
}
