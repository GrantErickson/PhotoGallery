using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Data;

/// <summary>
/// Approximate locations for photos without one, estimated from located photos taken around the same time
/// (<see cref="LocationEstimator"/>), and tracks of where photos were taken (<see cref="TrackBuilder"/>). An estimate is
/// kept in Media.Latitude/Longitude with LocationSource = Estimated, so it's on the map, filed under your places and
/// named after the park or restaurant like any location; one removed by hand stays removed.
/// </summary>
public sealed class LocationRepository(GalleryDatabase database)
{
    /// <summary>
    /// Times that line up with other photos': the camera's own clock (EXIF), or an iPhone video's (local wall clock).
    /// Dates from file names (often when a picture was saved, not taken) and other videos' (UTC, read in this PC's
    /// zone) can be hours out. The clock must be one that's set for you, too (<see cref="TrustedClocks"/>).
    /// </summary>
    private const string TrustedTime = "(m.DateSource = 3 OR m.DateSource = 2 AND m.CameraMake = 'Apple')";

    /// <summary>
    /// Estimates a location for every photo and video without one of its own (screenshots aside), and drops estimates
    /// that no longer hold (a nearer photo was deleted, a date changed); returns how many changed.
    /// </summary>
    public int EstimateAll()
    {
        using var db = database.Open();
        var trusted = TrustedClocks(db);
        var cameras = new Cameras();
        var estimator = new LocationEstimator(db.Query<Row>(
                $"""
                SELECT m.Id, m.DateTaken, m.UtcOffset, m.Latitude, m.Longitude, m.CameraMake, m.CameraModel FROM Media m
                WHERE m.LocationSource = 0 AND m.Latitude IS NOT NULL AND m.IsHidden = 0 AND {TrustedTime}
                """)
            .Where(f => trusted(f.CameraMake, f.CameraModel))
            .Select(f => f.ToFix(cameras)));
        var targets = db.Query<Row>(
            $"""
            SELECT m.Id, m.DateTaken, m.UtcOffset, m.CameraMake, m.CameraModel FROM Media m
            WHERE (m.LocationSource = 1 OR m.LocationSource = 0 AND m.Latitude IS NULL)
              AND m.IsHidden = 0 AND m.IsScreenshot = 0 AND {TrustedTime}
            """);
        var estimates = new Dictionary<long, LocationEstimate>();
        foreach (var t in targets)
            if (trusted(t.CameraMake, t.CameraModel)
                && estimator.Estimate(t.Id, t.DateTaken, (int?)t.UtcOffset, cameras.Of(t.CameraMake, t.CameraModel)) is { } estimate)
                estimates[t.Id] = estimate;

        var current = db.Query<(long Id, double Latitude, double Longitude, double? Accuracy, long? FromId)>(
            "SELECT Id, Latitude, Longitude, LocationAccuracy, LocationFromId FROM Media WHERE LocationSource = 1").ToDictionary(c => c.Id);
        var moved = new List<long>();
        var changed = 0;
        using var tx = db.BeginTransaction();
        foreach (var estimate in estimates.Values)
        {
            current.Remove(estimate.Id, out var was);
            var samePlace = was.Id != 0 && was.Latitude == estimate.Latitude && was.Longitude == estimate.Longitude;
            if (samePlace && was.Accuracy == estimate.AccuracyMeters && was.FromId == estimate.FromId) continue;
            db.Execute(
                """
                UPDATE Media SET Latitude = @Latitude, Longitude = @Longitude, LocationSource = 1, LocationAccuracy = @AccuracyMeters,
                                 LocationFromId = @FromId, PoiId = CASE WHEN Latitude IS @Latitude AND Longitude IS @Longitude THEN PoiId END
                WHERE Id = @Id
                """, estimate, tx);
            changed++;
            if (!samePlace) moved.Add(estimate.Id);
        }
        // What's left was estimated before but can't be now.
        Clear(db, tx, current.Keys, LocationSource.File);
        moved.AddRange(current.Keys);
        Refile(db, tx, moved);
        tx.Commit();
        return changed + current.Count;
    }

