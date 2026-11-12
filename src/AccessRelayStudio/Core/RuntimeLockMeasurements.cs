using System.Buffers;

namespace EliteScriptStudio.Core;

internal enum RuntimeLockPath { InputPoll, VisionRead, VisionSubmission, ReleaseDeadline }
internal readonly record struct RuntimeLockStatistics(int Samples, long TotalSamples, double WindowSpanMs,
    double? LatestMs, double? P50Ms, double? P95Ms, double? P99Ms, double? MaximumMs);
internal sealed record RuntimeLockSnapshot(long Generation, bool Collecting, RuntimeLockStatistics InputPoll,
    RuntimeLockStatistics VisionRead, RuntimeLockStatistics VisionSubmission, RuntimeLockStatistics ReleaseDeadline);
internal readonly record struct RuntimeLockSample(long AcquiredTicks, long WaitTicks);

/// <summary>Caller serializes mutations with the existing controller lock. Recording
/// allocates nothing; copies are sorted outside that lock. A generation owns each run.</summary>
internal sealed class RuntimeLockMeasurements
{
    internal const int Capacity = 256, Paths = 4, Storage = Capacity * Paths;
    private readonly RuntimeLockSample[] _samples = new RuntimeLockSample[Storage];
    private readonly int[] _counts = new int[Paths], _next = new int[Paths];
    private readonly long[] _totals = new long[Paths];
    internal long Generation { get; private set; }
    internal bool Collecting { get; private set; }
    internal long Begin()
    { Generation++; Collecting = true; Array.Clear(_counts); Array.Clear(_next); Array.Clear(_totals); return Generation; }
    internal bool Finish(long generation)
    { if (generation <= 0 || generation != Generation) return false; Collecting = false; return true; }
    internal void Record(RuntimeLockPath path, long generation, long requested, long acquired)
    {
        int p = (int)path;
        if (!Collecting || generation != Generation || p < 0 || p >= Paths || requested <= 0 || acquired < requested) return;
        if (_counts[p] > 0 && acquired < _samples[p * Capacity + (_next[p] + Capacity - 1) % Capacity].AcquiredTicks) return;
        _samples[p * Capacity + _next[p]] = new(acquired, acquired - requested);
        _next[p] = (_next[p] + 1) % Capacity; _counts[p] = Math.Min(Capacity, _counts[p] + 1); _totals[p]++;
    }
    internal bool Copy(long generation, Span<RuntimeLockSample> samples, Span<int> counts, Span<int> next,
        Span<long> totals, out bool collecting)
    {
        collecting = Collecting;
        if (generation <= 0 || generation != Generation) return false;
        _samples.CopyTo(samples); _counts.CopyTo(counts); _next.CopyTo(next); _totals.CopyTo(totals); return true;
    }
    internal static RuntimeLockSnapshot Summarize(long generation, bool collecting, ReadOnlySpan<RuntimeLockSample> samples,
        ReadOnlySpan<int> counts, ReadOnlySpan<int> next, ReadOnlySpan<long> totals, long frequency)
    {
        var values = ArrayPool<double>.Shared.Rent(Capacity);
        try
        {
            var results = new RuntimeLockStatistics[Paths];
            for (int p = 0; p < Paths; p++)
            {
                int count = counts[p];
                if (count == 0) { results[p] = new(0, 0, 0, null, null, null, null, null); continue; }
                var latest = samples[p * Capacity + (next[p] + Capacity - 1) % Capacity];
                var first = samples[p * Capacity + (count == Capacity ? next[p] : 0)];
                for (int i = 0; i < count; i++) values[i] = samples[p * Capacity + i].WaitTicks * 1000d / frequency;
                Array.Sort(values, 0, count);
                results[p] = new(count, totals[p], (latest.AcquiredTicks - first.AcquiredTicks) * 1000d / frequency,
                    latest.WaitTicks * 1000d / frequency, values[(int)Math.Ceiling(count * .5) - 1], values[(int)Math.Ceiling(count * .95) - 1],
                    values[(int)Math.Ceiling(count * .99) - 1], values[count - 1]);
            }
            return new(generation, collecting, results[0], results[1], results[2], results[3]);
        }
        finally { ArrayPool<double>.Shared.Return(values); }
    }
    internal static void Validate(RuntimeLockSnapshot snapshot)
    {
        if (snapshot.Generation <= 0) throw new InvalidDataException("Invalid lock-measurement generation.");
        foreach (var stats in new[] { snapshot.InputPoll, snapshot.VisionRead, snapshot.VisionSubmission, snapshot.ReleaseDeadline })
        {
            double?[] values = [stats.LatestMs, stats.P50Ms, stats.P95Ms, stats.P99Ms, stats.MaximumMs];
            if (stats.Samples is < 0 or > Capacity || stats.TotalSamples < stats.Samples || !double.IsFinite(stats.WindowSpanMs) || stats.WindowSpanMs < 0
                || (stats.Samples == 0 && (stats.TotalSamples != 0 || stats.WindowSpanMs != 0 || values.Any(value => value is not null)))
                || (stats.Samples > 0 && values.Any(value => value is null || !double.IsFinite(value.Value) || value < 0))
                || stats.P50Ms > stats.P95Ms || stats.P95Ms > stats.P99Ms || stats.P99Ms > stats.MaximumMs || stats.LatestMs > stats.MaximumMs)
                throw new InvalidDataException("Invalid lock-measurement samples or percentiles.");
        }
    }
}
