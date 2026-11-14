using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteScriptStudio.Desktop;

if (args is ["--read-calibration-clip", var calibrationDirectory])
{
    using var dataset = new DesktopVisionDataset(calibrationDirectory);
    if (dataset.Regions.Length != 2 || dataset.ReadFrame("scene", 0).Bytes[54] != 11)
        throw new InvalidDataException("Calibration scene did not round-trip without fabricated HUD regions.");
    Console.WriteLine("SUCCESS: raw full-scene calibration clip is reviewable with no meter/feedback region prerequisite.");
    return;
}

if (args is ["--read-automatic-clip", var clipDirectory])
{
    using var dataset = new DesktopVisionDataset(clipDirectory);
    using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(dataset.Snapshot(), DesktopJson.Options));
    if (snapshot.RootElement.GetProperty("tracking").GetString() != "identity-pose-v1"
        || snapshot.RootElement.GetProperty("frames")[0].GetProperty("tracking").GetProperty("keypoints").GetArrayLength() != 17
        || dataset.ReadFrame("player", 0).Bytes[54] != 37) throw new InvalidDataException("Actual automatic recording did not round-trip through the desktop reader.");
    Console.WriteLine("SUCCESS: completed native v4 recording read through the desktop dataset reader with exact pixels, identity and pose.");
    return;
}

