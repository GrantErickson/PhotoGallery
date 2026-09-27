using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Pages;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Indexing;

namespace PhotoGallery.App;

public sealed partial class MainWindow : Window
{
    private Action<int, bool>? _viewerClosed;
    private Controls.GalleryView? _viewerOwner;
    private DispatcherTimer? _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 950));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        Viewer.Closed += OnViewerClosed;
        // Mouse back/forward buttons anywhere in the window (handledEventsToo: grids and viewers handle presses).
        Root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);
        AppWindow.Closing += OnClosing;
        var indexing = App.Services.Indexing;
        indexing.ProgressChanged += p => DispatcherQueue.TryEnqueue(() => OnIndexProgress(p));
        indexing.StatusChanged += s => DispatcherQueue.TryEnqueue(() => ShowStatus(s ?? "", sticky: true));
        App.Services.CloudSync.StatusChanged += s => DispatcherQueue.TryEnqueue(() => ShowStatus(s, sticky: true));

        ContentFrame.Navigate(typeof(GalleryPage), TimelineRequest());
    }

    // ---------- History (back / forward) ----------
    //
    // Page history is the Frame's back and forward stacks. The viewer and editor are overlays on the current page:
    // Back closes the top overlay first (editor, then viewer), then goes to the previous page. A viewer closed with
    // Back can be re-opened with Forward; any new navigation drops that, like a browser.

    private (Controls.GalleryView Owner, long ItemId)? _forwardViewer;
    private (Controls.GalleryView Owner, long ItemId)? _viewerHandoff;

    public async void GoBack()
    {
        if (VideoEditor.Visibility == Visibility.Visible)
        {
            await VideoEditor.RequestCloseAsync();
        }
        else if (Editor.Visibility == Visibility.Visible)
        {
            await Editor.RequestCloseAsync();
        }
        else if (Viewer.Visibility == Visibility.Visible)
        {
            Viewer.Close(); // OnViewerClosed records it as "forward"
        }
        else if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
        UpdateBackButton();
    }

    public void GoForward()
    {
        if (IsEditorOpen) return;
        if (Viewer.Visibility != Visibility.Visible && _forwardViewer is { } forward && IsOnCurrentPage(forward.Owner))
        {
            _forwardViewer = null;
            forward.Owner.OpenItem(forward.ItemId);
        }
        else if (ContentFrame.CanGoForward)
        {
            if (Viewer.Visibility == Visibility.Visible) Viewer.Close();
            _forwardViewer = null;
            ContentFrame.GoForward();
        }
        UpdateBackButton();
    }

    public bool CanGoBack => IsEditorOpen || Viewer.Visibility == Visibility.Visible || ContentFrame.CanGoBack;

    private void UpdateBackButton() => Nav.IsBackEnabled = CanGoBack;

    private bool IsOnCurrentPage(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ContentFrame.Content)) return true;
        return false;
    }

    /// <summary>Closes the viewer and navigates, so that Back returns to the viewer on the same photo.</summary>
    public void NavigateFromViewer(Action navigate)
    {
        if (_viewerOwner is { } owner && Viewer.CurrentId is { } id) _viewerHandoff = (owner, id);
        Viewer.Close();
        _forwardViewer = null;
        navigate();
    }

    /// <summary>Called by a gallery page as it's left: the viewer to re-open on return, and one Forward could re-open.</summary>
    public (long? ReopenOnBack, long? ReopenOnForward) TakeViewerHistory(Controls.GalleryView gallery)
    {
        long? reopen = _viewerHandoff is { } h && ReferenceEquals(h.Owner, gallery) ? h.ItemId : null;
        long? forward = _forwardViewer is { } f && ReferenceEquals(f.Owner, gallery) ? f.ItemId : null;
        _viewerHandoff = null;
        _forwardViewer = null;
        return (reopen, forward);
    }

    /// <summary>A page returned to with Forward restores the viewer it had backed out of.</summary>
    public void RestoreForwardViewer(Controls.GalleryView gallery, long itemId) => _forwardViewer = (gallery, itemId);

    private void OnRootPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Root).Properties;
        if (props.IsXButton1Pressed)
        {
            GoBack();
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            GoForward();
            e.Handled = true;
        }
    }

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.GoBack:
            case Windows.System.VirtualKey.Left when alt:
                GoBack();
                break;
            case Windows.System.VirtualKey.GoForward:
            case Windows.System.VirtualKey.Right when alt:
                GoForward();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    private void OnFrameNavigating(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
    {
        // Leaving the page closes any overlay (pages save their viewer history in OnNavigatingFrom first).
        if (VideoEditor.Visibility == Visibility.Visible) VideoEditor.CloseWithoutSaving();
        if (Editor.Visibility == Visibility.Visible) Editor.CloseWithoutSaving();
        if (Viewer.Visibility == Visibility.Visible)
        {
            var forward = _forwardViewer;
            Viewer.Close();
            _forwardViewer = forward;
        }
    }

    private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.New) _forwardViewer = null;
        SyncNavSelection(e.SourcePageType, e.Parameter);
        UpdateBackButton();
    }

    private bool _syncingNav;

    /// <summary>Highlights the menu item for the page shown (after Back/Forward or links), without navigating again.</summary>
    private void SyncNavSelection(Type page, object? parameter)
    {
        var tag = page == typeof(GalleryPage) ? (parameter as GalleryRequest)?.Section
            : page == typeof(OnThisDayPage) ? "onthisday"
            : page == typeof(PeoplePage) ? "people"
            : page == typeof(TagsPage) ? "tags"
            : page == typeof(FoldersPage) ? "folders"
            : page == typeof(MapPage) ? "map"
            : page == typeof(AlbumsPage) ? "albums"
            : page == typeof(DuplicatesPage) ? "duplicates"
            : page == typeof(SettingsPage) ? "settings"
            : null;
        object? item = tag == "settings"
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        if (ReferenceEquals(Nav.SelectedItem, item)) return;
        _syncingNav = true;
        try
        {
            Nav.SelectedItem = item;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    private bool _closeConfirmed;

    /// <summary>Photos with edits kept only in the gallery aren't lost on exit, but the user should know no file has them.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        var pending = App.Services.Media.CountEdited();
        if (pending == 0) return;
        args.Cancel = true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = pending == 1 ? "1 photo has unsaved edits" : $"{pending:N0} photos have unsaved edits",
            Content = "These edits are kept in the gallery (and shown here next time), but they haven't been written to any file. " +
                      "Open each one and choose Save as copy or Overwrite original to make them part of the photo.",
            PrimaryButtonText = "Show them",
            SecondaryButtonText = "Exit anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("Unsaved edits", new MediaFilter { EditedOnly = true, IncludeScreenshots = true },
                    "Edits kept only in the gallery", EmptyMessage: "No photos with unsaved edits."));
                break;
            case ContentDialogResult.Secondary:
                _closeConfirmed = true;
                Close();
                break;
        }
    }

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private static GalleryRequest TimelineRequest() => new("Photos", MediaFilter.Timeline, EmptyMessage:
        "No photos yet. The library is indexed in the background — add folders in Settings if this stays empty.", Section: "timeline");

    // ---------- Navigation ----------

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav) return;
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        var today = DateTime.Today;
        switch ((args.SelectedItem as NavigationViewItem)?.Tag as string)
        {
            case "timeline":
                ContentFrame.Navigate(typeof(GalleryPage), TimelineRequest());
                break;
            case "onthisday":
                ContentFrame.Navigate(typeof(OnThisDayPage));
                break;
            case "favorites":
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("Favorites", new MediaFilter { MinRating = 4, IncludeScreenshots = true },
                    "Rated 4 stars or more", EmptyMessage: "Rate photos with the stars in the viewer (or keys 1–5) and your 4 and 5 star photos show up here.",
                    Section: "favorites"));
                break;
            case "live":
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("Live Photos", new MediaFilter { MotionOnly = true },
                    "iPhone Live Photos and Android motion photos", Section: "live"));
                break;
            case "folders":
                ContentFrame.Navigate(typeof(FoldersPage));
                break;
            case "people":
                ContentFrame.Navigate(typeof(PeoplePage));
                break;
            case "tags":
                ContentFrame.Navigate(typeof(TagsPage));
                break;
            case "map":
                ContentFrame.Navigate(typeof(MapPage));
                break;
            case "duplicates":
                ContentFrame.Navigate(typeof(DuplicatesPage));
                break;
            case "albums":
                ContentFrame.Navigate(typeof(AlbumsPage));
                break;
        }
    }

    public void Navigate(Type page, object? parameter) => ContentFrame.Navigate(page, parameter);

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => Search(args.QueryText);

    public void Search(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        SearchBox.Text = text;
        ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest($"“{text}”", new MediaFilter { Text = text, IncludeScreenshots = true },
            "File and folder names, tags, people, cameras, text in photos and what's said in videos", EmptyMessage: "No matches."));
    }

    // ---------- Viewer ----------

    /// <summary>Opens the viewer; <paramref name="owner"/> (the gallery it was opened from) lets Forward re-open it.</summary>
    public void OpenViewer(IReadOnlyList<MediaSummary> items, int index, Action<int, bool> closed, Controls.GalleryView? owner = null)
    {
        _viewerClosed = closed;
        _viewerOwner = owner;
        _forwardViewer = null;
        Viewer.Visibility = Visibility.Visible;
        // Opened from search results: what was searched for is highlighted in transcripts.
        Viewer.Show(items, index, owner?.BaseFilter?.Text);
        UpdateBackButton();
    }

    public bool IsEditorOpen => Editor.Visibility == Visibility.Visible || VideoEditor.Visibility == Visibility.Visible;

    /// <summary>Opens the video editor over the viewer; the callback reports whether a video was saved.</summary>
    public void OpenVideoEditor(PhotoGallery.Core.Media.MediaItem item, string videoPath, bool livePhoto, TimeSpan startAt, Action<bool> closed)
    {
        void OnClosed(bool saved)
        {
            VideoEditor.Closed -= OnClosed;
            VideoEditor.Visibility = Visibility.Collapsed;
            closed(saved);
            UpdateBackButton();
        }
        VideoEditor.Closed += OnClosed;
        VideoEditor.Visibility = Visibility.Visible;
        VideoEditor.Open(item, videoPath, livePhoto, startAt);
        UpdateBackButton();
    }

    /// <summary>Opens the editor over the viewer; the callback reports whether edits were saved.</summary>
    public void OpenEditor(PhotoGallery.Core.Media.MediaItem item, Action<bool> closed)
    {
        void OnClosed(bool saved)
        {
            Editor.Closed -= OnClosed;
            Editor.Visibility = Visibility.Collapsed;
            closed(saved);
            UpdateBackButton();
        }
        Editor.Closed += OnClosed;
        Editor.Visibility = Visibility.Visible;
        _ = Editor.OpenAsync(item);
        UpdateBackButton();
    }

    private void OnViewerClosed(int index, bool changed)
    {
        var id = Viewer.CurrentId;
        Viewer.Visibility = Visibility.Collapsed;
        if (_viewerOwner is { } owner && id is { } itemId) _forwardViewer = (owner, itemId);
        _viewerOwner = null;
        var callback = _viewerClosed;
        _viewerClosed = null;
        callback?.Invoke(index, changed);
        UpdateBackButton();
    }

    // ---------- Status ----------

    private void OnIndexProgress(IndexProgress p)
    {
        switch (p.Phase)
        {
            case IndexPhase.Scanning:
                IndexProgress.Visibility = Visibility.Visible;
                IndexProgress.IsIndeterminate = true;
                StatusText.Text = $"Scanning library… {p.Found:N0} files";
                break;
            case IndexPhase.Reading:
                IndexProgress.Visibility = Visibility.Visible;
                IndexProgress.IsIndeterminate = false;
                IndexProgress.Maximum = Math.Max(1, p.ToProcess);
                IndexProgress.Value = p.Processed;
                StatusText.Text = $"Reading metadata {p.Processed:N0} / {p.ToProcess:N0}";
                break;
            case IndexPhase.Finishing:
                IndexProgress.IsIndeterminate = true;
                StatusText.Text = "Finishing…";
                break;
            case IndexPhase.Done:
                IndexProgress.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Shows a message in the status bar; non-sticky messages clear after a few seconds.</summary>
    public void ShowStatus(string message, bool sticky = false)
    {
        if (Viewer.Visibility == Visibility.Visible && !sticky) Viewer.ShowToast(message);
        StatusText.Text = message;
        _statusTimer?.Stop();
        if (sticky) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            if (StatusText.Text == message) StatusText.Text = "";
        };
        _statusTimer.Start();
    }
}
