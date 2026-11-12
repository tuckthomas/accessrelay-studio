using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionTrainingStatus(bool HasTake, bool IsRecording, bool IsFinalized,
    bool IsComplete, string DirectoryPath, long StoredFrames, long DroppedFrames, string Message);

/// <summary>Opt-in, bounded raw player-crop dataset writer. This records observations, never target labels.</summary>
internal sealed class VisionTrainingRecorder : IDisposable
{
    internal const int StoredWidth = 256, StoredHeight = 256, FrameBytes = StoredWidth * StoredHeight * 4;
    private sealed record Pending(byte[] Pixels, long Sequence, long CaptureTicks, long ShotToken, bool PhysicalShootHeld);
    private sealed record FrameEntry(long Sequence, long CaptureTicks, string File, long OffsetBytes, int ByteLength,
        long ShotToken, bool PhysicalShootHeld);
    private readonly Channel<Pending> _frames = Channel.CreateBounded<Pending>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<FrameEntry> _index = [];
    private readonly Nba2K27ShotProfile _profile;
    private readonly VisionSettings _settings;
    private readonly string _sourceTitle;
    private readonly Task _writer;
    private readonly Timer _timeout;
    private readonly Func<string, Stream> _createStream;
    private readonly TimeSpan _durationLimit;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _createdAtUtc = DateTimeOffset.UtcNow;
    private readonly object _gate = new();
    private long _generation = -1, _dropped, _storedFrames;
    private int _width, _height;
    private bool _finished, _complete = true, _metadataComplete;
    private string _message = "Recording a 10-second manual training take.";
    internal string DirectoryPath { get; }
    internal string Message { get { lock (_gate) return _message; } }
    internal bool IsRecording { get { lock (_gate) return !_finished; } }
    internal bool IsFinalized => _writer.IsCompleted;
    internal VisionTrainingStatus Snapshot
    {
        get
        {
            lock (_gate) return new(true, !_finished, _writer.IsCompleted, _metadataComplete && _writer.IsCompleted,
                DirectoryPath, Interlocked.Read(ref _storedFrames), Interlocked.Read(ref _dropped), _message);
        }
    }

