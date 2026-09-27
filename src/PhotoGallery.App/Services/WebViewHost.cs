using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.Core;

namespace PhotoGallery.App.Services;

/// <summary>
/// The app's one WebView2 environment: one browser process and profile (%LocalAppData%\PhotoGallery\webview) shared by
/// the map, the OneDrive Connect page and the hidden OneDrive sign-in renewal. Each used to create its own; when two
/// started at the same time (the map opened while the renewal ran) one came up without a browser. Create and use it
/// on the UI thread.
/// </summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(); // WEBVIEW2_USER_DATA_FOLDER → the app's profile
            // Once its browser process has gone (closed, crashed, updated), start afresh next time.
            environment.BrowserProcessExited += (_, _) => _environment = null;
            return environment;
        }
        catch
        {
            _environment = null;
            throw;
        }
    }

    /// <summary>
    /// Starts a WebView2 control on the shared environment. Throws with WebView2's own reason if it couldn't start
    /// (the control reports that through an event, and its task can finish without a browser).
    /// </summary>
    public static async Task<CoreWebView2> StartAsync(WebView2 view)
    {
        if (view.CoreWebView2 is { } running) return running;
        Exception? failure = null;
        void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args) => failure ??= args.Exception;
        view.CoreWebView2Initialized += OnInitialized;
        try
        {
            await view.EnsureCoreWebView2Async(await EnvironmentAsync());
        }
        finally
        {
            view.CoreWebView2Initialized -= OnInitialized;
        }
        if (view.CoreWebView2 is { } core) return core;
        _environment = null; // don't hand out an environment that failed
        var error = failure ?? new InvalidOperationException("WebView2 didn't start.");
        Log.Error("WebView2 didn't start", error);
        throw error;
    }
}
