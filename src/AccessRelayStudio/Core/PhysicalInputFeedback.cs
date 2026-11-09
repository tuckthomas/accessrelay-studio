namespace EliteScriptStudio.Core;

internal sealed class PhysicalInputFeedback
{
    private const int FlashMs = 120;
    private readonly long[] _visibleUntil = new long[ControlMap.All.Count];
    private long _connectionVersion = long.MinValue;

    public uint Sample(ControllerState state, PaddleButtons paddles, bool connected, long connectionVersion, long now)
    {
        if (!connected || connectionVersion != _connectionVersion)
        {
            Array.Clear(_visibleUntil);
            _connectionVersion = connectionVersion;
        }
        if (!connected)
            return 0;

        uint pressed = ControlMap.PressedMask(state, paddles);
        uint visible = 0;
        for (int index = 0; index < _visibleUntil.Length; index++)
        {
            uint bit = (uint)ControlMap.All[index];
            if ((pressed & bit) != 0)
                _visibleUntil[index] = now + FlashMs;
            if (_visibleUntil[index] > now)
                visible |= bit;
        }
        return visible;
    }

    public static (double X, double Y) StickOffset(short x, short y)
    {
        double right = x / (x < 0 ? 32768.0 : 32767.0);
        double up = y / (y < 0 ? 32768.0 : 32767.0);
        double scale = 15.0 / Math.Max(1, Math.Sqrt(right * right + up * up));
        return (right * scale, -up * scale);
    }
}
