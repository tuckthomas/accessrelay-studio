namespace EliteScriptStudio.Core;

/// <summary>Qualify a HUD result against the controller action that produced it.</summary>
internal sealed class ShotFeedbackLearningGate
{
    private string _lastRun = "";
    private long _lastReceipt;
    internal void Reset() { _lastRun = ""; _lastReceipt = 0; }
    internal string? Check(VisionActionReceipt receipt, OcrFeedbackRead read)
    {
        if (read.ReceiptId != receipt.Id || read.RunId != receipt.RunId)
            return "The HUD result belongs to a different shot.";
        if (receipt.RunId == _lastRun && receipt.Id <= _lastReceipt)
            return "Duplicate or superseded feedback cannot change timing.";
        _lastRun = receipt.RunId; _lastReceipt = receipt.Id;
        if (read.Disposition is not null) return read.Disposition;
        if (receipt.Outcome != VisionActionOutcome.Completed || !receipt.EligibleForFeedback)
            return "Feedback logged only: action was cancelled or local output timing was not validated.";
        if (read.ShotType == "unrecognized") return "Feedback was not recognized reliably; timing unchanged.";
        if (receipt.StreamTelemetry?.CanAdapt != true)
            return receipt.StreamTelemetry?.Message ?? "No stream-health evidence for this shot; automatic tuning is paused.";
        return null;
    }
}
