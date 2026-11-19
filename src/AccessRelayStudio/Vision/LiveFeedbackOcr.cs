using System.Diagnostics;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed class LiveFeedbackOcr(Action<VisionActionReceipt, OcrFeedbackRead> onFeedback) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private VisionAnalysisModels? _models;
    private Task? _worker;
    private FeedbackHeadingProbe? _probe;
    private long _clearedAt;
    private string _status = "Loading the shot feedback reader.";
    internal string Status => Volatile.Read(ref _status);

    internal void Start(VisionSession session, StudioRuntime runtime, VisionSettings settings,
        Func<VisionAnalysisModels> loadModels)
    {
        long cursor = runtime.LastVisionAttempt?.Id ?? 0;
        _worker = Task.Run(() => RunAsync(session, runtime, settings, loadModels, cursor, _stop.Token));
    }

    private async Task RunAsync(VisionSession session, StudioRuntime runtime, VisionSettings settings,
        Func<VisionAnalysisModels> loadModels, long cursor, CancellationToken ct)
    {
        string? unavailable = null;
        var sequence = new ShotFeedbackBannerSequence();
        var primary = ShotFeedbackAnalysis.BuildBannerSearchCrop(settings.TrainingCapture.FeedbackRegion);
        VisionActionReceipt? pending = null;
        long after = 0;
        int misses = 0;
        Task? probeWorker = null;
        using var probeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Finish(string reason)
        {
            if (pending is not { } shot) return;
            pending = null;
            onFeedback(shot, new(shot.Id, shot.RunId, "unrecognized", "none", "none", "", 0, 0, Disposition: reason));
        }
        void Collect(long through)
        {
            foreach (var receipt in runtime.VisionAttemptsAfter(cursor))
            {
                if (receipt.CompletedTimestamp > through) break;
                Finish("The next attempt finished before a fresh HUD result was confirmed.");
                cursor = receipt.Id; pending = receipt; after = receipt.CompletedTimestamp; misses = 0;
                if (unavailable is not null) Finish(unavailable);
                else if (!runtime.VisionAction.HasAssignedTrigger) Finish("This attempt had no assigned shot trigger.");
            }
        }
        try
        {
            if (settings.PluginAssistance is not { } config
                || config.TryGetProperty("autoOcrTiming", out var enabled) && enabled.ValueKind == System.Text.Json.JsonValueKind.False)
                unavailable = "Automatic shot feedback is turned off.";
            else
            {
                try { var models = loadModels(); lock (_gate) _models = models; }
                catch (Exception ex) { unavailable = "Shot feedback reader unavailable. " + ex.GetBaseException().Message; }
            }
            Volatile.Write(ref _status, unavailable ?? "Shot feedback reader ready.");
            if (unavailable is null) probeWorker = WatchHeadingAsync(session, probeStop.Token);
            while (!ct.IsCancellationRequested)
            {
                Collect(Stopwatch.GetTimestamp());
                if (unavailable is not null)
                { await Task.Delay(80, ct).ConfigureAwait(false); continue; }
                if (pending is { Outcome: VisionActionOutcome.Cancelled } && runtime.VisionStatus.HoldingCancelledTriggerShot)
                    after = Stopwatch.GetTimestamp();
                if (pending is not null && Stopwatch.GetElapsedTime(after).TotalSeconds > 3)
                    Finish("No fresh HUD result was confirmed before the feedback window closed.");
                // Prefer the known HUD. Probe alternative layouts after sustained misses in the primary crop.
                bool searching = pending is not null && misses >= 5 && Stopwatch.GetElapsedTime(after).TotalMilliseconds > 1200;
                var crop = searching && misses % 2 == 1 ? ShotFeedbackAnalysis.SelectSearchCrop(2 + (misses / 2) % 4, primary) : primary;
                var frame = await session.CaptureRegionOnceAsync(crop, ct).ConfigureAwait(false);
                if (frame is null) { await Task.Delay(40, ct).ConfigureAwait(false); continue; }
                long began = Stopwatch.GetTimestamp();
                var regions = _models!.ReadText(frame.Pixels, frame.Width, frame.Height, frame.Stride);
                var banner = ShotFeedbackAnalysis.ParseFeedback(regions);
                long cleared = Volatile.Read(ref _clearedAt);
                if (cleared < frame.CaptureTicks) sequence.ConfirmClear(cleared);
                var appearance = sequence.Observe(frame.CaptureTicks, banner, ShotFeedbackAnalysis.HasHeading(regions), crop == primary);
                if (banner is not null && (_probe is null || appearance is not null)
                    && regions.FirstOrDefault(r => r.Confidence >= .8 && ShotFeedbackAnalysis.HasHeading([r])) is { } heading)
                    Volatile.Write(ref _probe, FeedbackHeadingProbe.Create(frame, crop, heading));
                if (banner is null) misses++; else misses = 0;
                if (banner?.BannerBounds is { } box && crop != primary)
                    primary = ShotFeedbackAnalysis.BuildBannerSearchCrop(ShotFeedbackAnalysis.ProjectCrop(crop, box));
                Collect(frame.CaptureTicks);
                if (pending is { } shot && appearance is { } fresh && fresh.StartedAt > after)
                {
                    pending = null;
                    var result = fresh.Banner;
                    onFeedback(shot, new(shot.Id, shot.RunId, result.ShotType, result.Timing, result.Tempo, result.RawText,
                        frame.CaptureTicks, Stopwatch.GetElapsedTime(began).TotalMilliseconds, result.Distance, result.Coverage));
                }
                await Task.Delay(pending is not null ? 10 : 90, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            unavailable = "Shot feedback reader failed. " + ex.GetBaseException().Message;
            Volatile.Write(ref _status, unavailable);
            Finish(unavailable);
            try
            {
                while (!ct.IsCancellationRequested) { Collect(Stopwatch.GetTimestamp()); await Task.Delay(80, ct).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        finally
        {
            probeStop.Cancel();
            if (probeWorker is not null)
                try { await probeWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Collect(Stopwatch.GetTimestamp());
            Finish("Gameplay capture ended before a fresh HUD result was confirmed.");
            lock (_gate) { _models?.Dispose(); _models = null; }
        }
    }

    private async Task WatchHeadingAsync(VisionSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var probe = Volatile.Read(ref _probe);
            if (probe is not null && await session.CaptureRegionOnceAsync(probe.Region, ct).ConfigureAwait(false) is { } frame
                && probe == Volatile.Read(ref _probe) && probe.Cleared(frame))
                Volatile.Write(ref _clearedAt, frame.CaptureTicks);
            await Task.Delay(probe is null ? 60 : 20, ct).ConfigureAwait(false);
        }
    }

    internal void Stop()
    {
        _stop.Cancel();
        lock (_gate) _models?.RequestStop();
        try { _worker?.GetAwaiter().GetResult(); }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() { Stop(); _stop.Dispose(); }
}
