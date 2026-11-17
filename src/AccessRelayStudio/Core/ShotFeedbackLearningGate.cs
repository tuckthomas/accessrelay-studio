using System.Text.RegularExpressions;

namespace EliteScriptStudio.Core;

/// <summary>Never learn from an invalid action or mix alternating shot contexts into one correction.</summary>
internal sealed class ShotFeedbackLearningGate
{
    private string _context = "";
    private int _consecutive;
    private string _lastRun = "";
    private long _lastReceipt;
    internal void Reset() { _context = _lastRun = ""; _consecutive = 0; _lastReceipt = 0; }
    internal static string Context(OcrFeedbackRead read)
    {
        var feet = Regex.Match(read.Distance ?? "", @"^\d+");
        string distance = int.TryParse(feet.Value, out int ft) && ft is >= 1 and <= 94 ? (ft / 5 * 5).ToString() + "–" + (ft / 5 * 5 + 4) + " ft" : "distance unknown";
        return string.Join(" / ", read.ShotType, distance, read.Coverage ?? "coverage unknown");
    }
    internal string? Check(VisionActionReceipt receipt, OcrFeedbackRead read, string profile)
    {
        if (receipt.RunId == _lastRun && receipt.Id <= _lastReceipt)
            return "Duplicate or superseded feedback cannot change timing.";
        _lastRun = receipt.RunId; _lastReceipt = receipt.Id;
        if (receipt.Outcome != VisionActionOutcome.Completed || !receipt.EligibleForFeedback)
        { _consecutive = 0; return "Feedback logged only: action was cancelled or local output timing was not validated."; }
        if (read.ShotType == "unrecognized") { _consecutive = 0; return "Feedback was not recognized reliably; timing unchanged."; }
        if (receipt.StreamTelemetry?.CanAdapt != true)
        { _consecutive = 0; return receipt.StreamTelemetry?.Message ?? "No stream-health evidence for this shot; automatic tuning is paused."; }
        if (string.IsNullOrWhiteSpace(read.Distance) || string.IsNullOrWhiteSpace(read.Coverage))
        { _consecutive = 0; return "Shot distance or coverage was unreadable; feedback logged without changing timing."; }
        string context = profile + "/" + Context(read);
        if (_context.Length == 0) _context = context;
        if (context != _context)
        {
            _consecutive = 0;
            return "This shot has a different distance or coverage. Timing is unchanged; use a separate saved profile for that context.";
        }
        _consecutive++;
        return _consecutive < 2 ? "Confirming this shot context with another reliable result before changing timing." : null;
    }
}
