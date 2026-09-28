using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace PhotoGallery.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loadingSwitch;

    public SettingsPage()
    {
        InitializeComponent();
        App.Services.Transcription.StateChanged += OnTranscriptionChanged;
        App.Services.Transcription.Completed += _ => OnTranscriptionChanged();
        App.Services.PhotoText.StateChanged += OnTranscriptionChanged;
        App.Services.PhotoText.Completed += _ => OnTranscriptionChanged();
        App.Services.Similar.StateChanged += OnTranscriptionChanged;
        App.Services.PlacesOnline.StateChanged += OnTranscriptionChanged;
        App.Services.Remote.StateChanged += () => DispatcherQueue.TryEnqueue(RefreshRemote);
        App.Services.PeopleChanges.StateChanged += () => DispatcherQueue.TryEnqueue(RefreshPeopleChanges);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        RefreshRoots();
        RefreshAccount();
        _loadingSwitch = true;
        TranscribeSwitch.IsOn = App.Services.Settings.TranscribeInBackground;
        PhotoTextSwitch.IsOn = App.Services.Settings.ReadPhotoTextInBackground;
        SimilarSwitch.IsOn = App.Services.Settings.FindSimilarInBackground;
        ConfirmDeleteSwitch.IsOn = App.Services.Settings.ConfirmDelete;
        ThemeChoice.SelectedIndex = App.Services.Settings.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
        PoiSwitch.IsOn = App.Services.Settings.NamePlacesFromOsm;
        RemoteSwitch.IsOn = App.Services.Settings.RemoteEnabled;
        RemoteChangesSwitch.IsOn = App.Services.Settings.RemoteAllowChanges;
        RemotePortBox.Value = App.Services.Settings.RemotePort;
        RefreshRemote();
        _loadingSwitch = false;
        await RefreshTranscriptsAsync();
        var services = App.Services;
        PathsText.Text = $"Database: {services.Paths.Database}\nThumbnails: {services.Paths.Thumbnails}";
        var stats = await Task.Run(services.Media.GetStats);
        StatsText.Text =
            $"{stats.Photos:N0} photos · {stats.Videos:N0} videos · {stats.Screenshots:N0} screenshots · {stats.UtilityShots:N0} utility shots\n" +
            $"Live & motion: {stats.LocalPairs:N0} with local video, {stats.Embedded:N0} with embedded video, {stats.Cloud:N0} with video in OneDrive";
    }

    private void RefreshRoots() => RootsList.ItemsSource = App.Services.Settings.LibraryRoots.ToList();

    private DateTime _lastTranscriptRefresh;

    /// <summary>Background updates, at most once a second while this page is showing.</summary>
    private void OnTranscriptionChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!IsLoaded || DateTime.UtcNow - _lastTranscriptRefresh < TimeSpan.FromSeconds(1)) return;
        await RefreshTranscriptsAsync();
    });

    private async Task RefreshTranscriptsAsync()
    {
        _lastTranscriptRefresh = DateTime.UtcNow;
        var service = App.Services.Transcription;
        var (done, total, withSpeech) = await Task.Run(App.Services.Transcripts.GetProgress);
        var runtime = service.Runtime is { } r ? $" · running on {(r == "Cpu" ? "the CPU" : $"the GPU ({r})")}" : "";
        TranscriptStatsText.Text = $"{done:N0} of {total:N0} videos done, {withSpeech:N0} with speech · {service.Status}{runtime}";

        var ocr = App.Services.PhotoText;
        var (read, photos, withText) = await Task.Run(App.Services.PhotoTexts.GetProgress);
        PhotoTextStatsText.Text = !ocr.IsAvailable
            ? "Windows has no OCR language installed for your languages (Settings › Time & language › Language)."
            : $"{read:N0} of {photos:N0} photos read, {withText:N0} with text · " +
              (!App.Services.Settings.ReadPhotoTextInBackground ? "background reading is off" : ocr.IsWorking ? "Reading…" : read == photos ? "Up to date" : "Waiting to start");

        var poi = App.Services.PlacesOnline;
        var (tilesDone, tiles, places, photosNamed) = await Task.Run(App.Services.Pois.GetProgress);
        PoiStatsText.Text = !App.Services.Settings.NamePlacesFromOsm && tilesDone == 0
            ? ""
            : $"{tilesDone:N0} of {tiles:N0} areas looked up · {places:N0} places · {photosNamed:N0} photos named · " +
              (poi.LastError is { } poiError ? poiError
               : !App.Services.Settings.NamePlacesFromOsm ? "looking up is off"
               : poi.IsWorking ? "Looking up…" : tilesDone == tiles ? "Up to date" : "Waiting to start");

        var similar = App.Services.Similar;
        var (compared, all) = await Task.Run(App.Services.Embeddings.GetProgress);
        var device = similar.Device is { } d ? $" · on {(d == "CPU" ? "the CPU" : $"the GPU ({d})")}" : "";
        SimilarStatsText.Text = !similar.IsInstalled ? "The comparing part of the app is missing; rebuild it."
            : similar.DownloadProgress is { } p ? $"Downloading the model… {p:P0}"
            : $"{compared:N0} of {all:N0} photos and videos compared · " +
              (similar.LastError is { } error ? error
               : !App.Services.Settings.FindSimilarInBackground ? "background comparing is off"
               : similar.IsWorking ? "Comparing…" : compared == all ? "Up to date" : "Waiting to start") + device;
        var (scored, scorable, utility) = await Task.Run(App.Services.Media.GetUtilityProgress);
        UtilityStatsText.Text = scorable == 0 ? ""
            : $"{scored:N0} of {scorable:N0} compared photos checked for utility shots · {utility:N0} found" +
              (scored < scorable && !similar.IsWorking && App.Services.Settings.FindSimilarInBackground ? " · checking…" : "");
    }

    private void OnPoiToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.NamePlacesFromOsm = PoiSwitch.IsOn;
        App.Services.SaveSettings();
        App.Services.PlacesOnline.Nudge();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSwitch || ThemeChoice.SelectedIndex < 0) return;
        var theme = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "System", _ => "Dark" };
        if (theme == App.Services.Settings.Theme) return;
        App.Services.Settings.Theme = theme;
        App.Services.SaveSettings();
        App.MainWindow.ApplyTheme(theme);
        ThemeRestart.Visibility = Visibility.Visible;
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        App.Services.SaveSettings();
        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
    }

    // ---------- Remote access ----------

    private void RefreshRemote()
    {
        var remote = App.Services.Remote;
        RemotePassphraseText.Text = remote.HasPassphrase
            ? "A passphrase is set. Setting a new one signs every other computer out."
            : "Set a passphrase first: other computers need it to connect. Several words are easier to type and harder to guess than one.";
        RemoteSwitch.IsEnabled = remote.HasPassphrase;
        if (remote.LastError is { } error)
        {
            RemoteStatusText.Text = $"Remote access couldn't start: {error}";
        }
        else if (remote.IsRunning && remote.Fingerprint is { } fingerprint)
        {
            var port = remote.Port;
            var addresses = PhotoGallery.Remote.LocalNetwork.Addresses().Select(a => $"https://{a}:{port}").ToList();
            RemoteStatusText.Text =
                $"On. On the other computer, open https://{Environment.MachineName}:{port} in a browser" +
                (addresses.Count > 0 ? $" (or {string.Join(", ", addresses)})" : "") +
                $", or use Another computer in Photo Gallery there, with the computer name {Environment.MachineName}.\n\n" +
                $"Security code: {PhotoGallery.Remote.HostCertificate.SecurityCode(fingerprint)}\n" +
                "The first time, check that the other computer shows this same code. Browsers warn that the connection isn't private " +
                "(this PC made its own certificate): the certificate's SHA-256 fingerprint there should begin with the code.\n\n" +
                "If Windows asks whether Photo Gallery may use the network, allow it on private networks.";
        }
        else
        {
            RemoteStatusText.Text = remote.HasPassphrase ? "Off." : "";
        }
    }

    private void OnRemotePassphraseKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        OnSetRemotePassphrase(sender, e);
        e.Handled = true;
    }

    private async void OnSetRemotePassphrase(object sender, RoutedEventArgs e)
    {
        var passphrase = RemotePassphraseBox.Password;
        if (PhotoGallery.Remote.RemoteSecret.Normalize(passphrase).Length < PhotoGallery.Remote.RemoteSecret.MinLength)
        {
            RemotePassphraseText.Text = $"Use at least {PhotoGallery.Remote.RemoteSecret.MinLength} characters; a few words are best.";
            return;
        }
        RemotePassphraseButton.IsEnabled = false;
        try
        {
            await App.Services.Remote.SetPassphraseAsync(passphrase);
            RemotePassphraseBox.Password = "";
        }
        finally
        {
            RemotePassphraseButton.IsEnabled = true;
        }
        RefreshRemote();
        RemotePassphraseText.Text = "Passphrase set. Other computers that were connected need to sign in with it again.";
    }

    private async void OnRemoteToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch || RemoteSwitch.IsOn == App.Services.Settings.RemoteEnabled) return;
        App.Services.Settings.RemoteEnabled = RemoteSwitch.IsOn;
        App.Services.SaveSettings();
        RemoteStatusText.Text = RemoteSwitch.IsOn ? "Starting…" : "";
        await App.Services.Remote.ApplyAsync();
    }

    private void OnRemoteChangesToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.RemoteAllowChanges = RemoteChangesSwitch.IsOn; // the server reads it on every request
        App.Services.SaveSettings();
    }

    private async void OnRemotePortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSwitch || double.IsNaN(args.NewValue)) return;
        var port = (int)Math.Clamp(Math.Round(args.NewValue), 1024, 65535);
        if (port == App.Services.Settings.RemotePort) return;
        App.Services.Settings.RemotePort = port;
        App.Services.SaveSettings();
        if (App.Services.Settings.RemoteEnabled) await App.Services.Remote.ApplyAsync();
    }

    private void OnConfirmDeleteToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.ConfirmDelete = ConfirmDeleteSwitch.IsOn;
        App.Services.SaveSettings();
    }

    private void OnSimilarToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.FindSimilarInBackground = SimilarSwitch.IsOn;
        App.Services.SaveSettings();
        App.Services.Similar.Nudge();
    }

    private void OnPhotoTextToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.ReadPhotoTextInBackground = PhotoTextSwitch.IsOn;
        App.Services.SaveSettings();
        App.Services.PhotoText.Nudge();
    }

    private void OnTranscribeToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitch) return;
        App.Services.Settings.TranscribeInBackground = TranscribeSwitch.IsOn;
        App.Services.SaveSettings();
        App.Services.Transcription.Nudge();
    }

    private void RefreshAccount()
    {
        var oneDrive = App.Services.OneDrive;
        AccountText.Text = oneDrive.IsSignedIn ? $"Signed in as {oneDrive.AccountName}" : "Not signed in";
        var sync = App.Services.CloudSync;
        var last = new[] { sync.LastSync, sync.LastFaceSync }.Max();
        CloudSyncText.Text = "OneDrive's tags (things, places, categories) are read in the background once a day; people and their faces " +
                             "too once OneDrive is connected below (every photo once a month, new ones daily). " +
                             (last is { } when ? $"Last read {when.ToLocalTime():g}." : "Not read yet.");
        SyncButton.IsEnabled = oneDrive.IsSignedIn || App.Services.WebSession.IsConnected;
        SignInButton.Visibility = oneDrive.IsSignedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = oneDrive.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;

        var web = App.Services.WebSession;
        LiveStatusText.Text = web.IsConnected ? "Connected — Live Photos stored in OneDrive play in the gallery, and people come from OneDrive." : "Not connected.";
        ConnectWebButton.Content = web.IsConnected ? "Sign in again" : "Connect OneDrive";
        DisconnectWebButton.Visibility = web.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        RefreshPeopleChanges();
    }

    /// <summary>Names and joins made here on their way to OneDrive: waiting, or refused (to try again or keep here only).</summary>
    private void RefreshPeopleChanges()
    {
        var (waiting, refused, error) = App.Services.People.GetChangeStatus();
        static string Changes(long n) => n == 1 ? "1 name or join" : $"{n:N0} names and joins";
        PeopleChangesText.Text =
            (waiting > 0 ? $"{Changes(waiting)} waiting to go to OneDrive. {App.Services.PeopleChanges.Problem ?? "Sending…"}" : "") +
            (waiting > 0 && refused > 0 ? "\n" : "") +
            (refused > 0 ? $"OneDrive refused {Changes(refused)} made here{(error is null ? "" : $" ({error})")}." : "");
        PeopleChangesText.Visibility = waiting + refused > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefusedPanel.Visibility = refused > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRetryPeopleChanges(object sender, RoutedEventArgs e)
    {
        App.Services.People.RetryRefusedChanges();
        RefreshPeopleChanges();
    }

    private void OnDiscardPeopleChanges(object sender, RoutedEventArgs e)
    {
        App.Services.People.DiscardRefusedChanges();
        RefreshPeopleChanges();
    }

    private async void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Handle);
        if (await picker.PickSingleFolderAsync() is not { } folder) return;

        var roots = App.Services.Settings.LibraryRoots;
        if (roots.Any(r => folder.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
        {
            App.MainWindow.ShowStatus("That folder is already part of the library.");
            return;
        }
        roots.RemoveAll(r => r.StartsWith(folder.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        roots.Add(folder.Path);
        ApplyRootChange();
    }

    private async void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string root) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Remove folder?", $"{root}\n\nIts photos will be removed from the gallery (ratings, tags and album entries for them are lost). Files on disk are not touched.", "Remove"))
            return;
        App.Services.Settings.LibraryRoots.Remove(root);
        await Task.Run(() => App.Services.Media.RemoveRoot(root));
        ApplyRootChange();
    }

    private void ApplyRootChange()
    {
        App.Services.SaveSettings();
        RefreshRoots();
        App.Services.Indexing.RestartWatcher();
        App.Services.Indexing.RequestIndex();
    }

    private void OnRescan(object sender, RoutedEventArgs e) => App.Services.Indexing.RequestIndex();

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        try
        {
            if (!await App.Services.OneDrive.SignInAsync())
                App.MainWindow.ShowStatus("Sign-in was cancelled or failed.");
            else
                App.Services.CloudSync.SyncIfStale();
        }
        finally
        {
            SignInButton.IsEnabled = true;
            RefreshAccount();
        }
    }

    private void OnConnectWeb(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(OneDriveConnectPage), null);

    private async void OnDisconnectWeb(object sender, RoutedEventArgs e)
    {
        await App.Services.WebSession.DisconnectAsync();
        RefreshAccount();
        App.MainWindow.ShowStatus("Disconnected. Live Photos stored in OneDrive won't play until you connect again.");
    }

    /// <summary>Reads everything again, including every photo's faces (a few minutes for a large library).</summary>
    private void OnSyncNow(object sender, RoutedEventArgs e) => App.Services.CloudSync.Start(fullFaceScan: true);

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await App.Services.OneDrive.SignOutAsync();
        RefreshAccount();
    }
}
