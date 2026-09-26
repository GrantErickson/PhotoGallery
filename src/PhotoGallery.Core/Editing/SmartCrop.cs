namespace PhotoGallery.Core.Editing;

/// <summary>A face (or other must-keep region) in grid coordinates.</summary>
public readonly record struct Region(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Content-aware cropping. Scores candidate crops by how much "interest" they keep (a saliency map: edges plus
/// colour that stands out from the whole picture) and strongly prefers keeping faces whole, with head room.
/// Works on a small grid (e.g. 256 px on the long side); results are normalised to the image.
/// </summary>
public static class SmartCrop
{
    private const double FaceWeight = 12;
    private const double FaceCutPenalty = 30;

    /// <summary>
    /// The largest crop of <paramref name="aspect"/> (width/height, in image pixels) placed to keep the most
    /// interest and every face. With a null aspect the crop tightens around the subject in whatever shape it has
    /// (between 1:2 and 2:1).
    /// </summary>
    public static CropRect Find(float[] saliency, int width, int height, IReadOnlyList<Region> faces, double? aspect)
    {
        var integral = Integral(saliency, width, height);
        var padded = faces.Select(WithHeadRoom).Select(f => Clamp(f, width, height)).ToList();
        return aspect is { } a ? FixedAspect(integral, width, height, padded, a) : Tighten(saliency, width, height, faces, padded);
    }

    /// <summary>A face box grown to include hair and chin/shoulders, so crops don't sit right on the face edge.</summary>
    private static Region WithHeadRoom(Region f) =>
        new(f.X - f.Width * 0.35, f.Y - f.Height * 0.55, f.Width * 1.7, f.Height * 2.0);

    private static Region Clamp(Region r, int width, int height)
    {
        var x = Math.Clamp(r.X, 0, width);
        var y = Math.Clamp(r.Y, 0, height);
        return new Region(x, y, Math.Clamp(r.Right, 0, width) - x, Math.Clamp(r.Bottom, 0, height) - y);
    }

    private static CropRect FixedAspect(double[] integral, int width, int height, List<Region> faces, double aspect)
    {
        // The largest window of this aspect fills one dimension, so it only slides along the other.
        double cw = width, ch = width / aspect;
        if (ch > height) (cw, ch) = (height * aspect, height);
        var slideX = cw < width - 0.5;
        var steps = (int)Math.Max(1, Math.Round(slideX ? width - cw : height - ch));

        double bestScore = double.MinValue, bestX = 0, bestY = 0;
        var total = Sum(integral, width, 0, 0, width, height);
        for (var i = 0; i <= steps; i++)
        {
            var x = slideX ? i * (width - cw) / steps : 0;
            var y = slideX ? 0 : i * (height - ch) / steps;
            var score = Sum(integral, width, x, y, x + cw, y + ch) / Math.Max(total, 1e-9);
            foreach (var face in faces)
            {
                var area = Math.Max(1e-9, face.Width * face.Height);
                var kept = Overlap(face, x, y, x + cw, y + ch) / area;
                score += FaceWeight * kept * area / (width * height) + (kept is > 0 and < 0.999 ? -FaceCutPenalty * (1 - kept) * area / (width * height) : 0);
            }
            // Tiny pull towards the centre breaks ties between equally good windows.
            var centre = slideX ? (x + cw / 2) / width : (y + ch / 2) / height;
            score -= 1e-4 * Math.Abs(centre - 0.5);
            if (score > bestScore) (bestScore, bestX, bestY) = (score, x, y);
        }
        return new CropRect(bestX / width, bestY / height, cw / width, ch / height).Clamp(0.01);
    }

    /// <summary>
    /// Free aspect: shrinks around the subject with a margin. When faces take up a good part of the picture (a group or
    /// a portrait) the people are the subject; otherwise it's the region holding the central 90% of the interest, plus
    /// any faces. Returns the full image when that wouldn't remove much.
    /// </summary>
    private static CropRect Tighten(float[] saliency, int width, int height, IReadOnlyList<Region> faces, List<Region> padded)
    {
        double left = width, top = height, right = 0, bottom = 0;
        foreach (var f in padded)
        {
            left = Math.Min(left, f.X);
            top = Math.Min(top, f.Y);
            right = Math.Max(right, f.Right);
            bottom = Math.Max(bottom, f.Bottom);
        }
        var peopleFill = padded.Count > 0 && (right - left) * (bottom - top) >= 0.2 * width * height;
        double margin;
        if (peopleFill)
        {
            // Room around the heads in proportion to their size: shoulders below, a little air above and beside.
            var faceHeight = faces.Select(f => f.Height).Order().ElementAt(faces.Count / 2);
            margin = Math.Max(0.6 * faceHeight, 0.03 * Math.Max(width, height));
        }
        else
        {
            // Only interest above the picture's average counts, so a faint busy background doesn't hold the crop open.
            var mean = saliency.Average();
            var cols = new double[width];
            var rows = new double[height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var v = Math.Max(0, saliency[y * width + x] - mean);
                    cols[x] += v;
                    rows[y] += v;
                }
            var (l, r) = MassRange(cols, 0.05);
            var (t, b) = MassRange(rows, 0.05);
            (left, top, right, bottom) = (Math.Min(left, l), Math.Min(top, t), Math.Max(right, r), Math.Max(bottom, b));
            margin = 0.06 * Math.Max(width, height);
        }
        left = Math.Max(0, left - margin);
        top = Math.Max(0, top - margin);
        right = Math.Min(width, right + margin);
        bottom = Math.Min(height, bottom + margin);

        // Keep the shape between 1:2 and 2:1 by growing the short side around the centre.
        double cw = right - left, ch = bottom - top;
        if (cw / ch > 2) ch = Math.Min(height, cw / 2);
        if (ch / cw > 2) cw = Math.Min(width, ch / 2);
        if (cw * ch >= 0.88 * width * height) return CropRect.Full;
        var x0 = Math.Clamp((left + right - cw) / 2, 0, width - cw);
        var y0 = Math.Clamp((top + bottom - ch) / 2, 0, height - ch);
        return new CropRect(x0 / width, y0 / height, cw / width, ch / height).Clamp(0.01);
    }

    /// <summary>Indices bounding the middle (1 - 2·trim) of the mass along one axis.</summary>
    private static (double Start, double End) MassRange(double[] values, double trim)
    {
        var total = values.Sum();
        if (total <= 0) return (0, values.Length);
        double acc = 0;
        int start = 0, end = values.Length;
        for (var i = 0; i < values.Length; i++)
        {
            acc += values[i];
            if (acc >= total * trim) { start = i; break; }
        }
        acc = 0;
        for (var i = values.Length - 1; i >= 0; i--)
        {
            acc += values[i];
            if (acc >= total * trim) { end = i + 1; break; }
        }
        return (start, Math.Max(end, start + 1));
    }

    private static double Overlap(Region r, double x0, double y0, double x1, double y1) =>
        Math.Max(0, Math.Min(r.Right, x1) - Math.Max(r.X, x0)) * Math.Max(0, Math.Min(r.Bottom, y1) - Math.Max(r.Y, y0));

    /// <summary>Summed-area table with a zero row/column: size (w+1)×(h+1).</summary>
    private static double[] Integral(float[] values, int width, int height)
    {
        var table = new double[(width + 1) * (height + 1)];
        for (var y = 1; y <= height; y++)
        {
            double row = 0;
            for (var x = 1; x <= width; x++)
            {
                row += values[(y - 1) * width + (x - 1)];
                table[y * (width + 1) + x] = table[(y - 1) * (width + 1) + x] + row;
            }
        }
        return table;
    }

    /// <summary>Sum over a fractional rectangle (rounded to whole cells).</summary>
    private static double Sum(double[] table, int width, double x0, double y0, double x1, double y1)
    {
        int a = (int)Math.Round(x0), b = (int)Math.Round(y0), c = (int)Math.Round(x1), d = (int)Math.Round(y1);
        var stride = width + 1;
        return table[d * stride + c] - table[b * stride + c] - table[d * stride + a] + table[b * stride + a];
    }
}

/// <summary>A simple saliency map: edge strength plus how much each pixel's colour differs from the picture's average.</summary>
public static class Saliency
{
    /// <summary>From 32-bit BGRA pixels (top-down) to a map with values 0..1.</summary>
    public static float[] Compute(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var n = width * height;
        var l = new float[n];
        var a = new float[n];
        var b = new float[n];
        for (var i = 0; i < n; i++)
            (l[i], a[i], b[i]) = Lab(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);

        // Frequency-tuned saliency: distance of each (slightly blurred) pixel from the mean colour.
        var bl = Blur(l, width, height);
        var ba = Blur(a, width, height);
        var bb = Blur(b, width, height);
        float ml = l.Average(), ma = a.Average(), mb = b.Average();
        var colour = new float[n];
        for (var i = 0; i < n; i++)
            colour[i] = MathF.Sqrt((bl[i] - ml) * (bl[i] - ml) + (ba[i] - ma) * (ba[i] - ma) + (bb[i] - mb) * (bb[i] - mb));

        // Edges (Sobel on lightness): texture and outlines.
        var edges = new float[n];
        for (var y = 1; y < height - 1; y++)
            for (var x = 1; x < width - 1; x++)
            {
                float P(int dx, int dy) => l[(y + dy) * width + x + dx];
                var gx = P(1, -1) + 2 * P(1, 0) + P(1, 1) - P(-1, -1) - 2 * P(-1, 0) - P(-1, 1);
                var gy = P(-1, 1) + 2 * P(0, 1) + P(1, 1) - P(-1, -1) - 2 * P(0, -1) - P(1, -1);
                edges[y * width + x] = MathF.Sqrt(gx * gx + gy * gy);
            }
        var smoothEdges = Blur(edges, width, height);

        Normalize(colour);
        Normalize(smoothEdges);
        var result = new float[n];
        for (var i = 0; i < n; i++) result[i] = 0.6f * colour[i] + 0.4f * smoothEdges[i];
        return result;
    }

    private static void Normalize(float[] values)
    {
        var max = values.Max();
        if (max <= 0) return;
        for (var i = 0; i < values.Length; i++) values[i] /= max;
    }

    /// <summary>Two passes of a 5-tap box blur (≈ small Gaussian).</summary>
    private static float[] Blur(float[] src, int width, int height)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (var pass = 0; pass < 2; pass++)
        {
            var input = pass == 0 ? src : dst;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    float s = 0; var c = 0;
                    for (var k = -2; k <= 2; k++)
                        if (x + k >= 0 && x + k < width) { s += input[y * width + x + k]; c++; }
                    tmp[y * width + x] = s / c;
                }
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    float s = 0; var c = 0;
                    for (var k = -2; k <= 2; k++)
                        if (y + k >= 0 && y + k < height) { s += tmp[(y + k) * width + x]; c++; }
                    dst[y * width + x] = s / c;
                }
        }
        return dst;
    }

    /// <summary>sRGB (0..255) → CIE L*a*b* (D65).</summary>
    private static (float L, float A, float B) Lab(byte r8, byte g8, byte b8)
    {
        static float Lin(byte c)
        {
            var v = c / 255f;
            return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
        float r = Lin(r8), g = Lin(g8), b = Lin(b8);
        var x = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f;
        var y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        var z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
