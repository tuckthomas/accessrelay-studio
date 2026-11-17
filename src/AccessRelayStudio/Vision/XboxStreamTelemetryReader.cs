using EliteScriptStudio.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace EliteScriptStudio.Vision;

/// <summary>Optional background observer of the selected window's visible statistics.
/// Does not create a second capture session or load the game's OCR models.</summary>
internal sealed class XboxStreamTelemetryReader
{
    internal string Status { get; private set; } = "Waiting for the Xbox statistics overlay.";

    internal async Task RunAsync(VisionSession session, StudioRuntime runtime, StreamTelemetrySettings settings, CancellationToken ct)
    {
        if (!settings.Enabled) { Status = "Stream statistics monitoring is off."; return; }
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) { Status = "Windows OCR language support is unavailable. Install an OCR language in Windows Settings to read stream statistics."; return; }
            long previousFrame = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(750, ct).ConfigureAwait(false);
                try
                {
                    using var cropTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cropTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var frame = await session.CaptureRegionOnceAsync(settings.Region, cropTimeout.Token).ConfigureAwait(false);
                    if (frame is null || frame.CaptureTicks <= previousFrame) continue;
                    previousFrame = frame.CaptureTicks;
                    if (frame.Width > OcrEngine.MaxImageDimension || frame.Height > OcrEngine.MaxImageDimension)
                    { Status = "Statistics region is too large for Windows OCR. Select a smaller overlay region."; continue; }
                    using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(frame.Pixels),
                        BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
                    var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                    string text = string.Join("\n", result.Lines.Select(l => l.Text));
                    var reading = StreamTelemetryParser.Parse(text, frame.CaptureTicks);
                    if (reading is null)
                    { Status = "No complete stream statistics found. Enable the Xbox overlay or adjust its region."; continue; }
                    ct.ThrowIfCancellationRequested();
                    runtime.ObserveVisionStreamTelemetry(reading);
                    Status = "Reading the Xbox statistics overlay with Windows OCR.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) { Status = "Stream statistics unavailable: " + error.Message; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { Status = "Stream statistics reader unavailable: " + error.Message; }
    }
}
