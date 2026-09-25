using Dapper;

namespace PhotoGallery.Core.Data;

public sealed class AlbumRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long Created { get; init; }
    public long? CoverMediaId { get; init; }
    public long Count { get; init; }
}

public sealed class TagRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long Count { get; init; }
}

/// <summary>User-curated collections: albums (ordered) and tags.</summary>
public sealed class CollectionRepository(GalleryDatabase database)
{
    // ---------- Albums ----------

    public List<AlbumRow> GetAlbums()
    {
        using var db = database.Open();
        return db.Query<AlbumRow>(
            """
            SELECT a.Id, a.Name, a.Created,
                   coalesce(a.CoverMediaId, (SELECT MediaId FROM AlbumMedia WHERE AlbumId = a.Id ORDER BY SortOrder LIMIT 1)) AS CoverMediaId,
                   (SELECT count(*) FROM AlbumMedia WHERE AlbumId = a.Id) AS Count
            FROM Albums a ORDER BY a.Name COLLATE NOCASE
            """).AsList();
    }

    public long CreateAlbum(string name)
    {
        using var db = database.Open();
        return db.ExecuteScalar<long>("INSERT INTO Albums (Name, Created) VALUES (@name, unixepoch()) RETURNING Id", new { name = name.Trim() });
    }

    public void RenameAlbum(long albumId, string name)
    {
        using var db = database.Open();
        db.Execute("UPDATE Albums SET Name = @name WHERE Id = @albumId", new { albumId, name = name.Trim() });
    }

    public void DeleteAlbum(long albumId)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM Albums WHERE Id = @albumId", new { albumId });
    }

    /// <summary>Appends items to the end of the album, skipping ones already in it.</summary>
    public void AddToAlbum(long albumId, IEnumerable<long> mediaIds)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var next = db.ExecuteScalar<long>("SELECT coalesce(max(SortOrder), 0) FROM AlbumMedia WHERE AlbumId = @albumId", new { albumId }, tx);
        foreach (var mediaId in mediaIds)
            next += db.Execute("INSERT OR IGNORE INTO AlbumMedia (AlbumId, MediaId, SortOrder) VALUES (@albumId, @mediaId, @order)",
                new { albumId, mediaId, order = next + 1 }, tx);
        tx.Commit();
    }

    public void RemoveFromAlbum(long albumId, IEnumerable<long> mediaIds)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM AlbumMedia WHERE AlbumId = @albumId AND MediaId IN @mediaIds", new { albumId, mediaIds });
    }

    /// <summary>Moves one item to a new position (0-based) and renumbers the album.</summary>
    public void MoveInAlbum(long albumId, long mediaId, int newIndex)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var order = db.Query<long>("SELECT MediaId FROM AlbumMedia WHERE AlbumId = @albumId ORDER BY SortOrder", new { albumId }, tx).AsList();
        if (order.Remove(mediaId))
        {
            order.Insert(Math.Clamp(newIndex, 0, order.Count), mediaId);
            for (var i = 0; i < order.Count; i++)
                db.Execute("UPDATE AlbumMedia SET SortOrder = @i WHERE AlbumId = @albumId AND MediaId = @m", new { i = i + 1, albumId, m = order[i] }, tx);
        }
        tx.Commit();
    }

    public List<long> GetAlbumsContaining(long mediaId)
    {
        using var db = database.Open();
        return db.Query<long>("SELECT AlbumId FROM AlbumMedia WHERE MediaId = @mediaId", new { mediaId }).AsList();
    }

    // ---------- Tags (user tags; Source = 0) ----------

    public List<TagRow> GetTags()
    {
        using var db = database.Open();
        return db.Query<TagRow>(
            "SELECT t.Id, t.Name, count(mt.MediaId) AS Count FROM Tags t LEFT JOIN MediaTags mt ON mt.TagId = t.Id GROUP BY t.Id ORDER BY t.Name COLLATE NOCASE").AsList();
    }

    public List<TagRow> GetTagsFor(long mediaId)
    {
        using var db = database.Open();
        return db.Query<TagRow>(
            "SELECT t.Id, t.Name, 0 AS Count FROM Tags t JOIN MediaTags mt ON mt.TagId = t.Id WHERE mt.MediaId = @mediaId ORDER BY t.Name COLLATE NOCASE",
            new { mediaId }).AsList();
    }

    public void AddTag(IEnumerable<long> mediaIds, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var tagId = db.ExecuteScalar<long>(
            "INSERT INTO Tags (Name, Source, TagType) VALUES (@name, 0, 0) ON CONFLICT(Name, Source) DO UPDATE SET Name = Name RETURNING Id",
            new { name }, tx);
        var ids = mediaIds.ToList();
        foreach (var mediaId in ids)
            db.Execute("INSERT OR IGNORE INTO MediaTags (MediaId, TagId) VALUES (@mediaId, @tagId)", new { mediaId, tagId }, tx);
        RefreshSearchTags(db, tx, ids);
        tx.Commit();
    }

    public void RemoveTag(IEnumerable<long> mediaIds, long tagId)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var ids = mediaIds.ToList();
        db.Execute("DELETE FROM MediaTags WHERE TagId = @tagId AND MediaId IN @ids", new { tagId, ids }, tx);
        db.Execute("DELETE FROM Tags WHERE Id = @tagId AND NOT EXISTS (SELECT 1 FROM MediaTags WHERE TagId = @tagId)", new { tagId }, tx);
        RefreshSearchTags(db, tx, ids);
        tx.Commit();
    }

    private static void RefreshSearchTags(System.Data.IDbConnection db, System.Data.IDbTransaction tx, List<long> ids)
    {
        foreach (var chunk in ids.Chunk(500))
            db.Execute(
                """
                UPDATE MediaFts SET Tags = (SELECT group_concat(t.Name, ' ') FROM MediaTags mt JOIN Tags t ON t.Id = mt.TagId WHERE mt.MediaId = MediaFts.rowid)
                WHERE rowid IN @chunk
                """, new { chunk }, tx);
    }
}
