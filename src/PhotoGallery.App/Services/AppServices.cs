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
        Thumbnails = new ThumbnailCache(Paths.Thumbnails);
        Thumbnails.Failed += (path, ex) => Log.Error($"Thumbnail failed for {path}", ex);
        OneDrive = new OneDriveClient(Settings.ClientId, Paths.TokenCache);
        Motion = new MotionVideoService(Media, OneDrive, Settings, Paths.MotionCache);
        Indexing = new IndexingService(this);
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public GalleryDatabase Database { get; }
    public MediaRepository Media { get; }
    public CollectionRepository Collections { get; }
    public ThumbnailCache Thumbnails { get; }
    public OneDriveClient OneDrive { get; }
    public MotionVideoService Motion { get; }
    public IndexingService Indexing { get; }

    public void SaveSettings() => Settings.Save(Paths);
}
