using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoGallery.Core.Places;

/// <summary>A place found by name: where it is, and for an area, how far it reaches (to fit the map to it).</summary>
public sealed record PlaceHit(string Name, string Detail, string Kind, double Latitude, double Longitude,
    double? South = null, double? West = null, double? North = null, double? East = null)
{
    public bool HasBounds => South is not null && West is not null && North is not null && East is not null;

    /// <summary>The line under the name in a list: "Spokane, WA · town", "park · place near your photos".</summary>
    public string Caption => string.Join(" · ", new[] { Detail, Kind }.Where(s => s.Length > 0));
}

/// <summary>
/// Finding a place on the map by name, on this PC: your places, the OpenStreetMap places photos were taken near, and
/// towns from the GeoNames list. Case and accents don't matter; "Spokane, WA" or "Paris, France" narrow a town down.
/// </summary>
public static class PlaceSearch
{
    public const int MaxResults = 10;

    /// <summary>Lower case without accents, for comparing names ("Zürich" and "zurich" match).</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// How well a (folded) name matches a (folded) query: 3 the same, 2 starts with it, 1 a word in it starts with it, 0 not
    /// at all.
    /// </summary>
    public static int Score(string foldedName, string foldedQuery)
    {
        if (foldedQuery.Length == 0) return 0;
        if (foldedName == foldedQuery) return 3;
        if (foldedName.StartsWith(foldedQuery, StringComparison.Ordinal)) return 2;
        var at = foldedName.IndexOf(foldedQuery, StringComparison.Ordinal);
        while (at > 0)
        {
            if (!char.IsLetterOrDigit(foldedName[at - 1])) return 1;
            at = foldedName.IndexOf(foldedQuery, at + 1, StringComparison.Ordinal);
        }
        return 0;
    }

    /// <summary>"Spokane, WA" → ("spokane", "wa"); "Manito Park" → ("manito park", null).</summary>
    public static (string Name, string? Qualifier) Split(string query)
    {
        var comma = query.IndexOf(',');
        return comma < 0 ? (Fold(query), null) : (Fold(query[..comma]), Fold(query[(comma + 1)..]) is { Length: > 0 } q ? q : null);
    }

    /// <summary>
    /// Places near your photos first (your places, OpenStreetMap places photos were taken near, and towns where you took
    /// photos), then how well the name matches, then your places before others, then the biggest towns: "Spokane"
    /// finds the town you took thousands of photos in before its fairgrounds, and those before Spokane, Missouri.
    /// </summary>
    public static List<PlaceHit> Local(string query, IEnumerable<Place> yours, IEnumerable<Poi> spots, CityIndex? cities,
        Func<double, double, bool>? nearPhotos = null, int max = MaxResults)
    {
        var (name, _) = Split(query);
        if (name.Length < 2) return [];
        var candidates = new List<(PlaceHit Hit, bool Near, int Score, int Order, long Population)>();
        candidates.AddRange(yours
            .Select(p => (Place: p, Score: Score(Fold(p.Name), name)))
            .Where(x => x.Score > 0)
            .Select(x => (Around(x.Place), true, x.Score, 0, 0L)));
        candidates.AddRange(spots
            .Select(p => (Spot: p, Score: Score(Fold(p.Name), name)))
            .Where(x => x.Score > 0)
            .DistinctBy(x => (x.Spot.Name, Math.Round(x.Spot.Latitude, 2), Math.Round(x.Spot.Longitude, 2)))
            .Select(x => (new PlaceHit(x.Spot.Name, x.Spot.Kind, "place near your photos",
                x.Spot.Latitude, x.Spot.Longitude, x.Spot.South, x.Spot.West, x.Spot.North, x.Spot.East), true, x.Score, 1, 0L)));
        if (cities is not null)
            candidates.AddRange(cities.Search(query, max * 3).Select(c => (
                new PlaceHit(c.Name, c.DisplayName, "town", c.Latitude, c.Longitude),
                nearPhotos?.Invoke(c.Latitude, c.Longitude) ?? false,
                Score(Fold(c.Name), name),
                2,
                c.Population)));
        return candidates
            .OrderByDescending(c => c.Near)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Order)
            .ThenByDescending(c => c.Population)
            .Take(max)
            .Select(c => c.Hit)
            .ToList();
    }

    /// <summary>A named place of yours, with a box around its circle.</summary>
    private static PlaceHit Around(Place place)
    {
        var dLat = place.RadiusMeters / Geo.MetersPerDegree;
        var dLon = dLat / Math.Max(0.01, Math.Cos(place.Latitude * Math.PI / 180));
        return new PlaceHit(place.Name, "", "your place", place.Latitude, place.Longitude,
            place.Latitude - dLat, place.Longitude - dLon, place.Latitude + dLat, place.Longitude + dLon);
    }
}

/// <summary>
/// OpenStreetMap's Nominatim, for places this PC doesn't know (addresses, landmarks, anywhere): only when asked, as it
/// sends what was typed. Its usage policy allows at most one request a second, from an identified application, and no
/// searching as you type.
/// </summary>
public static class Nominatim
{
    public const string Endpoint = "https://nominatim.openstreetmap.org/search";

    public static string Url(string query, string language) =>
        $"{Endpoint}?format=jsonv2&limit={PlaceSearch.MaxResults}&q={Uri.EscapeDataString(query.Trim())}&accept-language={Uri.EscapeDataString(language)}";

    public static List<PlaceHit> Parse(string json)
    {
        var hits = new List<PlaceHit>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return hits;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!Number(e, "lat", out var lat) || !Number(e, "lon", out var lon)) continue;
            var display = e.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
            var name = e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } given ? given : display.Split(',')[0].Trim();
            var detail = display.StartsWith(name, StringComparison.Ordinal) ? display[name.Length..].TrimStart(',', ' ') : display;
            double? s = null, north = null, w = null, east = null;
            // boundingbox: [south, north, west, east], as strings.
            if (e.TryGetProperty("boundingbox", out var box) && box.ValueKind == JsonValueKind.Array && box.GetArrayLength() == 4 &&
                double.TryParse(box[0].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bs) &&
                double.TryParse(box[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bn) &&
                double.TryParse(box[2].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bw) &&
                double.TryParse(box[3].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var be))
                (s, north, w, east) = (bs, bn, bw, be);
            hits.Add(new PlaceHit(name, detail, "OpenStreetMap", lat, lon, s, w, north, east));
        }
        return hits;
    }

    private static bool Number(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
               double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
