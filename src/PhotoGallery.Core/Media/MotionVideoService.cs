using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;

namespace PhotoGallery.Core.Media;

public enum MotionResult
{
    Ready,
    None,
    NeedsSignIn,
    Unavailable,
}

/// <summary>Resolves a playable video file for a still's motion: local pair, embedded MP4, or OneDrive.</summary>
public sealed class MotionVideoService(MediaRepository media, OneDriveClient oneDrive, AppSettings settings, string cacheDirectory)
{
    public async Task<(MotionResult Result, string? Path)> GetVideoAsync(MediaItem item, CancellationToken ct = default)
    {
        switch (item.Motion)
        {
            case MotionSource.LocalPair when item.PairedId is { } videoId && media.Get(videoId) is { } video && File.Exists(video.Path):
                return (MotionResult.Ready, video.Path);

            case MotionSource.Embedded:
                var embedded = Path.Combine(cacheDirectory, $"{item.Id}.mp4");
                if (!File.Exists(embedded)) await ExtractAsync(item, embedded, ct);
                return (MotionResult.Ready, embedded);

            case MotionSource.Cloud:
                var cached = Path.Combine(cacheDirectory, $"{item.Id}.mov");
                if (File.Exists(cached)) return (MotionResult.Ready, cached);
                if (settings.ToOneDrivePath(item.Path) is not { } remote) return (MotionResult.Unavailable, null);
                switch (await oneDrive.DownloadLiveVideoAsync(remote, cached, ct))
                {
                    case LiveVideoStatus.Downloaded:
                        return (MotionResult.Ready, cached);
                    case LiveVideoStatus.NotSignedIn:
                        return (MotionResult.NeedsSignIn, null);
                    case LiveVideoStatus.NotLivePhoto:
                        media.SetMotion(item.Id, MotionSource.CloudMissing);
                        item.Motion = MotionSource.CloudMissing;
                        return (MotionResult.None, null);
                    default:
                        return (MotionResult.Unavailable, null);
                }

            default:
                return (MotionResult.None, null);
        }
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
