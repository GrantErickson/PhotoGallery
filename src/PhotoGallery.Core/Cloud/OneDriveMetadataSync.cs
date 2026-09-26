using System.Text.Json;
using Dapper;
using PhotoGallery.Core.Data;

namespace PhotoGallery.Core.Cloud;

public sealed record MetadataSyncProgress(int ItemsRead, int Matched);

public sealed record MetadataSyncResult(int ItemsRead, int Matched, int Tagged, int People, TimeSpan Elapsed);

/// <summary>
/// Pulls OneDrive's AI tags, categories, places and recognised people for every file, from the SharePoint list
/// behind the personal drive (see docs/plan.md "OneDrive tags and people"), and stores them locally:
/// Tags/MediaTags with Source = 1 and People/MediaFaces. Undocumented, so any failure leaves existing data intact.
/// People come from here only until <see cref="OneDriveFaceSync"/> has run: this list's person ids are the groups as
/// first detected (before any merge made in OneDrive), so they split merged people into duplicates.
/// </summary>
public sealed class OneDriveMetadataSync(OneDriveClient client, GalleryDatabase database, MediaRepository media, AppSettings settings)
{
    public const string LastSyncKey = "OneDriveMetadataSynced";
    private const int OneDriveSource = 1;

    private const string FirstPage =
        "https://graph.microsoft.com/v1.0/me/drive/list/items?$select=id&$top=999" +
        "&$expand=fields($select=FileRef,FSObjType,RecognizedEntities,MediaServiceOCR,MediaServiceLocation,TagListTags,UserAddedTags)";

    public async Task<MetadataSyncResult> RunAsync(bool includePeople = true, IProgress<MetadataSyncProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrEmpty(settings.OneDriveRoot)) throw new InvalidOperationException("The OneDrive folder isn't known.");
        var root = Path.TrimEndingDirectorySeparator(settings.OneDriveRoot);
        var ids = media.GetIndexState().ToDictionary(e => e.Key, e => e.Value.Id, StringComparer.OrdinalIgnoreCase);

        var tagsByMedia = new Dictionary<long, List<CloudTag>>();
        var peopleByMedia = new Dictionary<long, List<string>>();
        int read = 0, matched = 0;
        string? url = FirstPage;
        while (url is not null)
        {
            using var page = await client.GetJsonAsync(url, ct);
            var body = page.RootElement;
            foreach (var item in body.GetProperty("value").EnumerateArray())
            {
                read++;
                if (!item.TryGetProperty("fields", out var fields)) continue;
                if (fields.TryGetProperty("FSObjType", out var type) && type.ToString() == "1") continue; // folder
                if (Str(fields, "FileRef") is not { } fileRef || ToLocalPath(fileRef, root) is not { } local || !ids.TryGetValue(local, out var mediaId))
                    continue;
                matched++;

                var tags = CloudTagParser.Parse(Str(fields, "MediaServiceOCR"), Str(fields, "MediaServiceLocation"),
                    Lookups(fields, "TagListTags"), Str(fields, "UserAddedTags"));
                if (tags.Count > 0) tagsByMedia[mediaId] = tags;
                var people = Lookups(fields, "RecognizedEntities").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (people.Count > 0 && includePeople) peopleByMedia[mediaId] = people;
            }
            progress?.Report(new MetadataSyncProgress(read, matched));
            url = body.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        var peopleCount = Store(tagsByMedia, includePeople ? peopleByMedia : null);
        media.SetSyncValue(LastSyncKey, DateTime.UtcNow.ToString("O"));
        return new MetadataSyncResult(read, matched, tagsByMedia.Count, peopleCount, clock.Elapsed);
    }

    /// <summary>"/personal/19b4…/Documents/Pictures/a.jpg" → "D:\OneDrive\Pictures\a.jpg".</summary>
    internal static string? ToLocalPath(string fileRef, string oneDriveRoot)
    {
        const string marker = "/Documents/";
        var i = fileRef.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var relative = fileRef[(i + marker.Length)..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(oneDriveRoot, relative);
    }

    private static string? Str(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Lookups(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in v.EnumerateArray())
            if (e.TryGetProperty("LookupValue", out var value) && value.GetString() is { Length: > 0 } s)
                yield return s;
    }

    /// <summary>Replaces all OneDrive-sourced tags (and face links, if given) in one transaction; returns the number of people.</summary>
    private int Store(Dictionary<long, List<CloudTag>> tagsByMedia, Dictionary<long, List<string>>? peopleByMedia)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();

        db.Execute("DELETE FROM MediaTags WHERE TagId IN (SELECT Id FROM Tags WHERE Source = @OneDriveSource)", new { OneDriveSource }, tx);
        if (peopleByMedia is not null) db.Execute("DELETE FROM MediaFaces", transaction: tx);

        var tagIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagsByMedia.Values.SelectMany(t => t).DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            tagIds[tag.Name] = db.ExecuteScalar<long>(
                """
                INSERT INTO Tags (Name, Source, TagType) VALUES (@Name, @OneDriveSource, @Type)
                ON CONFLICT(Name, Source) DO UPDATE SET TagType = excluded.TagType RETURNING Id
                """, new { tag.Name, OneDriveSource, Type = (int)tag.Type }, tx);
        foreach (var (mediaId, tags) in tagsByMedia)
            foreach (var tag in tags)
                db.Execute("INSERT OR IGNORE INTO MediaTags (MediaId, TagId) VALUES (@mediaId, @tagId)", new { mediaId, tagId = tagIds[tag.Name] }, tx);

        if (peopleByMedia is null)
        {
            db.Execute("DELETE FROM Tags WHERE Source = @OneDriveSource AND Id NOT IN (SELECT TagId FROM MediaTags)", new { OneDriveSource }, tx);
            SearchIndex.RefreshTags(db, tx);
            tx.Commit();
            return 0;
        }

        // Merged people: their OneDrive ids map onto the surviving person.
        var aliases = db.Query<(string Guid, long PersonId)>("SELECT OneDrivePersonId, PersonId FROM PersonAliases", transaction: tx)
            .ToDictionary(a => a.Guid, a => a.PersonId, StringComparer.OrdinalIgnoreCase);
        var personIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in peopleByMedia.Values.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase))
            personIds[guid] = aliases.TryGetValue(guid, out var alias) ? alias : db.ExecuteScalar<long>(
                "INSERT INTO People (OneDrivePersonId) VALUES (@guid) ON CONFLICT(OneDrivePersonId) DO UPDATE SET OneDrivePersonId = excluded.OneDrivePersonId RETURNING Id",
                new { guid }, tx);
        foreach (var (mediaId, people) in peopleByMedia)
            foreach (var guid in people)
                db.Execute("INSERT OR IGNORE INTO MediaFaces (MediaId, PersonId) VALUES (@mediaId, @personId)", new { mediaId, personId = personIds[guid] }, tx);

        // Drop OneDrive tags nobody uses any more; keep people the user has named even if OneDrive lost them.
        db.Execute("DELETE FROM Tags WHERE Source = @OneDriveSource AND Id NOT IN (SELECT TagId FROM MediaTags)", new { OneDriveSource }, tx);
        db.Execute("DELETE FROM People WHERE Name IS NULL AND Id NOT IN (SELECT PersonId FROM MediaFaces)", transaction: tx);

        SearchIndex.RefreshTags(db, tx);
        tx.Commit();
        return personIds.Count;
    }
}
