using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;

namespace PhotoGallery.Core.Cloud;

public sealed record FaceSyncProgress(int PhotosRead, int PhotosMatched);

public sealed record FaceSyncResult(int People, int Named, int PhotosRead, int PhotosMatched, int PhotosUpdated, int Faces,
    int Remapped, int Combined, bool FullScan, TimeSpan Elapsed);

/// <summary>
/// Brings OneDrive's people into the gallery through the web session (see <see cref="OneDrivePeopleClient"/>):
/// names given in OneDrive, which photos each person is in, and where their face is. Unlike the SharePoint list
/// column <see cref="OneDriveMetadataSync"/> reads, faces here mostly point at people as merged in OneDrive; the rest
/// (groups merged away, which can keep the person's name) are resolved so one person no longer shows up as several.
/// A full scan reads every photo's faces (about 250 requests for 250,000 photos); an incremental one reads the newest
/// photos until a page brings nothing new, then resolves people merged since.
/// </summary>
public sealed class OneDriveFaceSync(OneDrivePeopleClient client, GalleryDatabase database, MediaRepository media, AppSettings settings)
{
    public const string LastSyncKey = "OneDriveFacesSynced";
    public const string LastFullScanKey = "OneDriveFacesFullScan";
    /// <summary>OneDrive finds faces a while after upload, so an incremental run always re-reads the newest photos.</summary>
    private const int MinIncrementalPhotos = 2000;

    public async Task<FaceSyncResult> RunAsync(bool fullScan, IProgress<FaceSyncProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(settings.OneDriveRoot)) throw new InvalidOperationException("The OneDrive folder isn't known.");
        var root = Path.TrimEndingDirectorySeparator(settings.OneDriveRoot);
        var driveId = await client.GetDriveIdAsync(ct);

        var everyone = new List<OneDrivePerson>();
        await foreach (var person in client.GetPeopleAsync(driveId, ct)) everyone.Add(person);
        var byId = everyone.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        var index = media.GetIndexState();
        var etags = LoadETags();
        var persons = LoadPersonMap();
        int read = 0, matched = 0, updated = 0, faces = 0, quietPages = 0;
        string? next = null;
        do
        {
            var page = await client.GetPhotosAsync(driveId, next, ct: ct);
            var changed = new List<(long MediaId, OneDrivePhoto Photo)>();
            foreach (var photo in page.Photos)
            {
                read++;
                if (photo.FolderPath is null) continue;
                var local = Path.Combine(root, photo.FolderPath.Replace('/', Path.DirectorySeparatorChar), photo.Name);
                if (!index.TryGetValue(local, out var file)) continue;
                matched++;
                if (!fullScan && etags.TryGetValue(file.Id, out var etag) && etag == photo.ETag) continue;
                changed.Add((file.Id, photo));
            }
            faces += StorePage(changed, byId, persons);
            updated += changed.Count;
            progress?.Report(new FaceSyncProgress(read, matched));
            quietPages = changed.Count == 0 ? quietPages + 1 : 0;
            next = page.NextLink;
            if (!fullScan && read >= MinIncrementalPhotos && quietPages > 0) break;
        } while (next is not null);

        if (fullScan) DropUnverifiedFaces();
        var remapped = await ResolveUnlistedAsync(driveId, byId, persons, ct);
        var named = StorePeople(everyone);
        var combined = CombineSameNames(byId);

