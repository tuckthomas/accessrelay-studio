using System.IO;
using System.Collections.Immutable;
using MoonSharp.Interpreter;

namespace EliteScriptStudio.Core;

public sealed record LuaControllerScript(Guid Id, string Name, string Source)
{
    public ImmutableDictionary<string, double> Tuning { get; init; } = ImmutableDictionary<string, double>.Empty;
    public const int MaxScripts = 32;
    public const int MaxSourceLength = 32768;

    public void Validate()
    {
        GamePackManifest.ValidateNumbers(Tuning);
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new InvalidDataException("A Lua script needs an ID and a name of up to 80 characters.");
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > MaxSourceLength)
            throw new InvalidDataException($"Lua source must contain 1–{MaxSourceLength} characters.");
    }

    public static LuaControllerScript Create() => new(Guid.NewGuid(), "Button sequence", """
        -- Runs once when its assigned control is pressed.
        -- Read live inputs with input.down("A") or input.axis("RightY").
        output.button("A", true)
        wait(80)
        output.button("A", false)
        wait(40)
        output.release("A")
        """);
}

/// <summary>Compiled Lua and a cooperative controller coroutine. Only explicit controller APIs
/// and numeric math are exposed; no CLR objects, file, process, network or dynamic-load APIs.
/// Instruction budgets stop unyielding Lua code. This is not process-level memory isolation.</summary>
internal sealed class LuaControllerProgram
{
    internal const int InstructionsPerTick = 4000;
    private const long MaxRunMs = 60000;
    private readonly Script _vm;
    private readonly DynValue _chunk;
    private Coroutine? _routine;
    private ControllerState _input;
    private PaddleButtons _paddles;
    private ControllerState _values;
    private readonly Dictionary<string, double> _state = new(StringComparer.Ordinal);
    private uint _buttonOverrides;
    private byte _axisOverrides;
    private long _now, _started, _wakeAt;
    private ControlId _trigger;

