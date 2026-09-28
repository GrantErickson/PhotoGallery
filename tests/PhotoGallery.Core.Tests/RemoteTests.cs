using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Ocr;
using PhotoGallery.Core.Transcripts;
using PhotoGallery.Remote;

namespace PhotoGallery.Core.Tests;

public class RemoteSecretTests
{
    [Fact]
    public void Verifies_the_passphrase_it_was_made_from()
    {
        var secret = RemoteSecret.Create("correct horse battery", iterations: 1000);
        Assert.True(secret.Verify("correct horse battery"));
        Assert.True(secret.Verify("  correct horse battery "));
        Assert.False(secret.Verify("Correct horse battery"));
        Assert.False(secret.Verify(""));
        Assert.DoesNotContain("horse", secret.Key + secret.Salt);
    }

    [Fact]
    public void Accented_letters_match_however_they_were_typed()
    {
        var secret = RemoteSecret.Create("café au lait", iterations: 1000);
        Assert.True(secret.Verify("café au lait"));
    }

    [Fact]
    public void A_damaged_secret_verifies_nothing() =>
        Assert.False(new RemoteSecret("not base64!", 1000, "also not").Verify("anything"));
}

public class LocalNetworkTests
{
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.16.4.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1c2b:3aff:fe4d:5e6f", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.4.4", false)]
    public void Only_local_addresses_count(string address, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(address)));
}

public class LoginThrottleTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Wrong_guesses_lock_an_address_out_for_longer_each_time()
    {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(clock);
        var guesser = IPAddress.Parse("192.168.1.66");
        var other = IPAddress.Parse("192.168.1.67");

        for (var i = 1; i < LoginThrottle.FreeTries; i++) throttle.Failed(guesser);
        Assert.Null(throttle.RetryAfter(guesser));

        throttle.Failed(guesser);
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.RetryAfter(guesser));
        Assert.Null(throttle.RetryAfter(other));

        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Null(throttle.RetryAfter(guesser));
        throttle.Failed(guesser);
        Assert.Equal(TimeSpan.FromMinutes(2), throttle.RetryAfter(guesser));

        // The same address written as IPv4-in-IPv6 is the same guesser.
        Assert.NotNull(throttle.RetryAfter(IPAddress.Parse("::ffff:192.168.1.66")));

        clock.Now += TimeSpan.FromMinutes(3);
        throttle.Succeeded(guesser);
        throttle.Failed(guesser);
        Assert.Null(throttle.RetryAfter(guesser));
    }
}

public sealed class RemoteServerTests : IAsyncLifetime
{
    private const string Passphrase = "blue kettle morning";

