using System.Buffers;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>One in-flight setup analysis. Raw recording never waits for OCR/pose inference.</summary>
internal sealed class VisionAcquisitionWorker(IVisionRegionTracker tracker, CancellationToken stopping = default) : IDisposable
{
    internal sealed record Result(VisionPreview Preview, long Generation);
    private Task<Result>? _pending;
    private bool _disposed;

    internal bool TryQueue(CapturedVisionFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is not null) return false;
        int length = checked(frame.Stride * frame.Height);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            frame.Pixels.Span.CopyTo(pixels);
            int width = frame.Width, height = frame.Height, stride = frame.Stride;
            long sequence = frame.Sequence, ticks = frame.CaptureTicks, generation = frame.SourceGeneration;
            _pending = Task.Run(() =>
            {
                try
                {
                    var observation = tracker.Track(pixels.AsSpan(0, length), width, height, stride, sequence, generation, ticks);
                    if (observation.Sequence != sequence) throw new InvalidDataException("Capture analysis returned a different frame sequence.");
                    return new Result(new(pixels, width, height, stride, sequence, ticks, observation), generation);
                }
                catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
            });
            return true;
        }
        catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
    }

    internal Result? TakeCompleted()
    {
        if (_pending is not { IsCompleted: true } task) return null;
        _pending = null;
        return task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        tracker.RequestStop();
        var pending = _pending; _pending = null;
        if (pending is not null)
            try { pending.GetAwaiter().GetResult().Preview.Dispose(); }
            catch (Exception) when (stopping.IsCancellationRequested) { }
    }
}
