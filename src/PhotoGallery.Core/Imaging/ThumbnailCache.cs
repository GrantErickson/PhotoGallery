using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace PhotoGallery.Core.Imaging;

/// <summary>
/// Disk cache of grid thumbnails (JPEG, keyed by media id). Thumbnails come from the Windows shell, which
/// covers every format that has a codec installed (HEIC, RAW, video frames...) with EXIF orientation applied.
/// </summary>
public sealed class ThumbnailCache(string root, int maxConcurrency = 4)
{
    public const uint RequestedSize = 360;
    private readonly SemaphoreSlim _gate = new(maxConcurrency);

    public string GetPath(long id) => Path.Combine(root, (id & 0xFF).ToString("x2"), $"{id}.jpg");

    public bool TryGetCached(long id, out string path)
    {
        path = GetPath(id);
        return File.Exists(path);
    }

    /// <summary>Returns the cached thumbnail path, generating it if needed; null if Windows can't render the file.</summary>
    public async Task<string?> GetOrCreateAsync(long id, string sourcePath, CancellationToken ct = default)
    {
        if (TryGetCached(id, out var target)) return target;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target)) return target;
            ct.ThrowIfCancellationRequested();

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, RequestedSize, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Type != ThumbnailType.Image) return null; // an icon means no codec

            var decoder = await BitmapDecoder.CreateAsync(thumb);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tmp";
            using (var output = new InMemoryRandomAccessStream())
            {
                var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85, Windows.Foundation.PropertyType.Single) };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
                encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
                await encoder.FlushAsync();
                output.Seek(0);
                await using var fs = File.Create(temp);
                await output.AsStreamForRead().CopyToAsync(fs, ct);
            }
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // WinRT surfaces codec failures as COMException
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                File.Delete(GetPath(id));
            }
            catch (IOException)
            {
            }
        }
    }
}
