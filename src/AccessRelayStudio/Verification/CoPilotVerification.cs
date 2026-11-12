using System.Collections.Immutable;
using EliteScriptStudio.Core;

internal static class CoPilotVerification
{
    internal static void Run()
    {
        var settings = new CoPilotSettings { Enabled = true, PrimaryDeviceId = "physical-a", CompanionDeviceId = "physical-b" };
        var a = new ControllerState((ushort)(ControlId.A | ControlId.DPadUp), 90, 80, 10000, -12000, 22000, 0);
        var b = new ControllerState((ushort)(ControlId.A | ControlId.B | ControlId.DPadDown), 40, 200, 24000, 0, -14000, 15000);
        var expected = new ControllerState((ushort)(ControlId.A | ControlId.B), 90, 200, 10000, -12000, -14000, 15000);
        Equal(expected, CoPilotInput.Merge(a, true, b, true, settings));
        Equal(b with { LeftX = 0, LeftY = 0 }, CoPilotInput.Merge(a, false, b, true, settings));
        Equal(a with { RightX = 0, RightY = 0 }, CoPilotInput.Merge(a, true, b, false, settings));
        Equal(default, CoPilotInput.Merge(a, false, b, false, settings));
        Equal(a, CoPilotInput.Merge(a, true, b, true, settings with { Enabled = false }));
        var owned = settings with { LeftStick = CoPilotAxisOwner.Strongest, RightStick = CoPilotAxisOwner.Primary,
            Triggers = CoPilotAxisOwner.Companion, Buttons = ImmutableDictionary<ControlId, CoPilotButtonOwner>.Empty
                .Add(ControlId.A, CoPilotButtonOwner.Companion).Add(ControlId.B, CoPilotButtonOwner.Primary) };
        var merged = CoPilotInput.Merge(a, true, b with { Buttons = 0 }, true, owned);
        Equal((ushort)ControlId.DPadUp, merged.Buttons);
        Equal((short)24000, merged.LeftX); Equal((short)0, merged.LeftY);
        Equal((short)22000, merged.RightX); Equal((byte)40, merged.LeftTrigger);
        // Full-range magnitude never overflows, and ties choose one whole vector.
        var full = a with { LeftX = short.MinValue, LeftY = short.MinValue };
        Equal((short)-32768, CoPilotInput.Merge(full, true, b, true, owned).LeftX);
        Equal(a.LeftY, CoPilotInput.Merge(a, true, b with { LeftX = a.LeftX, LeftY = (short)-a.LeftY }, true, owned).LeftY);
        Expect(() => (settings with { CompanionDeviceId = "physical-a" }).Validate());
        Expect(() => (settings with { LeftStick = (CoPilotAxisOwner)99 }).Validate());
        Expect(() => (settings with { Buttons = owned.Buttons.Add(ControlId.LeftTrigger, CoPilotButtonOwner.Primary) }).Validate());
        Expect(() => (settings with { PrimaryDeviceId = "bad\n" }).Validate());
        string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.CoPilot.{Guid.NewGuid():N}");
        try
        {
            var store = new ProfileStore(directory); Equal(false, store.LoadCoPilotSettings().Enabled);
            store.SaveCoPilotSettings(owned);
            var restored = new ProfileStore(directory).LoadCoPilotSettings();
            Equal(false, restored.Enabled); Equal(owned.PrimaryDeviceId, restored.PrimaryDeviceId);
            Equal(CoPilotAxisOwner.Strongest, restored.LeftStick); Equal(CoPilotButtonOwner.Companion, restored.Buttons[ControlId.A]);
            File.WriteAllText(Path.Combine(directory, "co-pilot-input.json"), "{\"enabled\":true,\"primaryDeviceId\":null}");
            Equal(false, store.LoadCoPilotSettings().Enabled);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        var trace = new VisionTrainingTrace(); trace.SetInputComposition(settings, true, true);
        Equal(settings, trace.InputComposition!.Settings);
        trace.Record(default);
        try { trace.SetInputComposition(settings with { Enabled = false }); throw new Exception("Relabeling recorded composition was accepted."); }
        catch (InvalidOperationException) { }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Expect(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid co-pilot settings accepted."); }
}
