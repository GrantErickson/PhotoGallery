using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

public sealed class PersonTile(PersonRow row) : Observable
{
    private ImageSource? _cover;
    private bool _loading;

    public PersonRow Row { get; private set; } = row;
    public string Name => Row.DisplayName;
    public double NameOpacity => Row.Name is null ? 0.6 : 1;
    public string CountText => Row.Count == 1 ? "1 photo" : $"{Row.Count:N0} photos";

    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            _cover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoCover));
        }
    }

    public Visibility NoCover => _cover is null ? Visibility.Visible : Visibility.Collapsed;

    public void Update(PersonRow row)
    {
        Row = row;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NameOpacity));
    }

    public async Task EnsureCoverAsync()
    {
        if (_cover is not null || _loading || Row.CoverMediaId is not { } id) return;
        _loading = true;
        var personId = Row.Id;
        var path = await Task.Run(async () =>
        {
            // Their face as OneDrive found it (or the clearest face among a few photos); otherwise the cover photo's thumbnail.
            var candidates = App.Services.People.GetCoverCandidates(personId, 8)
                .Select(c => (Path: App.Services.Media.GetPath(c.MediaId), c.Box))
                .Where(c => c.Path is not null)
                .Select(c => (c.Path!, c.Box));
            if (await App.Services.Faces.GetOrCreateAsync(personId, candidates) is { } face) return face;
            return App.Services.Media.GetPath(id) is { } source ? await App.Services.Thumbnails.GetOrCreateAsync(id, source) : null;
        });
        if (path is not null) Cover = new BitmapImage(new Uri(path)) { DecodePixelWidth = 300 };
    }
}

/// <summary>People recognised by OneDrive; click for their photos, right-click to name, merge or hide.</summary>
public sealed partial class PeoplePage : Page
{
    private List<PersonTile> _all = [];

    private bool _stale;

    public PeoplePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // keep the scroll position when coming back from a person
        // New people, names or face boxes from OneDrive: rebuild the tiles (and their avatars) now or when next shown.
        App.Services.CloudSync.Completed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) _ = LoadAsync();
            else _stale = true;
        });
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_all.Count == 0 || _stale)
        {
            _stale = false;
            await LoadAsync();
            return;
        }
        // Coming back: refresh names in place (one may have been named on their page) without losing the scroll position.
        var includeHidden = HiddenBox.IsChecked == true; // read on the UI thread
        var rows = await Task.Run(() => App.Services.People.GetPeople(includeHidden));
        var byId = rows.ToDictionary(r => r.Id);
        foreach (var tile in _all)
            if (byId.TryGetValue(tile.Row.Id, out var row)) tile.Update(row);
    }

    private async Task LoadAsync()
    {
        var includeHidden = HiddenBox.IsChecked == true;
        var rows = await Task.Run(() => App.Services.People.GetPeople(includeHidden));
        _all = rows.Select(r => new PersonTile(r)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var text = FilterBox.Text.Trim();
        var includeFew = FewBox.IsChecked == true;
        var visible = _all.Where(p => (includeFew || p.Row.Count > 1 || p.Row.Name is not null) &&
                                      (text.Length == 0 || p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        People.ItemsSource = visible;
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = _all.Count == 0
            ? ""
            : $"{visible.Count:N0} of {_all.Count:N0} people recognised by OneDrive, with the names you gave them there. Right-click to rename, merge or hide. Named people are searchable.";
    }

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, HiddenBox)) _ = LoadAsync();
        else ApplyFilter();
    }

    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PersonTile tile) _ = tile.EnsureCoverAsync();
    }

    private void OnPersonClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PersonTile tile) Open(tile.Row);
    }

    public static void Open(PersonRow person) =>
        App.MainWindow.Navigate(typeof(GalleryPage), new GalleryRequest(person.DisplayName,
            new MediaFilter { PersonId = person.Id, IncludeScreenshots = true },
            person.Count == 1 ? "1 photo" : $"{person.Count:N0} photos", PersonId: person.Id, Section: "people"));

    private void OnPersonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PersonTile tile) return;
        var menu = new MenuFlyout();
        var name = new MenuFlyoutItem { Text = tile.Row.Name is null ? "Name…" : "Rename…", Icon = new SymbolIcon(Symbol.Rename) };
        name.Click += async (_, _) =>
        {
            if (await NameAsync(XamlRoot, tile.Row) is { } updated) tile.Update(updated);
        };
        var merge = new MenuFlyoutItem { Text = "Same person as…", Icon = new SymbolIcon(Symbol.People) };
        merge.Click += async (_, _) =>
        {
            if (await MergeAsync(tile.Row)) await LoadAsync();
        };
        var hide = new MenuFlyoutItem { Text = tile.Row.Hidden ? "Unhide" : "Hide", Icon = new SymbolIcon(tile.Row.Hidden ? Symbol.View : Symbol.Remove) };
        hide.Click += async (_, _) =>
        {
            App.Services.People.SetHidden(tile.Row.Id, !tile.Row.Hidden);
            await LoadAsync();
        };
        menu.Items.Add(name);
        menu.Items.Add(merge);
        menu.Items.Add(hide);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    /// <summary>Asks for a name; returns the updated person, or null if cancelled.</summary>
    public static async Task<PersonRow?> NameAsync(XamlRoot root, PersonRow person)
    {
        var name = await Dialogs.PromptAsync(root, person.Name is null ? "Who is this?" : "Rename person", "Name", person.Name ?? "", "Save");
        if (name is null) return null;
        await Task.Run(() => App.Services.People.Rename(person.Id, name));
        return App.Services.People.Get(person.Id);
    }

    private async Task<bool> MergeAsync(PersonRow source)
    {
        var named = _all.Where(p => p.Row.Id != source.Id && p.Row.Name is not null).OrderBy(p => p.Name).ToList();
        if (named.Count == 0)
        {
            App.MainWindow.ShowStatus("Name the other person first, then merge into them.");
            return false;
        }
        var box = new ComboBox { ItemsSource = named.Select(p => p.Name).ToList(), SelectedIndex = 0, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Same person as…",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "OneDrive sometimes splits one person into several. Their photos will be combined.", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Merge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.SelectedIndex < 0) return false;
        var target = named[box.SelectedIndex].Row;
        await Task.Run(() => App.Services.People.Merge(source.Id, target.Id));
        App.MainWindow.ShowStatus($"Merged into {target.DisplayName}");
        return true;
    }
}
