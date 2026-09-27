using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Data;

public enum KindFilter
{
    All,
    Photos,
    Videos,
}

public enum MediaOrder
{
    Newest,
    /// <summary>Least sharp first (only photos with a sharpness score).</summary>
    Blurriest,
    /// <summary>In the order of <see cref="MediaFilter.Ids"/> (e.g. most similar first).</summary>
    Listed,
}

/// <summary>
/// The shared query definition (the plan's FilterContext) used by the grid, folders, albums, search and
/// later the map. Immutable so view models can compare/replace it.
/// </summary>
public sealed record MediaFilter
{
    public KindFilter Kinds { get; init; } = KindFilter.All;
    public bool IncludeScreenshots { get; init; }
    /// <summary>Nothing but screenshots.</summary>
    public bool ScreenshotsOnly { get; init; }
    public bool MotionOnly { get; init; }
    /// <summary>Only items with edits kept in the gallery (not yet written to a file).</summary>
    public bool EditedOnly { get; init; }
    /// <summary>Only photos measured as less sharp than this (<see cref="Imaging.Sharpness"/>).</summary>
    public double? SharpnessBelow { get; init; }
    public MediaOrder Order { get; init; }
    public int MinRating { get; init; }
    public long? FolderId { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public long? AlbumId { get; init; }
    public long? TagId { get; init; }
    /// <summary>Photos OneDrive recognised this person in.</summary>
    public long? PersonId { get; init; }
    /// <summary>Photos at one of your places.</summary>
    public long? PlaceId { get; init; }
    public string? Text { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>"On this day": match month/day across all years.</summary>
    public (int Month, int Day)? MonthDay { get; init; }
    public (double South, double West, double North, double East)? Bounds { get; init; }
    /// <summary>An explicit set of items (e.g. the photos in a map cluster).</summary>
    public IReadOnlyCollection<long>? Ids { get; init; }

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
    public bool IsEdited { get; set; }
    /// <summary>A copy saved from the editor (Media.DerivedFromId is set).</summary>
    public bool IsDerived { get; set; }
    /// <summary>First item of a new day in the current (date-ordered) list; set by the view.</summary>
    public bool StartsDay { get; set; }
    /// <summary>
    /// Where the faces are (the middle of the box around all of them), packed as x·10⁹ + y·10⁴ with x, y in
    /// ten-thousandths of the upright picture's long side; null without face boxes. See <see cref="Focus"/>.
    /// </summary>
    public long? FaceFocus { get; set; }

    /// <summary>The faces' middle as fractions of the long side, if known (for cropping tiles around people).</summary>
    public (double X, double Y)? Focus => FaceFocus is { } f ? (f / 100000 / 10000.0, f % 100000 / 10000.0) : null;

    public DateTime TakenLocal => DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(DateTaken), DateTimeKind.Unspecified);
}
