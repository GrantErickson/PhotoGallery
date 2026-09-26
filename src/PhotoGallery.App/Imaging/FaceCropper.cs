using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using PhotoGallery.App.Editing;
using PhotoGallery.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Makes an avatar for a person: looks at a few of their photos with Windows' on-device face detector (detection
/// only, no recognition), keeps the one with the largest face and crops to it. OneDrive doesn't share its own face
/// crops. Results (and "no usable face") are cached on disk per person.
/// </summary>
public sealed class FaceCropper(string cacheDirectory)
{
    private const int DetectSize = 1600;
    private const int OutputSize = 256;
    /// <summary>Faces smaller than this fraction of the photo's short side make poor avatars.</summary>
    private const double MinFaceFraction = 0.08;
    /// <summary>Good enough to stop looking at more candidates.</summary>
    private const double GoodFaceFraction = 0.22;

    private readonly SemaphoreSlim _gate = new(2);
    private FaceDetector? _detector;

    /// <summary>Path of a square face crop for the person, or null if none of the candidates has a usable face.</summary>
    public async Task<string?> GetOrCreateAsync(long personId, IEnumerable<string> candidatePhotos)
    {
        var target = Path.Combine(cacheDirectory, $"{personId}.jpg");
        var miss = target + ".none";
        if (File.Exists(target)) return target;
        if (File.Exists(miss)) return null;

        await _gate.WaitAsync();
        try
        {
            if (!FaceDetector.IsSupported) return null;
            _detector ??= await FaceDetector.CreateAsync();
            Directory.CreateDirectory(cacheDirectory);

            (CanvasBitmap Bitmap, BitmapBounds Face, double Fraction)? best = null;
            foreach (var path in candidatePhotos)
            {
                var found = await LargestFaceAsync(path);
                if (found is null) continue;
                if (best is null || found.Value.Fraction > best.Value.Fraction)
                {
                    best?.Bitmap.Dispose();
                    best = found;
                }
                else
                {
                    found.Value.Bitmap.Dispose();
                }
                if (best.Value.Fraction >= GoodFaceFraction) break;
            }

            if (best is not { } choice)
            {
                await File.WriteAllTextAsync(miss, "");
                return null;
            }
            using (choice.Bitmap)
                await SaveCropAsync(choice.Bitmap, choice.Face, target);
            return target;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Face crop failed for person {personId}", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(CanvasBitmap Bitmap, BitmapBounds Face, double Fraction)?> LargestFaceAsync(string path)
    {
        CanvasBitmap? source = null;
        try
        {
            source = await EditRenderer.LoadAsync(path, DetectSize);
            using var bgra = SoftwareBitmap.CreateCopyFromBuffer(source.GetPixelBytes().AsBuffer(), BitmapPixelFormat.Bgra8,
                (int)source.SizeInPixels.Width, (int)source.SizeInPixels.Height, BitmapAlphaMode.Premultiplied);
            var format = FaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Nv12;
            using var gray = SoftwareBitmap.Convert(bgra, format);
            var faces = await _detector!.DetectFacesAsync(gray);
            if (faces.Count == 0) return Discard();
            var face = faces.MaxBy(f => f.FaceBox.Width * f.FaceBox.Height)!.FaceBox;
            var fraction = face.Height / (double)Math.Min(source.SizeInPixels.Width, source.SizeInPixels.Height);
            if (fraction < MinFaceFraction) return Discard();
            var result = (source, face, fraction);
            source = null; // ownership passes to the caller
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            return Discard();
        }

        (CanvasBitmap, BitmapBounds, double)? Discard()
        {
            source?.Dispose();
            return null;
        }
    }

    private static async Task SaveCropAsync(CanvasBitmap source, BitmapBounds face, string target)
    {
        // Pad the face box so the avatar shows the head rather than just the features.
        double width = source.SizeInPixels.Width, height = source.SizeInPixels.Height;
        var side = Math.Min(Math.Max(face.Width, face.Height) * 1.8, Math.Min(width, height));
        var x = Math.Clamp(face.X + face.Width / 2.0 - side / 2, 0, width - side);
        var y = Math.Clamp(face.Y + face.Height / 2.0 - side / 2, 0, height - side);
        using var crop = new CanvasRenderTarget(EditRenderer.Device, OutputSize, OutputSize, 96);
        using (var session = crop.CreateDrawingSession())
            session.DrawImage(source, new Rect(0, 0, OutputSize, OutputSize), new Rect(x, y, side, side));
        await crop.SaveAsync(target, CanvasBitmapFileFormat.Jpeg, 0.9f);
    }
}
