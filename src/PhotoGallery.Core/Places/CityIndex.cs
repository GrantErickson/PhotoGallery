using System.Globalization;

namespace PhotoGallery.Core.Places;

/// <summary>A town from the GeoNames list.</summary>
public sealed record City(string Name, string CountryCode, string Admin1, double Latitude, double Longitude)
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

    public int Count { get; private set; }

    /// <summary>Reads the tab-separated GeoNames file (name at column 1, lat/lon at 4/5, country 8, admin1 10).</summary>
    public static CityIndex Load(IEnumerable<string> lines)
    {
        var index = new CityIndex();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 11 ||
                !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            index.Add(new City(f[1], f[8], f[10], lat, lon));
        }
        return index;
    }

    public void Add(City city)
    {
        var key = ((int)Math.Floor(city.Latitude), (int)Math.Floor(city.Longitude));
        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = [];
        cell.Add(city);
        Count++;
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
