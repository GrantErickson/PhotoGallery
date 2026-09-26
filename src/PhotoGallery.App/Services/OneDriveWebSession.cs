using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.Core;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App.Services;

/// <summary>
/// The user's OneDrive web session, used only to download Live Photo videos and read people and face positions. The web app's own requests carry an
/// Authorization header; we read it from a WebView2 (the Connect page, or a hidden one that reloads onedrive.live.com
/// with the saved sign-in cookies when the header expires, ~1 hour). A hidden page (visibilityState "hidden") skips the
/// my.microsoftpersonalcontent.com/_api calls but still calls api.onedrive.com, whose token works for the video API too.
/// The header is kept in memory only: never logged, persisted, or shown. The sign-in cookies live in the app's
/// WebView2 profile under %LocalAppData%\PhotoGallery\webview.
/// </summary>
public sealed class OneDriveWebSession(AppServices services) : IOneDriveWebToken
{
    public const string StartUrl = "https://onedrive.live.com/?view=8";
    private static readonly string[] ApiFilters = ["https://my.microsoftpersonalcontent.com/_api/*", "https://api.onedrive.com/*"];
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(40);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshGate = new(1);
    private string? _authorization;
    private DateTime _capturedAt;

    /// <summary>Raised (on the UI thread) the first time a header is captured after connecting.</summary>
    public event Action? Connected;

    private event Action? Captured;

    public bool IsConnected => services.Settings.OneDriveWebConnected;

    public async Task<string?> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!IsConnected) return null;
        if (!forceRefresh && IsFresh) return _authorization;

        var before = _capturedAt;
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited.
            if (_capturedAt > before && IsFresh) return _authorization;
            return await OnUiThreadAsync(RefreshHiddenAsync);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool IsFresh => _authorization is not null && DateTime.UtcNow - _capturedAt < Fresh;

    /// <summary>Starts reading Authorization headers from a WebView2's requests to the OneDrive API.</summary>
    public void Attach(CoreWebView2 core)
    {
        foreach (var filter in ApiFilters)
            core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    public void Detach(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        foreach (var filter in ApiFilters)
            core.RemoveWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var headers = e.Request.Headers;
        if (!headers.Contains("Authorization")) return;
        var value = headers.GetHeader("Authorization");
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) return;

        _authorization = value;
        _capturedAt = DateTime.UtcNow;
        if (!services.Settings.OneDriveWebConnected)
        {
            services.Settings.OneDriveWebConnected = true;
            services.SaveSettings();
            Log.Info("OneDrive web session connected");
            Connected?.Invoke();
        }
        Captured?.Invoke();
    }

    /// <summary>Loads OneDrive in an invisible WebView2 (saved cookies) and waits for the page's first API call.</summary>
    private async Task<string?> RefreshHiddenAsync()
    {
        var before = _capturedAt;
        CoreWebView2Controller? controller = null;
        // Completed from inside WebView2's request callback: the code after the await must not run (and close the
        // WebView) inside that callback.
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCaptured() => captured.TrySetResult();
        Captured += OnCaptured;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(); // WEBVIEW2_USER_DATA_FOLDER → the app's profile
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)App.MainWindow.Handle));
            // Hidden but full-sized, so the web app lays out and loads like a normal (background) tab.
            controller.Bounds = new Windows.Foundation.Rect(0, 0, 1280, 800);
            controller.IsVisible = false;
            Attach(controller.CoreWebView2);
            controller.CoreWebView2.Navigate(StartUrl);
            await Task.WhenAny(captured.Task, Task.Delay(RefreshTimeout));
        }
        catch (Exception ex)
        {
            Log.Error($"Refreshing the OneDrive web session failed: {ex.GetType().Name}");
        }
        finally
        {
            Captured -= OnCaptured;
            if (controller is not null)
            {
                // Let WebView2 finish dispatching its events before the view goes away.
                var closing = controller;
                App.MainWindow.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    try { closing.Close(); }
                    catch (Exception ex) { Log.Error($"Closing the hidden OneDrive view failed: {ex.GetType().Name}"); }
                });
            }
        }

        if (_capturedAt > before) return _authorization;
        Log.Info("OneDrive web session needs signing in again");
        return null;
    }

    /// <summary>Forgets the session: the in-memory header and the WebView2 profile's cookies.</summary>
    public async Task DisconnectAsync()
    {
        _authorization = null;
        _capturedAt = default;
        services.Settings.OneDriveWebConnected = false;
        services.SaveSettings();
        await OnUiThreadAsync(async () =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync();
                var controller = await environment.CreateCoreWebView2ControllerAsync(
                    CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)App.MainWindow.Handle));
                await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
                controller.Close();
            }
            catch (Exception ex)
            {
                Log.Error($"Clearing the OneDrive web session failed: {ex.GetType().Name}");
            }
            return (string?)null;
        });
    }

    private static Task<string?> OnUiThreadAsync(Func<Task<string?>> work)
    {
        var queue = App.MainWindow.DispatcherQueue;
        if (queue.HasThreadAccess) return work();
        var result = new TaskCompletionSource<string?>();
        if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                    result.SetResult(await work());
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            }))
            result.SetResult(null);
        return result.Task;
    }
}
