using System.Text.Json;
using Dapper;
using PhotoGallery.Core.Ocr;

namespace PhotoGallery.Core.Data;

/// <summary>A photo waiting to have its text read.</summary>
public sealed record PhotoTextJob(long MediaId, string Path);

/// <summary>Text read from photos (OCR), kept only while the file is unchanged, and its words in the search index.</summary>
public sealed class PhotoTextRepository(GalleryDatabase database)
{
    /// <summary>OneDrive's categories for photos that are mostly text; they (and screenshots) are read first.</summary>
    private static readonly string[] TextCategories = ["Text", "Receipt", "Document", "Whiteboard", "Sign", "Menu", "Poster", "Book"];

    private sealed class Row
    {
        public long MediaId { get; init; }
        public string Engine { get; init; } = "";
        public string Lines { get; init; } = "[]";
    }

    /// <summary>The text of the photo as it is now, or null if it hasn't been read (or the file has changed since).</summary>
    public PhotoText? Get(long mediaId)
    {
        using var db = database.Open();
        var row = db.QuerySingleOrDefault<Row>(
            """
            SELECT p.MediaId, p.Engine, p.Lines FROM PhotoText p JOIN Media m ON m.Id = p.MediaId
            WHERE p.MediaId = @mediaId AND p.FileSize = m.FileSize AND p.FileModified = m.FileModified
            """, new { mediaId });
        return row is null ? null : new PhotoText(row.MediaId, JsonSerializer.Deserialize<List<OcrLine>>(row.Lines) ?? [], row.Engine);
    }

    /// <summary>Stores what was read (possibly nothing) for the file's current size/date and indexes its words.</summary>
    public void Save(PhotoText text)
    {
        var plain = text.Text;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute(
            """
            INSERT INTO PhotoText (MediaId, FileSize, FileModified, Engine, CreatedUtc, Lines, Text)
            SELECT Id, FileSize, FileModified, @Engine, @created, @lines, @plain FROM Media WHERE Id = @MediaId
            ON CONFLICT(MediaId) DO UPDATE SET
                FileSize = excluded.FileSize, FileModified = excluded.FileModified, Engine = excluded.Engine,
                CreatedUtc = excluded.CreatedUtc, Lines = excluded.Lines, Text = excluded.Text
            """,
            new { text.MediaId, text.Engine, created = DateTime.UtcNow.ToString("O"), lines = JsonSerializer.Serialize(text.Lines), plain }, tx);
        db.Execute("UPDATE MediaFts SET PhotoText = @plain WHERE rowid = @MediaId", new { plain = plain.Length == 0 ? null : plain, text.MediaId }, tx);
        tx.Commit();
    }

    /// <summary>
    /// Photos not read yet for their current file: visible, on disk, not an edited copy. Screenshots and photos
    /// OneDrive filed as text (receipts, documents, whiteboards…) first, then newest first.
    /// </summary>
    public List<PhotoTextJob> GetBacklog(int count)
    {
        using var db = database.Open();
        return db.Query<PhotoTextJob>(
            """
            SELECT m.Id AS MediaId, m.Path FROM Media m
            LEFT JOIN PhotoText p ON p.MediaId = m.Id
            WHERE m.Kind IN (1, 3) AND m.IsHidden = 0 AND m.OnlineOnly = 0 AND m.DerivedFromId IS NULL
              AND (p.MediaId IS NULL OR p.FileSize <> m.FileSize OR p.FileModified <> m.FileModified)
            ORDER BY (m.IsScreenshot = 1 OR EXISTS (SELECT 1 FROM MediaTags mt JOIN Tags t ON t.Id = mt.TagId
                                                   WHERE mt.MediaId = m.Id AND t.Name IN @TextCategories)) DESC,
                     m.DateTaken DESC
            LIMIT @count
            """, new { count, TextCategories }).AsList();
    }

    /// <summary>How many photos have been read, of those the background work covers, and how many have text.</summary>
    public (int Done, int Total, int WithText) GetProgress()
    {
        using var db = database.Open();
        return db.QuerySingle<(int, int, int)>(
            """
            SELECT count(p.MediaId), count(*), coalesce(sum(p.Text <> ''), 0) FROM Media m
            LEFT JOIN PhotoText p ON p.MediaId = m.Id AND p.FileSize = m.FileSize AND p.FileModified = m.FileModified
            WHERE m.Kind IN (1, 3) AND m.IsHidden = 0 AND m.OnlineOnly = 0 AND m.DerivedFromId IS NULL
            """);
    }
}
