using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.Services;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>Full-screen viewer over the current grid list: photos (zoom), videos, and Live/motion playback.</summary>
public sealed partial class ViewerControl : UserControl
{
    private static AppServices S => App.Services;

    private IReadOnlyList<MediaSummary> _items = [];
    private int _index;
    private MediaItem? _current;
    private CancellationTokenSource _loadCts = new();
    private bool _changed;
    private bool _suppressRating;
    private bool _playingMotion;

    private readonly MediaPlayer _player = new() { AutoPlay = true };

    public ViewerControl()
    {
        InitializeComponent();
        Player.SetMediaPlayer(_player);
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingMotion) StopPlayback();
        });
        Unloaded += (_, _) => StopPlayback();
    }

    /// <summary>Raised when the viewer closes: (index of the item last shown, whether ratings/tags changed).</summary>
    public event Action<int, bool>? Closed;

    public void Show(IReadOnlyList<MediaSummary> items, int index)
    {
        _items = items;
        _changed = false;
        InfoToggle.IsChecked = InfoColumn.Width.Value > 0;
        _ = ShowIndexAsync(index);
        Focus(FocusState.Programmatic);
    }

    private async Task ShowIndexAsync(int index)
    {
        if (_items.Count == 0) return;
        _index = Math.Clamp(index, 0, _items.Count - 1);
        await _loadCts.CancelAsync();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        StopPlayback();
        ErrorText.Visibility = Visibility.Collapsed;
        PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        Zoom.ChangeView(0, 0, 1, disableAnimation: true);

        var id = _items[_index].Id;
        var item = await Task.Run(() => S.Media.Get(id), ct);
        if (ct.IsCancellationRequested) return;
        if (item is null)
        {
            ShowError("This file is no longer in the library.");
            return;
        }
        _current = item;
        ShowDetails(item);

        if (item.Kind == MediaKind.Video)
        {
            Photo.Source = null;
            Player.Visibility = Visibility.Visible;
            Player.AreTransportControlsEnabled = true;
            _player.Source = MediaSource.CreateFromUri(new Uri(item.Path));
            return;
        }

        // Thumbnail first for instant feedback, then the full image decoded at screen resolution.
        FitPhoto();
        LoadingRing.IsActive = true;
        Photo.Source = S.Thumbnails.TryGetCached(item.Id, out var thumb) ? new BitmapImage(new Uri(thumb)) : null;
        try
        {
            var full = new BitmapImage();
            full.ImageFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_current?.Id == id && Photo.Source is null) ShowError("Windows can't decode this file. A codec extension may be missing.");
            });
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var longest = Math.Max(Stage.ActualWidth, Stage.ActualHeight) * scale * 2; // 2x leaves room to zoom
            if (Math.Max(item.Width, item.Height) > longest)
            {
                if (item.Width >= item.Height) full.DecodePixelWidth = (int)longest;
                else full.DecodePixelHeight = (int)longest;
            }
            full.ImageOpened += (_, _) => DispatcherQueue.TryEnqueue(() => LoadingRing.IsActive = false);
            full.UriSource = new Uri(item.Path);
            Photo.Source = full;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (Photo.Source is null) LoadingRing.IsActive = false;
        }
    }

    private void ShowDetails(MediaItem item)
    {
        TitleText.Text = item.FileName;
        SubtitleText.Text = $"{_index + 1:N0} of {_items.Count:N0}";
        TakenText.Text = item.TakenLocal.ToString("dddd, MMMM d, yyyy  h:mm tt", CultureInfo.CurrentCulture) +
                         (item.DateSource == DateSource.FileModified ? "  (file date)" : "");
        FileText.Text = $"{Format.FileSize(item.FileSize)}" + (item.Width > 0 ? $" · {item.Width:N0} × {item.Height:N0}" : "") +
                        (item.Kind == MediaKind.Video && item.DurationMs > 0 ? $" · {Format.Duration(item.DurationMs)}" : "");
        FolderText.Text = Path.GetDirectoryName(item.Path);

        var camera = string.Join(" ", new[] { item.CameraMake, item.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        CameraPanel.Visibility = camera.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraText.Text = camera;

        LocationPanel.Visibility = item.Latitude is not null ? Visibility.Visible : Visibility.Collapsed;
        LocationLink.Content = item.Latitude is { } lat && item.Longitude is { } lon
            ? string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}")
            : null;

        LiveButton.Visibility = item.Motion is MotionSource.LocalPair or MotionSource.Embedded or MotionSource.Cloud
            ? Visibility.Visible
            : Visibility.Collapsed;
        MotionPanel.Visibility = LiveButton.Visibility;
        MotionText.Text = item.Motion switch
        {
            MotionSource.LocalPair => "Live Photo · video stored next to the photo",
            MotionSource.Embedded => "Motion photo · video embedded in the file",
            MotionSource.Cloud => S.OneDrive.IsSignedIn ? "Live Photo · video streamed from OneDrive" : "Live Photo · sign in to OneDrive (Settings) to play",
            _ => "",
        };

        _suppressRating = true;
        Rating.Value = item.Rating > 0 ? item.Rating : -1;
        _suppressRating = false;

        RefreshTags();
        RefreshAlbums();
    }

    private void RefreshTags() => TagList.ItemsSource = _current is null ? null : S.Collections.GetTagsFor(_current.Id);

    private void RefreshAlbums()
    {
        if (_current is null) return;
        var ids = S.Collections.GetAlbumsContaining(_current.Id);
        var names = S.Collections.GetAlbums().Where(a => ids.Contains(a.Id)).Select(a => a.Name).ToList();
        AlbumsText.Text = names.Count == 0 ? "Not in any album" : string.Join(", ", names);
    }

    private void ShowError(string message)
    {
        LoadingRing.IsActive = false;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ---------- Motion / video ----------

    private async void OnPlayMotion(object sender, RoutedEventArgs e) => await PlayMotionAsync();

    private async Task PlayMotionAsync()
    {
        if (_current is not { } item || _playingMotion) return;
        LiveBusy.IsActive = true;
        try
        {
            var (result, path) = await S.Motion.GetVideoAsync(item, _loadCts.Token);
            if (_current?.Id != item.Id) return;
            switch (result)
            {
                case MotionResult.Ready when path is not null:
                    _playingMotion = true;
                    Player.AreTransportControlsEnabled = false;
                    Player.Visibility = Visibility.Visible;
                    _player.Source = MediaSource.CreateFromUri(new Uri(path));
                    break;
                case MotionResult.NeedsSignIn:
                    App.MainWindow.ShowStatus("Sign in to OneDrive in Settings to play Live Photos stored in the cloud.");
                    break;
                case MotionResult.None:
                    App.MainWindow.ShowStatus("OneDrive has no motion for this photo.");
                    ShowDetails(item);
                    break;
                default:
                    App.MainWindow.ShowStatus("Couldn't download the Live Photo video. Check your connection and try again.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LiveBusy.IsActive = false;
        }
    }

    private void StopPlayback()
    {
        _playingMotion = false;
        _player.Pause();
        _player.Source = null;
        Player.Visibility = Visibility.Collapsed;
    }

    // ---------- Navigation ----------

    private void OnPrevious(object sender, RoutedEventArgs e) => _ = ShowIndexAsync(_index - 1);

    private void OnNext(object sender, RoutedEventArgs e) => _ = ShowIndexAsync(_index + 1);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public void Close()
    {
        _loadCts.Cancel();
        StopPlayback();
        Photo.Source = null;
        Closed?.Invoke(_index, _changed);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or AutoSuggestBox) return;
        switch (e.Key)
        {
            case VirtualKey.Left when _index > 0:
                _ = ShowIndexAsync(_index - 1);
                break;
            case VirtualKey.Right when _index < _items.Count - 1:
                _ = ShowIndexAsync(_index + 1);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            case VirtualKey.Space when _current?.Kind != MediaKind.Video:
                _ = PlayMotionAsync();
                break;
            case VirtualKey.I:
                InfoToggle.IsChecked = !InfoToggle.IsChecked;
                OnToggleInfo(InfoToggle, new RoutedEventArgs());
                break;
            case >= VirtualKey.Number0 and <= VirtualKey.Number5:
                SetRating(e.Key - VirtualKey.Number0);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => FitPhoto();

    /// <summary>At zoom 1 the image fills the stage (letterboxed by Stretch=Uniform); the ScrollViewer zooms from there.</summary>
    private void FitPhoto()
    {
        if (Stage.ActualWidth <= 0) return;
        Photo.Width = Stage.ActualWidth;
        Photo.Height = Stage.ActualHeight;
    }

    private void OnZoomDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Zoom.ZoomFactor > 1.01f)
        {
            Zoom.ChangeView(0, 0, 1);
        }
        else
        {
            var p = e.GetPosition(Photo);
            const float factor = 3f;
            Zoom.ChangeView(p.X * factor - Zoom.ViewportWidth / 2, p.Y * factor - Zoom.ViewportHeight / 2, factor);
        }
    }

    private void OnToggleInfo(object sender, RoutedEventArgs e) =>
        InfoColumn.Width = InfoToggle.IsChecked == true ? new GridLength(320) : new GridLength(0);

    // ---------- Info pane edits ----------

    private void OnRatingChanged(RatingControl sender, object args)
    {
        if (_suppressRating) return;
        SetRating(sender.Value < 1 ? 0 : (int)sender.Value);
    }

    private void SetRating(int rating)
    {
        if (_current is null) return;
        S.Media.SetRating([_current.Id], rating);
        _current.Rating = rating;
        _items[_index].Rating = rating;
        _changed = true;
        _suppressRating = true;
        Rating.Value = rating > 0 ? rating : -1;
        _suppressRating = false;
    }

    private void OnTagBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text.Trim();
        sender.ItemsSource = text.Length == 0
            ? null
            : S.Collections.GetTags().Where(t => t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Select(t => t.Name).Take(8).ToList();
    }

    private void OnTagSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var name = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (_current is null || name.Length == 0) return;
        S.Collections.AddTag([_current.Id], name);
        sender.Text = "";
        _changed = true;
        RefreshTags();
    }

    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (_current is null || ((FrameworkElement)sender).Tag is not long tagId) return;
        S.Collections.RemoveTag([_current.Id], tagId);
        _changed = true;
        RefreshTags();
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string name)
        {
            Close();
            App.MainWindow.Search(name);
        }
    }

    private async void OnAlbumMenuOpening(object sender, object e)
    {
        AlbumMenu.Items.Clear();
        var newAlbum = new MenuFlyoutItem { Text = "New album…", Icon = new SymbolIcon(Symbol.Add) };
        newAlbum.Click += async (_, _) =>
        {
            if (_current is null) return;
            if (await Dialogs.PromptAsync(XamlRoot, "New album", "Album name", "") is { Length: > 0 } name)
            {
                S.Collections.AddToAlbum(S.Collections.CreateAlbum(name), [_current.Id]);
                RefreshAlbums();
            }
        };
        AlbumMenu.Items.Add(newAlbum);
        var albums = await Task.Run(S.Collections.GetAlbums);
        if (albums.Count > 0) AlbumMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var album in albums)
        {
            var entry = new MenuFlyoutItem { Text = album.Name };
            entry.Click += (_, _) =>
            {
                if (_current is null) return;
                S.Collections.AddToAlbum(album.Id, [_current.Id]);
                RefreshAlbums();
            };
            AlbumMenu.Items.Add(entry);
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (_current is not null) Process.Start("explorer.exe", $"/select,\"{_current.Path}\"");
    }

    private async void OnOpenOneDrive(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (S.Settings.ToOneDrivePath(_current.Path) is not { } remote)
        {
            App.MainWindow.ShowStatus("This file isn't in your OneDrive folder.");
            return;
        }
        var url = await S.OneDrive.GetWebUrlAsync(remote);
        if (url is null)
        {
            App.MainWindow.ShowStatus(S.OneDrive.IsSignedIn ? "OneDrive couldn't find this file." : "Sign in to OneDrive in Settings first.");
            return;
        }
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    private async void OnOpenMap(object sender, RoutedEventArgs e)
    {
        if (_current is { Latitude: { } lat, Longitude: { } lon })
            await Launcher.LaunchUriAsync(new Uri(string.Create(CultureInfo.InvariantCulture,
                $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=16/{lat}/{lon}")));
    }
}
