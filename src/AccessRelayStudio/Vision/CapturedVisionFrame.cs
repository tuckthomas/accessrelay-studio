namespace EliteScriptStudio.Vision;

internal interface ICapturedFrameSurface
{
    byte[] ReadPixels();
    long CpuReadbackBytes { get; }
    double CpuReadbackMs { get; }
}

/// <summary>An exclusive lease over CPU or GPU BGRA8 pixels. GPU CPU-access is lazy and measured.
/// CaptureTicks marks local WGC frame receipt before image copies, on the PC QPC clock.
/// CompositorTicks preserves WGC's separate presentation metadata; neither is Xbox render time.</summary>
internal sealed class CapturedVisionFrame : IDisposable
{
    private readonly byte[]? _buffer;
    private readonly ICapturedFrameSurface? _surface;
    private Action? _release;

    internal CapturedVisionFrame(byte[] buffer, int width, int height, long captureTicks,
        long sequence, long sourceGeneration, Action release, double? captureReadbackMs = null, long? compositorTicks = null)
    {
        if (captureReadbackMs is { } duration && (!double.IsFinite(duration) || duration < 0))
            throw new ArgumentOutOfRangeException(nameof(captureReadbackMs));
        _buffer = buffer;
        Width = width;
        Height = height;
        CaptureTicks = captureTicks;
        Sequence = sequence;
        SourceGeneration = sourceGeneration;
        _release = release;
        CaptureReadbackMs = captureReadbackMs;
        CompositorTicks = compositorTicks;
    }

    internal CapturedVisionFrame(ICapturedFrameSurface surface, int width, int height, long captureTicks,
        long sequence, long sourceGeneration, Action release, long? compositorTicks = null)
    {
        _surface = surface; Width = width; Height = height; CaptureTicks = captureTicks;
        Sequence = sequence; SourceGeneration = sourceGeneration; _release = release;
        CompositorTicks = compositorTicks;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long CaptureTicks { get; }
    public long? CompositorTicks { get; }
    public long Sequence { get; }
    public long SourceGeneration { get; }
    public double? CaptureReadbackMs { get; }
    internal ICapturedFrameSurface? Surface => Volatile.Read(ref _release) is not null ? _surface
        : throw new ObjectDisposedException(nameof(CapturedVisionFrame));
    internal long OptionalReadbackBytes => _surface?.CpuReadbackBytes ?? 0;
    internal double OptionalReadbackMs => _surface?.CpuReadbackMs ?? 0;
    internal int FreshnessReadbackBytes { get; set; }
    public byte[] Buffer => Volatile.Read(ref _release) is not null ? _buffer ?? _surface!.ReadPixels()
        : throw new ObjectDisposedException(nameof(CapturedVisionFrame));
    public ReadOnlyMemory<byte> Pixels => Buffer.AsMemory(0, checked(Stride * Height));

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
