using EliteScriptStudio.Core;

// These stand-ins are compiled only by this isolated verification project. The actual
// StudioRuntime and its engine/arbiter/timers run unchanged; no controller APIs or drivers load.
namespace EliteScriptStudio.Input
{
    internal readonly record struct ControllerSample(bool IsConnected, ControllerState State,
        PaddleButtons HeldPaddles, PaddleButtons PressedPaddles, long ConnectionVersion)
    {
        internal bool SupportsBackgroundInput => true;
        internal bool PrimaryConnected { get; init; } = true;
        internal bool CompanionConnected { get; init; }
        internal ControllerState NavigationState { get; init; }
        internal bool NavigationConnected { get; init; } = true;
    }

    internal sealed class EliteInputMonitor : IControllerInput
    {
        private readonly object _gate = new();
        private ControllerState _state;
        private PaddleButtons _paddles;
        private bool _connected = true;
        private long _connectionVersion = 1;
        private CoPilotSettings _coPilot = new();
        private ControllerState _companion;
        private bool _companionConnected;
        public CoPilotSettings CoPilotSettings { get { lock (_gate) return _coPilot; } }
        public void ConfigureCoPilot(CoPilotSettings settings) { settings.Validate(); lock (_gate) { _coPilot = settings; _connectionVersion++; } }
        internal void SetCompanion(ControllerState state, bool connected = true)
        { lock (_gate) { _companion = state; if (_companionConnected != connected) _connectionVersion++; _companionConnected = connected; } }
        internal void SetState(ControllerState state, PaddleButtons paddles = PaddleButtons.None)
        { lock (_gate) { _state = state; _paddles = paddles; } }
        internal void SetConnection(bool connected, long connectionVersion)
        { lock (_gate) { _connected = connected; _connectionVersion = connectionVersion; } }
        public ControllerSample Poll()
        {
            lock (_gate) return new(_connected || (_coPilot.Enabled && _companionConnected),
                CoPilotInput.Merge(_state, _connected, _companion, _companionConnected, _coPilot), _connected ? _paddles : PaddleButtons.None,
                PaddleButtons.None, _connectionVersion) { PrimaryConnected = _connected, CompanionConnected = _coPilot.Enabled && _companionConnected,
                    NavigationState = _state, NavigationConnected = _connected };
        }
        public void SelectDevice(string? id) { }
        public void SetVirtualOutputActive(bool active) { }
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
        private int _rejectWrites;
        internal bool RejectWrites
        {
            get => Volatile.Read(ref _rejectWrites) != 0;
            set => Volatile.Write(ref _rejectWrites, value ? 1 : 0);
        }
        public string Status => "Verification output";
        public bool IsAvailable => true;
        public double? RecentHandoffMs => null;
        public bool TryWrite(ControllerState state) => !RejectWrites;
        public void Neutralize() { }
        public void Dispose() { }
    }
}
