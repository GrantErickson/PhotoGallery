// Headless maintenance tool: index the library, print stats, pre-build thumbnails, fetch a Live Photo video.
// Usage:
//   dotnet run --project tools/PhotoGallery.Cli -- index [root...]
//   dotnet run --project tools/PhotoGallery.Cli -- stats
//   dotnet run --project tools/PhotoGallery.Cli -- thumbs [count] [skip]
//   dotnet run --project tools/PhotoGallery.Cli -- motion <mediaId>
using System.Diagnostics;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Imaging;
using PhotoGallery.Core.Indexing;
using PhotoGallery.Core.Media;

var paths = AppPaths.Default;
paths.EnsureCreated();
Log.Initialize(paths.Root);
var settings = AppSettings.Load(paths);
var database = new GalleryDatabase(paths.Database);
database.Migrate();
var media = new MediaRepository(database);
var thumbs = new ThumbnailCache(paths.Thumbnails);
thumbs.Failed += (path, ex) => Console.WriteLine($"  thumbnail error {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");

switch (args.FirstOrDefault())
{
    case "index":
    {
        var roots = args.Length > 1 ? args[1..] : settings.LibraryRoots.ToArray();
        Console.WriteLine($"Indexing {string.Join(", ", roots)} into {paths.Database}");
        var indexer = new LibraryIndexer(database, media);
        indexer.ItemsChanged += thumbs.Invalidate;
        var progress = new Progress<IndexProgress>(p =>
            Console.Write($"\r{p.Phase,-9} found {p.Found:N0}  to read {p.ToProcess:N0}  read {p.Processed:N0}  removed {p.Removed:N0}      "));
        var result = await indexer.RunAsync(roots, progress);
        Console.WriteLine();
        Console.WriteLine($"Done in {result.Elapsed:mm\\:ss}: {result.Found:N0} files, {result.Added:N0} added, {result.Updated:N0} updated, " +
                          $"{result.Removed:N0} removed, {result.Unchanged:N0} unchanged");
        PrintStats();
        break;
    }
    case "stats":
        PrintStats();
        break;
    case "thumbs":
    {
        var count = args.Length > 1 ? int.Parse(args[1]) : 500;
        var skip = args.Length > 2 ? int.Parse(args[2]) : 0;
        var items = media.Query(MediaFilter.Timeline).Skip(skip).Take(count).ToList();
        var clock = Stopwatch.StartNew();
        int ok = 0, failed = 0;
        var perType = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, double Ms)>();
        await Parallel.ForEachAsync(items, async (s, ct) =>
        {
            var item = media.Get(s.Id)!;
            var one = Stopwatch.StartNew();
            var made = await thumbs.GetOrCreateAsync(item.Id, item.Path, ct);
            perType.AddOrUpdate(Path.GetExtension(item.Path).ToLowerInvariant(), (1, one.Elapsed.TotalMilliseconds),
                (_, v) => (v.Count + 1, v.Ms + one.Elapsed.TotalMilliseconds));
            if (made is null)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"  no thumbnail: {item.Path}");
            }
            else Interlocked.Increment(ref ok);
        });
        foreach (var (ext, (n, ms)) in perType.OrderByDescending(p => p.Value.Count))
            Console.WriteLine($"  {ext,-6} {n,5} files, {ms / n,6:F0} ms each (in parallel)");
        Console.WriteLine($"{ok} thumbnails, {failed} failed in {clock.Elapsed.TotalSeconds:F1}s ({items.Count / clock.Elapsed.TotalSeconds:F0}/s)");
        break;
    }
    case "motion":
    {
        var item = media.Get(long.Parse(args[1])) ?? throw new ArgumentException("No such media id");
        var oneDrive = new OneDriveClient(settings.ClientId, paths.TokenCache);
        if (!await oneDrive.TrySignInSilentAsync()) await oneDrive.SignInAsync();
        // The OneDrive web session lives in the app (WebView2); the CLI can only resolve local and embedded motion.
        var service = new MotionVideoService(media, oneDrive, new OneDriveLiveVideoClient(new NoWebSession()), settings, paths.MotionCache);
        var (result, file) = await service.GetVideoAsync(item);
        Console.WriteLine($"{item.Path} [{item.Motion}] -> {result} {file}");
        break;
    }
    case "cloudsync":
    {
        var oneDrive = new OneDriveClient(settings.ClientId, paths.TokenCache);
        if (!await oneDrive.TrySignInSilentAsync()) { Console.WriteLine("not signed in"); break; }
        var sync = new OneDriveMetadataSync(oneDrive, database, media, settings);
        var result = await sync.RunAsync(new Progress<MetadataSyncProgress>(p => Console.Write($"\rread {p.ItemsRead:N0}, matched {p.Matched:N0}   ")));
        Console.WriteLine();
        Console.WriteLine($"{result.ItemsRead:N0} items, {result.Matched:N0} in library, {result.Tagged:N0} tagged, {result.People:N0} people in {result.Elapsed:mm\\:ss}");
        break;
    }
    case "probe":
    {
        var oneDrive = new OneDriveClient(settings.ClientId, paths.TokenCache);
        if (!await oneDrive.TrySignInSilentAsync()) { Console.WriteLine("not signed in"); break; }
        foreach (var arg in args[1..])
        {
            var parts = arg.Split('|', 2);
            Console.WriteLine($"=== {parts[0]} {(parts.Length > 1 ? "[Accept " + parts[1] + "]" : "")}");
            Console.WriteLine(await oneDrive.ProbeAsync(parts[0], parts.Length > 1 ? parts[1] : null));
        }
        break;
    }
    default:
        Console.WriteLine("Commands: index [root...] | stats | thumbs [count] | motion <mediaId>");
        break;
}

void PrintStats()
{
    var s = media.GetStats();
    Console.WriteLine($"Photos {s.Photos:N0}, videos {s.Videos:N0}, screenshots {s.Screenshots:N0}");
    Console.WriteLine($"Motion: local pairs {s.LocalPairs:N0}, embedded {s.Embedded:N0}, cloud Live Photos {s.Cloud:N0}");
}

sealed class NoWebSession : IOneDriveWebToken
{
    public bool IsConnected => false;

    public Task<string?> GetAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult<string?>(null);
}
