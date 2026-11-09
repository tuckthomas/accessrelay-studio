using EliteScriptStudio.Audio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Globalization.NumberFormatting;

namespace EliteScriptStudio;

public sealed partial class VoiceTrimEditor : UserControl, IDisposable
{
    private const long MinMs = 1_000;
    private const long MaxMs = 30_000;
    private const long MaxAuditionMs = VoiceProfiles.MaxAuditionSeconds * 1_000L;
    private const long DefaultSelectionMs = 15_000;
    private const double SeekLaneHeight = 24;
    private const double TrimLaneHeight = 36;
    private readonly DispatcherQueueTimer _previewTimer;
    private VoiceWaveform? _waveform;
    private VoiceTrimPreview? _preview;
    private CancellationTokenSource? _startingPreview;
    private TimeSpan _sourceOffset;
    private long _durationMs;
    private long _startMs;
    private long _endMs;
    private long _playheadMs;
    private long _viewStartMs;
    private long _viewSpanMs;
    private bool _updating;
    private bool _updatingView;
    private bool _playheadFocused;
    private bool _resumeAfterSeek;
    private bool _disposed;
    private uint? _pointerId;
    private DragPart _dragPart;
    private long _grabOffsetMs;

    public VoiceTrimEditor()
    {
        InitializeComponent();
        StartSeconds.NumberFormatter = new DecimalFormatter { FractionDigits = 3, IntegerDigits = 1 };
        EndSeconds.NumberFormatter = new DecimalFormatter { FractionDigits = 3, IntegerDigits = 1 };
        PlayheadSeconds.NumberFormatter = new DecimalFormatter { FractionDigits = 3, IntegerDigits = 1 };
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(33);
        _previewTimer.Tick += PreviewTick;
        Unloaded += (_, _) => ReleaseDrag();
    }

    public event EventHandler? SelectionChanged;

    public TimeSpan Start => TimeSpan.FromMilliseconds(_startMs);
    public TimeSpan End => TimeSpan.FromMilliseconds(_endMs);
    private bool CanEditSelection => _waveform is not null && _durationMs >= MinMs;
    internal bool HasAuditionableSelection => CanEditSelection && _startMs >= 0 && _endMs <= _durationMs
        && _endMs - _startMs is >= MinMs and <= MaxAuditionMs;
    public bool HasValidSelection => HasAuditionableSelection && _endMs - _startMs <= MaxMs;

    internal void SetPreviewSource(string path, TimeSpan sourceOffset = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PausePreview(syncPosition: true);
        _preview?.Dispose();
        _preview = new VoiceTrimPreview(path);
        _sourceOffset = sourceOffset;
        UpdateTransport();
    }

    internal void PausePlayback()
    {
        if (!_disposed)
            PausePreview(syncPosition: true);
    }

    internal void SetWaveform(VoiceWaveform waveform)
    {
        _waveform = waveform;
        _durationMs = (long)waveform.Duration.TotalMilliseconds;
        if (_durationMs < MinMs)
        {
            ShowError("The audio track must be at least one second long.");
            return;
        }

        WaveLoading.IsActive = false;
        WaveLoading.Visibility = Visibility.Collapsed;
        Status.Text = "Drag Start or End to trim that edge. Drag the shaded waveform to move the whole selection. The top ruler moves the playhead.";
        Editor.Visibility = Visibility.Visible;
        _updating = true;
        StartSeconds.Minimum = EndSeconds.Minimum = 0;
        StartSeconds.Maximum = EndSeconds.Maximum = _durationMs / 1000.0;
        _updating = false;
        _viewStartMs = 0;
        _viewSpanMs = _durationMs;
        SetSelection(0, Math.Min(DefaultSelectionMs, _durationMs));
        SetView(0, _durationMs);
    }

