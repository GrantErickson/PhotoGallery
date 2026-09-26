using System.Collections.Concurrent;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;

namespace PhotoGallery.Core.Media;

public enum MotionResult
{
    Ready,
    None,
    /// <summary>The OneDrive web session needs connecting (Settings) before cloud Live Photos can play.</summary>
    NeedsSignIn,
    /// <summary>The video exists but can't be fetched right now (offline, not a OneDrive file, or it failed validation).</summary>
    Unavailable,
}

/// <summary>Resolves a playable video file for a still's motion: local pair, embedded MP4, or OneDrive.</summary>
public sealed class MotionVideoService
{
    private readonly MediaRepository _media;
    private readonly OneDriveClient _graph;
    private readonly OneDriveLiveVideoClient _liveVideo;
    private readonly AppSettings _settings;
    private readonly string _cacheDirectory;
    private readonly bool _cacheAllowed;
    private readonly ConcurrentDictionary<long, Lazy<Task<(MotionResult, string?)>>> _inFlight = new();

    public MotionVideoService(MediaRepository media, OneDriveClient graph, OneDriveLiveVideoClient liveVideo, AppSettings settings, string cacheDirectory)
    {
        (_media, _graph, _liveVideo, _settings, _cacheDirectory) = (media, graph, liveVideo, settings, cacheDirectory);
        // Never cache inside a OneDrive folder: the videos would sync back up as new files.
        _cacheAllowed = !OneDriveRoots.IsUnder(cacheDirectory, OneDriveRoots.Find());
        if (!_cacheAllowed) Log.Error($"Motion cache {cacheDirectory} is inside a OneDrive folder; cloud Live Photos are disabled");
    }

    public async Task<(MotionResult Result, string? Path)> GetVideoAsync(MediaItem item, CancellationToken ct = default)
    {
        switch (item.Motion)
        {
            case MotionSource.LocalPair when item.PairedId is { } videoId && _media.Get(videoId) is { } video && File.Exists(video.Path):
                return (MotionResult.Ready, video.Path);

            case MotionSource.Embedded:
                var embedded = Path.Combine(_cacheDirectory, $"{item.Id}.mp4");
                if (!File.Exists(embedded)) await ExtractAsync(item, embedded, ct);
                return (MotionResult.Ready, embedded);

            case MotionSource.Cloud:
                var cached = Path.Combine(_cacheDirectory, $"{item.Id}.mov");
                if (File.Exists(cached)) return (MotionResult.Ready, cached);
                // A prefetch and a LIVE click for the same photo share one download.
                var download = _inFlight.GetOrAdd(item.Id, _ => new Lazy<Task<(MotionResult, string?)>>(() => DownloadCloudAsync(item, cached)));
                try
                {
                    return await download.Value.WaitAsync(ct);
                }
                finally
                {
                    if (download.Value.IsCompleted) _inFlight.TryRemove(item.Id, out _);
                }

            default:
                return (MotionResult.None, null);
        }
    }

    private async Task<(MotionResult, string?)> DownloadCloudAsync(MediaItem item, string cached)
    {
        if (!_cacheAllowed || _settings.ToOneDrivePath(item.Path) is null) return (MotionResult.Unavailable, null); // not a OneDrive file
        if (!_liveVideo.IsConnected) return (MotionResult.NeedsSignIn, null);
        if (await GetItemIdAsync(item, CancellationToken.None) is not { } itemId) return (MotionResult.Unavailable, null);

        Directory.CreateDirectory(_cacheDirectory);
        switch (await _liveVideo.DownloadAsync(itemId, cached, item.ContentId))
        {
            case LiveVideoStatus.Downloaded:
                try
                {
                    File.SetLastWriteTime(cached, File.GetLastWriteTime(item.Path));
                }
                catch (IOException)
                {
                }
                return (MotionResult.Ready, cached);
            case LiveVideoStatus.NotConnected:
                return (MotionResult.NeedsSignIn, null);
            case LiveVideoStatus.NotLivePhoto:
                // Remembered, so the app doesn't ask again for photos that aren't Live Photos.
                _media.SetMotion(item.Id, MotionSource.CloudMissing);
                item.Motion = MotionSource.CloudMissing;
                return (MotionResult.None, null);
            default:
                return (MotionResult.Unavailable, null);
        }
    }

    /// <summary>The OneDrive item id for a local file (same id Graph and the web app use), looked up once and stored.</summary>
    public async Task<string?> GetItemIdAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.OneDriveItemId is { } known) return known;
        if (_settings.ToOneDrivePath(item.Path) is not { } remote) return null;
        var id = (_graph.IsSignedIn || await _graph.TrySignInSilentAsync(ct)) && await _graph.GetItemAsync(remote, ct) is { } found
            ? found.Id
            : await _liveVideo.FindItemIdAsync(remote, ct);
        if (id is null) return null;
        _media.SetOneDriveItemId(item.Id, id);
        item.OneDriveItemId = id;
        return id;
    }

    private static async Task ExtractAsync(MediaItem item, string destination, CancellationToken ct)
    {
        var temp = destination + ".part";
        await using (var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var target = File.Create(temp))
        {
            source.Seek(item.MotionOffset, SeekOrigin.Begin);
            var remaining = item.MotionLength;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        File.Move(temp, destination, overwrite: true);
    }
}
