using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Indexing;

public enum IndexPhase
{
    Scanning,
    Reading,
    Finishing,
    Done,
}

public sealed record IndexProgress(IndexPhase Phase, int Found, int ToProcess, int Processed, int Removed, string? Current);

public sealed record IndexResult(int Found, int Added, int Updated, int Removed, int Unchanged, TimeSpan Elapsed);

/// <summary>
/// Incremental indexer: enumerate files → compare size + mtime with the DB → read metadata in parallel
/// for new/changed files → one writer batches upserts → remove missing files → recompute Live Photo pairing.
/// </summary>
public sealed class LibraryIndexer(GalleryDatabase database, MediaRepository media)
{
    private const int BatchSize = 500;

    /// <summary>Bump when metadata extraction rules change; every file is then re-read once.</summary>
    public const int MetadataVersion = 2;
    private const string MetadataVersionKey = "MetadataVersion";

    /// <summary>Raised (from the writer thread) with the ids of files whose content changed, so caches can drop them.</summary>
    public event Action<IReadOnlyList<long>>? ItemsChanged;

    public async Task<IndexResult> RunAsync(IReadOnlyList<string> roots, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var existing = media.GetIndexState();
        var rereadAll = media.GetSyncValue(MetadataVersionKey) != MetadataVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var seen = new HashSet<long>();
        var work = new List<(string Path, string Root, long Size, long Modified, MediaKind Kind, bool IsNew, bool FileChanged, bool OnlineOnly)>();
        var found = 0;

        var reportClock = Stopwatch.StartNew();
        var reportLock = new Lock();
        void Report(IndexPhase phase, int processed, int removed, string? current, bool force = false)
        {
            if (progress is null) return;
            lock (reportLock)
            {
                if (!force && reportClock.ElapsedMilliseconds < 200) return;
                reportClock.Restart();
            }
            progress.Report(new IndexProgress(phase, found, work.Count, processed, removed, current));
        }

        var scannedRoots = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue; // an unavailable drive must not delete its index
            scannedRoots.Add(Path.TrimEndingDirectorySeparator(root));
            foreach (var file in EnumerateMedia(root))
            {
                ct.ThrowIfCancellationRequested();
                found++;
                if (existing.TryGetValue(file.Path, out var known))
                {
                    seen.Add(known.Id);
                    var fileChanged = known.FileSize != file.Size || known.FileModified != file.Modified;
                    // A placeholder that has since been downloaded gets its real metadata read now.
                    var nowLocal = known.OnlineOnly && !file.OnlineOnly;
                    if (!rereadAll && !fileChanged && !nowLocal) continue;
                    work.Add((file.Path, root, file.Size, file.Modified, file.Kind, false, fileChanged, file.OnlineOnly));
                }
                else
                {
                    work.Add((file.Path, root, file.Size, file.Modified, file.Kind, true, true, file.OnlineOnly));
                }
                Report(IndexPhase.Scanning, 0, 0, file.Path);
            }
        }
        Report(IndexPhase.Scanning, 0, 0, null, force: true);

        // Only remove files that lived under a root we actually scanned.
        var missing = existing
            .Where(e => !seen.Contains(e.Value.Id) && scannedRoots.Any(r => IsUnder(e.Key, r)))
            .Select(e => e.Value.Id)
            .ToList();

        var channel = Channel.CreateBounded<(MediaItem Item, string Root, bool IsNew, bool FileChanged)>(new BoundedChannelOptions(BatchSize * 4)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var processed = 0;
        int added = 0, updated = 0;

        var writer = Task.Run(() =>
        {
            using var db = database.Open();
            var folders = media.GetFolderIds();
            var batch = new List<(MediaItem Item, string Root, bool IsNew, bool FileChanged)>(BatchSize);
            var reader = channel.Reader;
            while (reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
            {
                while (batch.Count < BatchSize && reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count == 0) continue;
                using (var tx = db.BeginTransaction())
                {
                    foreach (var (item, root, _, _) in batch)
                    {
                        var folder = Path.GetDirectoryName(item.Path)!;
                        item.FolderId = media.EnsureFolder(db, tx, folders, folder, root);
                        media.Upsert(db, tx, item, folder);
                    }
                    tx.Commit();
                }
                // Only files whose bytes changed invalidate caches; a metadata-version re-read does not.
                var changed = batch.Where(b => !b.IsNew && b.FileChanged).Select(b => b.Item.Id).ToList();
                if (changed.Count > 0) ItemsChanged?.Invoke(changed);
                added += batch.Count(b => b.IsNew);
                updated += batch.Count(b => !b.IsNew);
                batch.Clear();
            }
        }, CancellationToken.None);

        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                async (file, token) =>
                {
                    var item = BuildItem(file.Path, file.Size, file.Modified, file.Kind, file.OnlineOnly);
                    await channel.Writer.WriteAsync((item, file.Root, file.IsNew, file.FileChanged), token);
                    var done = Interlocked.Increment(ref processed);
                    Report(IndexPhase.Reading, done, 0, file.Path);
                });
        }
        finally
        {
            channel.Writer.Complete();
            await writer;
        }

