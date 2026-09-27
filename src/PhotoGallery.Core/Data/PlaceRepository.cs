using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Data;

/// <summary>
/// Places you named (a centre and a radius) and which photos are at each (MediaPlaces, kept in step when a place
/// changes and when a photo is indexed). Place names are searchable, like tags.
/// </summary>
public sealed class PlaceRepository(GalleryDatabase database)
{
    public List<Place> GetAll()
    {
        using var db = database.Open();
        return db.Query<Place>("SELECT Id, Name, Latitude, Longitude, RadiusMeters FROM Places ORDER BY Name").AsList();
    }

    public Place? Get(long id)
    {
        using var db = database.Open();
        return db.QuerySingleOrDefault<Place>("SELECT Id, Name, Latitude, Longitude, RadiusMeters FROM Places WHERE Id = @id", new { id });
    }

    /// <summary>The smallest of your places that contains the point, if any.</summary>
    public Place? FindFor(double latitude, double longitude) =>
        GetAll().Where(p => p.Contains(latitude, longitude)).MinBy(p => p.RadiusMeters);

    public int CountMedia(long placeId)
    {
        using var db = database.Open();
        return db.ExecuteScalar<int>("SELECT count(*) FROM MediaPlaces WHERE PlaceId = @placeId", new { placeId });
    }

    /// <summary>Adds (Id 0) or updates a place and re-files the photos at it; returns its id.</summary>
    public long Save(Place place)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var id = place.Id;
        var args = new { place.Id, place.Name, place.Latitude, place.Longitude, place.RadiusMeters, LonScale = Math.Cos(place.Latitude * Math.PI / 180) };
        if (id == 0)
            id = db.ExecuteScalar<long>(
                "INSERT INTO Places (Name, Latitude, Longitude, RadiusMeters, LonScale) VALUES (@Name, @Latitude, @Longitude, @RadiusMeters, @LonScale) RETURNING Id",
                args, tx);
        else
            db.Execute("UPDATE Places SET Name = @Name, Latitude = @Latitude, Longitude = @Longitude, RadiusMeters = @RadiusMeters, LonScale = @LonScale WHERE Id = @Id",
                args, tx);

        var before = db.Query<long>("SELECT MediaId FROM MediaPlaces WHERE PlaceId = @id", new { id }, tx).ToHashSet();
        db.Execute("DELETE FROM MediaPlaces WHERE PlaceId = @id", new { id }, tx);
        db.Execute(
            $"""
            INSERT INTO MediaPlaces (MediaId, PlaceId)
            SELECT m.Id, p.Id FROM Media m JOIN Places p ON p.Id = @id
            WHERE {Within}
            """, new { id }, tx);
        var after = db.Query<long>("SELECT MediaId FROM MediaPlaces WHERE PlaceId = @id", new { id }, tx);
        before.UnionWith(after);
        SearchIndex.RefreshTags(db, tx, before);
        tx.Commit();
        return id;
    }

    public void Delete(long id)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var members = db.Query<long>("SELECT MediaId FROM MediaPlaces WHERE PlaceId = @id", new { id }, tx).AsList();
        db.Execute("DELETE FROM Places WHERE Id = @id", new { id }, tx); // MediaPlaces cascade
        SearchIndex.RefreshTags(db, tx, members);
        tx.Commit();
    }

    /// <summary>
    /// SQL condition: photo m is within place p. A bounding box first (uses the location index), then the distance in
    /// metres from the centre, with the longitude scaled by cos(latitude) stored on the place (no trig in SQL).
    /// </summary>
    internal const string Within =
        """
        m.Latitude IS NOT NULL
        AND m.Latitude BETWEEN p.Latitude - p.RadiusMeters / 111320.0 AND p.Latitude + p.RadiusMeters / 111320.0
        AND ((m.Latitude - p.Latitude) * 111320.0) * ((m.Latitude - p.Latitude) * 111320.0)
          + ((m.Longitude - p.Longitude) * 111320.0 * p.LonScale) * ((m.Longitude - p.Longitude) * 111320.0 * p.LonScale)
          <= p.RadiusMeters * p.RadiusMeters
        """;

    /// <summary>Files one photo under the places it's at (when it's indexed or its location changes).</summary>
    internal static void Refile(SqliteConnection db, SqliteTransaction tx, long mediaId)
    {
        db.Execute("DELETE FROM MediaPlaces WHERE MediaId = @mediaId", new { mediaId }, tx);
        db.Execute($"INSERT INTO MediaPlaces (MediaId, PlaceId) SELECT m.Id, p.Id FROM Media m, Places p WHERE m.Id = @mediaId AND {Within}", new { mediaId }, tx);
    }
}
