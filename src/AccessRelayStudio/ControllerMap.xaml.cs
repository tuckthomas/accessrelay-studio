using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace EliteScriptStudio;

public sealed partial class ControllerMap : UserControl
{
    private const uint DpadMask = (uint)(ControlId.DPadUp | ControlId.DPadDown | ControlId.DPadLeft | ControlId.DPadRight);
    private static readonly Brush IdleBackground = BrushFrom(11, 19, 29);
    private static readonly Brush SelectedBackground = BrushFrom(14, 45, 69);
    private static readonly Brush ActiveBackground = BrushFrom(6, 66, 88);
    private static readonly Brush IdleBorder = BrushFrom(51, 145, 214);
    private static readonly Brush LeaderBlue = BrushFrom(12, 158, 242);
    private static readonly Brush UnassignedDot = BrushFrom(143, 155, 169);
    private static readonly Brush ActiveCyan = BrushFrom(109, 232, 255);
    private static readonly Brush HaloFill = new SolidColorBrush(ColorHelper.FromArgb(62, 21, 195, 240));
    private readonly List<Callout> _callouts = [];
    private readonly List<(ControlId Control, Ellipse Halo)> _halos = [];
    private readonly TranslateTransform _leftStickOffset = new();
    private readonly TranslateTransform _rightStickOffset = new();
    private int _rearCalloutStart;
    private uint _selected;
    private uint _active;
    private uint _assigned;

    public event EventHandler<string>? ControlSelected;

    public ControllerMap()
    {
        InitializeComponent();
        BuildMap();
    }

    private void BuildMap()
    {
        var front = new SchematicFrame(136, -56.7, 540);
        var rear = new SchematicFrame(176, 301.7, 460);

        AddCallout("Left Trigger (LT)", ControlId.LeftTrigger, front.At(274, 165), new(31, 0, 135, 28), true);
        AddCallout("Left Bumper (LB)", ControlId.LeftBumper, front.At(188, 210), new(23, 42, 139, 28), true);
        AddCallout("Left Stick (LS)\n(Click: L3)", ControlId.LeftStick, front.At(243, 348), new(34, 110, 131, 44), true,
            stickOffset: _leftStickOffset);
        AddCallout("View Button ▣", ControlId.View, front.At(426, 347), new(35, 192, 129, 29), true, elbowX: 296);
        AddCallout("D-Pad", ControlId.DPadUp, front.At(366, 510), new(58, 258, 95, 29), true, mask: DpadMask);

        AddCallout("Right Trigger (RT)", ControlId.RightTrigger, front.At(720, 165), new(662, 0, 143, 28), false);
        AddCallout("Right Bumper (RB)", ControlId.RightBumper, front.At(812, 210), new(659, 37, 146, 28), false);
        AddCallout("Y Button", ControlId.Y, front.At(758, 281), new(690, 78, 96, 27), false,
            letterBrush: BrushFrom(251, 213, 56));
        AddCallout("X Button", ControlId.X, front.At(690, 348), new(690, 109, 96, 27), false,
            letterBrush: BrushFrom(50, 166, 250));
        AddCallout("B Button", ControlId.B, front.At(826, 350), new(690, 140, 96, 27), false,
            letterBrush: BrushFrom(250, 92, 87));
        AddCallout("A Button", ControlId.A, front.At(758, 414), new(690, 171, 96, 27), false,
            letterBrush: BrushFrom(125, 223, 74));
        AddCallout("☰ Menu Button", ControlId.Menu, front.At(573, 348), new(669, 211, 136, 29), false, elbowX: 555);
        AddCallout("Right Stick (RS)\n(Click: R3)", ControlId.RightStick, front.At(634, 500), new(669, 252, 136, 44), false,
            stickOffset: _rightStickOffset);

        AddDpadDirection(ControlId.DPadUp, "D-pad Up", front.At(366, 454));
        AddDpadDirection(ControlId.DPadDown, "D-pad Down", front.At(366, 565));
        AddDpadDirection(ControlId.DPadLeft, "D-pad Left", front.At(312, 510));
        AddDpadDirection(ControlId.DPadRight, "D-pad Right", front.At(421, 510));

        _rearCalloutStart = CalloutLayer.Children.Count;
        AddCallout("Small Left Paddle\n(P1)", ControlId.SmallLeft, rear.At(292, 521), new(48, 511, 145, 44), true);
        AddCallout("Small Right Paddle\n(P2)", ControlId.SmallRight, rear.At(708, 521), new(624, 511, 164, 44), false);
        AddCallout("Large Left Paddle\n(P3)", ControlId.LargeLeft, rear.At(272, 610), new(42, 584, 145, 44), true);
        AddCallout("Large Right Paddle\n(P4)", ControlId.LargeRight, rear.At(728, 610), new(632, 584, 156, 44), false);
    }

