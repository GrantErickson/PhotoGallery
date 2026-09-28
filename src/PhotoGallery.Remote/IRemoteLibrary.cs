using PhotoGallery.Core.Data;
using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Ocr;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.Remote;

/// <summary>What the host's library offers the remote server (the app implements it over its own services).</summary>
public interface IRemoteLibrary
{
    /// <summary>The host computer's name, shown on the sign-in page and in the page title.</summary>
    string Name { get; }

    /// <summary>Whether other computers may change things (delete, tags, albums, people); read every request.</summary>
    bool AllowChanges { get; }

    // ---------- Reading ----------

    List<MediaSummary> Query(MediaFilter filter);

    /// <summary>A search's matches, best first: exact words only, or best matches (pictures too); Pictures says which ran.</summary>
    Task<(List<long> Ids, bool Pictures)> SearchAsync(string text, bool exact, CancellationToken ct);

    /// <summary>People, places and tags whose names contain the text, for the search box.</summary>
    List<(string Text, string Kind)> Suggest(string text);

    MediaItem? Get(long id);

    /// <summary>The rest of what the viewer's details show.</summary>
    Task<MediaDetails> GetDetailsAsync(MediaItem item, CancellationToken ct);

    List<PersonRow> GetPeople(bool includeHidden);

    List<AlbumRow> GetAlbums();

    List<TagRow> GetTags();

    List<FolderRow> GetFolders();

    /// <summary>Every located photo and video, for the map.</summary>
    List<(long Id, double Latitude, double Longitude)> GetGeoPoints();

    /// <summary>Places by name for the map: on the host, or (online) from OpenStreetMap; IOException if that fails.</summary>
    Task<List<PhotoGallery.Core.Places.PlaceHit>> SearchPlacesAsync(string query, bool online, CancellationToken ct);

    /// <summary>The items most like this one, most similar first (not including it).</summary>
    Task<List<long>> FindSimilarAsync(long id, CancellationToken ct);

    /// <summary>Finding duplicates: a scan runs on the host in the background; its state and the groups found.</summary>
    DuplicateScan Duplicates { get; }

    /// <summary>The grid thumbnail's file (JPEG), made if needed.</summary>
    Task<string?> GetThumbnailAsync(MediaItem item, CancellationToken ct);

    /// <summary>
    /// A square crop of a person's face (JPEG); until it's been made, their cover photo (Final false: ask again later).
    /// </summary>
    Task<(string? Path, bool Final)> GetFaceAsync(long personId, CancellationToken ct);

    /// <summary>The photo as a JPEG no bigger than this (upright, colours in sRGB, edits applied), or null.</summary>
    Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct);

    /// <summary>The video of a Live Photo or motion photo.</summary>
    Task<(MotionResult Result, string? Path)> GetMotionAsync(MediaItem item, CancellationToken ct);

    // ---------- Changing (ratings always; the rest only while AllowChanges) ----------

    void SetRating(IReadOnlyCollection<long> ids, int rating);

    /// <summary>To the host's Recycle Bin (a Live Photo's video with its photo), then out of the library.</summary>
    Task<(List<long> Deleted, List<string> Failed)> DeleteAsync(IReadOnlyCollection<long> ids);

    void AddTag(IReadOnlyCollection<long> ids, string name);

    void RemoveTag(IReadOnlyCollection<long> ids, long tagId);

    long CreateAlbum(string name);

    void RenameAlbum(long albumId, string name);

    void DeleteAlbum(long albumId);

    void AddToAlbum(long albumId, IReadOnlyCollection<long> ids);

    void RemoveFromAlbum(long albumId, IReadOnlyCollection<long> ids);

    void RenamePerson(long personId, string? name);

    void HidePerson(long personId, bool hidden);

    void MergePeople(long sourceId, long targetId);

    // ---------- Editing (previews and finding frames are read-only; saving needs AllowChanges) ----------

    /// <summary>The edits kept in the gallery for a photo (None if there are none).</summary>
    EditOperations GetEdits(MediaItem item);

    /// <summary>Whether the photo's format can be written back (JPEG and the like), for "Overwrite the original".</summary>
    bool CanOverwrite(MediaItem item);

    /// <summary>The photo with these edits, as a JPEG no bigger than this.</summary>
    Task<byte[]?> RenderEditAsync(MediaItem item, EditOperations ops, int maxSize, CancellationToken ct);

    /// <summary>The edits with light and colour set automatically (as the editor's Auto button).</summary>
    Task<EditOperations> AutoAdjustAsync(MediaItem item, EditOperations ops, CancellationToken ct);

    /// <summary>Keeps the edits in the gallery, saves a copy next to the original, or overwrites it; says what happened.</summary>
    Task<string> SaveEditAsync(MediaItem item, EditOperations ops, EditSave mode);

    /// <summary>Where the sharpest frame of a video or Live Photo is, or null if it has no video.</summary>
    Task<TimeSpan?> FindSharpestAsync(MediaItem item, CancellationToken ct);

    /// <summary>Saves the frame at this point of a video or Live Photo as a photo next to it; says what was saved.</summary>
    Task<string> SaveFrameAsync(MediaItem item, TimeSpan position);

    /// <summary>Starts saving a video (or a Live Photo's video) with these edits as an MP4 next to it.</summary>
    VideoJob StartVideoExport(MediaItem item, VideoEdits edits);
}

