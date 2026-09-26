using Microsoft.Graphics.Canvas;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using Windows.Foundation;

namespace PhotoGallery.App.Editing;

/// <summary>Runs <see cref="SmartCrop"/> on the photo as it currently looks in the editor (rotated/flipped, uncropped).</summary>
public static class SmartCropAnalyzer
{
    private const int MapSize = 256;   // saliency grid, long side
    private const int FaceSize = 1280; // face detection, long side

    public static async Task<(CropRect Crop, int Faces)> FindAsync(CanvasBitmap source, EditOperations ops, double? aspect)
    {
        var (image, size) = EditRenderer.Apply(source, ops with { Crop = null }, includeCrop: false);
        try
        {
            var (mapBytes, mw, mh) = Render(image, size, MapSize);
            var (faceBytes, fw, fh) = Render(image, size, FaceSize);
            var map = await Task.Run(() => Saliency.Compute(mapBytes, mw, mh));
            var faces = await FaceFinder.DetectAnyOrientationAsync(faceBytes, fw, fh);
            double sx = (double)mw / fw, sy = (double)mh / fh;
            var regions = faces.Select(f => new Region(f.X * sx, f.Y * sy, f.Width * sx, f.Height * sy)).ToList();
            var crop = await Task.Run(() => SmartCrop.Find(map, mw, mh, regions, aspect));
            return (crop, regions.Count);
        }
        finally
        {
            if (!ReferenceEquals(image, source)) (image as IDisposable)?.Dispose();
        }
    }

    /// <summary>Draws the image scaled so its long side is at most <paramref name="longSide"/>; returns BGRA pixels.</summary>
    private static (byte[] Pixels, int Width, int Height) Render(ICanvasImage image, Size size, int longSide)
    {
        var scale = Math.Min(1.0, longSide / Math.Max(size.Width, size.Height));
        var w = Math.Max(8, (int)Math.Round(size.Width * scale));
        var h = Math.Max(8, (int)Math.Round(size.Height * scale));
        using var target = new CanvasRenderTarget(EditRenderer.Device, w, h, 96);
        using (var session = target.CreateDrawingSession())
            session.DrawImage(image, new Rect(0, 0, w, h), new Rect(0, 0, size.Width, size.Height));
        return (target.GetPixelBytes(), w, h);
    }
}
