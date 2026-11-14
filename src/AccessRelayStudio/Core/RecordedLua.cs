using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;

namespace EliteScriptStudio.Core;

/// <summary>Lua is the editable source; raw capture values live inside that source.</summary>
public static class RecordedLua
{
    private static readonly (string Name, RecordingChannels Mask)[] Columns =
    [
        ("LeftX", RecordingChannels.LeftStickX), ("LeftY", RecordingChannels.LeftStickY),
        ("RightX", RecordingChannels.RightStickX), ("RightY", RecordingChannels.RightStickY),
        ("LeftTrigger", RecordingChannels.LeftTrigger), ("RightTrigger", RecordingChannels.RightTrigger),
        .. ControlMap.All.Where(control => (uint)control < 65536).Select(control => (control.ToString(), (RecordingChannels)(uint)control))
    ];

    public static LuaControllerScript Convert(MacroRecording recording)
    {
        recording.Validate();
        var columns = Columns.Where(column => recording.Channels.HasFlag(column.Mask)).ToArray();
        var source = new StringBuilder("-- Recorded controller input. Times are milliseconds from the start.\n")
            .Append("-- Columns: timeMs,").AppendJoin(',', columns.Select(column => column.Name)).Append('\n')
            .Append("-- Edit the captured values below, or add Lua logic around output.replay.\n")
            .Append("output.replay(").Append(recording.DurationMs).Append(", \"").AppendJoin(',', columns.Select(column => column.Name)).Append("\", [[\n");
        foreach (var frame in recording.Frames)
        {
            source.Append(frame.TimeMs);
            foreach (var column in columns) source.Append(',').Append(Read(frame.State, column.Name));
            source.Append('\n');
        }
        source.Append("]])\n");
        var script = new LuaControllerScript(recording.Id, recording.Name, source.ToString());
        script.Validate(); return script;
    }

