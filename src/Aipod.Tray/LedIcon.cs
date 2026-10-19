namespace Aipod.Tray;

/// <summary>Draws the tray icon: two round lights side by side, orange on the left and green on the right, bright when lit and dim when not.</summary>
public static class LedIcon
{
    public const int Size = 32;

    private static readonly (byte R, byte G, byte B) OrangeOn = (255, 149, 0);
    private static readonly (byte R, byte G, byte B) OrangeOff = (120, 78, 20);
    private static readonly (byte R, byte G, byte B) GreenOn = (48, 209, 88);
    private static readonly (byte R, byte G, byte B) GreenOff = (24, 92, 44);

    public static byte[] Png(Leds leds) => PngEncoder.Encode(Size, Size, Pixels(leds));

    /// <summary>The RGBA pixels, rows from the top. Each light is a disc with an edge one pixel soft; a lit one has a halo and a bright spot.</summary>
    public static byte[] Pixels(Leds leds)
    {
        var pixels = new byte[Size * Size * 4];
        Disc(pixels, 9.0, 16.0, leds.Orange ? OrangeOn : OrangeOff, leds.Orange);
        Disc(pixels, 23.0, 16.0, leds.Green ? GreenOn : GreenOff, leds.Green);
        return pixels;
    }

    private static void Disc(byte[] pixels, double cx, double cy, (byte R, byte G, byte B) color, bool lit)
    {
        const double radius = 6.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt(((x + 0.5 - cx) * (x + 0.5 - cx)) + ((y + 0.5 - cy) * (y + 0.5 - cy)));
                var body = Math.Clamp(radius + 0.5 - distance, 0.0, 1.0);
                var halo = lit ? Math.Clamp(1.0 - ((distance - radius) / 3.0), 0.0, 1.0) * 0.35 : 0.0;
                var alpha = Math.Max(body, halo);
                if (alpha <= 0.0) continue;
                var spot = lit && body > 0 ? Math.Clamp(1.0 - (Math.Sqrt(((x + 0.5 - (cx - 2)) * (x + 0.5 - (cx - 2))) + ((y + 0.5 - (cy - 2)) * (y + 0.5 - (cy - 2)))) / 3.0), 0.0, 1.0) : 0.0;
                var at = ((y * Size) + x) * 4;
                var existing = pixels[at + 3] / 255.0;
                if (existing > alpha) continue; // the other light's halo does not paint over this one's body
                pixels[at] = Mix(color.R, spot);
                pixels[at + 1] = Mix(color.G, spot);
                pixels[at + 2] = Mix(color.B, spot);
                pixels[at + 3] = (byte)Math.Round(alpha * 255.0);
            }
        }
    }

    private static byte Mix(byte channel, double towardWhite) => (byte)Math.Round(channel + ((255 - channel) * towardWhite * 0.7));
}
