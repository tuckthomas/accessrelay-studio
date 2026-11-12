using EliteScriptStudio.Core;

internal static class SwitchNavigationVerification
{
    internal static void Run()
    {
        var input = new SwitchNavigationInput();
        input.Configure(new(true, ControlId.DPadRight, ControlId.A, 50));
        var select = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        var step = default(ControllerState) with { Buttons = (ushort)ControlId.DPadRight };
        input.Sample(select, PaddleButtons.None, true, 0);
        Check(input.Snapshot(0).Events.Length == 0, "Activation accepted a held switch.");
        input.Sample(default, PaddleButtons.None, true, 1);
        input.Sample(step, PaddleButtons.None, true, 10);
        input.Sample(step, PaddleButtons.None, true, 11);
        input.Sample(default, PaddleButtons.None, true, 12);
        input.Sample(step, PaddleButtons.None, true, 20);
        input.Sample(default, PaddleButtons.None, true, 21);
        input.Sample(select, PaddleButtons.None, true, 22);
        var snapshot = input.Snapshot(25);
        Check(snapshot.Events.Length == 2 && snapshot.Events[0].Action == SwitchNavigationAction.Step
            && snapshot.Events[1].Action == SwitchNavigationAction.Activate && snapshot.Events[1].Sequence == 2,
            "Ordered edges, per-switch debounce or held-repeat suppression failed.");
        Check(input.Snapshot(26).Events.SequenceEqual(snapshot.Events), "Status reads consumed input history.");
        input.ResetInput();
        Check(input.Snapshot(30).Generation > snapshot.Generation && input.Snapshot(30).Events.Length == 0, "Reset retained replayable events.");
        input.Sample(default, PaddleButtons.None, true, 40);
        input.Sample(select with { Buttons = (ushort)(ControlId.A | ControlId.DPadRight) }, PaddleButtons.None, true, 100);
        Check(input.Snapshot(100).Events.Length == 0, "An ambiguous chord selected an item.");
        input.Sample(default, PaddleButtons.None, true, 101);
        for (int index = 0; index < 40; index++)
        {
            input.Sample(step, PaddleButtons.None, true, 200 + index * 100);
            input.Sample(default, PaddleButtons.None, true, 201 + index * 100);
        }
        snapshot = input.Snapshot(4200);
        Check(snapshot.Events.Length == 32 && snapshot.Events[0].Sequence == 9 && snapshot.Events[^1].Sequence == 40,
            "Input history is not bounded or does not expose gaps.");
        input.Stop(); input.Sample(select, PaddleButtons.None, true, 4300);
        Check(!input.Snapshot(4300).Enabled && input.Snapshot(4300).Events.Length == 0, "Stop retained active navigation.");
        input.Configure(new(true, ControlId.LargeRight, ControlId.SmallRight, 50));
        input.Sample(default, PaddleButtons.None, true, 4400);
        input.Sample(default, PaddleButtons.LargeRight, true, 4500);
        Check(input.Snapshot(4500).Events.Single().Action == SwitchNavigationAction.Step, "Adaptive paddle input was not routed.");
        bool rejected = false;
        try { input.Configure(new(true, ControlId.A, ControlId.A)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Conflicting navigation bindings were accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
