using System.Text.Json;
using Dapper;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.Core.Data;

/// <summary>A video waiting to be transcribed.</summary>
public sealed record TranscriptionJob(long MediaId, string Path, long DurationMs);

/// <summary>Transcripts of videos, kept only while the file is unchanged, and their words in the search index.</summary>
public sealed class TranscriptRepository(GalleryDatabase database)
{
    /// <summary>Clips shorter than this aren't transcribed in the background (they can still be done on request).</summary>
    public const long MinDurationMs = 2000;

    private sealed class Row
    {
        public long MediaId { get; init; }
        public int Status { get; init; }
        public string? Language { get; init; }
        public string Model { get; init; } = "";
        public string? Error { get; init; }
        public string Segments { get; init; } = "[]";
    }

    /// <summary>The transcript for the file as it is now, or null if there's none (or the file has changed since).</summary>
    public Transcript? Get(long mediaId)
    {
        using var db = database.Open();
        var row = db.QuerySingleOrDefault<Row>(
            """
            SELECT t.MediaId, t.Status, t.Language, t.Model, t.Error, t.Segments
            FROM Transcripts t JOIN Media m ON m.Id = t.MediaId
            WHERE t.MediaId = @mediaId AND t.FileSize = m.FileSize AND t.FileModified = m.FileModified
            """, new { mediaId });
        return row is null
            ? null
            : new Transcript(row.MediaId, (TranscriptStatus)row.Status,
                JsonSerializer.Deserialize<List<TranscriptSegment>>(row.Segments) ?? [], row.Language, row.Model, row.Error);
    }

    /// <summary>Stores a transcript for the file's current size/date and puts its words in the search index.</summary>
    public void Save(Transcript transcript)
    {
        var text = TranscriptFormatter.PlainText(TranscriptFormatter.Paragraphs(transcript.Segments));
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute(
            """
            INSERT INTO Transcripts (MediaId, Status, Language, Model, FileSize, FileModified, CreatedUtc, Error, Segments, Text)
            SELECT Id, @Status, @Language, @Model, FileSize, FileModified, @created, @Error, @segments, @text FROM Media WHERE Id = @MediaId
            ON CONFLICT(MediaId) DO UPDATE SET
                Status = excluded.Status, Language = excluded.Language, Model = excluded.Model, FileSize = excluded.FileSize,
                FileModified = excluded.FileModified, CreatedUtc = excluded.CreatedUtc, Error = excluded.Error,
                Segments = excluded.Segments, Text = excluded.Text
            """,
            new
            {
                transcript.MediaId, Status = (int)transcript.Status, transcript.Language, transcript.Model, transcript.Error,
                created = DateTime.UtcNow.ToString("O"), segments = JsonSerializer.Serialize(transcript.Segments), text,
            }, tx);
        db.Execute("UPDATE MediaFts SET Speech = @text WHERE rowid = @MediaId", new { text, transcript.MediaId }, tx);
        tx.Commit();
    }

    /// <summary>Forgets a transcript (to try again).</summary>
    public void Delete(long mediaId)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        db.Execute("DELETE FROM Transcripts WHERE MediaId = @mediaId", new { mediaId }, tx);
        db.Execute("UPDATE MediaFts SET Speech = NULL WHERE rowid = @mediaId", new { mediaId }, tx);
        tx.Commit();
    }

    /// <summary>
    /// Videos with no transcript for their current file, newest first: visible, on disk, not an edited copy, and
    /// at least <see cref="MinDurationMs"/> long. Failed ones aren't retried here. Transcripts with speech made by
    /// another pipeline than <paramref name="model"/> come after those, to be redone.
    /// </summary>
    public List<TranscriptionJob> GetBacklog(int count, string? model = null)
    {
        using var db = database.Open();
        return db.Query<TranscriptionJob>(
            """
            SELECT m.Id AS MediaId, m.Path, m.DurationMs FROM Media m
            LEFT JOIN Transcripts t ON t.MediaId = m.Id
            WHERE m.Kind = 2 AND m.IsHidden = 0 AND m.OnlineOnly = 0 AND m.DerivedFromId IS NULL AND m.DurationMs >= @MinDurationMs
              AND (t.MediaId IS NULL OR t.FileSize <> m.FileSize OR t.FileModified <> m.FileModified
                   OR (@model IS NOT NULL AND t.Status = 0 AND t.Text <> '' AND t.Model <> @model))
            ORDER BY t.MediaId IS NOT NULL, m.DateTaken DESC
            LIMIT @count
            """, new { MinDurationMs, count, model }).AsList();
    }

    /// <summary>How many of the videos the background work covers have a transcript (with speech or not).</summary>
    public (int Done, int Total, int WithSpeech) GetProgress()
    {
        using var db = database.Open();
        return db.QuerySingle<(int, int, int)>(
            """
            SELECT count(t.MediaId), count(*), coalesce(sum(t.Status = 0 AND t.Text <> ''), 0) FROM Media m
            LEFT JOIN Transcripts t ON t.MediaId = m.Id AND t.FileSize = m.FileSize AND t.FileModified = m.FileModified
            WHERE m.Kind = 2 AND m.IsHidden = 0 AND m.OnlineOnly = 0 AND m.DerivedFromId IS NULL AND m.DurationMs >= @MinDurationMs
            """, new { MinDurationMs });
    }
}
