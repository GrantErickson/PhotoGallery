using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>What a <see cref="GalleryPage"/> shows. Used for the timeline, search, on this day and albums.</summary>
/// <param name="Section">The menu item this view belongs to (highlighted after Back/Forward).</param>
/// <param name="Group">Month headers (date order), or <see cref="GroupMode.None"/> for the filter's own order.</param>
/// <param name="Search">Search results for these words: the page runs the search and offers exact words, sorting and grouping.</param>
public sealed record GalleryRequest(string Title, MediaFilter Filter, string? Subtitle = null, long? AlbumId = null, string? EmptyMessage = null,
    long? PersonId = null, string? Section = null, GroupMode Group = GroupMode.Month, string? Search = null);

/// <summary>What a gallery looked like when it was left, restored when coming back to it.</summary>
internal sealed class GalleryNavState
{
    public long? AnchorId { get; set; }
    public IReadOnlyList<long> SelectedIds { get; set; } = [];
    public long? ReopenViewerOnBack { get; set; }
    public long? ReopenViewerOnForward { get; set; }
    /// <summary>A search's choices: exact words only, sort (0 best match, 1 newest, 2 oldest), grouping (0 none … 3 year).</summary>
    public (bool Exact, int Sort, int Group)? SearchChoices { get; set; }
}

public sealed partial class GalleryPage : Page
{
    public GalleryPage()
    {
        InitializeComponent();
    }

    // Keyed by the request object, which the Frame keeps in its back/forward stack entry.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GalleryRequest, GalleryNavState> States = new();
    private GalleryRequest? _request;

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (_request is null) return;
        var (reopenOnBack, reopenOnForward) = App.MainWindow.TakeViewerHistory(Gallery);
        States.AddOrUpdate(_request, new GalleryNavState
        {
            AnchorId = Gallery.FirstVisibleId,
            SelectedIds = Gallery.SelectedMediaIds,
            ReopenViewerOnBack = reopenOnBack,
            // A viewer backed out of stays forward-able only while we're moving back through history.
            ReopenViewerOnForward = e.NavigationMode == NavigationMode.Back ? reopenOnForward : null,
            SearchChoices = _request.Search is null ? null : (ExactToggle.IsChecked == true, SortBox.SelectedIndex, GroupBox.SelectedIndex),
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var request = e.Parameter as GalleryRequest ?? new GalleryRequest("Photos", MediaFilter.Timeline, Section: "timeline");
        _request = request;
        GalleryNavState? restored = null;
        if (e.NavigationMode != NavigationMode.New && States.TryGetValue(request, out var state))
        {
            restored = state;
            Gallery.RestoreOnNextLoad(state.AnchorId, state.SelectedIds, e.NavigationMode == NavigationMode.Back ? state.ReopenViewerOnBack : null);
            if (e.NavigationMode == NavigationMode.Forward && state.ReopenViewerOnForward is { } forwardId)
                App.MainWindow.RestoreForwardViewer(Gallery, forwardId);
        }
        TitleText.Text = request.Title;
        SubtitleText.Text = request.Subtitle ?? "";
        Gallery.AlbumId = request.AlbumId;
        Gallery.GroupMode = request.Group;
        Gallery.EmptyMessage = request.EmptyMessage ?? "Nothing here yet.";
        SearchBar.Visibility = request.Search is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        if (request.Search is { } text)
        {
            _choosing = true;
            var (exact, sort, group) = restored?.SearchChoices ?? (false, 0, 2);
            ExactToggle.IsChecked = exact;
            SortBox.SelectedIndex = sort;
            GroupBox.SelectedIndex = group;
            _choosing = false;
            _ = RunSearchAsync(text);
        }
        else Gallery.BaseFilter = request.Filter;
        _personId = request.PersonId;
        NameButton.Visibility = request.PersonId is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    // ---------- Search results ----------

    private bool _choosing;
    private (string Text, bool Exact, List<long> Ids, bool Pictures)? _found;
    private int _searchVersion;

    private void OnExactClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SearchChoiceChanged();

    private void OnSearchChoiceChanged(object sender, SelectionChangedEventArgs e) => SearchChoiceChanged();

    private void SearchChoiceChanged()
    {
        if (_choosing || _request?.Search is not { } text) return;
        _ = RunSearchAsync(text);
    }

    /// <summary>
    /// Best matches (what's in the picture as well as the words) or exact words only, then sorted by match or date and
    /// grouped by day, month or year when sorted by date.
    /// </summary>
    private async Task RunSearchAsync(string text)
    {
        var version = ++_searchVersion;
        var exact = ExactToggle.IsChecked == true;
        var byMatch = SortBox.SelectedIndex <= 0;
        GroupBox.IsEnabled = !byMatch;
        if (_found is not { } found || found.Text != text || found.Exact != exact)
        {
            SearchBusy.IsActive = true;
            try
            {
                var (ids, pictures) = exact
                    ? (await Task.Run(() => App.Services.Media.SearchWords(text)), false)
                    : await App.Services.Similar.SearchAsync(text);
                found = (text, exact, ids, pictures);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                PhotoGallery.Core.Log.Error($"Searching for \"{text}\" failed", ex);
                App.MainWindow.ShowStatus($"Couldn't match pictures ({ex.Message}); showing exact words.");
                found = (text, exact, await Task.Run(() => App.Services.Media.SearchWords(text)), false);
            }
            finally
            {
                if (version == _searchVersion) SearchBusy.IsActive = false;
            }
            if (version != _searchVersion) return;
            _found = found;
        }

        SubtitleText.Text = exact
            ? "Exact words in names, folders, tags, people, places, cameras, text in photos and what's said in videos"
            : found.Pictures
                ? "Best matches: what's in the picture, and the words in names, tags, people, places and text"
                : "Words in names, tags, people, places and text (matching pictures starts once the AI model is ready; see Settings)";
        Gallery.EmptyMessage = exact
            ? "No exact matches here. Turn off “Exact words” for photos that look like it, or loosen the filters."
            : "No matches here. Try loosening the filters.";
        Gallery.GroupMode = byMatch ? GroupMode.None : GroupBox.SelectedIndex switch { 1 => GroupMode.Day, 2 => GroupMode.Month, 3 => GroupMode.Year, _ => GroupMode.None };
        Gallery.BaseFilter = new MediaFilter
        {
            Ids = found.Ids,
            Order = byMatch ? MediaOrder.Listed : SortBox.SelectedIndex == 2 ? MediaOrder.Oldest : MediaOrder.Newest,
            IncludeScreenshots = true,
        };
    }

    private long? _personId;

    private async void OnNamePerson(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_personId is not { } id || App.Services.People.Get(id) is not { } person) return;
        if (await PeoplePage.NameAsync(XamlRoot, person) is { } updated) TitleText.Text = updated.DisplayName;
    }
}
