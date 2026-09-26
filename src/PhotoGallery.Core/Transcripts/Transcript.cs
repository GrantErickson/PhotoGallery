using System.Text.Json.Serialization;

namespace PhotoGallery.Core.Transcripts;

public enum TranscriptStatus
{
    Done = 0,
    /// <summary>The audio has no speech (or only a moment of it).</summary>
    NoSpeech = 1,
    /// <summary>The video has no sound track.</summary>
    NoAudio = 2,
    Failed = 3,
}

/// <summary>A stretch of speech with its times (seconds from the start of the video).</summary>
public sealed record TranscriptSegment(
    [property: JsonPropertyName("s")] double Start,
    [property: JsonPropertyName("e")] double End,
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("k")] int? Speaker = null);

/// <summary>What was said in a video: the segments as recognised, in time order.</summary>
public sealed record Transcript(
    long MediaId,
    TranscriptStatus Status,
    IReadOnlyList<TranscriptSegment> Segments,
    string? Language = null,
    string Model = "",
    string? Error = null)
{
    public bool HasSpeech => Status == TranscriptStatus.Done && Segments.Count > 0;
}

/// <summary>Sentences grouped for reading, with when they start (for seeking) and who spoke, if known.</summary>
public sealed record TranscriptParagraph(double Start, double End, string Text, int? Speaker);
