using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class AutoAdjustTests
{
    /// <summary>BGRA bytes for colours given as 0–1 RGB.</summary>
    private static byte[] Image(IEnumerable<(double R, double G, double B)> colors) =>
        colors.SelectMany(c => new[] { Byte(c.B), Byte(c.G), Byte(c.R), (byte)255 }).ToArray();

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>A grey ramp between two levels, tinted by channel factors (80 pixels a row).</summary>
    private static byte[] Ramp(double from, double to, double r = 1, double g = 1, double b = 1, int count = 4000) =>
        Image(Enumerable.Range(0, count).Select(i => from + (to - from) * i / (count - 1)).Select(v => (v * r, v * g, v * b)));

    [Fact]
    public void No_colour_edits_is_the_identity()
    {
        var (m, offset) = ColorAdjust.Matrix(EditOperations.None);
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                Assert.Equal(row == col ? 1 : 0, m[row, col], 9);
        Assert.Equal(0, offset, 9);
        Assert.False(EditOperations.None.HasColorAdjustments);
        Assert.True((EditOperations.None with { Tint = 0.2 }).HasColorMatrix);
    }

    [Fact]
    public void No_saturation_is_grey_and_white_balance_keeps_brightness()
    {
        var grey = ColorAdjust.Apply(ColorAdjust.Matrix(EditOperations.None with { Saturation = -1 }), 0.8, 0.4, 0.2);
        Assert.Equal(grey.R, grey.G, 6);
        Assert.Equal(grey.G, grey.B, 6);
        Assert.Equal(0.2126 * 0.8 + 0.7152 * 0.4 + 0.0722 * 0.2, grey.R, 6);

        var (r, g, b) = ColorAdjust.Gains(0.7, -0.4);
        Assert.Equal(0, Math.Log(r) + Math.Log(g) + Math.Log(b), 9);
        Assert.True(r > 1 && b < 1); // warmer
    }

    [Fact]
    public void A_well_exposed_neutral_picture_is_left_alone()
    {
        var ops = AutoAdjust.Apply(EditOperations.None, Ramp(0.0, 1.0), 80);
        Assert.Equal((0.0, 0.0, 0.0, 0.0, 0.0), (ops.Brightness, ops.Contrast, ops.Temperature, ops.Tint, ops.Exposure));
    }

    [Fact]
    public void A_flat_picture_gets_more_contrast_around_its_middle()
    {
        var ops = AutoAdjust.Apply(EditOperations.None, Ramp(0.30, 0.60), 80);
        Assert.Equal(0.75, ops.Contrast); // 0.96 / 0.3 is capped at ×1.6
        var map = ColorAdjust.Matrix(ops);
        var low = ColorAdjust.Apply(map, 0.30, 0.30, 0.30).R;
        var high = ColorAdjust.Apply(map, 0.60, 0.60, 0.60).R;
        Assert.InRange(high - low, 0.47, 0.49);
        Assert.InRange((low + high) / 2, 0.45, 0.55);

        // A dark picture's full range is stretched to nearly black and white.
        var dark = ColorAdjust.Matrix(AutoAdjust.Apply(EditOperations.None, Ramp(0.0, 0.65), 80));
        Assert.InRange(ColorAdjust.Apply(dark, 0, 0, 0).R, 0, 0.06);
        Assert.InRange(ColorAdjust.Apply(dark, 0.65, 0.65, 0.65).R, 0.93, 1);
    }

    [Fact]
    public void A_blue_cast_is_mostly_removed()
    {
        var ops = AutoAdjust.Apply(EditOperations.None, Ramp(0.05, 0.75, r: 0.85, b: 1.25), 80);
        Assert.True(ops.Temperature > 0.2, $"warmth {ops.Temperature}");
        var (r, _, b) = ColorAdjust.Apply(ColorAdjust.Matrix(ops with { Brightness = 0, Contrast = 0, Saturation = 0 }), 0.4 * 0.85, 0.4, 0.4 * 1.25);
        Assert.True(Math.Abs(Math.Log(b / r)) < 0.4 * Math.Log(1.25 / 0.85), $"still blue: r {r:F3} b {b:F3}");
    }

    [Fact]
    public void Only_dull_pictures_get_more_saturation()
    {
        var dull = Image(Enumerable.Range(0, 2000).Select(i => (0.45 + (i % 2) * 0.04, 0.45, 0.45 + ((i + 1) % 2) * 0.04)));
        var colourful = Image(Enumerable.Range(0, 2000).Select(i => (i % 3 == 0 ? 0.9 : 0.2, i % 3 == 1 ? 0.8 : 0.2, i % 3 == 2 ? 0.9 : 0.2)));
        Assert.True(AutoAdjust.Apply(EditOperations.None, dull, 50).Saturation > 0);
        Assert.Equal(0, AutoAdjust.Apply(EditOperations.None, colourful, 50).Saturation);
    }

    [Fact]
    public void Colour_edits_round_trip_through_json()
    {
        var ops = new EditOperations { Rotation = 90, Saturation = 0.2, Temperature = -0.3, Tint = 0.1 };
        Assert.Equal(ops, EditOperations.FromJson(ops.ToJson()));
        Assert.DoesNotContain("Tint", new EditOperations { Saturation = 0.2 }.ToJson());
    }
}