        var now = DateTime.UtcNow.ToString("O");
        media.SetSyncValue(LastSyncKey, now);
        if (fullScan) media.SetSyncValue(LastFullScanKey, now);
        return new FaceSyncResult(everyone.Count, named, read, matched, updated, faces, remapped, combined, fullScan, clock.Elapsed);
    }

    private Dictionary<long, string> LoadETags()
    {
        using var db = database.Open();
        return db.Query<(long Id, string ETag)>("SELECT Id, OneDriveETag FROM Media WHERE OneDriveETag IS NOT NULL").ToDictionary(r => r.Id, r => r.ETag);
    }

    /// <summary>OneDrive person id → local person: aliases (people merged here) first, then each person's own id.</summary>
    private Dictionary<string, long> LoadPersonMap()
    {
        using var db = database.Open();
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (guid, id) in db.Query<(string, long)>("SELECT OneDrivePersonId, Id FROM People WHERE OneDrivePersonId IS NOT NULL")) map[guid] = id;
        foreach (var (guid, id) in db.Query<(string, long)>("SELECT OneDrivePersonId, PersonId FROM PersonAliases")) map[guid] = id;
        return map;
    }

    private static long Resolve(SqliteConnection db, SqliteTransaction tx, string guid, Dictionary<string, OneDrivePerson> byId, Dictionary<string, long> persons)
    {
        if (persons.TryGetValue(guid, out var id)) return id;
        byId.TryGetValue(guid, out var person);
        id = db.ExecuteScalar<long>(
            """
            INSERT INTO People (OneDrivePersonId, Name, NameFromOneDrive, Hidden) VALUES (@guid, @name, @fromOneDrive, @hidden)
            ON CONFLICT(OneDrivePersonId) DO UPDATE SET OneDrivePersonId = excluded.OneDrivePersonId RETURNING Id
            """, new { guid, name = person?.Name, fromOneDrive = person?.Name is not null, hidden = person?.IsHidden == true }, tx);
        persons[guid] = id;
        return id;
    }

    /// <summary>Replaces the faces of these photos; returns how many faces were stored.</summary>
    private int StorePage(List<(long MediaId, OneDrivePhoto Photo)> photos, Dictionary<string, OneDrivePerson> byId, Dictionary<string, long> persons)
    {
        if (photos.Count == 0) return 0;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var stored = 0;
        foreach (var (mediaId, photo) in photos)
        {
            db.Execute("UPDATE Media SET OneDriveItemId = @ItemId, OneDriveETag = @ETag WHERE Id = @mediaId", new { photo.ItemId, photo.ETag, mediaId }, tx);
            db.Execute("DELETE FROM MediaFaces WHERE MediaId = @mediaId", new { mediaId }, tx);
            // A person is listed once per photo; keep their largest face.
            foreach (var face in photo.Faces.OrderByDescending(f => f.Box.Area))
            {
                var personId = Resolve(db, tx, face.PersonId, byId, persons);
                stored += db.Execute(
                    """
                    INSERT OR IGNORE INTO MediaFaces (MediaId, PersonId, BoxX, BoxY, BoxW, BoxH, OneDriveFaceId)
                    VALUES (@mediaId, @personId, @X, @Y, @Width, @Height, @FaceId)
                    """, new { mediaId, personId, face.Box.X, face.Box.Y, face.Box.Width, face.Box.Height, face.FaceId }, tx);
            }
        }
        SearchIndex.RefreshTags(db, tx, photos.Select(p => p.MediaId).ToList());
        tx.Commit();
        return stored;
    }

    /// <summary>After a full scan every photo OneDrive knows has fresh faces; links without a face id are stale.</summary>
    private void DropUnverifiedFaces()
    {
        using var db = database.Open();
        var removed = db.Execute("DELETE FROM MediaFaces WHERE OneDriveFaceId IS NULL");
        if (removed > 0) Log.Info($"Face sync: dropped {removed:N0} face links not confirmed by OneDrive");
    }

    /// <summary>
    /// Faces can point at groups the people list leaves out: groups merged into someone in OneDrive (which may carry
    /// that person's name too), or whose faces OneDrive has since given to someone else while the photo listing still
    /// shows the old group. Named ones get their name (and are then combined with the listed person of that name);
    /// for the others a few of their faces are looked up one by one, and the group is folded into whoever most of
    /// them belong to now. Returns how many groups were resolved.
    /// </summary>
    private async Task<int> ResolveUnlistedAsync(string driveId, Dictionary<string, OneDrivePerson> byId, Dictionary<string, long> persons, CancellationToken ct)
    {
        const int Samples = 5, Agree = 3;
        List<(long PersonId, string Guid)> unlisted;
        using (var db = database.Open())
            unlisted = db.Query<(long, string)>(
                    "SELECT p.Id, p.OneDrivePersonId FROM People p WHERE p.OneDrivePersonId IS NOT NULL AND EXISTS (SELECT 1 FROM MediaFaces f WHERE f.PersonId = p.Id)")
                .Where(r => !byId.ContainsKey(r.Item2)).ToList();

        var resolved = 0;
        foreach (var (personId, guid) in unlisted)
        {
            if (await client.GetPersonAsync(driveId, guid, ct) is { Name: { } name })
            {
                using var db = database.Open();
                db.Execute("UPDATE People SET Name = @name, NameFromOneDrive = 1 WHERE Id = @personId AND (Name IS NULL OR NameFromOneDrive = 1)",
                    new { name, personId });
                resolved++;
                Log.Info($"Face sync: unlisted person {guid} is named \"{name}\" in OneDrive");
                continue;
            }

            // Where did this group's faces go? Look at a few, spread over time.
            List<(string FaceId, string ItemId)> faces;
            using (var db = database.Open())
                faces = db.Query<(string, string)>(
                    """
                    SELECT f.OneDriveFaceId, m.OneDriveItemId FROM MediaFaces f JOIN Media m ON m.Id = f.MediaId
                    WHERE f.PersonId = @personId AND f.OneDriveFaceId IS NOT NULL AND m.OneDriveItemId IS NOT NULL
                    ORDER BY m.DateTaken
                    """, new { personId }).AsList();
            var samples = Math.Min(Samples, faces.Count);
            var needed = Math.Min(Agree, samples);
            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < samples; i++)
            {
                var (faceId, itemId) = faces[(int)((i + 0.5) * faces.Count / samples)];
                var now = (await client.GetPhotoAsync(driveId, itemId, ct))?.Faces
                    .FirstOrDefault(f => string.Equals(f.FaceId, faceId, StringComparison.OrdinalIgnoreCase))?.PersonId;
                if (now is null || string.Equals(now, guid, StringComparison.OrdinalIgnoreCase)) continue;
                votes[now] = votes.GetValueOrDefault(now) + 1;
                if (votes[now] >= needed) break;
            }
            if (votes.Count == 0) continue;
            var winner = votes.MaxBy(v => v.Value);
            if (winner.Value < needed) continue;

            using (var db = database.Open())
            using (var tx = db.BeginTransaction())
            {
                var target = Resolve(db, tx, winner.Key, byId, persons);
                if (target == personId) continue;
                var mediaIds = PeopleRepository.Fold(db, tx, personId, target);
                SearchIndex.RefreshTags(db, tx, mediaIds);
                tx.Commit();
                persons[guid] = target;
            }
            resolved++;
            Log.Info($"Face sync: faces of unlisted person {guid} now belong to {winner.Key}");
        }
        return resolved;
    }

    /// <summary>
    /// Applies OneDrive's names (unless the person was renamed here), remembers the photo OneDrive shows for each
    /// person, and drops people left without photos. Returns how many people have a name.
    /// </summary>
    private int StorePeople(List<OneDrivePerson> everyone)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        foreach (var person in everyone)
            db.Execute(
                """
                UPDATE People SET
                    Name = CASE WHEN Name IS NULL OR NameFromOneDrive = 1 THEN @Name ELSE Name END,
                    NameFromOneDrive = CASE WHEN Name IS NULL OR NameFromOneDrive = 1 THEN @Name IS NOT NULL ELSE 0 END,
                    OneDriveCoverItemId = @RepresentativeItemId
                WHERE OneDrivePersonId = @Id
                """, person, tx);
        db.Execute("DELETE FROM People WHERE Id NOT IN (SELECT PersonId FROM MediaFaces) AND (Name IS NULL OR NameFromOneDrive = 1)", transaction: tx);
        SearchIndex.RefreshTags(db, tx);
        var named = db.ExecuteScalar<int>("SELECT count(*) FROM People WHERE Name IS NOT NULL");
        tx.Commit();
        return named;
    }

    /// <summary>
    /// People with the same name are one person (OneDrive keeps the name on groups it merged, and a name can be given
    /// twice): combine them, into the one OneDrive lists if any, else the one with the most photos.
    /// </summary>
    private int CombineSameNames(Dictionary<string, OneDrivePerson> listed)
    {
        using var db = database.Open();
        var groups = db.Query<(long Id, string Name, string? Guid, long Count)>(
                "SELECT p.Id, p.Name, p.OneDrivePersonId, (SELECT count(*) FROM MediaFaces f WHERE f.PersonId = p.Id) FROM People p WHERE p.Name IS NOT NULL")
            .GroupBy(p => p.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        var combined = 0;
        foreach (var group in groups)
        {
            var target = group.OrderByDescending(p => p.Guid is not null && listed.ContainsKey(p.Guid)).ThenByDescending(p => p.Count).First();
            using var tx = db.BeginTransaction();
            var mediaIds = new HashSet<long>();
            foreach (var other in group.Where(p => p.Id != target.Id))
            {
                mediaIds.UnionWith(PeopleRepository.Fold(db, tx, other.Id, target.Id));
                combined++;
            }
            SearchIndex.RefreshTags(db, tx, mediaIds.ToList());
            tx.Commit();
            Log.Info($"Face sync: combined {group.Count()} people named \"{group.Key}\"");
        }
        return combined;
    }
}
