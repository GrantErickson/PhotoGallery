using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
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

    private sealed class FakeLibrary : IRemoteLibrary
    {
        public List<MediaItem> Items { get; } = [];
        public string Thumbnail { get; set; } = "";
        public MediaFilter? LastFilter { get; private set; }
        public (string Text, bool Exact)? LastSearch { get; private set; }
        public (long Id, int Rating)? LastRating { get; private set; }

        public string Name => "TEST-PC";

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

        public MediaItem? Get(long id) => Items.FirstOrDefault(i => i.Id == id);

        public Task<MediaDetails> GetDetailsAsync(MediaItem item, CancellationToken ct) => Task.FromResult(new MediaDetails(
            "Spokane · Manito Park", [(7, "Grant")], null, [new TranscriptParagraph(1.5, 3, "Happy birthday", null)]));

        public List<PersonRow> GetPeople() => [new PersonRow { Id = 7, Name = "Grant", Count = 12 }];

        public List<AlbumRow> GetAlbums() => [];

        public void SetRating(long id, int rating) => LastRating = (id, rating);

        public Task<string?> GetThumbnailAsync(MediaItem item, CancellationToken ct) => Task.FromResult<string?>(Thumbnail);

        public Task<string?> GetFaceAsync(long personId, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task<(MotionResult Result, string? Path)> GetMotionAsync(MediaItem item, CancellationToken ct) =>
            Task.FromResult<(MotionResult, string?)>((MotionResult.NeedsSignIn, null));
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