    internal static MacroRecording Parse(int durationMs, string header, string data)
    {
        if (data.Length > LuaControllerScript.MaxSourceLength || header.Length > 512)
            throw new InvalidDataException("The replay data is too large.");
        var names = header.Split(',', StringSplitOptions.TrimEntries);
        if (names.Length == 0 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Choose distinct replay columns.");
        var columns = names.Select(name => Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (columns.Any(column => column.Mask == RecordingChannels.None)) throw new InvalidDataException("Unknown replay column.");
        var lines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > MacroRecording.MaxFrames) throw new InvalidDataException("A replay needs 1–4096 input samples.");
        var frames = ImmutableArray.CreateBuilder<RecordedFrame>(lines.Length);
        foreach (var line in lines)
        {
            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length != columns.Length + 1) throw new InvalidDataException("Each replay row needs a time and one value per column.");
            var values = new int[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                if (!int.TryParse(cells[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
                    throw new InvalidDataException("Replay times and raw input values must be integers.");
            ControllerState state = default;
            for (int i = 0; i < columns.Length; i++) state = Write(state, columns[i].Name, values[i + 1]);
            frames.Add(new(values[0], state));
        }
        var result = new MacroRecording(Guid.NewGuid(), "Lua replay", columns.Aggregate(RecordingChannels.None, (mask, column) => mask | column.Mask), frames.MoveToImmutable(), durationMs);
        result.Validate(); return result;
    }

    // Warm constant generated replay blocks when scripts compile, outside input polling.
    internal static IEnumerable<(int Duration, string Header, string Data)> Constants(string source)
    {
        int cursor = 0;
        const string prefix = "output.replay(";
        while ((cursor = source.IndexOf(prefix, cursor, StringComparison.Ordinal)) >= 0)
        {
            int start = cursor + prefix.Length, comma = source.IndexOf(", \"", start, Math.Min(12, source.Length - start), StringComparison.Ordinal);
            cursor = start;
            if (comma < 0 || !int.TryParse(source.AsSpan(start, comma - start), out int duration)) continue;
            int headerStart = comma + 3, headerEnd = source.IndexOf("\", [[\n", headerStart, Math.Min(520, source.Length - headerStart), StringComparison.Ordinal);
            if (headerEnd < 0) continue;
            int dataStart = headerEnd + 6, dataEnd = source.IndexOf("]])", dataStart, StringComparison.Ordinal);
            if (dataEnd < 0) continue;
            yield return (duration, source[headerStart..headerEnd], source[dataStart..dataEnd]);
            cursor = dataEnd + 3;
        }
    }

    private static int Read(ControllerState state, string column) => column switch
    {
        "LeftX" => state.LeftX, "LeftY" => state.LeftY, "RightX" => state.RightX, "RightY" => state.RightY,
        "LeftTrigger" => state.LeftTrigger, "RightTrigger" => state.RightTrigger,
        _ => (state.Buttons & (ushort)Enum.Parse<ControlId>(column)) != 0 ? 1 : 0
    };

    private static ControllerState Write(ControllerState state, string column, int value)
    {
        bool trigger = column is "LeftTrigger" or "RightTrigger";
        bool axis = column is "LeftX" or "LeftY" or "RightX" or "RightY";
        if (value < (axis ? short.MinValue : 0) || value > (axis ? short.MaxValue : trigger ? 255 : 1))
            throw new InvalidDataException("Raw sticks need -32768–32767, triggers 0–255, and buttons 0 or 1.");
        return column switch
        {
            "LeftX" => state with { LeftX = (short)value }, "LeftY" => state with { LeftY = (short)value },
            "RightX" => state with { RightX = (short)value }, "RightY" => state with { RightY = (short)value },
            "LeftTrigger" => state with { LeftTrigger = (byte)value }, "RightTrigger" => state with { RightTrigger = (byte)value },
            _ => state with { Buttons = value == 1 ? (ushort)(state.Buttons | (ushort)Enum.Parse<ControlId>(column)) : state.Buttons }
        };
    }
}

public static class LuaProfileMigration
{
    public static StudioProfile Convert(StudioProfile source)
    {
        source.Validate();
        if (source.Recordings.Count == 0 && source.Timings.Count == 0) return source;
        var scripts = source.LuaScripts.ToBuilder();
        var bindings = source.Bindings.ToBuilder();
        var recordingIds = new Dictionary<Guid, Guid>();
        foreach (var recording in source.Recordings.Values)
        {
            var script = RecordedLua.Convert(recording);
            if (scripts.ContainsKey(script.Id)) script = script with { Id = Guid.NewGuid() };
            scripts.Add(script.Id, script); recordingIds.Add(recording.Id, script.Id);
        }
        var builtins = new Dictionary<ScriptKind, Guid>();
        foreach (var (control, binding) in source.Bindings)
        {
            if (binding.Script is ScriptKind.Passthrough or ScriptKind.Lua) continue;
            Guid id;
            if (binding.Script == ScriptKind.Recorded) id = recordingIds[binding.RecordingId!.Value];
            else if (!builtins.TryGetValue(binding.Script, out id))
            {
                id = Guid.NewGuid(); builtins.Add(binding.Script, id);
                scripts.Add(id, Builtin(source, binding.Script, id));
            }
            bindings[control] = binding with { Script = ScriptKind.Lua, RecordingId = null, LuaScriptId = id };
        }
        var result = source with { LuaScripts = scripts.ToImmutable(), Bindings = bindings.ToImmutable(),
            Recordings = ImmutableDictionary<Guid, MacroRecording>.Empty, Timings = ImmutableDictionary<ShotVariant, ScriptTiming>.Empty };
        result.Validate(); foreach (var script in result.LuaScripts.Values) _ = new LuaControllerProgram(script);
        return result;
    }

    private static LuaControllerScript Builtin(StudioProfile profile, ScriptKind kind, Guid id)
    {
        var source = new StringBuilder("-- Converted controller action. Edit this Lua script to change its behavior.\n");
        void Append(ShotVariant variant)
        {
            int time = 0;
            foreach (var phase in Trajectory.Build(profile, variant))
            {
                source.Append("output.axis_raw(\"RightY\", ").Append(phase.RightY).Append(")\n");
                if (phase.RightX is { } x) source.Append("output.axis_raw(\"RightX\", ").Append(x).Append(")\n");
                else source.Append("output.release(\"RightX\")\n");
                time += phase.DurationMs;
                source.Append("wait_until(").Append(time).Append(")\n");
            }
            if (time > 60000) throw new InvalidDataException("The old action exceeds the Lua runtime's 60-second limit; its original profile was kept.");
        }
        if (kind == ScriptKind.IntentionalMiss)
        {
            source.Append("if state.get(\"late\") == 0 then\n"); Append(ShotVariant.MissEarly);
            source.Append("state.set(\"late\", 1)\nelse\n"); Append(ShotVariant.MissLate);
            source.Append("state.set(\"late\", 0)\nend\n");
        }
        else Append(Trajectory.VariantFor(kind));
        var name = kind switch { ScriptKind.Rhythm => "Timed input", ScriptKind.Moving => "Moving input", ScriptKind.Stepback => "Stepback", _ => "Alternating input" };
        return new(id, name, source.ToString());
    }
}
