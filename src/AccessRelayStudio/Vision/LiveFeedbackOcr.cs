using System.Diagnostics;
using System.Text;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>Reads the post-shot HUD feedback banner asynchronously on captured crop frames so the
/// 60 Hz meter tracker and release scheduler stay untouched.</summary>
internal sealed class LiveFeedbackOcr : IDisposable
{
    private readonly string _modelDirectory;
    private readonly VisionCaptureModelSet _modelSet;
    private readonly Action<OcrFeedbackRead> _onFeedback;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private VisionAnalysisModels? _models;
    private string _lastRunId = "";
    private long _lastReceiptId;
    private VisionCrop? _discoveredCrop;
    private int _discoveredMisses;
    private string _priorBannerText = "";
    private long _priorBannerAt, _lastClearCheck, _lastClearFrame;
    private bool _previousBannerCleared;
    private int _clearObservations;

    internal LiveFeedbackOcr(string modelDirectory, VisionCaptureModelSet modelSet, Action<OcrFeedbackRead> onFeedback)
    {
        _modelDirectory = modelDirectory;
        _modelSet = modelSet;
        _onFeedback = onFeedback;
    }

    internal void Start(Func<VisionSession?> getSession, Func<StudioRuntime?> getRuntime, Func<VisionSettings> getSettings)
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false }) return;
            if (getRuntime()?.LastVisionAttempt is { } existing)
            {
                _lastRunId = existing.RunId;
                _lastReceiptId = existing.Id;
            }
            _cts?.Dispose();
            _cts = new();
            var ct = _cts.Token;
            _worker = Task.Run(() => RunLoopAsync(getSession, getRuntime, getSettings, ct), ct);
        }
    }

    internal void Stop()
    {
        Task? worker;
        lock (_gate)
        {
            _cts?.Cancel();
            worker = _worker;
            _models?.RequestStop();
        }
        try { worker?.Wait(TimeSpan.FromSeconds(1)); } catch { /* cancellation / shutdown timeout */ }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _worker = null;
        }
    }

    private async Task RunLoopAsync(Func<VisionSession?> getSession, Func<StudioRuntime?> getRuntime,
        Func<VisionSettings> getSettings, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                var session = getSession();
                var runtime = getRuntime();
                if (session is null || runtime is null || !session.Status.Running) continue;
                var receipt = runtime.LastVisionAttempt;
                if (receipt is null || (receipt.RunId == _lastRunId && receipt.Id == _lastReceiptId))
                {
                    if (_models is not null && _priorBannerAt > 0 && !_previousBannerCleared
                        && Stopwatch.GetElapsedTime(_lastClearCheck).TotalMilliseconds >= 500)
                    {
                        _lastClearCheck = Stopwatch.GetTimestamp();
                        var empty = await session.CaptureRegionOnceAsync(ShotFeedbackAnalysis.BuildBannerSearchCrop(_discoveredCrop ?? getSettings().TrainingCapture.FeedbackRegion), ct).ConfigureAwait(false);
                        if (empty is not null && empty.CaptureTicks > _lastClearFrame)
                        {
                            _lastClearFrame = empty.CaptureTicks;
                            var text = _models.ReadText(empty.Pixels, empty.Width, empty.Height, empty.Stride);
                            _clearObservations = ShotFeedbackAnalysis.HasHeading(text) ? 0 : _clearObservations + 1;
                            if (_clearObservations >= 2) _previousBannerCleared = true;
                        }
                    }
                    continue;
                }
                _lastRunId = receipt.RunId;
                _lastReceiptId = receipt.Id;

                var settings = getSettings();
                if (!ShouldRead(settings)) continue;
                // Read feedback for automatic strokes started by an assigned control.
                if (!runtime.VisionAction.HasAssignedTrigger) continue;

                // Ensure OCR reader models are loaded lazily on this background worker (CPU-only, skip pose).
                _models ??= new(_modelSet, VisionExecutionProvider.Cpu, _modelDirectory, loadPose: false);

                var result = await WatchShotFeedbackAsync(session, receipt, settings,
                    () => getRuntime()?.LastVisionAttempt?.Id ?? 0, () => getRuntime()?.VisionStatus.HoldingCancelledTriggerShot == true, ct).ConfigureAwait(false);
                if (result is not null && (getRuntime()?.LastVisionAttempt?.Id ?? 0) == receipt.Id)
                    _onFeedback(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Never crash live session on background OCR read */ }
        }
    }

    private static bool ShouldRead(VisionSettings settings)
    {
        if (settings.PluginAssistance is not { } json) return false;
        if (json.TryGetProperty("enabled", out var en) && !en.GetBoolean()) return false;
        if (json.TryGetProperty("autoOcrTiming", out var auto) && auto.ValueKind is System.Text.Json.JsonValueKind.False)
            return false;
        return true;
    }

    private async Task<OcrFeedbackRead?> WatchShotFeedbackAsync(VisionSession session, VisionActionReceipt receipt,
        VisionSettings settings, Func<long> getLatestReceiptId, Func<bool> holdingCancelledTriggerShot, CancellationToken ct)
    {
        // A pre-stroke tracking cancellation can still be holding down for the user.
        // Its HUD result belongs after their physical release, not the cancellation instant.
        while (receipt.Outcome == VisionActionOutcome.Cancelled && holdingCancelledTriggerShot())
        {
            if (getLatestReceiptId() != receipt.Id) return null;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        long watchStarted = Math.Max(receipt.CompletedTimestamp, Stopwatch.GetTimestamp());
        double elapsedSinceCompleteMs = Stopwatch.GetElapsedTime(receipt.CompletedTimestamp).TotalMilliseconds;
        if (elapsedSinceCompleteMs < 180)
            await Task.Delay((int)Math.Ceiling(180 - elapsedSinceCompleteMs), ct).ConfigureAwait(false);

        var preferredSourceCrop = _discoveredCrop ?? settings.TrainingCapture.FeedbackRegion;
        var crop = ShotFeedbackAnalysis.BuildBannerSearchCrop(preferredSourceCrop);
        bool sawClearedSincePrevious = _priorBannerAt == 0 || _previousBannerCleared;
        string candidate = ""; int agreements = 0, clearFrames = 0; long candidateCapture = 0;
        string bestUnparsedRaw = "";
        long lastCaptureTicks = receipt.CompletedTimestamp;
        double lastProcMs = 0;

        for (int attempt = 0; attempt < 14 && !ct.IsCancellationRequested; attempt++)
        {
            if (getLatestReceiptId() != receipt.Id) return null;
            if (Stopwatch.GetElapsedTime(watchStarted).TotalMilliseconds > 2600) break;
            // Cycle across configured/discovered crop, top band, middle above-player band (y=0.24..0.72), and lower band
            // so custom HUD or above-player banners are never stranded in a vertical blind spot.
            var activeCrop = ShotFeedbackAnalysis.SelectSearchCrop(attempt, crop);
            var frame = await session.CaptureRegionOnceAsync(activeCrop, ct).ConfigureAwait(false);
            if (frame is null || getLatestReceiptId() != receipt.Id) return null;

            if (frame.CaptureTicks <= lastCaptureTicks)
            { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
            long t0 = Stopwatch.GetTimestamp();
            var regions = _models!.ReadText(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            double procMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lastCaptureTicks = frame.CaptureTicks;
            lastProcMs = procMs;
            var parsed = ShotFeedbackAnalysis.ParseFeedback(regions);
            if (parsed is null)
            {
                if (regions.Length > 0)
                {
                    string rawCandidate = string.Join(" | ", regions.Where(r => !string.IsNullOrWhiteSpace(r.Text))
                        .Select(r => ShotFeedbackAnalysis.CleanOcrLine(r.Text)).Where(t => t.Length > 0));
                    if (rawCandidate.Length > bestUnparsedRaw.Length)
                        bestUnparsedRaw = rawCandidate;
                }
                // Only scanning the primary banner crop proves the previous shot's banner disappeared.
                agreements = 0;
                if (activeCrop == crop && !ShotFeedbackAnalysis.HasHeading(regions) && ++clearFrames >= 2)
                    sawClearedSincePrevious = true;
            }
            else
            {
                // Previous-shot banners commonly remain visible 750-950 ms into quick follow-up shots.
                if (!sawClearedSincePrevious && ShotFeedbackAnalysis.GradeIdentity(parsed) == _priorBannerText)
                {
                    await Task.Delay(170, ct).ConfigureAwait(false);
                    continue;
                }
                string identity = ShotFeedbackAnalysis.Identity(parsed);
                if (frame.CaptureTicks <= candidateCapture) continue;
                candidateCapture = frame.CaptureTicks;
                agreements = candidate == identity ? agreements + 1 : 1;
                candidate = identity;
                if (agreements < 2) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
                if (parsed.BannerBounds is { } localBounds)
                {
                    _discoveredCrop = ShotFeedbackAnalysis.ProjectCrop(activeCrop, localBounds);
                    _discoveredMisses = 0;
                }
                _priorBannerText = ShotFeedbackAnalysis.GradeIdentity(parsed);
                _previousBannerCleared = false; _clearObservations = 0;
                _priorBannerAt = frame.CaptureTicks;
                return new(receipt.Id, receipt.RunId, parsed.ShotType, parsed.Timing, parsed.Tempo,
                    parsed.RawText, frame.CaptureTicks, procMs, parsed.Distance, parsed.Coverage);
            }
            await Task.Delay(175, ct).ConfigureAwait(false);
        }
        if (_discoveredCrop is not null && ++_discoveredMisses >= 2)
        {
            _discoveredCrop = null;
            _discoveredMisses = 0;
        }
        return new(receipt.Id, receipt.RunId, "unrecognized", "none", "none", bestUnparsedRaw, lastCaptureTicks, lastProcMs);
    }

    public void Dispose() => Stop();
}
