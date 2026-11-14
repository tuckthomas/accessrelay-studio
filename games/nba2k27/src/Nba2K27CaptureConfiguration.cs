using System.Buffers.Binary;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

/// <summary>NBA-owned region rules and visual acquisition reference, separate from release-model weights.</summary>
internal sealed record Nba2K27CaptureConfiguration
{
    public int Version { get; init; } = 1;
    public string Camera { get; init; } = "2K";
    public string PlayerAnchor { get; init; } = "torso-appearance-v1";
    public string MeterAnchor { get; init; } = "player-relative-region-v1";
    public string CourtAnchor { get; init; } = "floor-homography-v1";
    public VisionCrop Player { get; init; }
    public VisionCrop Viewport { get; init; } = new(0, 0, 1, 1);
    public VisionCrop? Meter { get; init; }
    public VisionPoint[] Court { get; init; } = [];
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public byte[] ReferenceGray { get; init; } = [];
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 16 };

    internal static Nba2K27CaptureConfiguration Read(JsonElement? value) => value?.Deserialize<Nba2K27CaptureConfiguration>(Json)
        ?? throw new InvalidDataException("Set the NBA player, meter and court anchors on a fresh preview.");

    internal void Validate(string camera, bool meterOn, bool requireReference)
    {
        if (Version != 1 || Camera != camera || PlayerAnchor != "torso-appearance-v1" || MeterAnchor != "player-relative-region-v1" || CourtAnchor != "floor-homography-v1")
            throw new InvalidDataException("Reconfigure the NBA capture anchors for this camera.");
        Player.Validate(); Meter?.Validate(); Viewport.Validate();
        if (meterOn && Meter is null) throw new InvalidDataException("Mark the meter's full outline while it is visible.");
        if (Court is not { Length: 4 } || Court.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > 1 || p.Y > 1))
            throw new InvalidDataException("Mark four court-floor corners in order around the floor.");
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = Court[i]; var b = Court[(i + 1) % 4]; var c = Court[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < .0001 || (sign != 0 && Math.Sign(cross) != Math.Sign(sign)))
                throw new InvalidDataException("Court corners must form a non-crossing floor outline.");
            sign = cross;
        }
        if (!Inside(Court, new(Player.Left + Player.Width / 2, Player.Top + Player.Height)))
            throw new InvalidDataException("The player's initial ground position must be on the marked floor. Include raised hands above the floor in the player box.");
        if (requireReference && (ReferenceWidth is < 32 or > 640 || ReferenceHeight is < 32 or > 640 || ReferenceGray.Length != ReferenceWidth * ReferenceHeight))
            throw new InvalidDataException("The visual anchor reference is missing. Open capture setup again.");
    }

    internal Nba2K27CaptureConfiguration WithReference(ReadOnlySpan<byte> bitmap)
    {
        if (bitmap.Length < 54 || bitmap[0] != 'B' || bitmap[1] != 'M' || BinaryPrimitives.ReadInt16LittleEndian(bitmap[28..]) != 32
            || BinaryPrimitives.ReadInt32LittleEndian(bitmap[30..]) != 0)
            throw new InvalidDataException("The capture reference must be a BGRA bitmap.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bitmap[10..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(bitmap[18..]), signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bitmap[22..]);
        int height = Math.Abs(signedHeight);
        if (width is < 32 or > 16384 || height is < 32 or > 16384 || offset < 54 || (long)offset + (long)width * height * 4 > bitmap.Length)
            throw new InvalidDataException("The capture reference dimensions are invalid.");
        double scale = Math.Min(1, Math.Min(640d / Math.Max(width, height), Math.Sqrt(360000d / ((double)width * height))));
        int rw = Math.Max(32, (int)Math.Round(width * scale)), rh = Math.Max(32, (int)Math.Round(height * scale));
        var gray = new byte[rw * rh];
        for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
        {
            int sy = Math.Min(height - 1, (int)((y + .5) * height / rh));
            if (signedHeight > 0) sy = height - 1 - sy;
            int index = offset + (sy * width + Math.Min(width - 1, (int)((x + .5) * width / rw))) * 4;
            gray[y * rw + x] = (byte)((bitmap[index] * 29 + bitmap[index + 1] * 150 + bitmap[index + 2] * 77) >> 8);
        }
        return this with { ReferenceWidth = rw, ReferenceHeight = rh, ReferenceGray = gray };
    }

    internal static bool Inside(IReadOnlyList<VisionPoint> polygon, VisionPoint point)
    {
        double sign = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            double cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (Math.Abs(cross) < 1e-8) continue;
            if (sign != 0 && Math.Sign(cross) != Math.Sign(sign)) return false;
            sign = cross;
        }
        return true;
    }
}