string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
string directory = Path.Combine(root, "AccessRelayDesktopEvidence-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await ResourceMonitorChecks.RunAsync(Path.Combine(directory, "preferences"));
    var source = new byte[3 * 2 * 4 + 8];
    for (int row = 0; row < 2; row++)
        for (int col = 0; col < 3; col++) { int offset = row * 16 + col * 4; source[offset] = (byte)col; source[offset + 1] = (byte)row; source[offset + 2] = 77; source[offset + 3] = 255; }
    var bmp = DesktopVisionBitmap.Encode(source, 3, 2, 16);
    Check(bmp.ContentType == "image/bmp" && bmp.Bytes.Length == 54 + 24, "BMP length/type");
    Check(bmp.Bytes[0] == 'B' && bmp.Bytes[1] == 'M' && BinaryPrimitives.ReadInt32LittleEndian(bmp.Bytes.AsSpan(22)) == -2, "Top-down BMP header");
    Check(bmp.Bytes[54] == 0 && bmp.Bytes[54 + 8] == 2 && bmp.Bytes[54 + 12 + 1] == 1, "Padded BGRA row handling");
    var big = DesktopVisionBitmap.Encode(new byte[2560 * 1440 * 4], 2560, 1440, 2560 * 4);
    Check(BinaryPrimitives.ReadInt32LittleEndian(big.Bytes.AsSpan(18)) == 960 && BinaryPrimitives.ReadInt32LittleEndian(big.Bytes.AsSpan(22)) == -540, "1440p bounded preview dimensions");
    Reject(() => DesktopVisionBitmap.Encode(new byte[4], 0, 1, 4), "Invalid dimensions");
    Reject(() => DesktopVisionBitmap.Encode(new byte[8], 4, 2, 16), "Short pixel buffer");
    Console.WriteLine("SUCCESS: binary top-down BMP, padded stride, pixels, invalid dimensions and bounded1440p preview.");

    JsonObject Metadata(bool enriched, bool meter = true, bool sceneFirst = false)
    {
        var regions = new List<(string Name, string File, int Width, int Height)> { ("player", "frames.bgra", 256, 256) };
        if (enriched) { regions.Add(("scene", "scene.bgra", sceneFirst ? 1280 : 640, sceneFirst ? 720 : 360)); if (meter) regions.Add(("meter", "meter.bgra", 256, 256)); regions.Add(("feedback", "feedback.bgra", 640, 192)); }
        foreach (var region in regions)
        {
            int size = region.Width * region.Height * 4;
            var pixels = new byte[size * 2]; Array.Fill(pixels, (byte)43, 0, size); Array.Fill(pixels, (byte)67, size, size);
            File.WriteAllBytes(Path.Combine(directory, region.File), pixels);
        }
        var frames = new JsonArray();
        for (int i = 0; i < 2; i++)
        {
            var frame = new JsonObject { ["captureTicks"] = 1000 + i * 100, ["sequence"] = i + 1, ["file"] = "frames.bgra", ["offsetBytes"] = i * 256 * 256 * 4, ["byteLength"] = 256 * 256 * 4 };
            if (enriched) frame["evidence"] = new JsonArray(regions.Skip(1).Select(region => (JsonNode)new JsonObject { ["name"] = region.Name, ["file"] = region.File, ["offsetBytes"] = i * region.Width * region.Height * 4, ["byteLength"] = region.Width * region.Height * 4 }).ToArray());
            frames.Add(frame);
        }
        var metadata = new JsonObject { ["schemaVersion"] = 1, ["sessionId"] = "synthetic-desktop-reader", ["complete"] = true, ["stopwatchFrequency"] = 1000, ["storedWidth"] = 256, ["storedHeight"] = 256, ["storedStride"] = 1024, ["pixelFormat"] = "BGRA8", ["frames"] = frames };
        if (enriched) metadata["captureEvidence"] = new JsonObject { ["version"] = sceneFirst ? 2 : 1, ["tracking"] = sceneFirst ? "unannotated-scene" : "fixed-region", ["purpose"] = meter ? "meterSupervisedTraining" : "meterOffValidation", ["streams"] = new JsonArray(regions.Select(region => (JsonNode)new JsonObject { ["name"] = region.Name, ["file"] = region.File, ["width"] = region.Width, ["height"] = region.Height, ["stride"] = region.Width * 4, ["pixelFormat"] = "BGRA8", ["crop"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 1, ["height"] = 1 } }).ToArray()) };
        return metadata;
    }
    void Write(JsonObject metadata) { File.WriteAllText(Path.Combine(directory, "session.json"), metadata.ToJsonString()); string labels = Path.Combine(directory, "labels.json"); if (File.Exists(labels)) File.Delete(labels); }
    foreach (var (enriched, meter, expectedCount, sceneFirst) in new[] { (false, false, 1, false), (true, true, 4, false), (true, false, 3, false), (true, false, 3, true) })
    {
        Write(Metadata(enriched, meter, sceneFirst)); using var dataset = new DesktopVisionDataset(directory);
        Check(dataset.Regions.Length == expectedCount && dataset.FrameCount == 2, "Supported stream counts");
        foreach (var region in dataset.Regions) { var image = dataset.ReadFrame(region.Name, 1); Check(image.Bytes[54] == 67, "Read correct region and frame offset"); }
        Reject(() => dataset.ReadFrame("../../outside", 0), "Unknown region"); Reject(() => dataset.ReadFrame("player", -1), "Negative frame");
        var labels = new DesktopVisionDataset.LabelDocument(1, "synthetic-desktop-reader", "validation", [new("shot-001", 1000, 1100, 1100, true, true, true)]);
        dataset.Save(labels);
        Check(File.Exists(Path.Combine(directory, "labels.json")), "Reviewed label saved");
        Reject(() => dataset.Save(labels with { Shots = [labels.Shots[0] with { Reviewed = false }] }), "Unreviewed label");
        Reject(() => dataset.Save(labels with { Shots = [labels.Shots[0] with { PlayerIdentityConfirmed = false }] }), "Unconfirmed player");
        Reject(() => dataset.Save(labels with { Shots = [labels.Shots[0] with { TargetReleaseTicks = 1200 }] }), "Target outside interval");
        Reject(() => dataset.Save(labels with { Shots = [labels.Shots[0], labels.Shots[0] with { ShotId = "overlap" }] }), "Overlapping intervals");
        File.AppendAllText(Path.Combine(directory, "labels.json"), " ");
        Reject(() => dataset.Save(labels), "Concurrent edit detection");
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(dataset.Snapshot(), DesktopJson.Options));
        Check(snapshot.RootElement.GetProperty("frames")[1].GetProperty("timeMs").GetDouble() == 100, "Shared timeline milliseconds");
    }
    Console.WriteLine("SUCCESS: legacy, supervised4-region and meter-off3-region review, exact pixels/offsets, labels, splits, overlap/identity/bounds and concurrent-edit guards.");
    var valid = Metadata(true);
    foreach (Action<JsonObject> mutate in new Action<JsonObject>[] {
        m => m["captureEvidence"] = null,
        m => m["captureEvidence"]!["version"] = 2,
        m => m["captureEvidence"]!["streams"]![1]!["file"] = "../../outside.bgra",
        m => m["captureEvidence"]!["streams"]![1]!["name"] = "player",
        m => m["frames"]![1]!["sequence"] = 1,
        m => m["frames"]![1]!["captureTicks"] = 1000,
        m => m["frames"]![1]!["evidence"]![0]!["offsetBytes"] = long.MaxValue,
        m => m["complete"] = false,
    }) { var changed = (JsonObject)valid.DeepClone(); mutate(changed); Write(changed); Reject(() => new DesktopVisionDataset(directory).Dispose(), "Invalid/foreign evidence"); }
    Write(valid); File.WriteAllBytes(Path.Combine(directory, "feedback.bgra"), [0, 1, 2]);
    Reject(() => new DesktopVisionDataset(directory).Dispose(), "Partial auxiliary file");
    Console.WriteLine("SUCCESS: malformed/future evidence, foreign filename, repeated stream/sequence, invalid timestamps/offsets, incomplete take and truncated auxiliary-file rejection.");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(directory)) != root || !Path.GetFileName(directory).StartsWith("AccessRelayDesktopEvidence-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned cleanup path.");
    Directory.Delete(directory, recursive: true);
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string name) { try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; } throw new Exception("Expected rejection: " + name); }
