using System.Runtime.InteropServices.WindowsRuntime;
using PhotoGallery.Core;
using PhotoGallery.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoGallery.App.Services;

/// <summary>
/// Measures how sharp every photo looks (<see cref="Sharpness"/>) from its 360 px thumbnail, in the background, newest
/// first, for Blurry photos. Thumbnails are made (at background priority) when missing. Events are raised on a
/// background thread.
/// </summary>
public sealed class SharpnessService(AppServices services)
{
    private const int Workers = 4;
    private readonly SemaphoreSlim _wake = new(0);
    private int _started;
    private int _measured;

    public event Action? StateChanged;

    public bool IsWorking { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Wakes the background work (e.g. after indexing found new photos).</summary>
    public void Nudge()
    {
        if (_started == 1) _wake.Release();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            var batch = services.Media.GetSharpnessBacklog(200);
            if (batch.Count == 0)
            {
                IsWorking = false;
                StateChanged?.Invoke();
                await _wake.WaitAsync(TimeSpan.FromMinutes(30));
                continue;
            }
            IsWorking = true;
            var scores = new System.Collections.Concurrent.ConcurrentBag<(long, double)>();
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                async (job, _) => scores.Add((job.Id, await MeasureAsync(job.Id, job.Path))));
            services.Media.SetSharpness(scores.ToList());
            if ((_measured += scores.Count) % 10_000 < scores.Count) Log.Info($"Measured the sharpness of {_measured:N0} photos");
            StateChanged?.Invoke();
        }
    }

    /// <summary>The photo's score, or -1 when it has no thumbnail (so it isn't tried again until the file changes).</summary>
    private async Task<double> MeasureAsync(long id, string path)
    {
        try
        {
            if (await services.Thumbnails.GetOrCreateAsync(id, path, background: true) is not { } thumbnail) return -1;
            var file = await StorageFile.GetFileFromPathAsync(thumbnail);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            bitmap.CopyToBuffer(buffer);
            return Sharpness.Score(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Info($"Couldn't measure the sharpness of {path}: {ex.GetType().Name} {ex.Message}");
            return -1;
        }
    }
}
