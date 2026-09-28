using PhotoGallery.Core.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoGallery.Remote;

/// <summary>
/// Photos as browsers can show them: JPEG, upright, in sRGB, at the size asked for. HEIC and the like are decoded
/// here, on the host (with its codecs), so the other computer needs nothing installed.
/// </summary>
public static class DisplayRenderer
{
    public static async Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct = default)
    {
        if (item.Kind == MediaKind.Video || !File.Exists(item.Path)) return null;
        // Already fine as it is: an upright JPEG no bigger than asked.
        var extension = Path.GetExtension(item.Path);
        if ((extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            && item.Orientation <= 1 && item.Width > 0 && Math.Max(item.Width, item.Height) <= maxSize)
            return await File.ReadAllBytesAsync(item.Path, ct);

        var file = await StorageFile.GetFileFromPathAsync(item.Path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, maxSize / (double)Math.Max(1, Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight)));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        return await EncodeAsync(bitmap);
    }

    public static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, float quality = 0.9f)
    {
        using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        encoder.SetSoftwareBitmap(opaque);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.AsStreamForRead().ReadExactlyAsync(bytes);
        return bytes;
    }
}
