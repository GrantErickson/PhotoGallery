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

    /// <summary>Formats that can be written back (overwrite): JPEG, HEIC (needs the HEVC extension) and PNG.</summary>
    public static bool CanWriteFormat(string path) => EncoderFor(path) is not null;

    private static Guid? EncoderFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => BitmapEncoder.JpegEncoderId,
        ".heic" or ".heif" => BitmapEncoder.HeifEncoderId,
        ".png" => BitmapEncoder.PngEncoderId,
        _ => null,
    };

    /// <summary>
    /// Renders the edited photo at full resolution into <paramref name="destination"/>, in the format its extension
    /// names, copying date taken, camera and GPS from the original. Orientation is baked into the pixels.
    /// </summary>
    public static async Task WriteFileAsync(string sourcePath, EditOperations ops, string destination, DateTime? taken,
        (double Latitude, double Longitude)? location = null)
    {
        var encoderId = EncoderFor(destination) ?? throw new NotSupportedException($"Can't write {Path.GetExtension(destination)} files.");
        var metadata = await ReadMetadataAsync(sourcePath);
        using var source = await LoadAsync(sourcePath, 0);
        using var bitmap = await RenderAsync(source, ops);
        await using (var file = File.Create(destination))
        {
            var stream = file.AsRandomAccessStream();
            var props = encoderId == BitmapEncoder.PngEncoderId
                ? null
                : new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) };
            var encoder = props is null ? await BitmapEncoder.CreateAsync(encoderId, stream) : await BitmapEncoder.CreateAsync(encoderId, stream, props);
            using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            encoder.SetSoftwareBitmap(opaque);
            if (taken is { } date) metadata["System.Photo.DateTaken"] = new BitmapTypedValue(TakenForWic(date), Windows.Foundation.PropertyType.DateTime);
            if (location is { } gps) AddGps(metadata, gps.Latitude, gps.Longitude, encoderId == BitmapEncoder.JpegEncoderId ? "/app1/ifd/gps" : "/ifd/gps");
            foreach (var (key, value) in metadata)
            {
                try
                {
                    await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet { [key] = value });
                }
                catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    // Metadata is best-effort; each property is written separately so one failure doesn't drop the rest.
                }
            }
            await encoder.FlushAsync();
        }
        File.SetLastWriteTime(destination, DateTime.Now);
    }

    private static readonly string[] CopiedProperties = ["System.Photo.CameraManufacturer", "System.Photo.CameraModel"];

    /// <summary>
    /// WIC's System.GPS.* properties don't round-trip, so GPS is written as raw EXIF entries through metadata
    /// query paths: ref letters plus degrees/minutes/seconds rationals packed as (denominator &lt;&lt; 32 | numerator).
    /// </summary>
    private static void AddGps(Dictionary<string, BitmapTypedValue> metadata, double latitude, double longitude, string gpsPath)
    {
        metadata[$"{gpsPath}/{{ushort=1}}"] = new BitmapTypedValue(latitude >= 0 ? "N" : "S", Windows.Foundation.PropertyType.String);
        metadata[$"{gpsPath}/{{ushort=2}}"] = new BitmapTypedValue(Dms(latitude), Windows.Foundation.PropertyType.UInt64Array);
        metadata[$"{gpsPath}/{{ushort=3}}"] = new BitmapTypedValue(longitude >= 0 ? "E" : "W", Windows.Foundation.PropertyType.String);
        metadata[$"{gpsPath}/{{ushort=4}}"] = new BitmapTypedValue(Dms(longitude), Windows.Foundation.PropertyType.UInt64Array);

        static ulong[] Dms(double value)
        {
            value = Math.Abs(value);
            var degrees = Math.Floor(value);
            var minutesExact = (value - degrees) * 60;
            var minutes = Math.Floor(minutesExact);
            var seconds = (minutesExact - minutes) * 60;
            return [Rational((uint)degrees, 1), Rational((uint)minutes, 1), Rational((uint)Math.Round(seconds * 10000), 10000)];
        }

        static ulong Rational(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
    }

    private static async Task<Dictionary<string, BitmapTypedValue>> ReadMetadataAsync(string path)
    {
        var result = new Dictionary<string, BitmapTypedValue>();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var found = await decoder.BitmapProperties.GetPropertiesAsync(CopiedProperties);
            foreach (var (key, value) in found) result[key] = value;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
        }
        return result;
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
                    ["System.Photo.DateTaken"] = new BitmapTypedValue(TakenForWic(date), Windows.Foundation.PropertyType.DateTime),
                });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // Metadata is best-effort; the pixels are what matter.
            }
        }
        await encoder.FlushAsync();
    }

    /// <summary>
    /// WIC stores System.Photo.DateTaken as UTC and writes EXIF in local time. The photo's wall-clock time is local, so
    /// hand it over as an explicit UTC instant (offset 0); passing the local value directly shifted copies by the UTC offset.
    /// </summary>
    private static DateTimeOffset TakenForWic(DateTime localWallClock) =>
        new(DateTime.SpecifyKind(localWallClock, DateTimeKind.Local).ToUniversalTime(), TimeSpan.Zero);

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
