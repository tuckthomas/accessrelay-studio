using System.Threading;

namespace EliteScriptStudio.Core;

internal enum VisionTrainingTraceKind { Poll, DeadlineOutput }

/// <summary>PC observations, in Stopwatch/QPC ticks. Local acceptance is not Xbox receipt.
/// A successful output call can retain a previously queued identical report rather than enqueue a new one.</summary>
internal readonly record struct VisionTrainingTraceSample(
    VisionTrainingTraceKind Kind, long SampleTicks, long BeforeSubmitTicks, long AfterSubmitTicks,
    ControllerState Physical, ControllerState RequestedOutput, bool Connected, long ConnectionVersion,
    PaddleButtons HeldPaddles, PaddleButtons PressedPaddles, bool OutputAvailable, bool AcceptedForLocalHandoff);

internal sealed record VisionTrainingTraceSnapshot(VisionTrainingTraceSample[] Samples, long DroppedSamples, int Capacity);

/// <summary>Bounded append-only controller trace. Producers allocate nothing and never perform I/O.
/// Slots are published once, so snapshots can safely copy completed records while capture continues.</summary>
internal sealed class VisionTrainingTrace
{
    internal const int MaximumSamples = 10000;
    private readonly VisionTrainingTraceSample[] _samples;
    private readonly int[] _published;
    private long _reserved, _dropped;
    private int _stored;

    internal VisionTrainingTrace(int capacity = MaximumSamples)
    {
        if (capacity is < 1 or > MaximumSamples) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new VisionTrainingTraceSample[capacity];
        _published = new int[capacity];
    }

    internal int StoredSamples => Volatile.Read(ref _stored);
    internal long DroppedSamples => Interlocked.Read(ref _dropped);

    internal void Record(in VisionTrainingTraceSample sample)
    {
        long index = Interlocked.Increment(ref _reserved) - 1;
        if (index >= _samples.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _samples[(int)index] = sample;
        Volatile.Write(ref _published[(int)index], 1);
        Interlocked.Increment(ref _stored);
    }

    internal VisionTrainingTraceSnapshot Snapshot()
    {
        // Snapshot allocation is intentional; only Record runs in the input/output hot path.
        int limit = (int)Math.Min(Interlocked.Read(ref _reserved), _samples.Length);
        var samples = new List<VisionTrainingTraceSample>(limit);
        for (int index = 0; index < limit; index++)
            if (Volatile.Read(ref _published[index]) != 0) samples.Add(_samples[index]);
        return new(samples.ToArray(), DroppedSamples, _samples.Length);
    }
}
