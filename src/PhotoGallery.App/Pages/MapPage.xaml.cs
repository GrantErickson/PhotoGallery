using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>
/// Leaflet map (WebView2) of every located photo, clustered; the gallery lists what's in the visible area.
/// The page, points and thumbnails are served from local folders through WebView2 virtual hosts.
/// </summary>
public sealed partial class MapPage : Page
{
    private bool _initialized;
    private bool _clusterSelected;

    public MapPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // keep the map position between visits
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
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
            core.Navigate("https://app.local/map.html");
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
                Gallery.BaseFilter = new MediaFilter { Bounds = Normalize(bounds) };
                break;
            case "open":
                Gallery.OpenItem(root.GetProperty("id").GetInt64());
                break;
        }
    }

    private async void OnShowAll(object sender, RoutedEventArgs e) =>
        await Map.CoreWebView2.ExecuteScriptAsync("clearSelection()");

    /// <summary>Leaflet longitudes run past ±180 when the world wraps; clamp to the valid range.</summary>
    private static (double, double, double, double) Normalize((double South, double West, double North, double East) b) =>
        b.East - b.West >= 360
            ? (b.South, -180, b.North, 180)
            : (b.South, Math.Max(-180, b.West), b.North, Math.Min(180, b.East));
}
