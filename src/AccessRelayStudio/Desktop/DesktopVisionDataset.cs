using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Desktop;

/// <summary>UI-independent, bounded local clip reader. No paths supplied by browser media URLs are opened.</summary>
internal sealed class DesktopVisionDataset : IDisposable
{
    internal sealed record RegionFrame(string Name, string File, long OffsetBytes, int ByteLength);
    internal sealed record Frame(long CaptureTicks, long OffsetBytes, int ByteLength, string File,
        long Sequence = 0, RegionFrame[]? Evidence = null);
    internal sealed record Region(string Name, string Label, string File, int Width, int Height)
    { internal int ByteLength => checked(Width * Height * 4); }
    private sealed record StreamInfo(string Name, string File, int Width, int Height, int Stride, string PixelFormat, VisionCrop Crop);
    private sealed record Evidence(int Version, string Purpose, StreamInfo[] Streams, string? Tracking = null);
    private sealed record SessionInfo(int SchemaVersion, string SessionId, bool Complete, long StopwatchFrequency,
        int StoredWidth, int StoredHeight, int StoredStride, string PixelFormat, Frame[] Frames, Evidence? CaptureEvidence = null);
    internal sealed record Shot(string ShotId, long StartTicks, long EndTicks, long? TargetReleaseTicks,
        bool IsTargetShot, bool PlayerIdentityConfirmed, bool Reviewed);
    internal sealed record LabelDocument(int SchemaVersion, string SessionId, string Split, Shot[] Shots,
        string LabelTimeBasis = "captured-target-game-phase");
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly SessionInfo _session;
    private string? _labelsHash, _openRegion;
    private FileStream? _stream;
    private byte[] _pixels = [];
    private bool _disposed;
    internal string Token { get; } = Guid.NewGuid().ToString("N");
    internal Region[] Regions { get; }
    internal LabelDocument Labels { get; private set; }
    internal int FrameCount => _session.Frames.Length;

    internal DesktopVisionDataset(string directory)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        byte[] metadata = ReadBounded(Child("session.json"));
        using var doc = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.TryGetProperty("captureEvidence", out var marker) && marker.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("This clip has malformed evidence metadata.");
        _session = JsonSerializer.Deserialize<SessionInfo>(metadata, DesktopJson.Options)
            ?? throw new InvalidDataException("The clip metadata is empty.");
        if (_session.SchemaVersion != 1 || !_session.Complete || string.IsNullOrWhiteSpace(_session.SessionId)
            || _session.SessionId.Length > 128 || _session.SessionId.Any(char.IsControl) || _session.StopwatchFrequency <= 0
            || _session.StoredWidth != 256 || _session.StoredHeight != 256 || _session.StoredStride != 1024
            || _session.PixelFormat != "BGRA8" || _session.Frames is not { Length: >= 2 and <= 10000 })
            throw new InvalidDataException("Choose a completed clip containing at least two recorded frames.");
        long primaryLength = new FileInfo(Child("frames.bgra")).Length, previous = -1;
        foreach (var frame in _session.Frames)
        {
            if (frame is null || frame.CaptureTicks < 0 || frame.CaptureTicks <= previous || frame.File != "frames.bgra"
                || frame.ByteLength != 256 * 256 * 4 || frame.OffsetBytes < 0 || frame.OffsetBytes > primaryLength - frame.ByteLength)
                throw new InvalidDataException("The clip contains invalid timestamps or frame offsets.");
            previous = frame.CaptureTicks;
        }
        Regions = ValidateEvidence(primaryLength);
        string labelsPath = Child("labels.json");
        if (File.Exists(labelsPath))
        {
            byte[] bytes = ReadBounded(labelsPath);
            Labels = JsonSerializer.Deserialize<LabelDocument>(bytes, DesktopJson.Options)
                ?? throw new InvalidDataException("The clip's labels are empty.");
            ValidateLabels(Labels);
            _labelsHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        else Labels = new(1, _session.SessionId, "train", []);
    }

    internal object Snapshot()
    {
        lock (_gate) return new
        {
            token = Token, sessionId = _session.SessionId, directory = _directory, frameCount = FrameCount,
            enriched = _session.CaptureEvidence is not null, purpose = _session.CaptureEvidence?.Purpose,
            tracking = _session.CaptureEvidence?.Tracking,
            regions = Regions.Select(r => new { r.Name, r.Label, r.Width, r.Height }), labels = Labels,
            frames = _session.Frames.Select(f => new { ticks = f.CaptureTicks, sequence = f.Sequence,
                timeMs = ((double)f.CaptureTicks - _session.Frames[0].CaptureTicks) * 1000 / _session.StopwatchFrequency })
        };
    }