    internal LuaControllerProgram(LuaControllerScript source)
    {
        source.Validate();
        Name = source.Name;
        _vm = new Script(CoreModules.Math | CoreModules.TableIterators | CoreModules.Bit32);
        var tuning = new Table(_vm);
        tuning["get"] = Callback(args =>
        {
            string key = Text(args, 0);
            if (!source.Tuning.TryGetValue(key, out double value)) throw new ScriptRuntimeException("Unknown tuning control: " + key);
            return DynValue.NewNumber(value);
        });
        _vm.Globals["tuning"] = tuning;
        var input = new Table(_vm);
        input["down"] = Callback(args => DynValue.NewBoolean(ControlMap.IsPressed(Control(args, 0), _input, _paddles)));
        input["axis"] = Callback(args => DynValue.NewNumber(ReadAxis(Text(args, 0))));
        input["trigger"] = Callback(_ => DynValue.NewString(_trigger.ToString()));
        _vm.Globals["input"] = input;
        var output = new Table(_vm);
        output["button"] = Callback(args =>
        {
            var control = Control(args, 0);
            if ((uint)control >= (uint)ControlId.LeftTrigger)
                throw new ScriptRuntimeException("output.button expects a controller button; use output.axis for triggers.");
            var down = args[1];
            if (down.Type != DataType.Boolean)
                throw new ScriptRuntimeException("Button state must be true or false.");
            _buttonOverrides |= (uint)control;
            _values = _values with { Buttons = down.Boolean ? (ushort)(_values.Buttons | (ushort)control)
                : (ushort)(_values.Buttons & ~(ushort)control) };
            return DynValue.Nil;
        });
        output["axis"] = Callback(args =>
        {
            string axis = Text(args, 0);
            int index = AxisIndex(axis);
            double value = Number(args, 1);
            bool trigger = index >= 4;
            if (value < (trigger ? 0 : -100) || value > 100)
                throw new ScriptRuntimeException(trigger ? "Trigger values must be 0–100 percent." : "Stick values must be -100–100 percent.");
            short stick = (short)Math.Round(value * 32767 / 100);
            byte pressure = (byte)Math.Round(Math.Max(0, value) * 255 / 100);
            _values = index switch
            {
                0 => _values with { LeftX = stick }, 1 => _values with { LeftY = stick },
                2 => _values with { RightX = stick }, 3 => _values with { RightY = stick },
                4 => _values with { LeftTrigger = pressure }, _ => _values with { RightTrigger = pressure }
            };
            _axisOverrides |= (byte)(1 << index);
            return DynValue.Nil;
        });
        output["release"] = Callback(args =>
        {
            string channel = Text(args, 0);
            if (Enum.TryParse<ControlId>(channel, true, out var control) && Enum.IsDefined(control)
                && (uint)control < (uint)ControlId.LeftTrigger)
                _buttonOverrides &= ~(uint)control;
            else
                _axisOverrides &= (byte)~(1 << AxisIndex(channel));
            return DynValue.Nil;
        });
        output["clear"] = Callback(_ => { _buttonOverrides = 0; _axisOverrides = 0; return DynValue.Nil; });
        _vm.Globals["output"] = output;
        _vm.Globals["wait"] = Callback(args =>
        {
            double delay = Number(args, 0);
            if (delay < 0 || delay > 10000)
                throw new ScriptRuntimeException("wait expects 0–10000 milliseconds.");
            _wakeAt = _now + Math.Max(1, (long)Math.Ceiling(delay));
            return DynValue.NewYieldReq([]);
        });
        _vm.Globals["elapsed"] = Callback(_ => DynValue.NewNumber(_now - _started));
        // Persistent, bounded numeric state survives successive triggers until profile apply.
        var state = new Table(_vm);
        state["get"] = Callback(args => DynValue.NewNumber(_state.GetValueOrDefault(StateKey(args))));
        state["set"] = Callback(args =>
        {
            string key = StateKey(args);
            if (!_state.ContainsKey(key) && _state.Count >= 64)
                throw new ScriptRuntimeException("A script can store up to 64 state values.");
            _state[key] = Number(args, 1);
            return DynValue.Nil;
        });
        _vm.Globals["state"] = state;
        try { _chunk = _vm.LoadString(source.Source, codeFriendlyName: source.Name); }
        catch (InterpreterException error) { throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error); }
    }

    internal string Name { get; }
    internal bool IsRunning => _routine is not null;

    internal void Start(ControlId trigger, long timestampMs)
    {
        Cancel();
        _trigger = trigger;
        _started = _wakeAt = timestampMs;
        _routine = _vm.CreateCoroutine(_chunk).Coroutine;
        _routine.AutoYieldCounter = InstructionsPerTick;
    }

    internal void Cancel()
    {
        _routine = null;
        _buttonOverrides = 0;
        _axisOverrides = 0;
    }

    internal void Reset() { Cancel(); _state.Clear(); }

    internal ControllerState Tick(ControllerState physical, PaddleButtons paddles, ControllerState output,
        long timestampMs, out string? error)
    {
        error = null;
        if (_routine is null)
            return output;
        _input = physical;
        _paddles = paddles;
        _now = timestampMs;
        try
        {
            if (_now - _started >= MaxRunMs)
                throw new ScriptRuntimeException("Script stopped after 60 seconds. Release and press its control to run it again.");
            if (_now >= _wakeAt)
            {
                _routine.Resume();
                if (_routine.State == CoroutineState.ForceSuspended)
                    throw new ScriptRuntimeException($"Script exceeded {InstructionsPerTick} instructions without wait().");
                if (_routine.State == CoroutineState.Dead)
                {
                    // A completed script relinquishes every channel on this sample.
                    Cancel();
                    return output;
                }
            }
            return output with
            {
                Buttons = (ushort)((output.Buttons & ~_buttonOverrides) | (_values.Buttons & _buttonOverrides)),
                LeftX = HasAxis(0) ? _values.LeftX : output.LeftX,
                LeftY = HasAxis(1) ? _values.LeftY : output.LeftY,
                RightX = HasAxis(2) ? _values.RightX : output.RightX,
                RightY = HasAxis(3) ? _values.RightY : output.RightY,
                LeftTrigger = HasAxis(4) ? _values.LeftTrigger : output.LeftTrigger,
                RightTrigger = HasAxis(5) ? _values.RightTrigger : output.RightTrigger
            };
        }
        catch (Exception failure) when (failure is InterpreterException or ArgumentException or InvalidOperationException)
        {
            error = $"{Name}: {(failure is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : failure.Message)}";
            Cancel();
            return output;
        }
    }

    private bool HasAxis(int index) => (_axisOverrides & (1 << index)) != 0;
    private double ReadAxis(string axis) => AxisIndex(axis) switch
    {
        0 => Math.Max(-100, _input.LeftX * 100.0 / 32767), 1 => Math.Max(-100, _input.LeftY * 100.0 / 32767),
        2 => Math.Max(-100, _input.RightX * 100.0 / 32767), 3 => Math.Max(-100, _input.RightY * 100.0 / 32767),
        4 => _input.LeftTrigger * 100.0 / 255, _ => _input.RightTrigger * 100.0 / 255
    };
    private static int AxisIndex(string axis) => axis.ToLowerInvariant() switch
    {
        "leftx" => 0, "lefty" => 1, "rightx" => 2, "righty" => 3,
        "lefttrigger" => 4, "righttrigger" => 5,
        _ => throw new ScriptRuntimeException($"Unknown axis '{axis}'. Use LeftX, LeftY, RightX, RightY, LeftTrigger or RightTrigger.")
    };
    private static DynValue Callback(Func<CallbackArguments, DynValue> action) => DynValue.NewCallback((_, args) => action(args));
    private static string Text(CallbackArguments args, int index) => args[index].Type == DataType.String
        ? args[index].String : throw new ScriptRuntimeException("Expected a channel name in quotes.");
    private static double Number(CallbackArguments args, int index) => args[index].Type == DataType.Number && double.IsFinite(args[index].Number)
        ? args[index].Number : throw new ScriptRuntimeException("Expected a finite number.");
    private static ControlId Control(CallbackArguments args, int index) =>
        Enum.TryParse<ControlId>(Text(args, index), true, out var control) && Enum.IsDefined(control)
            ? control : throw new ScriptRuntimeException("Unknown controller input.");
    private static string StateKey(CallbackArguments args)
    {
        string key = Text(args, 0);
        return key.Length is > 0 and <= 64 ? key : throw new ScriptRuntimeException("State keys must contain 1–64 characters.");
    }
}
