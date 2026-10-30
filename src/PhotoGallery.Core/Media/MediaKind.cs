namespace PhotoGallery.Core.Media;

public enum MediaKind
{
    Photo = 1,
    Video = 2,
    Raw = 3,
}

/// <summary>How a still's motion (Live Photo / Motion Photo) can be played, if at all.</summary>
public enum MotionSource
{
    None = 0,
    /// <summary>iPhone still paired with a local .MOV by Apple content identifier.</summary>
    LocalPair = 1,
    /// <summary>Android motion photo: MP4 embedded at <c>MotionOffset</c>/<c>MotionLength</c> in the file.</summary>
    Embedded = 2,
    /// <summary>iPhone Live Photo whose video exists only in OneDrive (Graph content?format=video).</summary>
    Cloud = 3,
    /// <summary>Looked like a Live Photo but OneDrive had no video for it.</summary>
    CloudMissing = 4,
}

public enum DateSource
{
    FileModified = 0,
    FileName = 1,
    Container = 2,
    Exif = 3,
}

/// <summary>Where an item's Latitude/Longitude came from.</summary>
public enum LocationSource
{
    /// <summary>The file's own GPS (or no location at all).</summary>
    File = 0,
    /// <summary>Estimated from a located photo taken around the same time (<see cref="Places.LocationEstimator"/>); never written to files.</summary>
    Estimated = 1,
    /// <summary>An estimate removed by hand: no location, and none is estimated again.</summary>
    Removed = 2,
}
