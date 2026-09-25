using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Metadata;

public sealed class MediaMetadata
{
    public DateTime? Taken { get; set; }
    public DateSource DateSource { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Orientation { get; set; }
    public long DurationMs { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Apple Live Photo content identifier (still MakerNote or MOV metadata).</summary>
    public string? ContentId { get; set; }
    public long MotionOffset { get; set; }
    public long MotionLength { get; set; }
    /// <summary>iOS writes EXIF UserComment "Screenshot" on screenshots.</summary>
    public bool ScreenshotHint { get; set; }
}

/// <summary>Extracts the metadata the gallery indexes. Never throws for a bad file; returns what it could read.</summary>
public static class MediaMetadataReader
{
    public static MediaMetadata Read(string path, MediaKind kind)
    {
        var result = new MediaMetadata();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
            if (kind == MediaKind.Video)
                ReadVideo(stream, path, result);
            else
                ReadImage(stream, path, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageProcessingException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            // Corrupt or unsupported: fall through to the name/mtime date below.
        }

        if (result.Taken is null && FileNameDates.Parse(Path.GetFileName(path)) is { } fromName && IsPlausible(fromName))
            (result.Taken, result.DateSource) = (fromName, DateSource.FileName);
        return result;
    }

    private static void ReadVideo(Stream stream, string path, MediaMetadata result)
    {
        if (!MediaFormats.IsQuickTimeFamily(path) || QuickTimeReader.Read(stream) is not { } qt) return;

        result.DurationMs = qt.DurationMs;
        result.Width = qt.Width;
        result.Height = qt.Height;
        result.ContentId = qt.ContentIdentifier;
        result.Make = qt.Make;
        result.Model = qt.Model;
        result.Latitude = qt.Latitude;
        result.Longitude = qt.Longitude;
        if (qt.AppleCreationDate is { } apple && IsPlausible(apple.DateTime))
            (result.Taken, result.DateSource) = (apple.DateTime, DateSource.Container); // wall clock where it was shot
        else if (qt.CreatedUtc is { } utc && IsPlausible(utc.ToLocalTime()))
            (result.Taken, result.DateSource) = (utc.ToLocalTime(), DateSource.Container);
    }

    private static void ReadImage(Stream stream, string path, MediaMetadata result)
    {
        var directories = ImageMetadataReader.ReadMetadata(stream);

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken) && IsPlausible(taken))
            (result.Taken, result.DateSource) = (taken, DateSource.Exif);
        else if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out taken) && IsPlausible(taken))
            (result.Taken, result.DateSource) = (taken, DateSource.Exif);

        if (sub?.GetDescription(ExifDirectoryBase.TagUserComment) is { } comment && comment.Contains("Screenshot", StringComparison.OrdinalIgnoreCase))
            result.ScreenshotHint = true;

        if (ifd0 is not null)
        {
            result.Make = Clean(ifd0.GetString(ExifDirectoryBase.TagMake));
            result.Model = Clean(ifd0.GetString(ExifDirectoryBase.TagModel));
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation)) result.Orientation = orientation;
        }

        (result.Width, result.Height) = FindDimensions(directories, sub);
        // EXIF orientations 5-8 are rotated 90/270 degrees; store display dimensions.
        if (result.Orientation is >= 5 and <= 8) (result.Width, result.Height) = (result.Height, result.Width);

        var gps = directories.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
        if (gps is { IsZero: false } g && !double.IsNaN(g.Latitude))
            (result.Latitude, result.Longitude) = (g.Latitude, g.Longitude);

        result.ContentId = Clean(directories.OfType<AppleMakernoteDirectory>().FirstOrDefault()
            ?.GetString(AppleMakernoteDirectory.TagContentIdentifier));

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".heic" && MotionPhotoLocator.Locate(stream) is var (offset, length))
            (result.MotionOffset, result.MotionLength) = (offset, length);
    }

    private static (int, int) FindDimensions(IReadOnlyList<MetadataExtractor.Directory> directories, ExifSubIfdDirectory? sub)
    {
        if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) &&
            sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) && w > 0 && h > 0)
            return (w, h);

        // Fall back to the largest "Image Width/Height" any format directory reports (JPEG, PNG, HEIC ispe, BMP, GIF...).
        var best = (0, 0);
        foreach (var dir in directories)
        {
            int? dw = null, dh = null;
            foreach (var tag in dir.Tags)
            {
                if (tag.Name.EndsWith("Image Width", StringComparison.Ordinal) && dir.TryGetInt32(tag.Type, out var tw)) dw = tw;
                else if (tag.Name.EndsWith("Image Height", StringComparison.Ordinal) && dir.TryGetInt32(tag.Type, out var th)) dh = th;
            }
            if (dw is > 0 && dh is > 0 && (long)dw.Value * dh.Value > (long)best.Item1 * best.Item2)
                best = (dw.Value, dh.Value);
        }
        return best;
    }

    /// <summary>Rejects corrupt dates (the library has JPEGs whose EXIF says 3913) so the next source is used.</summary>
    internal static bool IsPlausible(DateTime taken) => taken.Year >= 1900 && taken <= DateTime.Now.AddDays(2);

    private static string? Clean(string? s)
    {
        s = s?.Trim('\0', ' ');
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
