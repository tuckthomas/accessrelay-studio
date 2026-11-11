using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EliteScriptStudio.Audio;

public sealed record VoiceProfile(Guid Id, string Name, string? ReferencePath, TimeSpan Duration)
{
    internal VoiceSettings? Effects { get; init; }
}
internal sealed record VoiceProfileSelection(Guid? ProfileId, string? ModelPackage);
public readonly record struct VoiceWaveformBin(float Minimum, float Maximum);
public sealed record VoiceWaveform(TimeSpan Duration, IReadOnlyList<VoiceWaveformBin> Bins);

public sealed partial class VoiceProfiles
{
    public const int ReferenceSampleRate = 16_000;
    internal const int MaxAuditionSeconds = 300;
    private const int MinSamples = ReferenceSampleRate;
    private const int MaxSamples = 30 * ReferenceSampleRate;
    private const int MaxWaveformBins = 16_384;
    private const int WaveformBinMilliseconds = 10;
    private const string MetadataFile = "profile.json";
    private const string ReferenceFile = "reference.wav";
    private const string SelectionFile = "selection.json";
    private static readonly WaveFormat ReferenceFormat = new(ReferenceSampleRate, 16, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".m4v"];
    public static IReadOnlyList<string> ClipExtensions { get; } = Array.AsReadOnly<string>(
        [".wav", ".mp3", ".m4a", ".aac", ".flac", .. VideoExtensions]);

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public VoiceProfiles(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaySignalStudio", "VoiceProfiles"));
    }

    public string DirectoryPath { get; }

    internal VoiceProfileSelection? LoadSelection()
    {
        try
        {
            using var file = File.Open(Path.Combine(DirectoryPath, SelectionFile), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return file.Length <= 4096 ? JsonSerializer.Deserialize<VoiceProfileSelection>(file, Json) : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    internal void SaveSelection(Guid? profileId, string? modelPackage)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = Path.Combine(DirectoryPath, $".selection-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new VoiceProfileSelection(profileId, modelPackage), Json));
            File.Move(temporary, Path.Combine(DirectoryPath, SelectionFile), overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public IReadOnlyList<VoiceProfile> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath))
            return [];

        var profiles = new List<VoiceProfile>();
        foreach (string directory in Directory.EnumerateDirectories(DirectoryPath))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id) || id == Guid.Empty)
                continue;

            try
            {
                if (Read(id, directory) is { } profile)
                    profiles.Add(profile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A damaged reference must not hide other voice profiles.
            }
        }
        return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ThenBy(profile => profile.Id).ToArray();
    }

    internal void Delete(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Choose a saved voice profile.", nameof(id));

        string directory = Folder(id);
        string tombstone = Path.Combine(DirectoryPath, $".deleted-{id:N}-{Guid.NewGuid():N}");
        try
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The voice profile folder is not a regular directory.");

            // Commit the deletion before removing files. LoadAll only sees GUID-named folders.
            Directory.Move(directory, tombstone);
        }
        catch (IOException) when (IsMissing(directory))
        {
            return;
        }

