using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EliteScriptStudio.Core;

internal static class StickyButtonVerification
{
    internal static void Run()
    {
        var sticky = new StickyButtons();
        sticky.Configure([ControlId.A, ControlId.LeftTrigger]);
        var down = new ControllerState((ushort)ControlId.A, 80, 75, 1200, -1300, 1400, -1500);
        // Held input at activation is not a fresh request.
        Check(sticky.Sample(down, true).Buttons == 0 && sticky.Sample(down, true).LeftTrigger == 0, "Activation latched a held control.");
        sticky.Sample(default, true);
        var output = sticky.Sample(down, true);
        Check(output.Buttons == down.Buttons && output.LeftTrigger == 255 && output.RightTrigger == down.RightTrigger
            && output.RightY == down.RightY, "Sticky composition changed unrelated channels.");
        Check(sticky.Sample(down, true) == output, "Holding a press toggled repeatedly.");
        output = sticky.Sample(default, true);
        Check(output.Buttons == (ushort)ControlId.A && output.LeftTrigger == 255, "Physical release cleared latched intent.");
        Check(sticky.Sample(down, true).Buttons == 0 && sticky.Sample(down, true).LeftTrigger == 0, "Second press did not release.");
        sticky.Sample(default, true); sticky.Sample(down, true); sticky.Reset();
        Check(sticky.Sample(down, true).Buttons == 0, "Reset accepted a pre-existing held press.");
        sticky.Sample(default, true); sticky.Sample(down, true);
        Check(sticky.Sample(default, false) == default, "Inactive processing retained sticky output.");
        Check(sticky.Sample(down, true).Buttons == 0, "Reactivation accepted input held across inactivity.");
        sticky.Configure([]);
        Check(sticky.Sample(down, true) == down, "Disabled sticky input changed manual input.");

        sticky.Configure([ControlId.A, ControlId.B]); sticky.Sample(default, true);
        sticky.Sample(down, true); sticky.Sample(default, true);
        Check(sticky.Sample(default(ControllerState) with { Buttons = (ushort)ControlId.B }, true).Buttons == (ushort)(ControlId.A | ControlId.B), "Independent button latches interfered.");

        var engine = new MacroEngine();
        engine.Apply(StudioProfile.CreateDefault() with { ScriptsEnabled = true });
        engine.Tick(default, PaddleButtons.None, true, 0);
        var latched = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        Check(engine.Tick(default, PaddleButtons.None, true, 10, manualOutput: latched) == latched && !engine.Status.IsActive,
            "Sticky manual output generated a physical macro trigger.");

        var profile = StudioProfile.CreateDefault() with { StickyControls = ImmutableHashSet.Create(ControlId.A, ControlId.LeftTrigger) };
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.Sticky." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory); store.Save(profile);
            Check(store.Load(profile.Id).StickyControls.SetEquals(profile.StickyControls), "Sticky settings did not round-trip.");
            var path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Check(json["version"]!.GetValue<int>() == 4, "Accessibility profiles need an explicit format version.");
            json["version"] = 1; json["profile"]!.AsObject().Remove("stickyControls"); File.WriteAllText(path, json.ToJsonString());
            Check(store.Load(profile.Id).StickyControls.Count == 0, "Legacy profiles enabled sticky input.");
            bool rejected = false;
            try { (profile with { StickyControls = ImmutableHashSet.Create(ControlId.LargeLeft) }).Validate(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "A paddle was accepted as a virtual output button.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
