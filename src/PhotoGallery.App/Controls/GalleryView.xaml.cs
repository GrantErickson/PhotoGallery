using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.Services;
using PhotoGallery.Core.Data;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>
/// The virtualized photo grid used by every view (timeline, folders, albums, search, on this day).
/// All rows for the filter are loaded as small <see cref="MediaSummary"/> objects; thumbnails load per
/// visible container and are cancelled when the container is recycled.
/// </summary>
public sealed partial class GalleryView : UserControl
{
    private static AppServices S => App.Services;

    private MediaFilter? _baseFilter;
    private List<MediaSummary> _items = [];
    private int _loadVersion;
    private ScrollViewer? _scroller;
    private bool _loaded;

    private bool _initialized;

    public GalleryView()
    {
        InitializeComponent();
        SizeSlider.Value = S.Settings.TileSize;
        _initialized = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>When set, the view shows an album (custom order) and offers "Remove from album".</summary>
    public long? AlbumId { get; set; }

    private bool _showFilterBar = true;

    public bool ShowFilterBar
    {
        get => _showFilterBar;
        set
        {
            _showFilterBar = value;
            FilterBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public string EmptyMessage { get; set; } = "Nothing here yet.";

    /// <summary>The page's query; the grid stays empty until this is set.</summary>
    public MediaFilter? BaseFilter
    {
        get => _baseFilter;
        set
        {
            _baseFilter = value;
            if (_loaded) _ = ReloadAsync();
        }
    }

    private MediaFilter? EffectiveFilter => _baseFilter is not { } f ? null : f with
    {
        Kinds = (KindFilter)Math.Max(0, KindBox.SelectedIndex),
        MinRating = Math.Max(f.MinRating, RatingBox.SelectedIndex),
        MotionOnly = f.MotionOnly || MotionToggle.IsChecked == true,
        IncludeScreenshots = f.IncludeScreenshots || ScreenshotToggle.IsChecked == true,
        AlbumId = AlbumId ?? f.AlbumId,
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        RemoveFromAlbumButton.Visibility = AlbumId is null ? Visibility.Collapsed : Visibility.Visible;
        JumpList.Visibility = AlbumId is null ? Visibility.Visible : Visibility.Collapsed;
        S.Indexing.LibraryChanged += OnLibraryChanged;
        _scroller ??= FindDescendant<ScrollViewer>(Grid);
        if (_scroller is not null) _scroller.ViewChanged += (_, _) => UpdateCurrentDate();
        ApplyTileSize();
        _ = ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        S.Indexing.LibraryChanged -= OnLibraryChanged;
        S.SaveSettings();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(() => _ = ReloadAsync(keepPosition: true));

    public async Task ReloadAsync(bool keepPosition = false)
    {
        if (EffectiveFilter is not { } filter) return;
        var version = ++_loadVersion;
        Busy.IsActive = true;
        var anchor = keepPosition ? FirstVisibleItem()?.Id : null;
        List<MediaSummary> items;
        try
        {
            items = await Task.Run(() => S.Media.Query(filter));
        }
        catch (Exception ex)
        {
            Busy.IsActive = false;
            EmptyText.Text = $"Could not load: {ex.Message}";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        if (version != _loadVersion) return;

        _items = items;
        Grid.ItemsSource = items;
        CountText.Text = items.Count == 1 ? "1 item" : $"{items.Count:N0} items";
        EmptyText.Text = EmptyMessage;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildJumpList();
        Busy.IsActive = false;

        if (anchor is { } id && items.FindIndex(i => i.Id == id) is var index and >= 0)
            Grid.ScrollIntoView(items[index], ScrollIntoViewAlignment.Leading);
        UpdateCurrentDate();
    }

    // ---------- Thumbnails ----------

    /// <summary>Which item a recycled tile currently shows, and how to cancel its pending thumbnail.</summary>
    private sealed class TileState(MediaSummary item)
    {
        public MediaSummary Item { get; } = item;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid root || root.Children[0] is not Image image) return;

        if (args.InRecycleQueue)
        {
            (root.Tag as TileState)?.Cancellation.Cancel();
            root.Tag = null;
            image.Source = null;
            return;
        }

        if (args.Phase == 0)
        {
            (root.Tag as TileState)?.Cancellation.Cancel();
            image.Source = null;
            if (args.Item is MediaSummary item) root.Tag = new TileState(item);
            args.RegisterUpdateCallback(1, OnContainerContentChanging);
        }
        else if (args.Phase == 1 && root.Tag is TileState state)
        {
            _ = LoadThumbnailAsync(root, image, state);
        }
        args.Handled = true;
    }

    private async Task LoadThumbnailAsync(Grid root, Image image, TileState state)
    {
        var id = state.Item.Id;
        var ct = state.Cancellation.Token;
        if (!S.Thumbnails.TryGetCached(id, out var path))
        {
            try
            {
                path = await Task.Run(async () =>
                    S.Media.GetPath(id) is { } source ? await S.Thumbnails.GetOrCreateAsync(id, source, ct) : null, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (path is null || ct.IsCancellationRequested || !ReferenceEquals(root.Tag, state)) return; // recycled meanwhile

        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        image.Source = new BitmapImage
        {
            DecodePixelWidth = (int)(SizeSlider.Value * scale),
            DecodePixelType = DecodePixelType.Physical,
            UriSource = new Uri(path),
        };
    }

    // ---------- Tile size ----------

    private void OnTileSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized) return; // the slider reports its minimum while XAML initializes
        S.Settings.TileSize = e.NewValue;
        ApplyTileSize();
    }

    private void ApplyTileSize()
    {
        if (Grid?.ItemsPanelRoot is ItemsWrapGrid panel)
            panel.ItemWidth = panel.ItemHeight = SizeSlider.Value + 4;
    }

    // ---------- Timeline jump list ----------

    private void BuildJumpList()
    {
        var entries = new List<JumpEntry>();
        int lastYear = -1, lastMonth = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            var taken = _items[i].TakenLocal;
            if (taken.Year != lastYear)
            {
                entries.Add(new JumpEntry(taken.Year.ToString(CultureInfo.CurrentCulture), i, isYear: true));
                lastYear = taken.Year;
                lastMonth = -1;
            }
            if (taken.Month != lastMonth)
            {
                entries.Add(new JumpEntry("   " + CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(taken.Month), i, isYear: false));
                lastMonth = taken.Month;
            }
        }
        JumpList.ItemsSource = entries;
    }

    private void OnJumpClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JumpEntry entry && entry.Index < _items.Count)
            Grid.ScrollIntoView(_items[entry.Index], ScrollIntoViewAlignment.Leading);
    }

    private MediaSummary? FirstVisibleItem() =>
        Grid.ItemsPanelRoot is ItemsWrapGrid { FirstVisibleIndex: >= 0 } panel && panel.FirstVisibleIndex < _items.Count
            ? _items[panel.FirstVisibleIndex]
            : null;

    private void UpdateCurrentDate() =>
        CurrentDate.Text = AlbumId is null && FirstVisibleItem() is { } item ? item.TakenLocal.ToString("MMMM yyyy", CultureInfo.CurrentCulture) : "";

    // ---------- Opening the viewer ----------

    private void OnTileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TileState state)
        {
            Open(state.Item);
            e.Handled = true;
        }
    }

    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile || tile.Tag is not TileState state) return;
        // Acting on a multi-selection that includes this tile; otherwise just this item.
        var ids = Grid.SelectedItems.Contains(state.Item) && Grid.SelectedItems.Count > 1 ? SelectedIds() : [state.Item.Id];
        var menu = new MenuFlyout();
        if (ids.Count == 1)
            menu.Items.Add(MenuItem("Open", Symbol.OpenFile, () => Open(state.Item)));
        menu.Items.Add(MenuItem(ids.Count == 1 ? "Copy path" : $"Copy {ids.Count:N0} paths", Symbol.Copy,
            () => Clipboard.CopyPaths(ids.Select(S.Media.GetPath).OfType<string>().ToList())));
        if (ids.Count == 1)
            menu.Items.Add(MenuItem("Show in File Explorer", Symbol.Folder, () =>
            {
                if (S.Media.GetPath(state.Item.Id) is { } path) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }));
        menu.ShowAt(tile, e.GetPosition(tile));
        e.Handled = true;
    }

    private static MenuFlyoutItem MenuItem(string text, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnCopySelectedPaths(object sender, RoutedEventArgs e) =>
        Clipboard.CopyPaths(SelectedIds().Select(S.Media.GetPath).OfType<string>().ToList());

    private void OnGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Grid.SelectedItem is MediaSummary item)
        {
            Open(item);
            e.Handled = true;
        }
    }

    /// <summary>Opens the viewer on an item by id (the map's markers); falls back to showing just that item.</summary>
    public void OpenItem(long id)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            Open(item);
            return;
        }
        var single = S.Media.Query(new MediaFilter { IncludeScreenshots = true }).Where(i => i.Id == id).ToList();
        if (single.Count == 1) App.MainWindow.OpenViewer(single, 0, (_, _) => { });
    }

    private void Open(MediaSummary item)
    {
        var index = _items.IndexOf(item);
        if (index >= 0) App.MainWindow.OpenViewer(_items, index, OnViewerClosed);
    }

    private void OnViewerClosed(int index, bool changed)
    {
        if (changed)
            _ = ReloadAsync(keepPosition: true);
        if (index >= 0 && index < _items.Count)
        {
            Grid.ScrollIntoView(_items[index]);
            Grid.SelectedItem = _items[index];
        }
        Grid.Focus(FocusState.Programmatic);
    }

    // ---------- Filters and selection ----------

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded) _ = ReloadAsync();
    }

    private List<long> SelectedIds() => Grid.SelectedItems.OfType<MediaSummary>().Select(i => i.Id).ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = Grid.SelectedItems.Count;
        var multi = count > 1;
        SelectionBar.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = multi || !_showFilterBar ? Visibility.Collapsed : Visibility.Visible;
        if (multi) CountText.Text = $"{count:N0} selected";
        else CountText.Text = _items.Count == 1 ? "1 item" : $"{_items.Count:N0} items";
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) => Grid.SelectedItems.Clear();

    private async void OnAlbumMenuOpening(object sender, object e)
    {
        AlbumMenu.Items.Clear();
        var newAlbum = new MenuFlyoutItem { Text = "New album…", Icon = new SymbolIcon(Symbol.Add) };
        newAlbum.Click += async (_, _) =>
        {
            var ids = SelectedIds();
            if (await Dialogs.PromptAsync(XamlRoot, "New album", "Album name", "") is { Length: > 0 } name)
            {
                var albumId = S.Collections.CreateAlbum(name);
                S.Collections.AddToAlbum(albumId, ids);
                App.MainWindow.ShowStatus($"Added {ids.Count:N0} items to “{name}”");
            }
        };
        AlbumMenu.Items.Add(newAlbum);
        var albums = await Task.Run(S.Collections.GetAlbums);
        if (albums.Count > 0) AlbumMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var album in albums)
        {
            var item = new MenuFlyoutItem { Text = album.Name };
            item.Click += (_, _) =>
            {
                var ids = SelectedIds();
                S.Collections.AddToAlbum(album.Id, ids);
                App.MainWindow.ShowStatus($"Added {ids.Count:N0} items to “{album.Name}”");
            };
            AlbumMenu.Items.Add(item);
        }
    }

    private void OnRemoveFromAlbum(object sender, RoutedEventArgs e)
    {
        if (AlbumId is not { } albumId) return;
        S.Collections.RemoveFromAlbum(albumId, SelectedIds());
        _ = ReloadAsync(keepPosition: true);
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
        if (name.Length == 0) return;
        var ids = SelectedIds();
        S.Collections.AddTag(ids, name);
        sender.Text = "";
        TagFlyout.Hide();
        App.MainWindow.ShowStatus($"Tagged {ids.Count:N0} items “{name}”");
    }

    private void OnRateSelection(object sender, RoutedEventArgs e)
    {
        var rating = int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
        S.Media.SetRating(SelectedIds(), rating);
        _ = ReloadAsync(keepPosition: true);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
