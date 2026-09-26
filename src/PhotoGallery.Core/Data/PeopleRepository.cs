using Dapper;
using Microsoft.Data.Sqlite;

namespace PhotoGallery.Core.Data;

public sealed class PersonRow
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public long Count { get; init; }
    public bool Hidden { get; init; }
    /// <summary>A photo to represent the person: the one OneDrive uses, else the one with the fewest other people in it.</summary>
    public long? CoverMediaId { get; init; }

    public string DisplayName => Name ?? "Unnamed person";
}

/// <summary>A person in one photo, with where their face is (when OneDrive said).</summary>
public sealed class FaceRow
{
    public long MediaId { get; init; }
    public long PersonId { get; init; }
    public string? Name { get; init; }
    public bool Hidden { get; init; }
    public double? BoxX { get; init; }
    public double? BoxY { get; init; }
    public double? BoxW { get; init; }
    public double? BoxH { get; init; }

    public string DisplayName => Name ?? "Unnamed person";
    public Cloud.FaceBox? Box => BoxX is { } x && BoxY is { } y && BoxW is double w and > 0 && BoxH is double h and > 0 ? new Cloud.FaceBox(x, y, w, h) : null;
}

/// <summary>People recognised by OneDrive (grouped by its per-person id) and the names given to them here or there.</summary>
public sealed class PeopleRepository(GalleryDatabase database)
{
    public List<PersonRow> GetPeople(bool includeHidden = false)
    {
        using var db = database.Open();
        return db.Query<PersonRow>(
            """
            WITH counts AS (SELECT PersonId, count(*) AS Count FROM MediaFaces GROUP BY PersonId),
                 crowd AS (SELECT MediaId, count(*) AS Faces FROM MediaFaces GROUP BY MediaId)
            SELECT p.Id, p.Name, c.Count, p.Hidden,
                   (SELECT f.MediaId FROM MediaFaces f JOIN crowd ON crowd.MediaId = f.MediaId JOIN Media m ON m.Id = f.MediaId
                     WHERE f.PersonId = p.Id AND m.IsHidden = 0
                     ORDER BY coalesce(m.OneDriveItemId = p.OneDriveCoverItemId, 0) DESC,
                              m.IsScreenshot, m.Kind = 2, crowd.Faces, m.Rating DESC, m.DateTaken DESC LIMIT 1) AS CoverMediaId
            FROM People p JOIN counts c ON c.PersonId = p.Id
            WHERE @includeHidden OR p.Hidden = 0
            ORDER BY (p.Name IS NULL), c.Count DESC
            """, new { includeHidden }).AsList();
    }

    /// <summary>
    /// Best candidate photos to represent a person, with their face box when known: the photo OneDrive shows for
    /// them, then the largest (but not frame-filling) faces; without boxes, photos with few other people,
    /// favourites and recent first.
    /// </summary>
    public List<(long MediaId, Cloud.FaceBox? Box)> GetCoverCandidates(long personId, int count)
    {
        using var db = database.Open();
        return db.Query<FaceRow>(
            """
            SELECT f.MediaId, f.PersonId, f.BoxX, f.BoxY, f.BoxW, f.BoxH
            FROM MediaFaces f JOIN Media m ON m.Id = f.MediaId JOIN People p ON p.Id = f.PersonId
            WHERE f.PersonId = @personId AND m.IsHidden = 0 AND m.Kind IN (1, 3) AND m.IsScreenshot = 0 AND m.OnlineOnly = 0
            ORDER BY m.OneDriveItemId IS NOT NULL AND m.OneDriveItemId = p.OneDriveCoverItemId DESC,
                     coalesce(f.BoxW < 0.6 AND f.BoxH < 0.7, 0) DESC, -- a box filling the frame is rarely a good face
                     coalesce(f.BoxW * f.BoxH, 0) DESC,
                     (SELECT count(*) FROM MediaFaces c WHERE c.MediaId = f.MediaId), m.Rating DESC, m.DateTaken DESC
            LIMIT @count
            """, new { personId, count }).Select(f => (f.MediaId, f.Box)).ToList();
    }

