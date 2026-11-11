using System.Security.Cryptography;
using System.Text;
using PhotoGallery.App.Editing;
using PhotoGallery.App.Services;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoGallery.App;

/// <summary>
/// Photos and videos handed to other apps (PowerPoint, Word, a chat, a folder…) by copying or dragging them, as files
/// those apps can open. JPEG, PNG, GIF and BMP photos go as they are, and so do videos. Other photos (HEIC, RAW, TIFF,
/// WebP…), photos stored sideways (turned by their EXIF orientation), and photos with edits kept in the gallery go as an
/// upright JPEG made for the purpose. It looks as the photo does here and keeps the date taken, the camera and the
/// file's own location.
/// </summary>
public static class Handoff
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "PhotoGallery", "Handoff");

    /// <summary>Photos other apps open as they are.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    /// <summary>One JPEG is made at a time: full-size photos are big, and the same one may be asked for twice at once.</summary>
    private static readonly SemaphoreSlim Making = new(1, 1);

    private static bool _cleanedUp;

    private static AppServices S => App.Services;

    /// <summary>The items, looked up (any that are gone are left out).</summary>
    public static Task<List<MediaItem>> ItemsAsync(IReadOnlyList<long> ids) =>
        Task.Run(() => ids.Select(S.Media.Get).OfType<MediaItem>().ToList());

    /// <summary>
    /// The files for these items: their own, or JPEGs made from them. Items that can't be read (gone, or no codec) are
    /// left out and counted. <paramref name="progress"/> hears how many are done whenever a JPEG has to be made.
    /// </summary>
    public static async Task<(List<string> Files, int Failed)> FilesAsync(IReadOnlyList<MediaItem> items, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var files = new List<string>(items.Count);
        var failed = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                files.Add(await FileAsync(items[i], () => progress?.Report(i), ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PhotoGallery.Core.Log.Error($"Couldn't hand over {items[i].Path}", ex);
                failed++;
            }
        }
        return (files, failed);
    }

    /// <summary>The file to hand over for an item. A JPEG made for it is kept, and used again until the photo or its edits change.</summary>
    private static async Task<string> FileAsync(MediaItem item, Action making, CancellationToken ct)
    {
        if (!File.Exists(item.Path)) throw new FileNotFoundException("The file is gone.", item.Path);
        var edits = await Task.Run(() => S.Edits.Get(item.Id), ct);
        var asItIs = item.Kind == MediaKind.Video ||
                     (Common.Contains(Path.GetExtension(item.Path)) && item.Orientation <= 1 && edits is null or { IsIdentity: true });
        if (asItIs) return item.Path;

        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.FileModified}|{item.FileSize}|{edits?.ToJson()}")))[..12];
        var folder = Path.Combine(Folder, $"{item.Id}-{stamp}");
        var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(item.Path) + ".jpg");
        await Making.WaitAsync(ct);
        try
        {
            if (!_cleanedUp)
            {
                _cleanedUp = true;
                await Task.Run(RemoveOld, ct);
            }
            if (File.Exists(target)) return target;
            making();
            Directory.CreateDirectory(folder);
            var part = Path.Combine(folder, $".{Guid.NewGuid():N}.jpg");
            try
            {
                // Off the UI thread: decoding and encoding a full-size photo takes a moment.
                await Task.Run(() => EditRenderer.WriteFileAsync(item.Path, edits ?? EditOperations.None, part, item.TakenLocal, item.FileLocation), ct);
                File.Move(part, target);
            }
            finally
            {
                File.Delete(part);
            }
            return target;
        }
        finally
        {
            Making.Release();
        }
    }

    /// <summary>JPEGs made in earlier sessions: whatever they were pasted or dropped into has its own copy by now.</summary>
    private static void RemoveOld()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var dir in Directory.EnumerateDirectories(Folder))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-2))
                    Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<IReadOnlyList<StorageFile>> StorageFilesAsync(IEnumerable<string> paths) =>
        await Task.WhenAll(paths.Select(p => StorageFile.GetFileFromPathAsync(p).AsTask()));

    /// <summary>
    /// Offers the items as files in a drag. They're got ready (any JPEGs made) only when the app they're dropped on
    /// asks for them, so the drag starts at once.
    /// </summary>
    public static void OfferLater(DataPackage data, IReadOnlyList<long> ids)
    {
        // Copy only: allowed to move, File Explorer would move the originals out of the library.
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetDataProvider(StandardDataFormats.StorageItems, request => _ = ProvideAsync(request, ids));
    }

    private static async Task ProvideAsync(DataProviderRequest request, IReadOnlyList<long> ids)
    {
        var deferral = request.GetDeferral();
        try
        {
            var (files, _) = await FilesAsync(await ItemsAsync(ids));
            request.SetData(await StorageFilesAsync(files));
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error("Couldn't hand over dragged files", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>A small picture of the item to drag (from its thumbnail), or null if there's none yet.</summary>
    public static async Task<SoftwareBitmap?> DragPictureAsync(long id, int size = 160)
    {
        if (!S.Thumbnails.TryGetCached(id, out var thumb)) return null;
        var file = await StorageFile.GetFileFromPathAsync(thumb);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, size / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    }
}
