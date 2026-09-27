using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Data;

public sealed record IndexedFile(long Id, long FileSize, long FileModified, bool OnlineOnly = false);

public sealed class FolderRow
{
    public long Id { get; init; }
    public long? ParentId { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class LibraryStats
{
    public long Photos { get; init; }
    public long Videos { get; init; }
    public long Screenshots { get; init; }
    public long LocalPairs { get; init; }
    public long Embedded { get; init; }
    public long Cloud { get; init; }
}

public sealed class MediaRepository(GalleryDatabase database)
{
    private const string SummaryColumns =
        "m.Id, m.Kind, m.DateTaken, m.Motion, m.Rating, m.DurationMs, m.IsScreenshot, EXISTS (SELECT 1 FROM Edits e WHERE e.MediaId = m.Id) AS IsEdited, " +
        "m.DerivedFromId IS NOT NULL AS IsDerived, " +
        "(SELECT CAST(round((min(f.BoxX) + max(f.BoxX + f.BoxW)) * 5000) AS INTEGER) * 100000 + CAST(round((min(f.BoxY) + max(f.BoxY + f.BoxH)) * 5000) AS INTEGER) " +
        "FROM MediaFaces f WHERE f.MediaId = m.Id AND f.BoxX IS NOT NULL) AS FaceFocus";

    // ---------- Indexing ----------

    public Dictionary<string, IndexedFile> GetIndexState()
    {
        using var db = database.Open();
        var rows = db.Query<(string Path, long Id, long FileSize, long FileModified, long OnlineOnly)>("SELECT Path, Id, FileSize, FileModified, OnlineOnly FROM Media");
        var map = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.Path] = new IndexedFile(r.Id, r.FileSize, r.FileModified, r.OnlineOnly != 0);
        return map;
    }

    public string? GetSyncValue(string key)
    {
        using var db = database.Open();
        return db.ExecuteScalar<string?>("SELECT Value FROM SyncState WHERE Key = @key", new { key });
    }

    public void SetSyncValue(string key, string? value)
    {
        using var db = database.Open();
        db.Execute("INSERT INTO SyncState (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value", new { key, value });
    }

    public Dictionary<string, long> GetFolderIds()
    {
        using var db = database.Open();
        return db.Query<(string Path, long Id)>("SELECT Path, Id FROM Folders")
            .ToDictionary(r => r.Path, r => r.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns the id of the folder, creating it and any missing ancestors up to (and including) <paramref name="root"/>.</summary>
    public long EnsureFolder(SqliteConnection db, SqliteTransaction tx, Dictionary<string, long> cache, string folder, string root)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (cache.TryGetValue(folder, out var id)) return id;

        long? parentId = null;
        var isRoot = string.Equals(folder, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        if (!isRoot && Path.GetDirectoryName(folder) is { } parent)
            parentId = EnsureFolder(db, tx, cache, parent, root);

        var name = isRoot ? folder : Path.GetFileName(folder);
        id = db.ExecuteScalar<long>(
            """
            INSERT INTO Folders (ParentId, Path, Name) VALUES (@parentId, @folder, @name)
            ON CONFLICT(Path) DO UPDATE SET ParentId = excluded.ParentId
            RETURNING Id
            """, new { parentId, folder, name }, tx);
        cache[folder] = id;
        return id;
    }

    /// <summary>Inserts or updates files by path. Rating, album membership and tags survive a re-index.</summary>
    public void Upsert(SqliteConnection db, SqliteTransaction tx, MediaItem item, string folderPath)
    {
        item.Id = db.ExecuteScalar<long>(
            """
            INSERT INTO Media (FolderId, Path, FileName, FileSize, FileModified, Kind, DateTaken, DateSource, Width, Height,
                               Orientation, DurationMs, CameraMake, CameraModel, Latitude, Longitude, IsScreenshot,
                               ContentId, MotionOffset, MotionLength, Motion, OnlineOnly)
            VALUES (@FolderId, @Path, @FileName, @FileSize, @FileModified, @Kind, @DateTaken, @DateSource, @Width, @Height,
                    @Orientation, @DurationMs, @CameraMake, @CameraModel, @Latitude, @Longitude, @IsScreenshot,
                    @ContentId, @MotionOffset, @MotionLength, @Motion, @OnlineOnly)
            ON CONFLICT(Path) DO UPDATE SET
                QuickHash = CASE WHEN Media.FileSize = excluded.FileSize AND Media.FileModified = excluded.FileModified THEN Media.QuickHash END,
                PerceptualHash = CASE WHEN Media.FileSize = excluded.FileSize AND Media.FileModified = excluded.FileModified THEN Media.PerceptualHash END,
                FolderId = excluded.FolderId, FileName = excluded.FileName, FileSize = excluded.FileSize,
                FileModified = excluded.FileModified, Kind = excluded.Kind, DateTaken = excluded.DateTaken,
                DateSource = excluded.DateSource, Width = excluded.Width, Height = excluded.Height,
                Orientation = excluded.Orientation, DurationMs = excluded.DurationMs, CameraMake = excluded.CameraMake,
                CameraModel = excluded.CameraModel, Latitude = excluded.Latitude, Longitude = excluded.Longitude,
                IsScreenshot = excluded.IsScreenshot, ContentId = excluded.ContentId,
                MotionOffset = excluded.MotionOffset, MotionLength = excluded.MotionLength, OnlineOnly = excluded.OnlineOnly,
                -- Pairing/cloud state is owned by RecomputeMotion; only embedded motion comes from the file itself.
                Motion = CASE WHEN excluded.MotionLength > 0 THEN 2 WHEN Media.Motion = 2 THEN 0 ELSE Media.Motion END
            RETURNING Id
            """, item, tx);

        db.Execute(
            """
            DELETE FROM Transcripts WHERE MediaId = @Id AND (FileSize <> @FileSize OR FileModified <> @FileModified);
            DELETE FROM PhotoText WHERE MediaId = @Id AND (FileSize <> @FileSize OR FileModified <> @FileModified);
            DELETE FROM MediaFts WHERE rowid = @Id;
            INSERT INTO MediaFts (rowid, Name, Folder, Tags, Camera, Speech, PhotoText)
            VALUES (@Id, @FileName, @folderPath,
                    trim(coalesce((SELECT group_concat(t.Name, ' ') FROM MediaTags mt JOIN Tags t ON t.Id = mt.TagId WHERE mt.MediaId = @Id), '') || ' ' ||
                         coalesce((SELECT group_concat(pp.Name, ' ') FROM MediaFaces f JOIN People pp ON pp.Id = f.PersonId WHERE f.MediaId = @Id AND pp.Name IS NOT NULL), '')),
                    trim(coalesce(@CameraMake, '') || ' ' || coalesce(@CameraModel, '')),
                    (SELECT Text FROM Transcripts WHERE MediaId = @Id),
                    (SELECT nullif(Text, '') FROM PhotoText WHERE MediaId = @Id));
            """, new { item.Id, item.FileName, folderPath, item.CameraMake, item.CameraModel, item.FileSize, item.FileModified }, tx);
    }

    public void Delete(SqliteConnection db, SqliteTransaction tx, IEnumerable<long> ids)
    {
        foreach (var chunk in ids.Chunk(500))
        {
            db.Execute("DELETE FROM MediaFts WHERE rowid IN @chunk", new { chunk }, tx);
            db.Execute("DELETE FROM Media WHERE Id IN @chunk", new { chunk }, tx);
        }
    }

    /// <summary>Forgets everything under a library root that was removed from settings (files are untouched).</summary>
    public void RemoveRoot(string root)
    {
        root = Indexing.LibraryIndexer.LongPath(root); // paths are stored in long form
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        using (var db = database.Open())
        using (var tx = db.BeginTransaction())
        {
            var ids = db.Query<long>("SELECT Id FROM Media WHERE substr(Path, 1, length(@prefix)) = @prefix COLLATE NOCASE", new { prefix }, tx);
            Delete(db, tx, ids);
            db.Execute("DELETE FROM Folders WHERE Path = @root COLLATE NOCASE OR substr(Path, 1, length(@prefix)) = @prefix COLLATE NOCASE",
                new { root = Path.TrimEndingDirectorySeparator(root), prefix }, tx);
            tx.Commit();
        }
        RecomputeMotion();
    }

    /// <summary>
    /// Recomputes Live Photo / motion state for the whole library:
    /// local JPG/HEIC + MOV pairs by Apple content identifier (MOV is hidden from the grid),
    /// Android embedded motion, and iPhone stills whose video only exists in OneDrive.
    /// </summary>
    public void RecomputeMotion()
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute(
            """
            UPDATE Media SET IsHidden = 0, PairedId = NULL WHERE PairedId IS NOT NULL OR IsHidden = 1;
            UPDATE Media SET Motion = 0 WHERE Motion IN (1, 2);

            CREATE TEMP TABLE IF NOT EXISTS Pairs (StillId INTEGER PRIMARY KEY, VideoId INTEGER);
            DELETE FROM Pairs;
            INSERT INTO Pairs (StillId, VideoId)
            SELECT s.Id,
                   (SELECT v.Id FROM Media v
                     WHERE v.ContentId = s.ContentId AND v.Kind = 2 AND v.DurationMs <= 10000 AND v.DerivedFromId IS NULL
                     ORDER BY (v.FolderId = s.FolderId) DESC, v.Id LIMIT 1)
            FROM Media s
            WHERE s.Kind IN (1, 3) AND s.ContentId IS NOT NULL;
            DELETE FROM Pairs WHERE VideoId IS NULL;

            UPDATE Media SET Motion = 1, PairedId = (SELECT VideoId FROM Pairs WHERE StillId = Media.Id)
             WHERE Id IN (SELECT StillId FROM Pairs);
            UPDATE Media SET IsHidden = 1, PairedId = (SELECT min(StillId) FROM Pairs WHERE VideoId = Media.Id)
             WHERE Id IN (SELECT VideoId FROM Pairs);

            UPDATE Media SET Motion = 2 WHERE MotionLength > 0 AND Motion = 0;

            UPDATE Media SET Motion = 3
             WHERE Kind = 1 AND ContentId IS NOT NULL AND Motion = 0 AND PairedId IS NULL;
            """, transaction: tx);
        tx.Commit();
    }

    // ---------- Browsing ----------

    public List<MediaSummary> Query(MediaFilter filter)
    {
        var (sql, parameters) = BuildQuery(filter, SummaryColumns);
        using var db = database.Open();
        return db.Query<MediaSummary>(sql, parameters).AsList();
    }

    public long Count(MediaFilter filter)
    {
        var (sql, parameters) = BuildQuery(filter, "count(*)", ordered: false);
        using var db = database.Open();
        return db.ExecuteScalar<long>(sql, parameters);
    }

    internal static (string Sql, DynamicParameters Parameters) BuildQuery(MediaFilter f, string columns, bool ordered = true)
    {
        var p = new DynamicParameters();
        var sql = new StringBuilder($"SELECT {columns} FROM Media m");
        if (f.AlbumId is { } albumId)
        {
            sql.Append(" JOIN AlbumMedia am ON am.MediaId = m.Id AND am.AlbumId = @albumId");
            p.Add("albumId", albumId);
        }
        sql.Append(" WHERE m.IsHidden = 0");

        switch (f.Kinds)
        {
            case KindFilter.Photos: sql.Append(" AND m.Kind IN (1, 3)"); break;
            case KindFilter.Videos: sql.Append(" AND m.Kind = 2"); break;
        }
        // Screenshots are excluded from the main flow, but always shown when browsing a folder or album explicitly.
        if (f.ScreenshotsOnly) sql.Append(" AND m.IsScreenshot = 1");
        else if (!f.IncludeScreenshots && f.FolderId is null && f.AlbumId is null) sql.Append(" AND m.IsScreenshot = 0");
        if (f.MotionOnly) sql.Append(" AND m.Motion IN (1, 2, 3)");
        if (f.EditedOnly) sql.Append(" AND m.Id IN (SELECT MediaId FROM Edits)");
        if (f.MinRating > 0)
        {
            sql.Append(" AND m.Rating >= @minRating");
            p.Add("minRating", f.MinRating);
        }
        if (f.FolderId is { } folderId)
        {
            if (f.IncludeSubfolders)
                sql.Append("""
                     AND m.FolderId IN (WITH RECURSIVE sub(Id) AS (SELECT @folderId UNION ALL
                       SELECT f.Id FROM Folders f JOIN sub ON f.ParentId = sub.Id) SELECT Id FROM sub)
                    """);
            else
                sql.Append(" AND m.FolderId = @folderId");
            p.Add("folderId", folderId);
        }
        if (f.TagId is { } tagId)
        {
            sql.Append(" AND m.Id IN (SELECT MediaId FROM MediaTags WHERE TagId = @tagId)");
            p.Add("tagId", tagId);
        }
        if (f.PersonId is { } personId)
        {
            sql.Append(" AND m.Id IN (SELECT MediaId FROM MediaFaces WHERE PersonId = @personId)");
            p.Add("personId", personId);
        }
        if (ToFtsQuery(f.Text) is { } fts)
        {
            sql.Append(" AND m.Id IN (SELECT rowid FROM MediaFts WHERE MediaFts MATCH @fts)");
            p.Add("fts", fts);
        }
        if (f.From is { } from)
        {
            sql.Append(" AND m.DateTaken >= @from");
            p.Add("from", ToUnix(from));
        }
        if (f.To is { } to)
        {
            sql.Append(" AND m.DateTaken < @to");
            p.Add("to", ToUnix(to));
        }
        if (f.MonthDay is { } monthDay)
        {
            sql.Append(" AND strftime('%m-%d', m.DateTaken, 'unixepoch') = @monthDay");
            p.Add("monthDay", $"{monthDay.Month:00}-{monthDay.Day:00}");
        }
        if (f.Ids is { } ids)
        {
            // json_each avoids SQLite's parameter limit for big clusters.
            sql.Append(" AND m.Id IN (SELECT value FROM json_each(@idsJson))");
            p.Add("idsJson", System.Text.Json.JsonSerializer.Serialize(ids));
        }
        if (f.Bounds is { } bounds)
        {
            var (south, west, north, east) = bounds;
            sql.Append(" AND m.Latitude BETWEEN @south AND @north AND m.Longitude BETWEEN @west AND @east");
            p.AddDynamicParams(new { south, west, north, east });
        }

        if (ordered)
            sql.Append(f.AlbumId is null ? " ORDER BY m.DateTaken DESC, m.Id DESC" : " ORDER BY am.SortOrder, m.DateTaken");
        return (sql.ToString(), p);
    }

    /// <summary>Each word becomes a quoted prefix term; all terms must match.</summary>
    internal static string? ToFtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", ""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        var query = string.Join(' ', terms);
        return query.Length == 0 ? null : query;
    }

    public static long ToUnix(DateTime local) => (long)(DateTime.SpecifyKind(local, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    public MediaItem? Get(long id)
    {
        using var db = database.Open();
        return db.QuerySingleOrDefault<MediaItem>("SELECT * FROM Media WHERE Id = @id", new { id });
    }

    public string? GetPath(long id)
    {
        using var db = database.Open();
        return db.ExecuteScalar<string?>("SELECT Path FROM Media WHERE Id = @id", new { id });
    }

    public string? GetFolderPath(long folderId)
    {
        using var db = database.Open();
        return db.ExecuteScalar<string?>("SELECT Path FROM Folders WHERE Id = @folderId", new { folderId });
    }

    public List<FolderRow> GetFolders()
    {
        using var db = database.Open();
        return db.Query<FolderRow>("SELECT Id, ParentId, Path, Name FROM Folders ORDER BY Path").AsList();
    }

    /// <summary>Every located photo/video (screenshots excluded) for the map: id, latitude, longitude.</summary>
    public List<(long Id, double Latitude, double Longitude)> GetGeoPoints()
    {
        using var db = database.Open();
        return db.Query<(long, double, double)>(
            "SELECT Id, Latitude, Longitude FROM Media WHERE Latitude IS NOT NULL AND Longitude IS NOT NULL AND IsHidden = 0 AND IsScreenshot = 0").AsList();
    }

    public void SetRating(IEnumerable<long> ids, int rating)
    {
        using var db = database.Open();
        db.Execute("UPDATE Media SET Rating = @rating WHERE Id IN @ids", new { rating = Math.Clamp(rating, 0, 5), ids });
    }

    public void SetDerivedFrom(long id, long originalId)
    {
        using var db = database.Open();
        db.Execute("UPDATE Media SET DerivedFromId = @originalId WHERE Id = @id", new { id, originalId });
    }

    public void SetOneDriveItemId(long id, string itemId)
    {
        using var db = database.Open();
        db.Execute("UPDATE Media SET OneDriveItemId = @itemId WHERE Id = @id", new { id, itemId });
    }

    public void SetMotion(long id, MotionSource motion)
    {
        using var db = database.Open();
        db.Execute("UPDATE Media SET Motion = @motion WHERE Id = @id", new { id, motion });
    }

    public long CountEdited()
    {
        using var db = database.Open();
        return db.ExecuteScalar<long>("SELECT count(*) FROM Edits");
    }

    public LibraryStats GetStats()
    {
        using var db = database.Open();
        return db.QuerySingle<LibraryStats>(
            """
            SELECT
              coalesce(sum(Kind IN (1, 3) AND IsScreenshot = 0), 0) AS Photos,
              coalesce(sum(Kind = 2 AND IsHidden = 0), 0) AS Videos,
              coalesce(sum(IsScreenshot), 0) AS Screenshots,
              coalesce(sum(Motion = 1), 0) AS LocalPairs,
              coalesce(sum(Motion = 2), 0) AS Embedded,
              coalesce(sum(Motion = 3), 0) AS Cloud
            FROM Media
            """);
    }
}
