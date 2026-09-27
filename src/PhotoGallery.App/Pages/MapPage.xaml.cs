using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>Opens the map at a point: zoomed in on a photo (selected in the list), or starting a new place there.</summary>
public sealed record MapRequest(double Latitude, double Longitude, int Zoom, long? SelectId = null, bool NewPlace = false, long? PlaceId = null);

/// <summary>
/// Leaflet map (WebView2) of every located photo, clustered; the gallery lists what's in the visible area. Places you
/// named show as circles. The page, points and thumbnails are served from local folders through WebView2 virtual hosts.
/// </summary>
public sealed partial class MapPage : Page
{
    private bool _initialized;
    private bool _clusterSelected;
    private bool _ready;
    private MapRequest? _pending;
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
        if (e.Parameter is MapRequest request)
        {
            if (_ready) Apply(request);
            else _pending = request;
        }
        if (_initialized) return;
        _initialized = true;
        PlaceRadiusSlider.Value = RadiusToSlider(150);
        try
        {
            await WritePointsAsync();
            await Map.EnsureCoreWebView2Async();
            var core = Map.CoreWebView2;
            var services = App.Services;
            core.SetVirtualHostNameToFolderMapping("gallery.local", services.Paths.Root, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            // Versioned by the file's date, so WebView2 never runs a cached copy of an older page.
            var page = Path.Combine(AppContext.BaseDirectory, "Assets", "map.html");
            core.Navigate($"https://app.local/map.html?v={File.GetLastWriteTimeUtc(page).Ticks}");
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error("Map failed to start", ex);
            MapError.Text = $"The map needs the Microsoft Edge WebView2 runtime and an internet connection for map tiles.\n{ex.Message}";
            MapError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Writes [[id, lat, lon], ...] for the page to fetch (compact: 5 decimals ≈ 1 m).</summary>
    private static async Task WritePointsAsync()
    {
        var paths = App.Services.Paths;
        var points = await Task.Run(App.Services.Media.GetGeoPoints);
        var json = new StringBuilder(points.Count * 32).Append('[');
        for (var i = 0; i < points.Count; i++)
        {
            var (id, lat, lon) = points[i];
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture, $"[{id},{lat:F5},{lon:F5}]");
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
                SubtitleText.Text = "Pan and zoom to list the photos in view, or click a number to list just that group.";
                break;
            case "bounds" when _clusterSelected:
                break; // keep showing the selected group while panning
            case "bounds":
                var bounds = (root.GetProperty("south").GetDouble(), root.GetProperty("west").GetDouble(),
                              root.GetProperty("north").GetDouble(), root.GetProperty("east").GetDouble());
                if (_ready && _selectAfterMove is { } select)
                {
                    _selectAfterMove = null;
                    Gallery.RestoreOnNextLoad(select, [select], null);
                }
                Gallery.BaseFilter = new MediaFilter { Bounds = Normalize(bounds) };
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
        PlaceRadiusText.Text = $"Radius: {FormatDistance(draft.RadiusMeters)}";
        PlacePanelHint.Text = _draftHasCentre ? "Drag the pin to move the centre." : "Click the map to set the centre, then drag the pin to adjust.";
        SavePlaceButton.IsEnabled = _draftHasCentre && PlaceNameBox.Text.Trim().Length > 0;
        if (_draftHasCentre) await Script($"setDraft({Js(draft.Latitude)},{Js(draft.Longitude)},{Js(draft.RadiusMeters)})");
    }

    private void OnPlaceNameChanged(object sender, TextChangedEventArgs e) =>
        SavePlaceButton.IsEnabled = _draftHasCentre && PlaceNameBox.Text.Trim().Length > 0;

    private void OnPlaceRadiusChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var radius = SliderToRadius(e.NewValue);
        PlaceRadiusText.Text = $"Radius: {FormatDistance(radius)}";
        if (_draft is null) return;
        _draft = _draft with { RadiusMeters = radius };
        UpdateDraft();
    }

    /// <summary>The slider runs 20 m … 5 km on a log scale, so small radii (a house) are as easy to set as large ones.</summary>
    private static double SliderToRadius(double value) => Math.Round(20 * Math.Pow(250, value / 100) / 5) * 5;

    private static double RadiusToSlider(double radius) => Math.Clamp(100 * Math.Log(radius / 20) / Math.Log(250), 0, 100);

    private static string FormatDistance(double meters) =>
        meters >= 1000 ? $"{meters / 1000:0.#} km" : $"{meters:0} m";

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
        SubtitleText.Text = "Pan and zoom to list the photos in view, or click a number to list just that group.";
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
            // A place was listed (not a cluster, which clearSelection handles): back to what's in view.
            _clusterSelected = false;
            ShowAllButton.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Pan and zoom to list the photos in view, or click a number to list just that group.";
            await Map.CoreWebView2.ExecuteScriptAsync("sendBounds()");
        }
    }

    /// <summary>Leaflet longitudes run past ±180 when the world wraps; clamp to the valid range.</summary>
    private static (double, double, double, double) Normalize((double South, double West, double North, double East) b) =>
        b.East - b.West >= 360
            ? (b.South, -180, b.North, 180)
            : (b.South, Math.Max(-180, b.West), b.North, Math.Min(180, b.East));
}
