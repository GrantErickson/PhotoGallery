using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

public sealed class AlbumTile(AlbumRow row) : INotifyPropertyChanged
{
    private ImageSource? _cover;

    public AlbumRow Row { get; } = row;
    public string CountText => Row.Count == 1 ? "1 item" : $"{Row.Count:N0} items";

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoCover));
        }
    }

    public Visibility NoCover => _cover is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class AlbumsPage : Page
{
    public AlbumsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var rows = await Task.Run(App.Services.Collections.GetAlbums);
        var tiles = rows.Select(r => new AlbumTile(r)).ToList();
        Albums.ItemsSource = tiles;
        EmptyText.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tile in tiles)
        {
            if (tile.Row.CoverMediaId is not { } coverId) continue;
            var path = await Task.Run(async () =>
                App.Services.Media.GetPath(coverId) is { } source ? await App.Services.Thumbnails.GetOrCreateAsync(coverId, source) : null);
            if (path is not null) tile.Cover = new BitmapImage(new Uri(path)) { DecodePixelWidth = 400 };
        }
    }

    private async void OnNewAlbum(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.PromptAsync(XamlRoot, "New album", "Album name", "", "Create") is { Length: > 0 } name)
        {
            App.Services.Collections.CreateAlbum(name);
            await LoadAsync();
        }
    }

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumTile tile) Open(tile.Row);
    }

    public static void Open(AlbumRow album) =>
        App.MainWindow.Navigate(typeof(GalleryPage), new GalleryRequest(album.Name, new MediaFilter { AlbumId = album.Id, IncludeScreenshots = true },
            AlbumId: album.Id, EmptyMessage: "This album is empty. Select photos anywhere and choose “Add to album”.", Section: "albums"));

    private void OnAlbumRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlbumTile tile) return;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename", Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) =>
        {
            if (await Dialogs.PromptAsync(XamlRoot, "Rename album", "Album name", tile.Row.Name, "Rename") is { Length: > 0 } name)
            {
                App.Services.Collections.RenameAlbum(tile.Row.Id, name);
                await LoadAsync();
            }
        };
        var delete = new MenuFlyoutItem { Text = "Delete album", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) =>
        {
            if (await Dialogs.ConfirmAsync(XamlRoot, $"Delete “{tile.Row.Name}”?", "The photos stay in your library; only the album is removed.", "Delete"))
            {
                App.Services.Collections.DeleteAlbum(tile.Row.Id);
                await LoadAsync();
            }
        };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }
}
