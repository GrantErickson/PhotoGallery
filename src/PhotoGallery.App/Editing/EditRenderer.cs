using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoGallery.Core.Editing;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoGallery.App.Editing;

/// <summary>
/// Applies <see cref="EditOperations"/> with Win2D (GPU): exposure → brightness/contrast → flip/rotate → crop.
/// Sources are decoded through WIC with EXIF orientation applied, so every format with a codec works.
/// </summary>
public static class EditRenderer
{
    public static CanvasDevice Device => CanvasDevice.GetSharedDevice();

    /// <summary>Decodes an image, oriented, scaled so its longest side is at most <paramref name="maxSize"/> (0 = full size).</summary>
    public static async Task<CanvasBitmap> LoadAsync(string path, int maxSize)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = maxSize > 0 ? Math.Min(1.0, maxSize / (double)Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight)) : 1.0;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return CanvasBitmap.CreateFromSoftwareBitmap(Device, bitmap);
    }

    /// <summary>
    /// Builds the effect graph. With <paramref name="includeCrop"/> false the result is the whole rotated image
    /// (the editor draws the crop box on top of it).
    /// </summary>
    public static (ICanvasImage Image, Size Size) Apply(CanvasBitmap source, EditOperations ops, bool includeCrop = true)
    {
        ICanvasImage image = source;
        var w = (float)source.Size.Width;
        var h = (float)source.Size.Height;

        if (Math.Abs(ops.Exposure) >= 0.001)
            image = new ExposureEffect { Source = image, Exposure = (float)Math.Clamp(ops.Exposure, -2, 2) };
        if (Math.Abs(ops.Brightness) >= 0.001 || Math.Abs(ops.Contrast) >= 0.001)
        {
            // out = (in - 0.5) * c + 0.5 + b, per colour channel.
            var c = (float)(1 + Math.Clamp(ops.Contrast, -1, 1) * 0.8);
            var o = (float)(0.5 - 0.5 * c + Math.Clamp(ops.Brightness, -1, 1) * 0.4);
            image = new ColorMatrixEffect
            {
                Source = image,
                ColorMatrix = new Matrix5x4 { M11 = c, M22 = c, M33 = c, M44 = 1, M51 = o, M52 = o, M53 = o },
                ClampOutput = true,
            };
        }

        var matrix = Matrix3x2.Identity;
        if (ops.FlipHorizontal) matrix = Matrix3x2.CreateScale(-1, 1) * Matrix3x2.CreateTranslation(w, 0);
        var rotation = ((ops.Rotation % 360) + 360) % 360;
        matrix *= rotation switch
        {
            90 => Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(h, 0),
            180 => Matrix3x2.CreateRotation(MathF.PI) * Matrix3x2.CreateTranslation(w, h),
            270 => Matrix3x2.CreateRotation(MathF.PI * 3 / 2) * Matrix3x2.CreateTranslation(0, w),
            _ => Matrix3x2.Identity,
        };
        if (!matrix.IsIdentity)
            image = new Transform2DEffect { Source = image, TransformMatrix = matrix, InterpolationMode = CanvasImageInterpolation.NearestNeighbor };

        var size = ops.SwapsDimensions ? new Size(h, w) : new Size(w, h);
        if (includeCrop && ops.Crop is { } crop && !crop.IsFull)
        {
            var rect = new Rect(Math.Round(crop.X * size.Width), Math.Round(crop.Y * size.Height),
                Math.Max(1, Math.Round(crop.Width * size.Width)), Math.Max(1, Math.Round(crop.Height * size.Height)));
            image = new Transform2DEffect
            {
                Source = new CropEffect { Source = image, SourceRectangle = rect },
                TransformMatrix = Matrix3x2.CreateTranslation((float)-rect.X, (float)-rect.Y),
            };
            size = new Size(rect.Width, rect.Height);
        }
        return (image, size);
    }

    /// <summary>Renders the edited image into a SoftwareBitmap (BGRA8 premultiplied, ready for SoftwareBitmapSource).</summary>
    public static async Task<SoftwareBitmap> RenderAsync(CanvasBitmap source, EditOperations ops)
    {
        var (image, size) = Apply(source, ops);
        using var target = new CanvasRenderTarget(Device, (float)size.Width, (float)size.Height, 96);
        using (var session = target.CreateDrawingSession())
            session.DrawImage(image);
        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(target, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>Loads, edits and returns a display-sized bitmap of the photo.</summary>
    public static async Task<SoftwareBitmap> RenderPreviewAsync(string path, EditOperations ops, int maxSize)
    {
        using var source = await LoadAsync(path, maxSize);
        return await RenderAsync(source, ops);
    }

    /// <summary>Writes a full-resolution edited JPEG, carrying over the date taken.</summary>
    public static async Task ExportAsync(string sourcePath, EditOperations ops, StorageFile destination, DateTime? taken)
    {
        using var source = await LoadAsync(sourcePath, 0);
        using var bitmap = await RenderAsync(source, ops);
        using var stream = await destination.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, props);
        using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        encoder.SetSoftwareBitmap(opaque);
        if (taken is { } date)
        {
            try
            {
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    ["System.Photo.DateTaken"] = new BitmapTypedValue(new DateTimeOffset(date), Windows.Foundation.PropertyType.DateTime),
                });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // Metadata is best-effort; the pixels are what matter.
            }
        }
        await encoder.FlushAsync();
    }

    /// <summary>A size that fits (w, h) into the box, preserving aspect.</summary>
    public static Rect Fit(Size content, Size box)
    {
        if (content.Width <= 0 || content.Height <= 0 || box.Width <= 0 || box.Height <= 0) return default;
        var scale = Math.Min(box.Width / content.Width, box.Height / content.Height);
        var w = content.Width * scale;
        var h = content.Height * scale;
        return new Rect((box.Width - w) / 2, (box.Height - h) / 2, w, h);
    }

}
