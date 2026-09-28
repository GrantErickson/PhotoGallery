using System.Globalization;

namespace PhotoGallery.Core.Places;

/// <summary>A town from the GeoNames list.</summary>
public sealed record City(string Name, string CountryCode, string Admin1, double Latitude, double Longitude, long Population = 0)
{
    /// <summary>"Spokane, WA" in the US (like OneDrive's names), "Vancouver, Canada" elsewhere.</summary>
    public string DisplayName
    {
        get
        {
            if (CountryCode == "US" && Admin1.Length == 2) return $"{Name}, {Admin1}";
            try
            {
                return $"{Name}, {new RegionInfo(CountryCode).EnglishName}";
            }
            catch (ArgumentException)
            {
                return Name;
            }
        }
    }
}

/// <summary>
/// The nearest town to a point, offline, from GeoNames' "cities1000" list (every place with 1,000+ people, ~150k
/// rows, CC BY 4.0). Indexed in one-degree cells, so a lookup only looks at the neighbouring cells.
/// </summary>
public sealed class CityIndex
{
    /// <summary>Further than this from any town and there's no name.</summary>
    public const double MaxDistanceMeters = 40_000;

    private readonly Dictionary<(int, int), List<City>> _cells = [];
    /// <summary>Every town with its name folded for searching (and its ASCII spelling, if different), biggest first.</summary>
    private readonly List<(City City, string Name, string? Ascii)> _byName = [];
    private bool _sorted;

    public int Count { get; private set; }

    /// <summary>Reads the tab-separated GeoNames file (name at column 1, ASCII name 2, lat/lon 4/5, country 8, admin1 10, population 14).</summary>
    public static CityIndex Load(IEnumerable<string> lines)
    {
        var index = new CityIndex();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 11 ||
                !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            var population = f.Length > 14 && long.TryParse(f[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            index.Add(new City(f[1], f[8], f[10], lat, lon, population), f[2]);
        }
        return index;
    }

    public void Add(City city, string? asciiName = null)
    {
        var key = ((int)Math.Floor(city.Latitude), (int)Math.Floor(city.Longitude));
        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = [];
        cell.Add(city);
        var name = PlaceSearch.Fold(city.Name);
        var ascii = asciiName is { Length: > 0 } ? PlaceSearch.Fold(asciiName) : null;
        _byName.Add((city, name, ascii == name ? null : ascii));
        _sorted = false;
        Count++;
    }

    /// <summary>
    /// Towns whose names match, best matches first and the biggest first among equals. "Spokane, WA" or
    /// "Paris, France" narrows it to a state (US) or a country (name or code).
    /// </summary>
    public List<City> Search(string query, int max = PlaceSearch.MaxResults)
    {
        var (name, qualifier) = PlaceSearch.Split(query);
        if (name.Length < 2) return [];
        lock (_byName)
        {
            if (!_sorted)
            {
                _byName.Sort((a, b) => b.City.Population.CompareTo(a.City.Population));
                _sorted = true;
            }
        }
        return _byName
            .Select(t => (t.City, Score: Math.Max(PlaceSearch.Score(t.Name, name), t.Ascii is null ? 0 : PlaceSearch.Score(t.Ascii, name))))
            .Where(t => t.Score > 0 && (qualifier is null || InRegion(t.City, qualifier)))
            .OrderByDescending(t => t.Score) // stable: the biggest first within each score
            .Take(max)
            .Select(t => t.City)
            .ToList();
    }

    private static bool InRegion(City city, string qualifier)
    {
        if (PlaceSearch.Fold(city.Admin1).StartsWith(qualifier, StringComparison.Ordinal) ||
            PlaceSearch.Fold(city.CountryCode) == qualifier) return true;
        try
        {
            return PlaceSearch.Fold(new RegionInfo(city.CountryCode).EnglishName).StartsWith(qualifier, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public City? Nearest(double latitude, double longitude)
    {
        int row = (int)Math.Floor(latitude), column = (int)Math.Floor(longitude);
        City? best = null;
        var bestDistance = MaxDistanceMeters;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var c = column + dx;
                if (c < -180) c += 360;
                else if (c >= 180) c -= 360;
                if (!_cells.TryGetValue((row + dy, c), out var cell)) continue;
                foreach (var city in cell)
                {
                    var d = Geo.Haversine(latitude, longitude, city.Latitude, city.Longitude);
                    if (d < bestDistance) (best, bestDistance) = (city, d);
                }
            }
        return best;
    }
}
