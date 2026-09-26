using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace PhotoGallery.Core.Cloud;

public enum LiveVideoStatus
{
    Downloaded,
    NotLivePhoto,
    NotSignedIn,
    /// <summary>OneDrive refused the video request (406 since 2026-09-25) — says nothing about the photo itself.</summary>
    ServiceRefused,
    Failed,
}

/// <summary>
/// Microsoft Graph access for the personal OneDrive (MSAL public client, Files.Read).
/// The Live Photo video comes from the undocumented <c>/items/{id}/content?format=video</c>. It worked in
/// spikes/OneDriveLivePhoto and then started answering 406 for every item the same day, so callers must
/// treat it as best-effort and fall back to opening the photo on OneDrive.com.
/// </summary>
public sealed record OneDriveItem(string Id, string? WebUrl);

public sealed class OneDriveClient
{
    private static readonly string[] Scopes = ["Files.Read"];
    private readonly IPublicClientApplication _app;
    private readonly Task _cacheReady;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public OneDriveClient(string clientId, string tokenCacheDirectory)
    {
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost")
            .Build();
        _cacheReady = RegisterCacheAsync(tokenCacheDirectory);
    }

    private static readonly TimeSpan RefusalBackoff = TimeSpan.FromMinutes(15);
    private DateTime _refusedUntil;

    public string? AccountName { get; private set; }

    /// <summary>True while we're backing off after OneDrive refused a Live Photo video request.</summary>
    public bool IsLiveVideoBackingOff => DateTime.UtcNow < _refusedUntil;
    public bool IsSignedIn => AccountName is not null;

    private async Task RegisterCacheAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var props = new StorageCreationPropertiesBuilder("msal.cache", directory).Build(); // DPAPI-protected on Windows
        (await MsalCacheHelper.CreateAsync(props)).RegisterCache(_app.UserTokenCache);
    }

    /// <summary>Signs in from the token cache without UI. Returns false if the user must sign in interactively.</summary>
    public async Task<bool> TrySignInSilentAsync(CancellationToken ct = default) => await GetTokenAsync(interactive: false, ct) is not null;

    public async Task<bool> SignInAsync(CancellationToken ct = default) => await GetTokenAsync(interactive: true, ct) is not null;

    public async Task SignOutAsync()
    {
        await _cacheReady;
        foreach (var account in await _app.GetAccountsAsync()) await _app.RemoveAsync(account);
        AccountName = null;
    }

    private async Task<string?> GetTokenAsync(bool interactive, CancellationToken ct)
    {
        await _cacheReady;
        AuthenticationResult? result = null;
        try
        {
            var account = (await _app.GetAccountsAsync()).FirstOrDefault();
            if (account is not null) result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
        }
        if (result is null && interactive)
        {
            try
            {
                result = await _app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false).ExecuteAsync(ct);
            }
            catch (MsalException)
            {
                return null;
            }
        }
        AccountName = result?.Account.Username;
        return result?.AccessToken;
    }

    /// <summary>Looks up a OneDrive item by its OneDrive-relative path ("Pictures/Camera Roll/x.heic").</summary>
    public async Task<OneDriveItem?> GetItemAsync(string oneDrivePath, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(interactive: false, ct);
        if (token is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PathUrl(oneDrivePath)}?$select=id,webUrl");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"OneDrive lookup {oneDrivePath}: {(int)response.StatusCode}");
                return null;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            return new OneDriveItem(root.GetProperty("id").GetString()!, root.TryGetProperty("webUrl", out var url) ? url.GetString() : null);
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"OneDrive lookup {oneDrivePath} failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Downloads the Live Photo video of an item. Must be addressed by item id: the path form
    /// (/root:/path:/content?format=video) answers 406 even for real Live Photos.
    /// </summary>
    public async Task<LiveVideoStatus> DownloadLiveVideoAsync(string itemId, string destination, CancellationToken ct = default)
    {
        // Don't hammer the service while it's refusing; retry automatically after the backoff.
        if (IsLiveVideoBackingOff) return LiveVideoStatus.ServiceRefused;
        var token = await GetTokenAsync(interactive: false, ct);
        if (token is null) return LiveVideoStatus.NotSignedIn;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/drive/items/{itemId}/content?format=video");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Log.Info($"Live video {itemId}: {(int)response.StatusCode} {response.Content.Headers.ContentType} {response.Content.Headers.ContentLength} bytes");
            if (response.StatusCode == HttpStatusCode.NotFound) return LiveVideoStatus.NotLivePhoto;
            // format=video returned the MOV on 2026-09-25 morning, then 406 for every item (even ones that had
            // worked). Treat refusals as a service problem, not as "this photo has no motion".
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotAcceptable)
            {
                _refusedUntil = DateTime.UtcNow + RefusalBackoff;
                return LiveVideoStatus.ServiceRefused;
            }
            _refusedUntil = default;
            if (!response.IsSuccessStatusCode) return LiveVideoStatus.Failed;

            var temp = destination + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
                await source.CopyToAsync(file, ct);

            if (!IsVideo(temp))
            {
                // Some items answer with the still itself; that means there is no motion to play.
                Log.Info($"Live video {itemId}: response is not a video ({new FileInfo(temp).Length} bytes)");
                File.Delete(temp);
                return LiveVideoStatus.NotLivePhoto;
            }
            File.Move(temp, destination, overwrite: true);
            return LiveVideoStatus.Downloaded;
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Live video {itemId} failed", ex);
            return LiveVideoStatus.Failed;
        }
    }

    /// <summary>GET a Graph URL as JSON, retrying throttling (429/503) with the server's Retry-After.</summary>
    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await GetTokenAsync(interactive: false, ct) ?? throw new InvalidOperationException("Not signed in to OneDrive.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (attempt < 6 && (int)response.StatusCode is 429 or 503 or 504)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                Log.Info($"Graph throttled ({(int)response.StatusCode}); retrying in {wait.TotalSeconds:F0}s");
                await Task.Delay(wait, ct);
                continue;
            }
            throw new HttpRequestException($"Graph {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    /// <summary>Diagnostics: sends a signed request and returns status, content type, length and the first bytes.</summary>
    public async Task<string> ProbeAsync(string url, string? accept = null, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(interactive: false, ct);
        if (token is null) return "not signed in";
        var anonymous = url.StartsWith("noauth:", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, anonymous ? url[7..] : url);
        if (!anonymous) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        using var response = await _http.SendAsync(request, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (Environment.GetEnvironmentVariable("PG_PROBE_OUT") is { Length: > 0 } outFile) await File.WriteAllBytesAsync(outFile, bytes, ct);
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var preview = type.Contains("json") || type.StartsWith("text") ? System.Text.Encoding.UTF8.GetString(bytes) : Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
        return $"{(int)response.StatusCode} {type} {bytes.Length:N0} bytes{Environment.NewLine}{(preview.Length > 50000 ? preview[..50000] : preview)}";
    }

    private static string PathUrl(string oneDrivePath) =>
        "https://graph.microsoft.com/v1.0/me/drive/root:/" + string.Join('/', oneDrivePath.Split('/').Select(Uri.EscapeDataString));

    private static bool IsVideo(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var f = File.OpenRead(path);
        if (f.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12) return false;
        if (!head.Slice(4, 4).SequenceEqual("ftyp"u8)) return head.Slice(4, 4).SequenceEqual("moov"u8) || head.Slice(4, 4).SequenceEqual("wide"u8);
        var brand = head.Slice(8, 4);
        return !(brand.SequenceEqual("heic"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("heix"u8));
    }
}
