using EliteScriptStudio.Core;
using Microsoft.ML.OnnxRuntime;

namespace EliteScriptStudio.Vision;

/// <summary>Tracking owns sample admission and history indices. Implementations
/// own tensor storage and exact selected-player content comparison.</summary>
internal interface ITemporalFrameInput
{
    bool PrepareFrame(CapturedVisionFrame frame, int historyIndex, bool shiftHistory);
    void RestartWithCurrentFrame(int historyIndex);
    void ResetContent();
}

internal interface IDeviceTemporalFrameInput : ITemporalFrameInput, IDisposable
{
    OrtValue CreateTensorValue();
}

internal sealed class CpuTemporalFrameInput(VisionModelManifest manifest, VisionSettings settings, float[] history) : ITemporalFrameInput
{
    private readonly int _frameLength = checked(3 * manifest.InputWidth * manifest.InputHeight);
    private readonly float[] _previous = new float[checked(3 * manifest.InputWidth * manifest.InputHeight)];
    private RgbSamplingPlan? _plan;
    private bool _hasPrevious;

    public bool PrepareFrame(CapturedVisionFrame frame, int historyIndex, bool shiftHistory)
    {
        if (history.Length != checked(_frameLength * manifest.HistoryLength) || historyIndex < 0 || historyIndex >= manifest.HistoryLength)
            throw new InvalidDataException("The temporal RGB storage or write index is invalid.");
        if (frame.PlayerCrop is null && (_plan is null || _plan.SourceWidth != frame.Width || _plan.SourceHeight != frame.Height))
            _plan = new(frame.Width, frame.Height, settings.Crop, manifest.InputWidth, manifest.InputHeight, manifest.RgbMean, manifest.RgbStandardDeviation);
        if (shiftHistory) history.AsSpan(_frameLength).CopyTo(history);
        var current = history.AsSpan(historyIndex * _frameLength, _frameLength);
        if (frame.PlayerCrop is { } tracked)
            FramePreprocessor.FillRgb(frame.Pixels.Span, frame.Width, frame.Height, frame.Stride, tracked, manifest.InputWidth, manifest.InputHeight, manifest.RgbMean, manifest.RgbStandardDeviation, current);
        else _plan!.Fill(frame.Pixels.Span, frame.Stride, current);
        bool changed = !_hasPrevious || !current.SequenceEqual(_previous);
        if (changed) { current.CopyTo(_previous); _hasPrevious = true; }
        return changed;
    }
    public void RestartWithCurrentFrame(int historyIndex) => history.AsSpan(historyIndex * _frameLength, _frameLength).CopyTo(history);
    public void ResetContent() => _hasPrevious = false;
}
