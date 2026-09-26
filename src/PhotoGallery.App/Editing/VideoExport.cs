using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Metadata;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PhotoGallery.App.Editing;

/// <summary>
/// Writes a video (or a Live Photo's motion) as an H.264/AAC MP4 with <see cref="VideoEdits"/> applied: trim and mute
/// through Media Foundation, then capture time, rotation and location patched into the MP4 (Windows' encoder can't
/// rotate inside a composition, and rotation metadata is lossless anyway).
/// </summary>
public static class VideoExport
{
    public static async Task ExportAsync(string sourcePath, VideoEdits edits, string destination, DateTime takenLocal,
        (double Latitude, double Longitude)? location, IProgress<double>? progress, CancellationToken ct)
    {
        var source = await StorageFile.GetFileFromPathAsync(sourcePath);
        var clip = await MediaClip.CreateFromFileAsync(source);
        var props = clip.GetVideoEncodingProperties(); // display size, rotation already applied
        var duration = clip.OriginalDuration;
        clip.TrimTimeFromStart = Clamp(edits.TrimStart, duration);
        if (edits.TrimEnd is { } end && end < duration) clip.TrimTimeFromEnd = duration - Clamp(end, duration);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        // The display size from the file's header (1920×1080), not the coded size Media Foundation reports for
        // HEVC (1920×1088 with green padding rows). H.264 needs even sizes.
        var (displayWidth, displayHeight) = VideoFrames.DisplaySize(sourcePath) is var (dw, dh) && Math.Abs(dw * dh - (int)(props.Width * props.Height)) < props.Width * 32
            ? ((uint)dw, (uint)dh)
            : (props.Width, props.Height);
        var width = displayWidth & ~1u;
        var height = displayHeight & ~1u;
        var fps = props.FrameRate.Denominator > 0 ? (double)props.FrameRate.Numerator / props.FrameRate.Denominator : 30;
        var rate = (uint)Math.Clamp(Math.Round(fps), 24, 60);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = rate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Min(60_000_000, width * height * rate * 0.12);
        if (edits.Mute || clip.EmbeddedAudioTracks.Count == 0) profile.Audio = null;

        // Render beside the destination under a name the indexer ignores, then patch and move into place.
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var temp = await folder.CreateFileAsync($".{Path.GetFileName(destination)}.partial", CreationCollisionOption.ReplaceExisting);
        try
        {
            var render = composition.RenderToFileAsync(temp, MediaTrimmingPreference.Precise, profile);
            render.Progress = (_, percent) => progress?.Report(percent / 100);
            TranscodeFailureReason result;
            using (ct.Register(() => render.Cancel()))
            {
                try
                {
                    result = await render;
                }
                catch (TaskCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }
            }
            if (result != TranscodeFailureReason.None) throw new InvalidOperationException($"Video encoding failed ({result}).");

            // A video's copy starts later by the trim; a Live Photo's clip belongs to the photo's moment.
            var takenUtc = DateTime.SpecifyKind(takenLocal, DateTimeKind.Local).ToUniversalTime();
            Mp4Metadata.Patch(temp.Path, takenUtc, edits.Rotation, location);
            File.Move(temp.Path, destination);
        }
        finally
        {
            if (File.Exists(temp.Path)) File.Delete(temp.Path);
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan duration) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > duration ? duration : value;

    /// <summary>"D:\x\IMG_1234.MOV" → "D:\x\IMG_1234_1.mp4"; for a Live Photo "D:\x\IMG_1234.HEIC" → "…\IMG_1234_live.mp4".</summary>
    public static string NextPath(string namedAfter, bool livePhoto)
    {
        var folder = Path.GetDirectoryName(namedAfter)!;
        var stem = Path.GetFileNameWithoutExtension(namedAfter);
        if (livePhoto)
        {
            var first = Path.Combine(folder, $"{stem}_live.mp4");
            if (!File.Exists(first)) return first;
        }
        for (var n = livePhoto ? 2 : 1; ; n++)
        {
            var candidate = Path.Combine(folder, livePhoto ? $"{stem}_live_{n}.mp4" : $"{stem}_{n}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
