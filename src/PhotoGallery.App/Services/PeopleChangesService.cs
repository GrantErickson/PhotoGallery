using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App.Services;

/// <summary>
/// Sends names and merges made in the gallery to OneDrive: right away, and again every few minutes while any are
/// waiting (not connected, OneDrive busy). Events are raised on a background thread.
/// </summary>
public sealed class PeopleChangesService(AppServices services)
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _wake = new(0);
    private OneDrivePeopleWriter? _writer;
    private int _started;

    /// <summary>Something was sent, refused, or couldn't be sent.</summary>
    public event Action? StateChanged;

    /// <summary>Why changes are waiting (not connected, OneDrive unreachable), or null.</summary>
    public string? Problem { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        services.People.ChangesQueued += Nudge;
        _ = Task.Run(LoopAsync);
    }

    public void Nudge()
    {
        if (_started == 1) _wake.Release();
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            var waiting = false;
            try
            {
                _writer ??= new OneDrivePeopleWriter(services.PeopleOnline, services.People);
                var result = await _writer.SendAsync();
                waiting = result.Waiting > 0;
                Problem = waiting ? "OneDrive couldn't be reached; trying again in a few minutes." : null;
                if (result.Sent + result.Refused > 0 || waiting) StateChanged?.Invoke();
            }
            catch (OneDriveWebUnavailableException)
            {
                waiting = true;
                Problem = "Waiting for OneDrive: connect it in Settings › Live Photos and people.";
                StateChanged?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                waiting = true;
                Problem = $"Couldn't send to OneDrive: {ex.Message}";
                Log.Error("Sending people changes to OneDrive failed", ex);
                StateChanged?.Invoke();
            }
            if (waiting) await _wake.WaitAsync(RetryEvery);
            else await _wake.WaitAsync();
            while (_wake.CurrentCount > 0) await _wake.WaitAsync(); // several changes at once: one pass sends them all
        }
    }
}
