using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.Services;
using PhotoGallery.Core.Data;
using Windows.Media.Playback;
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
    private System.Collections.ObjectModel.ObservableCollection<MediaSummary>? _albumOrder;
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

    /// <summary>Section headers per month (timeline) or per day with the full date (On this day).</summary>
    public GroupMode GroupMode { get; set; } = GroupMode.Month;

    /// <summary>The page's query; the grid stays empty until this is set.</summary>
    public MediaFilter? BaseFilter
    {
        get => _baseFilter;
        set
        {
            // Views that show screenshots anyway (search, folders) start with them included.
            if (value is { IncludeScreenshots: true } && (_baseFilter is null || !_baseFilter.IncludeScreenshots) && ScreenshotBox.SelectedIndex == 0)
            {
                _settingScreenshots = true;
                ScreenshotBox.SelectedIndex = 1;
                _settingScreenshots = false;
            }
            _baseFilter = value;
            if (_loaded) _ = ReloadAsync();
        }
    }

    private MediaFilter? EffectiveFilter => _baseFilter is not { } f ? null : f with
    {
        Kinds = (KindFilter)Math.Max(0, KindBox.SelectedIndex),
        MinRating = Math.Max(f.MinRating, RatingBox.SelectedIndex),
        MotionOnly = f.MotionOnly || MotionToggle.IsChecked == true,
        People = _people.Count == 0 ? f.People : [.. _people, .. f.People ?? []],
        IncludeScreenshots = ScreenshotBox.SelectedIndex >= 1,
        ScreenshotsOnly = ScreenshotBox.SelectedIndex == 2,
        AlbumId = AlbumId ?? f.AlbumId,
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        RemoveFromAlbumButton.Visibility = AlbumId is null ? Visibility.Collapsed : Visibility.Visible;
        JumpList.Visibility = Flat ? Visibility.Collapsed : Visibility.Visible;
        S.Indexing.LibraryChanged += OnLibraryChanged;
        _scroller ??= FindDescendant<ScrollViewer>(Grid);
        if (!_wheelHooked)
        {
            // handledEventsToo: the ScrollViewer marks wheel events handled before they bubble to us.
            Grid.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnGridWheel), handledEventsToo: true);
            _wheelHooked = true;
        }
        if (_scroller is not null) _scroller.ViewChanged += (_, _) => UpdateCurrentDate();
        ApplyTileSize();
        _ = ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPreview();
        S.Indexing.LibraryChanged -= OnLibraryChanged;
        S.SaveSettings();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(() => _ = ReloadAsync(keepPosition: true));

    /// <param name="anchorId">The item to keep in view (default: the first one visible now, with <paramref name="keepPosition"/>).</param>
    public async Task ReloadAsync(bool keepPosition = false, long? anchorId = null)
    {
        if (EffectiveFilter is not { } filter) return;
        var version = ++_loadVersion;
        Busy.IsActive = true;
        var anchor = anchorId ?? (keepPosition ? FirstVisibleItem()?.Id : null);
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
        MarkDayStarts(items, dateOrdered: !Flat && GroupMode == GroupMode.Month);
        _lastSelection.Clear();
        _previousSelection.Clear();
        if (AlbumId is not null)
        {
            // Albums keep their own order and can be rearranged by dragging.
            _albumOrder = new System.Collections.ObjectModel.ObservableCollection<MediaSummary>(items);
            Grid.CanDragItems = Grid.CanReorderItems = Grid.AllowDrop = true;
            Grid.ItemsSource = _albumOrder;
        }
        else
        {
            // Timeline views are grouped by month (headers inside the grid); the jump list navigates between them.
            Grid.CanDragItems = Grid.CanReorderItems = Grid.AllowDrop = false;
            Grid.ItemsSource = GroupMode == GroupMode.None
                ? items
                : new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = MonthGroup.Split(items, GroupMode) }.View;
        }
        CountText.Text = items.Count == 1 ? "1 item" : $"{items.Count:N0} items";
        JumpList.Visibility = Flat ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = EmptyMessage;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildJumpList();
        Busy.IsActive = false;

        if (anchor is { } id && items.FindIndex(i => i.Id == id) is var index and >= 0)
            Grid.ScrollIntoView(items[index], ScrollIntoViewAlignment.Leading);
        if (_restore is not null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyRestore); // after layout
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateCurrentDate);
    }

    private static void MarkDayStarts(List<MediaSummary> items, bool dateOrdered)
    {
        var previous = DateTime.MinValue;
        foreach (var item in items)
        {
            var day = item.TakenLocal.Date;
            item.StartsDay = dateOrdered && day != previous;
            previous = day;
        }
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
        if (args.ItemContainer.ContentTemplateRoot is not Grid root || root.Children[0] is not Border image) return;

        if (args.InRecycleQueue)
        {
            if (ReferenceEquals(_previewRoot, root)) StopPreview();
            (root.Tag as TileState)?.Cancellation.Cancel();
            root.Tag = null;
            image.Background = null;
            return;
        }

        if (args.Phase == 0)
        {
            if (ReferenceEquals(_previewRoot, root)) StopPreview();
            (root.Tag as TileState)?.Cancellation.Cancel();
            image.Background = null;
            SetSelectedVisual(root, args.ItemContainer.IsSelected);
            if (args.Item is MediaSummary item) root.Tag = new TileState(item);
            args.RegisterUpdateCallback(1, OnContainerContentChanging);
        }
        else if (args.Phase == 1 && root.Tag is TileState state)
        {
            _ = LoadThumbnailAsync(root, image, state);
        }
        args.Handled = true;
    }

    private async Task LoadThumbnailAsync(Grid root, Border image, TileState state)
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

        // Thumbnails are small (360 px); decoded whole so the short side still fills the square tile.
        var bitmap = new BitmapImage();
        var brush = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        if (state.Item.Focus is { } focus)
            bitmap.ImageOpened += (_, _) => brush.RelativeTransform = FocusShift(focus, bitmap.PixelWidth, bitmap.PixelHeight);
        bitmap.UriSource = new Uri(path);
        image.Background = brush;
    }

    /// <summary>
    /// Moves a cropped picture so the faces sit in the middle of the square tile (as far as the picture allows),
    /// instead of always keeping the middle of the picture. <paramref name="focus"/> is in fractions of the long side.
    /// </summary>
    private static TranslateTransform? FocusShift((double X, double Y) focus, int width, int height)
    {
        if (width <= 0 || height <= 0 || width == height) return null;
        double longSide = Math.Max(width, height);
        if (width > height)
        {
            // Filled by height: the picture is `aspect` tiles wide, centred; slide it sideways.
            var aspect = width / (double)height;
            var shift = Math.Clamp(aspect * (0.5 - focus.X * longSide / width), -(aspect - 1) / 2, (aspect - 1) / 2);
            return new TranslateTransform { X = shift };
        }
        else
        {
            var aspect = height / (double)width;
            var shift = Math.Clamp(aspect * (0.5 - focus.Y * longSide / height), -(aspect - 1) / 2, (aspect - 1) / 2);
            return new TranslateTransform { Y = shift };
        }
    }

    // ---------- Video previews in the grid ----------

    private MediaPlayer? _previewPlayer;
    private Microsoft.UI.Xaml.Controls.MediaPlayerElement? _previewElement;
    private Grid? _previewRoot;
    private Button? _previewButton;

    /// <summary>The tile's play button: plays the video right there; again pauses, again resumes.</summary>
    private void OnTilePlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindAncestor<Grid>(button, g => g.Tag is TileState) is not { Tag: TileState state } root) return;
        if (ReferenceEquals(_previewRoot, root) && _previewPlayer is { } player)
        {
            if (player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
            {
                player.Pause();
                SetPlayGlyph(button, playing: false);
            }
            else
            {
                player.Play();
                SetPlayGlyph(button, playing: true);
            }
            return;
        }
        StopPreview();
        if (S.Media.GetPath(state.Item.Id) is not { } path) return;
        _previewPlayer ??= CreatePreviewPlayer();
        _previewElement = new Microsoft.UI.Xaml.Controls.MediaPlayerElement { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        _previewElement.SetMediaPlayer(_previewPlayer);
        root.Children.Insert(1, _previewElement); // over the thumbnail, under the badges
        _previewRoot = root;
        _previewButton = button;
        _previewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
        _previewPlayer.Play();
        SetPlayGlyph(button, playing: true);
    }

    private MediaPlayer CreatePreviewPlayer()
    {
        var player = new MediaPlayer { AutoPlay = false };
        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(StopPreview);
        return player;
    }

    private void StopPreview()
    {
        if (_previewPlayer is { } player)
        {
            player.Pause();
            player.Source = null;
        }
        if (_previewRoot is not null && _previewElement is not null) _previewRoot.Children.Remove(_previewElement);
        if (_previewButton is not null) SetPlayGlyph(_previewButton, playing: false);
        _previewElement = null;
        _previewRoot = null;
        _previewButton = null;
    }

    private static void SetPlayGlyph(Button button, bool playing)
    {
        if (button.Content is StackPanel { Children: [FontIcon icon, ..] }) icon.Glyph = playing ? "\uE769" : "\uE768";
    }

    private static T? FindAncestor<T>(DependencyObject start, Func<T, bool> match) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T t && match(t)) return t;
        return null;
    }

    // ---------- Tile size ----------

    private bool _wheelHooked;

    /// <summary>Ctrl + mouse wheel changes the thumbnail size, like Explorer.</summary>
    private void OnGridWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        var delta = e.GetCurrentPoint(Grid).Properties.MouseWheelDelta;
        var anchor = FirstVisibleItem();
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + Math.Sign(delta) * SizeSlider.StepFrequency, SizeSlider.Minimum, SizeSlider.Maximum);
        if (anchor is not null) Grid.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
        e.Handled = true;
    }

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
            if (taken.Month != lastMonth && GroupMode == GroupMode.Month)
            {
                entries.Add(new JumpEntry("   " + CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(taken.Month), i, isYear: false));
                lastMonth = taken.Month;
            }
        }
        JumpList.ItemsSource = entries;
        _jumpEntries = entries;
        _currentJump = null;
    }

    private List<JumpEntry> _jumpEntries = [];
    private JumpEntry? _currentJump;
    private JumpEntry? _currentJumpYear;

    /// <summary>Marks the month (and year) at the top of the grid in the jump list, and keeps it in view there.</summary>
    private void UpdateCurrentJump(int firstVisible)
    {
        if (_jumpEntries.Count == 0 || firstVisible < 0) return;
        var entry = _jumpEntries.LastOrDefault(e => e.Index <= firstVisible && (!e.IsYear || GroupMode != GroupMode.Month)) ?? _jumpEntries[0];
        if (ReferenceEquals(entry, _currentJump)) return;
        if (_currentJump is not null) _currentJump.IsCurrent = false;
        if (_currentJumpYear is not null) _currentJumpYear.IsCurrent = false;
        _currentJump = entry;
        entry.IsCurrent = true;
        _currentJumpYear = _jumpEntries.LastOrDefault(e => e.IsYear && e.Index <= entry.Index);
        if (_currentJumpYear is not null) _currentJumpYear.IsCurrent = true;
        JumpList.ScrollIntoView(entry);
    }

    private void OnJumpClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JumpEntry entry && entry.Index < _items.Count)
            Grid.ScrollIntoView(_items[entry.Index], ScrollIntoViewAlignment.Leading);
    }

    /// <summary>The photo at the top of the visible area (to restore the scroll position later).</summary>
    public long? FirstVisibleId => FirstVisibleItem()?.Id;

    public IReadOnlyList<long> SelectedMediaIds => SelectedIds();

    private (long? AnchorId, IReadOnlyList<long> Selected, long? OpenId)? _restore;

    /// <summary>After the next load: scroll to <paramref name="anchorId"/>, reselect items, optionally re-open the viewer.</summary>
    public void RestoreOnNextLoad(long? anchorId, IReadOnlyList<long> selectedIds, long? openViewerId) =>
        _restore = (anchorId, selectedIds, openViewerId);

    private void ApplyRestore()
    {
        if (_restore is not { } restore) return;
        _restore = null;
        var byId = _items.ToDictionary(i => i.Id);
        if (restore.Selected.Count > 0)
        {
            _restoringSelection = true;
            try
            {
                Grid.SelectedItems.Clear();
                foreach (var id in restore.Selected)
                    if (byId.TryGetValue(id, out var item)) Grid.SelectedItems.Add(item);
                _lastSelection.Clear();
                _lastSelection.AddRange(Grid.SelectedItems.OfType<MediaSummary>());
            }
            finally
            {
                _restoringSelection = false;
            }
        }
        if (restore.AnchorId is { } anchor && byId.TryGetValue(anchor, out var top))
            Grid.ScrollIntoView(top, ScrollIntoViewAlignment.Leading);
        if (restore.OpenId is { } open && byId.TryGetValue(open, out var viewed))
            Open(viewed);
    }

    /// <summary>
    /// The item at the top-left of the visible grid, found by hit-testing (the panel's FirstVisibleIndex isn't
    /// reliable once the grid is grouped by month).
    /// </summary>
    private MediaSummary? FirstVisibleItem()
    {
        if (Grid.ActualWidth <= 0 || XamlRoot is null) return null;
        var origin = Grid.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        for (var y = 8.0; y < Math.Min(Grid.ActualHeight, 600); y += 24)
            foreach (var x in new[] { 24.0, 96.0 })
                foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(origin.X + x, origin.Y + y), Grid))
                    if (element is GridViewItem container && Grid.ItemFromContainer(container) is MediaSummary item) return item;
        return null;
    }

    private void UpdateCurrentDate()
    {
        var first = FirstVisibleItem();
        CurrentDate.Text = !Flat && first is { } item ? item.TakenLocal.ToString("MMMM yyyy", CultureInfo.CurrentCulture) : "";
        if (!Flat && first is { } top) UpdateCurrentJump(_items.IndexOf(top));
    }

    /// <summary>Not in date order: an album's own order, or <see cref="GroupMode.None"/>.</summary>
    private bool Flat => AlbumId is not null || GroupMode == GroupMode.None;

    // ---------- Opening the viewer ----------

    private void OnTileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TileState state)
        {
            RestorePreviousSelection();
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
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(ids.Count == 1 ? "Delete" : $"Delete {ids.Count:N0}", Symbol.Delete, () => _ = DeleteAsync(ids)));
        menu.ShowAt(tile, e.GetPosition(tile));
        e.Handled = true;
    }

    private static MenuFlyoutItem MenuItem(string text, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (AlbumId is not { } albumId || _albumOrder is null) return;
        _items = _albumOrder.ToList();
        S.Collections.SetAlbumOrder(albumId, _items.Select(i => i.Id).ToList());
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
        else if (e.Key == VirtualKey.Delete && Grid.SelectedItems.Count > 0)
        {
            _ = DeleteAsync(SelectedIds());
            e.Handled = true;
        }
    }

    private void OnDeleteSelection(object sender, RoutedEventArgs e) => _ = DeleteAsync(SelectedIds());

    /// <summary>
    /// Deletes items (to the Recycle Bin), keeping the view where it was and selecting the next item, so Delete can
    /// be pressed again to go on weeding.
    /// </summary>
    private async Task DeleteAsync(IReadOnlyList<long> ids)
    {
        var gone = (await Deletion.DeleteAsync(XamlRoot, ids)).ToHashSet();
        if (gone.Count == 0) return;
        var last = _items.FindLastIndex(i => gone.Contains(i.Id));
        var next = _items.Skip(last + 1).FirstOrDefault(i => !gone.Contains(i.Id)) ?? _items.Take(last).LastOrDefault(i => !gone.Contains(i.Id));
        var top = FirstVisibleItem();
        await ReloadAsync(keepPosition: true, anchorId: top is not null && !gone.Contains(top.Id) ? top.Id : next?.Id);
        if (next is not null && _items.FirstOrDefault(i => i.Id == next.Id) is { } again)
        {
            Grid.SelectedItems.Clear();
            Grid.SelectedItem = again;
            if (Grid.ContainerFromItem(again) is Control tile) tile.Focus(FocusState.Programmatic);
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
        if (single.Count == 1) App.MainWindow.OpenViewer(single, 0, (_, _) => { }, this);
    }

    private void Open(MediaSummary item)
    {
        var index = _items.IndexOf(item);
        if (index >= 0) App.MainWindow.OpenViewer(_items, index, OnViewerClosed, this);
    }

    private void RestorePreviousSelection()
    {
        if (_previousSelection.Count == 0) return;
        _restoringSelection = true;
        try
        {
            Grid.SelectedItems.Clear();
            foreach (var item in _previousSelection) Grid.SelectedItems.Add(item);
            _lastSelection.Clear();
            _lastSelection.AddRange(_previousSelection);
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private void OnViewerClosed(int index, bool changed)
    {
        // Keep the selection as it was; just bring the last viewed photo into view.
        if (changed)
            _ = ReloadAsync(keepPosition: true);
        else if (index >= 0 && index < _items.Count)
            Grid.ScrollIntoView(_items[index]);
        Grid.Focus(FocusState.Programmatic);
    }

    // ---------- People filter ----------

    private readonly HashSet<long> _people = [];
    private List<Pages.PersonTile>? _allPeople;
    private bool _settingPeople;

    /// <summary>Named people, most photos first (loaded the first time the list opens).</summary>
    private async void OnPeopleFlyoutOpening(object? sender, object e)
    {
        _allPeople ??= (await Task.Run(() => S.People.GetPeople()))
            .Where(p => p.Name is not null).OrderByDescending(p => p.Count).Select(p => new Pages.PersonTile(p)).ToList();
        ShowPeople();
    }

    private void OnPeopleSearchChanged(object sender, TextChangedEventArgs e) => ShowPeople();

    private void ShowPeople()
    {
        if (_allPeople is null) return;
        var text = PeopleSearch.Text.Trim();
        var shown = _allPeople.Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => _people.Contains(p.Row.Id)).ToList(); // the chosen ones stay at the top
        _settingPeople = true;
        try
        {
            PeopleList.ItemsSource = shown;
            PeopleList.UpdateLayout();
            foreach (var tile in shown.Where(p => _people.Contains(p.Row.Id))) PeopleList.SelectedItems.Add(tile);
        }
        finally
        {
            _settingPeople = false;
        }
    }

    private void OnPeopleContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is Pages.PersonTile tile) _ = tile.EnsureCoverAsync();
    }

    private void OnPeopleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingPeople) return;
        foreach (var tile in e.AddedItems.OfType<Pages.PersonTile>()) _people.Add(tile.Row.Id);
        foreach (var tile in e.RemovedItems.OfType<Pages.PersonTile>()) _people.Remove(tile.Row.Id);
        PeopleChanged();
    }

    private void OnClearPeople(object sender, RoutedEventArgs e)
    {
        _people.Clear();
        PeopleFlyout.Hide();
        PeopleChanged();
    }

    private void PeopleChanged()
    {
        var names = _allPeople?.Where(p => _people.Contains(p.Row.Id)).Select(p => p.Name).ToList() ?? [];
        PeopleText.Text = names.Count switch { 0 => "Anyone", 1 => names[0], _ => $"{names[0]} + {names.Count - 1}" };
        if (_loaded) _ = ReloadAsync();
    }

    // ---------- Filters and selection ----------

    private bool _settingScreenshots;

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded && !_settingScreenshots) _ = ReloadAsync();
    }

    /// <summary>The tile's selection overlay (accent border, tint and check) — stronger than the default highlight.</summary>
    private static void SetSelectedVisual(Grid root, bool selected) =>
        root.Children[^1].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateSelectedVisuals(IEnumerable<object> items, bool selected)
    {
        foreach (var item in items)
            if (Grid.ContainerFromItem(item) is SelectorItem { ContentTemplateRoot: Grid root })
                SetSelectedVisual(root, selected);
    }

    private List<long> SelectedIds() => Grid.SelectedItems.OfType<MediaSummary>().Select(i => i.Id).ToList();

    // Selection before the most recent change: a double-click's first click replaces the selection, and the
    // double-tap handler puts it back so opening a photo never loses what you had selected.
    private readonly List<MediaSummary> _lastSelection = [];
    private readonly List<MediaSummary> _previousSelection = [];
    private bool _restoringSelection;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedVisuals(e.AddedItems, selected: true);
        UpdateSelectedVisuals(e.RemovedItems, selected: false);
        if (!_restoringSelection)
        {
            _previousSelection.Clear();
            _previousSelection.AddRange(_lastSelection);
            _lastSelection.Clear();
            _lastSelection.AddRange(Grid.SelectedItems.OfType<MediaSummary>());
        }
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