    /// <summary>Takes away estimated locations (not the files' own) and keeps them from being estimated again; returns how many.</summary>
    public int RemoveEstimates(IReadOnlyCollection<long> ids)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var estimated = db.Query<long>("SELECT Id FROM Media WHERE Id IN @ids AND LocationSource = 1", new { ids }, tx).AsList();
        Clear(db, tx, estimated, LocationSource.Removed);
        Refile(db, tx, estimated);
        tx.Commit();
        return estimated.Count;
    }

    /// <summary>Lets removed estimates be made again (by the next <see cref="EstimateAll"/>); returns how many.</summary>
    public int AllowEstimates(IReadOnlyCollection<long> ids)
    {
        using var db = database.Open();
        return db.Execute("UPDATE Media SET LocationSource = 0 WHERE Id IN @ids AND LocationSource = 2", new { ids });
    }

    /// <summary>Forgets every estimated location (estimating was switched off); ones removed by hand stay removed.</summary>
    public int ClearEstimates()
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var estimated = db.Query<long>("SELECT Id FROM Media WHERE LocationSource = 1", transaction: tx).AsList();
        Clear(db, tx, estimated, LocationSource.File);
        Refile(db, tx, estimated);
        tx.Commit();
        return estimated.Count;
    }

    /// <summary>Photos and videos with an estimated location, and those whose estimate was removed by hand.</summary>
    public (long Estimated, long Removed) GetCounts()
    {
        using var db = database.Open();
        return db.QuerySingle<(long, long)>(
            "SELECT coalesce(sum(LocationSource = 1), 0), coalesce(sum(LocationSource = 2), 0) FROM Media WHERE IsHidden = 0");
    }

    /// <summary>
    /// The track of the located photos and videos the filter matches (the map leaves out screenshots and utility shots,
    /// and so does this): their own locations make the line, and estimated ones join the stop they were estimated from.
    /// When some were taken in other places at the same time (people apart), it's one group's (<see cref="Track.Groups"/>).
    /// </summary>
    public Track GetTrack(MediaFilter filter, int group = 0)
    {
        var (sql, parameters) = MediaRepository.BuildQuery(filter with { Located = true, Order = MediaOrder.Oldest },
            "m.Id, m.DateTaken, m.UtcOffset, m.Latitude, m.Longitude, m.CameraMake, m.CameraModel, m.LocationSource, m.LocationFromId");
        using var db = database.Open();
        var cameras = new Cameras();
        var fixes = new List<Fix>();
        var estimated = new List<(long, long, long?)>();
        foreach (var row in db.Query<Row>(sql, parameters))
        {
            var fix = row.ToFix(cameras);
            if (row.LocationSource == (long)LocationSource.Estimated) estimated.Add((fix.Id, fix.Utc, row.LocationFromId));
            else fixes.Add(fix);
        }
        return TrackBuilder.Build(fixes, estimated, group, cameras.Name);
    }

    /// <summary>When the newest located photo the filter matches was taken (local wall clock), if there is one.</summary>
    public DateTime? GetNewest(MediaFilter filter)
    {
        var (sql, parameters) = MediaRepository.BuildQuery(filter with { Located = true }, "max(m.DateTaken)", ordered: false);
        using var db = database.Open();
        return db.ExecuteScalar<long?>(sql, parameters) is { } newest ? TrackBuilder.Local(newest) : null;
    }

    /// <summary>The OpenStreetMap place (park, restaurant…) each of these photos was taken at, for those matched to one.</summary>
    public Dictionary<long, string> GetSpotNames(IReadOnlyCollection<long> ids)
    {
        using var db = database.Open();
        return db.Query<(long Id, string Name)>(
                "SELECT m.Id, p.Name FROM json_each(@ids) j CROSS JOIN Media m ON m.Id = j.value JOIN Pois p ON p.Id = m.PoiId",
                new { ids = System.Text.Json.JsonSerializer.Serialize(ids) })
            .ToDictionary(r => r.Id, r => r.Name);
    }

    /// <summary>
    /// Whether a camera's clock is set for it: a phone's, or one whose photos here carry GPS
    /// (<see cref="LocationEstimator.GpsShare"/>), which a camera keeping its own time doesn't.
    /// </summary>
    private static Func<string?, string?, bool> TrustedClocks(SqliteConnection db)
    {
        var withGps = db.Query<(string Make, string? Model)>(
                """
                SELECT CameraMake, CameraModel FROM Media
                WHERE CameraMake IS NOT NULL AND DateSource = 3 AND IsHidden = 0
                GROUP BY CameraMake, CameraModel
                HAVING sum(LocationSource = 0 AND Latitude IS NOT NULL) >= max(3, count(*) * @share)
                """, new { share = LocationEstimator.GpsShare })
            .Select(c => (c.Make, c.Model ?? "")).ToHashSet();
        return (make, model) => LocationEstimator.IsPhoneMaker(make) || make is not null && withGps.Contains((make, model ?? ""));
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public long DateTaken { get; init; }
        public long? UtcOffset { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public string? CameraMake { get; init; }
        public string? CameraModel { get; init; }
        public long LocationSource { get; init; }
        public long? LocationFromId { get; init; }

        /// <summary>A file that doesn't say its clock's zone was taken on the time where it was.</summary>
        public Fix ToFix(Cameras cameras) => new(Id, DateTaken, Latitude, Longitude,
            UtcOffset is { } offset ? (int)offset : TimeZones.OffsetAtLocal(Latitude, Longitude, DateTaken), cameras.Of(CameraMake, CameraModel));
    }

    /// <summary>A number for each camera (make and model), as in <see cref="Fix.Camera"/>, and its name.</summary>
    private sealed class Cameras
    {
        private readonly Dictionary<(string, string), int> _numbers = [];
        private readonly List<string?> _names = [null];

        public int Of(string? make, string? model)
        {
            if (make is null && model is null) return 0;
            var key = (make ?? "", model ?? "");
            if (!_numbers.TryGetValue(key, out var number))
            {
                _numbers[key] = number = _names.Count;
                _names.Add(model ?? make);
            }
            return number;
        }

        public string? Name(int number) => _names[number];
    }

    private static void Clear(SqliteConnection db, SqliteTransaction tx, IEnumerable<long> ids, LocationSource source)
    {
        foreach (var chunk in ids.Chunk(500))
            db.Execute(
                """
                UPDATE Media SET Latitude = NULL, Longitude = NULL, LocationSource = @source, LocationAccuracy = NULL,
                                 LocationFromId = NULL, PoiId = NULL
                WHERE Id IN @chunk AND LocationSource = 1
                """, new { chunk, source = (int)source }, tx);
    }

    /// <summary>Photos whose location changed: filed again under your places, and their place names searched again.</summary>
    private static void Refile(SqliteConnection db, SqliteTransaction tx, IReadOnlyCollection<long> ids)
    {
        foreach (var id in ids) PlaceRepository.Refile(db, tx, id);
        SearchIndex.RefreshTags(db, tx, ids);
    }
}
