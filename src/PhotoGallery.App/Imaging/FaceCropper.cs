using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using PhotoGallery.App.Editing;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Makes an avatar for a person: crops their photo to the face box OneDrive found for them. Photos without a box
/// fall back to Windows' on-device face detector (detection only, no recognition): the largest face among a few of
/// the person's photos. Results (and "no usable face") are cached on disk per person.
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

    /// <summary>Forgets every avatar (after people or face boxes change); they're re-cropped when next shown.</summary>
    public void Clear()
    {
        if (!Directory.Exists(cacheDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(cacheDirectory))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A crop made before, without making one: true if the person has been tried (<paramref name="path"/> is their face,
    /// or null if none of their photos had a usable one); false if not yet.
    /// </summary>
    public bool TryGetCached(long personId, out string? path)
    {
        var target = Path.Combine(cacheDirectory, $"{personId}.jpg");
        path = File.Exists(target) ? target : null;
        return path is not null || File.Exists(target + ".none");
    }

    /// <summary>
    /// Path of a square face crop for the person, or null if none of the candidates has a usable face. Candidates
    /// with a face box (fractions of the upright photo) are cropped to it directly.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(long personId, IEnumerable<(string Path, FaceBox? Box)> candidates)
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
            foreach (var (path, box) in candidates)
            {
                if (box is { } known)
                {
                    if (await KnownFaceAsync(path, known) is not { } face) continue;
                    best?.Bitmap.Dispose();
                    best = face;
                    break;
                }
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

    /// <summary>The photo with OneDrive's box for this person's face, if the face is big enough for an avatar.</summary>
    private static async Task<(CanvasBitmap Bitmap, BitmapBounds Face, double Fraction)?> KnownFaceAsync(string path, FaceBox box)
    {
        try
        {
            var source = await EditRenderer.LoadAsync(path, DetectSize);
            double width = source.SizeInPixels.Width, height = source.SizeInPixels.Height;
            var (x, y, w, h) = box.In(width, height);
            var face = new BitmapBounds
            {
                X = (uint)Math.Clamp(Math.Round(x), 0, width - 1), Y = (uint)Math.Clamp(Math.Round(y), 0, height - 1),
                Width = (uint)Math.Max(1, Math.Round(w)), Height = (uint)Math.Max(1, Math.Round(h)),
            };
            if (face.Height < 48)
            {
                source.Dispose();
                return null;
            }
            return (source, face, face.Height / Math.Min(width, height));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
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
        // Source rectangles are in DIPs, which differ from pixels unless the photo says 96 DPI (JPEGs often say 72).
        var dips = source.Size.Width / width;
        using (var session = crop.CreateDrawingSession())
            session.DrawImage(source, new Rect(0, 0, OutputSize, OutputSize), new Rect(x * dips, y * dips, side * dips, side * dips));
        await crop.SaveAsync(target, CanvasBitmapFileFormat.Jpeg, 0.9f);
    }
}