    public void ShowError(string message)
    {
        PausePreview(syncPosition: false);
        _waveform = null;
        _durationMs = _startMs = _endMs = 0;
        _viewStartMs = _viewSpanMs = 0;
        WaveLoading.IsActive = false;
        WaveLoading.Visibility = Visibility.Collapsed;
        Status.Text = message;
        Editor.Visibility = Visibility.Collapsed;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSelection(long start, long end)
    {
        bool changed = _startMs != start || _endMs != end;
        if (changed && PreviewActive)
            PausePreview(syncPosition: true);
        _startMs = start;
        _endMs = end;
        _updating = true;
        StartSeconds.Value = start / 1000.0;
        EndSeconds.Value = end / 1000.0;
        PlayheadSeconds.Minimum = 0;
        PlayheadSeconds.Maximum = end / 1000.0;
        PlayheadSeconds.Minimum = start / 1000.0;
        _updating = false;
        SetPlayhead(_playheadMs);
        long length = end - start;
        string selectionText = $"Selected length {length / 1000.0:0.000} seconds.";
        if (length is < MinMs or > MaxMs)
            selectionText += " Voice profiles need 1–30 seconds.";
        if (length < MinMs)
            selectionText += " Select at least one second to play or clean up.";
        else if (length > MaxAuditionMs)
            selectionText += " Playback and cleanup support up to five minutes.";
        if (SelectionLength.Text != selectionText)
            SelectionLength.Text = selectionText;
        DrawSelection();
        UpdateZoomButton();
        UpdateTransport();
        if (changed)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartEdited(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || !CanEditSelection || !double.IsFinite(args.NewValue))
            return;

        long start = Math.Clamp((long)Math.Round(args.NewValue * 1000), 0, _endMs);
        SetSelection(start, _endMs);
        RevealSelection();
    }

    private void EndEdited(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || !CanEditSelection || !double.IsFinite(args.NewValue))
            return;

        long end = Math.Clamp((long)Math.Round(args.NewValue * 1000), _startMs, _durationMs);
        SetSelection(_startMs, end);
        RevealSelection();
    }

    private bool PreviewActive => _startingPreview is { IsCancellationRequested: false } || _preview?.IsPlaying == true;

    private void PlayPauseClicked(object sender, RoutedEventArgs args)
    {
        if (PreviewActive)
            PausePreview(syncPosition: true);
        else
            _ = PlayPreviewAsync();
    }

    private async Task PlayPreviewAsync()
    {
        if (_disposed || !HasAuditionableSelection || PreviewActive || _preview is not { } preview)
            return;

        if (_playheadMs == _endMs)
            SetPlayhead(_startMs);
        SetPreviewStatus(null);
        using var cancellation = new CancellationTokenSource();
        _startingPreview = cancellation;
        UpdateTransport();
        try
        {
            await preview.PlayAsync(Start - _sourceOffset, End - _sourceOffset,
                TimeSpan.FromMilliseconds(_playheadMs) - _sourceOffset, cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                SyncPlayhead(preview);
                if (preview.IsPlaying)
                    _previewTimer.Start();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && !cancellation.IsCancellationRequested)
                SetPreviewStatus(error.Message);
        }
        finally
        {
            if (ReferenceEquals(_startingPreview, cancellation))
                _startingPreview = null;
            if (!_disposed)
                UpdateTransport();
        }
    }

    private void PausePreview(bool syncPosition)
    {
        bool wasPlaying = _preview?.IsPlaying == true;
        _startingPreview?.Cancel();
        _previewTimer.Stop();
        if (_preview is { } preview)
        {
            preview.Pause();
            if (syncPosition && wasPlaying)
                SyncPlayhead(preview);
        }
        UpdateTransport();
    }