    public void ShowPaddles(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SchematicSurface.Height = show ? 696 : 332;
        RearSchematic.Visibility = visibility;
        for (int index = _rearCalloutStart; index < CalloutLayer.Children.Count; index++)
            CalloutLayer.Children[index].Visibility = visibility;
    }

    private void AddCallout(string label, ControlId control, Point anchor, Rect bounds, bool onLeft,
        uint mask = 0, double? elbowX = null, Brush? letterBrush = null, TranslateTransform? stickOffset = null)
    {
        if (mask == 0)
            mask = (uint)control;

        var start = new Point(onLeft ? bounds.Right : bounds.Left, bounds.Top + bounds.Height / 2);
        double direction = onLeft ? 1 : -1;
        double lead = Math.Min(Math.Abs(anchor.X - start.X) * .48, 9 + Math.Abs(anchor.Y - start.Y) * .75);
        var elbow = new Point(elbowX ?? anchor.X - direction * lead, start.Y);
        var line = new Polyline
        {
            Points = new PointCollection { start, elbow, anchor },
            Stroke = LeaderBlue,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        CalloutLayer.Children.Add(line);

        // The stick markers move while the callout lines stay anchored to their centers.
        if (mask != DpadMask)
            AddHalo(control, anchor);
        var dot = AddDot(anchor, stickOffset is null ? 7 : 8, LeaderBlue);
        if (stickOffset is not null)
            dot.RenderTransform = stickOffset;
        if (mask != DpadMask)
            AddHotspot(label.Replace('\n', ' '), control, anchor, 26);

        var text = new TextBlock { TextAlignment = TextAlignment.Center, LineHeight = 16 };
        if (letterBrush is null)
            text.Text = label;
        else
        {
            text.Inlines.Add(new Run { Text = label[..1], FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = letterBrush });
            text.Inlines.Add(new Run { Text = label[1..] });
        }

        var button = new Button
        {
            Style = (Style)Resources["MapCallout"],
            Tag = control,
            Background = IdleBackground,
            BorderBrush = IdleBorder,
            Content = text
        };
        AutomationProperties.SetName(button, label.Replace('\n', ' '));
        button.Click += ControlClicked;
        Place(button, bounds);
        CalloutLayer.Children.Add(button);
        _callouts.Add(new(mask, button, line, dot));
    }

    private void AddDpadDirection(ControlId control, string label, Point point)
    {
        AddHalo(control, point, diameter: 24);
        AddHotspot(label, control, point, 22);
    }

    private void AddHalo(ControlId control, Point point, double diameter = 28)
    {
        var halo = AddDot(point, diameter, HaloFill);
        halo.Stroke = ActiveCyan;
        halo.StrokeThickness = 1.6;
        halo.Opacity = 0;
        _halos.Add((control, halo));
    }

    private void AddHotspot(string label, ControlId control, Point point, double size)
    {
        var button = new Button { Style = (Style)Resources["MapHotspot"], Tag = control };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAccessibilityView(button, AccessibilityView.Raw);
        button.Click += ControlClicked;
        Place(button, new(point.X - size / 2, point.Y - size / 2, size, size));
        CalloutLayer.Children.Add(button);
    }

    private Ellipse AddDot(Point point, double diameter, Brush fill)
    {
        var dot = new Ellipse { Fill = fill, IsHitTestVisible = false };
        Place(dot, new(point.X - diameter / 2, point.Y - diameter / 2, diameter, diameter));
        CalloutLayer.Children.Add(dot);
        return dot;
    }

    public void Select(string control)
    {
        _selected = Enum.TryParse<ControlId>(control, ignoreCase: true, out var id) ? (uint)id : 0;
        RefreshCallouts();
    }

    public void UpdateBindingHints(Func<ControlId, string> describe, Func<ControlId, bool>? isAssigned = null)
    {
        _assigned = 0;
        if (isAssigned is not null)
        {
            foreach (var control in ControlMap.All.Where(isAssigned))
                _assigned |= (uint)control;
        }

        foreach (var button in CalloutLayer.Children.OfType<Button>())
        {
            var control = (ControlId)button.Tag;
            string title = AutomationProperties.GetName(button);
            string hint = title == "D-Pad"
                ? $"Up  {describe(ControlId.DPadUp)}\nDown  {describe(ControlId.DPadDown)}\nLeft  {describe(ControlId.DPadLeft)}\nRight  {describe(ControlId.DPadRight)}"
                : $"{title} · {describe(control)}";
            ToolTipService.SetToolTip(button, hint);
            AutomationProperties.SetHelpText(button, hint);
        }
        RefreshCallouts();
    }

    public void ShowInput(ControllerState state, uint controls)
    {
        MoveStick(_leftStickOffset, state.LeftX, state.LeftY);
        MoveStick(_rightStickOffset, state.RightX, state.RightY);
        if (state.LeftTrigger != 0)
            controls |= (uint)ControlId.LeftTrigger;
        if (state.RightTrigger != 0)
            controls |= (uint)ControlId.RightTrigger;
        if (controls == _active)
            return;

        _active = controls;
        foreach (var (control, halo) in _halos)
            halo.Opacity = (_active & (uint)control) != 0 ? 1 : 0;
        RefreshCallouts();
    }

    private void RefreshCallouts()
    {
        foreach (var (mask, button, line, dot) in _callouts)
        {
            bool pressed = (_active & mask) != 0;
            bool selected = (_selected & mask) != 0;
            button.Background = pressed ? ActiveBackground : selected ? SelectedBackground : IdleBackground;
            button.BorderBrush = pressed ? ActiveCyan : selected ? LeaderBlue : IdleBorder;
            line.Stroke = pressed ? ActiveCyan : LeaderBlue;
            dot.Fill = pressed ? ActiveCyan : (_assigned & mask) != 0 ? LeaderBlue : UnassignedDot;
        }
    }

    private static void Place(FrameworkElement element, Rect bounds)
    {
        Canvas.SetLeft(element, bounds.Left);
        Canvas.SetTop(element, bounds.Top);
        element.Width = bounds.Width;
        element.Height = bounds.Height;
    }

    private static void MoveStick(TranslateTransform transform, short x, short y)
    {
        var (right, down) = PhysicalInputFeedback.StickOffset(x, y);
        transform.X = right;
        transform.Y = down;
    }

    private static SolidColorBrush BrushFrom(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));

    private void ControlClicked(object sender, RoutedEventArgs args) =>
        ControlSelected?.Invoke(this, ((ControlId)((Button)sender).Tag).ToString());

    private readonly record struct Callout(uint Mask, Button Button, Polyline Line, Ellipse Dot);

    private readonly record struct SchematicFrame(double Left, double Top, double Size)
    {
        public Point At(double x, double y) => new(Left + x * Size / 1000, Top + y * Size / 1000);
    }
}
