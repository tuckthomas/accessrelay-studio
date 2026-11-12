using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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
    private sealed record FrameIndex(long CaptureTicks, long OffsetBytes, int ByteLength, string File);
    private sealed record Session(int SchemaVersion, string SessionId, bool Complete, long StopwatchFrequency,
        int StoredWidth, int StoredHeight, int StoredStride, string PixelFormat, FrameIndex[] Frames);
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
        panel.Children.Add(Text("Session: " + take.Session.SessionId));
        var split = new ComboBox { Header = "Dataset split for this entire take", ItemsSource = Splits, SelectedItem = take.Labels.Split,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(split);
        var bitmap = new WriteableBitmap(Width, Height);
        var preview = new Image { Source = bitmap, Width = 320, Height = 320, Stretch = Stretch.Uniform,
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
            HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(existing);
        var shotId = new TextBox { Header = "Shot / interval ID", MaxLength = 80 };
        var isTarget = new CheckBox { Content = "This interval is an intended-player target shooting attempt", IsChecked = true };
        var identity = new CheckBox { Content = "I confirmed the intended player's identity throughout this interval" };
        var reviewed = new CheckBox { Content = "I reviewed this interval and, for a target shot, its controlled target game phase" };
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
                take.ReadFrame(index);
                bitmap.PixelBuffer.As<IBufferByteAccess>().Buffer(out nint destination);
                Marshal.Copy(take.Pixels, 0, destination, FrameBytes);
                bitmap.Invalidate();
                long ticks = take.Session.Frames[index].CaptureTicks;
                double elapsedMs = ((double)ticks - take.Session.Frames[0].CaptureTicks) * 1000 / take.Session.StopwatchFrequency;
                frameText.Text = $"Frame {index + 1} / {take.Session.Frames.Length} · {elapsedMs:F2} ms from take start · capture ticks {ticks}";
            }
            catch (Exception error) { message.Text = "Could not read this frame: " + error.Message; }
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
        RefreshLabels(); NewLabel(); Render();
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 680, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        await dialog.ShowAsync();
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private sealed class Take : IDisposable
    {
        private readonly string _directory, _labelsPath;
        private readonly FileStream _frames;
        private string? _labelsHash;
        internal Session Session { get; }
        internal Labels Labels { get; private set; }
        internal byte[] Pixels { get; } = new byte[FrameBytes];

        internal Take(string directory)
        {
            _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            _labelsPath = Child("labels.json");
            byte[] metadata = ReadBounded(Child("session.json"));
            Session = JsonSerializer.Deserialize<Session>(metadata, Json) ?? throw new InvalidDataException("Session metadata is empty.");
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
            if (File.Exists(_labelsPath))
            {
                byte[] bytes = ReadBounded(_labelsPath);
                Labels = JsonSerializer.Deserialize<Labels>(bytes, Json) ?? throw new InvalidDataException("Labels are empty.");
                Validate(Labels);
                _labelsHash = Convert.ToHexString(SHA256.HashData(bytes));
            }
            else Labels = new Labels(1, Session.SessionId, "train", []);
            _frames = File.Open(framePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        internal void ReadFrame(int index)
        {
            _frames.Position = Session.Frames[index].OffsetBytes;
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

        public void Dispose() => _frames.Dispose();
    }
}
