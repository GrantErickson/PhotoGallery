using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Places;

namespace PhotoGallery.App.Pages;

/// <summary>Opens the map at a point: zoomed in on a photo (selected in the list), or starting a new place there.</summary>
public sealed record MapRequest(double Latitude, double Longitude, int Zoom, long? SelectId = null, bool NewPlace = false, long? PlaceId = null);

/// <summary>Opens the map at a track: the photos taken from one day to another, or these photos (one selected in the list).</summary>
public sealed record TrackRequest(DateTime? FirstDay, DateTime? LastDay, IReadOnlyList<long>? Ids = null, long? SelectId = null);

/// <summary>A place stopped at on a track, as listed beside the map.</summary>
public sealed class StopRow(int index, TrackStop stop, string name, string when, string? town)
{
    public int Index { get; } = index;
    public TrackStop Stop { get; } = stop;
    public string Number { get; } = stop.Number.ToString(CultureInfo.CurrentCulture);
    public string Name { get; } = name;
    /// <summary>When it was there, and the town if the name isn't it.</summary>
    public string Detail { get; } = town is not null && town != name ? $"{when} · {town}" : when;
    public string Count { get; } = stop.Ids.Count.ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>For the map's tooltip.</summary>
    public string Tip { get; } = $"{when} · {(stop.Ids.Count == 1 ? "1 photo" : $"{stop.Ids.Count:N0} photos")}";
}

/// <summary>
/// One of the groups of a track's photos when people were in more than one place at once: around where, how many,
/// and taken with which phones.
/// </summary>
public sealed class GroupRow(string name, int photos, IReadOnlyList<string> cameras)
{
    public string Name { get; } = name;
    public string Detail { get; } = (photos == 1 ? "1 photo" : $"{photos:N0} photos") + (cameras.Count switch
    {
        0 => "",
        1 or 2 => $" · {string.Join(", ", cameras)}",
        _ => $" · {cameras[0]}, {cameras[1]} and {cameras.Count - 2:N0} more",
    });
}

/// <summary>
/// Leaflet map (WebView2) of every located photo, clustered; the gallery lists what's in the visible area. Places you
/// named show as circles, and a track (Track) shows where photos were taken in order. The page, points and thumbnails
/// are served from local folders through WebView2 virtual hosts.
/// </summary>
public sealed partial class MapPage : Page
{
    private bool _initialized;
    /// <summary>The list shows a group (a cluster, a place, a stop on the track) rather than everything in view or on the track.</summary>
    private bool _clusterSelected;
    private bool _ready;
    /// <summary>A MapRequest or TrackRequest that came before the map was ready.</summary>
    private object? _pending;
    /// <summary>The part of the map in view (the last the page sent).</summary>
    private (double South, double West, double North, double East)? _lastBounds;
    /// <summary>The place being made or edited (Id 0 for a new one); null when the panel is closed.</summary>
    private PhotoGallery.Core.Places.Place? _draft;
    private bool _draftHasCentre;
    /// <summary>A photo to select in the list when the next area arrives from the map.</summary>
    private long? _selectAfterMove;

