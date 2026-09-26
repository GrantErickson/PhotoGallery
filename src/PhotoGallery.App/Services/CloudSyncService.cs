using System.Globalization;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App.Services;

/// <summary>Keeps OneDrive tags and people up to date: after sign-in, daily, or on request.</summary>
public sealed class CloudSyncService(AppServices services)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private int _running;

    /// <summary>Status text for the status bar / settings (raised on a background thread).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when new tags/people were stored; views should re-query.</summary>
    public event Action? Completed;

    public bool IsRunning => _running == 1;

    public DateTime? LastSync =>
        DateTime.TryParse(services.Media.GetSyncValue(OneDriveMetadataSync.LastSyncKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;

    /// <summary>Syncs if signed in and the last sync is older than a day.</summary>
    public void SyncIfStale()
    {
        if (services.OneDrive.IsSignedIn && (LastSync is null || DateTime.UtcNow - LastSync > MaxAge)) Start();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                StatusChanged?.Invoke("Reading tags and people from OneDrive…");
                var sync = new OneDriveMetadataSync(services.OneDrive, services.Database, services.Media, services.Settings);
                var result = await sync.RunAsync(new Progress<MetadataSyncProgress>(p =>
                    StatusChanged?.Invoke($"Reading tags and people from OneDrive… {p.ItemsRead:N0} items")));
                Log.Info($"OneDrive metadata sync: {result}");
                StatusChanged?.Invoke($"OneDrive tags updated · {result.Tagged:N0} photos tagged · {result.People:N0} people");
                Completed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("OneDrive metadata sync failed", ex);
                StatusChanged?.Invoke($"Couldn't read tags from OneDrive: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }
}
