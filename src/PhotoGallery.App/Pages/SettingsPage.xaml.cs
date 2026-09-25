using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace PhotoGallery.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        RefreshRoots();
        RefreshAccount();
        var services = App.Services;
        PathsText.Text = $"Database: {services.Paths.Database}\nThumbnails: {services.Paths.Thumbnails}";
        var stats = await Task.Run(services.Media.GetStats);
        StatsText.Text =
            $"{stats.Photos:N0} photos · {stats.Videos:N0} videos · {stats.Screenshots:N0} screenshots\n" +
            $"Live & motion: {stats.LocalPairs:N0} with local video, {stats.Embedded:N0} with embedded video, {stats.Cloud:N0} with video in OneDrive";
    }

    private void RefreshRoots() => RootsList.ItemsSource = App.Services.Settings.LibraryRoots.ToList();

    private void RefreshAccount()
    {
        var oneDrive = App.Services.OneDrive;
        AccountText.Text = oneDrive.IsSignedIn ? $"Signed in as {oneDrive.AccountName}" : "Not signed in";
        SignInButton.Visibility = oneDrive.IsSignedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = oneDrive.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
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
        }
        finally
        {
            SignInButton.IsEnabled = true;
            RefreshAccount();
        }
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await App.Services.OneDrive.SignOutAsync();
        RefreshAccount();
    }
}
