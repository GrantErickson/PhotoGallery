using System.Globalization;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App.Services;

/// <summary>
/// Keeps OneDrive tags and people up to date: after sign-in, daily, or on request. Tags come through Graph; people,
/// their names and face positions through the OneDrive web session (a full read of every photo's faces monthly or
/// on request, otherwise just the newest photos).
/// </summary>
public sealed class CloudSyncService(AppServices services)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan FullScanAge = TimeSpan.FromDays(30);
    private int _running;

    /// <summary>Status text for the status bar / settings (raised on a background thread).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when new tags/people were stored; views should re-query.</summary>
    public event Action? Completed;

    public bool IsRunning => _running == 1;

    public DateTime? LastSync => SyncTime(OneDriveMetadataSync.LastSyncKey);
    public DateTime? LastFaceSync => SyncTime(OneDriveFaceSync.LastSyncKey);
    public DateTime? LastFullFaceScan => SyncTime(OneDriveFaceSync.LastFullScanKey);

    private DateTime? SyncTime(string key) =>
        DateTime.TryParse(services.Media.GetSyncValue(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;

    private static bool Stale(DateTime? last, TimeSpan age) => last is null || DateTime.UtcNow - last > age;

    /// <summary>Syncs whatever is connected and older than a day.</summary>
    public void SyncIfStale()
    {
        var tags = services.OneDrive.IsSignedIn && Stale(LastSync, MaxAge);
        var people = services.PeopleOnline.IsConnected && Stale(LastFaceSync, MaxAge);
        if (tags || people) Start(tags: tags, people: people);
    }

    /// <param name="fullFaceScan">Re-read every photo's faces rather than only the newest.</param>
    public void Start(bool fullFaceScan = false, bool tags = true, bool people = true)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            var messages = new List<string>();
            try
            {
                if (tags && services.OneDrive.IsSignedIn)
                {
                    try
                    {
                        StatusChanged?.Invoke("Reading tags from OneDrive…");
                        var sync = new OneDriveMetadataSync(services.OneDrive, services.Database, services.Media, services.Settings);
                        // Once people come from the web session, the list's pre-merge person ids would only add duplicates.
                        var result = await sync.RunAsync(includePeople: LastFaceSync is null, new Progress<MetadataSyncProgress>(p =>
                            StatusChanged?.Invoke($"Reading tags from OneDrive… {p.ItemsRead:N0} items")));
                        Log.Info($"OneDrive metadata sync: {result}");
                        messages.Add($"{result.Tagged:N0} photos tagged");
                        Completed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("OneDrive metadata sync failed", ex);
                        messages.Add($"couldn't read tags: {ex.Message}");
                    }
                }

                if (people && services.PeopleOnline.IsConnected)
                {
                    try
                    {
                        var full = fullFaceScan || Stale(LastFullFaceScan, FullScanAge);
                        var verb = full ? "Reading people and faces from OneDrive" : "Checking OneDrive for new faces";
                        StatusChanged?.Invoke($"{verb}…");
                        var sync = new OneDriveFaceSync(services.PeopleOnline, services.Database, services.Media, services.Settings);
                        var result = await sync.RunAsync(full, new Progress<FaceSyncProgress>(p =>
                            StatusChanged?.Invoke($"{verb}… {p.PhotosRead:N0} photos")));
                        Log.Info($"OneDrive face sync: {result}");
                        services.Faces.Clear(); // avatars are re-cropped from the new face boxes
                        messages.Add($"{result.People:N0} people, {result.Named:N0} named");
                        Completed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("OneDrive face sync failed", ex);
                        messages.Add($"couldn't read people: {ex.Message}");
                    }
                }

                StatusChanged?.Invoke(messages.Count == 0 ? "" : "OneDrive: " + string.Join(" · ", messages));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }
}
