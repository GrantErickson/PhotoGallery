using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using PhotoGallery.Core;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Remote;

public sealed class RemoteServerOptions
{
    public const int DefaultPort = 47813;

    /// <summary>0 picks a free port (tests).</summary>
    public int Port { get; init; } = DefaultPort;
    public required RemoteSecret Secret { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    /// <summary>Listen on this computer's loopback address only (tests).</summary>
    public bool LoopbackOnly { get; init; }
    public TimeProvider? Clock { get; init; }
}

/// <summary>
/// The library over HTTPS for other computers on the network: a web app (browse, search, view, play) and the JSON
/// and files behind it. Only local-network addresses are answered, everything but the sign-in needs a signed-in
/// session, and nothing about OneDrive's sign-in ever leaves this computer.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    public const string CookieName = "pg_session";
    /// <summary>Requests that change something must carry this header: the web app adds it, a form on another site can't.</summary>
    public const string ScriptHeader = "X-Photo-Gallery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRemoteLibrary _library;
    private readonly RemoteServerOptions _options;
    private readonly RemoteSessions _sessions = new();
    private readonly LoginThrottle _throttle;
    private readonly SemaphoreSlim _renderGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private WebApplication? _app;

    private RemoteServer(IRemoteLibrary library, RemoteServerOptions options)
    {
        (_library, _options) = (library, options);
        _throttle = new LoginThrottle(options.Clock);
        Fingerprint = HostCertificate.Fingerprint(options.Certificate);
    }

    /// <summary>The certificate's SHA-256 fingerprint (hex).</summary>
    public string Fingerprint { get; }

    /// <summary>The port it's listening on.</summary>
    public int Port { get; private set; }

