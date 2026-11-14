using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionTrainingStatus(bool HasTake, bool IsRecording, bool IsFinalized,
    bool IsComplete, string DirectoryPath, long StoredFrames, long DroppedFrames, string Message, bool AcquisitionOnly = false);

/// <summary>Opt-in, bounded synchronized visual/controller evidence writer. Observations are not target labels.</summary>
internal sealed class VisionTrainingRecorder : IDisposable
{
    internal const int StoredWidth = 256, StoredHeight = 256, FrameBytes = StoredWidth * StoredHeight * 4;
    internal const long EvidenceByteLimit = 2L * 1024 * 1024 * 1024;
    internal const long SceneEvidenceByteLimit = 4L * 1024 * 1024 * 1024;
    private sealed record StoredRegion(string Name, string File, int Width, int Height, VisionCrop Crop)
    {
        internal int ByteLength => checked(Width * Height * 4);
        internal BgraCropResizer Resizer { get; } = new(Width, Height);
    }
    private sealed record RegionFrameEntry(string Name, string File, long OffsetBytes, int ByteLength);
    private sealed record Pending(byte[] Pixels, long Sequence, long CaptureTicks, long ObservedTicks,
        long ShotToken, bool PhysicalShootHeld, long SourceGeneration, long? CompositorTicks, VisionRegionObservation? Tracking,
        long PreparedTicks, double? CaptureReadbackMs, CapturePipelineSnapshot? CapturePipeline);
    private sealed record FrameEntry(long Sequence, long CaptureTicks, string File, long OffsetBytes, int ByteLength,
        long ShotToken, bool PhysicalShootHeld, long ObservedTicks, long SourceGeneration, RegionFrameEntry[]? Evidence, long? CompositorTicks, VisionRegionObservation? Tracking,
        double? CaptureReadbackMs, double PreparationMs, double WriterQueueMs, double WriteMs);
    private readonly Channel<Pending> _frames = Channel.CreateBounded<Pending>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<FrameEntry> _index = [];
    private readonly IVisionCaptureProfile _profile;
    private readonly VisionSettings _settings;
    private readonly string _sourceTitle;
    private readonly Task _writer;
    private readonly Timer _timeout;
    private readonly Func<string, Stream> _createStream;
    private readonly TimeSpan _durationLimit;
    private readonly StoredRegion[] _regions;
    private readonly int _bytesPerFrame;
    private readonly long _byteLimit;
    private readonly VisionTrainingTrace? _controllerTrace;
    private Action? _detachControllerTrace;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _createdAtUtc = DateTimeOffset.UtcNow;
    private readonly object _gate = new();
    private long _generation = -1, _dropped, _storedFrames;
    private int _width, _height;
    private bool _finished, _complete = true, _metadataComplete;
    private readonly CapturePipelineSnapshot? _capturePipelineAtStart;
    private CapturePipelineSnapshot? _capturePipelineAtLastFrame;
    private string _message = "Recording a 10-second manual training take.";
    internal string DirectoryPath { get; }
    private bool AcquisitionOnly => _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst && _settings.TrainingCapture.FeedbackRegion is null;
    internal string Message { get { lock (_gate) return _message; } }
    internal bool IsRecording { get { lock (_gate) return !_finished; } }
    internal bool IsFinalized => _writer.IsCompleted;
    internal VisionTrainingStatus Snapshot
    {
        get
        {
            lock (_gate) return new(true, !_finished, _writer.IsCompleted, _metadataComplete && _writer.IsCompleted,
                DirectoryPath, Interlocked.Read(ref _storedFrames), Interlocked.Read(ref _dropped), _message, AcquisitionOnly);
        }
    }

