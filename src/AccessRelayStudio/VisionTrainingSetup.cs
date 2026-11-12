using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace EliteScriptStudio;

/// <summary>A frozen-frame editor; it never captures a second source or changes controller output.</summary>
internal static class VisionTrainingSetup
{
    internal readonly record struct Result(VisionTrainingCaptureSettings Capture, VisionCrop Player);

    internal static async Task<Result?> ShowAsync(XamlRoot root, ElementTheme theme, WriteableBitmap snapshot,
        VisionSettings settings, Nba2K27ShotProfile profile, int sourceWidth, int sourceHeight)
    {
        var editor = new Editor(snapshot, settings, profile, sourceWidth, sourceHeight,
            Math.Max(240, Math.Min(820, root.Size.Width - 100)));
        var dialog = StudioDialog.Create(root, theme);
        dialog.Title = "Set up training capture";
        dialog.Resources["ContentDialogMaxWidth"] = 920d;
        dialog.Content = new ScrollViewer { Content = editor.Content, MaxHeight = Math.Max(240, root.Size.Height - 220),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.PrimaryButtonText = "Save capture layout";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        Result? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { result = editor.Read(); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            { editor.Error.Text = error.Message; args.Cancel = true; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    private sealed class Editor
    {
        private static readonly string[] RegionNames = ["Gameplay area", "Player, hands and ball", "Shot meter search area", "Shot feedback"];
        private static readonly Windows.UI.Color[] RegionColors = [Colors.DodgerBlue, Colors.LimeGreen, Colors.Gold, Colors.Magenta];
        private readonly Canvas _canvas;
        private readonly ComboBox _region = new() { Header = "Area to mark", ItemsSource = RegionNames, SelectedIndex = 0, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _purpose = new() { Header = "Clip purpose", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Training — shot meter on", "Validation — shot meter off" } };
        private readonly TextBox _hud = new() { Header = "HUD layout name", MaxLength = 80, FontSize = 14 };
        private readonly TextBox _build = new() { Header = "Game build / patch (optional)", MaxLength = 120, FontSize = 14 };
        private readonly ComboBox _category = new() { Header = "Shots in this clip", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Mixed / unclassified", "Stationary", "Catch-and-shoot", "Off-dribble", "Pull-up", "Fade / leaner", "Step-back", "Free throw" } };
        private readonly NumberBox[] _coordinates = [Number("Left (%)"), Number("Top (%)"), Number("Width (%)"), Number("Height (%)")];
        private readonly VisionCrop?[] _regions;
        private readonly bool[] _marked;
        private readonly TextBlock _progress = Text("");
        private readonly TextBlock _active = Text("");
        private readonly Nba2K27ShotProfile _profile;
        private readonly int _sourceWidth, _sourceHeight;
        private Point? _dragStart;
        private uint? _pointerId;
        private int _dragIndex;
        private VisionCrop? _beforeDrag;
        private bool _beforeMarked;
        private bool _editing, _coordinatesDirty;
        internal TextBlock Error { get; } = Text("");
        internal StackPanel Content { get; } = new() { Spacing = 10 };

        internal Editor(WriteableBitmap snapshot, VisionSettings settings, Nba2K27ShotProfile profile,
            int sourceWidth, int sourceHeight, double displayWidth)
        {
            _profile = profile; _sourceWidth = sourceWidth; _sourceHeight = sourceHeight;
            var capture = settings.TrainingCapture;
            _regions = [capture.GameplayRegion, settings.Crop, capture.MeterRegion, capture.FeedbackRegion];
            bool confirmed = capture.Configured && capture.SourceWidth == sourceWidth && capture.SourceHeight == sourceHeight;
            _marked = [confirmed, confirmed, confirmed && capture.MeterRegion.HasValue, confirmed && capture.FeedbackRegion.HasValue];
            _purpose.SelectedIndex = capture.Purpose == VisionTrainingPurpose.MeterOffValidation ? 1 : 0;
            _hud.Text = capture.HudName; _build.Text = capture.GameBuild;
            _category.SelectedItem = capture.ShotCategory;
            if (_category.SelectedIndex < 0) _category.SelectedIndex = 0;
            _canvas = new Canvas { Width = displayWidth, Height = displayWidth * snapshot.PixelHeight / snapshot.PixelWidth,
                Background = new SolidColorBrush(Colors.Black) };
            var image = new Image { Source = snapshot, Width = _canvas.Width, Height = _canvas.Height,
                Stretch = Stretch.Fill, IsHitTestVisible = false };
            _canvas.Children.Add(image);
            Content.Children.Add(Text("Mark four areas on this frozen frame. Choose an area, then drag a box. For keyboard control, enter percentages and choose Apply coordinates. Live view is paused while you edit."));
            Content.Children.Add(Text($"Profile: {profile.Name} · {profile.CameraView} · shot meter {(profile.UsesShotMeter ? "on" : "off")} · source {sourceWidth} × {sourceHeight}"));
            Content.Children.Add(_purpose);
            Content.Children.Add(Text("Training requires the meter enabled both in NBA 2K and in this shot profile. Meter-off validation saves separate evaluation footage. Feedback and controller traces are evidence for review; saving a clip does not train a model."));
            Content.Children.Add(Pair(_hud, _build));
            Content.Children.Add(_category);
            Content.Children.Add(_region);
            Content.Children.Add(_active);
            Content.Children.Add(_canvas);
            Content.Children.Add(_progress);
            var numeric = new StackPanel { Spacing = 8 };
            numeric.Children.Add(Pair(_coordinates[0], _coordinates[1]));
            numeric.Children.Add(Pair(_coordinates[2], _coordinates[3]));
            var apply = new Button { Content = "Apply coordinates", FontSize = 14 };
            apply.Click += (_, _) =>
            {
                try { ApplyCoordinates(); }
                catch (InvalidDataException error) { Error.Text = error.Message; }
            };
            numeric.Children.Add(apply);
            Content.Children.Add(new Expander { Header = Text("Keyboard / exact coordinates"), Content = numeric,
                HorizontalAlignment = HorizontalAlignment.Stretch });
            Content.Children.Add(Text("Use the gameplay area to exclude browser controls and black bars. Keep hands and ball in the player area. The meter search area must cover where it moves during a shot. Mark the feedback panel where timing, tempo and coverage appear, even if it is currently hidden."));
            Content.Children.Add(Error);
            _region.SelectionChanged += (_, _) => SelectRegion();
            _purpose.SelectionChanged += (_, _) => Redraw();
            foreach (var coordinate in _coordinates)
                coordinate.ValueChanged += (_, _) => { if (!_editing) _coordinatesDirty = true; };
            _canvas.PointerPressed += PointerPressed;
            _canvas.PointerMoved += PointerMoved;
            _canvas.PointerReleased += PointerReleased;
            _canvas.PointerCanceled += (_, args) => { CancelDrag(); _canvas.ReleasePointerCapture(args.Pointer); };
            _canvas.PointerCaptureLost += (_, _) => CancelDrag();
            AutomationProperties.SetName(_canvas, "Training regions on frozen gameplay; keyboard coordinates available below");
            AutomationProperties.SetName(_region, "Choose training capture area to mark");
            SelectRegion();
        }

        internal Result Read()
        {
            if (_coordinatesDirty) throw new InvalidDataException("Choose Apply coordinates to keep your coordinate changes before saving.");
            if (!_marked[0] || !_marked[1] || !_marked[3] || (_purpose.SelectedIndex == 0 && !_marked[2]))
                throw new InvalidDataException("Mark or apply coordinates for the gameplay, player and feedback areas, plus the meter area for training.");
            var capture = new VisionTrainingCaptureSettings
            {
                Configured = true, HudName = _hud.Text.Trim(), GameBuild = _build.Text.Trim(),
                ShotCategory = _category.SelectedItem as string ?? "Mixed / unclassified",
                Purpose = _purpose.SelectedIndex == 1 ? VisionTrainingPurpose.MeterOffValidation : VisionTrainingPurpose.MeterSupervisedTraining,
                SourceWidth = _sourceWidth, SourceHeight = _sourceHeight,
                GameplayRegion = _regions[0]!.Value, MeterRegion = _purpose.SelectedIndex == 0 ? _regions[2] : null,
                FeedbackRegion = _regions[3]
            };
            capture.ValidateForRecording(_profile);
            var player = _regions[1]!.Value;
            player.Validate();
            if (!VisionTrainingCaptureSettings.Contains(capture.GameplayRegion, player))
                throw new InvalidDataException("The player area must be inside the gameplay area.");
            return new(capture, player);
        }

        private void SelectRegion()
        {
            int index = Math.Clamp(_region.SelectedIndex, 0, 3);
            _active.Text = index switch
            {
                0 => "Gameplay area: exclude browser chrome and letterboxing.",
                1 => "Player area: include the intended player's body, hands and ball. This version records a fixed region.",
                2 => "Meter search area: include the meter's full travel, not just its current fill.",
                _ => "Feedback area: include the complete timing, tempo and shot-coverage panel."
            };
            SetCoordinateFields(_regions[index] ?? _regions[0]!.Value);
            Redraw();
        }

        private void ApplyCoordinates()
        {
            if (_editing) return;
            var crop = new VisionCrop(_coordinates[0].Value / 100, _coordinates[1].Value / 100,
                _coordinates[2].Value / 100, _coordinates[3].Value / 100);
            crop.Validate();
            int index = Math.Clamp(_region.SelectedIndex, 0, 3);
            _regions[index] = crop; _marked[index] = true; _coordinatesDirty = false; Error.Text = "";
            Redraw();
        }

        private void SetCoordinateFields(VisionCrop crop)
        {
            _editing = true;
            _coordinates[0].Value = crop.Left * 100; _coordinates[1].Value = crop.Top * 100;
            _coordinates[2].Value = crop.Width * 100; _coordinates[3].Value = crop.Height * 100;
            _editing = false; _coordinatesDirty = false;
        }

        private void PointerPressed(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(_canvas);
            if (_pointerId is not null) return;
            if (!point.Properties.IsLeftButtonPressed && args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
            _canvas.CancelDirectManipulations();
            if (!_canvas.CapturePointer(args.Pointer)) return;
            _dragIndex = Math.Clamp(_region.SelectedIndex, 0, 3);
            _beforeDrag = _regions[_dragIndex]; _beforeMarked = _marked[_dragIndex];
            _dragStart = Clamp(point.Position); _pointerId = args.Pointer.PointerId; Error.Text = ""; args.Handled = true;
        }

        private void PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (_dragStart is null || _pointerId != args.Pointer.PointerId) return;
            UpdateDrag(Clamp(args.GetCurrentPoint(_canvas).Position), false); args.Handled = true;
        }

        private void PointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (_dragStart is null || _pointerId != args.Pointer.PointerId) return;
            if (!UpdateDrag(Clamp(args.GetCurrentPoint(_canvas).Position), true))
            {
                CancelDrag();
                Error.Text = "That selection was too small. The previous area was kept; drag a larger box.";
            }
            _dragStart = null; _pointerId = null; _canvas.ReleasePointerCapture(args.Pointer); args.Handled = true;
        }

        private bool UpdateDrag(Point end, bool finished)
        {
            Point start = _dragStart!.Value;
            double left = Math.Min(start.X, end.X) / _canvas.Width, top = Math.Min(start.Y, end.Y) / _canvas.Height;
            double width = Math.Abs(start.X - end.X) / _canvas.Width, height = Math.Abs(start.Y - end.Y) / _canvas.Height;
            if (width < 1d / _sourceWidth || height < 1d / _sourceHeight) return false;
            int index = _dragIndex;
            var crop = new VisionCrop(left, top, Math.Min(width, 1 - left), Math.Min(height, 1 - top));
            _regions[index] = crop; _marked[index] = finished;
            SetCoordinateFields(crop); Redraw();
            return true;
        }

        private void CancelDrag()
        {
            if (_dragStart is not null)
            {
                _regions[_dragIndex] = _beforeDrag; _marked[_dragIndex] = _beforeMarked;
                SetCoordinateFields(_beforeDrag ?? _regions[0]!.Value); Redraw();
            }
            _dragStart = null; _pointerId = null;
        }

        private Point Clamp(Point value) => new(Math.Clamp(value.X, 0, _canvas.Width), Math.Clamp(value.Y, 0, _canvas.Height));

        private void Redraw()
        {
            while (_canvas.Children.Count > 1) _canvas.Children.RemoveAt(_canvas.Children.Count - 1);
            int active = Math.Clamp(_region.SelectedIndex, 0, 3);
            for (int index = 0; index < _regions.Length; index++)
            {
                if (_regions[index] is not { } crop || (index == 2 && _purpose.SelectedIndex == 1)) continue;
                var box = new Rectangle { Width = crop.Width * _canvas.Width, Height = crop.Height * _canvas.Height,
                    Stroke = new SolidColorBrush(RegionColors[index]), StrokeThickness = index == active ? 3 : 1,
                    IsHitTestVisible = false };
                Canvas.SetLeft(box, crop.Left * _canvas.Width); Canvas.SetTop(box, crop.Top * _canvas.Height);
                _canvas.Children.Add(box);
            }
            _progress.Text = string.Join(" · ", Enumerable.Range(0, 4).Where(index => index != 2 || _purpose.SelectedIndex == 0)
                .Select(index => RegionNames[index] + (_marked[index] ? ": marked" : ": needed")));
        }

        private static NumberBox Number(string label)
        {
            var box = new NumberBox { Header = label, Minimum = 0, Maximum = 100, Value = 0, FontSize = 14,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(box, label); return box;
        }
    }

    private static TextBlock Text(string value) => new() { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private static Grid Pair(Control first, Control second)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(first); grid.Children.Add(second); Grid.SetColumn(second, 1); return grid;
    }
}