public enum EditSave
{
    /// <summary>In the gallery only; no file changes.</summary>
    Keep,
    /// <summary>A new photo next to the original.</summary>
    Copy,
    /// <summary>Replaces the original (which goes to the Recycle Bin first).</summary>
    Overwrite,
}

/// <summary>A video being saved on the host: its progress, then what happened.</summary>
public sealed class VideoJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public double Progress { get; set; }
    public bool Done { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public void Finish(string message) => (Message, Done) = (message, true);

    public void Fail(string error) => (Error, Done) = (error, true);
}

/// <summary>A face in a photo: who, and where (fractions of the upright picture's longer side, like OneDrive's boxes).</summary>
public sealed record FaceInPhoto(long PersonId, string Name, double X, double Y, double Width, double Height);

public sealed record TagOnPhoto(long Id, string Name, bool Yours);

public sealed record MediaDetails(
    string? Place = null,
    IReadOnlyList<(long Id, string Name)>? People = null,
    string? Text = null,
    IReadOnlyList<TranscriptParagraph>? Transcript = null,
    bool Edited = false,
    IReadOnlyList<FaceInPhoto>? Faces = null,
    IReadOnlyList<OcrLine>? TextLines = null,
    IReadOnlyList<TagOnPhoto>? Tags = null,
    IReadOnlyList<long>? Albums = null);

/// <summary>
/// A duplicate scan on the host, shared by every remote viewer: started on request, runs in the background (it reads
/// every photo's thumbnail the first time), and keeps its groups until the next scan.
/// </summary>
public sealed class DuplicateScan(Func<IProgress<DuplicateScanProgress>, CancellationToken, Task<IReadOnlyList<DuplicateGroup>>> scan)
{
    private readonly object _gate = new();
    private Task? _running;

    public DuplicateScanProgress? Progress { get; private set; }
    public IReadOnlyList<DuplicateGroup>? Groups { get; private set; }
    public DateTime? Finished { get; private set; }
    public string? Error { get; private set; }
    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>Starts a scan unless one is running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            Error = null;
            Progress = new DuplicateScanProgress("Starting", 0, 0);
            _running = Task.Run(async () =>
            {
                try
                {
                    Groups = await scan(new ProgressSink(this), CancellationToken.None);
                    Finished = DateTime.Now;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Error = ex.Message;
                }
                finally
                {
                    Progress = null;
                }
            });
        }
    }

    /// <summary>Forgets deleted items, so the groups stay true without a new scan.</summary>
    public void Forget(IReadOnlyCollection<long> ids)
    {
        if (Groups is not { } groups || ids.Count == 0) return;
        var gone = ids.ToHashSet();
        Groups = groups
            .Select(g => g with { Members = g.Members.Where(m => !gone.Contains(m.Id)).ToList() })
            .Where(g => g.Members.Count > 1)
            .ToList();
    }

    private sealed class ProgressSink(DuplicateScan owner) : IProgress<DuplicateScanProgress>
    {
        public void Report(DuplicateScanProgress value) => owner.Progress = value;
    }
}
