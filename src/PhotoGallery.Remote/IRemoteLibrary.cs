using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.Remote;

/// <summary>What the host's library offers the remote server (the app implements it over its own services).</summary>
public interface IRemoteLibrary
{
    /// <summary>The host computer's name, shown on the sign-in page and in the page title.</summary>
    string Name { get; }

    List<MediaSummary> Query(MediaFilter filter);

    /// <summary>A search's matches, best first: exact words only, or best matches (pictures too); Pictures says which ran.</summary>
    Task<(List<long> Ids, bool Pictures)> SearchAsync(string text, bool exact, CancellationToken ct);

    MediaItem? Get(long id);

    /// <summary>The rest of what the viewer's details show.</summary>
    Task<MediaDetails> GetDetailsAsync(MediaItem item, CancellationToken ct);

    List<PersonRow> GetPeople();

    List<AlbumRow> GetAlbums();

    void SetRating(long id, int rating);

    /// <summary>The grid thumbnail's file (JPEG), made if needed.</summary>
    Task<string?> GetThumbnailAsync(MediaItem item, CancellationToken ct);

    /// <summary>A square crop of a person's face (JPEG), or null.</summary>
    Task<string?> GetFaceAsync(long personId, CancellationToken ct);

    /// <summary>The photo as a JPEG no bigger than this (upright, colours in sRGB, edits applied), or null.</summary>
    Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct);

    /// <summary>The video of a Live Photo or motion photo.</summary>
    Task<(MotionResult Result, string? Path)> GetMotionAsync(MediaItem item, CancellationToken ct);
}

public sealed record MediaDetails(
    string? Place = null,
    IReadOnlyList<(long Id, string Name)>? People = null,
    string? Text = null,
    IReadOnlyList<TranscriptParagraph>? Transcript = null,
    bool Edited = false);