    private readonly string _dir = Directory.CreateTempSubdirectory("pg-remote-").FullName;
    private readonly FakeLibrary _library = new();
    private RemoteServer _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _library.Thumbnail = Path.Combine(_dir, "thumb.jpg");
        File.WriteAllBytes(_library.Thumbnail, [0xFF, 0xD8, 0xFF, 0xD9]);
        var video = Path.Combine(_dir, "clip.mov");
        File.WriteAllBytes(video, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        _library.Items.Add(new MediaItem { Id = 1, FileName = "IMG_0001.HEIC", Path = Path.Combine(_dir, "IMG_0001.HEIC"), Kind = MediaKind.Photo, DateTaken = 1_700_000_000, Motion = MotionSource.Cloud, Rating = 3 });
        _library.Items.Add(new MediaItem { Id = 2, FileName = "clip.mov", Path = video, Kind = MediaKind.Video, DateTaken = 1_690_000_000, DurationMs = 12_400 });

        _server = await RemoteServer.StartAsync(_library, new RemoteServerOptions
        {
            Port = 0,
            LoopbackOnly = true,
            Secret = RemoteSecret.Create(Passphrase, iterations: 1000),
            Certificate = HostCertificate.CreateTemporary(),
        });
        // Trusts exactly the server's certificate, as Photo Gallery does once the security code is confirmed.
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && HostCertificate.Fingerprint(certificate) == _server.Fingerprint,
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequestMessage Post(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(RemoteServer.ScriptHeader, "1");
        return request;
    }

    private async Task<string> SignInAsync()
    {
        using var response = await _client.SendAsync(Post("api/login", new { passphrase = Passphrase }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var token = body.GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    [Fact]
    public async Task Serves_the_web_app_and_asks_for_the_passphrase_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await _client.GetStringAsync("", ct);
        Assert.Contains("app.js", page);

        using var home = await _client.GetAsync("", ct);
        Assert.Contains("frame-ancestors 'none'", home.Headers.GetValues("Content-Security-Policy").Single());

        var hello = await _client.GetFromJsonAsync<JsonElement>("api/hello", ct);
        Assert.Equal("TEST-PC", hello.GetProperty("name").GetString());
        Assert.False(hello.GetProperty("signedIn").GetBoolean());

        using var items = await _client.GetAsync("api/items", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, items.StatusCode);
        using var thumb = await _client.GetAsync("api/media/1/thumb", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, thumb.StatusCode);
    }

    [Fact]
    public async Task Answers_only_to_its_own_names()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/hello");
        request.Headers.Host = "photos.example.com";
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var machine = Environment.MachineName;
        Assert.True(RemoteServer.IsOwnName(machine.ToLowerInvariant()));
        Assert.True(RemoteServer.IsOwnName(machine + ".local"));
        Assert.True(RemoteServer.IsOwnName("192.168.1.20"));
        Assert.True(RemoteServer.IsOwnName("[fe80::1]"));
        Assert.False(RemoteServer.IsOwnName(machine + "x.example.com"));
        Assert.False(RemoteServer.IsOwnName(""));
    }

    [Fact]
    public async Task Signs_in_with_the_right_passphrase_only()
    {
        var ct = TestContext.Current.CancellationToken;
        // Without the web app's header (a form posted from another site) it's refused outright.
        using var forged = await _client.PostAsync("api/login", JsonContent.Create(new { passphrase = Passphrase }), ct);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        using var wrong = await _client.SendAsync(Post("api/login", new { passphrase = "red kettle morning" }), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        using var right = await _client.SendAsync(Post("api/login", new { passphrase = Passphrase }), ct);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var cookie = right.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(RemoteServer.CookieName + "=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        // The cookie works as well as the token.
        var session = cookie.Split(';')[0];
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/hello");
        request.Headers.Add("Cookie", session);
        using var hello = await _client.SendAsync(request, ct);
        Assert.True((await hello.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task Too_many_wrong_passphrases_have_to_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < LoginThrottle.FreeTries; i++)
        {
            using var wrong = await _client.SendAsync(Post("api/login", new { passphrase = "guess " + i }), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        // Now even the right one waits.
        using var locked = await _client.SendAsync(Post("api/login", new { passphrase = Passphrase }), ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
    }

    [Fact]
    public async Task Lists_items_compactly_and_searches()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();

        var timeline = await _client.GetFromJsonAsync<JsonElement>("api/items", ct);
        Assert.Equal([1L, 2L], timeline.GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()));
        var flags = timeline.GetProperty("flags").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(2 + (3 << 2), flags[0]); // Live Photo, 3 stars
        Assert.Equal(1 + (12 << 5), flags[1]); // video, 12 s
        Assert.False(_library.LastFilter!.IncludeScreenshots);

        var found = await _client.GetFromJsonAsync<JsonElement>("api/items?section=search&q=kettle&exact=1&sort=oldest&people=5,6", ct);
        Assert.Equal(("kettle", true), _library.LastSearch);
        Assert.Equal(MediaOrder.Oldest, _library.LastFilter!.Order);
        Assert.Equal([5L, 6L], _library.LastFilter.People!);
        Assert.Equal([2L, 1L], found.GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()));
        Assert.False(found.GetProperty("pictures").GetBoolean());

        using var blank = await _client.GetAsync("api/items?section=search&q=", ct);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task Serves_details_files_and_video_ranges()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();

        var details = await _client.GetFromJsonAsync<JsonElement>("api/media/1", ct);
        Assert.Equal("IMG_0001.HEIC", details.GetProperty("name").GetString());
        Assert.True(details.GetProperty("live").GetBoolean());
        Assert.Equal("Spokane · Manito Park", details.GetProperty("place").GetString());
        Assert.Equal("Grant", details.GetProperty("people")[0].GetProperty("name").GetString());
        Assert.Equal("Happy birthday", details.GetProperty("transcript")[0].GetProperty("text").GetString());

        var thumb = await _client.GetByteArrayAsync("api/media/1/thumb", ct);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], thumb);

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/media/2/video");
        request.Headers.Range = new RangeHeaderValue(100, 109);
        using var part = await _client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(Enumerable.Range(100, 10).Select(i => (byte)i), await part.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("video/mp4", part.Content.Headers.ContentType!.MediaType);

        using var motion = await _client.GetAsync("api/media/1/motion", ct);
        Assert.Equal(HttpStatusCode.Conflict, motion.StatusCode);
        Assert.Contains("OneDrive", await motion.Content.ReadAsStringAsync(ct));

        using var missing = await _client.GetAsync("api/media/99/thumb", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var notVideo = await _client.GetAsync("api/media/1/video", ct);
        Assert.Equal(HttpStatusCode.NotFound, notVideo.StatusCode);
    }

    [Fact]
    public async Task Rates_photos_and_signs_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();

        using var rated = await _client.SendAsync(Post("api/media/2/rating", new { rating = 5 }), ct);
        Assert.Equal(HttpStatusCode.OK, rated.StatusCode);
        Assert.Equal((2L, 5), _library.LastRating);

        using var tooHigh = await _client.SendAsync(Post("api/media/2/rating", new { rating = 6 }), ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);

        using var outRequest = await _client.SendAsync(Post("api/logout", new { }), ct);
        Assert.Equal(HttpStatusCode.OK, outRequest.StatusCode);
        using var after = await _client.GetAsync("api/items", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Changes_wait_for_the_hosts_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        var hello = await _client.GetFromJsonAsync<JsonElement>("api/hello", ct);
        Assert.False(hello.GetProperty("changes").GetBoolean());

        using var refused = await _client.SendAsync(Post("api/media/delete", new { ids = new[] { 2 } }), ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("turned off", await refused.Content.ReadAsStringAsync(ct));
        Assert.Empty(_library.Changes);

        // Ratings and utility shots don't need it.
        using var rated = await _client.SendAsync(Post("api/media/rating", new { ids = new[] { 1, 2 }, rating = 2 }), ct);
        Assert.Equal(HttpStatusCode.OK, rated.StatusCode);
        Assert.Contains("rate 1,2 2", _library.Changes);
        using var marked = await _client.SendAsync(Post("api/media/utility", new { ids = new[] { 1, 2 }, utility = true }), ct);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        using var unmarked = await _client.SendAsync(Post("api/media/utility", new { ids = new[] { 2 }, utility = (bool?)null }), ct);
        Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);
        Assert.Equal(["rate 1,2 2", "utility 1,2 True", "utility 2 auto"], _library.Changes);
        using var none = await _client.SendAsync(Post("api/media/utility", new { utility = true }), ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        var details = await _client.GetFromJsonAsync<JsonElement>("api/media/2", ct);
        Assert.False(details.GetProperty("utility").GetBoolean());
        Assert.False(details.GetProperty("utilityChosen").GetBoolean());

        _library.AllowChanges = true;
        using var deleted = await _client.SendAsync(Post("api/media/delete", new { ids = new[] { 2 } }), ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(2, (await deleted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("deleted")[0].GetInt64());
        Assert.Contains("delete 2", _library.Changes);
    }

    [Fact]
    public async Task Tags_albums_and_people_change_when_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        _library.AllowChanges = true;
        async Task<HttpStatusCode> SendAsync(string path, object body)
        {
            using var response = await _client.SendAsync(Post(path, body), ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/tags/add", new { ids = new[] { 1 }, name = " holiday " }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/tags/remove", new { ids = new[] { 1 }, tagId = 9 }));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync("api/tags/add", new { ids = new[] { 1 }, name = "" }));
        using (var created = await _client.SendAsync(Post("api/albums", new { name = "Trip" }), ct))
            Assert.Equal(40, (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/albums/40/add", new { ids = new[] { 1, 2 } }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/albums/40/remove", new { ids = new[] { 2 } }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/albums/40/rename", new { name = "Road trip" }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/albums/40/delete", new { }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/people/7/rename", new { name = "Pat" }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/people/7/hide", new { hidden = true }));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync("api/people/7/merge", new { into = 7 }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync("api/people/7/merge", new { into = 8 }));

        Assert.Equal(["tag 1 holiday", "untag 1 9", "album Trip", "album 40 add 1,2", "album 40 remove 2", "album 40 name Road trip",
            "album 40 delete", "person 7 name Pat", "person 7 hidden True", "person 7 into 8"], _library.Changes);
    }

    [Fact]
    public async Task Sections_and_filters_become_the_right_query()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        async Task<MediaFilter> FilterAsync(string query)
        {
            using var response = await _client.GetAsync("api/items?" + query, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return _library.LastFilter!;
        }

        var folder = await FilterAsync("section=folder&id=5&sub=0");
        Assert.Equal((5L, false), (folder.FolderId, folder.IncludeSubfolders));
        Assert.Equal(9, (await FilterAsync("section=tag&id=9")).TagId);
        var blurry = await FilterAsync("section=blurry&live=1");
        Assert.Equal((MediaOrder.Blurriest, true), (blurry.Order, blurry.MotionOnly));
        Assert.NotNull(blurry.SharpnessBelow);
        Assert.Equal((47.5, -117.6, 47.8, -117.2), (await FilterAsync("section=area&s=47.5&w=-117.6&n=47.8&e=-117.2")).Bounds);
        var filtered = await FilterAsync("rating=3&screenshots=only&kind=videos");
        Assert.Equal((3, true, true, KindFilter.Videos), (filtered.MinRating, filtered.ScreenshotsOnly, filtered.IncludeScreenshots, filtered.Kinds));
        Assert.Equal(4, (await FilterAsync("section=favorites&rating=2")).MinRating);

        using var unknown = await _client.GetAsync("api/items?section=nowhere", ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var list = await _client.PostAsync("api/items/list", JsonContent.Create(new { ids = new[] { 2, 1 }, sort = "listed" }), ct);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode); // a POST needs the web app's header
        using var listed = await _client.SendAsync(Post("api/items/list", new { ids = new[] { 2, 1 }, sort = "listed" }), ct);
        Assert.Equal([2L, 1L], (await listed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal(MediaOrder.Listed, _library.LastFilter!.Order);
    }

    [Fact]
    public async Task Serves_the_map_folders_tags_suggestions_and_rich_details()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        var map = await _client.GetFromJsonAsync<JsonElement>("api/map", ct);
        Assert.Equal(47.65881, map.GetProperty("lat")[0].GetDouble());
        Assert.Equal("Pictures", (await _client.GetFromJsonAsync<JsonElement>("api/folders", ct))[0].GetProperty("name").GetString());
        var tags = await _client.GetFromJsonAsync<JsonElement>("api/tags", ct);
        Assert.Equal(1, tags.GetArrayLength()); // empty tags are left out
        Assert.Equal("person", (await _client.GetFromJsonAsync<JsonElement>("api/suggest?q=gra", ct))[0].GetProperty("kind").GetString());

        var details = await _client.GetFromJsonAsync<JsonElement>("api/media/1", ct);
        Assert.Equal(0.25, details.GetProperty("faces")[0].GetProperty("x").GetDouble());
        Assert.Equal("EXIT", details.GetProperty("textLines")[0][0].GetProperty("t").GetString());
        Assert.True(details.GetProperty("tags")[0].GetProperty("yours").GetBoolean());
        Assert.Equal(3, details.GetProperty("albums")[0].GetInt64());

        var similar = await _client.GetFromJsonAsync<JsonElement>("api/media/1/similar", ct);
        Assert.Equal([1L, 2L], similar.GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()));

        // The web app's own files, and nothing else.
        using var css = await _client.GetAsync("app.css", ct);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        using var missing = await _client.GetAsync("secrets.txt", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var sneaky = await _client.GetAsync("..%2F..%2Fsettings.json", ct);
        Assert.Equal(HttpStatusCode.NotFound, sneaky.StatusCode);
    }

    [Fact]
    public async Task Edits_preview_freely_but_save_only_when_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        var edits = await _client.GetFromJsonAsync<JsonElement>("api/media/1/edits", ct);
        Assert.Equal(90, edits.GetProperty("ops").GetProperty("Rotation").GetInt32());

        // Out-of-range values are brought back into range, and choosing the crop shows the whole picture.
        var ops = new { Rotation = 450, Exposure = 7.0, Crop = new { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5 } };
        using (var preview = await _client.SendAsync(Post("api/media/1/preview", new { ops, crop = false }), ct))
        {
            Assert.Equal("image/jpeg", preview.Content.Headers.ContentType!.MediaType);
            Assert.Equal((90, 2.0, null), (_library.LastRendered!.Rotation, _library.LastRendered.Exposure, _library.LastRendered.Crop));
        }
        using (var preview = await _client.SendAsync(Post("api/media/1/preview", new { ops }), ct))
            Assert.NotNull(_library.LastRendered!.Crop);
        using var video = await _client.SendAsync(Post("api/media/2/preview", new { ops }), ct);
        Assert.Equal(HttpStatusCode.BadRequest, video.StatusCode);

        using (var auto = await _client.SendAsync(Post("api/media/1/auto", new { ops = new { Rotation = 180 } }), ct))
        {
            var result = (await auto.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("ops");
            Assert.Equal((180, 0.5), (result.GetProperty("Rotation").GetInt32(), result.GetProperty("Exposure").GetDouble()));
        }

        using var refused = await _client.SendAsync(Post("api/media/1/edit", new { ops, mode = "copy" }), ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        _library.AllowChanges = true;
        using var bad = await _client.SendAsync(Post("api/media/1/edit", new { ops, mode = "sideways" }), ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var saved = await _client.SendAsync(Post("api/media/1/edit", new { ops, mode = "copy" }), ct);
        Assert.Equal("Saved.", (await saved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("message").GetString());
        Assert.Contains("edit 1 Copy 90 2", _library.Changes);
    }

    [Fact]
    public async Task Frames_and_video_saves()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        using (var sharpest = await _client.SendAsync(Post("api/media/2/sharpest", new { }), ct))
            Assert.Equal(1.25, (await sharpest.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("seconds").GetDouble());
        using (var none = await _client.SendAsync(Post("api/media/1/sharpest", new { }), ct))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _library.AllowChanges = true;
        using (var frame = await _client.SendAsync(Post("api/media/2/frame", new { seconds = 3.5 }), ct))
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        using (var backwards = await _client.SendAsync(Post("api/media/2/export", new { start = 5.0, end = 2.0 }), ct))
            Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        using var started = await _client.SendAsync(Post("api/media/2/export", new { rotation = -90, start = 1.0, end = 4.0, mute = true }), ct);
        var job = (await started.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("job").GetString();
        var status = await _client.GetFromJsonAsync<JsonElement>($"api/jobs/{job}", ct);
        Assert.True(status.GetProperty("done").GetBoolean());
        Assert.Equal("Saved clip.mp4.", status.GetProperty("message").GetString());
        Assert.Equal(["frame 2 3.5", "export 2 270 1-4 True"], _library.Changes);
    }

    [Fact]
    public async Task Finds_places_for_the_map()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        var here = (await _client.GetFromJsonAsync<JsonElement>("api/places?q=spokane", ct))[0];
        Assert.Equal(("spokane", "here", 47.5), (here.GetProperty("name").GetString(), here.GetProperty("detail").GetString(), here.GetProperty("s").GetDouble()));
        Assert.Equal("here · town", here.GetProperty("caption").GetString());
        var online = (await _client.GetFromJsonAsync<JsonElement>("api/places?q=spokane&online=1", ct))[0];
        Assert.Equal("from OpenStreetMap", online.GetProperty("detail").GetString());

        using var failed = await _client.GetAsync("api/places?q=nowhere&online=1", ct);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("didn't answer", (await failed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString());
        using var tooShort = await _client.GetAsync("api/places?q=s", ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
    }

    [Fact]
    public async Task Finds_duplicates_in_the_background()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignInAsync();
        var before = await _client.GetFromJsonAsync<JsonElement>("api/duplicates", ct);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("groups").ValueKind);
        using (await _client.SendAsync(Post("api/duplicates/scan", new { }), ct)) { }
        JsonElement after = default;
        for (var i = 0; i < 50; i++)
        {
            after = await _client.GetFromJsonAsync<JsonElement>("api/duplicates", ct);
            if (!after.GetProperty("running").GetBoolean()) break;
            await Task.Delay(50, ct);
        }
        var group = after.GetProperty("groups")[0];
        Assert.Equal("exact", group.GetProperty("kind").GetString());
        Assert.Equal(2, group.GetProperty("members").GetArrayLength());
    }

    private sealed class FakeLibrary : IRemoteLibrary
    {
        public FakeLibrary() => Duplicates = new DuplicateScan((progress, ct) =>
        {
            progress.Report(new DuplicateScanProgress("Comparing", 1, 2));
            return Task.FromResult<IReadOnlyList<DuplicateGroup>>(
            [
                new(DuplicateKind.Exact,
                [
                    new DuplicateMember { Id = 1, FileName = "a.jpg", FileSize = 10, IsSuggestedKeep = true },
                    new DuplicateMember { Id = 2, FileName = "a (1).jpg", FileSize = 10 },
                ]),
            ]);
        });

        public List<MediaItem> Items { get; } = [];
        public string Thumbnail { get; set; } = "";
        public MediaFilter? LastFilter { get; private set; }
        public (string Text, bool Exact)? LastSearch { get; private set; }
        public (long Id, int Rating)? LastRating { get; private set; }
        public List<string> Changes { get; } = [];

        public string Name => "TEST-PC";
        public bool AllowChanges { get; set; }
        public DuplicateScan Duplicates { get; }

        public List<MediaSummary> Query(MediaFilter filter)
        {
            LastFilter = filter;
            var items = filter.Ids is { } ids ? ids.Select(id => Items.First(i => i.Id == id)) : Items;
            return items.Select(i => new MediaSummary { Id = i.Id, Kind = i.Kind, DateTaken = i.DateTaken, Motion = i.Motion, Rating = i.Rating, DurationMs = i.DurationMs }).ToList();
        }

        public Task<(List<long> Ids, bool Pictures)> SearchAsync(string text, bool exact, CancellationToken ct)
        {
            LastSearch = (text, exact);
            return Task.FromResult<(List<long>, bool)>(([2, 1], !exact));
        }

        public List<(string Text, string Kind)> Suggest(string text) => [("Grant", "person")];

        public MediaItem? Get(long id) => Items.FirstOrDefault(i => i.Id == id);

        public Task<MediaDetails> GetDetailsAsync(MediaItem item, CancellationToken ct) => Task.FromResult(new MediaDetails(
            "Spokane · Manito Park", [(7, "Grant")], "EXIT", [new TranscriptParagraph(1.5, 3, "Happy birthday", null)],
            Faces: [new FaceInPhoto(7, "Grant", 0.25, 0.1, 0.2, 0.2)],
            TextLines: [new OcrLine([new OcrWord("EXIT", 0.5, 0.5, 0.1, 0.05)])],
            Tags: [new TagOnPhoto(9, "holiday", true)],
            Albums: [3]));

        public List<PersonRow> GetPeople(bool includeHidden) => [new PersonRow { Id = 7, Name = "Grant", Count = 12 }];

        public List<AlbumRow> GetAlbums() => [];

        public List<TagRow> GetTags() => [new TagRow { Id = 9, Name = "holiday", Count = 3 }, new TagRow { Id = 10, Name = "unused", Count = 0 }];

        public List<FolderRow> GetFolders() => [new FolderRow { Id = 5, Name = "Pictures", Path = @"C:\Pictures" }];

        public List<(long Id, double Latitude, double Longitude)> GetGeoPoints() => [(1, 47.658812, -117.4260)];

        public Task<List<PhotoGallery.Core.Places.PlaceHit>> SearchPlacesAsync(string query, bool online, CancellationToken ct) =>
            online && query == "nowhere"
                ? throw new IOException("OpenStreetMap's place search didn't answer.")
                : Task.FromResult(new List<PhotoGallery.Core.Places.PlaceHit> { new(query, online ? "from OpenStreetMap" : "here", "town", 47.6, -117.4, 47.5, -117.5, 47.7, -117.3) });

        public Task<List<long>> FindSimilarAsync(long id, CancellationToken ct) => Task.FromResult(new List<long> { 2 });

        public Task<string?> GetThumbnailAsync(MediaItem item, CancellationToken ct) => Task.FromResult<string?>(Thumbnail);

        public Task<(string? Path, bool Final)> GetFaceAsync(long personId, CancellationToken ct) => Task.FromResult<(string?, bool)>((null, true));

        public Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task<(MotionResult Result, string? Path)> GetMotionAsync(MediaItem item, CancellationToken ct) =>
            Task.FromResult<(MotionResult, string?)>((MotionResult.NeedsSignIn, null));

        public void SetRating(IReadOnlyCollection<long> ids, int rating)
        {
            if (ids.Count == 1) LastRating = (ids.First(), rating);
            Changes.Add($"rate {string.Join(",", ids)} {rating}");
        }

        public void SetUtility(IReadOnlyCollection<long> ids, bool? utility) =>
            Changes.Add($"utility {string.Join(",", ids)} {utility?.ToString() ?? "auto"}");

        public Task<(List<long> Deleted, List<string> Failed)> DeleteAsync(IReadOnlyCollection<long> ids)
        {
            Changes.Add($"delete {string.Join(",", ids)}");
            return Task.FromResult((ids.ToList(), new List<string>()));
        }

        public void AddTag(IReadOnlyCollection<long> ids, string name) => Changes.Add($"tag {string.Join(",", ids)} {name}");

        public void RemoveTag(IReadOnlyCollection<long> ids, long tagId) => Changes.Add($"untag {string.Join(",", ids)} {tagId}");

        public long CreateAlbum(string name)
        {
            Changes.Add($"album {name}");
            return 40;
        }

        public void RenameAlbum(long albumId, string name) => Changes.Add($"album {albumId} name {name}");

        public void DeleteAlbum(long albumId) => Changes.Add($"album {albumId} delete");

        public void AddToAlbum(long albumId, IReadOnlyCollection<long> ids) => Changes.Add($"album {albumId} add {string.Join(",", ids)}");

        public void RemoveFromAlbum(long albumId, IReadOnlyCollection<long> ids) => Changes.Add($"album {albumId} remove {string.Join(",", ids)}");

        public void RenamePerson(long personId, string? name) => Changes.Add($"person {personId} name {name}");

        public void HidePerson(long personId, bool hidden) => Changes.Add($"person {personId} hidden {hidden}");

        public void MergePeople(long sourceId, long targetId) => Changes.Add($"person {sourceId} into {targetId}");

        public EditOperations GetEdits(MediaItem item) => new() { Rotation = 90 };

        public bool CanOverwrite(MediaItem item) => false;

        public EditOperations? LastRendered { get; private set; }

        public Task<byte[]?> RenderEditAsync(MediaItem item, EditOperations ops, int maxSize, CancellationToken ct)
        {
            LastRendered = ops;
            return Task.FromResult<byte[]?>([0xFF, 0xD8, 0xFF, 0xD9]);
        }

        public Task<EditOperations> AutoAdjustAsync(MediaItem item, EditOperations ops, CancellationToken ct) => Task.FromResult(ops with { Exposure = 0.5 });

        public Task<string> SaveEditAsync(MediaItem item, EditOperations ops, EditSave mode)
        {
            Changes.Add($"edit {item.Id} {mode} {ops.Rotation} {ops.Exposure}");
            return Task.FromResult("Saved.");
        }

        public Task<TimeSpan?> FindSharpestAsync(MediaItem item, CancellationToken ct) =>
            Task.FromResult<TimeSpan?>(item.Kind == MediaKind.Video ? TimeSpan.FromSeconds(1.25) : null);

        public Task<string> SaveFrameAsync(MediaItem item, TimeSpan position)
        {
            Changes.Add($"frame {item.Id} {position.TotalSeconds}");
            return Task.FromResult("Saved a frame.");
        }

        public VideoJob StartVideoExport(MediaItem item, VideoEdits edits)
        {
            Changes.Add($"export {item.Id} {edits.Rotation} {edits.TrimStart.TotalSeconds}-{edits.TrimEnd?.TotalSeconds} {edits.Mute}");
            var job = new VideoJob { Progress = 1 };
            job.Finish("Saved clip.mp4.");
            return job;
        }
    }
}

public class RemoteAddressTests
{
    [Theory]
    [InlineData("GRANT-PC", "grant-pc", 47813)]
    [InlineData("  grant-pc:48000 ", "grant-pc", 48000)]
    [InlineData("192.168.1.20", "192.168.1.20", 47813)]
    [InlineData("https://192.168.1.20:47813/", "192.168.1.20", 47813)]
    [InlineData("https://grant-pc:443", "grant-pc", 443)]
    [InlineData("[fe80::1]:47813", "[fe80::1]", 47813)]
    public void Reads_what_people_type(string typed, string host, int port)
    {
        var address = RemoteAddress.Parse(typed);
        Assert.NotNull(address);
        Assert.Equal((host, port), (address.Host, address.Port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://grant-pc")]
    [InlineData("https://grant-pc/somewhere")]
    [InlineData("https://someone@grant-pc")]
    public void Refuses_what_isnt_a_computer(string typed) => Assert.Null(RemoteAddress.Parse(typed));

    [Fact]
    public void Knows_its_own_addresses()
    {
        var address = RemoteAddress.Parse("Grant-PC")!;
        Assert.Equal("grant-pc:47813", address.Key);
        Assert.True(address.Owns("https://grant-pc:47813/?embedded=1#/search?q=cake"));
        Assert.False(address.Owns("https://grant-pc:47814/"));
        Assert.False(address.Owns("http://grant-pc:47813/"));
        Assert.False(address.Owns("https://grant-pc.evil.example:47813/"));
        Assert.False(address.Owns(null));
    }
}
