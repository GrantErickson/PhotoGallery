using Dapper;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Data;

/// <summary>Non-destructive edits per media item (the Edits table).</summary>
public sealed class EditRepository(GalleryDatabase database)
{
    public EditOperations? Get(long mediaId)
    {
        using var db = database.Open();
        var json = db.ExecuteScalar<string?>("SELECT OperationsJson FROM Edits WHERE MediaId = @mediaId", new { mediaId });
        return json is null ? null : EditOperations.FromJson(json);
    }

    public bool HasEdits(long mediaId)
    {
        using var db = database.Open();
        return db.ExecuteScalar<long>("SELECT count(*) FROM Edits WHERE MediaId = @mediaId", new { mediaId }) > 0;
    }

    /// <summary>Saves the edits; identity edits remove the row so the item shows as unedited.</summary>
    public void Save(long mediaId, EditOperations operations)
    {
        using var db = database.Open();
        if (operations.IsIdentity)
            db.Execute("DELETE FROM Edits WHERE MediaId = @mediaId", new { mediaId });
        else
            db.Execute(
                """
                INSERT INTO Edits (MediaId, OperationsJson, Modified) VALUES (@mediaId, @json, unixepoch())
                ON CONFLICT(MediaId) DO UPDATE SET OperationsJson = excluded.OperationsJson, Modified = excluded.Modified
                """, new { mediaId, json = operations.ToJson() });
    }
}
