using System.Diagnostics;

namespace EliteScriptStudio.Vision;

/// <summary>The decoder owns the mapped BGRA memory until the synchronous callback returns.</summary>
internal readonly record struct DecodedBgraFrame(nint Pixels, int ByteLength, int RowPitch, int Width, int Height, long? DecoderReceiptTicks = null);

internal interface IDecodedVideoTrack
{
    // An observation owns only its subscription. A new connection supplies a new track.
    EliteScriptStudio.Core.DirectStreamMetrics? LatestTelemetry => null;
    IDisposable Subscribe(Action<DecodedBgraFrame> onFrame, Action onEnded);
}

internal sealed class DecodedStreamCapture : IVisionFrameSource
{
    private readonly object _gate = new();
    private readonly LatestFrameBuffer _frames = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly IDisposable _subscription;
    private readonly IDecodedVideoTrack _track;
    internal EliteScriptStudio.Core.DirectStreamMetrics? LatestTelemetry => _track.LatestTelemetry;
    private bool _running = true;
    private bool _disposed;
    private long _generation, _callbacks, _published, _latestTicks, _changedTicks;
    private int _width, _height;
    private ulong _fingerprint;
    private string _message = "Waiting for the first decoded Xbox frame.";

    internal DecodedStreamCapture(IDecodedVideoTrack track)
    {
        _track = track;
        try { _subscription = track.Subscribe(Publish, () => End("The Xbox video track ended.")); }
        catch
        {
            lock (_gate)
            {
                _running = false;
                _ready.Dispose();
                _frames.Dispose();
            }
            throw;
        }
    }

    public WaitHandle FrameReady => _ready;

    public CaptureStatus Status
    {
        get
        {
            lock (_gate)
                return new(_running, _message, _generation, _latestTicks, _published, _changedTicks,
                    _width, _height, _frames.DroppedFrames, new(Stopwatch.GetTimestamp(), _callbacks,
                        _callbacks, 0, _published, _callbacks - _published, _frames.PendingReplacements,
                        _frames.BufferUnavailable, null, null) { CaptureTimeBasis = CapturePipelineSnapshot.DecoderReceipt });
        }
    }

    private void Publish(DecodedBgraFrame frame)
    {
        long copyStarted = Stopwatch.GetTimestamp();
        long receipt = frame.DecoderReceiptTicks is > 0 && frame.DecoderReceiptTicks <= copyStarted ? frame.DecoderReceiptTicks.Value : copyStarted;
        lock (_gate)
        {
            if (!_running) return;
            _callbacks++;
            if (frame.Width is <= 0 or > 3840 || frame.Height is <= 0 or > 2160 || frame.Pixels == 0
                || frame.RowPitch < frame.Width * 4
                || (long)(frame.Height - 1) * frame.RowPitch + frame.Width * 4 > frame.ByteLength)
            {
                End("The Xbox decoder supplied an unsupported BGRA frame layout.");
                return;
            }
            if (receipt < _latestTicks) return;
            if (frame.Width != _width || frame.Height != _height)
            {
                _frames.Clear();
                _generation++;
                _width = frame.Width;
                _height = frame.Height;
                _latestTicks = _changedTicks = 0;
            }
            if (!_frames.PublishMapped(frame.Pixels, (uint)frame.RowPitch, _width, _height, receipt,
                _published + 1, _generation, readbackStartedTicks: copyStarted)) return;

            _latestTicks = receipt;
            _published++;
            ulong fingerprint = _frames.LatestFingerprint;
            if (_changedTicks == 0 || _fingerprint != fingerprint) _changedTicks = receipt;
            _fingerprint = fingerprint;
            _message = "Receiving decoded Xbox frames.";
            _ready.Set();
        }
    }

    private void End(string message)
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _message = message;
            _latestTicks = _changedTicks = 0;
            _frames.Clear();
            _ready.Set();
        }
    }

    public bool TryAcquireLatest(out CapturedVisionFrame? frame) => _frames.TryAcquireLatest(out frame);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            End("Xbox video observation stopped.");
        }
        // Unsubscription may wait for an in-flight decoder callback.
        try { _subscription.Dispose(); }
        finally { _frames.Dispose(); _ready.Dispose(); }
    }
}