        Report(IndexPhase.Finishing, processed, missing.Count, null, force: true);
        if (missing.Count > 0)
        {
            using var db = database.Open();
            using var tx = db.BeginTransaction();
            media.Delete(db, tx, missing);
            tx.Commit();
            ItemsChanged?.Invoke(missing);
        }
        if (work.Count > 0 || missing.Count > 0)
            media.RecomputeMotion();
        if (rereadAll && scannedRoots.Count > 0)
            media.SetSyncValue(MetadataVersionKey, MetadataVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = new IndexResult(found, added, updated, missing.Count, found - work.Count, clock.Elapsed);
        progress?.Report(new IndexProgress(IndexPhase.Done, found, work.Count, processed, missing.Count, null));
        return result;
    }

    /// <summary>Indexes one file right away (e.g. a copy just saved by the editor); null if it isn't a media file.</summary>
    public MediaItem? IndexFile(string path, string root)
    {
        if (!MediaFormats.TryGetKind(path, out var kind) || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        var item = BuildItem(path, info.Length, info.LastWriteTimeUtc.Ticks, kind, CloudFiles.IsOnlineOnly(info.Attributes));
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var folder = Path.GetDirectoryName(path)!;
        item.FolderId = media.EnsureFolder(db, tx, media.GetFolderIds(), folder, root);
        media.Upsert(db, tx, item, folder);
        tx.Commit();
        return item;
    }

    internal static MediaItem BuildItem(string path, long size, long modifiedTicks, MediaKind kind, bool onlineOnly = false)
    {
        // Never read a cloud-only placeholder: that would make OneDrive download the whole file.
        var md = onlineOnly ? MediaMetadataReader.FromNameOnly(path) : MediaMetadataReader.Read(path, kind);
        var taken = md.Taken ?? new DateTime(modifiedTicks, DateTimeKind.Utc).ToLocalTime();
        return new MediaItem
        {
            Path = path,
            FileName = Path.GetFileName(path),
            FileSize = size,
            FileModified = modifiedTicks,
            Kind = kind,
            DateTaken = MediaRepository.ToUnix(taken),
            DateSource = md.Taken is null ? DateSource.FileModified : md.DateSource,
            Width = md.Width,
            Height = md.Height,
            Orientation = md.Orientation,
            DurationMs = md.DurationMs,
            CameraMake = md.Make,
            CameraModel = md.Model,
            Latitude = md.Latitude,
            Longitude = md.Longitude,
            IsScreenshot = IsScreenshot(path, kind, md),
            ContentId = md.ContentId,
            MotionOffset = md.MotionOffset,
            MotionLength = md.MotionLength,
            Motion = md.MotionLength > 0 ? MotionSource.Embedded : MotionSource.None,
            OnlineOnly = onlineOnly,
        };
    }

    internal static bool IsScreenshot(string path, MediaKind kind, MediaMetadata md)
    {
        if (kind == MediaKind.Video) return false;
        if (md.ScreenshotHint) return true;
        if (path.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("screen shot", StringComparison.OrdinalIgnoreCase))
            return true;
        // Phone screenshots are PNGs without any camera EXIF.
        return Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && md.Make is null && md.Model is null;
    }

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == Path.DirectorySeparatorChar;

    internal readonly record struct FoundFile(string Path, long Size, long Modified, MediaKind Kind, bool OnlineOnly);

    internal static IEnumerable<FoundFile> EnumerateMedia(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };
        return new FileSystemEnumerable<FoundFile?>(root,
            (ref FileSystemEntry e) =>
            {
                var name = e.FileName;
                var dot = name.LastIndexOf('.');
                if (dot < 0 || !MediaFormats.TryGetKind(name[dot..].ToString(), out var kind)) return null;
                return new FoundFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcTicks, kind, CloudFiles.IsOnlineOnly(e.Attributes));
            }, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        }.Where(f => f is not null).Select(f => f!.Value);
    }
}
