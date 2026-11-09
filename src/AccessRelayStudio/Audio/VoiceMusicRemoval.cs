using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EliteScriptStudio.Audio;

internal static class VoiceMusicRemoval
{
    private const string TemporaryPrefix = "AccessRelay-voice-cleaned-";

    public static bool IsAvailable => DialogueSeparatorModel.IsAvailable;

    public static Task<Result> RemoveAsync(string sourcePath, TimeSpan start, TimeSpan end, CancellationToken cancellation) =>
        Task.Run(() => Remove(sourcePath, start, end, cancellation), cancellation);

    private static Result Remove(string sourcePath, TimeSpan start, TimeSpan end, CancellationToken cancellation)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The bundled dialogue separation model is missing.");

        const int rate = VoiceProfiles.ReferenceSampleRate;
        var input = VoiceProfiles.ReadSelection(sourcePath, start, end, DialogueSeparatorModel.SampleRate, cancellation);
        var dialogue = DialogueSeparatorModel.Separate(input, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (dialogue.Length != input.Length)
            throw new InvalidDataException("Dialogue separation returned an incomplete clip.");

        int expected = (int)Math.Round((end - start).TotalSeconds * rate);
        var samples = new WdlResamplingSampleProvider(new DialogueSamples(dialogue), rate);
        string path = Path.Combine(Path.GetTempPath(), TemporaryPrefix + Guid.NewGuid().ToString("N") + ".wav");
        bool created = false;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                created = true;
                using var writer = new WaveFileWriter(file, new WaveFormat(rate, 16, 1));
                float peak = 0;
                var buffer = new float[4096];
                for (int remaining = expected; remaining > 0;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = samples.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (count == 0)
                        throw new InvalidDataException("The cleaned dialogue could not be resampled.");
                    for (int index = 0; index < count; index++)
                    {
                        float value = buffer[index];
                        buffer[index] = float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
                        peak = Math.Max(peak, Math.Abs(buffer[index]));
                    }
                    writer.WriteSamples(buffer, 0, count);
                    remaining -= count;
                }
                if (peak <= 0.0001f)
                    throw new InvalidDataException("No audible dialogue was found in the selected range.");
            }
            cancellation.ThrowIfCancellationRequested();
            return new Result(path, TimeSpan.FromSeconds((double)expected / rate));
        }
        catch
        {
            if (created)
                DeleteTemporary(path);
            throw;
        }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { _ = RetryDeleteAsync(path); }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task RetryDeleteAsync(string path)
    {
        // A canceled preview can still be closing its reader when the temporary clip is released.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(150).ConfigureAwait(false);
            try { File.Delete(path); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    internal sealed class Result(string path, TimeSpan duration) : IDisposable
    {
        private int _disposed;
        public string Path { get; } = path;
        public TimeSpan Duration { get; } = duration;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                DeleteTemporary(Path);
        }
    }

    private sealed class DialogueSamples(float[] samples) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(DialogueSeparatorModel.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - _position);
            samples.AsSpan(_position, available).CopyTo(buffer.AsSpan(offset));
            // Let the resampler finish the exact selected duration using silence beyond the endpoint.
            buffer.AsSpan(offset + available, count - available).Clear();
            _position += available;
            return count;
        }
    }
}
