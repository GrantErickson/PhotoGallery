namespace PhotoGallery.Core.Media;

/// <summary>A row of the Media table.</summary>
public sealed class MediaItem
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public long FileModified { get; set; }
    public MediaKind Kind { get; set; }
    /// <summary>Local wall-clock time taken, stored as Unix seconds (as if UTC).</summary>
    public long DateTaken { get; set; }
    public DateSource DateSource { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Orientation { get; set; }
    public long DurationMs { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsScreenshot { get; set; }
    public string? ContentId { get; set; }
    public long MotionOffset { get; set; }
    public long MotionLength { get; set; }
    public MotionSource Motion { get; set; }
    public long? PairedId { get; set; }
    public bool IsHidden { get; set; }
    public int Rating { get; set; }
    public string? OneDriveItemId { get; set; }
    /// <summary>Indexed while the file was a cloud-only placeholder (name/date only).</summary>
    public bool OnlineOnly { get; set; }

    public DateTime TakenLocal => DateTime.UnixEpoch.AddSeconds(DateTaken);
}
