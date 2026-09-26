using PhotoGallery.App.Transcription;
using PhotoGallery.Core;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.App.Services;

/// <summary>
/// Transcribes videos one at a time: the one being watched first (on request), otherwise the rest of the library,
/// newest first, when background transcription is on. Downloads the speech model on first use and releases it when
/// idle. Events are raised on a background thread.
/// </summary>
public sealed class TranscriptionService(AppServices services)
{
    /// <summary>The video being transcribed, so one that takes the app down is skipped next time.</summary>
    private const string InProgressKey = "TranscribingNow";
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(2);

    private readonly SpeechTranscriber _transcriber = new(services.Paths.Models);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    /// <summary>Requested videos, most recent last (served first).</summary>
    private readonly List<(long MediaId, TaskCompletionSource<Transcript?> Done)> _requests = [];
    private int _started;
    private long? _currentId;
    private double _currentProgress;
    private double? _downloadProgress;
    private string? _lastError;

    /// <summary>(video, 0–1) while a video is transcribed.</summary>
    public event Action<long, double>? Progress;
    /// <summary>A transcript was stored (requested or from the background work).</summary>
    public event Action<Transcript>? Completed;
    /// <summary>Status text or counts changed.</summary>
    public event Action? StateChanged;

    public long? CurrentId => _currentId;
    public double CurrentProgress => _currentProgress;
    /// <summary>0–1 while the speech model downloads.</summary>
    public double? DownloadProgress => _downloadProgress;
    public string? Runtime => _transcriber.Runtime;

    public string Status => _downloadProgress is { } d ? $"Downloading the speech model (1.6 GB)… {d:P0}"
        : _lastError is { } e ? $"Paused: {e}"
        : _currentId is not null ? "Transcribing…"
        : services.Settings.TranscribeInBackground ? "Up to date" : "Background transcription is off";

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Wakes the worker (e.g. after background transcription was switched on).</summary>
    public void Nudge()
    {
        Start();
        _wake.Release();
        StateChanged?.Invoke();
    }

    /// <summary>Transcribes this video next (ahead of the background work); null if it can't be read.</summary>
    public Task<Transcript?> RequestAsync(long mediaId)
    {
        Start();
        TaskCompletionSource<Transcript?> done;
        lock (_gate)
        {
            var existing = _requests.FindIndex(r => r.MediaId == mediaId);
            if (existing >= 0)
            {
                // Asked again (opened again): move it to the front.
                var request = _requests[existing];
                _requests.RemoveAt(existing);
                _requests.Add(request);
                return request.Done.Task;
            }
            done = new TaskCompletionSource<Transcript?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add((mediaId, done));
        }
        _wake.Release();
        return done.Task;
    }

    private async Task WorkAsync()
    {
        SkipCrashedVideo();
        while (true)
        {
            var (job, request) = NextJob();
            if (job is null)
            {
                // Nothing to do: free the GPU memory if it stays that way.
                if (!await _wake.WaitAsync(IdleUnload))
                {
                    _transcriber.Unload();
                    await _wake.WaitAsync();
                }
                continue;
            }

            if (!await EnsureModelsAsync())
            {
                request?.TrySetResult(null);
                await _wake.WaitAsync(TimeSpan.FromMinutes(10)); // try the download again later
                continue;
            }

            var transcript = await TranscribeAsync(job);
            request?.TrySetResult(transcript);
            // Let the other waiting requests for the same video (if any) finish too.
            if (transcript is not null)
            {
                lock (_gate)
                    foreach (var other in _requests.Where(r => r.MediaId == job.MediaId).ToList())
                    {
                        other.Done.TrySetResult(transcript);
                        _requests.Remove(other);
                    }
            }
        }
    }

    private (TranscriptionJob? Job, TaskCompletionSource<Transcript?>? Request) NextJob()
    {
        while (true)
        {
            (long MediaId, TaskCompletionSource<Transcript?> Done) request;
            lock (_gate)
            {
                if (_requests.Count == 0) break;
                request = _requests[^1];
                _requests.RemoveAt(_requests.Count - 1);
            }
            var item = services.Media.Get(request.MediaId);
            if (item is null || item.OnlineOnly) // cloud-only placeholders are never read
            {
                request.Done.TrySetResult(null);
                continue;
            }
            if (services.Transcripts.Get(item.Id) is { } existing)
            {
                request.Done.TrySetResult(existing);
                continue;
            }
            return (new TranscriptionJob(item.Id, item.Path, item.DurationMs), request.Done);
        }
        if (!services.Settings.TranscribeInBackground || _lastError is not null) return (null, null);
        return (services.Transcripts.GetBacklog(1, SpeechTranscriber.ModelName).FirstOrDefault(), null);
    }

    private async Task<bool> EnsureModelsAsync()
    {
        if (_transcriber.ModelsReady) return true;
        try
        {
            _downloadProgress = 0;
            StateChanged?.Invoke();
            var progress = new Progress<double>(p =>
            {
                _downloadProgress = p;
                StateChanged?.Invoke();
            });
            await _transcriber.DownloadModelsAsync(progress, CancellationToken.None);
            _lastError = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Error("Downloading the speech model failed", ex);
            _lastError = $"couldn't download the speech model ({ex.Message})";
            return false;
        }
        finally
        {
            _downloadProgress = null;
            StateChanged?.Invoke();
        }
    }

    private async Task<Transcript?> TranscribeAsync(TranscriptionJob job)
    {
        _currentId = job.MediaId;
        _currentProgress = 0;
        StateChanged?.Invoke();
        services.Media.SetSyncValue(InProgressKey, job.MediaId.ToString());
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(p =>
            {
                _currentProgress = p;
                Progress?.Invoke(job.MediaId, p);
            });
            var transcript = await _transcriber.TranscribeAsync(job.MediaId, job.Path, progress, CancellationToken.None);
            services.Transcripts.Save(transcript);
            Log.Info($"Transcribed {job.Path} ({job.DurationMs / 1000.0:0}s) in {clock.Elapsed.TotalSeconds:0.0}s on {_transcriber.Runtime}: " +
                     $"{transcript.Status}, {transcript.Segments.Count} segments{(transcript.Error is null ? "" : $" ({transcript.Error})")}");
            Completed?.Invoke(transcript);
            return transcript;
        }
        catch (Exception ex)
        {
            Log.Error($"Transcription of {job.Path} failed", ex);
            return null;
        }
        finally
        {
            services.Media.SetSyncValue(InProgressKey, "");
            _currentId = null;
            StateChanged?.Invoke();
        }
    }

    /// <summary>The app is closing normally: an unfinished transcription isn't a crash, so it's simply redone next time.</summary>
    public void Shutdown()
    {
        if (_currentId is not null) services.Media.SetSyncValue(InProgressKey, "");
    }

    /// <summary>If the app crashed mid-transcription, don't let that video take it down again.</summary>
    private void SkipCrashedVideo()
    {
        if (!long.TryParse(services.Media.GetSyncValue(InProgressKey), out var mediaId)) return;
        Log.Info($"The app stopped while transcribing media {mediaId}; marking it failed");
        services.Transcripts.Save(new Transcript(mediaId, TranscriptStatus.Failed, [], Model: SpeechTranscriber.ModelName,
            Error: "The app stopped unexpectedly while transcribing this video."));
        services.Media.SetSyncValue(InProgressKey, "");
    }
}
