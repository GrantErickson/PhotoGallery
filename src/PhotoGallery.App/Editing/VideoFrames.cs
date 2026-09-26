using PhotoGallery.Core.Media;
using PhotoGallery.Core.Metadata;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace PhotoGallery.App.Editing;

/// <summary>Grabs single frames from videos (any format Media Foundation plays) and saves them as photos.</summary>
public static class VideoFrames
{
    /// <summary>
    /// The frame shown at <paramref name="position"/>, at the video's full resolution and display orientation
    /// (Media Foundation applies the rotation), independent of what the on-screen player has rendered.
    /// </summary>
    public static async Task<SoftwareBitmap> GetFrameAsync(string videoPath, TimeSpan position)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var last = clip.OriginalDuration - TimeSpan.FromMilliseconds(1);
        var at = position < TimeSpan.Zero ? TimeSpan.Zero : position > last ? last : position;
        // Asking for the display size (from the file's own track header) returns just the picture; asking for the
        // native size returns the coded frame, e.g. 1920×1088 for 1080p HEVC, with 8 green padding rows at the bottom.
        var (width, height) = DisplaySize(videoPath) ?? (0, 0); // 0×0 = native size
        using var frame = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(frame);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>Display width × height (rotation applied) from a QuickTime/MP4 header; null for other containers.</summary>
    private static (int, int)? DisplaySize(string videoPath)
    {
        if (!MediaFormats.IsQuickTimeFamily(videoPath)) return null;
        try
        {
            using var stream = File.OpenRead(videoPath);
            return QuickTimeReader.Read(stream) is { Width: > 0, Height: > 0 } info ? (info.Width, info.Height) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// "D:\x\IMG_1234.MOV" at 2.345 s → "D:\x\IMG_1234_frame_0m02.345s.jpg" (with _2, _3… if that exists).
    /// Frames from a Live Photo are named after (and saved next to) the photo.
    /// </summary>
    public static string SnapshotPath(string namedAfter, TimeSpan position)
    {
        var folder = Path.GetDirectoryName(namedAfter)!;
        var stem = $"{Path.GetFileNameWithoutExtension(namedAfter)}_frame_{(int)position.TotalMinutes}m{position.Seconds:00}.{position.Milliseconds:000}s";
        var candidate = Path.Combine(folder, stem + ".jpg");
        for (var n = 2; File.Exists(candidate); n++) candidate = Path.Combine(folder, $"{stem}_{n}.jpg");
        return candidate;
    }

    /// <summary>
    /// Saves the frame at <paramref name="position"/> of <paramref name="videoPath"/> next to <paramref name="item"/>,
    /// with the item's camera and location, and a date taken of the video's start plus the position.
    /// </summary>
    public static async Task<string> SaveSnapshotAsync(MediaItem item, string videoPath, TimeSpan position, bool isLivePhoto)
    {
        using var bitmap = await GetFrameAsync(videoPath, position);
        var target = SnapshotPath(item.Path, position);
        // A Live Photo's clip surrounds the still, so its date is the photo's; a video's frame is start + position.
        var taken = isLivePhoto ? item.TakenLocal : item.TakenLocal + position;
        var location = item is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : ((double, double)?)null;
        await EditRenderer.SaveBitmapAsync(bitmap, target, taken, location, EditRenderer.CameraMetadata(item.CameraMake, item.CameraModel));
        return target;
    }
}
