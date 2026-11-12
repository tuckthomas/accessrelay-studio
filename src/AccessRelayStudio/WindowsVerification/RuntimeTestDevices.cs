using EliteScriptStudio.Core;

// These stand-ins are compiled only by this isolated verification project. The actual
// StudioRuntime and its engine/arbiter/timers run unchanged; no controller APIs or drivers load.
namespace EliteScriptStudio.Input
{
    internal readonly record struct ControllerSample(bool IsConnected, ControllerState State,
        PaddleButtons HeldPaddles, PaddleButtons PressedPaddles, long ConnectionVersion)
    {
        internal bool SupportsBackgroundInput => true;
    }

    internal sealed class EliteInputMonitor : IDisposable
    {
        private readonly object _gate = new();
        private ControllerState _state;
        private PaddleButtons _paddles;
        internal void SetState(ControllerState state, PaddleButtons paddles = PaddleButtons.None)
        { lock (_gate) { _state = state; _paddles = paddles; } }
        internal ControllerSample Poll() { lock (_gate) return new(true, _state, _paddles, PaddleButtons.None, 1); }
        internal void SelectDevice(string? id) { }
        internal void SetVirtualOutputActive(bool active) { }
        public void Dispose() { }
    }
}

namespace EliteScriptStudio.Output
{
    internal interface IControllerOutput : IDisposable
    {
        string Status { get; }
        bool IsAvailable { get; }
        bool TryWrite(ControllerState state);
        void Neutralize();
    }

    internal sealed class UsbipXboxOutput : IControllerOutput
    {
        public string Status => "Verification output";
        public bool IsAvailable => true;
        public double? RecentHandoffMs => null;
        public bool TryWrite(ControllerState state) => true;
        public void Neutralize() { }
        public void Dispose() { }
    }
}
