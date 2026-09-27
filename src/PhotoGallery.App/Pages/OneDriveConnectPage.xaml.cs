using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.App.Services;

namespace PhotoGallery.App.Pages;

/// <summary>Signs in to OneDrive on the web (once) so Live Photo videos stored in the cloud can be downloaded.</summary>
public sealed partial class OneDriveConnectPage : Page
{
    private static OneDriveWebSession Session => App.Services.WebSession;
    private bool _done;

    public OneDriveConnectPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            Session.Attach(await WebViewHost.StartAsync(Browser));
            Session.Connected += OnConnected;
            if (Session.IsConnected) ShowConnected();
            Browser.Source = new Uri(OneDriveWebSession.StartUrl);
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "Couldn't open OneDrive";
            StatusBar.Message = $"The Microsoft Edge WebView2 runtime is needed. {ex.Message}";
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Session.Connected -= OnConnected;
        if (Browser.CoreWebView2 is { } core) Session.Detach(core);
        Browser.Close();
    }

    private void OnConnected() => DispatcherQueue.TryEnqueue(ShowConnected);

    private void ShowConnected()
    {
        if (_done) return;
        _done = true;
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Title = "Connected";
        StatusBar.Message = "Live Photos stored in OneDrive will now play in the gallery. People and their names are being read from OneDrive.";
        DoneButton.Visibility = Visibility.Visible;
        App.Services.CloudSync.SyncIfStale();
    }

    private void OnDone(object sender, RoutedEventArgs e) => App.MainWindow.GoBack();
}
