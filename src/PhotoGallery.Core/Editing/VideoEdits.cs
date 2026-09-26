namespace PhotoGallery.Core.Editing;

/// <summary>Basic edits for exporting a video (or a Live Photo's motion) to MP4.</summary>
public sealed record VideoEdits
{
    /// <summary>Clockwise quarter turns: 0, 90, 180 or 270 (written as MP4 rotation metadata, not re-encoded).</summary>
    public int Rotation { get; init; }
    public TimeSpan TrimStart { get; init; }
    /// <summary>End of the kept range; null = to the end.</summary>
    public TimeSpan? TrimEnd { get; init; }
    public bool Mute { get; init; }

    public static VideoEdits None { get; } = new();

    public VideoEdits RotateClockwise() => this with { Rotation = (Rotation + 90) % 360 };

    public VideoEdits RotateCounterClockwise() => this with { Rotation = (Rotation + 270) % 360 };

    /// <summary>Length of the kept range for a video of <paramref name="duration"/>.</summary>
    public TimeSpan KeptDuration(TimeSpan duration) => (TrimEnd ?? duration) - TrimStart;
}