    public MapPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // keep the map position between visits
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MapRequest or TrackRequest)
        {
            if (_ready) Apply(e.Parameter);
            else _pending = e.Parameter;
        }
        if (_initialized) return;
        _initialized = true;
        PlaceRadiusSlider.Value = RadiusToSlider(150);
        MapError.Visibility = Visibility.Collapsed;
        try
        {
            await WritePointsAsync();
            var core = await PhotoGallery.App.Services.WebViewHost.StartAsync(Map);
            var services = App.Services;
            core.SetVirtualHostNameToFolderMapping("gallery.local", services.Paths.Root, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            // Versioned by the file's date, so WebView2 never runs a cached copy of an older page.
            var page = Path.Combine(AppContext.BaseDirectory, "Assets", "map.html");
            core.Navigate($"https://app.local/map.html?v={File.GetLastWriteTimeUtc(page).Ticks}&dark={(App.MainWindow.IsDark ? 1 : 0)}");
            // Follows the app's theme (and Windows', on "Use my Windows setting").
            ActualThemeChanged += (_, _) => _ = core.ExecuteScriptAsync($"window.setTheme && window.setTheme({(ActualTheme == ElementTheme.Dark ? "true" : "false")})");
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error("Map failed to start", ex);
            MapError.Text = $"The map couldn't start: {ex.Message}\nIt needs the Microsoft Edge WebView2 runtime, and an internet connection for map tiles. Open the map again to retry.";
            MapError.Visibility = Visibility.Visible;
            _initialized = false; // try again next time the map is opened
        }
    }

    /// <summary>Writes [[id, lat, lon], ...] for the page to fetch (compact: 5 decimals ≈ 1 m), with a 1 after approximate ones.</summary>
    private static async Task WritePointsAsync()
    {
        var paths = App.Services.Paths;
        var points = await Task.Run(App.Services.Media.GetGeoPoints);
        var json = new StringBuilder(points.Count * 32).Append('[');
        for (var i = 0; i < points.Count; i++)
        {
            var (id, lat, lon, approximate) = points[i];
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture, $"[{id},{lat:F5},{lon:F5}");
            json.Append(approximate ? ",1]" : "]");
        }
        json.Append(']');
        var dir = Path.Combine(paths.Root, "map");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "points.json"), json.ToString());
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var doc = JsonDocument.Parse(args.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "selection":
                var ids = root.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ToList();
                _clusterSelected = true;
                ShowAllButton.Visibility = Visibility.Visible;
                SubtitleText.Text = ids.Count == 1 ? "1 photo in the selected group." : $"{ids.Count:N0} photos in the selected group.";
                Gallery.BaseFilter = new MediaFilter { Ids = ids };
                break;
            case "clear":
                _clusterSelected = false;
                ShowAllButton.Visibility = Visibility.Collapsed;
                if (_track is not null) ListTrack();
                else SubtitleText.Text = ViewHint;
                break;
            case "bounds":
                var bounds = Normalize((root.GetProperty("south").GetDouble(), root.GetProperty("west").GetDouble(),
                                        root.GetProperty("north").GetDouble(), root.GetProperty("east").GetDouble()));
                _lastBounds = bounds;
                if (_clusterSelected || _track is not null) break; // keep showing the group or the track while panning
                if (_ready && _selectAfterMove is { } select)
                {
                    _selectAfterMove = null;
                    Gallery.RestoreOnNextLoad(select, [select], null);
                }
                Gallery.BaseFilter = new MediaFilter { Bounds = bounds };
                break;
            case "stop" when _stops is { } stops && root.GetProperty("index").GetInt32() is var index && index < stops.Count:
                SelectStopRow(stops[index]);
                ListStop(stops[index], reveal: false);
                break;
            case "history":
                if (root.GetProperty("back").GetBoolean()) App.MainWindow.GoBack();
                else App.MainWindow.GoForward();
                break;
            case "open":
                Gallery.OpenItem(root.GetProperty("id").GetInt64());
                break;
            case "ready":
                _ready = true;
                SendPlaces();
                if (_pending is { } pending)
                {
                    _pending = null;
                    Apply(pending);
                }
                break;
            case "place":
                if (App.Services.Places.Get(root.GetProperty("id").GetInt64()) is { } place)
                {
                    ShowPlacePhotos(place);
                    EditPlace(place);
                }
                break;
            case "placeCenter" when _draft is not null:
                _draft = _draft with { Latitude = root.GetProperty("lat").GetDouble(), Longitude = root.GetProperty("lon").GetDouble() };
                _draftHasCentre = true;
                UpdateDraft();
                break;
        }
    }

    // ---------- Requests from elsewhere (the viewer's location links) ----------

    private const string ViewHint = "Pan and zoom to list the photos in view, or click a number to list just that group.";

    private async void Apply(object request)
    {
        if (request is TrackRequest track)
        {
            await StartTrackAsync(track);
            return;
        }
        if (request is not MapRequest point) return;
        if (TrackToggle.IsChecked == true) await EndTrackAsync(); // a photo or place asked for: what's in view again
        Apply(point);
    }

    private async void Apply(MapRequest request)
    {
        if (request.PlaceId is { } placeId && App.Services.Places.Get(placeId) is { } place)
        {
            await Script($"fitCircle({Js(place.Latitude)},{Js(place.Longitude)},{Js(place.RadiusMeters)})");
            ShowPlacePhotos(place);
            return;
        }
        // Selected once the list shows the new area (an earlier area's list may still be loading).
        _selectAfterMove = request.SelectId;
        if (_clusterSelected)
        {
            _clusterSelected = false;
            ShowAllButton.Visibility = Visibility.Collapsed;
            await Script("clearSelection()");
        }
        await Script($"focusOn({Js(request.Latitude)},{Js(request.Longitude)},{request.Zoom})");
        if (request.NewPlace) BeginPlace(request.Latitude, request.Longitude);
    }

    private async Task Script(string code)
    {
        if (Map.CoreWebView2 is { } core) await core.ExecuteScriptAsync(code);
    }

    private static string Js(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // ---------- Finding a place by name ----------

    /// <summary>The list's last row: search OpenStreetMap (which sends the text there) instead of only this PC.</summary>
    private const string OnlineKind = "online";
    private const string NoteKind = "";
    private CancellationTokenSource? _placeSearch;
    private string _placeQuery = "";

    private async void OnPlaceSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _placeSearch?.Cancel();
        var search = _placeSearch = new CancellationTokenSource();
        _placeQuery = sender.Text.Trim();
        if (_placeQuery.Length < 2)
        {
            sender.ItemsSource = null;
            return;
        }
        try
        {
            await Task.Delay(200, search.Token); // until typing pauses
            var hits = await App.Services.PlaceSearch.SearchAsync(_placeQuery, online: false, search.Token);
            if (search.IsCancellationRequested) return;
            hits.Add(new PlaceHit($"Search OpenStreetMap for “{_placeQuery}”", "Sends what you typed to OpenStreetMap", OnlineKind, 0, 0));
            sender.ItemsSource = hits;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnPlaceSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as PlaceHit
                     ?? (sender.ItemsSource as List<PlaceHit>)?.FirstOrDefault(h => h.Kind is not (OnlineKind or NoteKind));
        if (chosen is { Kind: NoteKind }) return;
        if (chosen is not null && chosen.Kind != OnlineKind)
        {
            sender.Text = chosen.Name;
            await ShowFoundAsync(chosen);
            return;
        }
        // OpenStreetMap: asked for (the last row), or nothing here matched.
        var text = _placeQuery.Length >= 2 ? _placeQuery : args.QueryText.Trim();
        if (text.Length < 2) return;
        _placeSearch?.Cancel();
        sender.Text = text;
        sender.ItemsSource = new List<PlaceHit> { new("Searching OpenStreetMap…", "", NoteKind, 0, 0) };
        sender.IsSuggestionListOpen = true;
        try
        {
            var hits = await App.Services.PlaceSearch.SearchAsync(text, online: true);
            sender.ItemsSource = hits.Count > 0 ? hits : new List<PlaceHit> { new($"Nothing found for “{text}”", "", NoteKind, 0, 0) };
        }
        catch (IOException ex)
        {
            sender.ItemsSource = new List<PlaceHit> { new(ex.Message, "", NoteKind, 0, 0) };
        }
        sender.IsSuggestionListOpen = true;
    }

    /// <summary>Moves the map to the place (fitting an area to it) and marks it; the list shows the photos there.</summary>
    private Task ShowFoundAsync(PlaceHit hit)
    {
        var zoom = hit.Kind == "town" ? 12 : 17;
        var label = JsonSerializer.Serialize(hit.Name);
        return Script(hit.HasBounds
            ? $"showFound({Js(hit.Latitude)},{Js(hit.Longitude)},{label},{zoom},{Js(hit.South!.Value)},{Js(hit.West!.Value)},{Js(hit.North!.Value)},{Js(hit.East!.Value)})"
            : $"showFound({Js(hit.Latitude)},{Js(hit.Longitude)},{label},{zoom})");
    }

    // ---------- Places you named ----------

    private async void SendPlaces()
    {
        var places = await Task.Run(App.Services.Places.GetAll);
        var json = JsonSerializer.Serialize(places.Select(p => new { id = p.Id, name = p.Name, lat = p.Latitude, lon = p.Longitude, r = p.RadiusMeters }));
        await Script($"setPlaces({json})");
    }

    private async void OnPlacesMenuOpening(object sender, object e)
    {
        PlacesMenu.Items.Clear();
        var add = new MenuFlyoutItem { Text = "Add a place…", Icon = new SymbolIcon(Symbol.Add) };
        add.Click += (_, _) => BeginPlace(null, null);
        PlacesMenu.Items.Add(add);
        var places = await Task.Run(App.Services.Places.GetAll);
        if (places.Count > 0) PlacesMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var place in places)
        {
            var item = new MenuFlyoutItem { Text = place.Name, Icon = new FontIcon { Glyph = "\uE707" } };
            item.Click += async (_, _) =>
            {
                await Script($"fitCircle({Js(place.Latitude)},{Js(place.Longitude)},{Js(place.RadiusMeters)})");
                ShowPlacePhotos(place);
            };
            PlacesMenu.Items.Add(item);
        }
    }

    /// <summary>Lists the photos at a place (instead of those in view) until "Show all in view".</summary>
    private async void ShowPlacePhotos(PhotoGallery.Core.Places.Place place)
    {
        _clusterSelected = true;
        ShowAllButton.Visibility = Visibility.Visible;
        var count = await Task.Run(() => App.Services.Places.CountMedia(place.Id));
        SubtitleText.Text = count == 1 ? $"1 photo at {place.Name}." : $"{count:N0} photos at {place.Name}.";
        Gallery.BaseFilter = new MediaFilter { PlaceId = place.Id };
    }

    /// <summary>Starts a new place, at a point if given (otherwise the next click on the map sets the centre).</summary>
    private async void BeginPlace(double? latitude, double? longitude)
    {
        _draft = new PhotoGallery.Core.Places.Place(0, "", latitude ?? 0, longitude ?? 0, SliderToRadius(PlaceRadiusSlider.Value));
        _draftHasCentre = latitude is not null;
        PlacePanelTitle.Text = "New place";
        PlaceNameBox.Text = "";
        DeletePlaceButton.Visibility = Visibility.Collapsed;
        PlacePanel.Visibility = Visibility.Visible;
        await Script("clearDraft()");
        if (_draftHasCentre) UpdateDraft();
        else await Script("startPlacing()");
        PlaceNameBox.Focus(FocusState.Programmatic);
    }

    private async void EditPlace(PhotoGallery.Core.Places.Place place)
    {
        _draft = place;
        _draftHasCentre = true;
        PlacePanelTitle.Text = place.Name;
        PlaceNameBox.Text = place.Name;
        PlaceRadiusSlider.Value = RadiusToSlider(place.RadiusMeters);
        DeletePlaceButton.Visibility = Visibility.Visible;
        PlacePanel.Visibility = Visibility.Visible;
        await Script("clearDraft()");
        UpdateDraft();
    }

    private async void UpdateDraft()
    {
        if (_draft is not { } draft) return;
        PlaceRadiusText.Text = $"Radius: {Format.Distance(draft.RadiusMeters)}";
        PlacePanelHint.Text = _draftHasCentre ? "Drag the pin to move the centre." : "Click the map to set the centre, then drag the pin to adjust.";
        SavePlaceButton.IsEnabled = _draftHasCentre && PlaceNameBox.Text.Trim().Length > 0;
        if (_draftHasCentre) await Script($"setDraft({Js(draft.Latitude)},{Js(draft.Longitude)},{Js(draft.RadiusMeters)})");
    }

    private void OnPlaceNameChanged(object sender, TextChangedEventArgs e) =>
        SavePlaceButton.IsEnabled = _draftHasCentre && PlaceNameBox.Text.Trim().Length > 0;

    private void OnPlaceRadiusChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var radius = SliderToRadius(e.NewValue);
        PlaceRadiusText.Text = $"Radius: {Format.Distance(radius)}";
        if (_draft is null) return;
        _draft = _draft with { RadiusMeters = radius };
        UpdateDraft();
    }

    /// <summary>The slider runs 20 m … 5 km on a log scale, so small radii (a house) are as easy to set as large ones.</summary>
    private static double SliderToRadius(double value) => Math.Round(20 * Math.Pow(250, value / 100) / 5) * 5;

    private static double RadiusToSlider(double radius) => Math.Clamp(100 * Math.Log(radius / 20) / Math.Log(250), 0, 100);

    private async void OnSavePlace(object sender, RoutedEventArgs e)
    {
        if (_draft is not { } draft || !_draftHasCentre || PlaceNameBox.Text.Trim() is not { Length: > 0 } name) return;
        SavePlaceButton.IsEnabled = false;
        var saved = draft with { Name = name };
        var id = await Task.Run(() => App.Services.Places.Save(saved));
        await CloseDraftAsync();
        SendPlaces();
        ShowPlacePhotos(saved with { Id = id });
    }

    private async void OnCancelPlace(object sender, RoutedEventArgs e) => await CloseDraftAsync();

    private async void OnDeletePlace(object sender, RoutedEventArgs e)
    {
        if (_draft is not { Id: > 0 } place) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"Delete “{place.Name}”?", "The place is forgotten; its photos stay where they are.", "Delete")) return;
        await Task.Run(() => App.Services.Places.Delete(place.Id));
        await CloseDraftAsync();
        SendPlaces();
        await Script("clearSelection()");
        _clusterSelected = false;
        ShowAllButton.Visibility = Visibility.Collapsed;
        if (_track is not null) ListTrack();
        else SubtitleText.Text = ViewHint;
    }

    private async Task CloseDraftAsync()
    {
        _draft = null;
        _draftHasCentre = false;
        PlacePanel.Visibility = Visibility.Collapsed;
        await Script("clearDraft()");
    }

    private async void OnShowAll(object sender, RoutedEventArgs e)
    {
        await Map.CoreWebView2.ExecuteScriptAsync("clearSelection()");
        if (_clusterSelected)
        {
            // A place was listed (not a cluster or a stop, which clearSelection handles): back to the track or what's in view.
            _clusterSelected = false;
            ShowAllButton.Visibility = Visibility.Collapsed;
            if (_track is not null)
            {
                ListTrack();
                return;
            }
            SubtitleText.Text = ViewHint;
            await Map.CoreWebView2.ExecuteScriptAsync("sendBounds()");
        }
    }

    // ---------- Tracks ----------

    private const string TrackHint = "The track's photos, oldest first. Click a stop to list just its photos.";
    /// <summary>The track shown (null when Track is off), its stops as listed, and all its photos.</summary>
    private Track? _track;
    private List<StopRow>? _stops;
    private List<long> _trackPhotos = [];
    /// <summary>What the track is of: these photos, or the dates picked (and the area, if asked).</summary>
    private IReadOnlyList<long>? _trackIds;
    private (double South, double West, double North, double East)? _trackArea;
    /// <summary>When people were apart, whose track (an index into Track.Groups): the biggest group's until another is picked.</summary>
    private int _trackGroup;
    private int _trackVersion;
    private bool _settingTrack;

    private async void OnTrackToggle(object sender, RoutedEventArgs e)
    {
        if (TrackToggle.IsChecked == true) await StartTrackAsync(null);
        else await EndTrackAsync();
    }

    /// <summary>
    /// Shows a track: of the request's photos or days, or (from the button) of the newest day with photos in view.
    /// </summary>
    private async Task StartTrackAsync(TrackRequest? request)
    {
        TrackToggle.IsChecked = true;
        TrackPanel.Visibility = Visibility.Visible;
        ShowAllButton.Content = "Show the whole track";
        _trackIds = request?.Ids;
        _trackArea = null;
        _trackGroup = 0;
        TrackDates.Visibility = _trackIds is null ? Visibility.Visible : Visibility.Collapsed;
        if (_trackIds is null)
        {
            var (first, last) = (request?.FirstDay, request?.LastDay);
            if (request is null)
            {
                var view = _lastBounds;
                var newest = await Task.Run(() => App.Services.Locations.GetNewest(new MediaFilter { Bounds = view })
                                                  ?? App.Services.Locations.GetNewest(new MediaFilter()));
                first = last = newest?.Date;
            }
            _settingTrack = true;
            TrackFrom.Date = first?.Date;
            TrackTo.Date = last?.Date;
            TrackAreaCheck.IsChecked = false;
            _settingTrack = false;
        }
        await ShowTrackAsync(fit: true, request?.SelectId);
    }

    private async Task EndTrackAsync()
    {
        _trackVersion++;
        _track = null;
        _stops = null;
        _trackIds = null;
        _trackArea = null;
        TrackToggle.IsChecked = false;
        TrackPanel.Visibility = Visibility.Collapsed;
        ShowAllButton.Content = "Show all in view";
        _clusterSelected = false;
        ShowAllButton.Visibility = Visibility.Collapsed;
        SubtitleText.Text = ViewHint;
        await Script("clearTrack()");
        await Script("onlyPhotos(null)");
        await Script("sendBounds()");
    }

    private async void OnTrackDatesChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_settingTrack || TrackToggle.IsChecked != true) return;
        // Picking only one end makes a one-day track; picking a start after the end moves the end.
        _settingTrack = true;
        if (sender == TrackFrom && TrackFrom.Date is { } from && (TrackTo.Date is null || TrackTo.Date < from)) TrackTo.Date = from;
        if (sender == TrackTo && TrackTo.Date is { } to && (TrackFrom.Date is null || TrackFrom.Date > to)) TrackFrom.Date = to;
        _settingTrack = false;
        _trackGroup = 0;
        await ShowTrackAsync(fit: _trackArea is null);
    }

    private async void OnTrackAreaClick(object sender, RoutedEventArgs e)
    {
        _trackArea = TrackAreaCheck.IsChecked == true ? _lastBounds : null;
        _trackGroup = 0;
        await ShowTrackAsync(fit: false);
    }

    private async void OnTrackGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingTrack || TrackGroupBox.SelectedIndex < 0 || TrackGroupBox.SelectedIndex == _trackGroup) return;
        _trackGroup = TrackGroupBox.SelectedIndex;
        await ShowTrackAsync(fit: true);
    }

    private MediaFilter TrackFilter() => _trackIds is { } ids
        ? new MediaFilter { Ids = ids }
        : new MediaFilter { From = TrackFrom.Date?.Date, To = TrackTo.Date?.Date.AddDays(1), Bounds = _trackArea };

    /// <summary>Works out the track and its stops' names, draws it, and lists its photos (selecting one, if asked).</summary>
    private async Task ShowTrackAsync(bool fit, long? select = null)
    {
        var version = ++_trackVersion;
        var filter = TrackFilter();
        var group = _trackGroup;
        TrackSummary.Text = "Working out the track…";
        var (track, stops, groups) = await Task.Run(async () =>
        {
            var built = App.Services.Locations.GetTrack(filter, group);
            // The photo asked about may be another group's.
            if (select is { } id && !built.Stops.Any(s => s.Ids.Contains(id)))
                for (var g = 0; g < built.Groups.Count; g++)
                    if (g != built.Group && App.Services.Locations.GetTrack(filter, g) is var other && other.Stops.Any(s => s.Ids.Contains(id)))
                    {
                        built = other;
                        break;
                    }
            return (built, await DescribeStopsAsync(built), await DescribeGroupsAsync(built));
        });
        if (version != _trackVersion) return; // asked again (or Track turned off) meanwhile

        _track = track;
        _stops = stops;
        _trackGroup = track.Group;
        _trackPhotos = track.Stops.SelectMany(s => s.Ids).ToList();
        _settingTrack = true;
        StopList.ItemsSource = stops;
        TrackGroupBox.ItemsSource = groups;
        TrackGroupBox.SelectedIndex = groups.Count > 0 ? track.Group : -1;
        TrackGroupBox.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _settingTrack = false;
        StopList.Visibility = stops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrackSummary.Text = Summary(track);
        var json = JsonSerializer.Serialize(new
        {
            stops = stops.Select(s => new { lat = s.Stop.Latitude, lon = s.Stop.Longitude, name = s.Name, when = s.Tip }),
            legs = track.Legs.Select(l => new object[] { l.From, l.To, l.Known }),
        });
        await Script($"showTrack({json},{(fit ? "true" : "false")})");
        await Script($"onlyPhotos({JsonSerializer.Serialize(_trackPhotos)})");

        _clusterSelected = false;
        ShowAllButton.Visibility = Visibility.Collapsed;
        ListTrack(select);
        // The photo asked about: its stop marked on the map and in the list, the photo selected among the track's.
        if (select is { } id && stops.FirstOrDefault(s => s.Stop.Ids.Contains(id)) is { } stop)
        {
            SelectStopRow(stop);
            await Script($"selectStop({stop.Index},false)");
        }
    }

    private string Summary(Track track)
    {
        if (track.Stops.Count == 0)
            return _trackIds is not null ? "None of these photos has a location."
                : _trackArea is not null ? "No photos with a location on these dates in this area."
                : "No photos with a location on these dates.";
        var days = track.Days == 1 ? "1 day" : $"{track.Days:N0} days";
        var stops = track.Stops.Count == 1 ? "1 stop" : $"{track.Stops.Count:N0} stops";
        var photos = _trackPhotos.Count == 1 ? "1 photo" : $"{_trackPhotos.Count:N0} photos";
        var way = track.Meters > 0 ? $" · {Format.Distance(track.Meters)} between stops" : "";
        return $"{days} · {stops}{way} · {photos}" +
               (track.Legs.Any(l => !l.Known) ? ". Dashed lines: only the order is known (a long gap, or a flight)." : "") +
               (track.Groups.Count > 1
                   ? $" Photos were taken in {track.Groups.Count} places at once (people apart), so this is the track of one group:"
                   : "");
    }

    /// <summary>When people were apart, each group by the town where most of its own photos were taken.</summary>
    private static async Task<List<GroupRow>> DescribeGroupsAsync(Track track)
    {
        if (track.Groups.Count == 0) return [];
        var cities = await App.Services.PlaceNames.CitiesAsync();
        return track.Groups.Select(g => new GroupRow(
                cities?.Nearest(g.Latitude, g.Longitude)?.DisplayName is { } town ? $"Around {town}"
                    : string.Create(CultureInfo.InvariantCulture, $"Around {g.Latitude:F2}, {g.Longitude:F2}"),
                g.Photos, g.Cameras))
            .ToList();
    }

    /// <summary>
    /// Names each stop: one of your places, else the park or restaurant most of its photos were matched to, else the
    /// nearest town; with when it was there.
    /// </summary>
    private static async Task<List<StopRow>> DescribeStopsAsync(Track track)
    {
        var services = App.Services;
        var places = services.Places.GetAll();
        var cities = await services.PlaceNames.CitiesAsync();
        var spots = services.Locations.GetSpotNames(track.Stops.SelectMany(s => s.Ids).ToList());
        var rows = new List<StopRow>(track.Stops.Count);
        foreach (var stop in track.Stops)
        {
            var place = places.Where(p => p.Contains(stop.Latitude, stop.Longitude)).MinBy(p => p.RadiusMeters);
            var spot = stop.Ids.Select(id => spots.GetValueOrDefault(id)).OfType<string>().GroupBy(n => n).MaxBy(g => g.Count())?.Key;
            var town = cities?.Nearest(stop.Latitude, stop.Longitude)?.DisplayName;
            var name = place?.Name ?? spot ?? town ?? string.Create(CultureInfo.InvariantCulture, $"{stop.Latitude:F4}, {stop.Longitude:F4}");
            rows.Add(new StopRow(rows.Count, stop, name, Span(stop.StartLocal, stop.EndLocal), town));
        }
        return rows;
    }

    /// <summary>"Sat, Jul 12, 2025, 10:02 AM – 11:30 AM".</summary>
    private static string Span(DateTime start, DateTime end)
    {
        var culture = CultureInfo.CurrentCulture;
        var from = $"{start.ToString("ddd, MMM d, yyyy", culture)}, {start.ToString("t", culture)}";
        if (end - start < TimeSpan.FromMinutes(1)) return from;
        return end.Date == start.Date ? $"{from} – {end.ToString("t", culture)}" : $"{from} – {end.ToString("ddd, MMM d, t", culture)}";
    }

    /// <summary>All the track's photos in the list, oldest first.</summary>
    private void ListTrack(long? select = null)
    {
        SubtitleText.Text = TrackHint;
        _settingTrack = true;
        StopList.SelectedItem = null;
        _settingTrack = false;
        if (select is { } id) Gallery.RestoreOnNextLoad(id, [id], null);
        Gallery.BaseFilter = new MediaFilter { Ids = _trackPhotos, Order = MediaOrder.Oldest };
    }

    /// <summary>A stop's photos in the list (from the list or the map), until "Show the whole track".</summary>
    private void ListStop(StopRow stop, bool reveal)
    {
        _clusterSelected = true;
        ShowAllButton.Visibility = Visibility.Visible;
        SubtitleText.Text = $"Stop {stop.Number}, {stop.Name}: " + (stop.Stop.Ids.Count == 1 ? "1 photo." : $"{stop.Stop.Ids.Count:N0} photos.");
        Gallery.BaseFilter = new MediaFilter { Ids = stop.Stop.Ids, Order = MediaOrder.Oldest };
        _ = Script($"selectStop({stop.Index},{(reveal ? "true" : "false")})");
    }

    private void OnStopSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_settingTrack || StopList.SelectedItem is not StopRow stop) return;
        ListStop(stop, reveal: true);
    }

    /// <summary>Marks a stop in the list without listing its photos.</summary>
    private void SelectStopRow(StopRow stop)
    {
        _settingTrack = true;
        StopList.SelectedItem = stop;
        _settingTrack = false;
        StopList.ScrollIntoView(stop);
    }

    /// <summary>Leaflet longitudes run past ±180 when the world wraps; clamp to the valid range.</summary>
    private static (double, double, double, double) Normalize((double South, double West, double North, double East) b) =>
        b.East - b.West >= 360
            ? (b.South, -180, b.North, 180)
            : (b.South, Math.Max(-180, b.West), b.North, Math.Min(180, b.East));
}
