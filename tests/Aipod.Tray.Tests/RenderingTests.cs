using System.IO.Compression;
using Aipod.Tray;

namespace Aipod.Tray.Tests;

public sealed class RenderingTests
{
    private static (int Width, int Height, byte[] Pixels) Decode(byte[] png)
    {
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        var at = 8;
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png[(at + 8)..(at + 8 + length)];
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                Assert.Equal((8, 6), (data[8], data[9]));
            }

            if (type == "IDAT") idat.Write(data);
            at += 12 + length;
        }

        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress)) zlib.CopyTo(raw);
        var scan = raw.ToArray();
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, scan[y * ((width * 4) + 1)]); // filter none
            Array.Copy(scan, (y * ((width * 4) + 1)) + 1, pixels, y * width * 4, width * 4);
        }

        return (width, height, pixels);
    }

    [Fact]
    public void A_png_round_trips_its_pixels_and_carries_a_correct_checksum_in_every_chunk()
    {
        var rgba = new byte[4 * 3 * 4];
        for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 7);

        var png = PngEncoder.Encode(4, 3, rgba);

        var (width, height, pixels) = Decode(png);
        Assert.Equal((4, 3), (width, height));
        Assert.Equal(rgba, pixels);
        // every chunk's CRC: recompute with an independent bitwise implementation
        var at = 8;
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var crc = 0xFFFFFFFFu;
            for (var i = at + 4; i < at + 8 + length; i++)
            {
                crc ^= png[i];
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            var stored = ((uint)png[at + 8 + length] << 24) | ((uint)png[at + 9 + length] << 16) | ((uint)png[at + 10 + length] << 8) | png[at + 11 + length];
            Assert.Equal(crc ^ 0xFFFFFFFFu, stored);
            at += 12 + length;
        }
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 2, 3)]
    public void Pixel_data_that_does_not_match_the_size_is_refused(int width, int height, int length) =>
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(width, height, new byte[length]));

    [Fact]
    public void Missing_pixel_data_is_refused() => Assert.Throws<ArgumentNullException>(() => PngEncoder.Encode(1, 1, null!));

    private static (byte R, byte G, byte B, byte A) At(byte[] pixels, int x, int y)
    {
        var i = ((y * LedIcon.Size) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    [Fact]
    public void The_icon_has_an_orange_light_on_the_left_and_a_green_one_on_the_right_dim_until_lit()
    {
        var off = LedIcon.Pixels(Leds.Off);
        var orangeOn = LedIcon.Pixels(new Leds(true, false));
        var greenOn = LedIcon.Pixels(new Leds(false, true));

        Assert.Equal(255, At(off, 9, 16).A);
        Assert.True(At(off, 9, 16).R > At(off, 9, 16).G && At(off, 9, 16).G > At(off, 9, 16).B); // orange family
        Assert.True(At(off, 23, 16).G > At(off, 23, 16).R); // green family
        Assert.True(At(orangeOn, 9, 16).R > At(off, 9, 16).R); // brighter when lit
        Assert.Equal(At(off, 23, 16), At(orangeOn, 23, 16)); // the other light is not touched
        Assert.True(At(greenOn, 23, 16).G > At(off, 23, 16).G);
        Assert.Equal(At(off, 9, 16), At(greenOn, 9, 16));
        Assert.Equal(0, At(off, 0, 0).A); // the corners are transparent
        Assert.Equal(0, At(off, 31, 31).A);
        Assert.True(At(orangeOn, 9, 24).A > 0 && At(off, 9, 24).A == 0); // a lit light has a halo, a dim one none
    }

    [Fact]
    public void The_icon_png_is_a_square_of_the_declared_size()
    {
        var (width, height, pixels) = Decode(LedIcon.Png(new Leds(true, true)));
        Assert.Equal((LedIcon.Size, LedIcon.Size), (width, height));
        Assert.Equal(LedIcon.Pixels(new Leds(true, true)), pixels);
    }
}

public sealed class LedBlinkerTests
{
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_first_reading_and_a_reset_counter_never_light_anything()
    {
        var blinker = new LedBlinker(clock);
        Assert.Equal(Leds.Off, blinker.Now());
        blinker.Observe(40, 10);
        Assert.Equal(Leds.Off, blinker.Now());
        blinker.Observe(0, 0); // the database was emptied
        Assert.Equal(Leds.Off, blinker.Now());
        blinker.Observe(0, 0);
        Assert.Equal(Leds.Off, blinker.Now());
    }

    [Fact]
    public void A_message_in_blinks_the_orange_light_alone_for_three_seconds_starting_lit()
    {
        var blinker = new LedBlinker(clock);
        blinker.Observe(1, 1);
        blinker.Observe(2, 1);

        Assert.Equal(new Leds(true, false), blinker.Now());
        clock.Advance(TimeSpan.FromMilliseconds(310));
        Assert.Equal(Leds.Off, blinker.Now());
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(new Leds(true, false), blinker.Now());
        clock.Advance(LedBlinker.Active);
        Assert.Equal(Leds.Off, blinker.Now());
    }

    [Fact]
    public void A_message_out_blinks_the_green_light_and_a_new_one_restarts_the_blink()
    {
        var blinker = new LedBlinker(clock);
        blinker.Observe(0, 0);
        blinker.Observe(0, 1);
        Assert.Equal(new Leds(false, true), blinker.Now());
        clock.Advance(TimeSpan.FromMilliseconds(350));
        Assert.False(blinker.Now().Green);
        blinker.Observe(0, 2);
        Assert.True(blinker.Now().Green); // lit again at once

        blinker.Observe(1, 2);
        Assert.Equal(new Leds(true, true), blinker.Now()); // both can blink together
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(600, true)]
    [InlineData(2999, false)]
    [InlineData(3000, false)]
    public void The_blink_pattern_is_lit_for_half_a_period_then_dark(int milliseconds, bool lit) =>
        Assert.Equal(lit, LedBlinker.Lit(TimeSpan.FromMilliseconds(milliseconds)));
}
