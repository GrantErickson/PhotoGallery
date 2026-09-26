using System.Net;
using System.Text;
using PhotoGallery.Core.Cloud;
using static PhotoGallery.Core.Tests.Mp4Builder;

namespace PhotoGallery.Core.Tests;

public sealed class LiveVideoTests : IDisposable
{
    private const string ItemId = "19B4E9941270DFF7!s106a9743988646c5934372ceabb97896";
    private const string ContentId = "72F4B05A-4E47-4979-8B48-C00CAD139E1F";
    private const string Secret = "bearer EwAY-secret-token%2b%3d";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-live-{Guid.NewGuid():N}");

    public LiveVideoTests()
    {
        Directory.CreateDirectory(_dir);
        Log.Initialize(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Dest => Path.Combine(_dir, "video.mov");

    /// <summary>A Live Photo MOV as the iPhone writes it: 'wide' first, then mdat and moov with the content identifier.</summary>
    private static byte[] LiveMov(string contentId) =>
    [
        .. Box("wide"),
        .. Box("mdat", Bytes(64)),
        .. Box("moov", Mvhd(0, 600, 1410), AppleMeta(("com.apple.quicktime.content.identifier", contentId))),
    ];

    private sealed class FakeToken : IOneDriveWebToken
    {
        public int Refreshes { get; private set; }
        public bool IsConnected => true;

        public Task<string?> GetAsync(bool forceRefresh, CancellationToken ct)
        {
            if (forceRefresh) Refreshes++;
            return Task.FromResult<string?>(Secret);
        }
    }

    private sealed class Server(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(responses[Math.Min(_next++, responses.Length - 1)]);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task Downloads_and_accepts_the_matching_original()
    {
        var server = new Server(Ok(LiveMov(ContentId)));
        var client = new OneDriveLiveVideoClient(new FakeToken(), server);

        var status = await client.DownloadAsync(ItemId, Dest, ContentId, TestContext.Current.CancellationToken);

        Assert.Equal(LiveVideoStatus.Downloaded, status);
        Assert.True(File.Exists(Dest));
        Assert.False(File.Exists(Dest + ".partial"));
        var request = Assert.Single(server.Requests);
        Assert.Equal(
            "https://my.microsoftpersonalcontent.com/_api/v2.1/drives/19B4E9941270DFF7/items/19B4E9941270DFF7!s106a9743988646c5934372ceabb97896/content?format=video",
            request.RequestUri!.OriginalString);
        Assert.Equal(Secret, string.Join("", request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task A_video_for_another_photo_is_discarded()
    {
        var client = new OneDriveLiveVideoClient(new FakeToken(), new Server(Ok(LiveMov("00000000-0000-0000-0000-000000000000"))));

        Assert.Equal(LiveVideoStatus.Mismatch, await client.DownloadAsync(ItemId, Dest, ContentId, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir, "video.mov*"));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("heic")]
    [InlineData("json")]
    public async Task The_still_or_an_error_body_means_no_live_video(string kind)
    {
        byte[] body = kind switch
        {
            "jpeg" => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[40]],
            "heic" => Box("ftyp", Encoding.ASCII.GetBytes("heic"), U32(0), Encoding.ASCII.GetBytes("mif1")),
            _ => Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"itemNotFound\"}}"),
        };
        var client = new OneDriveLiveVideoClient(new FakeToken(), new Server(Ok(body)));

        Assert.Equal(LiveVideoStatus.NotLivePhoto, await client.DownloadAsync(ItemId, Dest, ContentId, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir, "video.mov*"));
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_then_reports_not_connected_without_logging_it()
    {
        var token = new FakeToken();
        var client = new OneDriveLiveVideoClient(token, new Server(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var status = await client.DownloadAsync(ItemId, Dest, ContentId, TestContext.Current.CancellationToken);

        Assert.Equal(LiveVideoStatus.NotConnected, status);
        Assert.Equal(1, token.Refreshes);
        var log = File.ReadAllText(Path.Combine(_dir, "app.log"));
        Assert.Contains("401", log);
        Assert.DoesNotContain("EwAY", log);
    }

    [Fact]
    public async Task Refreshed_token_retries_the_download()
    {
        var token = new FakeToken();
        var client = new OneDriveLiveVideoClient(token, new Server(new HttpResponseMessage(HttpStatusCode.Unauthorized), Ok(LiveMov(ContentId))));

        Assert.Equal(LiveVideoStatus.Downloaded, await client.DownloadAsync(ItemId, Dest, ContentId, TestContext.Current.CancellationToken));
        Assert.Equal(1, token.Refreshes);
    }

    [Fact]
    public void Sniff_accepts_quicktime_shapes_only()
    {
        string Write(byte[] bytes)
        {
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        Assert.Equal(OneDriveLiveVideoClient.SniffResult.QuickTime, OneDriveLiveVideoClient.Sniff(Write(LiveMov(ContentId))));
        Assert.Equal(OneDriveLiveVideoClient.SniffResult.QuickTime,
            OneDriveLiveVideoClient.Sniff(Write(Box("ftyp", Encoding.ASCII.GetBytes("qt  "), U32(0)))));
        Assert.Equal(OneDriveLiveVideoClient.SniffResult.Unknown,
            OneDriveLiveVideoClient.Sniff(Write(Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0)))));
    }

    [Theory]
    [InlineData(@"D:\OneDrive", true)]
    [InlineData(@"D:\OneDrive\Pictures\cache", true)]
    [InlineData(@"D:\OneDriveBackup\cache", false)]
    [InlineData(@"C:\Users\me\AppData\Local\PhotoGallery\motion", false)]
    public void Cache_guard_refuses_folders_inside_onedrive(string path, bool inside) =>
        Assert.Equal(inside, OneDriveRoots.IsUnder(path, [@"D:\OneDrive", @"C:\Users\me\OneDrive - Work"]));
}
