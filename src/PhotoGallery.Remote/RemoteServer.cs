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
using PhotoGallery.Core;

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
public sealed partial class RemoteServer : IAsyncDisposable
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
        if (!LocalNetwork.IsLocal(ctx.Connection.RemoteIpAddress) || !IsOwnName(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        // Map tiles come straight from OpenStreetMap to the browser, as they do in the app.
        headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' blob: data: https://tile.openstreetmap.org; media-src 'self'; " +
                                        "style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

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

    /// <summary>
    /// Asked for by this computer's name, an address, or localhost: a page on some other site can't point its own
    /// name at this computer ("DNS rebinding") and reach the API.
    /// </summary>
    internal static bool IsOwnName(string host)
    {
        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var label = host.Split('.')[0];
        return label.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
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
        app.MapGet("/", ctx => ResourceAsync(ctx, "index.html"));
        app.MapGet("/api/hello", ctx => WriteJsonAsync(ctx, new
        {
            name = _library.Name,
            signedIn = _sessions.Check(TokenOf(ctx)),
            changes = _library.AllowChanges,
        }));
        app.MapPost("/api/login", LoginAsync);
        app.MapPost("/api/logout", async ctx =>
        {
            _sessions.Remove(TokenOf(ctx));
            ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
            await WriteJsonAsync(ctx, new { });
        });
        MapReading(app);
        MapChanging(app);
        MapEditing(app);
        // The web app's own files (scripts, styles, icons, the map library).
        app.MapGet("/{*path}", ctx => ResourceAsync(ctx, ctx.Request.RouteValues["path"]?.ToString() ?? ""));
    }

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

    /// <summary>A request's JSON body, or null if it isn't what was expected.</summary>
    private static async Task<T?> ReadAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Json, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The web app's files, embedded in this assembly as web/... (folders with forward slashes).</summary>
    private static readonly Dictionary<string, string> Resources = typeof(RemoteServer).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("web/", StringComparison.Ordinal))
        .ToDictionary(n => n["web/".Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private static async Task ResourceAsync(HttpContext ctx, string path)
    {
        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => null,
        };
        if (contentType is null || !Resources.TryGetValue(path, out var name) ||
            typeof(RemoteServer).Assembly.GetManifestResourceStream(name) is not { } stream)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await using (stream)
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.CacheControl = "no-cache";
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }
}
