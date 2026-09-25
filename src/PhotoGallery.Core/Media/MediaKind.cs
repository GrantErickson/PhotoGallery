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
