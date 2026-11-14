using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;
using System.Text.Json;
using System.Buffers.Binary;
using System.Diagnostics;

internal static class AutomaticIdentityChecks
{
    internal static void CheckCourt()
    {
        VisionPoint[]? previous = null;
        foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440) })
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                double nx = x / (double)width, ny = y / (double)height;
                // Wall and paint share a color. A dark baseboard separates the floor.
                int r = 174, g = 118, b = 64;
                if (ny >= .32 && ny < .35) r = g = b = 15;
                else if (ny >= .35)
                {
                    r = g = b = 150;
                    if (nx > .4 && nx < .65 && ny > .45 && ny < .85) { r = 174; g = 118; b = 64; }
                    else if (nx > .1 && nx < .3 && ny > .6 && ny < .8) r = g = b = 90;
                    // A small obstruction lies over the foot-adjacent seed.
                    if (Math.Abs(nx - .508) < .008 && Math.Abs(ny - .694) < .012) r = g = b = 10;
                }
                int i = (y * width + x) * 4;
                pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
            var painted = Nba2K27AutomaticTracker.FindCourt(pixels, width, height, width * 4, new(.42, .38, .08, .32));
            var gray = Nba2K27AutomaticTracker.FindCourt(pixels, width, height, width * 4, new(.74, .38, .08, .32));
            foreach (var result in new[] { painted, gray })
            {
                Check(result.Court.Length >= 3 && result.Confidence > 0, "A multicolor floor was not acquired.");
                foreach (var point in new[] { new VisionPoint(.15, .75), new(.85, .75), new(.5, .6) })
                    Check(Nba2K27CaptureConfiguration.Inside(result.Court, point), "Floor colors or printed graphics split the court into a local patch.");
                Check(!Nba2K27CaptureConfiguration.Inside(result.Court, new(.5, .2)), "Matching wall paint leaked into the court.");
            }
            Check(painted.Court.SequenceEqual(gray.Court), "Moving from paint to gray floor changed the court extent.");
            if (previous is not null) Check(previous.SequenceEqual(painted.Court), "1080p and 1440p produced different normalized court estimates.");
            previous = painted.Court;
        }
        var flat = new byte[320 * 180 * 4];
        for (int i = 0; i < flat.Length; i += 4) { flat[i] = flat[i + 1] = flat[i + 2] = 150; flat[i + 3] = 255; }
        Check(Nba2K27AutomaticTracker.FindCourt(flat, 320, 180, 320 * 4, new(.4, .4, .1, .3)).Court.Length == 0,
            "A scene without a floor boundary fabricated a court.");
        Console.WriteLine("SUCCESS: mixed wood/gray/graphics, foot-seed obstruction, matching-wall exclusion, seed-position stability and 1080p/1440p court geometry.");
    }

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
        long at = Stopwatch.Frequency;
        Check(!tracker.Track(pixels, width, height, width * 4, 1, 1, at).Valid, "One observation was treated as repeated identity confirmation.");
        var acquired = tracker.Track(pixels, width, height, width * 4, 2, 1, at + Stopwatch.Frequency * 36 / 100);
        Check(acquired.Valid && acquired.Identity?.ObservedTag == "DrakulaVlad2" && acquired.Keypoints?.Length == 17, "Automatic tag/pose acquisition failed.");
        Check(acquired.Player.Top < .26 && acquired.Court.Length >= 3 && acquired.AcquisitionComplete, "Raised-hand padding or automatic court/HUD proposal failed.");
        models.People = [player, player with { Bounds = player.Bounds with { Left = .401 } }];
        Check(!tracker.Track(pixels, width, height, width * 4, 3, 1, at + Stopwatch.Frequency * 4 / 10).Valid, "Two ambiguous bodies silently retained a player identity.");
        using var clocked = new Nba2K27AutomaticTracker(new() { Gamertag = "DrakulaVlad2" }, () => models);
        models.People = [player];
        clocked.Track(pixels, width, height, width * 4, 1, 1, at);
        Check(clocked.Track(pixels, width, height, width * 4, 2, 1, at + Stopwatch.Frequency * 36 / 100).Valid, "Saved frame time did not schedule the second OCR confirmation.");
        models.Text = [];
        Check(clocked.Track(pixels, width, height, width * 4, 3, 1, at + Stopwatch.Frequency * 72 / 100).Valid, "One missing OCR observation discarded uniquely associated, recently verified pose continuity.");
        Check(!clocked.Track(pixels, width, height, width * 4, 4, 1, at + Stopwatch.Frequency * 11 / 10).Valid, "Offline replay retained expired identity based on processing wall time.");
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
