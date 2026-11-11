namespace EliteScriptStudio.Core;

internal readonly record struct StickDeadzone(double LeftPercent, double RightPercent)
{
    public const int DefaultPercent = 5;
    public const int MaximumPercent = 50;
    public static readonly StickDeadzone Default = new(DefaultPercent, DefaultPercent);
    public bool IsValid => IsValidPercent(LeftPercent) && IsValidPercent(RightPercent);

    public static bool IsValidPercent(double value) => value is >= 0 and <= MaximumPercent;

    public ControllerState Apply(ControllerState state)
    {
        if (IsCentered(state.LeftX, state.LeftY, LeftPercent))
            state = state with { LeftX = 0, LeftY = 0 };
        if (IsCentered(state.RightX, state.RightY, RightPercent))
            state = state with { RightX = 0, RightY = 0 };
        return state;
    }

    public static double DistancePercent(short x, short y) => Math.Sqrt(MagnitudeSquared(x, y)) * 100 / short.MaxValue;

    private static bool IsCentered(short x, short y, double percent)
    {
        double radius = short.MaxValue * percent / 100;
        return MagnitudeSquared(x, y) <= radius * radius;
    }

    private static long MagnitudeSquared(short x, short y) => (long)x * x + (long)y * y;
}
