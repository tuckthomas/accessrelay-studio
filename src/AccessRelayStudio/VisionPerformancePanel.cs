using EliteScriptStudio.Vision;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EliteScriptStudio;

/// <summary>Low-rate presentation only. Aggregation and frame-path storage belong to VisionSession.</summary>
internal sealed class VisionPerformancePanel : UserControl
{
    private sealed record RunChoice(VisionPerformanceRun Run)
    {
        public string Name => $"{Run.Label} · {Run.Snapshot.ExecutionProvider} · {Run.CreatedAtUtc.ToLocalTime():g}";
    }

    private readonly TextBlock _context = Text("Start observation to collect local timing samples.");
    private readonly TextBlock _activity = Text("No samples yet.");
    private readonly TextBlock _budget = Text("16.67 ms frame-to-decision reference: N/A · no model decisions yet.");
    private readonly TextBlock _allocations = Text("Window allocation / collection counts: N/A.");
    private readonly TextBlock _comparison = Text("Save or load a performance run to choose a baseline.");
    private readonly TextBox _runLabel = new() { Header = "Performance run label", PlaceholderText = "For example: CPU, same scene", MaxLength = 80, FontSize = 14 };
    private readonly ComboBox _baseline = new() { Header = "Saved comparison baseline", DisplayMemberPath = nameof(RunChoice.Name),
        HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 14 };
    private readonly Button _save = new() { Content = "Save performance run", FontSize = 14 };
    private readonly Button _load = new() { Content = "Load performance run…", FontSize = 14 };
    private readonly List<RunChoice> _runs = [];
    private readonly TextBlock[][] _cells = new TextBlock[6][];
    private readonly string[][] _cellNames = new string[6][];
    private VisionPerformanceSnapshot? _snapshot;
    private bool _selecting, _busy;

    internal string RunLabel => _runLabel.Text.Trim();

