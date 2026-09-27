namespace PhotoGallery.Core.Editing;

/// <summary>
/// The editor's "Auto": white balance, levels (contrast and brightness) and a little saturation, worked out from the
/// picture itself and returned as ordinary slider values so they can be tweaked. Deliberately gentle: a colour cast is
/// only partly removed, contrast is only ever raised, and pictures that are already colourful keep their saturation.
/// </summary>
public static class AutoAdjust
{
    /// <summary>
    /// <paramref name="ops"/> with auto light and colour, from a small sample of the picture as it's cropped (BGRA
    /// bytes, <paramref name="width"/> pixels a row, rendered without any light or colour edits). Exposure is left at
    /// 0: levels do its job without clipping.
    /// </summary>
    public static EditOperations Apply(EditOperations ops, ReadOnlySpan<byte> bgra, int width)
    {
        var count = bgra.Length / 4;
        if (count == 0 || width <= 0) return ops;
        var pixels = new (double R, double G, double B)[count];
        for (int i = 0, j = 0; i < count; i++, j += 4)
            pixels[i] = (bgra[j + 2] / 255.0, bgra[j + 1] / 255.0, bgra[j] / 255.0);

        var (temperature, tint) = WhiteBalance(pixels, width);
        var (gr, gg, gb) = ColorAdjust.Gains(temperature, tint);
        var balanced = pixels.Select(p => (R: Math.Min(1, p.R * gr), G: Math.Min(1, p.G * gg), B: Math.Min(1, p.B * gb))).ToArray();

        // Levels: stretch the luma between its 0.5th and 99.5th percentiles to 0.02–0.98, around the middle of the range.
        var histogram = new int[256];
        foreach (var (r, g, b) in balanced) histogram[(int)Math.Round(Luma(r, g, b) * 255)]++;
        double lo = Percentile(histogram, count, 0.005), hi = Percentile(histogram, count, 0.995), median = Percentile(histogram, count, 0.5);
        var contrast = Math.Clamp(0.96 / Math.Max(hi - lo, 0.01), 1, 1.6);
        var offset = -((lo + hi) / 2 - 0.5) * contrast;
        // Then, if the midtones are still dark or bright, move them part of the way back.
        var mid = (median - 0.5) * contrast + 0.5 + offset;
        offset = Math.Clamp(offset + Math.Clamp((Math.Clamp(mid, 0.38, 0.55) - mid) * 0.5, -0.06, 0.06), -0.4, 0.4);

        // Saturation for dull pictures only (after the contrast, which strengthens colours too).
        double chroma = 0;
        var colored = 0;
        foreach (var (r, g, b) in balanced)
        {
            var y = Luma(r, g, b);
            if (y is < 0.08 or > 0.92) continue;
            chroma += (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) * contrast;
            colored++;
        }
        chroma = colored > 0 ? chroma / colored : 1;
        var saturation = chroma switch { < 0.10 => 0.2, < 0.18 => 0.12, < 0.25 => 0.06, _ => 0 };

        return ops with
        {
            Exposure = 0,
            Brightness = Math.Round(offset / 0.4, 2),
            Contrast = Math.Round((contrast - 1) / 0.8, 2),
            Saturation = saturation,
            Temperature = Math.Round(temperature, 2),
            Tint = Math.Round(tint, 2),
        };
    }

    /// <summary>
    /// Two estimates of the light's colour that fail differently: the average of the near-grey pixels (fooled by a
    /// picture full of dry grass or skin) and of the colour differences across edges ("grey edge", fooled by strongly
    /// coloured detail). Only what both agree on is corrected: the smaller of the two, 70% of it, at most ±0.6, and
    /// nothing for slight casts. Lamp-lit rooms get cooler; daylight scenes are mostly left alone.
    /// </summary>
    private static (double Temperature, double Tint) WhiteBalance((double R, double G, double B)[] pixels, int width)
    {
        if (GreyPixels(pixels) is not { } grey) return (0, 0);
        var edges = Edges(pixels, width);
        return (Agree(grey.Temperature, edges.Temperature), Agree(grey.Tint, edges.Tint));

        static double Agree(double a, double b)
        {
            if (Math.Sign(a) != Math.Sign(b)) return 0;
            var v = Math.Min(Math.Abs(a), Math.Abs(b)) * Math.Sign(a) * 0.7;
            return Math.Abs(v) < 0.03 ? 0 : Math.Clamp(v, -0.6, 0.6);
        }
    }

    /// <summary>The cast of the lit, unclipped pixels, weighted strongly towards the greyest; null if there are too few.</summary>
    private static (double Temperature, double Tint)? GreyPixels((double R, double G, double B)[] pixels)
    {
        double sr = 0, sg = 0, sb = 0, weight = 0;
        var used = 0;
        foreach (var (r, g, b) in pixels)
        {
            var max = Math.Max(r, Math.Max(g, b));
            if (Luma(r, g, b) < 0.25 || max > 0.97) continue;
            var chroma = (max - Math.Min(r, Math.Min(g, b))) / max;
            var w = 1 / (1 + 100 * chroma * chroma);
            sr += r * w;
            sg += g * w;
            sb += b * w;
            weight += w;
            used++;
        }
        return used < Math.Max(1, pixels.Length / 50) ? null : ColorAdjust.Neutralise(sr / weight, sg / weight, sb / weight);
    }

    /// <summary>The cast of the average colour difference to the right and below (clipped pixels skipped).</summary>
    private static (double Temperature, double Tint) Edges((double R, double G, double B)[] pixels, int width)
    {
        var height = pixels.Length / width;
        double sr = 1e-9, sg = 1e-9, sb = 1e-9;
        for (var y = 0; y < height - 1; y++)
            for (var x = 0; x < width - 1; x++)
            {
                var p = pixels[y * width + x];
                if (Math.Max(p.R, Math.Max(p.G, p.B)) > 0.97) continue;
                var right = pixels[y * width + x + 1];
                var below = pixels[(y + 1) * width + x];
                sr += Math.Abs(p.R - right.R) + Math.Abs(p.R - below.R);
                sg += Math.Abs(p.G - right.G) + Math.Abs(p.G - below.G);
                sb += Math.Abs(p.B - right.B) + Math.Abs(p.B - below.B);
            }
        return ColorAdjust.Neutralise(sr, sg, sb);
    }

    private static double Luma(double r, double g, double b) => ColorAdjust.Lr * r + ColorAdjust.Lg * g + ColorAdjust.Lb * b;

    private static double Percentile(int[] histogram, int count, double fraction)
    {
        var target = fraction * count;
        var seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target && seen > 0) return i / 255.0;
        }
        return 1;
    }
}