    /// <summary>The people in a photo with their face boxes, named people first, left to right.</summary>
    public List<FaceRow> GetFacesIn(long mediaId)
    {
        using var db = database.Open();
        return db.Query<FaceRow>(
            """
            SELECT f.MediaId, p.Id AS PersonId, p.Name, p.Hidden, f.BoxX, f.BoxY, f.BoxW, f.BoxH
            FROM MediaFaces f JOIN People p ON p.Id = f.PersonId
            WHERE f.MediaId = @mediaId
            ORDER BY (p.Name IS NULL), f.BoxX, p.Name
            """, new { mediaId }).AsList();
    }

    public PersonRow? Get(long personId)
    {
        using var db = database.Open();
        return db.QuerySingleOrDefault<PersonRow>(
            "SELECT p.Id, p.Name, (SELECT count(*) FROM MediaFaces WHERE PersonId = p.Id) AS Count, p.Hidden FROM People p WHERE p.Id = @personId",
            new { personId });
    }

    /// <summary>Names the person here; this name wins over the one given in OneDrive.</summary>
    public void Rename(long personId, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute("UPDATE People SET Name = @name, NameFromOneDrive = 0 WHERE Id = @personId", new { personId, name }, tx);
        var mediaIds = db.Query<long>("SELECT MediaId FROM MediaFaces WHERE PersonId = @personId", new { personId }, tx).AsList();
        SearchIndex.RefreshTags(db, tx, mediaIds);
        tx.Commit();
    }

    public void SetHidden(long personId, bool hidden)
    {
        using var db = database.Open();
        db.Execute("UPDATE People SET Hidden = @hidden WHERE Id = @personId", new { personId, hidden });
    }

    /// <summary>Folds <paramref name="sourceId"/> into <paramref name="targetId"/> (OneDrive sometimes splits one person).</summary>
    public void Merge(long sourceId, long targetId)
    {
        if (sourceId == targetId) return;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var mediaIds = Fold(db, tx, sourceId, targetId);
        SearchIndex.RefreshTags(db, tx, mediaIds);
        tx.Commit();
    }

    /// <summary>
    /// Moves the source person's faces, aliases and (if the target has none) name onto the target, remembers the
    /// source's OneDrive id as an alias of the target, and deletes the source. Returns the target's photos.
    /// </summary>
    internal static List<long> Fold(SqliteConnection db, SqliteTransaction tx, long sourceId, long targetId)
    {
        var ids = new { sourceId, targetId };
        db.Execute("INSERT OR IGNORE INTO PersonAliases (OneDrivePersonId, PersonId) SELECT OneDrivePersonId, @targetId FROM People WHERE Id = @sourceId AND OneDrivePersonId IS NOT NULL",
            ids, tx);
        db.Execute("UPDATE PersonAliases SET PersonId = @targetId WHERE PersonId = @sourceId", ids, tx);
        db.Execute(
            """
            INSERT OR IGNORE INTO MediaFaces (MediaId, PersonId, BoxX, BoxY, BoxW, BoxH, OneDriveFaceId)
            SELECT MediaId, @targetId, BoxX, BoxY, BoxW, BoxH, OneDriveFaceId FROM MediaFaces WHERE PersonId = @sourceId
            """, ids, tx);
        db.Execute(
            """
            UPDATE People SET (Name, NameFromOneDrive) = (SELECT Name, NameFromOneDrive FROM People WHERE Id = @sourceId)
            WHERE Id = @targetId AND Name IS NULL
            """, ids, tx);
        db.Execute("DELETE FROM People WHERE Id = @sourceId", ids, tx);
        return db.Query<long>("SELECT MediaId FROM MediaFaces WHERE PersonId = @targetId", ids, tx).AsList();
    }
}
