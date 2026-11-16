using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed record VisionDiagnosticStatus(bool IsRecording, bool IsFinalized, bool Failed,
    string DirectoryPath, long ObservedFrames, long StoredFrames, long DroppedFrames, string Message);

/// <summary>Explicit, bounded recording of the exact analyzed pixels and their decisions. Never controls output.</summary>
internal sealed class VisionDiagnosticRecorder : IDisposable
{
    internal const long MaximumBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private sealed record FrameEntry(long Sequence, long CaptureTicks, long? CompositorTicks, long SourceGeneration,
        int Width, int Height, int Stride, long CompletedTicks, double ProcessingMs,
        VisionSignalObservation? Signal, VisionSignalContext? PhysicalContext, ControllerState? RequestedOutput,
        VisionStatusSnapshot Output, VisionActionReceipt? LastAction, bool SourceFresh,
        bool PredictionSubmitted, bool PredictionAccepted, CapturePipelineSnapshot? CapturePipeline)
    {
        public string? File { get; set; }
        public string ImageStatus { get; set; } = "pending";
        public double CopyMs { get; set; }
        public double QueueMs { get; set; }
        public double WriteMs { get; set; }
        public long CompressedBytes { get; set; }
    }
    private sealed record Pending(byte[] Pixels, int Length, FrameEntry Entry, long QueuedAt);
    private readonly object _gate = new();
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new() { SingleReader = true, SingleWriter = true });
    // Reserve a slot before copying. A slow disk cannot accumulate full-resolution images in memory.
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly List<FrameEntry> _frames = [];
    private readonly Task _writer;
    private readonly Timer _timeout;
    private readonly object _context;
    private readonly Func<string, Stream> _createStream;
    private readonly long _byteLimit;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private long _stored, _dropped, _bytes;
    private bool _stopped, _finalized, _failed;
    private string? _stopReason;
    private string _message = "Recording diagnostics. Take a practice shot; recording stops after 15 seconds.";
    internal string DirectoryPath { get; }
    internal VisionDiagnosticStatus Snapshot
    {
        get { lock (_gate) return new(!_stopped, _finalized, _failed, DirectoryPath, _frames.Count, _stored, _dropped, _message); }
    }
    internal Task Completion => _writer;

    internal VisionDiagnosticRecorder(string parent, object context, Func<string, Stream>? createStream = null,
        TimeSpan? duration = null, long byteLimit = MaximumBytes)
    {
        var limit = duration ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(15) || byteLimit <= 0 || byteLimit > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(duration));
        _byteLimit = byteLimit; _context = context;
        _createStream = createStream ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.SequentialScan));
        parent = Path.GetFullPath(parent);
        Directory.CreateDirectory(parent);
        if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < byteLimit + 64L * 1024 * 1024)
            throw new IOException("Diagnostic recording needs at least 1.1 GiB free in its destination.");
        DirectoryPath = Path.Combine(parent, "diagnostic-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        using (var review = typeof(VisionDiagnosticRecorder).Assembly.GetManifestResourceStream("VisionDiagnosticReview.html")
            ?? throw new InvalidOperationException("The diagnostic viewer is missing from the installation."))
        using (var destination = File.Create(Path.Combine(DirectoryPath, "review.html"))) review.CopyTo(destination);
        WriteManifest();
        _writer = Task.Run(WriteFrames);
        _timeout = new Timer(_ => Stop("The 15-second recording limit was reached."), null, limit, Timeout.InfiniteTimeSpan);
    }

    internal void Capture(CapturedVisionFrame frame, long completedTicks, double processingMs,
        VisionSignalObservation? signal, VisionSignalContext? physical, ControllerState? requested,
        VisionStatusSnapshot output, VisionActionReceipt? receipt, bool fresh, bool submitted, bool accepted,
        CapturePipelineSnapshot? pipeline)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_frames.Count >= 4096) { Stop("The diagnostic frame limit was reached."); return; }
            var entry = new FrameEntry(frame.Sequence, frame.CaptureTicks, frame.CompositorTicks, frame.SourceGeneration,
                frame.Width, frame.Height, frame.Stride, completedTicks, processingMs, signal, physical,
                requested, output, receipt, fresh, submitted, accepted, pipeline);
            _frames.Add(entry);
            if (!_slots.Wait(0)) { entry.ImageStatus = "writerQueueFull"; _dropped++; return; }
            byte[]? pixels = null;
            long began = Stopwatch.GetTimestamp();
            try
            {
                int length = checked(frame.Stride * frame.Height);
                pixels = ArrayPool<byte>.Shared.Rent(length);
                frame.Pixels.Span.CopyTo(pixels);
                entry.CopyMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                if (!_queue.Writer.TryWrite(new(pixels, length, entry, Stopwatch.GetTimestamp())))
                    throw new IOException("The diagnostic image writer is closed.");
            }
            catch (Exception error)
            {
                if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
                _slots.Release(); entry.ImageStatus = "copyFailed"; _dropped++;
                Fail(error);
            }
        }
    }

    internal void Stop(string reason = "Recording stopped by the player.")
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true; _stopReason = reason; _message = reason + " Saving diagnostic recording…";
            _queue.Writer.TryComplete();
        }
    }
    private void Fail(Exception error)
    {
        lock (_gate) { _failed = true; Stop(); _message = "Diagnostic recording failed: " + error.Message; }
    }
    private async Task WriteFrames()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    var entry = pending.Entry;
                    if (_failed) { entry.ImageStatus = "writerFailed"; lock (_gate) _dropped++; continue; }
                    if (_bytes >= _byteLimit) { entry.ImageStatus = "sizeLimit"; lock (_gate) _dropped++; continue; }
                    long began = Stopwatch.GetTimestamp();
                    entry.QueueMs = Stopwatch.GetElapsedTime(pending.QueuedAt, began).TotalMilliseconds;
                    string name = $"frame-{entry.Sequence:D8}.bgra.gz";
                    using (var file = _createStream(Path.Combine(DirectoryPath, name)))
                    {
                        using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                            gzip.Write(pending.Pixels, 0, pending.Length);
                        entry.CompressedBytes = file.Position;
                    }
                    entry.File = name; entry.ImageStatus = "saved";
                    entry.WriteMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                    lock (_gate)
                    {
                        _bytes += entry.CompressedBytes; _stored++;
                        // One final frame can exceed the soft compressed-byte limit.
                        if (_bytes >= _byteLimit) Stop("The diagnostic size limit was reached.");
                    }
                }
                catch (Exception error) { pending.Entry.ImageStatus = "writeFailed"; lock (_gate) _dropped++; Fail(error); }
                finally { ArrayPool<byte>.Shared.Return(pending.Pixels); _slots.Release(); }
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            try
            {
                lock (_gate)
                {
                    if (!_failed) _message = _stored == 0 ? "No frames were recorded. Start another diagnostic recording while the preview is running."
                        : $"Diagnostic recording saved: {_stored} images, {_dropped} images skipped. Detection details retained for all {_frames.Count} observed frames.";
                    _finalized = true;
                    WriteManifest();
                }
            }
            catch (Exception error) { lock (_gate) { _failed = true; _finalized = true; _message = "Could not save the diagnostic report: " + error.Message; } }
        }
    }
    private void WriteManifest()
    {
        string path = Path.Combine(DirectoryPath, "diagnostic.json"), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            version = 1, createdAtUtc = _createdAt, stopwatchFrequency = Stopwatch.Frequency, startedTicks = _started,
            finalized = _finalized, failed = _failed, message = _message, stopReason = _stopReason, context = _context,
            scope = "Diagnostic observations, not training labels. Exact native-resolution BGRA8 images are individually gzip-compressed; row layout is given per frame. Controller states are sampled with each processed frame, not a complete output-delivery trace. Capture timestamps are local PC receipt, not Xbox render time. Recording adds copying/compression/disk work; skipped images and copy/queue/write times are explicit.",
            limits = new { durationSeconds = 15, compressedBytes = _byteLimit, sizeLimitMayExceedByOneImage = true, pendingImages = 3, maximumFrames = 4096 },
            observedFrames = _frames.Count, storedFrames = _stored, droppedFrames = _dropped, compressedBytes = _bytes, frames = _frames
        }, Json));
        File.Move(temporary, path, true);
    }
    public void Dispose()
    {
        Stop(); _timeout.Dispose(); _writer.GetAwaiter().GetResult();
    }
}