        if (!TryRemoveDeletedProfile(tombstone))
            _ = RetryRemoveDeletedProfileAsync(tombstone);
    }

    private static bool IsMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static bool TryRemoveDeletedProfile(string directory)
    {
        try
        {
            // Check again after the rename before opening or deleting anything inside the folder.
            if (!IsLink(directory))
                RemoveFiles(directory);
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (FileNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static async Task RetryRemoveDeletedProfileAsync(string directory)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(150).ConfigureAwait(false);
            if (TryRemoveDeletedProfile(directory))
                return;
        }
    }

    public Task<VoiceProfile> ImportAsync(string name, string sourcePath, CancellationToken cancellation = default) =>
        ImportAsync(name, sourcePath, TimeSpan.Zero, cancellation);

    public Task<VoiceProfile> ImportAsync(string name, string sourcePath, TimeSpan startAt, CancellationToken cancellation = default) =>
        Task.Run(() => Import(name, sourcePath, startAt, null, cancellation), cancellation);

    public Task<VoiceProfile> ImportAsync(string name, string sourcePath, TimeSpan startAt, TimeSpan endAt,
        CancellationToken cancellation = default) =>
        Task.Run(() => Import(name, sourcePath, startAt, endAt, cancellation), cancellation);

    public static Task<VoiceWaveform> ReadWaveformAsync(string path, CancellationToken cancellation = default) =>
        Task.Run(() => ReadWaveform(ResolveSource(path), cancellation), cancellation);

    internal VoiceProfile SaveEffects(string name, VoiceSettings settings)
    {
        name = name.Trim();
        if (!ValidName(name))
            throw new ArgumentException("Enter a voice profile name between 1 and 80 characters.", nameof(name));
        if (!ValidEffects(settings))
            throw new ArgumentException("Choose valid built-in effects settings.", nameof(settings));

        return CommitProfile(_ => new(2, name, 0, settings with { Muted = false }));
    }

    private VoiceProfile Import(string name, string sourcePath, TimeSpan startAt, TimeSpan? endAt, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (!ValidName(name))
            throw new ArgumentException("Enter a voice profile name between 1 and 80 characters.", nameof(name));

        string source = ResolveSource(sourcePath);
        bool video = IsVideo(source);
        if (startAt < TimeSpan.Zero || (endAt is null && !video && startAt != TimeSpan.Zero))
            throw new ArgumentException("Choose a nonnegative start time for a video.", nameof(startAt));
        int? selectedSamples = null;
        if (endAt is TimeSpan end)
        {
            if (end <= startAt || end - startAt < TimeSpan.FromSeconds(1) || end - startAt > TimeSpan.FromSeconds(30))
                throw new InvalidDataException("Select between one and 30 seconds of audio.");
            selectedSamples = (int)Math.Round((end - startAt).TotalSeconds * ReferenceSampleRate);
        }

        return CommitProfile(temporary =>
        {
            int samples = WriteReference(source, Path.Combine(temporary, ReferenceFile), video, startAt, selectedSamples, cancellation);
            return new(1, name, samples);
        }, cancellation);
    }

    private VoiceProfile CommitProfile(Func<string, ProfileDocument> create, CancellationToken cancellation = default)
    {
        Guid id = Guid.NewGuid();
        string destination = Folder(id);
        string temporary = Path.Combine(DirectoryPath, ".import-" + id.ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var document = create(temporary);
            File.WriteAllText(Path.Combine(temporary, MetadataFile), JsonSerializer.Serialize(document, Json));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
            return ToProfile(id, destination, document);
        }
        catch
        {
            try { RemoveFiles(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static int WriteReference(string source, string destination, bool video, TimeSpan startAt, int? selectedSamples,
        CancellationToken cancellation) => ReadClip(source, input =>
        {
            if (startAt > TimeSpan.Zero)
                SkipToOffset(input, startAt, cancellation);
            ISampleProvider samples = AsMono(input);
            if (samples.WaveFormat.SampleRate != ReferenceSampleRate)
                samples = new WdlResamplingSampleProvider(samples, ReferenceSampleRate);

            using var writer = new WaveFileWriter(destination, ReferenceFormat);
            var buffer = new float[4096];
            int written = 0;
            float peak = 0;
            int limit = selectedSamples ?? MaxSamples;
            int overflowSample = video || selectedSamples is not null ? 0 : 1;
            while (written < limit + overflowSample)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = samples.Read(buffer, 0, Math.Min(buffer.Length, limit + overflowSample - written));
                if (count == 0)
                    break;
                if (count > limit - written)
                    throw new InvalidDataException("The voice clip must be 30 seconds or shorter.");

                for (int index = 0; index < count; index++)
                {
                    buffer[index] = Normalize(buffer[index]);
                    peak = Math.Max(peak, Math.Abs(buffer[index]));
                }
                writer.WriteSamples(buffer, 0, count);
                written += count;
            }

            if (selectedSamples is int expected && written < expected)
                throw new InvalidDataException("The selected end time extends beyond the audio track.");
            if (written < MinSamples)
                throw new InvalidDataException(video ? "The selected video segment must contain at least one second of audio."
                    : "The voice clip must be at least 1 second long.");
            if (peak <= 0.0001f)
                throw new InvalidDataException(video || selectedSamples is not null
                    ? "The selected segment is silent. Choose a range with audible speech."
                    : "The voice clip is silent. Choose a clip with audible speech.");
            return written;
        });

    private static VoiceWaveform ReadWaveform(string source, CancellationToken cancellation) => ReadClip(source, input =>
    {
        const int EnvelopeBins = MaxWaveformBins * 2;
        // Start with short bins and merge them as needed without trusting the container duration.
        var envelope = new VoiceWaveformBin[EnvelopeBins];
        int targetBinWidth = input.WaveFormat.SampleRate * WaveformBinMilliseconds / 1000;
        long binWidth = targetBinWidth;
        long frames = 0;
        long inBin = 0;
        int completed = 0;
        var current = new VoiceWaveformBin(1, -1);
        var buffer = new float[8192];
        var samples = AsMono(input);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = samples.Read(buffer, 0, buffer.Length);
            if (count == 0)
                break;
            frames += count;
            int offset = 0;
            while (offset < count)
            {
                if (completed == envelope.Length)
                {
                    completed /= 2;
                    for (int index = 0; index < completed; index++)
                        envelope[index] = Merge(envelope[index * 2], envelope[index * 2 + 1]);
                    binWidth *= 2;
                }

                int take = (int)Math.Min(count - offset, binWidth - inBin);
                float low = current.Minimum, high = current.Maximum;
                for (int index = offset; index < offset + take; index++)
                {
                    float value = Normalize(buffer[index]);
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                }
                current = new(low, high);
                offset += take;
                inBin += take;
                if (inBin == binWidth)
                {
                    envelope[completed++] = current;
                    current = new(1, -1);
                    inBin = 0;
                }
            }
        }

        if (frames == 0)
            throw new InvalidDataException("The selected file does not contain audio samples.");
        if (inBin > 0)
            envelope[completed++] = current;
        int binCount = (int)Math.Min(MaxWaveformBins, (frames - 1) / targetBinWidth + 1);
        var bins = new VoiceWaveformBin[binCount];
        for (int index = 0; index < bins.Length; index++)
        {
            int first = (int)((long)((double)frames * index / bins.Length) / binWidth);
            int last = Math.Min(completed - 1, (int)(((long)((double)frames * (index + 1) / bins.Length) - 1) / binWidth));
            var peak = envelope[first];
            for (int bin = first + 1; bin <= last; bin++)
                peak = Merge(peak, envelope[bin]);
            bins[index] = peak;
        }
        return new VoiceWaveform(TimeSpan.FromSeconds((double)frames / samples.WaveFormat.SampleRate), Array.AsReadOnly(bins));
    });

    private static VoiceWaveformBin Merge(VoiceWaveformBin first, VoiceWaveformBin second) =>
        new(Math.Min(first.Minimum, second.Minimum), Math.Max(first.Maximum, second.Maximum));

    private static float Normalize(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0;

    private static ISampleProvider AsMono(WaveStream input)
    {
        ISampleProvider samples = input.ToSampleProvider();
        return samples.WaveFormat.Channels == 1 ? samples : new MonoSamples(samples);
    }

    private static void SkipToOffset(WaveStream input, TimeSpan startAt, CancellationToken cancellation)
    {
        const string Message = "Choose a start time at least one second before the end of the audio track.";
        int alignment = input.WaveFormat.BlockAlign;
        double frames = Math.Round(startAt.TotalSeconds * input.WaveFormat.SampleRate);
        if (frames >= long.MaxValue / alignment)
            throw new InvalidDataException(Message);
        long remaining = (long)frames * alignment;
        if (input is WaveFileReader)
        {
            if (remaining >= input.Length)
                throw new InvalidDataException(Message);
            input.Position = remaining;
            return;
        }

        // Media Foundation seeks WebM/MKV by cluster. Skip decoded frames for an exact offset.
        var discard = new byte[(65536 / alignment) * alignment];
        while (remaining > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = input.Read(discard, 0, (int)Math.Min(discard.Length, remaining));
            if (read == 0)
                throw new InvalidDataException(Message);
            remaining -= read;
        }
    }

    private static string ResolveSource(string path)
    {
        string source = Path.GetFullPath(path);
        if (!ClipExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an audio clip or a downloaded MP4, WebM, MKV, MOV, or M4V video.");
        if (!File.Exists(source))
            throw new FileNotFoundException("The selected voice clip could not be opened.", source);
        return source;
    }

    private static T ReadClip<T>(string source, Func<WaveStream, T> read)
    {
        bool video = IsVideo(source);
        try
        {
            using WaveStream input = OpenClip(source);
            if (input.WaveFormat.Channels is < 1 or > 32 || input.WaveFormat.SampleRate is < 8_000 or > 384_000
                || input.WaveFormat.BlockAlign < 1)
                throw new InvalidDataException("The voice clip uses an unsupported channel count or sample rate.");
            return read(input);
        }
        catch (Exception error) when (error is COMException or FormatException or NotSupportedException or EndOfStreamException
            || (video && error is InvalidOperationException))
        {
            throw new InvalidDataException(video
                ? "Windows could not read an audio track from this video. Check that it contains audio and plays in Windows Media Player."
                : "Windows could not decode this voice clip. Try exporting it as a PCM WAV file.", error);
        }
    }

    private static WaveStream OpenClip(string source)
    {
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wave = new WaveFileReader(source);
            if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                return wave;
            wave.Dispose();
        }
        return new MediaFoundationReader(source, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
    }

    private static VoiceProfile? Read(Guid id, string directory)
    {
        string metadataPath = Path.Combine(directory, MetadataFile);
        string referencePath = Path.Combine(directory, ReferenceFile);
        if (IsLink(directory) || IsLink(metadataPath))
            return null;

        using var metadata = File.Open(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (metadata.Length > 4096)
            return null;
        var document = JsonSerializer.Deserialize<ProfileDocument>(metadata, Json);
        if (document is null || !ValidName(document.Name))
            return null;
        if (document is { Version: 2, Samples: 0, Effects: { } effects })
            return ValidEffects(effects) ? ToProfile(id, directory, document) : null;
        if (document is not { Version: 1, Samples: >= MinSamples and <= MaxSamples, Effects: null } || IsLink(referencePath))
            return null;

        using var reference = File.Open(referencePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (reference.Length > 2 * MaxSamples + 128)
            return null;
        using var wave = new WaveFileReader(reference);
        if (!wave.WaveFormat.Equals(ReferenceFormat) || wave.Length != 2L * document.Samples || reference.Length < wave.Length + 44)
            return null;

        return ToProfile(id, directory, document);
    }

    private static bool ValidName(string? name) => name is { Length: > 0 and <= 80 } &&
        !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    private static bool ValidEffects(VoiceSettings settings) => Enum.IsDefined(settings.Preset) &&
        settings.PitchSemitones is >= -8 and <= 8 && settings.GainDb is >= -18 and <= 12;

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static VoiceProfile ToProfile(Guid id, string directory, ProfileDocument document) => document.Effects is { } effects
        ? new(id, document.Name, null, TimeSpan.Zero) { Effects = effects }
        : new(id, document.Name, Path.Combine(directory, ReferenceFile), TimeSpan.FromSeconds((double)document.Samples / ReferenceSampleRate));

    private string Folder(Guid id) => Path.Combine(DirectoryPath, id.ToString("N"));

    private static void RemoveFiles(string directory)
    {
        File.Delete(Path.Combine(directory, ReferenceFile));
        File.Delete(Path.Combine(directory, MetadataFile));
        Directory.Delete(directory);
    }

    private sealed class MonoSamples(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _interleaved = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int length = checked(count * _channels);
            if (_interleaved.Length < length)
                _interleaved = new float[length];
            int frames = source.Read(_interleaved, 0, length) / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < _channels; channel++)
                    sum += _interleaved[frame * _channels + channel];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }

    private sealed record ProfileDocument(int Version, string Name, int Samples, VoiceSettings? Effects = null);
}