    internal VisionTrainingRecorder(string parentDirectory, IVisionCaptureProfile profile, VisionSettings settings, string sourceTitle,
        Func<string, Stream>? streamFactory = null, TimeSpan? durationLimit = null,
        VisionTrainingTrace? controllerTrace = null, Action? detachControllerTrace = null, long? byteLimit = null,
        CapturePipelineSnapshot? capturePipelineAtStart = null)
    {
        profile.Validate(); settings.Validate();
        // Legacy primary-only fixtures remain readable; the UI/session requires configured multi-region evidence.
        if (settings.TrainingCapture.Configured) profile.ValidateCapture(settings.TrainingCapture);
        if (settings.TrainingCapture.Configured && !VisionTrainingCaptureSettings.Contains(settings.TrainingCapture.GameplayRegion, settings.Crop))
            throw new InvalidDataException("The primary region must be inside the gameplay scene.");
        _regions = profile.EvidenceRegions(settings).Select(r => new StoredRegion(r.Name, r.File, r.Width, r.Height, r.Crop)).ToArray();
        _bytesPerFrame = _regions.Sum(region => region.ByteLength);
        long productionByteLimit = settings.TrainingCapture.Configured
            ? settings.TrainingCapture.EvidenceMode is VisionEvidenceMode.SceneFirst or VisionEvidenceMode.Anchored or VisionEvidenceMode.Automatic ? SceneEvidenceByteLimit : EvidenceByteLimit
            : 512L * 1024 * 1024;
        _byteLimit = byteLimit ?? productionByteLimit;
        if (_byteLimit < _bytesPerFrame || _byteLimit > productionByteLimit)
            throw new ArgumentOutOfRangeException(nameof(byteLimit));
        _controllerTrace = controllerTrace; _detachControllerTrace = detachControllerTrace;
        _capturePipelineAtStart = capturePipelineAtStart;
        // Production defaults: 10 seconds, at most 2 GiB across visual streams, eight pending frames.
        _durationLimit = durationLimit ?? TimeSpan.FromSeconds(10);
        if (_durationLimit <= TimeSpan.Zero || _durationLimit > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(durationLimit));
        _createStream = streamFactory ?? (static path => new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        var parent = Path.GetFullPath(parentDirectory);
        Directory.CreateDirectory(parent);
        if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < _byteLimit + 64L * 1024 * 1024)
            throw new IOException(settings.TrainingCapture.Configured
                ? $"Training evidence needs at least {(_byteLimit + 64L * 1024 * 1024) / (1024d * 1024 * 1024):F1} GiB free in the selected location."
                : "Training capture needs at least 576 MiB of free space in the selected location.");
        DirectoryPath = Path.Combine(parent, "take-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        _profile = profile; _settings = settings; _sourceTitle = sourceTitle;
        if (AcquisitionOnly) _message = "Recording full gameplay for offline player/HUD preparation.";
        // Metadata is written immediately as incomplete, and replaced after the writer finishes.
        WriteMetadata(complete: false);
        _writer = Task.Run(WriteAsync);
        _timeout = new Timer(_ =>
        {
            lock (_gate) FinishLocked(AcquisitionOnly ? "Gameplay clip saved. Open it to prepare and review player/HUD regions." : "Training take complete. Review and label the recorded frames before training.", true);
        }, null, _durationLimit, Timeout.InfiniteTimeSpan);
    }

    internal void Capture(CapturedVisionFrame frame, VisionStatusSnapshot output, VisionRegionObservation? tracking = null,
        CapturePipelineSnapshot? capturePipeline = null)
    {
        long observedTicks = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_finished) return;
            if (_settings.TrainingCapture.EvidenceMode is VisionEvidenceMode.Anchored or VisionEvidenceMode.Automatic && (tracking is not { Valid: true } || tracking.Sequence != frame.Sequence))
            { FinishLocked("Anchor tracking lost. Take is incomplete: " + (tracking?.Message ?? "No region observation arrived."), false); return; }
            if (_settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic &&
                (tracking?.Identity is not { CaseSensitive: true } identity || identity.ExpectedTag != identity.ObservedTag || identity.VerifiedSequence > frame.Sequence
                    || tracking.Keypoints is not { Length: 17 } || tracking.Court.Length < 3))
            { FinishLocked("Player identity, pose or court evidence was lost. Take is incomplete.", false); return; }
            if (Stopwatch.GetElapsedTime(_started) >= _durationLimit)
            { FinishLocked(AcquisitionOnly ? "Gameplay clip saved. Open it to prepare and review player/HUD regions." : "Training take complete. Review and label the recorded frames before training.", true); return; }
            if (_settings.TrainingCapture.Configured && (frame.Width != _settings.TrainingCapture.SourceWidth ||
                frame.Height != _settings.TrainingCapture.SourceHeight))
            { FinishLocked("Capture size changed since HUD setup. Take marked incomplete; confirm the regions on a fresh preview.", false); return; }
            if (_generation >= 0 && (_generation != frame.SourceGeneration || _width != frame.Width || _height != frame.Height))
            { FinishLocked("Source resized or changed. Take marked incomplete; record a new take.", false); return; }
            _generation = frame.SourceGeneration; _width = frame.Width; _height = frame.Height;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(_bytesPerFrame);
        try
        {
            int offset = 0;
            foreach (var region in _regions)
            {
                bool dynamicRegions = _settings.TrainingCapture.EvidenceMode is VisionEvidenceMode.Anchored or VisionEvidenceMode.Automatic;
                VisionCrop? resolved = dynamicRegions ? region.Name == "player" ? tracking?.Player : region.Name == "meter" ? tracking?.Meter : null : null;
                ResizeCrop(frame, buffer.AsSpan(offset, region.ByteLength), region, resolved);
                offset += region.ByteLength;
            }
            if (_frames.Writer.TryWrite(new(buffer, frame.Sequence, frame.CaptureTicks, observedTicks,
                    output.ShotToken, output.ShootHeld, frame.SourceGeneration, frame.CompositorTicks, tracking,
                    Stopwatch.GetTimestamp(), frame.CaptureReadbackMs, capturePipeline)))
                buffer = null!;
            else lock (_gate)
            {
                // Stop/timer can finalize while this producer finishes cropping. That rejected
                // post-stop frame must not mutate counters after metadata was already finalized.
                if (!_finished) Interlocked.Increment(ref _dropped);
            }
        }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ResizeCrop(CapturedVisionFrame frame, Span<byte> target, StoredRegion region, VisionCrop? resolved = null)
    {
        region.Resizer.Resize(frame.Pixels.Span, frame.Width, frame.Height, frame.Stride, target, resolved ?? region.Crop);
    }

    private async Task WriteAsync()
    {
        var streams = new List<Stream>();
        try
        {
            foreach (var region in _regions) streams.Add(_createStream(Path.Combine(DirectoryPath, region.File)));
            long totalBytes = 0;
            await foreach (var frame in _frames.Reader.ReadAllAsync())
            {
                try
                {
                    long writeStarted = Stopwatch.GetTimestamp();
                    if (totalBytes + _bytesPerFrame > _byteLimit)
                    {
                        lock (_gate) FinishLocked("Take reached its visual evidence storage limit.", true);
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }
                    long offset = streams[0].Position;
                    var evidence = _regions.Length > 1 ? new RegionFrameEntry[_regions.Length - 1] : null;
                    int bufferOffset = 0;
                    for (int index = 0; index < _regions.Length; index++)
                    {
                        var region = _regions[index];
                        long regionOffset = streams[index].Position;
                        await streams[index].WriteAsync(frame.Pixels.AsMemory(bufferOffset, region.ByteLength));
                        if (index > 0) evidence![index - 1] = new(region.Name, region.File, regionOffset, region.ByteLength);
                        bufferOffset += region.ByteLength;
                    }
                    totalBytes += _bytesPerFrame;
                    _index.Add(new(frame.Sequence, frame.CaptureTicks, "frames.bgra", offset, FrameBytes,
                        frame.ShotToken, frame.PhysicalShootHeld, frame.ObservedTicks, frame.SourceGeneration, evidence, frame.CompositorTicks, frame.Tracking,
                        frame.CaptureReadbackMs, Stopwatch.GetElapsedTime(frame.ObservedTicks, frame.PreparedTicks).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(frame.PreparedTicks, writeStarted).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds));
                    _capturePipelineAtLastFrame = frame.CapturePipeline;
                    Interlocked.Increment(ref _storedFrames);
                }
                finally { ArrayPool<byte>.Shared.Return(frame.Pixels); }
            }
            foreach (var stream in streams) await stream.FlushAsync();
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                DetachControllerTrace();
                _finished = true; _complete = false;
                _message = "Training capture failed: " + error.Message;
                _frames.Writer.TryComplete();
            }
            while (_frames.Reader.TryRead(out var pending)) ArrayPool<byte>.Shared.Return(pending.Pixels);
        }
        finally
        {
            DetachControllerTrace();
            foreach (var stream in streams)
                try { await stream.DisposeAsync(); }
                catch (Exception error) { lock (_gate) { _complete = false; _message = "Training stream finalization failed: " + error.Message; } }
            lock (_gate) { _finished = true; if (_index.Count == 0) _complete = false; }
            try
            {
                if (_controllerTrace is { } trace)
                {
                    var snapshot = trace.Snapshot();
                    if (snapshot.Samples.Length == 0 || snapshot.DroppedSamples > 0)
                        lock (_gate) { _complete = false; _message = "Controller trace is missing or exceeded its bound; evidence is incomplete."; }
                    WriteJson("controller-trace.json", new { schemaVersion = 1, stopwatchFrequency = Stopwatch.Frequency,
                        timingScope = "PC physical sampling and locally accepted output requests. Acceptance may retain a duplicate report; Xbox receipt/application is not measured.",
                        samples = snapshot.Samples, droppedSamples = snapshot.DroppedSamples, capacity = snapshot.Capacity });
                }
                WriteMetadata(_complete);
                lock (_gate) _metadataComplete = _complete;
            }
            catch (Exception error)
            {
                lock (_gate) { _complete = false; _message = "Could not finalize take metadata: " + error.Message; }
            }
        }
    }

