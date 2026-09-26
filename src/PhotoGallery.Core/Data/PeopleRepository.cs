using Dapper;

namespace PhotoGallery.Core.Data;

public sealed class PersonRow
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public long Count { get; init; }
    public bool Hidden { get; init; }
    /// <summary>A photo to represent the person: the one with the fewest other people in it.</summary>
    public long? CoverMediaId { get; init; }

    public string DisplayName => Name ?? "Unnamed person";
}

/// <summary>People recognised by OneDrive (grouped by its per-person id) and the names given to them here.</summary>
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
                     ORDER BY m.IsScreenshot, m.Kind = 2, crowd.Faces, m.Rating DESC, m.DateTaken DESC LIMIT 1) AS CoverMediaId
            FROM People p JOIN counts c ON c.PersonId = p.Id
            WHERE @includeHidden OR p.Hidden = 0
            ORDER BY (p.Name IS NULL), c.Count DESC
            """, new { includeHidden }).AsList();
    }

    /// <summary>Best candidate photos to represent a person: not screenshots, few other people, favourites and recent first.</summary>
    public List<long> GetCoverCandidates(long personId, int count)
    {
        using var db = database.Open();
        return db.Query<long>(
            """
            SELECT f.MediaId FROM MediaFaces f JOIN Media m ON m.Id = f.MediaId
            WHERE f.PersonId = @personId AND m.IsHidden = 0 AND m.Kind IN (1, 3) AND m.IsScreenshot = 0
            ORDER BY (SELECT count(*) FROM MediaFaces c WHERE c.MediaId = f.MediaId), m.Rating DESC, m.DateTaken DESC
            LIMIT @count
            """, new { personId, count }).AsList();
    }

    public List<PersonRow> GetPeopleIn(long mediaId)
    {
        using var db = database.Open();
        return db.Query<PersonRow>(
            "SELECT p.Id, p.Name, 0 AS Count, p.Hidden FROM People p JOIN MediaFaces f ON f.PersonId = p.Id WHERE f.MediaId = @mediaId ORDER BY (p.Name IS NULL), p.Name",
            new { mediaId }).AsList();
    }

    public PersonRow? Get(long personId)
    {
        using var db = database.Open();
        return db.QuerySingleOrDefault<PersonRow>(
            "SELECT p.Id, p.Name, (SELECT count(*) FROM MediaFaces WHERE PersonId = p.Id) AS Count, p.Hidden FROM People p WHERE p.Id = @personId",
            new { personId });
    }

    public void Rename(long personId, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute("UPDATE People SET Name = @name WHERE Id = @personId", new { personId, name }, tx);
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
        db.Execute("INSERT OR IGNORE INTO PersonAliases (OneDrivePersonId, PersonId) SELECT OneDrivePersonId, @targetId FROM People WHERE Id = @sourceId",
            new { sourceId, targetId }, tx);
        db.Execute("UPDATE PersonAliases SET PersonId = @targetId WHERE PersonId = @sourceId", new { sourceId, targetId }, tx);
        db.Execute("INSERT OR IGNORE INTO MediaFaces (MediaId, PersonId) SELECT MediaId, @targetId FROM MediaFaces WHERE PersonId = @sourceId",
            new { sourceId, targetId }, tx);
        var mediaIds = db.Query<long>("SELECT MediaId FROM MediaFaces WHERE PersonId = @targetId", new { targetId }, tx).AsList();
        db.Execute("DELETE FROM People WHERE Id = @sourceId", new { sourceId }, tx);
        SearchIndex.RefreshTags(db, tx, mediaIds);
        tx.Commit();
    }
}
