using System.Runtime.InteropServices;
using Dapper;
using PhotoGallery.Core.Similarity;

namespace PhotoGallery.Core.Data;

/// <summary>
/// CLIP embeddings of photos and videos (from their thumbnails), kept only while the file is unchanged: what "Similar
/// photos" and searching by description compare.
/// </summary>
public sealed class EmbeddingRepository(GalleryDatabase database)
{
    /// <summary>Items without an embedding for their current file: visible and on disk, newest first.</summary>
    public List<(long Id, string Path)> GetBacklog(int count)
    {
        using var db = database.Open();
        return db.Query<(long, string)>(
            """
            SELECT m.Id, m.Path FROM Media m
            LEFT JOIN Embeddings e ON e.MediaId = m.Id
            WHERE m.IsHidden = 0 AND m.OnlineOnly = 0
              AND (e.MediaId IS NULL OR e.FileSize <> m.FileSize OR e.FileModified <> m.FileModified)
            ORDER BY m.DateTaken DESC
            LIMIT @count
            """, new { count }).AsList();
    }

    /// <summary>Stores embeddings (null = the item couldn't be read; remembered so it isn't retried until it changes).</summary>
    public void Save(IReadOnlyCollection<(long Id, sbyte[]? Vector)> items)
    {
        if (items.Count == 0) return;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        foreach (var (id, vector) in items)
            db.Execute(
                """
                INSERT INTO Embeddings (MediaId, FileSize, FileModified, Vector)
                SELECT Id, FileSize, FileModified, @blob FROM Media WHERE Id = @id
                ON CONFLICT(MediaId) DO UPDATE SET FileSize = excluded.FileSize, FileModified = excluded.FileModified, Vector = excluded.Vector
                """, new { id, blob = vector is null ? [] : MemoryMarshal.AsBytes(vector.AsSpan()).ToArray() }, tx);
        tx.Commit();
    }

    /// <summary>Every current, readable embedding, for searching.</summary>
    public SimilarityIndex LoadIndex()
    {
        using var db = database.Open();
        var ids = new List<long>();
        var vectors = new MemoryStream();
        using var reader = db.ExecuteReader(
            """
            SELECT e.MediaId, e.Vector FROM Embeddings e JOIN Media m ON m.Id = e.MediaId
            WHERE m.IsHidden = 0 AND e.FileSize = m.FileSize AND e.FileModified = m.FileModified AND length(e.Vector) = @size
            """, new { size = Embedding.Dimensions });
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
            vectors.Write((byte[])reader.GetValue(1)); // whole value: blob streaming needs a rowid
        }
        return new SimilarityIndex([.. ids], MemoryMarshal.Cast<byte, sbyte>(vectors.GetBuffer().AsSpan(0, (int)vectors.Length)).ToArray());
    }

    /// <summary>How many items have an embedding, of those the background work covers.</summary>
    public (int Done, int Total) GetProgress()
    {
        using var db = database.Open();
        return db.QuerySingle<(int, int)>(
            """
            SELECT count(e.MediaId), count(*) FROM Media m
            LEFT JOIN Embeddings e ON e.MediaId = m.Id AND e.FileSize = m.FileSize AND e.FileModified = m.FileModified
            WHERE m.IsHidden = 0 AND m.OnlineOnly = 0
            """);
    }
}
