using EliteScriptStudio.Desktop;
using EliteScriptStudio.Diagnostics;

internal static class ResourceMonitorChecks
{
    internal static async Task RunAsync(string directory)
    {
        int created = 0, reads = 0, disposed = 0, active = 0, maximum = 0;
        using (var monitor = new SystemPerformanceMonitor(() =>
        {
            Interlocked.Increment(ref created); maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            return new Probe(() => { Interlocked.Increment(ref reads); return new(CpuPercent: 25); },
                () => { Interlocked.Increment(ref disposed); Interlocked.Decrement(ref active); });
        }, TimeSpan.FromMilliseconds(5)))
        {
            Check(monitor.Completion.IsCompleted && created == 0, "Disabled startup created a sampling worker.");
            await Task.Delay(25); Check(created == 0 && reads == 0, "Disabled startup sampled resources.");
            monitor.Enabled = true; await Until(() => monitor.Latest.SamplesCollected >= 65);
            Check(monitor.Latest.History?.Count == 60, "History was not bounded to 60 readings.");
            monitor.Enabled = false; await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            int stoppedReads = reads; long stoppedSamples = monitor.Latest.SamplesCollected;
            await Task.Delay(30);
            Check(reads == stoppedReads && monitor.Latest.SamplesCollected == stoppedSamples && !monitor.Latest.IsSampling,
                "Disabled monitoring continued sampling.");
            Check(disposed == 1 && active == 0, "Disabling left native probe resources open.");
            monitor.Enabled = true; await Until(() => created == 2 && monitor.Latest.IsSampling);
            monitor.Dispose(); await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(active == 0 && disposed == 2 && maximum == 1, "Restart/disposal leaked or overlapped probes.");
        }

        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int generations = 0;
        using (var monitor = new SystemPerformanceMonitor(() =>
        {
            int generation = Interlocked.Increment(ref generations);
            return new Probe(() =>
            {
                if (generation == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); }
                return new(CpuPercent: generation == 1 ? 99 : 27);
            }, () => { });
        }, TimeSpan.FromMilliseconds(5)))
        {
            monitor.Enabled = true; await Until(() => entered.IsSet);
            monitor.Enabled = false; monitor.Enabled = true;
            await Task.Delay(20); Check(generations == 1, "A new probe overlapped an in-flight old reading.");
            release.Set(); await Until(() => monitor.Latest.IsSampling);
            Check(monitor.Latest.CpuPercent == 27, "A cancelled generation published a stale reading.");
            monitor.Enabled = false; await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var preferences = new DesktopPreferenceStore(directory);
        Check(preferences.LoadResourceMonitoring(), "New preference default changed unexpectedly.");
        preferences.SaveResourceMonitoring(false);
        Check(!new DesktopPreferenceStore(directory).LoadResourceMonitoring(), "Off did not persist across launches.");
        preferences.SaveResourceMonitoring(true);
        Check(new DesktopPreferenceStore(directory).LoadResourceMonitoring(), "On did not persist across launches.");
        File.WriteAllText(Path.Combine(directory, "desktop-preferences.json"), "invalid");
        Check(!preferences.LoadResourceMonitoring(), "A damaged preference enabled sampling.");
        Check(SystemPerformanceMonitor.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 4) == 50,
            "Host CPU was not normalized to the whole machine.");
        Check(SystemPerformanceMonitor.ProcessPercent(TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.FromSeconds(1), 4) is null
            && SystemPerformanceMonitor.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, 4) is null,
            "Invalid CPU intervals were presented as valid usage.");
        Console.WriteLine("SUCCESS: resource monitor idle startup, native probe disposal, no disabled polling, bounded history, generation fencing, CPU accounting and saved preference.");
    }

    private static async Task Until(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (started.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Resource monitor state did not arrive."); await Task.Delay(5); }
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class Probe(Func<SystemPerformanceSnapshot> read, Action dispose) : ISystemPerformanceProbe
    {
        public SystemPerformanceSnapshot Read() => read();
        public void Dispose() => dispose();
    }
}
