using System.Data;
using Dapper;

namespace PhotoGallery.Core.Data;

/// <summary>
/// Keeps the FTS "Tags" column (tag names, named people, your places, the OpenStreetMap place and its kind) in step with
/// MediaTags, MediaFaces, MediaPlaces and Media.PoiId.
/// </summary>
internal static class SearchIndex
{
    internal const string TagsExpression =
        """
        trim(coalesce((SELECT group_concat(t.Name, ' ') FROM MediaTags mt JOIN Tags t ON t.Id = mt.TagId WHERE mt.MediaId = MediaFts.rowid), '')
             || ' ' ||
             coalesce((SELECT group_concat(p.Name, ' ') FROM MediaFaces f JOIN People p ON p.Id = f.PersonId WHERE f.MediaId = MediaFts.rowid AND p.Name IS NOT NULL), '')
             || ' ' ||
             coalesce((SELECT group_concat(pl.Name, ' ') FROM MediaPlaces mp JOIN Places pl ON pl.Id = mp.PlaceId WHERE mp.MediaId = MediaFts.rowid), '')
             || ' ' ||
             coalesce((SELECT po.Name || ' ' || po.Kind FROM Media mm JOIN Pois po ON po.Id = mm.PoiId WHERE mm.Id = MediaFts.rowid), ''))
        """;

    /// <summary>Recomputes tags for every row (after a OneDrive sync).</summary>
    public static void RefreshTags(IDbConnection db, IDbTransaction tx) =>
        db.Execute($"UPDATE MediaFts SET Tags = {TagsExpression}", transaction: tx, commandTimeout: 600);

    /// <summary>Recomputes tags for some rows (after tagging or naming a person).</summary>
    public static void RefreshTags(IDbConnection db, IDbTransaction tx, IEnumerable<long> mediaIds)
    {
        foreach (var chunk in mediaIds.Chunk(500))
            db.Execute($"UPDATE MediaFts SET Tags = {TagsExpression} WHERE rowid IN @chunk", new { chunk }, tx);
    }
}
