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
    Failed,
}

/// <summary>
/// Microsoft Graph access for the personal OneDrive (MSAL public client, Files.Read).
/// The Live Photo video comes from <c>/content?format=video</c> — undocumented but verified in
/// spikes/OneDriveLivePhoto, so failures are treated as "no motion available", never as errors.
/// </summary>
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

    public string? AccountName { get; private set; }
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

    /// <summary>Downloads the Live Photo video for a OneDrive-relative path ("Pictures/Camera Roll/x.heic").</summary>
    public async Task<LiveVideoStatus> DownloadLiveVideoAsync(string oneDrivePath, string destination, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(interactive: false, ct);
        if (token is null) return LiveVideoStatus.NotSignedIn;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(oneDrivePath)}:/content?format=video");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NotAcceptable)
                return LiveVideoStatus.NotLivePhoto;
            if (!response.IsSuccessStatusCode) return LiveVideoStatus.Failed;

            var temp = destination + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
                await source.CopyToAsync(file, ct);

            if (!IsVideo(temp))
            {
                // Some items answer with the still itself; that means there is no motion to play.
                File.Delete(temp);
                return LiveVideoStatus.NotLivePhoto;
            }
            File.Move(temp, destination, overwrite: true);
            return LiveVideoStatus.Downloaded;
        }
        catch (HttpRequestException)
        {
            return LiveVideoStatus.Failed;
        }
    }

    /// <summary>The item's OneDrive web URL, for "Open in OneDrive".</summary>
    public async Task<string?> GetWebUrlAsync(string oneDrivePath, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(interactive: false, ct);
        if (token is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(oneDrivePath)}?$select=webUrl");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("webUrl", out var url) ? url.GetString() : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string ItemUrl(string oneDrivePath) =>
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
