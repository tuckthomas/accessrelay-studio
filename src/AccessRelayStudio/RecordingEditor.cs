using System.Globalization;
using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace EliteScriptStudio;

internal sealed class RecordingEditor : UserControl
{
    private readonly TextBlock _total = Caption();
    private readonly TextBlock _rangeHint = Caption();
    private readonly TextBlock _valueHint = Caption();
    private readonly TextBlock _status = Caption();
    private readonly NumberBox _first = FrameSelector("Start frame");
    private readonly NumberBox _last = FrameSelector("End boundary");
    private readonly NumberBox _frame = FrameSelector("Frame to edit");
    private readonly TextBox _duration = new() { Header = "New duration (ms)", MinWidth = 0 };
    private readonly TextBox _time = new() { Header = "Timestamp (ms)", MinWidth = 0 };
    private readonly TextBox _value = new() { Header = "Exact value", MinWidth = 0 };
    private readonly ComboBox _channel = new() { Header = "Recorded input", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Canvas _plot = new() { Width = 560, Height = 76 };
    private readonly Button _downHold = new() { Content = "Select down hold" };
    private readonly Button _upSweep = new() { Content = "Select up sweep" };
    private readonly ChannelOption[] _channels;
    private MacroRecording _draft;
    private int _start;
    private int _end;
    private int _selectedFrame;
    private int _selectedChannel;
    private bool _syncing;
    private RecordedInterval? _holdRange;
    private RecordedInterval? _sweepRange;

    internal RecordingEditor(MacroRecording recording)
    {
        _draft = recording;
        _end = recording.Frames.Length;
        _channels = GetChannels().Where(option => recording.Channels.HasFlag(option.Channel)).ToArray();
        _channel.ItemsSource = _channels;
        _channel.DisplayMemberPath = nameof(ChannelOption.Label);
        _first.Maximum = _frame.Maximum = recording.Frames.Length;
        _last.Minimum = 2;
        _last.Maximum = recording.Frames.Length + 1;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var content = new StackPanel { Spacing = 9 };
        content.Children.Add(_total);
        content.Children.Add(_status);
        if (recording.Channels.HasFlag(RecordingChannels.RightStickY))
        {
            _plot.Background = (Brush)Application.Current.Resources["StudioWindowBrush"];
            _plot.PointerPressed += (_, args) =>
            {
                double x = args.GetCurrentPoint(_plot).Position.X;
                int ms = (int)Math.Clamp(x * _draft.DurationMs / _plot.Width, 0, _draft.DurationMs - 1);
                ChangeFrame(_draft.FrameIndexAt(ms), _selectedChannel);
            };
            content.Children.Add(new Viewbox { Child = _plot, Height = 86, Stretch = Stretch.Fill });
            content.Children.Add(Caption("Right stick Y · up is positive · click the trace to select a frame."));
        }

        content.Children.Add(Heading("Adjust hold or flick timing"));
        content.Children.Add(Caption("Choose the range boundaries. Updating the range scales its frames and moves later frames together."));
        content.Children.Add(Columns(_first, _last, _duration));
        content.Children.Add(_rangeHint);
        var timingActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        timingActions.Children.Add(ActionButton("Update range", UpdateRange));
        if (recording.Channels.HasFlag(RecordingChannels.RightStickY))
        {
            _downHold.Click += (_, _) => { if (_holdRange is { } range) ChangeRange(range.Start, range.End); };
            _upSweep.Click += (_, _) => { if (_sweepRange is { } range) ChangeRange(range.Start, range.End); };
            timingActions.Children.Add(_downHold);
            timingActions.Children.Add(_upSweep);
        }
        content.Children.Add(timingActions);

        content.Children.Add(Heading("Edit one frame"));
        content.Children.Add(Columns(_frame, _time));
        content.Children.Add(Columns(_channel, _value));
        content.Children.Add(_valueHint);
        var frameActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        frameActions.Children.Add(ActionButton("Update frame", UpdateFrame));
        frameActions.Children.Add(ActionButton("Reset unapplied fields", () =>
        {
            Refresh();
            _status.Text = "Fields restored to the current edit.";
        }));
        content.Children.Add(frameActions);

        Content = content;

        _first.ValueChanged += (_, args) =>
        {
            if (!_syncing && TryFrameNumber(args.NewValue, _draft.Frames.Length, out int index))
                ChangeRange(index, Math.Max(index + 1, _end));
        };
        _last.ValueChanged += (_, args) =>
        {
            if (!_syncing && TryFrameNumber(args.NewValue, _draft.Frames.Length + 1, out int index) && index > 0)
                ChangeRange(Math.Min(_start, index - 1), index);
        };
        _frame.ValueChanged += (_, args) =>
        {
            if (!_syncing && TryFrameNumber(args.NewValue, _draft.Frames.Length, out int index))
                ChangeFrame(index, _selectedChannel);
        };
        _channel.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _channel.SelectedIndex >= 0)
                ChangeFrame(_selectedFrame, _channel.SelectedIndex);
        };
        Refresh();
    }

    public event Action<MacroRecording>? RecordingChanged;

    public void UpdateRecording(MacroRecording recording)
    {
        if (ReferenceEquals(_draft, recording))
            return;

        bool keepDuration = TimingEdited, keepTime = TimeEdited, keepValue = ValueEdited;
        _draft = recording;
        Refresh(preserveDuration: keepDuration, preserveTime: keepTime, preserveValue: keepValue);
    }

    public bool TryGetResult(out MacroRecording recording)
    {
        recording = _draft;
        if (TimingEdited || TimeEdited || ValueEdited)
        {
            Report("Update or reset the edited fields before applying the profile.");
            return false;
        }
        if (_first.Value != _start + 1 || _last.Value != _end + 1 || _frame.Value != _selectedFrame + 1)
        {
            Report("Enter valid whole frame numbers or reset the unapplied fields.");
            return false;
        }
        return true;
    }

    private int StartMs => _draft.Frames[_start].TimeMs;
    private int EndMs => _end == _draft.Frames.Length ? _draft.DurationMs : _draft.Frames[_end].TimeMs;
    private RecordedFrame CurrentFrame => _draft.Frames[_selectedFrame];
    private ChannelOption CurrentChannel => _channels[_selectedChannel];
    private bool TimingEdited => !Matches(_duration, EndMs - StartMs);
    private bool TimeEdited => !Matches(_time, CurrentFrame.TimeMs);
    private bool ValueEdited => !Matches(_value, CurrentChannel.Read(CurrentFrame.State));

    private void ChangeRange(int start, int end)
    {
        if (TimingEdited)
        {
            Refresh(preserveDuration: true, preserveTime: TimeEdited, preserveValue: ValueEdited);
            Report("Update the range or reset the duration field before selecting another range.");
            return;
        }
        _start = start;
        _end = end;
        Refresh(preserveTime: TimeEdited, preserveValue: ValueEdited);
    }

    private void ChangeFrame(int index, int channel)
    {
        if (TimeEdited || ValueEdited)
        {
            Refresh(preserveDuration: TimingEdited, preserveTime: TimeEdited, preserveValue: ValueEdited);
            Report("Update the frame or reset the fields before selecting another frame or input.");
            return;
        }
        _selectedFrame = index;
        _selectedChannel = channel;
        Refresh(preserveDuration: TimingEdited);
    }

    private void UpdateRange()
    {
        if (_first.Value != _start + 1 || _last.Value != _end + 1 || !TryInteger(_duration, out int ms))
        {
            Report("Enter whole frame numbers and a duration in whole milliseconds.");
            return;
        }
        bool keepTime = TimeEdited, keepValue = ValueEdited;
        var previous = _draft;
        try
        {
            _draft = _draft.WithIntervalDuration(_start, _end, ms);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        {
            Report(error.Message);
            return;
        }
        Refresh(preserveTime: keepTime, preserveValue: keepValue);
        _status.Text = "Range updated in the draft.";
        if (!ReferenceEquals(previous, _draft))
            RecordingChanged?.Invoke(_draft);
    }

    private void UpdateFrame()
    {
        if (_frame.Value != _selectedFrame + 1 || !TryInteger(_time, out int ms) || !TryInteger(_value, out int value))
        {
            Report("Enter a whole frame number, timestamp and input value.");
            return;
        }
        var channel = CurrentChannel;
        if (value < channel.Minimum || value > channel.Maximum)
        {
            Report($"{channel.Label} must be between {channel.Minimum} and {channel.Maximum}.");
            return;
        }
        bool keepDuration = TimingEdited;
        bool changed = TimeEdited || ValueEdited;
        try
        {
            if (changed)
                _draft = _draft.WithFrame(_selectedFrame, ms, channel.Write(CurrentFrame.State, value));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        {
            Report(error.Message);
            return;
        }
        Refresh(preserveDuration: keepDuration);
        _status.Text = $"Frame {_selectedFrame + 1} updated in the draft.";
        if (changed)
            RecordingChanged?.Invoke(_draft);
    }

    private void Refresh(bool preserveDuration = false, bool preserveTime = false, bool preserveValue = false)
    {
        _syncing = true;
        _first.Value = _start + 1;
        _last.Value = _end + 1;
        _frame.Value = _selectedFrame + 1;
        _channel.SelectedIndex = _selectedChannel;
        _time.IsEnabled = _selectedFrame != 0;
        if (!preserveDuration) _duration.Text = (EndMs - StartMs).ToString(CultureInfo.CurrentCulture);
        if (!preserveTime) _time.Text = CurrentFrame.TimeMs.ToString(CultureInfo.CurrentCulture);
        if (!preserveValue) _value.Text = CurrentChannel.Read(CurrentFrame.State).ToString(CultureInfo.CurrentCulture);
        _syncing = false;

        _total.Text = $"{_draft.DurationMs:N0} ms · {_draft.Frames.Length:N0} captured frames";
        string end = _end == _draft.Frames.Length ? " · End of macro" : "";
        _rangeHint.Text = $"{StartMs:N0}–{EndMs:N0} ms · {EndMs - StartMs:N0} ms selected{end}. Frame {_draft.Frames.Length + 1:N0} is the end of the macro.";
        int earliest = _selectedFrame == 0 ? 0 : _draft.Frames[_selectedFrame - 1].TimeMs + 1;
        int latest = _selectedFrame == 0 ? 0 : (_selectedFrame + 1 == _draft.Frames.Length
            ? _draft.DurationMs : _draft.Frames[_selectedFrame + 1].TimeMs) - 1;
        string values = CurrentChannel.Maximum == 1 ? "0 released · 1 pressed"
            : $"{CurrentChannel.Minimum:N0} to {CurrentChannel.Maximum:N0}";
        _valueHint.Text = $"Frame {_selectedFrame + 1:N0} · timestamp {earliest:N0}–{latest:N0} ms · {CurrentChannel.Label} {values}";
        DrawTrace();
    }

    private void DrawTrace()
    {
        if (!_draft.Channels.HasFlag(RecordingChannels.RightStickY))
            return;

        _plot.Children.Clear();
        double X(int ms) => _plot.Width * ms / _draft.DurationMs;
        double Y(short value) => _plot.Height / 2 - value * (_plot.Height / 2 - 4) / 32768;
        var span = new Rectangle { Width = X(EndMs) - X(StartMs), Height = _plot.Height,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(44, 42, 143, 255)) };
        Canvas.SetLeft(span, X(StartMs));
        _plot.Children.Add(span);
        _plot.Children.Add(new Line { X2 = _plot.Width, Y1 = Y(0), Y2 = Y(0), Stroke = new SolidColorBrush(Colors.Gray) });
        var points = new PointCollection();
        for (int i = 0; i < _draft.Frames.Length; i++)
        {
            var frame = _draft.Frames[i];
            int end = i + 1 == _draft.Frames.Length ? _draft.DurationMs : _draft.Frames[i + 1].TimeMs;
            points.Add(new(X(frame.TimeMs), Y(frame.State.RightY)));
            points.Add(new(X(end), Y(frame.State.RightY)));
        }
        _plot.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(Colors.DeepSkyBlue), StrokeThickness = 2 });
        double cursor = X(CurrentFrame.TimeMs);
        _plot.Children.Add(new Line { X1 = cursor, X2 = cursor, Y2 = _plot.Height, Stroke = new SolidColorBrush(Colors.Gold) });

        var intervals = _draft.FindRightStickIntervals();
        _holdRange = intervals.DownHold;
        _sweepRange = intervals.UpSweep;
        _downHold.IsEnabled = _holdRange.HasValue;
        _upSweep.IsEnabled = _sweepRange.HasValue;
    }

    private void Report(string message)
    {
        _status.Text = message;
        _status.StartBringIntoView();
    }

    private static bool TryInteger(TextBox box, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);

    private static bool Matches(TextBox box, int expected) => TryInteger(box, out int actual) && actual == expected;

    private static bool TryFrameNumber(double value, int maximum, out int index)
    {
        index = 0;
        if (value < 1 || value > maximum || value != Math.Truncate(value))
            return false;
        index = (int)value - 1;
        return true;
    }

    private static NumberBox FrameSelector(string header) => new()
    {
        Header = header, Minimum = 1, SmallChange = 1, LargeChange = 10, MinWidth = 0,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };

    private static TextBlock Caption(string text = "") => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold };

    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static Grid Columns(params FrameworkElement[] fields)
    {
        var grid = new Grid { ColumnSpacing = 9 };
        for (int i = 0; i < fields.Length; i++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(fields[i], i);
            grid.Children.Add(fields[i]);
        }
        return grid;
    }

    private static ChannelOption[] GetChannels() =>
    [
        new(RecordingChannels.RightStickY, "Right stick Y", short.MinValue, short.MaxValue, s => s.RightY, (s, v) => s with { RightY = (short)v }),
        new(RecordingChannels.RightStickX, "Right stick X", short.MinValue, short.MaxValue, s => s.RightX, (s, v) => s with { RightX = (short)v }),
        new(RecordingChannels.LeftStickY, "Left stick Y", short.MinValue, short.MaxValue, s => s.LeftY, (s, v) => s with { LeftY = (short)v }),
        new(RecordingChannels.LeftStickX, "Left stick X", short.MinValue, short.MaxValue, s => s.LeftX, (s, v) => s with { LeftX = (short)v }),
        new(RecordingChannels.LeftTrigger, "Left trigger", 0, 255, s => s.LeftTrigger, (s, v) => s with { LeftTrigger = (byte)v }),
        new(RecordingChannels.RightTrigger, "Right trigger", 0, 255, s => s.RightTrigger, (s, v) => s with { RightTrigger = (byte)v }),
        ButtonChannel(RecordingChannels.A, "A"), ButtonChannel(RecordingChannels.B, "B"),
        ButtonChannel(RecordingChannels.X, "X"), ButtonChannel(RecordingChannels.Y, "Y"),
        ButtonChannel(RecordingChannels.LeftBumper, "Left bumper"), ButtonChannel(RecordingChannels.RightBumper, "Right bumper"),
        ButtonChannel(RecordingChannels.LeftStickClick, "Left stick click"), ButtonChannel(RecordingChannels.RightStickClick, "Right stick click"),
        ButtonChannel(RecordingChannels.DPadUp, "D-pad up"), ButtonChannel(RecordingChannels.DPadDown, "D-pad down"),
        ButtonChannel(RecordingChannels.DPadLeft, "D-pad left"), ButtonChannel(RecordingChannels.DPadRight, "D-pad right"),
        ButtonChannel(RecordingChannels.View, "View"), ButtonChannel(RecordingChannels.Menu, "Menu")
    ];

    private static ChannelOption ButtonChannel(RecordingChannels channel, string label)
    {
        ushort bit = (ushort)channel;
        return new(channel, label, 0, 1, state => (state.Buttons & bit) != 0 ? 1 : 0,
            (state, value) => state with { Buttons = (ushort)(value == 0 ? state.Buttons & ~bit : state.Buttons | bit) });
    }

    private sealed record ChannelOption(RecordingChannels Channel, string Label, int Minimum, int Maximum,
        Func<ControllerState, int> Read, Func<ControllerState, int, ControllerState> Write);
}
