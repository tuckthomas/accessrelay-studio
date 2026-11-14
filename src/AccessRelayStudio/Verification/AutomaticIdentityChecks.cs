using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;
using System.Text.Json;
using System.Buffers.Binary;

internal static class AutomaticIdentityChecks
{
    internal static void InspectAcquisition(string bitmap, string observation, string output)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var original = JsonSerializer.Deserialize<VisionRegionObservation>(File.ReadAllBytes(observation), options)!;
        var bmp = File.ReadAllBytes(bitmap);
        int width = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22));
        int height = Math.Abs(signedHeight), offset = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28)) != 32)
            throw new InvalidDataException("Use a native BGRA diagnostic bitmap.");
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++) bmp.AsSpan(offset + (signedHeight < 0 ? y : height - 1 - y) * width * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        var body = original.BodyBounds ?? throw new InvalidDataException("No acquired body.");
        var (court, confidence) = Nba2K27AutomaticTracker.FindCourt(pixels, width, height, width * 4, body);
        var result = original with { Meter = Nba2K27AutomaticTracker.FindMeter(pixels, width, height, width * 4, body),
            Feedback = Nba2K27AutomaticTracker.FindFeedback(original.TextRegions ?? []), Court = court, CourtConfidence = confidence };
        File.WriteAllText(output, JsonSerializer.Serialize(result, options));
        Console.WriteLine($"Inspected retained acquisition frame {original.Sequence}: {court.Length} floor vertices, meter={result.Meter is not null}, feedback={result.Feedback is not null}.");
    }

    // Runs the actual HUD policy on retained gameplay, without a GPU or live capture.
    internal static void InspectCalibration(string directory, string output)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "session.json")));
        var root = document.RootElement;
        var stream = root.GetProperty("captureEvidence").GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "scene");
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var pixels = new byte[checked(width * height * 4)]; var results = new List<object>();
        using var raw = File.OpenRead(Path.Combine(directory, stream.GetProperty("file").GetString()!));
        int index = 0;
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            var evidence = frame.GetProperty("evidence").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "scene");
            raw.Position = evidence.GetProperty("offsetBytes").GetInt64(); raw.ReadExactly(pixels);
            VisionCrop? meter = null, feedback = null;
            if (frame.TryGetProperty("tracking", out var tracking))
            {
                if (tracking.TryGetProperty("bodyBounds", out var body))
                    meter = Nba2K27AutomaticTracker.FindMeter(pixels, width, height, width * 4, body.Deserialize<VisionCrop>(options));
                if (tracking.TryGetProperty("textRegions", out var text))
                    feedback = Nba2K27AutomaticTracker.FindFeedback(text.Deserialize<VisionTextRegion[]>(options) ?? []);
            }
            results.Add(new { index = index++, sequence = frame.GetProperty("sequence").GetInt64(), meter, feedback });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results, options));
        Console.WriteLine($"Inspected {index} recorded frames with the native NBA HUD policy. Results: {output}");
    }

    internal static void Run()
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = (y * width + x) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(y > height * .35 ? 150 : 15); pixels[i + 3] = 255; }
        var joints = Enumerable.Range(0, 17).Select(_ => new VisionKeypoint(.47, .5, .95)).ToArray();
        joints[5] = new(.43, .4, .95); joints[6] = new(.51, .4, .95);
        joints[9] = new(.43, .26, .95); joints[10] = new(.51, .27, .95);
        joints[15] = new(.44, .73, .95); joints[16] = new(.5, .73, .95);
        var player = new VisionPersonPose(new(.4, .3, .15, .45), .95, joints);
        var models = new Models { People = [player], Text = [new("DrakulaVlad2", .98, new(.4, .78, .14, .025)),
            new("TIMING", .98, new(.7, .05, .1, .025)), new("TEMPO", .98, new(.7, .1, .1, .025))] };
        using var tracker = new Nba2K27AutomaticTracker(new() { Gamertag = "DrakulaVlad2" }, () => models);
        Check(!tracker.Track(pixels, width, height, width * 4, 1, 1).Valid, "One observation was treated as repeated identity confirmation.");
        Thread.Sleep(360);
        var acquired = tracker.Track(pixels, width, height, width * 4, 2, 1);
        Check(acquired.Valid && acquired.Identity?.ObservedTag == "DrakulaVlad2" && acquired.Keypoints?.Length == 17, "Automatic tag/pose acquisition failed.");
        Check(acquired.Player.Top < .26 && acquired.Court.Length >= 3 && acquired.AcquisitionComplete, "Raised-hand padding or automatic court/HUD proposal failed.");
        models.People = [player, player with { Bounds = player.Bounds with { Left = .401 } }];
        Check(!tracker.Track(pixels, width, height, width * 4, 3, 1).Valid, "Two ambiguous bodies silently retained a player identity.");
        Check(!Nba2K27AutomaticTracker.MatchesTag("drakulavlad2", "DrakulaVlad2")
            && !Nba2K27AutomaticTracker.MatchesTag("OtherDrakulaVlad2", "DrakulaVlad2")
            && Nba2K27AutomaticTracker.MatchesTag("★ DrakulaVlad2", "DrakulaVlad2"), "Tag capitalization or token-boundary rules failed.");
        Console.WriteLine("SUCCESS: automatic identity association, repeated exact-tag confirmation, 17-joint crop, floor/HUD proposals and ambiguous-body rejection. Synthetic association checks, not gameplay acceptance.");
    }
    private sealed class Models : IVisionAnalysisModels
    {
        internal VisionPersonPose[] People = [];
        internal VisionTextRegion[] Text = [];
        public string ExecutionProvider => "synthetic";
        public VisionPersonPose[] DetectPeople(ReadOnlySpan<byte> pixels, int width, int height, int stride, VisionCrop? searchRegion = null) => People;
        public VisionTextRegion[] ReadText(ReadOnlySpan<byte> pixels, int width, int height, int stride) => Text;
        public void RequestStop() { }
        public void Dispose() { }
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
