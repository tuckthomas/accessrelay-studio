namespace EliteScriptStudio.Core;

public sealed record StickSmoothing(double LeftTimeConstantMs = 0, double RightTimeConstantMs = 0)
{
    public static readonly StickSmoothing Off = new();
    public void Validate()
    {
        if (!double.IsFinite(LeftTimeConstantMs) || !double.IsFinite(RightTimeConstantMs)
            || LeftTimeConstantMs is < 0 or > 100 || RightTimeConstantMs is < 0 or > 100)
            throw new InvalidDataException("Stick smoothing must be 0–100 milliseconds per stick.");
    }
}

/// <summary>Elapsed-time EMA over manual input only. Neutral input is immediate,
/// so returning to the deadzone cannot leave a decaying virtual stick movement.</summary>
internal sealed class StickSmoother
{
    private StickSmoothing _settings = StickSmoothing.Off;
    private double _leftX, _leftY, _rightX, _rightY;
    private long _previousMs;
    private bool _hasSample;

    internal void Configure(StickSmoothing settings) { settings.Validate(); _settings = settings; Reset(); }
    internal void Reset() { _hasSample = false; _leftX = _leftY = _rightX = _rightY = 0; }

    internal ControllerState Sample(ControllerState manual, long nowMs, bool active)
    {
        if (!active) { Reset(); return manual; }
        double elapsed = (double)nowMs - _previousMs;
        if (!_hasSample || elapsed < 0 || elapsed > 250)
        {
            _leftX = manual.LeftX; _leftY = manual.LeftY; _rightX = manual.RightX; _rightY = manual.RightY;
            _previousMs = nowMs; _hasSample = true;
            return manual;
        }
        _previousMs = nowMs;
        Filter(manual.LeftX, manual.LeftY, _settings.LeftTimeConstantMs, elapsed, ref _leftX, ref _leftY);
        Filter(manual.RightX, manual.RightY, _settings.RightTimeConstantMs, elapsed, ref _rightX, ref _rightY);
        return manual with { LeftX = ToStick(_leftX), LeftY = ToStick(_leftY), RightX = ToStick(_rightX), RightY = ToStick(_rightY) };
    }

    private static void Filter(short x, short y, double timeConstant, double elapsed, ref double outputX, ref double outputY)
    {
        if (timeConstant == 0 || (x == 0 && y == 0)) { outputX = x; outputY = y; return; }
        double alpha = 1 - Math.Exp(-elapsed / timeConstant);
        outputX += alpha * (x - outputX); outputY += alpha * (y - outputY);
    }

    private static short ToStick(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
}
