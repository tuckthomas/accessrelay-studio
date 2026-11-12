using System.Diagnostics;
using EliteScriptStudio.Core;

internal static class FeedbackVerification
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        foreach (var kind in Enum.GetValues<FeedbackCueKind>())
        {
            var samples = FeedbackSounds.Render(kind, .3);
            Check(samples.Length is > 0 and <= 5760 && samples.All(value => float.IsFinite(value) && Math.Abs(value) <= .075001),
                "Cue samples exceed their duration/volume bounds.");
            Check(samples[0] == 0 && samples[^1] == 0, "Cue envelope has a discontinuous endpoint.");
        }
        foreach (var settings in new[] { new FeedbackSettings(Volume: double.NaN), new(Strength: .6), new(Volume: -1) })
        {
            bool rejected = false; try { settings.Validate(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid feedback levels were accepted.");
        }
        long now = 1000;
        var output = new Output();
        using (var dispatcher = new FeedbackDispatcher(output, () => Interlocked.Read(ref now), 1000))
        {
            Check(!dispatcher.TryPublish(FeedbackCueKind.Test, new(0, 1), 1), "Feedback was active by default.");
            dispatcher.Configure(new(AudioEnabled: true));
            Check(dispatcher.TryPublish(FeedbackCueKind.PlayerLocked, new(1, 1), 1), "First cue was not queued.");
            await Until(() => dispatcher.Status.Played == 1);
            Check(!dispatcher.TryPublish(FeedbackCueKind.PlayerLocked, new(1, 1), 1), "A repeated observation replayed its cue.");
            Check(!dispatcher.TryPublish(FeedbackCueKind.PlayerLocked, new(1, 2), 1), "Rapid cue requests were not rate-limited.");
            Interlocked.Add(ref now, 200);
            output.Fail = true;
            Check(dispatcher.TryPublish(FeedbackCueKind.Released, new(1, 1), 1), "Independent cue kinds interfered.");
            await Until(() => dispatcher.Status.Error?.Contains("missing device", StringComparison.Ordinal) == true);
            output.Fail = false; Interlocked.Add(ref now, 200);
            dispatcher.TryPublish(FeedbackCueKind.Test, new(0, 2), 1);
            await Until(() => dispatcher.Status.Played == 2 && dispatcher.Status.Error is null);
        }
        Check(output.Disposed, "Feedback device resources were not disposed.");

        var held = new Output { Hold = true };
        using (var dispatcher = new FeedbackDispatcher(held, () => Interlocked.Read(ref now), 1000))
        {
            dispatcher.Configure(new(AudioEnabled: true, HapticsEnabled: true));
            dispatcher.TryPublish(FeedbackCueKind.Test, new(0, 1), 1);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int rejected = 0;
            for (int index = 2; index < 20; index++)
            {
                Interlocked.Add(ref now, 200);
                if (!dispatcher.TryPublish(FeedbackCueKind.Test, new(0, index), 1)) rejected++;
            }
            Check(rejected > 0 && dispatcher.Status.Dropped > 0, "Feedback queue was not bounded.");
            // A blocked device cannot block the producer/configuration path.
            await Task.Run(() => dispatcher.Configure(new())).WaitAsync(TimeSpan.FromSeconds(1));
            await Until(() => Volatile.Read(ref held.Active) == 0);
            Check(held.Calls == 1, "A settings change replayed stale queued cues.");
        }
        Check(held.Disposed && held.Active == 0, "Cancellation left a feedback pulse active.");

        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.Feedback." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            Check(store.LoadFeedbackSettings() == new FeedbackSettings(), "Missing preferences enabled feedback.");
            var settings = new FeedbackSettings(true, true, .3, .2); store.SaveFeedbackSettings(settings);
            Check(store.LoadFeedbackSettings() == settings, "Feedback preferences did not round-trip.");
            var profile = StudioProfile.CreateDefault(); store.Save(profile);
            Check(store.LoadAll().Count == 1, "Global feedback preferences were treated as a controller profile.");
            File.WriteAllText(Path.Combine(directory, "accessibility-feedback.json"), "{broken");
            Check(store.LoadFeedbackSettings() == new FeedbackSettings(), "Corrupt preferences enabled feedback.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 3000) throw new InvalidOperationException("Feedback worker did not reach the expected state."); await Task.Delay(5); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Output : IFeedbackOutput
    {
        internal volatile bool Fail, Hold;
        internal int Calls, Active;
        internal bool Disposed;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> PlayAsync(FeedbackCue cue, FeedbackSettings settings, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls); Interlocked.Increment(ref Active); Started.TrySetResult();
            try
            {
                if (Fail) throw new InvalidOperationException("missing device");
                if (Hold) await Task.Delay(Timeout.Infinite, cancellation);
                return null;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
