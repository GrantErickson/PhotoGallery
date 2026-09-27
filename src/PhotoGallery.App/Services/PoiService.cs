using System.Net;
using PhotoGallery.Core;
using PhotoGallery.Core.Places;

namespace PhotoGallery.App.Services;

/// <summary>
/// Looks up named places (parks, schools, restaurants…) from OpenStreetMap's Overpass API for the tiles where photos
/// were taken, once each, one at a time with a pause between (the service is shared and free), when switched on in
/// Settings. Only the tiles' corners are sent. New photos in tiles already looked up are matched locally. Events are
/// raised on a background thread.
/// </summary>
public sealed class PoiService(AppServices services)
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _wake = new(0);
    private int _started;

    public event Action? StateChanged;

    public bool IsWorking { get; private set; }
    public string? LastError { get; private set; }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        // Overpass asks callers to say who they are; no personal details.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoGallery/1.0 (+https://github.com/GrantErickson/PhotoGallery)");
        return http;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Wakes the background work (switched on, or new photos indexed).</summary>
    public void Nudge()
    {
        if (_started == 1) _wake.Release();
        StateChanged?.Invoke();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            if (!services.Settings.NamePlacesFromOsm)
            {
                IsWorking = false;
                StateChanged?.Invoke();
                await _wake.WaitAsync();
                continue;
            }
            await Task.Run(services.Pois.AssignPending); // new photos in tiles already looked up
            var tiles = await Task.Run(services.Pois.GetPendingTiles);
            if (tiles.Count == 0)
            {
                IsWorking = false;
                StateChanged?.Invoke();
                await _wake.WaitAsync(TimeSpan.FromMinutes(30));
                continue;
            }

            IsWorking = true;
            StateChanged?.Invoke();
            foreach (var tile in tiles)
            {
                if (!services.Settings.NamePlacesFromOsm) break;
                try
                {
                    var pois = Overpass.Parse(await FetchAsync(tile));
                    var named = await Task.Run(() => services.Pois.SaveTile(tile, pois));
                    LastError = null;
                    if (named > 0) Log.Info($"Place names: tile {Overpass.TileKey(tile)} has {pois.Count} places, {named} photos named");
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable)
                {
                    LastError = "OpenStreetMap is busy; trying again shortly";
                    StateChanged?.Invoke();
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    break; // start again from the pending list
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
                {
                    Log.Error($"Looking up places for tile {Overpass.TileKey(tile)} failed", ex);
                    LastError = $"couldn't reach OpenStreetMap ({ex.Message})";
                    StateChanged?.Invoke();
                    await _wake.WaitAsync(TimeSpan.FromMinutes(10));
                    break;
                }
                StateChanged?.Invoke();
                await Task.Delay(Pause);
            }
        }
    }

    private static async Task<string> FetchAsync((int Row, int Column) tile)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Overpass.Query(tile))]);
        using var response = await Http.PostAsync(Overpass.Endpoint, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
