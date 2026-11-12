using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using WinRT;

namespace EliteScriptStudio;

/// <summary>Local human review of completed training takes. Never derives optimal timing automatically.</summary>
internal static class VisionDatasetReview
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const int MaximumShots = 512;
    private const string TargetTimeBasis = "captured-target-game-phase";
    private const int Width = 256, Height = 256, FrameBytes = Width * Height * 4;
    private static readonly string[] Splits = ["train", "validation", "held-out"];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        WriteIndented = true
    };
    [ComImport, Guid("905A0FEF-BC53-11DF-8C49-001E4FC686DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess { void Buffer(out nint data); }
    private sealed record RegionFrameIndex(string Name, string File, long OffsetBytes, int ByteLength);
    private sealed record FrameIndex(long CaptureTicks, long OffsetBytes, int ByteLength, string File,
        long Sequence = 0, RegionFrameIndex[]? Evidence = null);
    private sealed record EvidenceStream(string Name, string File, int Width, int Height, int Stride,
        string PixelFormat, VisionCrop Crop);
    private sealed record CaptureEvidence(int Version, string Purpose, EvidenceStream[] Streams);
    private sealed record ReviewRegion(string Name, string Label, string File, int Width, int Height)
    { internal int ByteLength => checked(Width * Height * 4); }
    private sealed record Session(int SchemaVersion, string SessionId, bool Complete, long StopwatchFrequency,
        int StoredWidth, int StoredHeight, int StoredStride, string PixelFormat, FrameIndex[] Frames,
        CaptureEvidence? CaptureEvidence = null);
    private sealed record Shot(string ShotId, long StartTicks, long EndTicks, long? TargetReleaseTicks,
        bool IsTargetShot, bool PlayerIdentityConfirmed, bool Reviewed);
    private sealed record Labels(int SchemaVersion, string SessionId, string Split, Shot[] Shots,
        string LabelTimeBasis = TargetTimeBasis);

    internal static async Task ShowAsync(XamlRoot root, ElementTheme theme, Microsoft.UI.WindowId owner)
    {
        var picker = new FolderPicker(owner);
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        using var take = new Take(folder.Path);
        var dialog = StudioDialog.Create(root, theme);
        dialog.Title = "Review training take";
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        var panel = new StackPanel { Spacing = 10, Width = 620 };
        panel.Children.Add(Text("Mark the captured frame showing the desired target game phase, justified by a controlled practice attempt and reviewed game feedback. The target is not the physical button-up time or the PC virtual-controller send time. Runtime timing separately compensates video and input delay; labeling a button/send timestamp would compensate that delay twice. Ball departure alone does not establish the optimal target."));
        panel.Children.Add(Text("Labels use captured-target-game-phase timing. Choosing one frame has the recording's sampling uncertainty; it does not establish exact sub-millisecond Xbox timing."));
        panel.Children.Add(Text("Keep each complete session in one dataset split. Reserve held-out sessions for final evaluation; do not tune on them."));
        if (take.HasEvidence)
            panel.Children.Add(Text("This clip includes synchronized regions. Choose an available capture region, then use the same frame slider to inspect the shot and later feedback. Enriched clips still need reviewed label conversion before model training; nothing is extracted automatically."));
        panel.Children.Add(Text("Session: " + take.Session.SessionId));
        var split = new ComboBox { Header = "Dataset split for this entire take", ItemsSource = Splits, SelectedItem = take.Labels.Split,
            FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(split);
        var regionPicker = new ComboBox { Header = "Capture region", ItemsSource = take.Regions,
            DisplayMemberPath = "Label", SelectedIndex = 0, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = take.HasEvidence ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetName(regionPicker, "Recorded capture region");
        panel.Children.Add(regionPicker);
        var bitmap = new WriteableBitmap(Width, Height);
        var preview = new Image { Source = bitmap, Width = take.HasEvidence ? 600 : 320, Height = take.HasEvidence ? 340 : 320, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(preview, "Training frame preview");
        panel.Children.Add(preview);
        var frameText = Text("");
        panel.Children.Add(frameText);
        var scrub = new Slider { Minimum = 0, Maximum = take.Session.Frames.Length - 1,
            StepFrequency = 1, SmallChange = 1, LargeChange = 10 };
        AutomationProperties.SetName(scrub, "Training frame index");
        panel.Children.Add(scrub);
        var movement = Row();
        panel.Children.Add(movement);
        var existing = new ComboBox { Header = "Saved labels (select to edit)", DisplayMemberPath = "ShotId",
            FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(existing);
        var shotId = new TextBox { Header = "Shot / interval ID", MaxLength = 80, FontSize = 14 };
        var isTarget = new CheckBox { Content = "This interval is an intended-player target shooting attempt", IsChecked = true, FontSize = 14 };
        var identity = new CheckBox { Content = "I confirmed the intended player's identity throughout this interval", FontSize = 14 };
        var reviewed = new CheckBox { Content = "I reviewed this interval and, for a target shot, its controlled target game phase", FontSize = 14 };
        panel.Children.Add(shotId); panel.Children.Add(isTarget); panel.Children.Add(identity);
        long start = take.Session.Frames[0].CaptureTicks, end = take.Session.Frames[^1].CaptureTicks;
        long? target = null;
        string? editingId = null;
        int index = 0;
        bool populating = false;
        var markers = Text("");
        panel.Children.Add(markers);
        var markButtons = Row();
        panel.Children.Add(markButtons);
        var message = Text("Mark start/end and review the attempt before saving. Nothing is labeled automatically.");
        AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var actions = Row();
        panel.Children.Add(reviewed); panel.Children.Add(actions); panel.Children.Add(message);

        void UpdateMarkers() => markers.Text = $"Start: {start} · End: {end}\nTarget game phase (capture ticks): {(target.HasValue ? target.Value.ToString() : "not marked (allowed for negatives)")}";
        void Render()
        {
            try
            {
                index = Math.Clamp((int)Math.Round(scrub.Value), 0, take.Session.Frames.Length - 1);
                var region = regionPicker.SelectedItem as ReviewRegion ?? take.Regions[0];
                take.ReadFrame(index, region);
                if (bitmap.PixelWidth != region.Width || bitmap.PixelHeight != region.Height)
                    bitmap = new WriteableBitmap(region.Width, region.Height);
                bitmap.PixelBuffer.As<IBufferByteAccess>().Buffer(out nint destination);
                Marshal.Copy(take.Pixels, 0, destination, region.ByteLength);
                bitmap.Invalidate();
                preview.Source = bitmap;
                long ticks = take.Session.Frames[index].CaptureTicks;
                double elapsedMs = ((double)ticks - take.Session.Frames[0].CaptureTicks) * 1000 / take.Session.StopwatchFrequency;
                frameText.Text = $"{region.Label} · {region.Width} × {region.Height}\nFrame {index + 1} / {take.Session.Frames.Length} · {elapsedMs:F2} ms from take start · capture ticks {ticks}"
                    + (take.HasEvidence ? $" · source sequence {take.Session.Frames[index].Sequence}" : "");
            }
            catch (Exception error) { preview.Source = null; message.Text = "Could not read this frame: " + error.Message; }
        }
        void RefreshLabels(string? selectedId = null)
        {
            populating = true;
            var sorted = take.Labels.Shots.OrderBy(item => item.StartTicks).ThenBy(item => item.ShotId, StringComparer.Ordinal).ToArray();
            existing.ItemsSource = sorted;
            existing.SelectedItem = sorted.FirstOrDefault(item => item.ShotId == selectedId);
            populating = false;
        }
        void NewLabel()
        {
            editingId = null;
            shotId.Text = "shot-" + Guid.NewGuid().ToString("N")[..8];
            start = take.Session.Frames[index].CaptureTicks;
            end = take.Session.Frames[^1].CaptureTicks;
            target = null;
            isTarget.IsChecked = true; identity.IsChecked = false; reviewed.IsChecked = false;
            populating = true; existing.SelectedItem = null; populating = false;
            UpdateMarkers();
        }
        Button Add(Panel row, string label, Action action)
        {
            var button = new Button { Content = label, FontSize = 14 };
            button.Click += (_, _) => { try { action(); } catch (Exception error) { message.Text = error.Message; } };
            row.Children.Add(button);
            return button;
        }
        Add(movement, "Previous frame", () => scrub.Value = Math.Max(scrub.Minimum, scrub.Value - 1));
        Add(movement, "Next frame", () => scrub.Value = Math.Min(scrub.Maximum, scrub.Value + 1));
        Add(markButtons, "Mark start here", () => { start = take.Session.Frames[index].CaptureTicks; reviewed.IsChecked = false; UpdateMarkers(); });
        Add(markButtons, "Mark end here", () => { end = take.Session.Frames[index].CaptureTicks; reviewed.IsChecked = false; UpdateMarkers(); });
        var targetButton = Add(markButtons, "Mark target game phase here", () => { target = take.Session.Frames[index].CaptureTicks; reviewed.IsChecked = false; UpdateMarkers(); });
        Add(actions, "New interval", NewLabel);
        Add(actions, "Save reviewed label", () =>
        {
            var shot = new Shot(shotId.Text.Trim(), start, end, isTarget.IsChecked == true ? target : null,
                isTarget.IsChecked == true, identity.IsChecked == true, reviewed.IsChecked == true);
            var shots = take.Labels.Shots.Where(item => item.ShotId != editingId).ToList();
            if (shots.Any(item => item.ShotId == shot.ShotId)) throw new InvalidDataException("Choose an ID that is unique within this take.");
            shots.Add(shot);
            var labels = new Labels(1, take.Session.SessionId, split.SelectedItem as string ?? "train", shots.ToArray());
            take.Save(labels);
            editingId = shot.ShotId;
            RefreshLabels(shot.ShotId);
            message.Text = $"Saved {shots.Count} reviewed interval(s) to labels.json. These labels have not been independently validated.";
        });
        Add(actions, "Delete selected label", () =>
        {
            if (editingId is null) throw new InvalidOperationException("Select an existing saved label first.");
            var labels = take.Labels with { Split = split.SelectedItem as string ?? "train",
                Shots = take.Labels.Shots.Where(item => item.ShotId != editingId).ToArray() };
            take.Save(labels);
            RefreshLabels(); NewLabel();
            message.Text = "Selected label deleted from labels.json. Captured frames are unchanged.";
        });
        isTarget.Checked += (_, _) => targetButton.IsEnabled = true;
        isTarget.Unchecked += (_, _) => { targetButton.IsEnabled = false; target = null; reviewed.IsChecked = false; UpdateMarkers(); };
        existing.SelectionChanged += (_, _) =>
        {
            if (populating || existing.SelectedItem is not Shot shot) return;
            editingId = shot.ShotId;
            shotId.Text = shot.ShotId;
            start = shot.StartTicks; end = shot.EndTicks;
            isTarget.IsChecked = shot.IsTargetShot;
            target = shot.TargetReleaseTicks;
            identity.IsChecked = shot.PlayerIdentityConfirmed;
            reviewed.IsChecked = shot.Reviewed;
            int closest = Array.FindIndex(take.Session.Frames, frame => frame.CaptureTicks >= shot.StartTicks);
            scrub.Value = closest < 0 ? take.Session.Frames.Length - 1 : closest;
            UpdateMarkers();
        };
        shotId.TextChanged += (_, _) => reviewed.IsChecked = false;
        identity.Click += (_, _) => reviewed.IsChecked = false;
        scrub.ValueChanged += (_, _) => Render();
        regionPicker.SelectionChanged += (_, _) => Render();
        RefreshLabels(); NewLabel(); Render();
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 680, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        await dialog.ShowAsync();
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private sealed class Take : IDisposable
    {
        private readonly string _directory, _labelsPath;
        private FileStream? _frames;
        private string? _openRegion;
        private string? _labelsHash;
        internal Session Session { get; }
        internal Labels Labels { get; private set; }
        internal ReviewRegion[] Regions { get; }
        internal bool HasEvidence => Session.CaptureEvidence is not null;
        internal byte[] Pixels { get; private set; } = [];

        internal Take(string directory)
        {
            _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            _labelsPath = Child("labels.json");
            byte[] metadata = ReadBounded(Child("session.json"));
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            bool hasEvidenceMarker = document.RootElement.TryGetProperty("captureEvidence", out var evidenceMarker);
            if (hasEvidenceMarker && evidenceMarker.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The captureEvidence metadata is malformed; choose an original supported evidence take.");
            Session = JsonSerializer.Deserialize<Session>(metadata, Json) ?? throw new InvalidDataException("Session metadata is empty.");
            if (Session.CaptureEvidence is { } versionedEvidence && versionedEvidence.Version != 1)
                throw new InvalidDataException("This captureEvidence version is not supported. Update AccessRelay before reviewing this take.");
            if (Session.SchemaVersion != 1 || !Session.Complete || string.IsNullOrWhiteSpace(Session.SessionId)
                || Session.SessionId.Length > 128 || Session.SessionId.Any(char.IsControl) || Session.StopwatchFrequency <= 0
                || Session.StoredWidth != Width || Session.StoredHeight != Height || Session.StoredStride != Width * 4
                || Session.PixelFormat != "BGRA8" || Session.Frames is not { Length: >= 2 and <= 10000 })
                throw new InvalidDataException("Choose a completed take with 2–10000 timestamped packed 256×256 BGRA frames.");
            string framePath = Child("frames.bgra");
            long length = new FileInfo(framePath).Length;
            long previous = -1;
            foreach (var frame in Session.Frames)
            {
                if (frame is null || frame.CaptureTicks <= previous || frame.CaptureTicks < 0 || frame.File != "frames.bgra"
                    || frame.ByteLength != FrameBytes || frame.OffsetBytes < 0 || frame.OffsetBytes > length - FrameBytes)
                    throw new InvalidDataException("The training take contains invalid frame timestamps or data offsets.");
                previous = frame.CaptureTicks;
            }
            Regions = ValidateEvidence(length);
            if (File.Exists(_labelsPath))
            {
                byte[] bytes = ReadBounded(_labelsPath);
                Labels = JsonSerializer.Deserialize<Labels>(bytes, Json) ?? throw new InvalidDataException("Labels are empty.");
                Validate(Labels);
                _labelsHash = Convert.ToHexString(SHA256.HashData(bytes));
            }
            else Labels = new Labels(1, Session.SessionId, "train", []);
        }

        private ReviewRegion[] ValidateEvidence(long primaryLength)
        {
            var player = new ReviewRegion("player", "Player", "frames.bgra", Width, Height);
            if (Session.CaptureEvidence is not { } evidence)
            {
                if (Session.Frames.Any(frame => frame.Evidence is { Length: > 0 }))
                    throw new InvalidDataException("Region frames have no matching captureEvidence stream metadata.");
                return [player];
            }
            bool supervised = evidence.Purpose == "meterSupervisedTraining";
            if (!supervised && evidence.Purpose != "meterOffValidation")
                throw new InvalidDataException("The captureEvidence purpose is not supported.");
            var expected = new List<ReviewRegion>
            {
                player, new("scene", "Court", "scene.bgra", 640, 360)
            };
            if (supervised) expected.Add(new("meter", "Meter", "meter.bgra", 256, 256));
            expected.Add(new("feedback", "Shot feedback", "feedback.bgra", 640, 192));
            if (evidence.Streams is null || evidence.Streams.Length != expected.Count)
                throw new InvalidDataException("The evidence stream count does not match this clip's purpose.");
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal) { ["player"] = primaryLength };
            long totalBytes = 0;
            foreach (var stream in evidence.Streams)
            {
                if (stream is null || string.IsNullOrEmpty(stream.Name) || !declared.Add(stream.Name))
                    throw new InvalidDataException("Each evidence stream needs one unique supported region name.");
                var region = expected.FirstOrDefault(item => item.Name == stream.Name);
                if (region is null || stream.File != region.File || stream.Width != region.Width || stream.Height != region.Height
                    || stream.Stride != region.Width * 4 || stream.PixelFormat != "BGRA8")
                    throw new InvalidDataException("The evidence stream filename, dimensions, stride or pixel format is unsupported.");
                stream.Crop.Validate();
                // Fixed allowlisted filenames pass through the same containment/reparse
                // check as the primary stream. Validate file lengths without opening all
                // streams or allocating their pixel buffers during metadata review.
                long streamLength = stream.Name == "player" ? primaryLength : new FileInfo(Child(stream.File)).Length;
                long expectedLength = (long)Session.Frames.Length * region.ByteLength;
                if (streamLength != expectedLength)
                    throw new InvalidDataException("An evidence stream's length does not match the recorded frame count.");
                lengths[stream.Name] = streamLength;
                totalBytes += streamLength;
            }
            if (totalBytes > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The take exceeds the two-GiB visual evidence bound.");
            long previousSequence = -1;
            for (int index = 0; index < Session.Frames.Length; index++)
            {
                var frame = Session.Frames[index];
                if (frame.Sequence < 0 || frame.Sequence <= previousSequence || frame.OffsetBytes != (long)index * FrameBytes
                    || frame.Evidence is null || frame.Evidence.Length != expected.Count - 1)
                    throw new InvalidDataException("Evidence frames must preserve one increasing source sequence and every synchronized region.");
                previousSequence = frame.Sequence;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var stored in frame.Evidence)
                {
                    if (stored is null || string.IsNullOrEmpty(stored.Name) || stored.Name == "player" || !names.Add(stored.Name))
                        throw new InvalidDataException("A frame contains missing, repeated or unsupported evidence regions.");
                    var region = expected.FirstOrDefault(item => item.Name == stored.Name);
                    if (region is null || stored.File != region.File || stored.ByteLength != region.ByteLength
                        || stored.OffsetBytes < 0 || stored.OffsetBytes != (long)index * region.ByteLength
                        || stored.OffsetBytes > lengths[region.Name] - region.ByteLength)
                        throw new InvalidDataException("An evidence frame points outside its declared region stream.");
                }
            }
            return expected.ToArray();
        }

        internal void ReadFrame(int index, ReviewRegion region)
        {
            if (_openRegion != region.Name || _frames is null)
            {
                _frames?.Dispose();
                _frames = null;
                _openRegion = null;
                // Recheck redirects when a view is selected, not only when the take
                // was opened. Only the selected stream is held open and read.
                var stream = File.Open(Child(region.File), FileMode.Open, FileAccess.Read, FileShare.Read);
                Pixels = new byte[region.ByteLength];
                _frames = stream;
                _openRegion = region.Name;
            }
            var frame = Session.Frames[index];
            long offset = region.Name == "player" ? frame.OffsetBytes
                : frame.Evidence!.First(item => item.Name == region.Name).OffsetBytes;
            _frames.Position = offset;
            _frames.ReadExactly(Pixels);
        }

        internal void Save(Labels labels)
        {
            Validate(labels);
            string? currentHash = File.Exists(_labelsPath) ? Convert.ToHexString(SHA256.HashData(ReadBounded(_labelsPath))) : null;
            if (!string.Equals(currentHash, _labelsHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Labels changed in another window. Close and reopen this take before saving.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(labels, Json);
            if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Label metadata is too large.");
            string temporary = Child("labels-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                File.Move(temporary, _labelsPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Labels = labels;
            _labelsHash = Convert.ToHexString(SHA256.HashData(bytes));
        }

        private void Validate(Labels labels)
        {
            if (labels.SchemaVersion != 1 || labels.SessionId != Session.SessionId || !Splits.Contains(labels.Split)
                || labels.LabelTimeBasis != TargetTimeBasis
                || labels.Shots is null || labels.Shots.Length > MaximumShots)
                throw new InvalidDataException("Labels must match this session, captured-target-game-phase timing, one supported split, and at most 512 intervals.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long first = Session.Frames[0].CaptureTicks, last = Session.Frames[^1].CaptureTicks;
            foreach (var shot in labels.Shots)
            {
                if (shot is null || string.IsNullOrWhiteSpace(shot.ShotId) || shot.ShotId.Length > 80
                    || shot.ShotId.Any(char.IsControl) || !ids.Add(shot.ShotId) || !shot.Reviewed
                    || shot.StartTicks < first || shot.EndTicks > last || shot.EndTicks <= shot.StartTicks)
                    throw new InvalidDataException("Each interval needs a unique readable ID, reviewed confirmation, and increasing start/end timestamps within this take.");
                if (shot.IsTargetShot && (!shot.PlayerIdentityConfirmed || shot.TargetReleaseTicks is not long target
                    || target < shot.StartTicks || target > shot.EndTicks))
                    throw new InvalidDataException("A target shot needs confirmed player identity and a reviewed release target inside its interval.");
                if (!shot.IsTargetShot && shot.TargetReleaseTicks.HasValue)
                    throw new InvalidDataException("Negative intervals must have no release target.");
            }
            var ordered = labels.Shots.OrderBy(shot => shot.StartTicks).ThenBy(shot => shot.EndTicks).ToArray();
            for (int index = 1; index < ordered.Length; index++)
                if (ordered[index].StartTicks <= ordered[index - 1].EndTicks)
                    throw new InvalidDataException($"Intervals '{ordered[index - 1].ShotId}' and '{ordered[index].ShotId}' overlap or share an inclusive endpoint. Choose distinct frame ranges.");
        }

        private string Child(string filename)
        {
            string path = Path.GetFullPath(Path.Combine(_directory, filename));
            if (!string.Equals(Path.GetDirectoryName(path), _directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Training files must stay inside the chosen session folder.");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Training files cannot redirect outside the session folder.");
            return path;
        }

        private static byte[] ReadBounded(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumMetadataBytes)
                throw new InvalidDataException("Session and label metadata must be between 1 byte and 4 MiB.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public void Dispose() => _frames?.Dispose();
    }
}
