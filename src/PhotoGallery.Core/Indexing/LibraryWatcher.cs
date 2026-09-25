using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Indexing;

/// <summary>
/// Watches the library roots and raises <see cref="Changed"/> once activity settles. The indexer is
/// incremental, so a change simply triggers another run; FileSystemWatcher can drop events, which is why
/// the app also re-indexes on startup.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private readonly TimeSpan _quietPeriod;

    public LibraryWatcher(IEnumerable<string> roots, TimeSpan? quietPeriod = null)
    {
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(10);
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var root in roots.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error += (_, _) => Poke(); // buffer overflow: we lost events, so just re-index
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public event Action? Changed;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // Directory events and media files matter; ignore temp/sidecar churn.
        if (MediaFormats.TryGetKind(e.FullPath, out _) || !Path.HasExtension(e.FullPath)) Poke();
    }

    private void Poke() => _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce.Dispose();
    }
}
