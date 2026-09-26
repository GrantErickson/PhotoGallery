using Microsoft.UI.Xaml;
using PhotoGallery.App.Services;

namespace PhotoGallery.App;

public partial class App : Application
{
    private static AppServices? _services;
    private static MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            PhotoGallery.Core.Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
            _window?.ShowStatus($"Something went wrong: {e.Exception.Message}");
        };
    }

    public static AppServices Services => _services ?? throw new InvalidOperationException("App not started");

    public static MainWindow MainWindow => _window ?? throw new InvalidOperationException("App not started");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = new AppServices();
        // Unpackaged apps default WebView2's data folder to the exe directory, which may not be writable.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(_services.Paths.Root, "webview"));
        _window = new MainWindow();
        _window.Closed += (_, _) => _services.Thumbnails.Dispose();
        _window.Activate();

        _services.Indexing.Start();
        _ = SignInAndSyncAsync(_services);
    }

    private static async Task SignInAndSyncAsync(AppServices services)
    {
        if (await services.OneDrive.TrySignInSilentAsync()) services.CloudSync.SyncIfStale();
    }
}