    internal DesktopMedia ReadFrame(string regionName, int index)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= FrameCount) throw new InvalidDataException("Choose a frame inside this clip.");
            var region = Regions.FirstOrDefault(r => r.Name == regionName)
                ?? throw new InvalidDataException("This clip does not contain that capture region.");
            if (_stream is null || _openRegion != region.Name)
            {
                _stream?.Dispose(); _stream = null; _openRegion = null;
                _stream = File.Open(Child(region.File), FileMode.Open, FileAccess.Read, FileShare.Read);
                _pixels = new byte[region.ByteLength]; _openRegion = region.Name;
            }
            var frame = _session.Frames[index];
            _stream.Position = region.Name == "player" ? frame.OffsetBytes
                : frame.Evidence!.First(r => r.Name == region.Name).OffsetBytes;
            _stream.ReadExactly(_pixels);
            return DesktopVisionBitmap.Encode(_pixels, region.Width, region.Height, region.Width * 4);
        }
    }

    internal void Save(LabelDocument labels)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ValidateLabels(labels);
            string path = Child("labels.json");
            string? current = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(ReadBounded(path))) : null;
            if (current != _labelsHash) throw new InvalidOperationException("These labels changed in another window. Reopen the clip before saving.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(labels, DesktopJson.Options);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("The label document is too large.");
            string temporary = Child("labels-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Labels = labels; _labelsHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
    }

    private Region[] ValidateEvidence(long primaryLength)
    {
        bool sceneFirst = _session.CaptureEvidence?.Version == 2;
        var player = new Region("player", sceneFirst ? "Overview" : "Player", "frames.bgra", 256, 256);
        if (_session.CaptureEvidence is not { } evidence)
        {
            if (_session.Frames.Any(f => f.Evidence is { Length: > 0 }))
                throw new InvalidDataException("Region frames have no matching evidence metadata.");
            return [player];
        }
        if (evidence.Version is not (1 or 2) || evidence.Purpose is not ("meterSupervisedTraining" or "meterOffValidation")
            || (sceneFirst && evidence.Tracking != "unannotated-scene"))
            throw new InvalidDataException("This clip's evidence format is not supported.");
        var scene = evidence.Streams?.FirstOrDefault(s => s?.Name == "scene");
        if (sceneFirst && (scene is null || scene.Width is < 1 or > 1280 || scene.Height is < 1 or > 1280))
            throw new InvalidDataException("The gameplay scene exceeds its dimension limit.");
        var expected = new List<Region> { player, new("scene", sceneFirst ? "Gameplay scene" : "Court", "scene.bgra", sceneFirst ? scene!.Width : 640, sceneFirst ? scene!.Height : 360) };
        if (!sceneFirst && evidence.Purpose == "meterSupervisedTraining") expected.Add(new("meter", "Meter", "meter.bgra", 256, 256));
        expected.Add(new("feedback", "Shot feedback", "feedback.bgra", 640, 192));
        if (evidence.Streams is null || evidence.Streams.Length != expected.Count)
            throw new InvalidDataException("The clip has missing evidence streams.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var stream in evidence.Streams)
        {
            if (stream is null || string.IsNullOrEmpty(stream.Name) || !names.Add(stream.Name))
                throw new InvalidDataException("The clip contains repeated evidence regions.");
            var region = expected.FirstOrDefault(r => r.Name == stream.Name);
            if (region is null || stream.File != region.File || stream.Width != region.Width || stream.Height != region.Height
                || stream.Stride != region.Width * 4 || stream.PixelFormat != "BGRA8")
                throw new InvalidDataException("The clip has unsupported region dimensions or filenames.");
            stream.Crop.Validate();
            long length = stream.Name == "player" ? primaryLength : new FileInfo(Child(stream.File)).Length;
            if (length != (long)FrameCount * region.ByteLength) throw new InvalidDataException("The region data does not match its frame index.");
            total = checked(total + length);
        }
        if (total > (sceneFirst ? 4L : 2L) * 1024 * 1024 * 1024) throw new InvalidDataException("The clip exceeds the evidence size limit.");
        long sequence = -1;
        for (int index = 0; index < FrameCount; index++)
        {
            var frame = _session.Frames[index];
            if (frame.Sequence < 0 || frame.Sequence <= sequence || frame.OffsetBytes != (long)index * player.ByteLength
                || frame.Evidence is null || frame.Evidence.Length != expected.Count - 1)
                throw new InvalidDataException("The evidence regions do not share a consistent frame sequence.");
            sequence = frame.Sequence; names.Clear();
            foreach (var entry in frame.Evidence)
            {
                if (entry is null || string.IsNullOrEmpty(entry.Name) || entry.Name == "player" || !names.Add(entry.Name))
                    throw new InvalidDataException("An evidence frame has repeated regions.");
                var region = expected.FirstOrDefault(r => r.Name == entry.Name);
                if (region is null || entry.File != region.File || entry.ByteLength != region.ByteLength
                    || entry.OffsetBytes != (long)index * region.ByteLength)
                    throw new InvalidDataException("An evidence frame points outside its region stream.");
            }
        }
        return expected.ToArray();
    }

    private void ValidateLabels(LabelDocument labels)
    {
        if (labels.SchemaVersion != 1 || labels.SessionId != _session.SessionId || labels.Split is not ("train" or "validation" or "held-out")
            || labels.LabelTimeBasis != "captured-target-game-phase" || labels.Shots is null || labels.Shots.Length > 512)
            throw new InvalidDataException("Choose valid reviewed labels and one split for the entire clip.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long first = _session.Frames[0].CaptureTicks, last = _session.Frames[^1].CaptureTicks;
        foreach (var shot in labels.Shots)
        {
            if (shot is null || string.IsNullOrWhiteSpace(shot.ShotId) || shot.ShotId.Length > 80 || shot.ShotId.Any(char.IsControl)
                || !ids.Add(shot.ShotId) || !shot.Reviewed || shot.StartTicks < first || shot.EndTicks > last || shot.EndTicks <= shot.StartTicks)
                throw new InvalidDataException("Each interval needs a unique name, reviewed confirmation and valid start/end frames.");
            if (shot.IsTargetShot && (!shot.PlayerIdentityConfirmed || shot.TargetReleaseTicks is not long target || target < shot.StartTicks || target > shot.EndTicks))
                throw new InvalidDataException("A target shot needs confirmed player identity and a reviewed target phase inside the interval.");
            if (!shot.IsTargetShot && shot.TargetReleaseTicks.HasValue)
                throw new InvalidDataException("A negative interval must have no release target.");
        }
        var sorted = labels.Shots.OrderBy(s => s.StartTicks).ToArray();
        for (int index = 1; index < sorted.Length; index++)
            if (sorted[index].StartTicks <= sorted[index - 1].EndTicks)
                throw new InvalidDataException("Shot intervals must not overlap or share an endpoint.");
    }

    private string Child(string filename)
    {
        string path = Path.GetFullPath(Path.Combine(_directory, filename));
        if (Path.GetDirectoryName(path) != _directory || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Clip files must remain inside their selected folder without redirects.");
        return path;
    }
    private static byte[] ReadBounded(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Clip metadata is empty or too large.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    public void Dispose() { lock (_gate) { _disposed = true; _stream?.Dispose(); _stream = null; } }
}

internal static class DesktopVisionBitmap
{
    // Top-down uncompressed BGRA keeps preview transfer binary and avoids a video stream or base64 bridge.
    internal static DesktopMedia Encode(ReadOnlySpan<byte> source, int width, int height, int stride, int maximumWidth = 960)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || maximumWidth is < 1 or > 16384
            || stride < checked(width * 4) || source.Length < checked((height - 1) * stride + width * 4))
            throw new InvalidDataException("The preview image dimensions or row stride are invalid.");
        int targetWidth = Math.Min(width, maximumWidth), targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
        int length = checked(targetWidth * targetHeight * 4);
        var bytes = new byte[checked(54 + length)];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), targetWidth);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -targetHeight);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), length);
        var target = bytes.AsSpan(54);
        if (targetWidth == width && targetHeight == height)
            for (int y = 0; y < height; y++) source.Slice(y * stride, width * 4).CopyTo(target.Slice(y * width * 4, width * 4));
        else
            for (int y = 0; y < targetHeight; y++)
            {
                int sy = Math.Min(height - 1, y * height / targetHeight);
                for (int x = 0; x < targetWidth; x++)
                {
                    int sx = Math.Min(width - 1, x * width / targetWidth);
                    source.Slice(sy * stride + sx * 4, 4).CopyTo(target.Slice((y * targetWidth + x) * 4, 4));
                }
            }
        return new(bytes, "image/bmp");
    }
}
