using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>Browse by tag: OneDrive's AI tags, categories and places, plus tags added in this app.</summary>
public sealed partial class TagsPage : Page
{
    private List<TagRow> _all = [];

    public TagsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _all = await Task.Run(App.Services.Collections.GetTags);
        Apply();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => Apply();

    private void OnRareClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Apply();

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();

    private void Apply()
    {
        if (Tags is null) return;
        var text = FilterBox.Text.Trim();
        IEnumerable<TagRow> tags = KindBox.SelectedIndex switch
        {
            0 => _all.Where(t => !t.IsUserTag && t.TagType == TagType.Keyword),
            1 => _all.Where(t => !t.IsUserTag && t.TagType == TagType.Category),
            2 => _all.Where(t => !t.IsUserTag && t.TagType == TagType.Place),
            3 => _all.Where(t => t.IsUserTag),
            _ => _all,
        };
        // OneDrive tags on only a photo or two are mostly noise; show them on request (your own tags always show).
        var minimum = RareBox.IsChecked == true ? 1 : 3;
        tags = tags.Where(t => t.Count >= (t.IsUserTag ? 1 : minimum) && (text.Length == 0 || t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)));
        var list = (SortBox.SelectedIndex == 1 ? tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase) : tags.OrderByDescending(t => t.Count)).ToList();
        Tags.ItemsSource = list;
        SubtitleText.Text = _all.Count == 0
            ? "No tags yet. Sign in to OneDrive in Settings to bring in OneDrive's tags, or tag photos yourself from the viewer."
            : $"{list.Count:N0} tags" + (KindBox.SelectedIndex == 3 ? "" : " from OneDrive");
    }

    private void OnTagClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TagRow tag) Open(tag);
    }

    public static void Open(TagRow tag) =>
        App.MainWindow.Navigate(typeof(GalleryPage), new GalleryRequest(tag.Name, new MediaFilter { TagId = tag.Id, IncludeScreenshots = true },
            tag.Count > 0 ? $"{tag.Count:N0} photos" : null));
}
