using System.Runtime.InteropServices.WindowsRuntime;
using PhotoGallery.Core.Imaging;
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

    /// <summary>
    /// Where the sharpest frame is (by <see cref="Sharpness"/>, on 360 px copies), e.g. to find a better picture in a
    /// Live Photo's video than its blurry still: up to <paramref name="coarse"/> frames spread over the clip, then the
    /// ones next to the best. Frames are grabbed one at a time the way <see cref="GetFrameAsync"/> does (the batch call
    /// returns neighbouring frames), so the time returned gives exactly the frame that was judged.
    /// </summary>
    public static async Task<TimeSpan> FindSharpestAsync(string videoPath, int coarse = 30)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var (width, height) = DisplaySize(videoPath) ?? (1920, 1080);
        var scale = 360.0 / Math.Max(width, height);
        int w = Math.Max(8, (int)Math.Round(width * scale)), h = Math.Max(8, (int)Math.Round(height * scale));
        var times = FrameTimes(videoPath, clip.OriginalDuration);

        var scores = new Dictionary<int, double>();
        async Task Score(int i)
        {
            if (i < 0 || i >= times.Count || scores.ContainsKey(i)) return;
            using var frame = await composition.GetThumbnailAsync(times[i], w, h, VideoFramePrecision.NearestFrame);
            var decoder = await BitmapDecoder.CreateAsync(frame);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            bitmap.CopyToBuffer(buffer);
            scores[i] = Sharpness.Score(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
        }

        var count = Math.Min(coarse, times.Count);
        var stride = (double)times.Count / count;
        for (var k = 0; k < count; k++) await Score((int)(k * stride));
        var best = scores.MaxBy(s => s.Value).Key;
        for (var i = best - (int)Math.Ceiling(stride) + 1; i < best + stride; i++) await Score(i);
        return times[scores.MaxBy(s => s.Value).Key];
    }

    /// <summary>
    /// A time just after each frame starts (frame rates vary: a Live Photo's video starts slower), or every 1/30 s for
    /// containers whose frame times can't be read.
    /// </summary>
    private static List<TimeSpan> FrameTimes(string videoPath, TimeSpan duration)
    {
        List<TimeSpan>? starts = null;
        if (MediaFormats.IsQuickTimeFamily(videoPath))
        {
            try
            {
                using var stream = File.OpenRead(videoPath);
                starts = QuickTimeReader.ReadFrameTimes(stream);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
            {
            }
        }
        var last = duration - TimeSpan.FromMilliseconds(1);
        starts = starts is { Count: > 0 } ? starts : [.. Enumerable.Range(0, (int)Math.Max(1, duration.TotalSeconds * 30)).Select(i => TimeSpan.FromSeconds(i / 30.0))];
        return starts.Select(t => t + TimeSpan.FromMilliseconds(1)).Select(t => t > last ? last : t).ToList();
    }

    /// <summary>Display width × height (rotation applied) from a QuickTime/MP4 header; null for other containers.</summary>
    internal static (int, int)? DisplaySize(string videoPath)
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
