using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteScriptStudio.Audio;

/// <summary>Owned only by FeedbackDispatcher's worker. No WASAPI or rumble call
/// is performed by the controller polling or release deadline thread.</summary>
internal sealed class WindowsFeedbackOutput(EliteInputMonitor input) : IFeedbackOutput
{
    private WasapiOut? _audio;
    private MMDevice? _endpoint;
    private BufferedWaveProvider? _buffer;
    private Action<double>? _haptic;
    private Exception? _audioError;

    public async Task<string?> PlayAsync(FeedbackCue cue, FeedbackSettings settings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var errors = new List<string>();
        try
        {
            if (settings.AudioEnabled && settings.Volume > 0)
            {
                try
                {
                    EnsureAudio();
                    cancellation.ThrowIfCancellationRequested();
                    var samples = FeedbackSounds.Render(cue.Kind, settings.Volume);
                    var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                    _buffer!.AddSamples(bytes, 0, bytes.Length);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { StopAudio(); errors.Add("Audio cue unavailable: " + error.Message); }
            }
            cancellation.ThrowIfCancellationRequested();
            if (settings.HapticsEnabled && settings.Strength > 0)
            {
                try
                {
                    _haptic = input.CreateVibrationSink(cue.ConnectionVersion);
                    cancellation.ThrowIfCancellationRequested();
                    if (_haptic is null) errors.Add("No supported selected physical gamepad is available for rumble.");
                    else _haptic(settings.Strength);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { errors.Add("Haptic cue unavailable: " + error.Message); }
            }
            await Task.Delay(FeedbackSounds.DurationMs(cue.Kind), cancellation).ConfigureAwait(false);
        }
        finally
        {
            StopHaptic();
            if (cancellation.IsCancellationRequested) StopAudio();
        }
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }
    private void EnsureAudio()
    {
        if (Volatile.Read(ref _audioError) is { } error) throw new InvalidOperationException("The audio endpoint stopped.", error);
        if (_audio is not null) return;
        using var devices = new MMDeviceEnumerator();
        _endpoint = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(FeedbackSounds.SampleRate, 1))
            { BufferDuration = TimeSpan.FromMilliseconds(300), DiscardOnBufferOverflow = true, ReadFully = true };
        _audio = new WasapiOut(_endpoint, AudioClientShareMode.Shared, true, 20);
        _audio.PlaybackStopped += (_, args) => { if (args.Exception is { } failure) Volatile.Write(ref _audioError, failure); };
        _audio.Init(_buffer); _audio.Play();
    }
    private void StopHaptic() { var target = _haptic; _haptic = null; if (target is not null) target(0); }
    private void StopAudio()
    {
        var audio = _audio; _audio = null;
        try { audio?.Dispose(); }
        finally { _endpoint?.Dispose(); _endpoint = null; _buffer = null; Volatile.Write(ref _audioError, null); }
    }
    public void Stop() { try { StopHaptic(); } finally { StopAudio(); } }
    public void Dispose() => Stop();
}
