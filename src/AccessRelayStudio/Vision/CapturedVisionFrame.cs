namespace EliteScriptStudio.Vision;

/// <summary>An exclusive lease over packed BGRA8 pixels. Dispose before retaining another frame.
/// CaptureTicks uses the PC QPC clock; it is not the Xbox's original render timestamp.</summary>
internal sealed class CapturedVisionFrame : IDisposable
{
    private readonly byte[] _buffer;
    private Action? _release;

    internal CapturedVisionFrame(byte[] buffer, int width, int height, long captureTicks,
        long sequence, long sourceGeneration, Action release)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        CaptureTicks = captureTicks;
        Sequence = sequence;
        SourceGeneration = sourceGeneration;
        _release = release;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long CaptureTicks { get; }
    public long Sequence { get; }
    public long SourceGeneration { get; }
    public byte[] Buffer => Volatile.Read(ref _release) is not null ? _buffer
        : throw new ObjectDisposedException(nameof(CapturedVisionFrame));
    public ReadOnlyMemory<byte> Pixels => Buffer.AsMemory(0, checked(Stride * Height));

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
