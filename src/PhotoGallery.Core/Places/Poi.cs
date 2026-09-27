using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoGallery.Core.Places;

/// <summary>
/// A named place from OpenStreetMap (a park, school, restaurant, museum…): a point, or an area with its bounding box
/// and outline (<see cref="PoiShape"/>). <see cref="Kind"/> is a plain word ("park", "restaurant") that is searchable.
/// </summary>
public sealed record Poi(long Id, string OsmKey, string Name, string Kind, double Latitude, double Longitude,
    double? South = null, double? West = null, double? North = null, double? East = null, byte[]? Shape = null)
{
    public bool IsArea => South is not null && West is not null && North is not null && East is not null;

    /// <summary>Square metres of the bounding box (0 for a point).</summary>
    public double AreaM2 => IsArea
        ? Geo.Meters(South!.Value, West!.Value, North!.Value, West.Value) * Geo.Meters(South.Value, West.Value, South.Value, East!.Value)
        : 0;

    /// <summary>
    /// Inside the outline (or the box, for an area without one), give or take <paramref name="margin"/> metres: GPS is
    /// rarely better than ~10 m.
    /// </summary>
    public bool Contains(double latitude, double longitude, double margin = 15)
    {
        if (!IsArea) return false;
        var dLat = margin / 111_320.0;
        var dLon = margin / (111_320.0 * Math.Max(0.1, Math.Cos(latitude * Math.PI / 180)));
        if (latitude < South - dLat || latitude > North + dLat || longitude < West - dLon || longitude > East + dLon) return false;
        return Shape is null || PoiShape.Contains(Shape, latitude, longitude, margin);
    }
}

/// <summary>
/// An area's outline: its rings (a closed way) or member ways (a multipolygon's outer and inner parts), stored as
/// int32 counts and coordinates × 10⁷. A point is inside when a ray from it crosses the boundary an odd number of
/// times, which holds for multipolygons split over many ways too.
/// </summary>
public static class PoiShape
{
    public static byte[] Encode(IReadOnlyList<IReadOnlyList<(double Latitude, double Longitude)>> lines)
    {
        var values = new List<int> { lines.Count };
        foreach (var line in lines)
        {
            values.Add(line.Count);
            foreach (var (lat, lon) in line)
            {
                values.Add((int)Math.Round(lat * 1e7));
                values.Add((int)Math.Round(lon * 1e7));
            }
        }
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    public static bool Contains(byte[] shape, double latitude, double longitude, double margin)
    {
        ReadOnlySpan<int> v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(shape);
        if (v.Length == 0) return false;
        var inside = false;
        var nearest = double.MaxValue;
        var metersPerLon = 111_320.0 * Math.Cos(latitude * Math.PI / 180);
        int p = 1, lines = v[0];
        for (var l = 0; l < lines && p < v.Length; l++)
        {
            var count = v[p++];
            for (var i = 0; i + 1 < count && p + 2 * i + 3 < v.Length; i++)
            {
                double y1 = v[p + 2 * i] / 1e7, x1 = v[p + 2 * i + 1] / 1e7, y2 = v[p + 2 * i + 2] / 1e7, x2 = v[p + 2 * i + 3] / 1e7;
                if ((y1 > latitude) != (y2 > latitude) && longitude < (x2 - x1) * (latitude - y1) / (y2 - y1) + x1) inside = !inside;
                nearest = Math.Min(nearest, SegmentMeters(latitude, longitude, y1, x1, y2, x2, metersPerLon));
            }
            p += 2 * count;
        }
        return inside || nearest <= margin;
    }

    /// <summary>Metres from a point to a segment, on a local flat projection (fine at these distances).</summary>
    private static double SegmentMeters(double lat, double lon, double y1, double x1, double y2, double x2, double metersPerLon)
    {
        double ax = (x1 - lon) * metersPerLon, ay = (y1 - lat) * 111_320.0, bx = (x2 - lon) * metersPerLon, by = (y2 - lat) * 111_320.0;
        double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / length, 0, 1);
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt(cx * cx + cy * cy);
    }
}

/// <summary>Which named place a photo was taken at, if any.</summary>
public static class PoiMatcher
{
    /// <summary>A spot this close (a café, a shop) beats a big area around it (the park or campus it's in).</summary>
    public const double NearSpot = 25;
    /// <summary>Otherwise a spot this close still counts, when no area contains the photo.</summary>
    public const double NearAny = 40;
    /// <summary>Areas bigger than this (2 ha) lose to a spot right next to the photo.</summary>
    private const double BigArea = 20_000;

    public static Poi? Best(double latitude, double longitude, IEnumerable<Poi> candidates)
    {
        Poi? spot = null, area = null;
        double spotDistance = double.MaxValue, areaSize = double.MaxValue;
        foreach (var poi in candidates)
        {
            if (poi.IsArea)
            {
                if (poi.Contains(latitude, longitude) && poi.AreaM2 < areaSize) (area, areaSize) = (poi, poi.AreaM2);
            }
            else
            {
                var d = Geo.Meters(latitude, longitude, poi.Latitude, poi.Longitude);
                if (d < spotDistance) (spot, spotDistance) = (poi, d);
            }
        }
        if (spot is not null && spotDistance <= NearSpot && (area is null || areaSize > BigArea)) return spot;
        if (area is not null) return area;
        return spot is not null && spotDistance <= NearAny ? spot : null;
    }
}

/// <summary>
/// OpenStreetMap's named places through the Overpass API: what to ask for, and reading the answer. One query per
/// tile of <see cref="TileSize"/> degrees (about 5 × 4 km here), asked once.
/// </summary>
public static class Overpass
{
    public const double TileSize = 0.05;
    public const string Endpoint = "https://overpass-api.de/api/interpreter";

