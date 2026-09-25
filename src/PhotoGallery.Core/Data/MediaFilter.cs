using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Data;

public enum KindFilter
{
    All,
    Photos,
    Videos,
}

/// <summary>
/// The shared query definition (the plan's FilterContext) used by the grid, folders, albums, search and
/// later the map. Immutable so view models can compare/replace it.
/// </summary>
public sealed record MediaFilter
{
    public KindFilter Kinds { get; init; } = KindFilter.All;
    public bool IncludeScreenshots { get; init; }
    public bool MotionOnly { get; init; }
    public int MinRating { get; init; }
    public long? FolderId { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public long? AlbumId { get; init; }
    public long? TagId { get; init; }
    public string? Text { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>"On this day": match month/day across all years.</summary>
    public (int Month, int Day)? MonthDay { get; init; }
    public (double South, double West, double North, double East)? Bounds { get; init; }

    public static MediaFilter Timeline { get; } = new();
}

/// <summary>What the grid needs per item. Kept small: the timeline loads all rows for the current filter.</summary>
public sealed class MediaSummary
{
    public long Id { get; set; }
    public MediaKind Kind { get; set; }
    public long DateTaken { get; set; }
    public MotionSource Motion { get; set; }
    public int Rating { get; set; }
    public long DurationMs { get; set; }
    public bool IsScreenshot { get; set; }

    public DateTime TakenLocal => DateTime.UnixEpoch.AddSeconds(DateTaken);
}