    private void PreviewTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _preview is not { } preview)
            return;
        SyncPlayhead(preview);
        if (!preview.IsPlaying)
        {
            _previewTimer.Stop();
            SetPreviewStatus(preview.ErrorMessage);
            UpdateTransport();
        }
    }

    private void SyncPlayhead(VoiceTrimPreview preview) =>
        SetPlayhead((long)Math.Round((preview.Position + _sourceOffset).TotalMilliseconds), updateField: !_playheadFocused);

    private void SetPlayhead(long milliseconds, bool updateField = true)
    {
        _playheadMs = Math.Clamp(milliseconds, _startMs, _endMs);
        if (updateField)
        {
            _updating = true;
            PlayheadSeconds.Value = _playheadMs / 1000.0;
            _updating = false;
        }
        DrawPlayhead();
    }

    private void PlayheadEdited(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || !CanEditSelection || !double.IsFinite(args.NewValue))
            return;

        bool resume = _preview?.IsPlaying == true;
        PausePreview(syncPosition: false);
        SetPlayhead((long)Math.Round(args.NewValue * 1000));
        if (resume && _playheadMs < _endMs)
            _ = PlayPreviewAsync();
    }

    private void PlayheadGotFocus(object sender, RoutedEventArgs args) => _playheadFocused = true;

    private void PlayheadLostFocus(object sender, RoutedEventArgs args) => _playheadFocused = false;

    private void UpdateTransport()
    {
        PlayPauseButton.IsEnabled = !_disposed && HasAuditionableSelection && _preview is not null;
        PlayheadSeconds.IsEnabled = !_disposed && CanEditSelection && _endMs > _startMs;
        PlayPauseButton.Content = PreviewActive ? "Pause selection" : "Play selection";
    }

    private void SetPreviewStatus(string? message)
    {
        PreviewStatus.Text = message ?? string.Empty;
        PreviewStatus.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseDrag();
        _previewTimer.Stop();
        _startingPreview?.Cancel();
        _preview?.Dispose();
        _preview = null;
    }

    private void WaveSizeChanged(object sender, SizeChangedEventArgs args)
    {
        DrawWaveform();
        DrawSelection();
        DrawPlayhead();
    }

    private bool Zoomed => _viewSpanMs < _durationMs;

    private long ZoomSpan() => Math.Min(_durationMs, Math.Max(8_000, (_endMs - _startMs) * 3 / 2 + 2_000));

    private void UpdateZoomButton()
    {
        ZoomButton.IsEnabled = Zoomed || ZoomSpan() < _durationMs;
        ZoomButton.Content = Zoomed ? "Full track" : "Fit selection";
        ZoomInButton.IsEnabled = _viewSpanMs > MinMs;
        ZoomOutButton.IsEnabled = Zoomed;
    }

    private void ZoomClicked(object sender, RoutedEventArgs args)
    {
        if (!CanEditSelection || _pointerId is not null)
            return;
        if (Zoomed)
        {
            SetView(0, _durationMs);
            return;
        }
        long span = ZoomSpan();
        SetView(_startMs - (span - (_endMs - _startMs)) / 2, span);
    }

    private void ZoomInClicked(object sender, RoutedEventArgs args) => ZoomView(Math.Max(MinMs, _viewSpanMs / 2));

    private void ZoomOutClicked(object sender, RoutedEventArgs args) => ZoomView(Math.Min(_durationMs, _viewSpanMs * 2));

    private void ZoomView(long span)
    {
        if (!CanEditSelection || _pointerId is not null)
            return;
        long center = _startMs + (_endMs - _startMs) / 2;
        if (!InView(center))
            center = _viewStartMs + _viewSpanMs / 2;
        SetView(center - span / 2, span);
    }

    private void ViewPositionChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_updatingView && Zoomed && _pointerId is null)
            SetView((long)Math.Round(args.NewValue * 1000), _viewSpanMs);
    }

    private void RevealSelection()
    {
        if (!Zoomed)
            return;
        long span = Math.Max(_viewSpanMs, _endMs - _startMs);
        long start = Math.Clamp(_viewStartMs, _endMs - span, _startMs);
        if (start != _viewStartMs || span != _viewSpanMs)
            SetView(start, span);
    }

    private void SetView(long start, long span)
    {
        _viewSpanMs = span;
        _viewStartMs = Math.Clamp(start, 0, _durationMs - span);
        _updatingView = true;
        ViewPosition.Maximum = (_durationMs - span) / 1000.0;
        ViewPosition.ViewportSize = span / 1000.0;
        ViewPosition.SmallChange = Math.Max(0.1, span / 20_000.0);
        ViewPosition.LargeChange = span / 1000.0;
        ViewPosition.Value = _viewStartMs / 1000.0;
        WaveScroll.Visibility = Zoomed ? Visibility.Visible : Visibility.Collapsed;
        _updatingView = false;
        ViewStart.Text = ClockText(_viewStartMs, Zoomed);
        ViewEnd.Text = ClockText(_viewStartMs + span, Zoomed);
        UpdateZoomButton();
        DrawWaveform();
        DrawSelection();
        DrawPlayhead();
    }

    private double StartLaneTop => WaveSurface.ActualHeight - 2 * TrimLaneHeight;

    private void DrawWaveform()
    {
        if (_waveform is not { Bins.Count: > 0 } waveform || _viewSpanMs <= 0 || WaveSurface.ActualWidth <= 0)
            return;

        double width = WaveSurface.ActualWidth;
        double height = WaveSurface.ActualHeight;
        double amplitude = Math.Max(0, (StartLaneTop - SeekLaneHeight) / 2 - 5);
        double center = (StartLaneTop + SeekLaneHeight) / 2;
        var geometry = new PathGeometry();
        var bins = waveform.Bins;
        int first = Math.Clamp((int)Math.Floor((double)_viewStartMs / _durationMs * bins.Count), 0, bins.Count - 1);
        int limit = Math.Clamp((int)Math.Ceiling((double)(_viewStartMs + _viewSpanMs) / _durationMs * bins.Count), first + 1, bins.Count);
        int visibleBins = limit - first;
        int columns = Math.Min(visibleBins, (int)Math.Ceiling(width));
        for (int index = 0; index < columns; index++)
        {
            int from = first + index * visibleBins / columns;
            int to = first + (index + 1) * visibleBins / columns;
            float min = 1;
            float max = -1;
            for (int bin = from; bin < to; bin++)
            {
                min = Math.Min(min, bins[bin].Minimum);
                max = Math.Max(max, bins[bin].Maximum);
            }
            double x = Math.Clamp(Position((from + to) * 0.5 / bins.Count * _durationMs), 0.5, Math.Max(0.5, width - 0.5));
            double top = center - Math.Clamp((double)max, -1, 1) * amplitude;
            double bottom = center - Math.Clamp((double)min, -1, 1) * amplitude;
            var line = new PathFigure { StartPoint = new Point(x, top), IsFilled = false };
            line.Segments.Add(new LineSegment { Point = new Point(x, Math.Max(top + 1, bottom)) });
            geometry.Figures.Add(line);
        }
        Wave.Data = geometry;
        SeekDivider.Width = StartDivider.Width = EndDivider.Width = width;
        Canvas.SetTop(SeekDivider, SeekLaneHeight);
        Canvas.SetTop(StartDivider, StartLaneTop);
        Canvas.SetTop(EndDivider, StartLaneTop + TrimLaneHeight);
        WaveSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
    }

    private void DrawSelection()
    {
        if (_viewSpanMs <= 0 || WaveSurface.ActualWidth <= 0)
            return;

        double width = WaveSurface.ActualWidth;
        double start = Math.Clamp(Position(_startMs), 0, width);
        double end = Math.Clamp(Position(_endMs), 0, width);
        Region.Height = Math.Max(0, StartLaneTop - SeekLaneHeight);
        Region.Width = end - start;
        Canvas.SetLeft(Region, start);
        Canvas.SetTop(Region, SeekLaneHeight);
        DrawTrimHandle(StartHandle, StartStem, _startMs, StartLaneTop);
        DrawTrimHandle(EndHandle, EndStem, _endMs, StartLaneTop + TrimLaneHeight);
        bool distinctStems = Position(_endMs) - Position(_startMs) >= StartStem.Width;
        StartStem.IsHitTestVisible = EndStem.IsHitTestVisible = distinctStems;
    }

    private void DrawTrimHandle(VoiceTrimGrip handle, VoiceTrimGrip stem, long time, double laneTop)
    {
        bool visible = InView(time);
        handle.Visibility = stem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        double x = Position(time);
        double width = WaveSurface.ActualWidth;
        double top = laneTop + (TrimLaneHeight - handle.Height) / 2;
        stem.Height = top - SeekLaneHeight;
        Canvas.SetLeft(stem, x - stem.Width / 2);
        Canvas.SetTop(stem, SeekLaneHeight);
        Canvas.SetLeft(handle, Math.Clamp(x - handle.Width / 2, 0, Math.Max(0, width - handle.Width)));
        Canvas.SetTop(handle, top);
    }

    private void DrawPlayhead()
    {
        double width = WaveSurface.ActualWidth;
        if (_viewSpanMs <= 0 || width <= 0)
            return;
        bool visible = InView(_playheadMs);
        PlayheadStem.Visibility = PlayheadMarker.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;
        double x = Position(_playheadMs);
        PlayheadStem.Height = StartLaneTop;
        Canvas.SetLeft(PlayheadStem, Math.Clamp(x - 1, 0, Math.Max(0, width - 2)));
        Canvas.SetLeft(PlayheadMarker, Math.Clamp(x - 8, 0, Math.Max(0, width - 16)));
    }

    private void StartPressed(object sender, PointerRoutedEventArgs args) => BeginDrag(DragPart.Start, args);

    private void EndPressed(object sender, PointerRoutedEventArgs args) => BeginDrag(DragPart.End, args);

    private void WavePressed(object sender, PointerRoutedEventArgs args)
    {
        double y = args.GetCurrentPoint(WaveSurface).Position.Y;
        if (y < StartLaneTop)
            BeginDrag(y <= SeekLaneHeight ? DragPart.Playhead : DragPart.Region, args);
    }

    private void BeginDrag(DragPart part, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        if (!CanEditSelection || _pointerId is not null || WaveSurface.ActualWidth <= 0)
            return;

        var point = args.GetCurrentPoint(WaveSurface);
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
            return;

        // Keep a drag inside the waveform from becoming a gesture on the outer page ScrollViewer.
        WaveSurface.CancelDirectManipulations();
        if (!WaveSurface.CapturePointer(args.Pointer))
            return;
        _pointerId = args.Pointer.PointerId;
        _dragPart = part;
        if (part is DragPart.Start or DragPart.End && args.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            WaveSurface.SetResizeCursor(true);

        double x = point.Position.X;
        long time = TimeAt(x);
        switch (part)
        {
            case DragPart.Start:
                _grabOffsetMs = time - _startMs;
                break;
            case DragPart.End:
                _grabOffsetMs = time - _endMs;
                break;
            case DragPart.Region:
                bool jump = time < _startMs || time > _endMs;
                _grabOffsetMs = jump ? (_endMs - _startMs) / 2 : time - _startMs;
                if (jump)
                    MoveSelection(x);
                break;
            case DragPart.Playhead:
                _resumeAfterSeek = _preview?.IsPlaying == true;
                PausePreview(syncPosition: false);
                MoveSelection(x);
                break;
        }
    }

    private void WaveMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;
        MoveSelection(args.GetCurrentPoint(WaveSurface).Position.X);
        args.Handled = true;
    }

    private void WaveReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;
        MoveSelection(args.GetCurrentPoint(WaveSurface).Position.X);
        bool resume = _resumeAfterSeek;
        ResetDrag();
        WaveSurface.ReleasePointerCapture(args.Pointer);
        if (resume && _playheadMs < _endMs)
            _ = PlayPreviewAsync();
        args.Handled = true;
    }

    private void WaveCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId)
            ResetDrag();
    }

    private void ResetDrag()
    {
        _pointerId = null;
        _resumeAfterSeek = false;
        WaveSurface.SetResizeCursor(false);
    }

    private void ReleaseDrag()
    {
        if (_pointerId is null)
            return;
        ResetDrag();
        WaveSurface.ReleasePointerCaptures();
    }

    private void MoveSelection(double x)
    {
        long time = TimeAt(x);
        switch (_dragPart)
        {
            case DragPart.Start:
                SetSelection(Math.Min(TrimTime(time), _endMs), _endMs);
                break;
            case DragPart.End:
                SetSelection(_startMs, Math.Max(_startMs, TrimTime(time)));
                break;
            case DragPart.Region:
                long duration = _endMs - _startMs;
                long start = Math.Clamp(time - _grabOffsetMs, 0, _durationMs - duration);
                SetSelection(start, start + duration);
                break;
            case DragPart.Playhead:
                SetPlayhead(time);
                break;
        }
    }

    private long TrimTime(long time) => Math.Clamp(time - _grabOffsetMs, _viewStartMs, _viewStartMs + _viewSpanMs);

    private bool InView(long milliseconds) => milliseconds >= _viewStartMs && milliseconds <= _viewStartMs + _viewSpanMs;

    private double Position(double milliseconds) => (milliseconds - _viewStartMs) / _viewSpanMs * WaveSurface.ActualWidth;

    private long TimeAt(double x) => _viewStartMs + (long)Math.Round(x / WaveSurface.ActualWidth * _viewSpanMs);

    private static string ClockText(long milliseconds, bool showTenths)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        string clock = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        return showTenths ? $"{clock}.{time.Milliseconds / 100}" : clock;
    }

    private enum DragPart { Start, End, Region, Playhead }
}

public sealed partial class VoiceTrimGrip : Grid
{
    public VoiceTrimGrip() => Loaded += (_, _) => ProtectedCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}

public sealed partial class VoiceTrimCanvas : Canvas
{
    private InputSystemCursor? _resizeCursor;

    internal void SetResizeCursor(bool resizing) => ProtectedCursor = resizing
        ? (_resizeCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast))
        : null;
}
