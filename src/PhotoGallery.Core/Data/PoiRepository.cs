using Dapper;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Data;

/// <summary>
/// Named places from OpenStreetMap (parks, schools, restaurants…) for the tiles where photos were taken, and which one
/// each photo was taken at: Media.PoiId is null until looked at, 0 when it's at none. Their names (and kinds) are
/// searchable, like tags.
/// </summary>
public sealed class PoiRepository(GalleryDatabase database)
{
    /// <summary>How far around a tile photos are looked at again when it's fetched (a spot just over the edge may be nearer).</summary>
    private const double Margin = 0.002;

    /// <summary>Tiles with photos that haven't been looked up yet, the ones with the most photos first.</summary>
    public List<(int Row, int Column)> GetPendingTiles()
    {
        using var db = database.Open();
        var fetched = db.Query<string>("SELECT Tile FROM PoiTiles").ToHashSet();
        return Located(db)
            .GroupBy(p => Overpass.TileOf(p.Latitude, p.Longitude))
            .Where(g => !fetched.Contains(Overpass.TileKey(g.Key)))
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key).ToList();
    }

    /// <summary>Tiles looked up, of those with photos; places known; photos named after one.</summary>
    public (int Done, int Total, int Places, int Photos) GetProgress()
    {
        using var db = database.Open();
        var fetched = db.Query<string>("SELECT Tile FROM PoiTiles").ToHashSet();
        var tiles = Located(db).Select(p => Overpass.TileKey(Overpass.TileOf(p.Latitude, p.Longitude))).ToHashSet();
        return (tiles.Count(fetched.Contains), tiles.Count,
            db.ExecuteScalar<int>("SELECT count(*) FROM Pois"),
            db.ExecuteScalar<int>("SELECT count(*) FROM Media WHERE PoiId > 0 AND IsHidden = 0"));
    }

    /// <summary>Names of places that photos were taken at (most photos first), for search suggestions.</summary>
    public List<string> GetNamesInUse()
    {
        using var db = database.Open();
        return db.Query<string>(
            "SELECT p.Name FROM Media m JOIN Pois p ON p.Id = m.PoiId WHERE m.IsHidden = 0 GROUP BY p.Name ORDER BY count(*) DESC").AsList();
    }

    /// <summary>The place a photo was taken at, if it's been matched to one.</summary>
    public Poi? GetFor(long mediaId)
    {
        using var db = database.Open();
        return db.QuerySingleOrDefault<Poi>(
            "SELECT p.Id, p.OsmKey, p.Name, p.Kind, p.Latitude, p.Longitude, p.South, p.West, p.North, p.East FROM Pois p JOIN Media m ON m.PoiId = p.Id WHERE m.Id = @mediaId",
            new { mediaId });
    }

    /// <summary>Stores a tile's places, then matches the photos in and around it; returns how many photos got a place.</summary>
    public int SaveTile((int Row, int Column) tile, IReadOnlyCollection<Poi> pois)
    {
        var (s, w, n, e) = Overpass.Bounds(tile);
        using (var db = database.Open())
        using (var tx = db.BeginTransaction())
        {
            foreach (var poi in pois)
                db.Execute(
                    """
                    INSERT INTO Pois (OsmKey, Name, Kind, Latitude, Longitude, South, West, North, East)
                    VALUES (@OsmKey, @Name, @Kind, @Latitude, @Longitude, @South, @West, @North, @East)
                    ON CONFLICT(OsmKey) DO UPDATE SET Name = excluded.Name, Kind = excluded.Kind, Latitude = excluded.Latitude,
                        Longitude = excluded.Longitude, South = excluded.South, West = excluded.West, North = excluded.North, East = excluded.East
                    """, poi, tx);
            db.Execute("INSERT OR REPLACE INTO PoiTiles (Tile, FetchedUtc, Count) VALUES (@tile, @now, @count)",
                new { tile = Overpass.TileKey(tile), now = DateTime.UtcNow.ToString("O"), count = pois.Count }, tx);
            db.Execute(
                "UPDATE Media SET PoiId = NULL WHERE Latitude BETWEEN @s AND @n AND Longitude BETWEEN @w AND @e",
                new { s = s - Margin, n = n + Margin, w = w - Margin, e = e + Margin }, tx);
            tx.Commit();
        }
        return AssignPending();
    }

    /// <summary>Matches photos not looked at yet (in tiles already fetched) to places; returns how many got one.</summary>
    public int AssignPending()
    {
        using var db = database.Open();
        var fetched = db.Query<string>("SELECT Tile FROM PoiTiles").ToHashSet();
        var pending = db.Query<(long Id, double Latitude, double Longitude)>(
                "SELECT Id, Latitude, Longitude FROM Media WHERE PoiId IS NULL AND Latitude IS NOT NULL")
            .Where(p => fetched.Contains(Overpass.TileKey(Overpass.TileOf(p.Latitude, p.Longitude))))
            .ToList();
        var named = 0;
        using var tx = db.BeginTransaction();
        foreach (var group in pending.GroupBy(p => Overpass.TileOf(p.Latitude, p.Longitude)))
        {
            var (s, w, n, e) = Overpass.Bounds(group.Key);
            const double Around = 0.01; // spots just over the tile's edge
            var candidates = db.Query<Poi>(
                """
                SELECT Id, OsmKey, Name, Kind, Latitude, Longitude, South, West, North, East FROM Pois
                WHERE (South IS NULL AND Latitude BETWEEN @s AND @n AND Longitude BETWEEN @w AND @e)
                   OR (South IS NOT NULL AND South <= @n AND North >= @s AND West <= @e AND East >= @w)
                """, new { s = s - Around, n = n + Around, w = w - Around, e = e + Around }, tx).AsList();
            var changed = new List<long>();
            foreach (var photo in group)
            {
                var best = PoiMatcher.Best(photo.Latitude, photo.Longitude, candidates);
                db.Execute("UPDATE Media SET PoiId = @poi WHERE Id = @Id", new { poi = best?.Id ?? 0, photo.Id }, tx);
                changed.Add(photo.Id);
                if (best is not null) named++;
            }
            SearchIndex.RefreshTags(db, tx, changed);
        }
        tx.Commit();
        return named;
    }

    private static IEnumerable<(double Latitude, double Longitude)> Located(System.Data.IDbConnection db) =>
        db.Query<(double, double)>("SELECT Latitude, Longitude FROM Media WHERE Latitude IS NOT NULL AND IsHidden = 0");
}