    private void WriteMetadata(bool complete)
    {
        var metadata = new
        {
            schemaVersion = 1, sessionId = Path.GetFileName(DirectoryPath), createdAtUtc = _createdAtUtc,
            shotProfileFingerprint = _profile.ConfigurationFingerprint, animationProfile = _settings.AnimationProfile,
            cameraView = _profile.CameraView,
            captureWidth = _width, captureHeight = _height, stride = _width * 4, crop = _settings.Crop,
            storedWidth = StoredWidth, storedHeight = StoredHeight, storedStride = StoredWidth * 4, pixelFormat = "BGRA8",
            preprocessing = _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst
                ? "scene-first-untracked-v1" : "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2",
            sourceTitle = _sourceTitle, shotProfile = _profile.ExportProfile(), stopwatchFrequency = Stopwatch.Frequency,
            frames = _index, droppedFrames = Interlocked.Read(ref _dropped), complete, completionReason = _message,
            capturePipeline = new { atStart = _capturePipelineAtStart, atLastRecordedFrame = _capturePipelineAtLastFrame,
                writerQueueDrops = Interlocked.Read(ref _dropped),
                scope = "WGC deliveries, superseded frames, publication failures and pending-buffer losses are separate from writer-queue drops. Unreported OS/stream frames are not inferred.",
                stageTimingScope = "Per-frame readback, preparation, writer wait and OS write completion. Physical disk durability and Xbox render time are not measured." },
            labelsRequired = "Target release labels require reviewed practice experiments; physical release and ball departure are not automatically ground truth.",
            captureTimeBasis = "wgc-local-receipt-qpc-v1",
            timingScope = "captureTicks marks local WGC frame receipt before image copies. compositorTicks preserves WGC metadata separately and can lead receipt. Original Xbox render time is not measured.",
            captureEvidence = _settings.TrainingCapture.Configured ? new
            {
                version = _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic ? 4 : _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Anchored ? 3 : _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst ? 2 : 1,
                purpose = _settings.TrainingCapture.Purpose, layout = _settings.TrainingCapture,
                acquisitionOnly = _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst && _settings.TrainingCapture.FeedbackRegion is null,
                tracking = _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic ? "identity-pose-v1" : _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Anchored ? "visual-anchors-v1" : _settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst ? "unannotated-scene" : "fixed-region",
                streams = _regions.Select(region => new { region.Name, region.File, region.Width, region.Height,
                    stride = region.Width * 4, pixelFormat = "BGRA8", region.Crop }),
                controllerTraceFile = _controllerTrace is not null ? "controller-trace.json" : null,
                inputComposition = _controllerTrace?.InputComposition,
                controllerSamples = _controllerTrace?.StoredSamples ?? 0,
                droppedControllerSamples = _controllerTrace?.DroppedSamples ?? 0,
                maximumVisualBytes = _byteLimit,
                labelStatus = "Raw synchronized evidence only. Meter, tempo, contest and feedback labels still require extraction and review. The trainer consumes reviewed, integrity-bound converted player crops; pose and HUD streams are retained for audit and future supervision.",
                shotTokenScope = "Diagnostic arbiter token, normally zero while manual training disarms assistance; not a gameplay shot identifier."
            } : null
        };
        WriteJson("session.json", metadata);
        if (!File.Exists(Path.Combine(DirectoryPath, "labels.example.json")))
            File.WriteAllText(Path.Combine(DirectoryPath, "labels.example.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, sessionId = Path.GetFileName(DirectoryPath), labelTimeBasis = "captured-target-game-phase", split = "train", shots = Array.Empty<object>(),
                instructions = "Copy to labels.json. Add reviewed shots with shotId, startTicks, endTicks, targetReleaseTicks, isTargetShot, playerIdentityConfirmed. See docs/vision-model-workflow.md."
            }, JsonOptions));
    }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private void WriteJson(string filename, object document)
    {
        string temporary = Path.Combine(DirectoryPath, filename + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, Path.Combine(DirectoryPath, filename), overwrite: true);
    }
    private void DetachControllerTrace() => Interlocked.Exchange(ref _detachControllerTrace, null)?.Invoke();
    private void FinishLocked(string message, bool complete)
    { if (_finished) return; _finished = true; _complete = complete; _message = message; DetachControllerTrace(); _frames.Writer.TryComplete(); }
    internal void Stop(bool complete = false) { lock (_gate) FinishLocked(complete ? "Take stopped; review labels." : "Take stopped early; marked incomplete.", complete); }
    public void Dispose() { _timeout.Dispose(); Stop(); _writer.GetAwaiter().GetResult(); }
}