    public static async Task<RemoteServer> StartAsync(IRemoteLibrary library, RemoteServerOptions options)
    {
        var server = new RemoteServer(library, options);
        await server.RunAsync();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not { } app) return;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private async Task RunAsync()
    {
        // The empty builder: no configuration files or environment variables that could change what it listens on.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            void Https(ListenOptions listen) => listen.UseHttps(_options.Certificate);
            if (_options.LoopbackOnly) kestrel.Listen(IPAddress.Loopback, _options.Port, Https);
            else kestrel.ListenAnyIP(_options.Port, Https);
        });
        builder.Services.AddLogging();
        builder.Services.AddRoutingCore();
        builder.Services.AddResponseCompression(o =>
        {
            // Only the web app and its lists are compressed (not photos or videos); none of them hold secrets.
            o.EnableForHttps = true;
            o.MimeTypes = ["application/json", "text/html", "text/css", "text/javascript"];
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        var app = builder.Build();
        app.Use(GuardAsync);
        app.UseResponseCompression();
        Map(app);
        try
        {
            await app.StartAsync();
        }
        catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
        {
            await app.DisposeAsync();
            throw new InvalidOperationException($"Port {_options.Port} is already used by another program. Choose another port.", ex);
        }
        Port = new Uri(app.Urls.First().Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port;
        _app = app;
    }

    // ---------- Access ----------

    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!LocalNetwork.IsLocal(ctx.Connection.RemoteIpAddress))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' blob: data:; media-src 'self'; style-src 'self'; " +
                                        "script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && ctx.Request.Headers[ScriptHeader] != "1")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.Ordinal) && path is not ("/api/hello" or "/api/login") && !_sessions.Check(TokenOf(ctx)))
        {
            await WriteJsonAsync(ctx, new { error = "Sign in first." }, StatusCodes.Status401Unauthorized);
            return;
        }
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Remote access: {method} {path} failed", ex);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private static string? TokenOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal)) return header[7..].Trim();
        return ctx.Request.Cookies[CookieName];
    }

    private sealed record LoginRequest(string? Passphrase);

    private async Task LoginAsync(HttpContext ctx)
    {
        var address = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        if (_throttle.RetryAfter(address) is { } wait)
        {
            var seconds = (int)Math.Ceiling(wait.TotalSeconds);
            ctx.Response.Headers.RetryAfter = seconds.ToString();
            await WriteJsonAsync(ctx, new { error = $"Too many wrong passphrases. Try again in {Describe(wait)}.", retryAfter = seconds },
                StatusCodes.Status429TooManyRequests);
            return;
        }
        LoginRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LoginRequest>(ctx.Request.Body, Json, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request?.Passphrase is not { Length: > 0 and <= 1024 } passphrase)
        {
            await WriteJsonAsync(ctx, new { error = "Enter the passphrase." }, StatusCodes.Status400BadRequest);
            return;
        }

        bool right;
        await _throttle.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            right = await Task.Run(() => _options.Secret.Verify(passphrase));
        }
        finally
        {
            _throttle.Gate.Release();
        }
        if (!right)
        {
            _throttle.Failed(address);
            Log.Info($"Remote access: wrong passphrase from {address}");
            await WriteJsonAsync(ctx, new { error = "That passphrase isn't right." }, StatusCodes.Status401Unauthorized);
            return;
        }
        _throttle.Succeeded(address);
        Log.Info($"Remote access: {address} signed in");
        var token = _sessions.Create();
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = RemoteSessions.Lifetime,
            IsEssential = true,
        });
        await WriteJsonAsync(ctx, new { token, name = _library.Name });
    }

    private static string Describe(TimeSpan wait) =>
        wait.TotalMinutes >= 1.5 ? $"{Math.Ceiling(wait.TotalMinutes)} minutes" : wait.TotalSeconds > 60 ? "a minute" : $"{Math.Ceiling(wait.TotalSeconds)} seconds";

    // ---------- Routes ----------

    private void Map(WebApplication app)
    {
        app.MapGet("/", ctx => ResourceAsync(ctx, "index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", ctx => ResourceAsync(ctx, "app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/app.css", ctx => ResourceAsync(ctx, "app.css", "text/css; charset=utf-8"));
        app.MapGet("/icon.svg", ctx => ResourceAsync(ctx, "icon.svg", "image/svg+xml"));

        app.MapGet("/api/hello", ctx => WriteJsonAsync(ctx, new { name = _library.Name, signedIn = _sessions.Check(TokenOf(ctx)) }));
        app.MapPost("/api/login", LoginAsync);
        app.MapPost("/api/logout", async ctx =>
        {
            _sessions.Remove(TokenOf(ctx));
            ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
            await WriteJsonAsync(ctx, new { });
        });

        app.MapGet("/api/items", ItemsAsync);
        app.MapGet("/api/people", ctx => WriteJsonAsync(ctx, _library.GetPeople()
            .Select(p => new { id = p.Id, name = p.Name, count = p.Count })));
        app.MapGet("/api/albums", ctx => WriteJsonAsync(ctx, _library.GetAlbums()
            .Select(a => new { id = a.Id, name = a.Name, count = a.Count, cover = a.CoverMediaId })));
        app.MapGet("/api/people/{id:long}/face", async ctx =>
        {
            var path = await _library.GetFaceAsync(IdOf(ctx), ctx.RequestAborted);
            await SendFileAsync(ctx, path, "image/jpeg", "private, max-age=3600");
        });

        app.MapGet("/api/media/{id:long}", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            var details = await _library.GetDetailsAsync(item, ctx.RequestAborted);
            await WriteJsonAsync(ctx, new
            {
                id = item.Id,
                name = item.FileName,
                video = item.Kind == MediaKind.Video,
                taken = item.TakenLocal.ToString("s"),
                size = item.FileSize,
                width = item.Width,
                height = item.Height,
                durationMs = item.DurationMs,
                camera = string.Join(" ", new[] { item.CameraMake, item.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
                rating = item.Rating,
                live = HasMotion(item.Motion),
                latitude = item.Latitude,
                longitude = item.Longitude,
                place = details.Place,
                people = details.People?.Select(p => new { id = p.Id, name = p.Name }),
                text = details.Text,
                transcript = details.Transcript?.Select(p => new { start = p.Start, text = p.Text, speaker = p.Speaker }),
                edited = details.Edited,
            });
        });
        app.MapGet("/api/media/{id:long}/thumb", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            await SendFileAsync(ctx, await _library.GetThumbnailAsync(item, ctx.RequestAborted), "image/jpeg", "private, max-age=3600");
        });
        app.MapGet("/api/media/{id:long}/display", DisplayAsync);
        app.MapGet("/api/media/{id:long}/video", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            if (item.Kind != MediaKind.Video)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await SendFileAsync(ctx, item.Path, VideoType(item.Path), "private, max-age=3600");
        });
        app.MapGet("/api/media/{id:long}/motion", MotionAsync);
        app.MapGet("/api/media/{id:long}/original", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            await SendFileAsync(ctx, item.Path, "application/octet-stream", "private, no-store", item.FileName);
        });
        app.MapPost("/api/media/{id:long}/rating", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, Json, ctx.RequestAborted);
            if (!body.TryGetProperty("rating", out var value) || !value.TryGetInt32(out var rating) || rating is < 0 or > 5)
            {
                await WriteJsonAsync(ctx, new { error = "A rating is 0 to 5." }, StatusCodes.Status400BadRequest);
                return;
            }
            _library.SetRating(item.Id, rating);
            await WriteJsonAsync(ctx, new { rating });
        });
    }

    private static long IdOf(HttpContext ctx) => long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id) ? id : 0;

    private async Task<MediaItem?> ItemOrNotFoundAsync(HttpContext ctx)
    {
        var item = await Task.Run(() => _library.Get(IdOf(ctx)));
        if (item is null) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return item;
    }

    private static bool HasMotion(MotionSource motion) => motion is MotionSource.LocalPair or MotionSource.Embedded or MotionSource.Cloud;

    /// <summary>
    /// A section of the library as three parallel lists (ids, dates and flags), small enough for the whole timeline:
    /// flags are 1 = video, 2 = Live Photo, 4·rating, and 32·seconds for a video's length.
    /// </summary>
    private async Task ItemsAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var filter = new MediaFilter
        {
            Kinds = q["kind"].ToString() switch { "photos" => KindFilter.Photos, "videos" => KindFilter.Videos, _ => KindFilter.All },
            People = ParseIds(q["people"]) is { Count: > 0 } people ? people : null,
        };
        bool? pictures = null;
        switch (q["section"].ToString())
        {
            case "favorites":
                filter = filter with { MinRating = 4, IncludeScreenshots = true };
                break;
            case "live":
                filter = filter with { MotionOnly = true };
                break;
            case "videos":
                filter = filter with { Kinds = KindFilter.Videos };
                break;
            case "onthisday":
                var day = q["day"].ToString().Split('-');
                if (day.Length != 2 || !int.TryParse(day[0], out var month) || !int.TryParse(day[1], out var date) || month is < 1 or > 12 || date is < 1 or > 31)
                {
                    await WriteJsonAsync(ctx, new { error = "Which day? (month-day)" }, StatusCodes.Status400BadRequest);
                    return;
                }
                filter = filter with { MonthDay = (month, date), To = DateTime.Today.AddDays(2) };
                break;
            case "person" when long.TryParse(q["id"], out var personId):
                filter = filter with { PersonId = personId, IncludeScreenshots = true };
                break;
            case "album" when long.TryParse(q["id"], out var albumId):
                filter = filter with { AlbumId = albumId };
                break;
            case "search":
                var text = q["q"].ToString().Trim();
                if (text.Length is 0 or > 500)
                {
                    await WriteJsonAsync(ctx, new { error = "What to search for?" }, StatusCodes.Status400BadRequest);
                    return;
                }
                var (ids, matchedPictures) = await _library.SearchAsync(text, q["exact"] == "1", ctx.RequestAborted);
                pictures = matchedPictures;
                filter = filter with
                {
                    Ids = ids,
                    IncludeScreenshots = true,
                    Order = q["sort"].ToString() switch { "newest" => MediaOrder.Newest, "oldest" => MediaOrder.Oldest, _ => MediaOrder.Listed },
                };
                break;
            default:
                filter = filter with { IncludeScreenshots = MediaFilter.Timeline.IncludeScreenshots };
                break;
        }
        var items = await Task.Run(() => _library.Query(filter), ctx.RequestAborted);
        var list = new long[items.Count];
        var dates = new long[items.Count];
        var flags = new long[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            list[i] = item.Id;
            dates[i] = item.DateTaken;
            var video = item.Kind == MediaKind.Video;
            flags[i] = (video ? 1L : 0L) + (HasMotion(item.Motion) ? 2L : 0L) + (Math.Clamp(item.Rating, 0, 5) << 2)
                       + (video ? (long)Math.Round(item.DurationMs / 1000.0) << 5 : 0L);
        }
        await WriteJsonAsync(ctx, new { ids = list, dates, flags, pictures });
    }

    private static List<long> ParseIds(StringValues value) =>
        value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().Take(20).ToList();

    private async Task DisplayAsync(HttpContext ctx)
    {
        if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
        var size = int.TryParse(ctx.Request.Query["size"], out var asked) ? Math.Clamp(asked, 256, 4096) : 2560;
        byte[]? jpeg;
        await _renderGate.WaitAsync(ctx.RequestAborted);
        try
        {
            jpeg = await _library.RenderAsync(item, size, ctx.RequestAborted);
        }
        finally
        {
            _renderGate.Release();
        }
        if (jpeg is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        ctx.Response.ContentLength = jpeg.Length;
        await ctx.Response.Body.WriteAsync(jpeg, ctx.RequestAborted);
    }

    private async Task MotionAsync(HttpContext ctx)
    {
        if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
        var (result, path) = await _library.GetMotionAsync(item, ctx.RequestAborted);
        switch (result)
        {
            case MotionResult.Ready when path is not null:
                await SendFileAsync(ctx, path, VideoType(path), "private, max-age=3600");
                break;
            case MotionResult.NeedsSignIn:
                await WriteJsonAsync(ctx, new { error = $"This Live Photo's video is only in OneDrive. On {_library.Name}, connect OneDrive in Settings to play it." },
                    StatusCodes.Status409Conflict);
                break;
            case MotionResult.Unavailable:
                await WriteJsonAsync(ctx, new { error = "The video can't be fetched right now." }, StatusCodes.Status503ServiceUnavailable);
                break;
            default:
                await WriteJsonAsync(ctx, new { error = "This photo has no video." }, StatusCodes.Status404NotFound);
                break;
        }
    }

    /// <summary>What browsers play: QuickTime files are labelled MP4 (the same format, near enough, and Chromium plays them as such).</summary>
    private static string VideoType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        _ => "video/mp4",
    };

    // ---------- Responses ----------

    private static async Task WriteJsonAsync(HttpContext ctx, object value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, value.GetType(), Json, ctx.RequestAborted);
    }

    /// <summary>A file, with ranges (for seeking in videos) and revalidation by date.</summary>
    private static async Task SendFileAsync(HttpContext ctx, string? path, string contentType, string cacheControl, string? downloadName = null)
    {
        if (path is null || !File.Exists(path))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var info = new FileInfo(path);
        var tag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"");
        ctx.Response.Headers.CacheControl = cacheControl;
        await TypedResults.PhysicalFile(path, contentType, downloadName, info.LastWriteTimeUtc, tag, enableRangeProcessing: true).ExecuteAsync(ctx);
    }

    private static async Task ResourceAsync(HttpContext ctx, string name, string contentType)
    {
        await using var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream("web/" + name);
        if (stream is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
}
