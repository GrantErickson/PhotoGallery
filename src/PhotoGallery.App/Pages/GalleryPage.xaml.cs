using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>What a <see cref="GalleryPage"/> shows. Used for the timeline, search, on this day and albums.</summary>
public sealed record GalleryRequest(string Title, MediaFilter Filter, string? Subtitle = null, long? AlbumId = null, string? EmptyMessage = null,
    long? PersonId = null);

public sealed partial class GalleryPage : Page
{
    public GalleryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var request = e.Parameter as GalleryRequest ?? new GalleryRequest("Photos", MediaFilter.Timeline);
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
