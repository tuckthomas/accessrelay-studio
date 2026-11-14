using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;

internal static class AutomaticIdentityChecks
{
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
        public VisionPersonPose[] DetectPeople(ReadOnlySpan<byte> pixels, int width, int height, int stride) => People;
        public VisionTextRegion[] ReadText(ReadOnlySpan<byte> pixels, int width, int height, int stride) => Text;
        public void RequestStop() { }
        public void Dispose() { }
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
