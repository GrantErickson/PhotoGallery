using System.Collections.Concurrent;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Ocr;

namespace PhotoGallery.App.Services;

/// <summary>
/// Reads the text in photos (OCR): the one being viewed straight away, the rest of the library in the background
/// (screenshots and text-like photos first) when that's switched on, a few at a time on the CPU. Events are raised on
/// a background thread.
/// </summary>
public sealed class PhotoTextService(AppServices services)
{
    /// <summary>Readers at once (decoding and OCR scale well; leave room for the rest of the PC).</summary>
    private static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 5, 2, 6);
    private readonly ConcurrentDictionary<long, Task<PhotoText?>> _inFlight = new();
    private readonly ConcurrentBag<PhotoTextReader> _readers = [];
    private readonly SemaphoreSlim _wake = new(0);
    private int _started;
    private int _read;

    /// <summary>A photo's text was read (possibly none).</summary>
    public event Action<PhotoText>? Completed;
    public event Action? StateChanged;

    public bool IsAvailable { get; } = new PhotoTextReader().IsAvailable;
    public bool IsWorking { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Wakes the background work (e.g. after it was switched on).</summary>
    public void Nudge()
    {
        Start();
        _wake.Release();
        StateChanged?.Invoke();
    }

    /// <summary>The photo's text, reading it now if needed; null for videos and files that can't be read here.</summary>
    public Task<PhotoText?> RequestAsync(long mediaId)
    {
        var task = _inFlight.GetOrAdd(mediaId, id => Task.Run(() => ReadAsync(id)));
        _ = task.ContinueWith(_ => _inFlight.TryRemove(new KeyValuePair<long, Task<PhotoText?>>(mediaId, task)), TaskScheduler.Default);
        return task;
    }

    private async Task<PhotoText?> ReadAsync(long mediaId)
    {
        var item = services.Media.Get(mediaId);
        if (item is null || item.Kind == MediaKind.Video || item.OnlineOnly) return null; // cloud-only placeholders are never read
        if (services.PhotoTexts.Get(mediaId) is { } existing) return existing;

        if (!_readers.TryTake(out var reader)) reader = new PhotoTextReader();
        PhotoText text;
        try
        {
            text = await reader.ReadAsync(mediaId, item.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Can't be decoded here (an unsupported format, a damaged file): remember that, so it isn't retried.
            Log.Info($"Couldn't read text in {item.Path}: {ex.GetType().Name} {ex.Message}");
            text = new PhotoText(mediaId, [], PhotoTextReader.EngineName);
        }
        finally
        {
            _readers.Add(reader);
        }
        services.PhotoTexts.Save(text);
        if (Interlocked.Increment(ref _read) % 1000 == 0) Log.Info($"Read text in {_read:N0} photos");
        Completed?.Invoke(text);
        return text;
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            if (!services.Settings.ReadPhotoTextInBackground || !IsAvailable)
            {
                await _wake.WaitAsync();
                continue;
            }
            var batch = services.PhotoTexts.GetBacklog(200);
            if (batch.Count == 0)
            {
                IsWorking = false;
                StateChanged?.Invoke();
                await _wake.WaitAsync(TimeSpan.FromMinutes(30)); // new photos arrive with indexing
                continue;
            }
            IsWorking = true;
            StateChanged?.Invoke();
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                async (job, _) => await RequestAsync(job.MediaId));
            StateChanged?.Invoke();
        }
    }
}
