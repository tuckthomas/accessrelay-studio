using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EliteScriptStudio.Audio;

internal sealed class VoiceTrimPreview(string path) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _preparing;
    private Selection? _selection;
    private Playback? _playback;
    private TimeSpan _position;
    private string? _errorMessage;
    private bool _disposed;

    public TimeSpan Position
    {
        get { lock (_gate) return ReadPosition(); }
    }

    public bool IsPlaying
    {
        get { lock (_gate) return _playback is { IsComplete: false }; }
    }

    public string? ErrorMessage
    {
        get { lock (_gate) return _playback?.ErrorMessage ?? _errorMessage; }
    }

    public async Task PlayAsync(TimeSpan start, TimeSpan end, TimeSpan position, CancellationToken cancellation = default)
    {
        using var prepare = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Selection? selection;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            prepare.Token.ThrowIfCancellationRequested();
            _preparing?.Cancel();
            _preparing = prepare;
            StopPlayback();
            _errorMessage = null;
            _position = position < start || position >= end ? start : position;
            selection = _selection is { } saved && saved.Start == start && saved.End == end ? saved : null;
        }

        try
        {
            if (selection is null)
            {
                var samples = await VoiceProfiles.ReadPreviewAsync(path, start, end, prepare.Token).ConfigureAwait(false);
                selection = new(start, end, samples);
            }

            lock (_gate)
            {
                prepare.Token.ThrowIfCancellationRequested();
                _selection = selection;
                _playback = new Playback(selection, _position);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_preparing == prepare)
                    _preparing = null;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _preparing?.Cancel();
            ReadPosition();
            StopPlayback();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _preparing?.Cancel();
            StopPlayback();
            _selection = null;
        }
    }

    private TimeSpan ReadPosition()
    {
        if (_playback is { } playback)
        {
            var current = playback.Position;
            if (current > _position)
                _position = current;
        }
        return _position;
    }

    private void StopPlayback()
    {
        if (_playback is not { } playback)
            return;

        _errorMessage = playback.ErrorMessage;
        _playback = null;
        playback.Dispose();
    }

    private sealed record Selection(TimeSpan Start, TimeSpan End, float[] Samples);

    private sealed class Playback : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly Stopwatch _clock = new();
        private readonly TimeSpan _start;
        private readonly TimeSpan _end;
        private string? _errorMessage;
        private int _complete;

        public Playback(Selection selection, TimeSpan position)
        {
            const int rate = VoiceProfiles.ReferenceSampleRate;
            int offset = Math.Clamp((int)Math.Round((position - selection.Start).TotalSeconds * rate), 0, selection.Samples.Length - 1);
            _start = selection.Start + TimeSpan.FromSeconds((double)offset / rate);
            _end = selection.End;

            using var devices = new MMDeviceEnumerator();
            _device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WasapiOut? output = null;
            try
            {
                output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 20);
                output.Init(new SelectionSamples(selection.Samples, offset));
                output.PlaybackStopped += Stopped;
                _output = output;
                _clock.Start();
                _output.Play();
            }
            catch
            {
                try { output?.Dispose(); }
                finally { _device.Dispose(); }
                throw;
            }
        }

        public bool IsComplete => Volatile.Read(ref _complete) != 0;
        public string? ErrorMessage => Volatile.Read(ref _errorMessage);

        public TimeSpan Position
        {
            get
            {
                if (IsComplete)
                    return ErrorMessage is null ? _end : _start;

                try
                {
                    double deviceSeconds = (double)_output.GetPosition() / _output.OutputWaveFormat.AverageBytesPerSecond;
                    double elapsed = Math.Clamp(deviceSeconds, 0, _clock.Elapsed.TotalSeconds);
                    return _start + TimeSpan.FromSeconds(Math.Min(elapsed, (_end - _start).TotalSeconds));
                }
                catch (COMException) { return _start; }
            }
        }

        private void Stopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is { } error)
                Volatile.Write(ref _errorMessage, $"Preview playback stopped. {error.Message}");
            Volatile.Write(ref _complete, 1);
        }

        public void Dispose()
        {
            _output.PlaybackStopped -= Stopped;
            try { _output.Dispose(); }
            finally { _device.Dispose(); }
        }
    }

    private sealed class SelectionSamples(float[] samples, int position) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceProfiles.ReferenceSampleRate, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count / sizeof(float), samples.Length - position);
            Buffer.BlockCopy(samples, position * sizeof(float), buffer, offset, read * sizeof(float));
            position += read;
            return read * sizeof(float);
        }
    }
}

public sealed partial class VoiceProfiles
{
    // Preview and import use the same decoder, sample alignment, mono mix, and reference rate.
    internal static Task<float[]> ReadPreviewAsync(string path, TimeSpan start, TimeSpan end, CancellationToken cancellation) =>
        Task.Run(() => ReadSelection(path, start, end, ReferenceSampleRate, cancellation), cancellation);

    internal static float[] ReadSelection(string path, TimeSpan start, TimeSpan end, int sampleRate, CancellationToken cancellation) =>
        ReadClip(ResolveSource(path), input =>
        {
            var duration = end - start;
            if (start < TimeSpan.Zero || duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(MaxAuditionSeconds))
                throw new InvalidDataException("Select between one second and five minutes of audio.");

            if (start > TimeSpan.Zero)
                SkipToOffset(input, start, cancellation);
            var source = AsMono(input);
            if (source.WaveFormat.SampleRate != sampleRate)
                source = new WdlResamplingSampleProvider(source, sampleRate);

            var samples = new float[(int)Math.Round(duration.TotalSeconds * sampleRate)];
            int offset = 0;
            while (offset < samples.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = source.Read(samples, offset, Math.Min(4096, samples.Length - offset));
                if (read == 0)
                    throw new InvalidDataException("The selected end time extends beyond the audio track.");
                for (int index = offset; index < offset + read; index++)
                    samples[index] = Normalize(samples[index]);
                offset += read;
            }
            return samples;
        });
}
