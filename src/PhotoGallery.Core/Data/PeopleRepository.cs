using Dapper;
using Microsoft.Data.Sqlite;

namespace PhotoGallery.Core.Data;

public sealed class PersonRow
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public long Count { get; init; }
    /// <summary>"Not someone I know" (or hidden in OneDrive): left out of People and of reviewing.</summary>
    public bool Hidden { get; init; }
    /// <summary>"Known, but don't tag": left out of reviewing until named after all. Kept on this PC only.</summary>
    public bool NotTagged { get; init; }
    /// <summary>A photo to represent the person: the one OneDrive uses, else the one with the fewest other people in it.</summary>
    public long? CoverMediaId { get; init; }

    public string DisplayName => Name ?? "Unnamed person";
}

/// <summary>One of a person's faces, to look at when deciding who they are.</summary>
public readonly record struct FaceSample(long MediaId, Cloud.FaceBox Box, long DateTaken);

public static class PeopleChangeKind
{
    public const string Rename = "name";
    public const string Merge = "merge";
}

/// <summary>A name or merge made here, waiting to be sent to OneDrive (or refused by it).</summary>
public sealed class PeopleChange
{
    public long Id { get; init; }
    /// <summary>A <see cref="PeopleChangeKind"/>.</summary>
    public string Kind { get; init; } = "";
    public string OneDrivePersonId { get; init; } = "";
    /// <summary>For a merge: the OneDrive person it's merged into.</summary>
    public string? IntoPersonId { get; init; }
    /// <summary>The name given; for a merge, the name of the person merged into.</summary>
    public string? Name { get; init; }
    public long Attempts { get; init; }
    public bool Failed { get; init; }
    public string? LastError { get; init; }
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

/// <summary>
/// People recognised by OneDrive (grouped by its per-person id) and the names given to them here or there. Names and
/// merges made here are queued for OneDrive too (<see cref="GetChanges"/>), in the same transaction.
/// </summary>
public sealed class PeopleRepository(GalleryDatabase database)
{
    /// <summary>Raised (on the calling thread) after a name or merge was queued for OneDrive.</summary>
    public event Action? ChangesQueued;

