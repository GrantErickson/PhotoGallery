using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Tests;

public sealed class OneDrivePeopleTests : IDisposable
{
    private const string Api = "https://my.microsoftpersonalcontent.com/_api/v2.1";
    private const string Secret = "bearer EwAY-secret";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-people-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly PeopleRepository _people;

    public OneDrivePeopleTests()
    {
        Directory.CreateDirectory(_dir);
        Log.Initialize(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _people = new PeopleRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Token : IOneDriveWebToken
    {
        public bool IsConnected => true;
        public Task<string?> GetAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult<string?>(Secret);
    }

    /// <summary>Answers by URL: the first route whose key the URL contains.</summary>
    private sealed class Server(Func<string, string?> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = route(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Person(string id, string? name, int count = 1, bool hidden = false) => JsonSerializer.Serialize(new
    {
        id, photoCount = count, isHidden = hidden, representativeItemId = "D!rep-" + id,
        identity = new { user = new { displayName = name, id } },
    });

    private static string Face(string faceId, string personId, int left, int top, int width, int height) =>
        JsonSerializer.Serialize(new
        {
            id = faceId, boundingBoxLeft = left, boundingBoxTop = top, boundingBoxWidth = width, boundingBoxHeight = height,
            recognizedEntity = new { id = personId },
        });

    private static string Photo(string itemId, string folder, string name, string etag, params string[] faces) =>
        $$"""
        {"@odata.etag":"{{etag}}","id":"{{itemId}}","name":"{{name}}",
         "parentReference":{"driveId":"D","path":"/drives/D/root:/{{folder}}"},
         "image":{"width":4000,"height":3000},"photo":{"orientation":"1"},
         "detectedEntities":[{{string.Join(",", faces)}}]}
        """;

    private static string Page(string? next, params string[] values) =>
        "{\"value\":[" + string.Join(",", values) + "]" + (next is null ? "" : ",\"@odata.nextLink\":" + JsonSerializer.Serialize(next)) + "}";

    private long AddPhoto(string path, string? contentId = null)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var folder = Path.GetDirectoryName(path)!;
        var item = new MediaItem { Path = path, FileName = Path.GetFileName(path), FileSize = 1, FileModified = 1, Kind = MediaKind.Photo, ContentId = contentId };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), folder, @"D:\OneDrive\Pictures");
        _media.Upsert(db, tx, item, folder);
        tx.Commit();
        return item.Id;
    }

    private OneDriveFaceSync Sync(Server server) =>
        new(new OneDrivePeopleClient(new Token(), server) { PageDelay = TimeSpan.Zero }, _database, _media,
            new AppSettings { OneDriveRoot = @"D:\OneDrive" });

    [Fact]
    public void Photos_parse_into_long_side_fractions_and_a_relative_folder()
    {
        using var json = JsonDocument.Parse(Photo("D!1", "Pictures/Camera%20Roll/2026", "a.jpg", "e1",
            Face("f0", "p1", 1000, 750, 400, 300), """{"id":"f1","boundingBoxLeft":1,"boundingBoxTop":1,"boundingBoxWidth":1,"boundingBoxHeight":1}"""));

        var photo = OneDrivePeopleClient.ParsePhoto(json.RootElement)!;

        Assert.Equal("Pictures/Camera Roll/2026", photo.FolderPath);
        Assert.False(photo.IsLive);
        var face = Assert.Single(photo.Faces); // a face without a person is skipped
        Assert.Equal("p1", face.PersonId);
        Assert.Equal(new FaceBox(0.25, 0.1875, 0.1, 0.075), face.Box); // 4000 × 3000: all over 4000
        Assert.Equal((1000.0, 750.0, 400.0, 300.0), face.Box.In(4000, 3000));
        // An iPhone HEIC OneDrive calls 4032 × 3024 that displays cropped to 4032 × 2268: same long side, same pixels.
        Assert.Equal(1000 * 4032 / 4000.0, face.Box.In(4032, 2268).X, 6);
    }

    [Fact]
    public async Task People_are_paged_and_the_token_never_leaves_the_api_host()
    {
        var server = new Server(url => url switch
        {
            _ when url.Contains("skiptoken=2") => Page(null, Person("b", null, 3)),
            _ when url.Contains("recognizedEntities") => Page($"{Api}/drives/D/recognizedEntities?$skiptoken=2", Person("a", "Emily", 9)),
            _ => null,
        });
        var client = new OneDrivePeopleClient(new Token(), server);

        var people = new List<OneDrivePerson>();
        await foreach (var p in client.GetPeopleAsync("D", TestContext.Current.CancellationToken)) people.Add(p);

        Assert.Equal([("a", "Emily", 9), ("b", (string?)null, 3)], people.Select(p => (p.Id, p.Name, p.PhotoCount)));
        Assert.All(server.Requests, r => Assert.Equal("my.microsoftpersonalcontent.com", r.RequestUri!.Host));

        var evil = new Server(url => Page("https://evil.example/steal", Person("a", "Emily")));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new OneDrivePeopleClient(new Token(), evil).GetPeopleAsync("D", TestContext.Current.CancellationToken)) { }
        });
        Assert.Single(evil.Requests);
    }

    [Fact]
    public async Task Full_scan_stores_names_boxes_and_item_ids_and_combines_same_names()
    {
        var solo = AddPhoto(@"D:\OneDrive\Pictures\2026\solo.jpg");
        var group = AddPhoto(@"D:\OneDrive\Pictures\2026\group.jpg");
        var stale = AddPhoto(@"D:\OneDrive\Pictures\old\stale.jpg");
        var young = AddPhoto(@"D:\OneDrive\Pictures\2026\young.jpg");
        using (var db = _database.Open())
        {
            // Left by the SharePoint-list import: a pre-merge cluster id, no box.
            var old = db.ExecuteScalar<long>("INSERT INTO People (OneDrivePersonId) VALUES ('cluster') RETURNING Id");
            db.Execute("INSERT INTO MediaFaces (MediaId, PersonId) VALUES (@stale, @old), (@solo, @old)", new { stale, solo, old });
        }
        var server = new Server(url => url switch
        {
            _ when url.EndsWith("/drive?select=id") => """{"id":"D"}""",
            _ when url.Contains("recognizedEntities/oldemily") => Person("oldemily", "Emily", 5),
            _ when url.Contains("recognizedEntities") => Page(null, Person("emily", "Emily", 2), Person("emily2", "emily "), Person("kid", null), Person("shy", null, hidden: true)),
            _ when url.Contains("items/root/items") => Page(null,
                Photo("D!solo", "Pictures/2026", "solo.jpg", "e1", Face("s0", "emily", 2000, 300, 800, 1000)),
                Photo("D!young", "Pictures/2026", "young.jpg", "e4", Face("y0", "oldemily", 100, 100, 500, 500)),
                Photo("D!group", "Pictures/2026", "group.jpg", "e2", Face("g0", "kid", 0, 0, 100, 100), Face("g1", "emily2", 400, 300, 200, 200), Face("g2", "shy", 3000, 0, 100, 100)),
                Photo("D!elsewhere", "Pictures/2020", "notlocal.jpg", "e3", Face("x0", "emily", 0, 0, 10, 10))),
            _ => null,
        });

        var result = await Sync(server).RunAsync(fullScan: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal((4, 3, 2), (result.PhotosRead, result.PhotosMatched, result.Combined));
        var everyone = _people.GetPeople(includeHidden: true);
        var emily = everyone.Single(p => p.Name == "Emily");
        Assert.Equal(3, emily.Count); // "emily " and the unlisted group still named Emily are the same person
        Assert.DoesNotContain(everyone, p => p.Name == "emily ");
        Assert.True(everyone.Single(p => p.Id != emily.Id && p.Hidden).Hidden);
        var groupFaces = _people.GetFacesIn(group);
        Assert.Equal(3, groupFaces.Count);
        Assert.Equal(["Emily"], groupFaces.Select(f => f.Name).OfType<string>());

        var soloFace = _people.GetFacesIn(solo).Single();
        Assert.Equal(new FaceBox(0.5, 0.075, 0.2, 0.25), soloFace.Box);
        using (var db = _database.Open())
        {
            Assert.Equal("D!solo", db.ExecuteScalar<string>("SELECT OneDriveItemId FROM Media WHERE Id = @solo", new { solo }));
            Assert.Equal(0, db.ExecuteScalar<long>("SELECT count(*) FROM MediaFaces WHERE MediaId = @stale", new { stale })); // unconfirmed link dropped
            Assert.Equal(0, db.ExecuteScalar<long>("SELECT count(*) FROM People WHERE OneDrivePersonId = 'cluster'"));
        }
        Assert.Equal([solo, group, young], _media.Query(new MediaFilter { Text = "emily" }).Select(m => m.Id).Order());
        using (var db = _database.Open())
            Assert.Equal(emily.Id, db.ExecuteScalar<long>("SELECT PersonId FROM PersonAliases WHERE OneDrivePersonId = 'oldemily'"));
    }

    [Fact]
    public async Task Cloud_Live_Photos_are_the_ones_OneDrive_says_once_it_has()
    {
        var live = AddPhoto(@"D:\OneDrive\Pictures\2026\live.heic", "A");
        var plain = AddPhoto(@"D:\OneDrive\Pictures\2026\plain.heic", "B");   // content id, but no video in OneDrive
        var unlisted = AddPhoto(@"D:\OneDrive\Pictures\2026\new.heic", "C"); // OneDrive hasn't said yet
        _media.RecomputeMotion();
        Assert.All([live, plain, unlisted], id => Assert.Equal(MotionSource.Cloud, _media.Get(id)!.Motion));

        var server = new Server(url => url switch
        {
            _ when url.EndsWith("/drive?select=id") => """{"id":"D"}""",
            _ when url.Contains("recognizedEntities") => Page(null),
            _ when url.Contains("items/root/items") => Page(null,
                Photo("D!live", "Pictures/2026", "live.heic", "e1").Replace("\"photo\":{", "\"photo\":{\"livePhoto\":{},"),
                Photo("D!plain", "Pictures/2026", "plain.heic", "e2")),
            _ => null,
        });
        await Sync(server).RunAsync(fullScan: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(MotionSource.Cloud, _media.Get(live)!.Motion);
        Assert.Equal(MotionSource.None, _media.Get(plain)!.Motion);
        Assert.Equal(MotionSource.Cloud, _media.Get(unlisted)!.Motion);
    }

    [Fact]
    public async Task Names_given_here_win_and_people_merged_in_OneDrive_are_followed()
    {
        var a = AddPhoto(@"D:\OneDrive\Pictures\a.jpg");
        var b = AddPhoto(@"D:\OneDrive\Pictures\b.jpg");
        var merged = false;
        var server = new Server(url => url switch
        {
            _ when url.EndsWith("/drive?select=id") => """{"id":"D"}""",
            _ when url.Contains("recognizedEntities/small") => null,
            _ when url.Contains("recognizedEntities") => merged
                ? Page(null, Person("big", "Megan", 2))
                : Page(null, Person("big", "Megan"), Person("small", null)),
            _ when url.Contains("items/root/items") => Page(null,
                Photo("D!a", "Pictures", "a.jpg", "ea", Face("fa", "big", 0, 0, 100, 100)),
                Photo("D!b", "Pictures", "b.jpg", "eb", Face("fb", merged ? "big" : "small", 0, 0, 100, 100))),
            // After the merge, only the single-item lookup says where face fb went (the eTag didn't change).
            _ when url.Contains("items/D!b") => Photo("D!b", "Pictures", "b.jpg", "eb", Face("fb", "big", 0, 0, 100, 100)),
            _ => null,
        });

        await Sync(server).RunAsync(fullScan: true, ct: TestContext.Current.CancellationToken);
        var megan = _people.GetPeople().Single(p => p.Name == "Megan");
        _people.Rename(megan.Id, "Meg");
        Assert.Equal(2, _people.GetPeople().Count);

        merged = true;
        var result = await Sync(server).RunAsync(fullScan: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Remapped);
        var only = Assert.Single(_people.GetPeople());
        Assert.Equal(("Meg", 2L), (only.Name, only.Count)); // local name kept, both photos
        Assert.Equal([a, b], _media.Query(new MediaFilter { PersonId = only.Id }).Select(m => m.Id).Order());
    }
}
