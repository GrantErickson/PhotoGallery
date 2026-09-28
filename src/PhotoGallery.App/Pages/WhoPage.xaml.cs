using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>One of a person's faces on the review page; cropped when shown.</summary>
public sealed class FaceSampleTile(FaceSample sample) : Observable
{
    private ImageSource? _image;

    public FaceSample Sample { get; } = sample;

    public string DateText =>
        DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(Sample.DateTaken), DateTimeKind.Unspecified).ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public ImageSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoImage));
        }
    }

    public Visibility NoImage => _image is null ? Visibility.Visible : Visibility.Collapsed;

    public async Task LoadAsync(CancellationToken ct)
    {
        var path = await Task.Run(async () => App.Services.Media.GetPath(Sample.MediaId) is { } source
            ? await App.Services.Faces.GetSampleAsync(Sample.MediaId, source, Sample.Box)
            : null);
        if (path is not null && !ct.IsCancellationRequested) Image = new BitmapImage(new Uri(path)) { DecodePixelWidth = 192 };
    }
}

/// <summary>
/// Goes through the people OneDrive found but nobody named, most photos first: name them (here and in OneDrive), join
/// them to someone already named (here and in OneDrive), or set them aside as known but not worth tagging, or as not
/// someone you know (those two stay on this PC, and People has them to change your mind).
/// </summary>
public sealed partial class WhoPage : Page
{
    private const int SampleCount = 8;
    private static Services.AppServices S => App.Services;

    private List<PersonRow> _queue = [];
    private List<PersonRow> _named = [];
    /// <summary>What was decided for people in this session, shown when going back to them.</summary>
    private readonly Dictionary<long, string> _decisions = [];
    private int _index = -1;
    private PersonRow? _current;
    private long? _shownMatch;
    private bool _loaded;
    private bool _changedAny;
    private CancellationTokenSource _loading = new();

