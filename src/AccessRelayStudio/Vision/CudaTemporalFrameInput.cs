using EliteScriptStudio.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EliteScriptStudio.Vision;

internal sealed class CudaTemporalFrameInput : IDeviceTemporalFrameInput
{
    private readonly VisionModelManifest _manifest;
    private readonly VisionSettings _settings;
    private readonly CudaD3D11Interop _cuda;
    private readonly nint[] _registrations = new nint[2];
    private D3D11RgbPreprocessor? _shader;
    private int _width, _height, _disposed;

    internal CudaTemporalFrameInput(VisionModelManifest manifest, VisionSettings settings,
        CaptureD3D11? graphics = null, int width = 0, int height = 0)
    {
        _manifest = manifest; _settings = settings;
        _cuda = new(checked(3 * manifest.InputWidth * manifest.InputHeight), manifest.HistoryLength);
        try { if (graphics is not null) PrepareShader(graphics, width, height); }
        catch { Dispose(); throw; }
    }

    private void PrepareShader(CaptureD3D11 graphics, int width, int height)
    {
        DisposeShader();
        var plan = new RgbSamplingPlan(width, height, _settings.Crop, _manifest.InputWidth, _manifest.InputHeight,
            _manifest.RgbMean, _manifest.RgbStandardDeviation);
        _shader = new(graphics, plan); _width = width; _height = height;
        try { for (int i = 0; i < 2; i++) _registrations[i] = _cuda.Register(graphics, _shader.Buffers[i]); }
        catch { DisposeShader(); throw; }
    }

    public bool PrepareFrame(CapturedVisionFrame frame, int historyIndex, bool shiftHistory)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (frame.Surface is not GpuFrameSurface surface)
            throw new InvalidOperationException("GPU preprocessing requires an owned GPU capture frame. Stop and restart capture with GPU preprocessing selected.");
        if (_shader is null || !ReferenceEquals(_shader.Graphics, surface.Graphics) || _width != frame.Width || _height != frame.Height)
            PrepareShader(surface.Graphics, frame.Width, frame.Height);
        var result = _shader!.Process(surface.View);
        int index = _shader.Buffers[0] == result.Buffer ? 0 : 1;
        _cuda.StoreFrame(_registrations[index], historyIndex, shiftHistory);
        frame.FreshnessReadbackBytes += 16;
        // The native graphics lock has been released before reporting to the
        // capture pool, avoiding an inversion with publication and shutdown.
        surface.ReportFingerprint?.Invoke(result.SourceFingerprint);
        return result.PlayerChanged;
    }
    public void RestartWithCurrentFrame(int historyIndex) => _cuda.RestartWithCurrent(historyIndex);
    public void ResetContent() => _shader?.ResetContent();
    public OrtValue CreateTensorValue()
    {
        using var memory = new OrtMemoryInfo("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
        return OrtValue.CreateTensorValueWithData(memory, TensorElementType.Float,
            [1, _manifest.HistoryLength, 3, _manifest.InputHeight, _manifest.InputWidth], _cuda.Pointer, _cuda.Bytes);
    }
    internal void ReadTensorForVerification(float[] destination) => _cuda.ReadTensor(destination);
    private void DisposeShader()
    {
        Exception? failure = null;
        try
        {
            for (int i = 0; i < _registrations.Length; i++)
                try { _cuda.Unregister(ref _registrations[i]); } catch (Exception error) { failure ??= error; }
        }
        finally { _shader?.Dispose(); _shader = null; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { DisposeShader(); }
        finally { _cuda.Dispose(); }
    }
}
