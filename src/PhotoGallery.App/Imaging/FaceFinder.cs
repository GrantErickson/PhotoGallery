using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace PhotoGallery.App.Imaging;

/// <summary>Windows' on-device face detector (detection only — it doesn't recognise anyone).</summary>
public static class FaceFinder
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static FaceDetector? _detector;

    /// <summary>Face boxes in pixel coordinates of a BGRA (top-down) image.</summary>
    public static async Task<IReadOnlyList<BitmapBounds>> DetectAsync(byte[] bgra, int width, int height)
    {
        if (!FaceDetector.IsSupported) return [];
        await Gate.WaitAsync();
        try
        {
            _detector ??= await FaceDetector.CreateAsync();
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var format = FaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Nv12;
            using var gray = SoftwareBitmap.Convert(bitmap, format);
            var faces = await _detector.DetectFacesAsync(gray);
            return faces.Select(f => f.FaceBox).ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Like <see cref="DetectAsync"/>, but also finds sideways and upside-down faces (the detector only sees roughly
    /// upright ones — a group leaning in or lying in a circle is mostly missed) by looking at the picture turned each
    /// way. Boxes are in the original image's pixel coordinates, without duplicates.
    /// </summary>
    public static async Task<IReadOnlyList<Rect>> DetectAnyOrientationAsync(byte[] bgra, int width, int height)
    {
        var found = new List<Rect>();
        foreach (var turns in new[] { 0, 1, 2, 3 })
        {
            var (pixels, w, h) = turns == 0 ? (bgra, width, height) : Rotate(bgra, width, height, turns);
            foreach (var b in await DetectAsync(pixels, w, h))
            {
                var box = Unrotate(new Rect(b.X, b.Y, b.Width, b.Height), width, height, turns);
                if (!found.Any(f => Overlaps(f, box))) found.Add(box);
            }
        }
        return found;
    }

    /// <summary>Turns BGRA pixels clockwise by <paramref name="turns"/> quarter turns.</summary>
    private static (byte[] Pixels, int Width, int Height) Rotate(byte[] src, int width, int height, int turns)
    {
        var (w, h) = turns % 2 == 1 ? (height, width) : (width, height);
        var dst = new byte[src.Length];
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (u, v) = turns switch
                {
                    1 => (height - 1 - y, x),
                    2 => (width - 1 - x, height - 1 - y),
                    _ => (y, width - 1 - x),
                };
                d[v * w + u] = s[y * width + x];
            }
        return (dst, w, h);
    }

    /// <summary>A box found in the turned picture, back in the original's coordinates.</summary>
    private static Rect Unrotate(Rect r, int width, int height, int turns) => turns switch
    {
        1 => new Rect(r.Y, height - (r.X + r.Width), r.Height, r.Width),
        2 => new Rect(width - (r.X + r.Width), height - (r.Y + r.Height), r.Width, r.Height),
        3 => new Rect(width - (r.Y + r.Height), r.X, r.Height, r.Width),
        _ => r,
    };

    /// <summary>The same face found twice: the overlap covers most of the smaller box.</summary>
    private static bool Overlaps(Rect a, Rect b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 && w * h > 0.4 * Math.Min(a.Width * a.Height, b.Width * b.Height);
    }
}
