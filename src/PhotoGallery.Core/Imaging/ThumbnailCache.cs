using PhotoGallery.Core.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoGallery.Core.Imaging;

/// <summary>
/// Disk cache of grid thumbnails (JPEG, keyed by media id). Sources: the Windows thumbnail cache when it
/// already has the file (<see cref="ShellThumbnailer"/>), otherwise a WIC decode for images or a Media
/// Foundation frame for videos. HEIC decoding is capped at ~8 files/s by the codec, so interactive
/// requests (visible tiles) take priority over background warming.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    public const int RequestedSize = 360;

    private readonly string _root;
    private readonly ShellThumbnailer _shell;
    private readonly SemaphoreSlim _fallbackGate;
    private int _interactive;

    public ThumbnailCache(string root, int shellThreads = 6, int fallbackConcurrency = 4)
    {
        _root = root;
        _shell = new ShellThumbnailer(shellThreads);
        _fallbackGate = new SemaphoreSlim(fallbackConcurrency);
    }

    /// <summary>Raised when a thumbnail can't be produced (source path, error).</summary>
    public event Action<string, Exception>? Failed;

    public string GetPath(long id) => Path.Combine(_root, (id & 0xFF).ToString("x2"), $"{id}.jpg");

    public bool TryGetCached(long id, out string path)
    {
        path = GetPath(id);
        return File.Exists(path);
    }

    /// <summary>
    /// Returns the cached thumbnail path, generating it if needed; null if nothing can render the file.
    /// Background requests yield to interactive ones.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(long id, string sourcePath, CancellationToken ct = default, bool background = false)
    {
        if (TryGetCached(id, out var target)) return target;
        if (background)
        {
            while (Volatile.Read(ref _interactive) > 0) await Task.Delay(100, ct);
        }
        else
        {
            Interlocked.Increment(ref _interactive);
        }
        try
        {
            var pixels = await _shell.GetAsync(sourcePath, RequestedSize, ct);
            if (pixels is not null)
            {
                await WriteJpegAsync(target, encoder => encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra), ct);
                return target;
            }

            await _fallbackGate.WaitAsync(ct);
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(sourcePath);
                using var bitmap = await FromCodecAsync(file) ?? await FromVideoAsync(file);
                if (bitmap is null) return null;
                using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                await WriteJpegAsync(target, encoder => encoder.SetSoftwareBitmap(opaque), ct);
                return target;
            }
            finally
            {
                _fallbackGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // WinRT surfaces codec failures as COMException
        {
            Failed?.Invoke(sourcePath, ex);
            return null;
        }
        finally
        {
            if (!background) Interlocked.Decrement(ref _interactive);
        }
    }

    private static async Task WriteJpegAsync(string target, Action<BitmapEncoder> setPixels, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        setPixels(encoder);
        await encoder.FlushAsync();
        output.Seek(0);
        var temp = target + $".{Environment.CurrentManagedThreadId}.tmp";
        await using (var fs = File.Create(temp))
            await output.AsStreamForRead().CopyToAsync(fs, ct);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Images: WIC decode scaled to thumbnail size and EXIF-oriented (~8 ms JPEG, ~130 ms 18 MP HEIC).</summary>
    private static async Task<SoftwareBitmap?> FromCodecAsync(StorageFile file)
    {
        if (!MediaFormats.TryGetKind(file.Path, out var kind) || kind == MediaKind.Video) return null;
        try
        {
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = Math.Min(1.0, RequestedSize / (double)Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Videos: a frame near the start via Media Foundation (MediaComposition).</summary>
    private static async Task<SoftwareBitmap?> FromVideoAsync(StorageFile file)
    {
        if (!MediaFormats.TryGetKind(file.Path, out var kind) || kind != MediaKind.Video) return null;
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            var at = TimeSpan.FromMilliseconds(Math.Min(1000, clip.OriginalDuration.TotalMilliseconds / 2));
            using var frame = await composition.GetThumbnailAsync(at, 0, RequestedSize, VideoFramePrecision.NearestKeyFrame);
            var decoder = await BitmapDecoder.CreateAsync(frame);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
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

    public void Dispose()
    {
        _shell.Dispose();
        _fallbackGate.Dispose();
    }
}
