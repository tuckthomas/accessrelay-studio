using System.Collections.Immutable;
using System.Diagnostics;
using EliteScriptStudio.Core;

internal static class VoiceCommandVerification
{
    internal static void Run()
    {
        var binding = new VoiceCommandBinding { Name = "Pass", Phrase = "pass", Control = ControlId.A, MinimumConfidence = .9 };
        var settings = new VoiceCommandSettings { Bindings = [binding] };
        var arbiter = new VoiceCommandArbiter(1000); long generation = arbiter.Activate(settings);
        var observation = new VoiceCommandObservation(binding.Id, .95, 100, 300, 400);
        Equal(false, arbiter.Enqueue(generation - 1, observation, 410));
        Equal(false, arbiter.Enqueue(generation, observation with { Confidence = double.NaN }, 410));
        Equal(false, arbiter.Enqueue(generation, observation with { EndTicks = 500 }, 410));
        Equal(false, arbiter.Enqueue(generation, observation, 1400));
        Equal(true, arbiter.Enqueue(generation, observation, 410));
        Equal(false, arbiter.Enqueue(generation, observation, 411));
        Equal((ushort)ControlId.A, arbiter.Sample(420).Pulse.Buttons);
        Equal((ushort)ControlId.A, arbiter.Sample(519).Pulse.Buttons);
        Equal((ushort)0, arbiter.Sample(520).Pulse.Buttons);
        Equal(120d, arbiter.Status.EndToHostMs!.Value);
        var manual = default(ControllerState) with { Buttons = (ushort)ControlId.B, RightTrigger = 150 };
        Equal(manual with { Buttons = (ushort)(ControlId.A | ControlId.B) }, VoiceCommandArbiter.Compose(manual, new((ushort)ControlId.A, 0, 0, 0, 0, 0, 0)));
        arbiter.Reset("Stop"); Equal(default, arbiter.Sample(521)); Equal(false, arbiter.Enqueue(generation, observation, 522));
        settings = settings with { Bindings = [.. Enumerable.Range(0, 9).Select(i => binding with { Id = Guid.NewGuid(), Phrase = "phrase " + i })] };
        generation = arbiter.Activate(settings);
        for (int i = 0; i < 9; i++) Equal(i < 8, arbiter.Enqueue(generation, observation with { Id = settings.Bindings[i].Id }, 410));
        Equal(default, arbiter.Sample(700)); // Host stalls cannot replay old commands.
        var template = AcousticTemplates.Extract(Tone(440)); template.Validate();
        var other = AcousticTemplates.Extract(Tone(1600));
        if (AcousticTemplates.Similarity(template, template) < .999 || AcousticTemplates.Similarity(template, other) > .9)
            throw new Exception("Personal sound matching lost its spectral distinction.");
        settings = new() { Bindings = [binding with { Phrase = "", Templates = [template], MinimumConfidence = .95 }] };
        Equal(binding.Id, AcousticTemplates.Match(template, settings)!.Value.Id);
        Equal(null, AcousticTemplates.Match(other, settings));
        Equal(null, AcousticTemplates.Match(template, settings with { Bindings = [.. settings.Bindings, binding with { Id = Guid.NewGuid(), Phrase = "", Templates = [template] }] }));
        var segmenter = new AcousticUtteranceSegmenter(.01);
        for (int i = 0; i < 100; i++) Equal(null, segmenter.Push(new short[320], i));
        foreach (var block in Tone(440).Chunk(320)) Equal(null, segmenter.Push(block, 200));
        AcousticUtterance? utterance = null;
        for (int i = 0; i < 6; i++) utterance = segmenter.Push(new short[320], 300);
        Equal(12 * 320, utterance!.Pcm.Length); // 40 ms pre-roll plus 200 ms sound.
        var longSound = new AcousticUtteranceSegmenter(.01);
        for (int i = 0; i < 200; i++) Equal(null, longSound.Push(Tone(440)[..320], i));
        for (int i = 0; i < 6; i++) Equal(null, longSound.Push(new short[320], i));
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.VoiceCommands." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoiceCommandStore(directory); Guid profile = Guid.NewGuid(); store.Save(profile, settings);
            Equal(1, store.Load(profile).Bindings[0].Templates.Length); Equal(0, store.Load(Guid.NewGuid()).Bindings.Length);
            Expect(() => store.Save(profile, settings with { Bindings = [binding, binding] }));
            Expect(() => (settings with { MinimumRms = double.NaN }).Validate());
            Expect(() => (settings with { Bindings = [binding with { DurationMs = 10000 }] }).Validate());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        var profileForSequence = StudioProfile.CreateDefault() with { ScriptsEnabled = true };
        var engine = new MacroEngine(); engine.Apply(profileForSequence); engine.Tick(default, PaddleButtons.None, true, 0);
        var sequence = engine.Tick(default, PaddleButtons.None, true, 1, externalTrigger: ControlId.LargeRight);
        if (sequence.RightY == 0) throw new Exception("Voice sequence request did not use the existing engine.");
        engine.CancelExternal(); Equal(default(ControllerState), engine.Tick(default, PaddleButtons.None, true, 2));
        engine.Tick(default, PaddleButtons.LargeRight, true, 3); engine.CancelExternal();
        if (!engine.Status.IsActive) throw new Exception("Cancelling voice interfered with a physical sequence.");
        PumpChecks(binding);
    }
    private static void PumpChecks(VoiceCommandBinding binding)
    {
        var settings = new VoiceCommandSettings { Bindings = [binding] };
        using var blocked = new BlockingRecognizer(binding.Id);
        int delivered = 0, stopped = 0;
        using (var pump = new VoiceRecognitionPump(settings, () => true, _ => Interlocked.Increment(ref delivered), () => blocked, () => Interlocked.Increment(ref stopped)))
        {
            Feed(pump); if (!blocked.Entered.Wait(3000)) throw new Exception("Fake recognizer was not called.");
            bool rejected = false;
            for (int i = 0; i < 26; i++) rejected |= !pump.TryWrite(new short[320], Stopwatch.GetTimestamp());
            Equal(true, rejected); Equal(false, pump.Status.Running);
            if (pump.Status.Error is null) throw new Exception("Audio overload was not reported.");
            pump.Dispose(); blocked.Release.Set();
            if (!pump.Completion.Wait(3000)) throw new Exception("Recognition did not exit.");
            Equal(0, delivered); Equal(1, stopped);
        }
        using (var pump = new VoiceRecognitionPump(settings, () => true, _ => delivered++, () => throw new InvalidOperationException("Synthetic recognizer fault"), () => { }))
        { if (!pump.Completion.Wait(3000) || pump.Status.Error != "Synthetic recognizer fault") throw new Exception("Recognizer fault was not surfaced."); }
        using (var pump = new VoiceRecognitionPump(settings, () => false, _ => delivered++, () => null, () => { }))
        { pump.Ready.Wait(3000); pump.TryWrite(new short[320], Stopwatch.GetTimestamp()); if (!pump.Completion.Wait(3000)) throw new Exception("Context cancellation did not stop capture."); }
        using (var pump = new VoiceRecognitionPump(settings, () => true, _ => delivered++, () => null, () => { }, learning: true))
        { Feed(pump); if (!pump.Learned.Wait(3000)) throw new Exception("Synthetic personal sound was not recorded."); pump.Learned.Result.Validate(); Equal(0, delivered); }
    }
    private static void Feed(VoiceRecognitionPump pump)
    {
        if (!pump.Ready.Wait(3000)) throw new Exception("Recognizer did not finish warm-up.");
        foreach (var frame in Tone(440).Chunk(320)) pump.TryWrite(frame, Stopwatch.GetTimestamp());
        for (int i = 0; i < 6; i++) pump.TryWrite(new short[320], Stopwatch.GetTimestamp());
    }
    private static short[] Tone(double frequency) => Enumerable.Range(0, 3200).Select(i => (short)(14000 * Math.Sin(2 * Math.PI * frequency * i / 16000))).ToArray();
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Expect(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid voice settings accepted."); }
    private sealed class BlockingRecognizer(Guid id) : ILocalPhraseRecognizer
    {
        internal ManualResetEventSlim Entered = new(), Release = new();
        public (Guid, double)? Recognize(AcousticUtterance utterance) { Entered.Set(); Release.Wait(); return (id, .99); }
        public void Dispose() { Release.Set(); }
    }
}
