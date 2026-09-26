using PhotoGallery.Core.Data;
using PhotoGallery.Core.Indexing;

namespace PhotoGallery.App.Services;

/// <summary>
/// Runs the incremental indexer on startup and whenever the watcher sees changes (coalescing requests that
/// arrive mid-run), then warms grid thumbnails in the background. Events are raised on background threads.
/// </summary>
public sealed class IndexingService(AppServices services)
{
    private readonly Lock _gate = new();
    private LibraryWatcher? _watcher;
    private Task? _running;
    private bool _pending;
    private CancellationTokenSource _warmCts = new();

    public event Action<IndexProgress>? ProgressChanged;
    /// <summary>The library contents changed; views should re-query.</summary>
    public event Action? LibraryChanged;
    public event Action<string?>? StatusChanged;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        RestartWatcher();
        RequestIndex();
    }

    public void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new LibraryWatcher(services.Settings.LibraryRoots);
        _watcher.Changed += RequestIndex;
    }

    /// <summary>Indexes a single new file immediately (under whichever library root contains it).</summary>
    public PhotoGallery.Core.Media.MediaItem? IndexFileNow(string path)
    {
        var root = services.Settings.LibraryRoots.FirstOrDefault(r =>
            path.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        // Copies land in an already-indexed folder, so that folder can stand in for the root (e.g. roots given as 8.3 paths).
        root ??= Path.GetDirectoryName(path);
        if (root is null) return null;
        var item = new LibraryIndexer(services.Database, services.Media).IndexFile(path, root);
        if (item is not null) LibraryChanged?.Invoke();
        return item;
    }

    public void RequestIndex()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                _pending = true;
                return;
            }
            _running = Task.Run(RunLoopAsync);
        }
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            await _warmCts.CancelAsync();
            IsRunning = true;
            IndexResult? result = null;
            try
            {
                var indexer = new LibraryIndexer(services.Database, services.Media);
                indexer.ItemsChanged += services.Thumbnails.Invalidate;
                result = await indexer.RunAsync(services.Settings.LibraryRoots, new SyncProgress(p => ProgressChanged?.Invoke(p)));
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Indexing failed: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
            }

            if (result is not null)
            {
                StatusChanged?.Invoke(result.Added + result.Updated + result.Removed == 0
                    ? $"Library up to date · {result.Found:N0} files"
                    : $"Indexed {result.Found:N0} files · {result.Added:N0} new, {result.Updated:N0} changed, {result.Removed:N0} removed");
                if (result.Added + result.Updated + result.Removed > 0) LibraryChanged?.Invoke();
            }

            lock (_gate)
            {
                if (!_pending)
                {
                    _warmCts = new CancellationTokenSource();
                    _ = Task.Run(() => WarmThumbnailsAsync(_warmCts.Token));
                    return;
                }
                _pending = false;
            }
        }
    }

    /// <summary>Pre-builds thumbnails newest-first so scrolling back through years is instant.</summary>
    private async Task WarmThumbnailsAsync(CancellationToken ct)
    {
        try
        {
            var items = services.Media.Query(new MediaFilter { IncludeScreenshots = true });
            var missing = items.Where(i => !services.Thumbnails.TryGetCached(i.Id, out _)).Select(i => i.Id).ToList();
            if (missing.Count == 0) return;
            var done = 0;
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, token) =>
            {
                if (services.Media.GetPath(id) is { } path) await services.Thumbnails.GetOrCreateAsync(id, path, token, background: true);
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0 || n == missing.Count) StatusChanged?.Invoke($"Building thumbnails {n:N0} / {missing.Count:N0}");
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Progress&lt;T&gt; posts to the captured context; the pipeline has none, so report synchronously.</summary>
    private sealed class SyncProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }
}
