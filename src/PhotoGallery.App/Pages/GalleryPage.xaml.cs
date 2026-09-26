using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>What a <see cref="GalleryPage"/> shows. Used for the timeline, search, on this day and albums.</summary>
/// <param name="Section">The menu item this view belongs to (highlighted after Back/Forward).</param>
public sealed record GalleryRequest(string Title, MediaFilter Filter, string? Subtitle = null, long? AlbumId = null, string? EmptyMessage = null,
    long? PersonId = null, string? Section = null);

/// <summary>What a gallery looked like when it was left, restored when coming back to it.</summary>
internal sealed class GalleryNavState
{
    public long? AnchorId { get; set; }
    public IReadOnlyList<long> SelectedIds { get; set; } = [];
    public long? ReopenViewerOnBack { get; set; }
    public long? ReopenViewerOnForward { get; set; }
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
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var request = e.Parameter as GalleryRequest ?? new GalleryRequest("Photos", MediaFilter.Timeline, Section: "timeline");
        _request = request;
        if (e.NavigationMode != NavigationMode.New && States.TryGetValue(request, out var state))
        {
            Gallery.RestoreOnNextLoad(state.AnchorId, state.SelectedIds, e.NavigationMode == NavigationMode.Back ? state.ReopenViewerOnBack : null);
            if (e.NavigationMode == NavigationMode.Forward && state.ReopenViewerOnForward is { } forwardId)
                App.MainWindow.RestoreForwardViewer(Gallery, forwardId);
        }
        TitleText.Text = request.Title;
        SubtitleText.Text = request.Subtitle ?? "";
        Gallery.AlbumId = request.AlbumId;
        Gallery.EmptyMessage = request.EmptyMessage ?? "Nothing here yet.";
        Gallery.BaseFilter = request.Filter;
        _personId = request.PersonId;
        NameButton.Visibility = request.PersonId is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    private long? _personId;

    private async void OnNamePerson(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_personId is not { } id || App.Services.People.Get(id) is not { } person) return;
        if (await PeoplePage.NameAsync(XamlRoot, person) is { } updated) TitleText.Text = updated.DisplayName;
    }
}
