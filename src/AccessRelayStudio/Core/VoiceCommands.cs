using System.Collections.Immutable;

namespace EliteScriptStudio.Core;

public enum VoiceCommandAction { Pulse, Sequence }
public sealed record VoiceCommandBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New command";
    public string Phrase { get; init; } = "";
    public ControlId Control { get; init; } = ControlId.A;
    public VoiceCommandAction Action { get; init; }
    public int DurationMs { get; init; } = 100;
    public double MinimumConfidence { get; init; } = .85;
    public int RefractoryMs { get; init; } = 750;
    public ImmutableArray<AcousticTemplate> Templates { get; init; } = [];
}
public sealed record VoiceCommandSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string RecognizerId { get; init; } = "";
    public double MinimumRms { get; init; } = .015;
    public ImmutableArray<VoiceCommandBinding> Bindings { get; init; } = [];
    public void Validate()
    {
        if (SchemaVersion != 1 || RecognizerId is null || RecognizerId.Length > 1024 || !double.IsFinite(MinimumRms)
            || MinimumRms is < .001 or > .2 || Bindings.IsDefault || Bindings.Length > 24
            || Bindings.Select(b => b?.Id).Distinct().Count() != Bindings.Length)
            throw new InvalidDataException("Choose a supported voice-command configuration (up to 24 commands).");
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Bindings)
        {
            if (binding is null || binding.Id == Guid.Empty || string.IsNullOrWhiteSpace(binding.Name) || binding.Name.Length > 80
                || binding.Phrase is null || binding.Phrase.Length > 80 || binding.Phrase.Any(char.IsControl)
                || (binding.Phrase.Length != 0 && (binding.Phrase.Trim() != binding.Phrase || !phrases.Add(binding.Phrase)))
                || !Enum.IsDefined(binding.Control) || (binding.Action == VoiceCommandAction.Pulse && (uint)binding.Control > (uint)ControlId.RightTrigger)
                || !Enum.IsDefined(binding.Action) || binding.DurationMs is < 20 or > 500
                || !double.IsFinite(binding.MinimumConfidence) || binding.MinimumConfidence is < .5 or > 1
                || binding.RefractoryMs is < 200 or > 5000 || binding.Templates.IsDefault || binding.Templates.Length > 3)
                throw new InvalidDataException("Voice commands need unique phrases, valid controls, bounded pulses and confidence/refractory settings.");
            foreach (var template in binding.Templates) { if (template is null) throw new InvalidDataException("Missing personal sound template."); template.Validate(); }
        }
    }
}
internal readonly record struct VoiceCommandObservation(Guid Id, double Confidence, long OnsetTicks, long EndTicks, long RecognizedTicks);
internal readonly record struct VoiceCommandIntent(ControllerState Pulse, ControlId? Sequence);
internal sealed record VoiceCommandOutputStatus(bool Enabled, long Generation, long Delivered, long Rejected,
    string? LastCommand, double? OnsetToHostMs, double? EndToHostMs, string? Message);

/// <summary>Host-owned bounded intents. Recognition never writes a controller. Call under the runtime lock.</summary>
internal sealed class VoiceCommandArbiter(long frequency)
{
    private readonly Queue<VoiceCommandObservation> _queue = new(8);
    private readonly Dictionary<Guid, long> _last = [];
    private VoiceCommandSettings _settings = new();
    private VoiceCommandBinding? _pulse;
    private long _until, _generation, _delivered, _rejected;
    private bool _enabled;
    private string? _lastCommand, _message;
    private double? _onsetLatency, _endLatency;
    internal bool Enabled => _enabled;
    internal long Generation => _generation;
    internal VoiceCommandOutputStatus Status => new(_enabled, _generation, _delivered, _rejected, _lastCommand, _onsetLatency, _endLatency, _message);
    internal long Activate(VoiceCommandSettings settings)
    { settings.Validate(); Reset("Listening explicitly enabled."); _settings = settings; _enabled = true; return _generation; }
    internal void Reset(string message)
    { _enabled = false; _generation++; _pulse = null; _queue.Clear(); _last.Clear(); _message = message; }
    internal bool Enqueue(long generation, VoiceCommandObservation observation, long now)
    {
        var binding = _settings.Bindings.FirstOrDefault(b => b.Id == observation.Id);
        if (!_enabled || generation != _generation || binding is null || !double.IsFinite(observation.Confidence)
            || observation.Confidence < binding.MinimumConfidence || observation.Confidence > 1
            || observation.OnsetTicks < 0 || observation.EndTicks < observation.OnsetTicks || observation.RecognizedTicks < observation.EndTicks
            || observation.RecognizedTicks > now || (double)now - observation.EndTicks > frequency
            || (double)now - observation.RecognizedTicks > frequency / 4d || _queue.Count == 8
            || (_last.TryGetValue(observation.Id, out long last) && ((double)now - last) * 1000 / frequency < binding.RefractoryMs))
        { _rejected++; return false; }
        _last[observation.Id] = now; _queue.Enqueue(observation); return true;
    }
    internal VoiceCommandIntent Sample(long now)
    {
        ControlId? sequence = null;
        if (!_enabled) return default;
        if (_pulse is not null && now >= _until) _pulse = null;
        // One new action per poll; expired observations never emerge after a stall.
        if (_queue.TryDequeue(out var item) && (double)now - item.RecognizedTicks <= frequency / 4d && (double)now - item.EndTicks <= frequency)
        {
            var binding = _settings.Bindings.First(b => b.Id == item.Id);
            if (binding.Action == VoiceCommandAction.Sequence) sequence = binding.Control;
            else { _pulse = binding; _until = now + (long)(binding.DurationMs * frequency / 1000d); }
            _delivered++; _lastCommand = binding.Name; _message = "Intent composed by host; timestamps do not measure console receipt.";
            _onsetLatency = ((double)now - item.OnsetTicks) * 1000 / frequency;
            _endLatency = ((double)now - item.EndTicks) * 1000 / frequency;
        }
        else if (item != default) _rejected++;
        var pulse = default(ControllerState);
        if (_pulse is { } active)
            pulse = active.Control switch
            {
                ControlId.LeftTrigger => pulse with { LeftTrigger = 255 },
                ControlId.RightTrigger => pulse with { RightTrigger = 255 },
                _ => pulse with { Buttons = (ushort)active.Control }
            };
        return new(pulse, sequence);
    }
    internal static ControllerState Compose(ControllerState manual, ControllerState pulse) => manual with
    { Buttons = (ushort)(manual.Buttons | pulse.Buttons), LeftTrigger = Math.Max(manual.LeftTrigger, pulse.LeftTrigger), RightTrigger = Math.Max(manual.RightTrigger, pulse.RightTrigger) };
}
