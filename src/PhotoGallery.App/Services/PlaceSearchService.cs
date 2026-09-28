using System.Globalization;
using PhotoGallery.Core;
using PhotoGallery.Core.Places;

namespace PhotoGallery.App.Services;

/// <summary>
/// Finding a place on the map by name. On this PC first (your places, OpenStreetMap places near your photos, GeoNames
/// towns); OpenStreetMap's Nominatim only when asked for, since that sends what was typed to OpenStreetMap.
/// </summary>
public sealed class PlaceSearchService(AppServices services)
{
    private static readonly HttpClient Http = CreateClient();
    private readonly SemaphoreSlim _online = new(1);
    private DateTime _lastOnline;
    private (DateTime At, HashSet<(int, int)> Cells)? _photoCells;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Nominatim's policy: identify the application.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoGallery/1.0 (+https://github.com/GrantErickson/PhotoGallery)");
        return http;
    }

    public async Task<List<PlaceHit>> SearchAsync(string query, bool online, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length < 2 || query.Length > 200) return [];
        if (online) return await OnlineAsync(query, ct);
        var cities = await services.PlaceNames.CitiesAsync();
        return await Task.Run(() =>
        {
            var cells = PhotoCells();
            return PlaceSearch.Local(query, services.Places.GetAll(), services.Pois.Search(PlaceSearch.Split(query).Name), cities,
                (lat, lon) => NearPhotos(cells, lat, lon));
        }, ct);
    }

    /// <summary>Where photos were taken, in cells of a quarter degree (about 25 km), re-read every ten minutes.</summary>
    private HashSet<(int, int)> PhotoCells()
    {
        if (_photoCells is { } known && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(10)) return known.Cells;
        var cells = services.Media.GetGeoPoints().Select(p => Cell(p.Latitude, p.Longitude)).ToHashSet();
        _photoCells = (DateTime.UtcNow, cells);
        return cells;
    }

    private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat * 4), (int)Math.Floor(lon * 4));

    /// <summary>Photos were taken in the town's cell or one next to it.</summary>
    private static bool NearPhotos(HashSet<(int, int)> cells, double lat, double lon)
    {
        var (row, column) = Cell(lat, lon);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (cells.Contains((row + dy, column + dx))) return true;
        return false;
    }

    private async Task<List<PlaceHit>> OnlineAsync(string query, CancellationToken ct)
    {
        await _online.WaitAsync(ct);
        try
        {
            // At most one request a second (Nominatim's usage policy).
            var wait = _lastOnline.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try
            {
                using var response = await Http.GetAsync(Nominatim.Url(query, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName), ct);
                response.EnsureSuccessStatusCode();
                return Nominatim.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            finally
            {
                _lastOnline = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Error("Searching OpenStreetMap for a place failed", ex); // what was typed isn't logged
            throw new IOException("OpenStreetMap's place search didn't answer. Try again in a moment.", ex);
        }
        finally
        {
            _online.Release();
        }
    }
}