    public List<PersonRow> GetPeople(bool includeHidden = false)
    {
        using var db = database.Open();
        return db.Query<PersonRow>(
            """
            WITH counts AS (SELECT PersonId, count(*) AS Count FROM MediaFaces GROUP BY PersonId),
                 crowd AS (SELECT MediaId, count(*) AS Faces FROM MediaFaces GROUP BY MediaId)
            SELECT p.Id, p.Name, c.Count, p.Hidden, p.NotTagged,
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
            "SELECT p.Id, p.Name, (SELECT count(*) FROM MediaFaces WHERE PersonId = p.Id) AS Count, p.Hidden, p.NotTagged FROM People p WHERE p.Id = @personId",
            new { personId });
    }

    /// <summary>Unnamed people still to be looked at (not hidden, not "known, but don't tag"), most photos first.</summary>
    public List<PersonRow> GetToReview()
    {
        using var db = database.Open();
        return db.Query<PersonRow>(
            """
            SELECT p.Id, p.Name, count(*) AS Count, p.Hidden, p.NotTagged
            FROM People p JOIN MediaFaces f ON f.PersonId = p.Id
            WHERE p.Name IS NULL AND p.Hidden = 0 AND p.NotTagged = 0
            GROUP BY p.Id
            ORDER BY Count DESC, p.Id
            """).AsList();
    }

    /// <summary>
    /// Faces to recognise someone by: the larger ones (but not frame-filling), from different photos spread over the
    /// years, oldest first.
    /// </summary>
    public List<FaceSample> GetFaceSamples(long personId, int count)
    {
        using var db = database.Open();
        var faces = db.Query<(long MediaId, double X, double Y, double W, double H, long DateTaken)>(
            """
            SELECT f.MediaId, f.BoxX, f.BoxY, f.BoxW, f.BoxH, m.DateTaken
            FROM MediaFaces f JOIN Media m ON m.Id = f.MediaId
            WHERE f.PersonId = @personId AND f.BoxX IS NOT NULL AND f.BoxY IS NOT NULL AND f.BoxW > 0 AND f.BoxH > 0
              AND m.IsHidden = 0 AND m.Kind IN (1, 3) AND m.IsScreenshot = 0 AND m.OnlineOnly = 0
            ORDER BY (f.BoxW < 0.6 AND f.BoxH < 0.7) DESC, f.BoxW * f.BoxH DESC
            LIMIT @pool
            """, new { personId, pool = count * 4 }).OrderBy(f => f.DateTaken).ToList();
        var picked = faces.Count <= count ? faces : Enumerable.Range(0, count).Select(i => faces[(int)((i + 0.5) * faces.Count / count)]);
        return picked.Select(f => new FaceSample(f.MediaId, new Cloud.FaceBox(f.X, f.Y, f.W, f.H), f.DateTaken)).ToList();
    }

    /// <summary>When a person was first and last photographed (Unix seconds, local time), or null without photos.</summary>
    public (long First, long Last)? GetSpan(long personId)
    {
        using var db = database.Open();
        var (first, last) = db.QuerySingle<(long?, long?)>(
            "SELECT min(m.DateTaken), max(m.DateTaken) FROM MediaFaces f JOIN Media m ON m.Id = f.MediaId WHERE f.PersonId = @personId AND m.IsHidden = 0",
            new { personId });
        return first is { } a && last is { } b ? (a, b) : null;
    }

    /// <summary>
    /// Names the person here (this name wins over the one given in OneDrive) and, for someone OneDrive recognised, in
    /// OneDrive too: queued, and sent in the background. Naming someone also brings them back from hidden or
    /// "known, but don't tag".
    /// </summary>
    public void Rename(long personId, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var (guid, old) = db.QuerySingleOrDefault<(string?, string?)>("SELECT OneDrivePersonId, Name FROM People WHERE Id = @personId", new { personId }, tx);
        db.Execute(
            """
            UPDATE People SET Name = @name, NameFromOneDrive = 0,
                              Hidden = CASE WHEN @name IS NULL THEN Hidden ELSE 0 END,
                              NotTagged = CASE WHEN @name IS NULL THEN NotTagged ELSE 0 END
            WHERE Id = @personId
            """, new { personId, name }, tx);
        var mediaIds = db.Query<long>("SELECT MediaId FROM MediaFaces WHERE PersonId = @personId", new { personId }, tx).AsList();
        SearchIndex.RefreshTags(db, tx, mediaIds);
        // Clearing a name stays here: OneDrive's People page has no way to.
        var queued = guid is not null && name is not null && name != old && Queue(db, tx, PeopleChangeKind.Rename, guid, null, name);
        tx.Commit();
        if (queued) ChangesQueued?.Invoke();
    }

    /// <summary>"Not someone I know" (or back again). Kept on this PC only.</summary>
    public void SetHidden(long personId, bool hidden)
    {
        using var db = database.Open();
        db.Execute("UPDATE People SET Hidden = @hidden WHERE Id = @personId", new { personId, hidden });
    }

    /// <summary>"Known, but don't tag" (or back to be looked at). Kept on this PC only.</summary>
    public void SetNotTagged(long personId, bool notTagged)
    {
        using var db = database.Open();
        db.Execute("UPDATE People SET NotTagged = @notTagged WHERE Id = @personId", new { personId, notTagged });
    }

    /// <summary>
    /// Folds <paramref name="sourceId"/> into <paramref name="targetId"/> (OneDrive sometimes splits one person), here
    /// and, for people OneDrive recognised, in OneDrive too: queued, and sent in the background.
    /// </summary>
    public void Merge(long sourceId, long targetId)
    {
        if (sourceId == targetId) return;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        const string Person = "SELECT OneDrivePersonId, Name FROM People WHERE Id = @id";
        var (sourceGuid, sourceName) = db.QuerySingleOrDefault<(string?, string?)>(Person, new { id = sourceId }, tx);
        var (targetGuid, targetName) = db.QuerySingleOrDefault<(string?, string?)>(Person, new { id = targetId }, tx);
        var mediaIds = Fold(db, tx, sourceId, targetId);
        SearchIndex.RefreshTags(db, tx, mediaIds);
        // OneDrive's People page merges by giving the merged-away person the other's id and name.
        var name = targetName ?? sourceName;
        var queued = sourceGuid is not null && targetGuid is not null && name is not null &&
                     !string.Equals(sourceGuid, targetGuid, StringComparison.OrdinalIgnoreCase) &&
                     Queue(db, tx, PeopleChangeKind.Merge, sourceGuid, targetGuid, name);
        tx.Commit();
        if (queued) ChangesQueued?.Invoke();
    }

    private static bool Queue(SqliteConnection db, SqliteTransaction tx, string kind, string guid, string? into, string name)
    {
        // A newer name replaces one not sent yet.
        if (kind == PeopleChangeKind.Rename)
            db.Execute("DELETE FROM PeopleChanges WHERE Kind = @kind AND OneDrivePersonId = @guid AND Failed = 0", new { kind, guid }, tx);
        db.Execute(
            "INSERT INTO PeopleChanges (Kind, OneDrivePersonId, IntoPersonId, Name, Created) VALUES (@kind, @guid, @into, @name, @created)",
            new { kind, guid, into, name, created = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, tx);
        return true;
    }

    // ---------- Changes for OneDrive ----------

    /// <summary>Changes waiting to be sent to OneDrive, in the order they were made; or those OneDrive refused.</summary>
    public List<PeopleChange> GetChanges(bool refused = false)
    {
        using var db = database.Open();
        return db.Query<PeopleChange>(
            "SELECT Id, Kind, OneDrivePersonId, IntoPersonId, Name, Attempts, Failed, LastError FROM PeopleChanges WHERE Failed = @refused ORDER BY Id",
            new { refused }).AsList();
    }

    /// <summary>Sent: forgets the change. OneDrive now has the name, so later renames there can come back here again.</summary>
    public void CompleteChange(PeopleChange change)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute("DELETE FROM PeopleChanges WHERE Id = @Id", new { change.Id }, tx);
        db.Execute("UPDATE People SET NameFromOneDrive = 1 WHERE OneDrivePersonId = @guid AND Name = @Name",
            new { guid = change.Kind == PeopleChangeKind.Merge ? change.IntoPersonId : change.OneDrivePersonId, change.Name }, tx);
        tx.Commit();
    }

    /// <summary>Couldn't be sent: tried again later, or (<paramref name="refused"/> by OneDrive) set aside for the user.</summary>
    public void FailChange(long id, string error, bool refused)
    {
        using var db = database.Open();
        db.Execute("UPDATE PeopleChanges SET Attempts = Attempts + 1, LastError = @error, Failed = @refused WHERE Id = @id", new { id, error, refused });
    }

    /// <summary>Tries the changes OneDrive refused again.</summary>
    public void RetryRefusedChanges()
    {
        int count;
        using (var db = database.Open())
            count = db.Execute("UPDATE PeopleChanges SET Failed = 0 WHERE Failed = 1");
        if (count > 0) ChangesQueued?.Invoke();
    }

    /// <summary>Gives up on the changes OneDrive refused (they stay made here).</summary>
    public void DiscardRefusedChanges()
    {
        using var db = database.Open();
        db.Execute("DELETE FROM PeopleChanges WHERE Failed = 1");
    }

    /// <summary>How many changes wait to be sent, how many OneDrive refused, and the latest problem with either.</summary>
    public (long Waiting, long Refused, string? LastError) GetChangeStatus()
    {
        using var db = database.Open();
        return db.QuerySingle<(long, long, string?)>(
            """
            SELECT coalesce(sum(Failed = 0), 0), coalesce(sum(Failed = 1), 0),
                   (SELECT LastError FROM PeopleChanges WHERE LastError IS NOT NULL ORDER BY Failed DESC, Id DESC LIMIT 1)
            FROM PeopleChanges
            """);
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
