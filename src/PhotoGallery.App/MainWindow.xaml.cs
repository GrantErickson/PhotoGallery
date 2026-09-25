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
    private DispatcherTimer? _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 950));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        Viewer.Closed += OnViewerClosed;
        var indexing = App.Services.Indexing;
        indexing.ProgressChanged += p => DispatcherQueue.TryEnqueue(() => OnIndexProgress(p));
        indexing.StatusChanged += s => DispatcherQueue.TryEnqueue(() => ShowStatus(s ?? "", sticky: true));

        ContentFrame.Navigate(typeof(GalleryPage), TimelineRequest());
    }

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private static GalleryRequest TimelineRequest() => new("Photos", MediaFilter.Timeline, EmptyMessage:
        "No photos yet. The library is indexed in the background — add folders in Settings if this stays empty.");

    // ---------- Navigation ----------

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
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
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("On this day",
                    new MediaFilter { MonthDay = (today.Month, today.Day), To = today },
                    today.ToString("MMMM d", CultureInfo.CurrentCulture) + " in past years",
                    EmptyMessage: "Nothing from this day in past years."));
                break;
            case "favorites":
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("Favorites", new MediaFilter { MinRating = 4, IncludeScreenshots = true },
                    "Rated 4 stars or more", EmptyMessage: "Rate photos with the stars in the viewer (or keys 1–5) and your 4 and 5 star photos show up here."));
                break;
            case "live":
                ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest("Live Photos", new MediaFilter { MotionOnly = true },
                    "iPhone Live Photos and Android motion photos"));
                break;
            case "folders":
                ContentFrame.Navigate(typeof(FoldersPage));
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
        Nav.SelectedItem = null;
        ContentFrame.Navigate(typeof(GalleryPage), new GalleryRequest($"“{text}”", new MediaFilter { Text = text, IncludeScreenshots = true },
            "File and folder names, tags and cameras", EmptyMessage: "No matches."));
    }

    // ---------- Viewer ----------

    public void OpenViewer(IReadOnlyList<MediaSummary> items, int index, Action<int, bool> closed)
    {
        _viewerClosed = closed;
        Viewer.Visibility = Visibility.Visible;
        Viewer.Show(items, index);
    }

    private void OnViewerClosed(int index, bool changed)
    {
        Viewer.Visibility = Visibility.Collapsed;
        var callback = _viewerClosed;
        _viewerClosed = null;
        callback?.Invoke(index, changed);
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
