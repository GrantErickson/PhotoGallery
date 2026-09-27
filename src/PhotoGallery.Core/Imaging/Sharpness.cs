namespace PhotoGallery.Core.Imaging;

/// <summary>
/// How sharp a picture looks: the edge energy (standard deviation of the Laplacian) of its sharpest areas, measured
/// on a small copy (~360 px) after stretching its brightness range, so a dark photo isn't "blurry" just for being dark
/// and a sharp subject in front of a soft background still counts as sharp. Roughly: under 20 is blurry, under 30
/// soft, 50 typical.
/// </summary>
public static class Sharpness
{
    /// <summary>Below this a photo is listed as blurry.</summary>
    public const double BlurryBelow = 24;

    private const int Grid = 4;
    private const double SharpestTiles = 0.9;

    /// <summary>The score of a BGRA picture (<paramref name="width"/> pixels a row).</summary>
    public static double Score(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width < 8 || height < 8 || bgra.Length < width * height * 4) return 0;
        var gray = new float[width * height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
            gray[i] = 0.114f * bgra[j] + 0.587f * bgra[j + 1] + 0.299f * bgra[j + 2];
        Stretch(gray);
        return Score(gray, width, height);
    }

    /// <summary>Levels: the 1st–99th percentile of brightness becomes 0–255 (at most ×16).</summary>
    private static void Stretch(float[] gray)
    {
        var histogram = new int[256];
        foreach (var v in gray) histogram[Math.Clamp((int)v, 0, 255)]++;
        float lo = Percentile(histogram, gray.Length, 0.01), hi = Percentile(histogram, gray.Length, 0.99);
        var scale = 255f / Math.Max(hi - lo, 16f);
        for (var i = 0; i < gray.Length; i++) gray[i] = (gray[i] - lo) * scale;
    }

    private static float Percentile(int[] histogram, int count, double fraction)
    {
        var target = fraction * count;
        var seen = 0;
        for (var i = 0; i < histogram.Length; i++)
            if ((seen += histogram[i]) >= target) return i;
        return 255;
    }

    /// <summary>The Laplacian's variance in each tile of a 4 × 4 grid; the 90th percentile tile, square-rooted.</summary>
    private static double Score(float[] g, int w, int h)
    {
        var tiles = new List<double>(Grid * Grid);
        for (var ty = 0; ty < Grid; ty++)
            for (var tx = 0; tx < Grid; tx++)
            {
                int x0 = Math.Max(1, tx * w / Grid), x1 = Math.Min(w - 1, (tx + 1) * w / Grid);
                int y0 = Math.Max(1, ty * h / Grid), y1 = Math.Min(h - 1, (ty + 1) * h / Grid);
                double sum = 0, squares = 0;
                var n = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = y * w + x;
                        double lap = 4 * g[i] - g[i - 1] - g[i + 1] - g[i - w] - g[i + w];
                        sum += lap;
                        squares += lap * lap;
                        n++;
                    }
                if (n > 0) tiles.Add(squares / n - sum / n * (sum / n));
            }
        if (tiles.Count == 0) return 0;
        tiles.Sort();
        return Math.Sqrt(Math.Max(0, tiles[(int)Math.Round(SharpestTiles * (tiles.Count - 1))]));
    }
}