    internal VisionPerformancePanel(Action save, Action load)
    {
        var content = new StackPanel { Spacing = 10 };
        Content = content;
        content.Children.Add(new TextBlock { Text = "Live vision performance", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Text("Local PC timing. Statistics use bounded recent sample windows; they do not measure original Xbox frame age or when the Xbox applies input. N/A means no matching samples exist."));
        content.Children.Add(_context);
        content.Children.Add(_activity);
        content.Children.Add(BuildTable());
        content.Children.Add(_budget);
        content.Children.Add(_allocations);
        content.Children.Add(Text("Helper handoff measures the output queue through the local pipe flush. It is a latest estimate, with no percentile sample history."));
        content.Children.Add(_runLabel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _save.Click += (_, _) => save();
        _load.Click += (_, _) => load();
        actions.Children.Add(_save); actions.Children.Add(_load);
        content.Children.Add(actions);
        content.Children.Add(_baseline);
        content.Children.Add(_comparison);
        content.Children.Add(Text("Live run comparisons can use different gameplay scenes. They show observed differences and are not controlled CPU/GPU benchmarks. Matching configuration and enough model decisions are required before reporting deltas."));
        AutomationProperties.SetName(_runLabel, "Label for the saved vision performance run");
        AutomationProperties.SetName(_baseline, "Saved vision performance comparison baseline");
        _baseline.SelectionChanged += (_, _) => { if (!_selecting) RefreshComparison(); };
        _save.IsEnabled = false;
    }

    private FrameworkElement BuildTable()
    {
        var table = new Grid { ColumnSpacing = 12, RowSpacing = 8, MinWidth = 790 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
        for (int column = 1; column < 7; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = column == 1 ? 60 : 75 });
        string[] headers = ["Measurement (ms)", "Samples", "Latest", "p50", "p95", "p99", "Maximum"];
        table.RowDefinitions.Add(new RowDefinition());
        for (int column = 0; column < headers.Length; column++)
            AddCell(table, Text(headers[column], bold: true), 0, column);
        string[] names = ["Frame age at processing start", "Frame to model decision", "Frame processing (all frames)",
            "Player preprocessing", "Inference including transfers", "Input helper pipe handoff (latest only)"];
        for (int row = 0; row < names.Length; row++)
        {
            table.RowDefinitions.Add(new RowDefinition());
            AddCell(table, Text(names[row]), row + 1, 0);
            _cells[row] = new TextBlock[6];
            _cellNames[row] = new string[6];
            for (int column = 0; column < 6; column++)
            {
                var cell = Text("N/A");
                cell.HorizontalAlignment = HorizontalAlignment.Right;
                cell.FontFamily = new FontFamily("Consolas");
                _cellNames[row][column] = names[row] + " · " + headers[column + 1];
                AutomationProperties.SetName(cell, _cellNames[row][column] + ": N/A");
                _cells[row][column] = cell;
                AddCell(table, cell, row + 1, column + 1);
            }
        }
        AutomationProperties.SetName(table, "Vision timing statistics in milliseconds");
        return new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static void AddCell(Grid table, TextBlock cell, int row, int column)
    { Grid.SetRow(cell, row); Grid.SetColumn(cell, column); table.Children.Add(cell); }

    private static TextBlock Text(string value, bool bold = false) => new() { Text = value, FontSize = 14,
        TextWrapping = TextWrapping.Wrap, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };

    internal void Update(VisionPerformanceSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _save.IsEnabled = !_busy && (snapshot?.TotalFrames ?? 0) > 0;
        if (snapshot is null)
        {
            _context.Text = "Start observation to collect local timing samples.";
            return;
        }
        string model = string.IsNullOrWhiteSpace(snapshot.ModelManifestHash) ? "no loaded model" : "model " + snapshot.ModelManifestHash[..Math.Min(12, snapshot.ModelManifestHash.Length)];
        _context.Text = $"Backend: {snapshot.ExecutionProvider} · {model} · capture {snapshot.CaptureWidth}×{snapshot.CaptureHeight}\n" +
            $"Camera: {(string.IsNullOrWhiteSpace(snapshot.CameraView) ? "unspecified" : snapshot.CameraView)} · elapsed {snapshot.ElapsedSeconds:F1} s";
        _activity.Text = $"Processed {snapshot.TotalFrames:N0} frames · {snapshot.ProcessingFps:F1} frames/s · {snapshot.TotalDecisions:N0} model decisions · {snapshot.TotalInferences:N0} inferences\n" +
            $"Replaced capture frames: {snapshot.ReplacedCaptureFrames:N0} · automated releases: {snapshot.AutomatedReleases:N0} · missed deadlines: {snapshot.MissedDeadlines:N0}\n" +
            (snapshot.Armed ? "Automatic release is armed." : "Automatic release is disarmed.") +
            (snapshot.RecordingTraining ? "\nThe recent window includes training capture; these samples cannot be used for run comparison." : "");
        SetStatistics(0, snapshot.FrameAge); SetStatistics(1, snapshot.FrameToDecision);
        SetStatistics(2, snapshot.Processing); SetStatistics(3, snapshot.Preprocessing); SetStatistics(4, snapshot.InferenceWithTransfers);
        _budget.Text = snapshot.FrameToDecision.Samples > 0
            ? $"Frame-to-decision over the {snapshot.DecisionBudgetMs:F2} ms reference: {snapshot.DecisionBudgetExceededCount:N0} / {snapshot.FrameToDecision.Samples:N0} ({100d * snapshot.DecisionBudgetExceededCount / snapshot.FrameToDecision.Samples:F1}%) in the recent decision window. This reference is not a release-deadline guarantee."
            : $"Frame-to-decision over the {snapshot.DecisionBudgetMs:F2} ms reference: N/A · no model decisions yet. This reference is not a release-deadline guarantee.";
        _allocations.Text = snapshot.Processing.Samples > 0
            ? $"Recent frame window: {snapshot.WindowAllocatedBytes / 1024d:F1} KiB allocated on the vision worker · observed GC collections Gen 0 / 1 / 2: {snapshot.WindowGen0Collections} / {snapshot.WindowGen1Collections} / {snapshot.WindowGen2Collections}. Collection counts are not pause durations."
            : "Window allocation / collection counts: N/A · no processed frames yet.";
        SetCellText(5, 0, "N/A");
        SetCellText(5, 1, Numeric(snapshot.LatestHelperHandoffMs));
        for (int column = 2; column < 6; column++) SetCellText(5, column, "N/A");
        RefreshComparison();
    }

    internal void SetBusy(bool busy)
    {
        _busy = busy;
        _save.IsEnabled = !busy && (_snapshot?.TotalFrames ?? 0) > 0;
        _load.IsEnabled = _runLabel.IsEnabled = _baseline.IsEnabled = !busy;
    }

    private void SetStatistics(int row, VisionLatencyStatistics statistics)
    {
        SetCellText(row, 0, statistics.Samples > 0 ? statistics.Samples.ToString("N0") : "N/A");
        double?[] values = [statistics.LatestMs, statistics.P50Ms, statistics.P95Ms, statistics.P99Ms, statistics.MaximumMs];
        for (int column = 0; column < values.Length; column++)
            SetCellText(row, column + 1, statistics.Samples > 0 ? Numeric(values[column]) : "N/A");
    }

    private void SetCellText(int row, int column, string value)
    {
        _cells[row][column].Text = value;
        AutomationProperties.SetName(_cells[row][column], _cellNames[row][column] + ": " + value);
    }

    internal void RememberRun(VisionPerformanceRun run, bool selectAsBaseline)
    {
        var selected = _baseline.SelectedItem as RunChoice;
        _runs.RemoveAll(choice => choice.Run.RunId == run.RunId);
        var added = new RunChoice(run);
        _runs.Insert(0, added);
        if (_runs.Count > 8) _runs.RemoveRange(8, _runs.Count - 8);
        _selecting = true;
        try
        {
            _baseline.ItemsSource = _runs.ToArray();
            _baseline.SelectedItem = selectAsBaseline || selected is null ? added : _runs.Contains(selected) ? selected : added;
        }
        finally { _selecting = false; }
        RefreshComparison();
    }

    private void RefreshComparison()
    {
        if (_baseline.SelectedItem is not RunChoice baseline)
        { _comparison.Text = "Save or load a performance run to choose a baseline."; return; }
        if (_snapshot is null)
        { _comparison.Text = "Baseline loaded. Start observation with a matching configuration to compare it."; return; }
        var current = new VisionPerformanceRun { Label = "Current observation", Snapshot = _snapshot };
        var comparison = VisionPerformanceRuns.Compare(baseline.Run, current);
        _comparison.Text = $"Baseline: {baseline.Run.Label} ({baseline.Run.Snapshot.ExecutionProvider}) → current ({_snapshot.ExecutionProvider})\n" + comparison.Message;
        if (comparison.Comparable)
            _comparison.Text += $"\nFrame-to-decision change · p95 {Delta(comparison.FrameToDecisionP95ChangeMs)} · p99 {Delta(comparison.FrameToDecisionP99ChangeMs)}. Negative means the current run is faster.";
    }

    private static string Numeric(double? value) => value is { } number && double.IsFinite(number) ? number.ToString("F2") : "N/A";
    private static string Delta(double? value) => value is { } number && double.IsFinite(number) ? number.ToString("+0.00;-0.00;0.00") + " ms" : "N/A";
}
