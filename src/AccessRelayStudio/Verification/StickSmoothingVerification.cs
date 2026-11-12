using EliteScriptStudio.Core;

internal static class StickSmoothingVerification
{
    internal static void Run()
    {
        var left = new StickSmoother(); left.Configure(new(20, 0));
        left.Sample(default, 0, true);
        var step = new ControllerState((ushort)ControlId.B, 80, 90, short.MaxValue, short.MinValue, 12345, -12345);
        var ten = left.Sample(step, 10, true);
        Check(Math.Abs(ten.LeftX - short.MaxValue * (1 - Math.Exp(-.5))) <= 1, "Filter response does not match its time constant.");
        Check(ten.Buttons == step.Buttons && ten.LeftTrigger == step.LeftTrigger && ten.RightX == step.RightX && ten.RightY == step.RightY,
            "Left-stick smoothing altered unrelated channels.");
        var twenty = left.Sample(step, 20, true);
        var once = new StickSmoother(); once.Configure(new(20, 0)); once.Sample(default, 0, true);
        Check(twenty == once.Sample(step, 20, true), "Response changed with sample rate.");
        var neutral = left.Sample(step with { LeftX = 0, LeftY = 0 }, 21, true);
        Check(neutral.LeftX == 0 && neutral.LeftY == 0, "Neutral input retained residual motion.");
        Check(left.Sample(step, 22, false) == step, "Inactive filtering changed input.");
        Check(left.Sample(default, 23, true) == default, "Reactivation retained filter history.");
        left.Configure(StickSmoothing.Off);
        Check(left.Sample(step, 30, true) == step && left.Sample(default, 31, true) == default, "Default-off filtering altered input.");
        left.Configure(new(100, 100)); left.Sample(default, 40, true);
        var delayed = left.Sample(step, 41, true); Check(delayed.LeftX < step.LeftX && delayed.RightX < step.RightX, "Both-stick filtering was bypassed.");
        Check(left.Sample(step, 500, true) == step, "A long polling gap reused obsolete history.");
        Check(left.Sample(default, 400, true) == default, "Clock rollback retained history.");
        left.Reset(); Check(left.Sample(default, 501, true) == default, "Reset retained stick movement.");
        foreach (var invalid in new[] { new StickSmoothing(double.NaN, 0), new(-1, 0), new(0, 101) })
        {
            bool rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid filter configuration was accepted.");
        }
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.Smoothing." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            var profile = StudioProfile.CreateDefault() with { Smoothing = new(25, 50) };
            store.Save(profile); Check(store.Load(profile.Id).Smoothing == profile.Smoothing, "Smoothing did not round-trip.");
            store.Save(profile with { Smoothing = StickSmoothing.Off });
            Check(store.Load(profile.Id).Smoothing == StickSmoothing.Off, "Disabling smoothing did not persist.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
