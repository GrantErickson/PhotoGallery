using System.IO.Compression;
using PhotoGallery.Core;
using PhotoGallery.Core.Places;

namespace PhotoGallery.App.Services;

/// <summary>
/// Names for where a photo was taken: one of your places if it's inside one, the park, school, restaurant… from
/// OpenStreetMap (when that's switched on), and OneDrive's name for the area, else the nearest town from GeoNames' city
/// list (downloaded once, ~10 MB, kept in %LocalAppData%\PhotoGallery\places).
/// </summary>
public sealed class PlaceNameService(AppServices services)
{
    private const string CitiesUrl = "https://download.geonames.org/export/dump/cities1000.zip";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Task<CityIndex?>? _cities;

    private string Folder => Path.Combine(services.Paths.Root, "places");

    /// <summary>(your place, the OpenStreetMap place it was taken at, the town or area); any may be null.</summary>
    public async Task<(Place? Place, Poi? Spot, string? Area)> DescribeAsync(long mediaId, double latitude, double longitude)
    {
        var place = await Task.Run(() => services.Places.FindFor(latitude, longitude));
        var spot = await Task.Run(() => services.Pois.GetFor(mediaId));
        var area = await Task.Run(() => services.Media.GetPlaceTag(mediaId));
        if (area is null && await CitiesAsync() is { } cities) area = cities.Nearest(latitude, longitude)?.DisplayName;
        return (place, spot, area);
    }

    private Task<CityIndex?> CitiesAsync() => _cities ??= Task.Run(LoadCitiesAsync);

    private async Task<CityIndex?> LoadCitiesAsync()
    {
        var file = Path.Combine(Folder, "cities1000.txt");
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Folder);
                var zip = file + ".zip";
                await using (var source = await Http.GetStreamAsync(CitiesUrl))
                await using (var target = File.Create(zip))
                    await source.CopyToAsync(target);
                ZipFile.ExtractToDirectory(zip, Folder, overwriteFiles: true);
                File.Delete(zip);
            }
            var index = CityIndex.Load(File.ReadLines(file));
            Log.Info($"Loaded {index.Count:N0} towns for place names");
            return index;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Log.Error("Loading the town list for place names failed", ex);
            _cities = null; // try again next time
            return null;
        }
    }
}
