using System.Net;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Cloud;

/// <summary>
/// Supplies the Authorization header of the user's OneDrive web session (the only credential the Live Photo
/// video endpoint accepts). Implementations keep it in memory only; it is never logged or persisted.
/// </summary>
public interface IOneDriveWebToken
{
    /// <summary>True once the user has connected the web session (a fresh token may still need fetching).</summary>
    bool IsConnected { get; }

    /// <summary>The header value ("bearer Ew…"), refreshing it if stale or when <paramref name="forceRefresh"/>; null if not connected.</summary>
    Task<string?> GetAsync(bool forceRefresh, CancellationToken ct);
}

public enum LiveVideoStatus
{
    Downloaded,
    /// <summary>OneDrive has no Live Photo video for this item.</summary>
    NotLivePhoto,
    /// <summary>The OneDrive web session isn't connected (or its sign-in expired).</summary>
    NotConnected,
    /// <summary>A video came back but its content identifier doesn't match the photo; it was discarded.</summary>
    Mismatch,
    Failed,
}

/// <summary>
/// Downloads the original Live Photo video (.MOV) of a OneDrive item through the web app's API:
/// <c>GET https://my.microsoftpersonalcontent.com/_api/v2.1/drives/{driveId}/items/{itemId}/content?format=video</c>
/// with the web session's Authorization header. Undocumented, so every response is validated: it must look like a
/// QuickTime movie (not the still, not an error body) and carry the photo's Apple content identifier.
/// <c>format=mp4</c> is deliberately not used: it is a re-encode with capture date, location and pairing ID stripped.
/// </summary>
public sealed class OneDriveLiveVideoClient(IOneDriveWebToken token, HttpMessageHandler? handler = null)
{
    private const string ApiRoot = "https://my.microsoftpersonalcontent.com/_api/v2.1";
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient Http = handler is null ? Shared : new HttpClient(handler);

    public bool IsConnected => token.IsConnected;

    public async Task<LiveVideoStatus> DownloadAsync(string itemId, string destination, string? expectedContentId, CancellationToken ct = default)
    {
        var driveId = itemId.Split('!')[0];
        // The item id's '!' is sent literally, as the web app does.
        var url = $"{ApiRoot}/drives/{driveId}/items/{itemId}/content?format=video";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var auth = await token.GetAsync(forceRefresh: attempt == 2, ct);
            if (auth is null) return LiveVideoStatus.NotConnected;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", auth);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Live video request for {itemId} failed: {ex.Message}");
                return LiveVideoStatus.Failed;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                Log.Info($"Live video {itemId}: {status} {response.Content.Headers.ContentType} {response.Content.Headers.ContentLength} bytes");
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized when attempt == 1:
                        continue; // token expired: refresh once and retry
                    case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                        return LiveVideoStatus.NotConnected;
                    case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable when attempt < 4:
                        await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt), ct);
                        continue;
                    case HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NotAcceptable:
                        return LiveVideoStatus.NotLivePhoto;
                }
                if (!response.IsSuccessStatusCode) return LiveVideoStatus.Failed;
                return await SaveAndValidateAsync(response, itemId, destination, expectedContentId, ct);
            }
        }
        return LiveVideoStatus.Failed;
    }

    /// <summary>Item id for a OneDrive-relative path via the web API (used when Graph isn't signed in).</summary>
    public async Task<string?> FindItemIdAsync(string oneDrivePath, CancellationToken ct = default)
    {
        if (await token.GetAsync(forceRefresh: false, ct) is not { } auth) return null;
        var path = string.Join('/', oneDrivePath.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/drive/root:/{path}?$select=id");
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"Web item lookup {oneDrivePath}: {(int)response.StatusCode}");
                return null;
            }
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            Log.Error($"Web item lookup {oneDrivePath} failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<LiveVideoStatus> SaveAndValidateAsync(HttpResponseMessage response, string itemId, string destination,
        string? expectedContentId, CancellationToken ct)
    {
        var partial = destination + ".partial";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(partial))
            await source.CopyToAsync(file, ct);

        var verdict = Sniff(partial);
        if (verdict != SniffResult.QuickTime)
        {
            Log.Info($"Live video {itemId}: response is {verdict}, not a video ({new FileInfo(partial).Length} bytes)");
            File.Delete(partial);
            return LiveVideoStatus.NotLivePhoto;
        }

        string? contentId;
        await using (var stream = File.OpenRead(partial))
            contentId = QuickTimeReader.Read(stream)?.ContentIdentifier;
        if (expectedContentId is not null && contentId is not null &&
            !string.Equals(contentId, expectedContentId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Live video {itemId}: content identifier {contentId} doesn't match the photo's {expectedContentId}; discarded");
            File.Delete(partial);
            return LiveVideoStatus.Mismatch;
        }

        File.Move(partial, destination, overwrite: true);
        return LiveVideoStatus.Downloaded;
    }

    internal enum SniffResult
    {
        QuickTime,
        StillImage,
        ErrorBody,
        Unknown,
    }

    /// <summary>The iPhone original starts with a 'wide' box then mdat/moov; accept QuickTime-shaped files only.</summary>
    internal static SniffResult Sniff(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var f = File.OpenRead(path);
        var read = f.ReadAtLeast(head, 12, throwOnEndOfStream: false);
        if (read >= 1 && head[0] is (byte)'{' or (byte)'<') return SniffResult.ErrorBody;
        if (read >= 2 && head[0] == 0xFF && head[1] == 0xD8) return SniffResult.StillImage;
        if (read < 12) return SniffResult.Unknown;

        var type = System.Text.Encoding.ASCII.GetString(head.Slice(4, 4));
        if (type is "wide" or "mdat" or "moov" or "free" or "skip") return SniffResult.QuickTime;
        if (type != "ftyp") return SniffResult.Unknown;
        var brand = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4));
        return brand switch
        {
            "qt  " => SniffResult.QuickTime,
            "heic" or "heix" or "mif1" or "msf1" or "avif" => SniffResult.StillImage,
            _ => SniffResult.Unknown,
        };
    }
}

/// <summary>Where OneDrive syncs to, so caches are never placed inside a synced folder.</summary>
public static class OneDriveRoots
{
    public static IReadOnlyList<string> Find()
    {
        var roots = new List<string>();
        foreach (var name in new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) roots.Add(value);
        try
        {
            using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string { Length: > 0 } folder) roots.Add(folder);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True if <paramref name="path"/> is one of the roots or inside one.</summary>
    public static bool IsUnder(string path, IEnumerable<string> roots)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return roots.Any(root =>
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
