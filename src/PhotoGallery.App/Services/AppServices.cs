using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Imaging;
using PhotoGallery.Core.Media;

namespace PhotoGallery.App.Services;

/// <summary>Composition root: one instance of each service for the app's lifetime.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        Paths = AppPaths.Default;
        Paths.EnsureCreated();
        Log.Initialize(Paths.Root);
        Settings = AppSettings.Load(Paths);
        Database = new GalleryDatabase(Paths.Database);
        Database.Migrate();
        Media = new MediaRepository(Database);
        Collections = new CollectionRepository(Database);
        Edits = new EditRepository(Database);
        People = new PeopleRepository(Database);
        Transcripts = new TranscriptRepository(Database);
        PhotoTexts = new PhotoTextRepository(Database);
        Embeddings = new EmbeddingRepository(Database);
        Places = new PlaceRepository(Database);
        Pois = new PoiRepository(Database);
        PlaceNames = new PlaceNameService(this);
        Faces = new Imaging.FaceCropper(Path.Combine(Paths.Root, "faces"));
        Thumbnails = new ThumbnailCache(Paths.Thumbnails);
        Thumbnails.Failed += (path, ex) => Log.Error($"Thumbnail failed for {path}", ex);
        // Edited photos get thumbnails with their edits applied.
        Thumbnails.Renderer = async (id, path, ct) =>
            Edits.Get(id) is { } ops ? await Editing.EditRenderer.RenderPreviewAsync(path, ops, ThumbnailCache.RequestedSize) : null;
        OneDrive = new OneDriveClient(Settings.ClientId, Paths.TokenCache);
        WebSession = new OneDriveWebSession(this);
        LiveVideo = new OneDriveLiveVideoClient(WebSession);
        PeopleOnline = new OneDrivePeopleClient(WebSession);
        Motion = new MotionVideoService(Media, OneDrive, LiveVideo, Settings, Paths.MotionCache);
        Indexing = new IndexingService(this);
        CloudSync = new CloudSyncService(this);
        Transcription = new TranscriptionService(this);
        PhotoText = new PhotoTextService(this);
        Sharpness = new SharpnessService(this);
        Similar = new EmbeddingService(this);
        PlacesOnline = new PoiService(this);
        Media.Removed += ForgetRemoved;
    }

    /// <summary>Drops what's cached under the ids of items that left the library, before new files reuse the ids.</summary>
    private void ForgetRemoved(IReadOnlyCollection<long> ids)
    {
        Thumbnails.Invalidate(ids);
        Similar.Forget(ids);
        foreach (var id in ids)
            foreach (var extension in new[] { ".mov", ".mp4" })
            {
                try
                {
                    File.Delete(Path.Combine(Paths.MotionCache, id + extension));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public GalleryDatabase Database { get; }
    public MediaRepository Media { get; }
    public CollectionRepository Collections { get; }
    public EditRepository Edits { get; }
    public PeopleRepository People { get; }
    public TranscriptRepository Transcripts { get; }
    public TranscriptionService Transcription { get; }
    public PhotoTextRepository PhotoTexts { get; }
    public PhotoTextService PhotoText { get; }
    public SharpnessService Sharpness { get; }
    public EmbeddingRepository Embeddings { get; }
    /// <summary>Similar photos and searching by description (CLIP).</summary>
    public EmbeddingService Similar { get; }
    public PlaceRepository Places { get; }
    /// <summary>Named places from OpenStreetMap (parks, schools, restaurants…) and which photos were taken at them.</summary>
    public PoiRepository Pois { get; }
    public PoiService PlacesOnline { get; }
    public PlaceNameService PlaceNames { get; }
    public Imaging.FaceCropper Faces { get; }
    public CloudSyncService CloudSync { get; }
    public ThumbnailCache Thumbnails { get; }
    public OneDriveClient OneDrive { get; }
    public MotionVideoService Motion { get; }
    public OneDriveWebSession WebSession { get; }
    public OneDriveLiveVideoClient LiveVideo { get; }
    public OneDrivePeopleClient PeopleOnline { get; }
    public IndexingService Indexing { get; }

    public void SaveSettings() => Settings.Save(Paths);
}