    public WhoPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // back from a person's photos to the same place
        S.PeopleChanges.StateChanged += () => DispatcherQueue.TryEnqueue(ShowSending);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ShowSending();
        var named = await Task.Run(NamedPeople);
        _named = named;
        if (_loaded) return;
        _loaded = true;
        _queue = await Task.Run(S.People.GetToReview);
        Show(0);
    }

    private static List<PersonRow> NamedPeople() =>
        S.People.GetPeople(includeHidden: true).Where(p => p.Name is not null).OrderByDescending(p => p.Count).ToList();

    // ---------- Showing one person ----------

    /// <summary>Moves on (or back), past people joined to someone meanwhile.</summary>
    private void Go(int direction)
    {
        var index = _index + direction;
        while (index >= 0 && index < _queue.Count && S.People.Get(_queue[index].Id) is null) index += direction;
        if (index >= 0) Show(index);
    }

    private async void Show(int index)
    {
        _loading.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;
        _index = index;
        PreviousButton.IsEnabled = index > 0;
        var done = index >= _queue.Count;
        DoneText.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        Faces.Visibility = AnswerPanel.Visibility = HintText.Visibility = AllPhotosLink.Visibility =
            done ? Visibility.Collapsed : Visibility.Visible;
        NextButton.IsEnabled = !done;
        if (done)
        {
            _current = null;
            Faces.ItemsSource = null;
            ProgressText.Text = AboutText.Text = DecisionText.Text = "";
            DoneText.Text = _queue.Count == 0
                ? "Everyone OneDrive found has a name or has been set aside."
                : "That's everyone for now. People has the ones you set aside, if you change your mind.";
            return;
        }

        var person = S.People.Get(_queue[index].Id) ?? _queue[index];
        _current = person;
        ProgressText.Text = $"{index + 1:N0} of {_queue.Count:N0}";
        AllPhotosLink.Content = person.Count == 1 ? "See the photo" : $"See all {person.Count:N0} photos";
        DecisionText.Text = _decisions.TryGetValue(person.Id, out var decision) ? decision : "";
        AboutText.Text = "";
        NameBox.Text = person.Name ?? "";
        NameBox.ItemsSource = null;
        UpdateSave();
        NameBox.Focus(FocusState.Programmatic);
        try
        {
            var (samples, span) = await Task.Run(() => (S.People.GetFaceSamples(person.Id, SampleCount), S.People.GetSpan(person.Id)), ct);
            if (ct.IsCancellationRequested) return;
            AboutText.Text = span is (var first, var last) ? $"In photos from {Year(first)}{(Year(last) != Year(first) ? $" to {Year(last)}" : "")}" : "";
            var tiles = samples.Select(s => new FaceSampleTile(s)).ToList();
            Faces.ItemsSource = tiles;
            foreach (var tile in tiles) _ = tile.LoadAsync(ct);
            Prefetch(index + 1);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int Year(long unix) => DateTime.UnixEpoch.AddSeconds(unix).Year;

    /// <summary>Crops the next person's faces meanwhile, so they show at once.</summary>
    private void Prefetch(int index)
    {
        if (index >= _queue.Count) return;
        var id = _queue[index].Id;
        _ = Task.Run(async () =>
        {
            foreach (var sample in S.People.GetFaceSamples(id, SampleCount))
                if (S.Media.GetPath(sample.MediaId) is { } path) await S.Faces.GetSampleAsync(sample.MediaId, path, sample.Box);
        });
    }

    private void OnFaceClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FaceSampleTile tile || Faces.ItemsSource is not List<FaceSampleTile> tiles) return;
        var items = S.Media.Query(new MediaFilter { Ids = tiles.Select(t => t.Sample.MediaId).ToList(), Order = MediaOrder.Listed, IncludeScreenshots = true });
        var index = items.FindIndex(i => i.Id == tile.Sample.MediaId);
        if (index >= 0) App.MainWindow.OpenViewer(items, index, (_, _) => { });
    }

    private void OnAllPhotos(object sender, RoutedEventArgs e)
    {
        if (_current is { } person) PeoplePage.Open(S.People.Get(person.Id) ?? person);
    }

    // ---------- Naming, or joining to someone named ----------

    /// <summary>Someone else already named this (ignoring case): naming would join them.</summary>
    private PersonRow? Match(string text) =>
        _named.FirstOrDefault(p => p.Id != _current?.Id && string.Equals(p.Name, text, StringComparison.CurrentCultureIgnoreCase));

    private void OnNameChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var text = sender.Text.Trim();
            sender.ItemsSource = text.Length == 0
                ? null
                : _named.Where(p => p.Id != _current?.Id && p.Name!.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(p => p.Name!.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                    .ThenByDescending(p => p.Count)
                    .Take(10)
                    .Select(p => p.Name!)
                    .ToList();
        }
        UpdateSave();
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) => UpdateSave();

    private void OnNameSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Choosing someone from the list only fills in their name; the button then shows who they'd be joined to.
        if (args.ChosenSuggestion is string chosen)
        {
            sender.Text = chosen;
            UpdateSave();
            return;
        }
        Save();
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private async void UpdateSave()
    {
        var text = NameBox.Text.Trim();
        var match = Match(text);
        SaveButton.IsEnabled = _current is not null && text.Length > 0 && (match is not null || text != _current.Name);
        SaveButton.Content = match is not null ? $"Same person as {match.Name}" : _current?.Name is null ? "Name" : "Rename";
        MatchPicture.Visibility = match is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(MatchPicture, match?.Name);
        if (match is null)
        {
            _shownMatch = null;
            return;
        }
        if (_shownMatch == match.Id) return;
        _shownMatch = match.Id;
        MatchImage.Source = null;
        var tile = new PersonTile(match);
        await tile.EnsureCoverAsync();
        if (_shownMatch == match.Id) MatchImage.Source = tile.Cover;
    }

    private void Save()
    {
        if (_current is not { } person || !SaveButton.IsEnabled) return;
        var text = NameBox.Text.Trim();
        if (Match(text) is { } match)
        {
            S.People.Merge(person.Id, match.Id);
            var joined = S.People.Get(match.Id) ?? match;
            _named[_named.IndexOf(match)] = joined;
            Decide(person, $"Joined to {joined.Name}");
        }
        else
        {
            S.People.Rename(person.Id, text);
            // Named now: the next ones can be joined to them.
            _named.RemoveAll(p => p.Id == person.Id);
            if (S.People.Get(person.Id) is { } named) _named.Add(named);
            Decide(person, $"Named {text}");
        }
        _changedAny = true;
        ShowSending();
        Go(1);
    }

    // ---------- Setting aside (on this PC only) ----------

    private void OnNotTagged(object sender, RoutedEventArgs e)
    {
        if (_current is not { } person) return;
        S.People.SetNotTagged(person.Id, true);
        S.People.SetHidden(person.Id, false);
        Decide(person, "Known, not tagged");
        Go(1);
    }

    private void OnStranger(object sender, RoutedEventArgs e)
    {
        if (_current is not { } person) return;
        S.People.SetHidden(person.Id, true);
        S.People.SetNotTagged(person.Id, false);
        Decide(person, "Not someone I know");
        Go(1);
    }

    private void Decide(PersonRow person, string decision)
    {
        _decisions[person.Id] = decision;
        App.MainWindow.ShowStatus(decision);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Go(1);

    private void OnPrevious(object sender, RoutedEventArgs e) => Go(-1);

    /// <summary>Whether names and joins made here have reached OneDrive yet.</summary>
    private void ShowSending()
    {
        var (waiting, refused, _) = S.People.GetChangeStatus();
        SendText.Text = refused > 0 ? $"OneDrive refused {refused:N0} change{(refused == 1 ? "" : "s")}; see Settings › Live Photos and people"
            : waiting > 0 ? S.PeopleChanges.Problem ?? $"Sending {waiting:N0} change{(waiting == 1 ? "" : "s")} to OneDrive…"
            : _changedAny ? "Names and joins are in OneDrive too"
            : "";
    }
}
