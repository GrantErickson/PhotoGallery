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
        // Menus and dialogs only take the app's theme, which can only be set before any window exists.
        switch (PhotoGallery.Core.AppSettings.Load(PhotoGallery.Core.AppPaths.Default).Theme)
        {
            case "Light": RequestedTheme = ApplicationTheme.Light; break;
            case "System": break;
            default: RequestedTheme = ApplicationTheme.Dark; break;
        }
        // Exceptions on background threads end the process; at least leave a trace of why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            PhotoGallery.Core.Log.Error("Fatal background exception", e.ExceptionObject as Exception);
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
        _window.Closed += (_, _) =>
        {
            _services.Transcription.Shutdown();
            _services.Similar.Dispose();
            _services.Thumbnails.Dispose();
        };
        _window.Activate();

        _services.Indexing.Start();
        _ = SignInAndSyncAsync(_services);
    }

    private static async Task SignInAndSyncAsync(AppServices services)
    {
        await services.OneDrive.TrySignInSilentAsync();
        // Tags need the Graph sign-in, people the web session (which loads OneDrive in a hidden view): give the
        // window a minute to settle first.
        await Task.Delay(TimeSpan.FromMinutes(1));
        services.CloudSync.SyncIfStale();
        services.Transcription.Start();
        services.PhotoText.Start();
        services.Sharpness.Start();
        services.Similar.Start();
        services.PlacesOnline.Start();
    }
}
