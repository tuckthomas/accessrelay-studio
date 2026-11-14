using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;

internal static class NbaCaptureAnchorChecks
{
    internal static void Run()
    {
        AutomaticIdentityChecks.Run();
        const int width = 320, height = 180;
        var random = new Random(42); var floor = new byte[width * height]; random.NextBytes(floor);
        var player = new VisionCrop(.4, .45, .12, .32);
        var configuration = new Nba2K27CaptureConfiguration { Player = player, Meter = new(.36, .5, .025, .2),
            Court = [new(.1, .4), new(.9, .4), new(.99, .95), new(.01, .95)], ReferenceWidth = width, ReferenceHeight = height };
        byte[] Image(int cameraX, int playerX)
        {
            var image = new byte[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) image[y * width + x] = floor[y * width + Math.Clamp(x - cameraX, 0, width - 1)];
            int left = (int)(player.Left * width) + cameraX + playerX, top = (int)(player.Top * height);
            for (int y = 0; y < (int)(player.Height * height); y++) for (int x = 0; x < (int)(player.Width * width); x++)
                image[(top + y) * width + left + x] = (byte)(40 + ((x * 31 + y * 17 + x * y * 3) % 180));
            return image;
        }
        byte[] Bgra(byte[] gray)
        {
            var image = new byte[gray.Length * 4];
            for (int i = 0; i < gray.Length; i++) { image[i * 4] = image[i * 4 + 1] = image[i * 4 + 2] = gray[i]; image[i * 4 + 3] = 255; }
            return image;
        }
        configuration = configuration with { ReferenceGray = Image(0, 0) };
        configuration.Validate("2K", true, true);
        var tracker = new Nba2K27RegionTracker(configuration);
        var first = tracker.Track(Bgra(Image(0, 0)), width, height, width * 4, 1, 1);
        Check(first.Valid, first.Message);
        var moved = tracker.Track(Bgra(Image(2, 3)), width, height, width * 4, 2, 1);
        Check(moved.Valid, moved.Message);
        Check(moved.Player.Left > first.Player.Left + .008 && moved.Meter!.Value.Left > first.Meter!.Value.Left + .008, "Player/meter regions did not follow player motion.");
        Check(Math.Abs(moved.Court[0].X - first.Court[0].X - 2d / width) < .005, "Court did not follow camera motion independently of the player.");
        Check(first.Player.Top < configuration.Court[0].Y, "Floor geometry clipped the area above the player's head.");
        var cut = tracker.Track(new byte[width * height * 4], width, height, width * 4, 3, 1);
        Check(!cut.Valid, "A scene cut retained player identity.");
        Check(!tracker.Track(Bgra(Image(2, 3)), width, height, width * 4, 4, 1).Valid, "Lost identity silently reacquired another player.");
        Check(!new Nba2K27RegionTracker(configuration).Track(Bgra(Image(0, 0)), width, height, width * 4, 1, 1).Court.Any(p => p.Y < 0), "Invalid court projection.");
        Console.WriteLine("SUCCESS: moving player/meter anchors, independent camera/court motion, upper-body padding and persistent identity loss on a cut. Synthetic geometry only; gameplay acceptance remains open.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