    internal VisionTrainingRecorder(string parentDirectory, Nba2K27ShotProfile profile, VisionSettings settings, string sourceTitle,
        Func<string, Stream>? streamFactory = null, TimeSpan? durationLimit = null)
    {
        profile.Validate(); settings.Validate();
        // Internal test seams keep production defaults: 10 seconds, 512 MiB, eight pending frames.
        _durationLimit = durationLimit ?? TimeSpan.FromSeconds(10);
        if (_durationLimit <= TimeSpan.Zero || _durationLimit > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(durationLimit));
        _createStream = streamFactory ?? (static path => new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        var parent = Path.GetFullPath(parentDirectory);
        Directory.CreateDirectory(parent);
        if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < 512L * 1024 * 1024)
            throw new IOException("Training capture needs at least 512 MiB of free space in the selected location.");
        DirectoryPath = Path.Combine(parent, "take-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        _profile = profile; _settings = settings; _sourceTitle = sourceTitle;
        // Metadata is written immediately as incomplete, and replaced after the writer finishes.
        WriteMetadata(complete: false);
        _writer = Task.Run(WriteAsync);
        _timeout = new Timer(_ =>
        {
            lock (_gate) FinishLocked("Training take complete. Review and label the recorded frames before training.", true);
        }, null, _durationLimit, Timeout.InfiniteTimeSpan);
    }

    internal void Capture(CapturedVisionFrame frame, VisionStatusSnapshot output)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (Stopwatch.GetElapsedTime(_started) >= _durationLimit)
            { FinishLocked("Training take complete. Review and label the recorded frames before training.", true); return; }
            if (_generation >= 0 && (_generation != frame.SourceGeneration || _width != frame.Width || _height != frame.Height))
            { FinishLocked("Source resized or changed. Take marked incomplete; record a new take.", false); return; }
            _generation = frame.SourceGeneration; _width = frame.Width; _height = frame.Height;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(FrameBytes);
        try
        {
            ResizeCrop(frame, buffer, _settings.Crop);
            if (_frames.Writer.TryWrite(new(buffer, frame.Sequence, frame.CaptureTicks, output.ShotToken, output.ShootHeld)))
                buffer = null!;
            else Interlocked.Increment(ref _dropped);
        }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ResizeCrop(CapturedVisionFrame frame, byte[] target, VisionCrop crop)
    {
        var source = frame.Pixels.Span;
        for (int y = 0; y < StoredHeight; y++)
        {
            int sy = Math.Min(frame.Height - 1, (int)((crop.Top + (y + .5) / StoredHeight * crop.Height) * frame.Height));
            for (int x = 0; x < StoredWidth; x++)
            {
                int sx = Math.Min(frame.Width - 1, (int)((crop.Left + (x + .5) / StoredWidth * crop.Width) * frame.Width));
                source.Slice(sy * frame.Stride + sx * 4, 4).CopyTo(target.AsSpan((y * StoredWidth + x) * 4, 4));
            }
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await using var stream = _createStream(Path.Combine(DirectoryPath, "frames.bgra"));
            await foreach (var frame in _frames.Reader.ReadAllAsync())
            {
                try
                {
                    if (stream.Position + FrameBytes > 512L * 1024 * 1024)
                    {
                        lock (_gate) FinishLocked("Take reached its 512 MiB limit.", true);
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }
                    long offset = stream.Position;
                    await stream.WriteAsync(frame.Pixels.AsMemory(0, FrameBytes));
                    _index.Add(new(frame.Sequence, frame.CaptureTicks, "frames.bgra", offset, FrameBytes,
                        frame.ShotToken, frame.PhysicalShootHeld));
                    Interlocked.Increment(ref _storedFrames);
                }
                finally { ArrayPool<byte>.Shared.Return(frame.Pixels); }
            }
            await stream.FlushAsync();
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _finished = true; _complete = false;
                _message = "Training capture failed: " + error.Message;
                _frames.Writer.TryComplete();
            }
            while (_frames.Reader.TryRead(out var pending)) ArrayPool<byte>.Shared.Return(pending.Pixels);
        }
        finally
        {
            lock (_gate) { _finished = true; if (_index.Count == 0) _complete = false; }
            try
            {
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
            preprocessing = "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2",
            sourceTitle = _sourceTitle, shotProfile = _profile, stopwatchFrequency = Stopwatch.Frequency,
            frames = _index, droppedFrames = Interlocked.Read(ref _dropped), complete,
            labelsRequired = "Target release labels require reviewed practice experiments; physical release and ball departure are not automatically ground truth.",
            timingScope = "PC capture timestamps; original Xbox render time is not measured."
        };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        string temporary = Path.Combine(DirectoryPath, "session.json.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, options));
        File.Move(temporary, Path.Combine(DirectoryPath, "session.json"), overwrite: true);
        if (!File.Exists(Path.Combine(DirectoryPath, "labels.example.json")))
            File.WriteAllText(Path.Combine(DirectoryPath, "labels.example.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, sessionId = Path.GetFileName(DirectoryPath), labelTimeBasis = "captured-target-game-phase", split = "train", shots = Array.Empty<object>(),
                instructions = "Copy to labels.json. Add reviewed shots with shotId, startTicks, endTicks, targetReleaseTicks, isTargetShot, playerIdentityConfirmed. See docs/vision-model-workflow.md."
            }, options));
    }
    private void FinishLocked(string message, bool complete)
    { if (_finished) return; _finished = true; _complete = complete; _message = message; _frames.Writer.TryComplete(); }
    internal void Stop(bool complete = false) { lock (_gate) FinishLocked(complete ? "Take stopped; review labels." : "Take stopped early; marked incomplete.", complete); }
    public void Dispose() { _timeout.Dispose(); Stop(); _writer.GetAwaiter().GetResult(); }
}
