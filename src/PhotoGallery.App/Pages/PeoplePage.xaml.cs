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
    /// <summary>The same, under a name that doesn't clash with the gallery's own CountText element (x:Bind in its people filter).</summary>
    public string PhotoCountText => CountText;

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

/// <summary>
/// People recognised by OneDrive; click for their photos, right-click to name, join to someone or set aside. Names and
/// joins go to OneDrive too; setting aside ("known, but don't tag", "not someone I know") stays on this PC.
/// </summary>
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
        // Coming back: refresh names in place (one may have been named on their page, or set aside while reviewing)
        // without losing the scroll position.
        var rows = await Task.Run(() => App.Services.People.GetPeople(includeHidden: true));
        var byId = rows.ToDictionary(r => r.Id);
        _all.RemoveAll(t => !byId.ContainsKey(t.Row.Id)); // joined to someone
        foreach (var tile in _all) tile.Update(byId[tile.Row.Id]);
        ApplyFilter(keepScroll: true);
    }

    private async Task LoadAsync()
    {
        var rows = await Task.Run(() => App.Services.People.GetPeople(includeHidden: true));
        _all = rows.Select(r => new PersonTile(r)).ToList();
        ApplyFilter();
    }

    /// <summary>Which people the Show box asks for: named and unnamed, or those set aside.</summary>
    private bool Shown(PersonRow row) => ShowBox.SelectedIndex switch
    {
        1 => row.NotTagged && !row.Hidden,
        2 => row.Hidden,
        _ => !row.NotTagged && !row.Hidden,
    };

    private void ApplyFilter(bool keepScroll = false)
    {
        var text = FilterBox.Text.Trim();
        var includeFew = FewBox.IsChecked == true || ShowBox.SelectedIndex > 0;
        var visible = _all.Where(p => Shown(p.Row) && (includeFew || p.Row.Count > 1 || p.Row.Name is not null) &&
                                      (text.Length == 0 || p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        // Unchanged (names updated in place): keep the list, and with it the scroll position.
        if (!keepScroll || People.ItemsSource is not List<PersonTile> shown || !shown.SequenceEqual(visible)) People.ItemsSource = visible;
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var toReview = _all.Count(p => p.Row.Name is null && !p.Row.Hidden && !p.Row.NotTagged);
        WhoText.Text = toReview > 0 ? $"Who's this? ({toReview:N0})" : "Who's this?";
        SubtitleText.Text = _all.Count == 0 ? ""
            : ShowBox.SelectedIndex switch
            {
                1 => $"{visible.Count:N0} people you know but didn't want tagged. Right-click to name them after all, or to look at them again in Who's this?",
                2 => $"{visible.Count:N0} people you don't know. Right-click to show them again.",
                _ => $"{visible.Count:N0} people recognised by OneDrive, with the names given there or here. Right-click to name, join or set aside; names and joins go to OneDrive too.",
            };
    }

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void OnFilterClick(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnShowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_all.Count > 0) ApplyFilter();
    }

    private void OnWho(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(WhoPage), null);

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
            if (await NameAsync(XamlRoot, tile.Row) is not { } updated) return;
            if (updated.Id == tile.Row.Id) tile.Update(updated);
            else await LoadAsync(); // joined to someone
        };
        var merge = new MenuFlyoutItem { Text = "Same person as…", Icon = new SymbolIcon(Symbol.People) };
        merge.Click += async (_, _) =>
        {
            if (await MergeAsync(tile.Row)) await LoadAsync();
        };
        menu.Items.Add(name);
        menu.Items.Add(merge);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (tile.Row.Name is null && !tile.Row.Hidden)
        {
            var notTagged = new MenuFlyoutItem
            {
                Text = tile.Row.NotTagged ? "Look at again in Who's this?" : "Known, but don't tag",
                Icon = new FontIcon { Glyph = tile.Row.NotTagged ? "\uE8FA" : "\uE8F8" },
            };
            notTagged.Click += async (_, _) =>
            {
                App.Services.People.SetNotTagged(tile.Row.Id, !tile.Row.NotTagged);
                await LoadAsync();
            };
            menu.Items.Add(notTagged);
        }
        var hide = new MenuFlyoutItem
        {
            Text = tile.Row.Hidden ? "Show again" : "Not someone I know",
            Icon = new SymbolIcon(tile.Row.Hidden ? Symbol.View : Symbol.Remove),
        };
        hide.Click += async (_, _) =>
        {
            App.Services.People.SetHidden(tile.Row.Id, !tile.Row.Hidden);
            if (!tile.Row.Hidden) App.Services.People.SetNotTagged(tile.Row.Id, false);
            await LoadAsync();
        };
        menu.Items.Add(hide);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    /// <summary>
    /// Asks for a name (here and in OneDrive); returns the updated person, or null if cancelled. A name someone else
    /// already has offers to join the two instead: one name, one person.
    /// </summary>
    public static async Task<PersonRow?> NameAsync(XamlRoot root, PersonRow person)
    {
        var name = await Dialogs.PromptAsync(root, person.Name is null ? "Who is this?" : "Rename person", "Name", person.Name ?? "", "Save");
        if (name is null) return null;
        name = name.Trim();
        var other = name.Length == 0 ? null
            : App.Services.People.GetPeople(includeHidden: true)
                .FirstOrDefault(p => p.Id != person.Id && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (other is not null)
        {
            if (!await Dialogs.ConfirmAsync(root, $"Same person as {other.Name}?",
                    $"{other.Name} is already someone. These photos will be joined to theirs, here and in OneDrive. OneDrive can't undo that.",
                    "Join them")) return null;
            await Task.Run(() => App.Services.People.Merge(person.Id, other.Id));
            App.MainWindow.ShowStatus($"Joined to {other.Name}");
            return App.Services.People.Get(other.Id);
        }
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
                    new TextBlock { Text = "OneDrive sometimes splits one person into several. Their photos will be joined, here and in OneDrive (OneDrive can't undo that).", TextWrapping = TextWrapping.Wrap },
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
