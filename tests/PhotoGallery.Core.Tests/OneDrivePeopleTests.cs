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
        _database.CloseConnections(); // only this test's database: other test classes run at the same time
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

    /// <summary>Records each request with its body, and answers with <paramref name="answer"/> (status, JSON).</summary>
    private sealed class Writes(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body,
                request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null));
            if (request.RequestUri!.AbsolutePath.EndsWith("/drive")) return Json(HttpStatusCode.OK, """{"id":"D"}""");
            var (status, json) = answer(request, body);
            var response = Json(status, json);
            if (status == HttpStatusCode.ServiceUnavailable) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Three OneDrive people ("big" named Megan, "small" and "other" unnamed), each in one photo.</summary>
    private async Task<(PersonRow Megan, PersonRow Small, PersonRow Other)> ThreePeopleAsync()
    {
        AddPhoto(@"D:\OneDrive\Pictures\a.jpg");
        AddPhoto(@"D:\OneDrive\Pictures\b.jpg");
        AddPhoto(@"D:\OneDrive\Pictures\c.jpg");
        var server = new Server(url => url switch
        {
            _ when url.EndsWith("/drive?select=id") => """{"id":"D"}""",
            _ when url.Contains("recognizedEntities") => Page(null, Person("big", "Megan"), Person("small", null), Person("other", null)),
            _ when url.Contains("items/root/items") => Page(null,
                Photo("D!a", "Pictures", "a.jpg", "ea", Face("fa", "big", 0, 0, 100, 100)),
                Photo("D!b", "Pictures", "b.jpg", "eb", Face("fb", "small", 0, 0, 100, 100)),
                Photo("D!c", "Pictures", "c.jpg", "ec", Face("fc", "other", 0, 0, 100, 100))),
            _ => null,
        });
        await Sync(server).RunAsync(fullScan: true, ct: TestContext.Current.CancellationToken);
        var people = _people.GetPeople();
        using var db = _database.Open();
        PersonRow ByGuid(string guid) => people.Single(p => p.Id == db.ExecuteScalar<long>("SELECT Id FROM People WHERE OneDrivePersonId = @guid", new { guid }));
        return (ByGuid("big"), ByGuid("small"), ByGuid("other"));
    }

    private OneDrivePeopleWriter Writer(HttpMessageHandler handler) => new(new OneDrivePeopleClient(new Token(), handler), _people);

    [Fact]
    public async Task Names_and_merges_made_here_go_to_OneDrive_in_order_as_its_People_page_sends_them()
    {
        var (megan, small, other) = await ThreePeopleAsync();
        var queued = 0;
        _people.ChangesQueued += () => queued++;

        _people.Rename(other.Id, "Alex Rivera");
        _people.Merge(small.Id, megan.Id);
        _people.Rename(megan.Id, "Megan"); // the same name again: nothing to send
        Assert.Equal(2, queued);
        Assert.Equal((2L, 0L, (string?)null), _people.GetChangeStatus());

        var server = new Writes((request, _) => (HttpStatusCode.OK, Person(request.RequestUri!.Segments[^1], "whoever")));
        var result = await Writer(server).SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new PeopleSendResult(2, 0, 0), result);
        var patches = server.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patches.Count);
        Assert.Equal($"{Api}/drives/D/recognizedEntities/other?expand=identity/user/thumbnails", patches[0].Url);
        Assert.Equal("""{"identity":{"user":{"displayName":"Alex Rivera"}}}""", patches[0].Body);
        Assert.Equal($"{Api}/drives/D/recognizedEntities/small?expand=identity/user/thumbnails", patches[1].Url);
        Assert.Equal("""{"id":"big","identity":{"user":{"displayName":"Megan"}}}""", patches[1].Body);
        Assert.All(server.Requests, r => Assert.Equal(Secret, r.Authorization));
        Assert.Equal((0L, 0L, (string?)null), _people.GetChangeStatus());

        // OneDrive has the name now, so a later rename there comes back here.
        using var db = _database.Open();
        Assert.True(db.ExecuteScalar<bool>("SELECT NameFromOneDrive FROM People WHERE Id = @Id", other));
    }

    [Fact]
    public async Task Changes_wait_while_OneDrive_is_busy_and_ones_it_refuses_are_set_aside()
    {
        var (megan, small, other) = await ThreePeopleAsync();
        _people.Rename(other.Id, "Sam");
        _people.Rename(other.Id, "Sam Lee"); // replaces the unsent "Sam"
        _people.Rename(small.Id, "Jordan");
        _people.Merge(small.Id, megan.Id);
        Assert.Equal(3, _people.GetChanges().Count);

        var busy = true;
        var server = new Writes((request, body) =>
            body.Contains("Sam Lee") ? (HttpStatusCode.BadRequest, """{"error":{"code":"invalidRequest","message":"Name not allowed"}}""")
            : busy ? (HttpStatusCode.ServiceUnavailable, "{}")
            : (HttpStatusCode.OK, Person("x", "x")));
        var writer = Writer(server);

        var first = await writer.SendAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new PeopleSendResult(0, 1, 2), first); // refused, then stopped at the busy one to keep the order
        var (waiting, refused, error) = _people.GetChangeStatus();
        Assert.Equal((2L, 1L), (waiting, refused));
        Assert.Contains("Name not allowed", error);

        busy = false;
        Assert.Equal(new PeopleSendResult(2, 0, 0), await writer.SendAsync(TestContext.Current.CancellationToken));
        Assert.Equal((0L, 1L), (_people.GetChangeStatus().Waiting, _people.GetChangeStatus().Refused));

        _people.RetryRefusedChanges();
        Assert.Equal(new PeopleSendResult(0, 1, 0), await writer.SendAsync(TestContext.Current.CancellationToken));
        _people.DiscardRefusedChanges();
        Assert.Equal((0L, 0L, (string?)null), _people.GetChangeStatus());
    }

    [Fact]
    public async Task A_merge_OneDrive_already_did_counts_as_sent_and_nothing_goes_without_a_connection()
    {
        var (megan, small, _) = await ThreePeopleAsync();
        _people.Merge(small.Id, megan.Id);

        var offline = new OneDrivePeopleWriter(new OneDrivePeopleClient(new NoToken(), new Writes((_, _) => (HttpStatusCode.OK, "{}"))), _people);
        await Assert.ThrowsAsync<OneDriveWebUnavailableException>(() => offline.SendAsync(TestContext.Current.CancellationToken));
        Assert.Single(_people.GetChanges());

        var gone = new Writes((_, _) => (HttpStatusCode.NotFound, """{"error":{"code":"itemNotFound","message":"gone"}}"""));
        Assert.Equal(new PeopleSendResult(1, 0, 0), await Writer(gone).SendAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_people.GetChanges());
    }

    private sealed class NoToken : IOneDriveWebToken
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Reviewing_leaves_out_named_hidden_and_not_tagged_people_and_naming_brings_them_back()
    {
        var (_, small, other) = await ThreePeopleAsync();
        Assert.Equal([small.Id, other.Id], _people.GetToReview().Select(p => p.Id).Order());

        _people.SetNotTagged(small.Id, true);
        _people.SetHidden(other.Id, true);
        Assert.Empty(_people.GetToReview());
        Assert.True(_people.Get(small.Id)!.NotTagged);
        Assert.Empty(_people.GetChanges()); // both stay on this PC

        _people.Rename(small.Id, "Jordan");
        var named = _people.Get(small.Id)!;
        Assert.False(named.NotTagged);
        _people.Rename(other.Id, null); // clearing a name doesn't go to OneDrive
        Assert.True(_people.Get(other.Id)!.Hidden);
        _people.SetHidden(other.Id, false);
        Assert.Equal([other.Id], _people.GetToReview().Select(p => p.Id));
        Assert.Single(_people.GetChanges());
    }

    [Fact]
    public void Face_samples_are_the_larger_faces_spread_over_the_years()
    {
        long personId;
        using (var db = _database.Open())
            personId = db.ExecuteScalar<long>("INSERT INTO People (OneDrivePersonId) VALUES ('p') RETURNING Id");
        const long Year = 31_536_000L;
        for (var i = 0; i < 40; i++)
        {
            var id = AddPhoto($@"D:\OneDrive\Pictures\{i}.jpg");
            using var db = _database.Open();
            db.Execute("UPDATE Media SET DateTaken = @taken WHERE Id = @id", new { id, taken = 1_000_000_000L + i * Year });
            // Every fourth photo has a big face, the first one filling the frame (a poor sample); the rest are tiny.
            var size = i == 0 ? 0.9 : i % 4 == 0 ? 0.2 : 0.02;
            db.Execute("INSERT INTO MediaFaces (MediaId, PersonId, BoxX, BoxY, BoxW, BoxH) VALUES (@id, @personId, 0.1, 0.1, @size, @size)", new { id, personId, size });
        }

        var samples = _people.GetFaceSamples(personId, 2);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal(0.2, s.Box.Width));
        Assert.True(samples[1].DateTaken - samples[0].DateTaken >= 12 * Year); // spread out, oldest first
        Assert.Equal((1_000_000_000L, 1_000_000_000L + 39 * Year), _people.GetSpan(personId));
    }
}
