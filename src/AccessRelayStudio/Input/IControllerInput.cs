using EliteScriptStudio.Core;

namespace EliteScriptStudio.Input;

internal interface IControllerInput : IDisposable
{
    ControllerSample Poll();
    void SelectDevice(string? id);
    void SetVirtualOutputActive(bool active);
    CoPilotSettings CoPilotSettings => new();
    void ConfigureCoPilot(CoPilotSettings settings) => throw new InvalidOperationException("Co-pilot input is unavailable in this host.");
    Action<double>? CreateVibrationSink(long connectionVersion) => null;
}
