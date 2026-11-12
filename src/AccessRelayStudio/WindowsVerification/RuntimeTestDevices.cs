using EliteScriptStudio.Core;

// These fake devices implement the actual runtime library contracts. Tests use the
// same compiled StudioRuntime, engine, arbiter and timers as the application.
namespace EliteScriptStudio.Input
{
    internal sealed class FakeControllerInput : IControllerInput
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
                CoPilotInput.Merge(_state, _connected, _companion, _companionConnected, _coPilot), "Simulated device", "verification", false, "",
                _connected ? _paddles : PaddleButtons.None,
                PaddleButtons.None, _connectionVersion) { SupportsBackgroundInput = true, PrimaryConnected = _connected, CompanionConnected = _coPilot.Enabled && _companionConnected,
                    NavigationState = _state, NavigationConnected = _connected };
        }
        public void SelectDevice(string? id) { }
        public void SetVirtualOutputActive(bool active) { }
        public void Dispose() { }
    }
}

namespace EliteScriptStudio.Output
{
    internal sealed class FakeControllerOutput : IControllerOutput
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
