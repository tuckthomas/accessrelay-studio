using EliteScriptStudio.Core;

internal static class RuntimeLockVerification
{
    internal static void Run()
    {
        var collector = new RuntimeLockMeasurements(); long token = collector.Begin();
        for (int i = 1; i <= 1000; i++) collector.Record(RuntimeLockPath.InputPoll, token, i * 10000, i * 10000 + i);
        var snapshot = Snapshot(collector, token)!;
        var stats = snapshot.InputPoll;
        Equal(256, stats.Samples); Equal(1000L, stats.TotalSamples);
        Equal(1000d, stats.LatestMs!.Value); Equal(872d, stats.P50Ms!.Value); Equal(988d, stats.P95Ms!.Value); Equal(998d, stats.P99Ms!.Value);
        Equal(0, snapshot.VisionSubmission.Samples); Equal(null, snapshot.ReleaseDeadline.MaximumMs);
        RuntimeLockMeasurements.Validate(snapshot);
        long newer = collector.Begin();
        Equal(false, collector.Finish(token)); Equal(true, collector.Collecting);
        collector.Record(RuntimeLockPath.VisionSubmission, token, 100, 200);
        collector.Record(RuntimeLockPath.VisionSubmission, newer, 100, 105);
        Equal(null, Snapshot(collector, token));
        Equal(1, Snapshot(collector, newer)!.VisionSubmission.Samples);
        Equal(true, collector.Finish(newer)); collector.Record(RuntimeLockPath.VisionSubmission, newer, 200, 205);
        Equal(1, Snapshot(collector, newer)!.VisionSubmission.Samples);
        token = collector.Begin(); collector.Record(RuntimeLockPath.InputPoll, token, 1, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 100000; i++) collector.Record(RuntimeLockPath.InputPoll, token, i * 10, i * 10 + 1);
        Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        var invalid = Snapshot(collector, token)! with { Generation = 0 };
        try { RuntimeLockMeasurements.Validate(invalid); throw new Exception("Invalid measurement generation accepted."); }
        catch (InvalidDataException) { }
    }
    private static RuntimeLockSnapshot? Snapshot(RuntimeLockMeasurements collector, long token)
    {
        var samples = new RuntimeLockSample[RuntimeLockMeasurements.Storage];
        Span<int> counts = stackalloc int[4], next = stackalloc int[4]; Span<long> totals = stackalloc long[4];
        return collector.Copy(token, samples, counts, next, totals, out bool collecting)
            ? RuntimeLockMeasurements.Summarize(token, collecting, samples, counts, next, totals, 1000) : null;
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
}