    /// <summary>OSM tag values worth naming a photo after, and the word shown (and searchable) for each.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Kinds = new()
    {
        ["leisure"] = Words("park", "playground", "stadium", "sports_centre:sports centre", "golf_course:golf course", "nature_reserve:nature reserve",
            "garden", "water_park:water park", "marina", "swimming_pool:pool", "ice_rink:ice rink", "dog_park:dog park", "beach_resort:beach",
            "pitch:field", "track", "fitness_centre:gym", "bowling_alley:bowling"),
        ["amenity"] = Words("school", "college", "university", "kindergarten:preschool", "restaurant", "cafe:café", "fast_food:restaurant", "pub",
            "bar", "ice_cream:ice cream", "place_of_worship:church", "library", "hospital", "theatre:theater", "cinema:movie theater",
            "arts_centre:arts center", "community_centre:community center", "events_venue:venue", "townhall:city hall", "marketplace:market"),
        ["tourism"] = Words("attraction", "museum", "zoo", "theme_park:theme park", "viewpoint", "camp_site:campground", "hotel", "motel",
            "resort", "aquarium", "gallery", "picnic_site:picnic area"),
        ["natural"] = Words("beach", "peak", "waterfall", "bay", "cave_entrance:cave"),
        ["shop"] = Words("mall"),
    };

    private static Dictionary<string, string> Words(params string[] items) =>
        items.Select(i => i.Split(':')).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : p[0].Replace('_', ' '));

    public static (int Row, int Column) TileOf(double latitude, double longitude) =>
        ((int)Math.Floor(latitude / TileSize), (int)Math.Floor(longitude / TileSize));

    public static string TileKey((int Row, int Column) tile) => $"{tile.Row}_{tile.Column}";

    public static (double South, double West, double North, double East) Bounds((int Row, int Column) tile) =>
        (tile.Row * TileSize, tile.Column * TileSize, (tile.Row + 1) * TileSize, (tile.Column + 1) * TileSize);

    /// <summary>The Overpass QL for one tile: named places of the kinds above, with areas' outlines and bounding boxes.</summary>
    public static string Query((int Row, int Column) tile)
    {
        var (s, w, n, e) = Bounds(tile);
        var box = string.Create(CultureInfo.InvariantCulture, $"({s:0.#####},{w:0.#####},{n:0.#####},{e:0.#####})");
        var q = new StringBuilder("[out:json][timeout:90];(");
        foreach (var (key, values) in Kinds)
            q.Append(CultureInfo.InvariantCulture, $"nwr[\"name\"][\"{key}\"~\"^({string.Join('|', values.Keys)})$\"]{box};");
        q.Append(");out tags geom;");
        return q.ToString();
    }

    /// <summary>Reads an Overpass JSON answer (Id 0: not stored yet).</summary>
    public static List<Poi> Parse(string json)
    {
        var result = new List<Poi>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return result;
        foreach (var e in elements.EnumerateArray())
        {
            if (!e.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("name", out var nameValue) || nameValue.GetString() is not { Length: > 0 } name)
                continue;
            var kind = KindOf(tags);
            if (kind is null) continue;
            var type = e.GetProperty("type").GetString() ?? "node";
            var key = $"{type[0]}{e.GetProperty("id").GetInt64()}";
            if (e.TryGetProperty("bounds", out var b))
            {
                double s = b.GetProperty("minlat").GetDouble(), w = b.GetProperty("minlon").GetDouble(),
                    n = b.GetProperty("maxlat").GetDouble(), ea = b.GetProperty("maxlon").GetDouble();
                var outline = Outline(e);
                var hasGeometry = e.TryGetProperty("geometry", out _) || e.TryGetProperty("members", out _);
                if (hasGeometry && outline.Count == 0) // an open line (a track, a stretch of beach): where its middle is
                    result.Add(new Poi(0, key, name.Trim(), kind, (s + n) / 2, (w + ea) / 2));
                else
                    result.Add(new Poi(0, key, name.Trim(), kind, (s + n) / 2, (w + ea) / 2, s, w, n, ea,
                        outline.Count == 0 ? null : PoiShape.Encode(outline))); // no geometry sent: just the box
            }
            else if (e.TryGetProperty("lat", out var lat) && e.TryGetProperty("lon", out var lon))
                result.Add(new Poi(0, key, name.Trim(), kind, lat.GetDouble(), lon.GetDouble()));
        }
        return result;
    }

    /// <summary>A closed way's ring, or a relation's outer and inner member ways; empty for an open way.</summary>
    private static List<IReadOnlyList<(double, double)>> Outline(JsonElement e)
    {
        var lines = new List<IReadOnlyList<(double, double)>>();
        if (e.TryGetProperty("geometry", out var geometry))
        {
            var ring = Points(geometry);
            if (ring.Count >= 4 && ring[0] == ring[^1]) lines.Add(ring);
        }
        else if (e.TryGetProperty("members", out var members))
        {
            foreach (var member in members.EnumerateArray())
                if (member.TryGetProperty("geometry", out var g) &&
                    member.TryGetProperty("role", out var role) && role.GetString() is "outer" or "inner" or "")
                {
                    var line = Points(g);
                    if (line.Count >= 2) lines.Add(line);
                }
        }
        return lines;
    }

    private static List<(double, double)> Points(JsonElement geometry) =>
        geometry.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("lat", out _))
            .Select(p => (p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble())).ToList();

    private static string? KindOf(JsonElement tags)
    {
        foreach (var (key, values) in Kinds)
            if (tags.TryGetProperty(key, out var v) && v.GetString() is { } value && values.TryGetValue(value, out var word))
                return word;
        return null;
    }
}
