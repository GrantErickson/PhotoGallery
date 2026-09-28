using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PhotoGallery.Core.Cloud;

/// <summary>A person OneDrive recognises (after any merges made in OneDrive), with the name given there.</summary>
public sealed record OneDrivePerson(string Id, string? Name, int PhotoCount, bool IsHidden, string? RepresentativeItemId);

/// <summary>
/// A face box in the upright (EXIF-rotated, as displayed) picture, as fractions of the picture's longer side. OneDrive
/// reports the picture's size inconsistently (sometimes before rotation, sometimes before an iPhone HEIC's display
/// crop, sometimes downscaled), but its boxes are always in an upright frame whose long side matches that size's.
/// </summary>
public readonly record struct FaceBox(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    /// <summary>The box in pixels (or any units) of the upright picture shown at this size.</summary>
    public (double X, double Y, double Width, double Height) In(double width, double height)
    {
        var scale = Math.Max(width, height);
        return (X * scale, Y * scale, Width * scale, Height * scale);
    }
}

/// <summary>One detected face: OneDrive's id for the face, the person it belongs to and where it is.</summary>
public sealed record OneDriveFace(string FaceId, string PersonId, FaceBox Box);

/// <summary>A photo in OneDrive with the faces detected in it, and whether OneDrive has its Live Photo video.</summary>
public sealed record OneDrivePhoto(string ItemId, string Name, string? FolderPath, string? ETag, IReadOnlyList<OneDriveFace> Faces, bool IsLive = false);

public sealed record OneDrivePhotoPage(IReadOnlyList<OneDrivePhoto> Photos, string? NextLink);

/// <summary>The OneDrive web session isn't connected or was refused.</summary>
public sealed class OneDriveWebUnavailableException(string message) : Exception(message);

/// <summary>
/// OneDrive's people and face positions, read the way onedrive.live.com's Photos › People view does:
/// <c>/_api/v2.1/drives/{driveId}/recognizedEntities</c> for the people and their names, and the photo listing with
/// <c>expand=detectedEntities(expand=recognizedEntity)</c> for each face's box and person. Names and merges made in
/// the gallery are sent the way that view sends them (seen in its own requests). Undocumented, so only the fields
/// used here are relied on. Uses the web session's Authorization header (in memory only), which is only ever sent to
/// the API host.
/// </summary>
public sealed class OneDrivePeopleClient(IOneDriveWebToken token, HttpMessageHandler? handler = null)
{
    private const string ApiHost = "my.microsoftpersonalcontent.com";
    private const string ApiRoot = $"https://{ApiHost}/_api/v2.1";
    /// <summary>The photo listing only accepts these selections (fewer fields → 400).</summary>
    private const string PhotoFields = "id,name,parentReference,image,photo,cTag,lastModifiedDateTime,fileSystemInfo,createdDateTime,size";
    private const string FacesExpand = "detectedEntities(expand=recognizedEntity)";
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient Http = handler is null ? Shared : new HttpClient(handler);

    public bool IsConnected => token.IsConnected;

    /// <summary>Pause between listing pages, to keep the load on OneDrive light.</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    public async Task<string> GetDriveIdAsync(CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ApiRoot}/drive?select=id", ct);
        return json.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("OneDrive didn't return a drive id.");
    }

    /// <summary>Everyone OneDrive has recognised, named first.</summary>
    public async IAsyncEnumerable<OneDrivePerson> GetPeopleAsync(string driveId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? url = $"{ApiRoot}/drives/{driveId}/recognizedEntities?top=100";
        while (url is not null)
        {
            using var json = await GetJsonAsync(url, ct);
            foreach (var person in json.RootElement.GetProperty("value").EnumerateArray())
                if (ParsePerson(person) is { } parsed) yield return parsed;
            url = NextLink(json.RootElement);
        }
    }

    /// <summary>
    /// A single person, including ones the people list leaves out: groups merged into someone in OneDrive keep their
    /// own id (and any name they had), so the same name can belong to several ids.
    /// </summary>
    public async Task<OneDrivePerson?> GetPersonAsync(string driveId, string personId, CancellationToken ct = default)
    {
        try
        {
            using var json = await GetJsonAsync($"{ApiRoot}/drives/{driveId}/recognizedEntities/{Uri.EscapeDataString(personId)}", ct);
            return ParsePerson(json.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// One page of the photo listing (newest first) with faces; pass the previous page's link to continue. The
    /// listing can name a face's person as first grouped (before merges); <see cref="GetPhotoAsync"/> has the current one.
    /// </summary>
    public async Task<OneDrivePhotoPage> GetPhotosAsync(string driveId, string? nextLink, int pageSize = 1000, CancellationToken ct = default)
    {
        if (nextLink is not null && PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct);
        var url = nextLink ?? $"{ApiRoot}/drives/{driveId}/items/root/items" +
            "?$filter=" + Uri.EscapeDataString("photo ne null and photo/takenDateTime ge 1900-01-01T00:00:00.000Z") +
            "&orderby=" + Uri.EscapeDataString("photo/takenDateTime desc") +
            "&select=" + Uri.EscapeDataString(PhotoFields) +
            $"&top={pageSize}&expand=" + Uri.EscapeDataString(FacesExpand);
        using var json = await GetJsonAsync(url, ct);
        var photos = json.RootElement.GetProperty("value").EnumerateArray().Select(ParsePhoto).OfType<OneDrivePhoto>().ToList();
        return new OneDrivePhotoPage(photos, NextLink(json.RootElement));
    }

    /// <summary>A single photo's faces (after a merge in OneDrive, its faces point at the surviving person).</summary>
    public async Task<OneDrivePhoto?> GetPhotoAsync(string driveId, string itemId, CancellationToken ct = default)
    {
        var url = $"{ApiRoot}/drives/{driveId}/items/{itemId}?select={Uri.EscapeDataString(PhotoFields)}&expand={Uri.EscapeDataString(FacesExpand)}";
        try
        {
            using var json = await GetJsonAsync(url, ct);
            return ParsePhoto(json.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Names a person in OneDrive, as its People page does (<c>PATCH recognizedEntities/{id}</c> with the display name).
    /// Returns the person as OneDrive has them now.
    /// </summary>
    public async Task<OneDrivePerson?> RenameAsync(string driveId, string personId, string name, CancellationToken ct = default)
    {
        using var json = await SendAsync(HttpMethod.Patch, EntityUrl(driveId, personId),
            new { identity = new { user = new { displayName = name } } }, ct);
        return ParsePerson(json.RootElement);
    }

    /// <summary>
    /// Merges a person into another in OneDrive, as its People page does: the merged-away person is patched with the
    /// other's id and name. Returns the person they're now part of.
    /// </summary>
    public async Task<OneDrivePerson?> MergeAsync(string driveId, string personId, string intoId, string intoName, CancellationToken ct = default)
    {
        using var json = await SendAsync(HttpMethod.Patch, EntityUrl(driveId, personId),
            new { id = intoId, identity = new { user = new { displayName = intoName } } }, ct);
        return ParsePerson(json.RootElement);
    }

    private static string EntityUrl(string driveId, string personId) =>
        $"{ApiRoot}/drives/{driveId}/recognizedEntities/{Uri.EscapeDataString(personId)}?expand={Uri.EscapeDataString("identity/user/thumbnails")}";

    internal static OneDrivePerson? ParsePerson(JsonElement e)
    {
        if (Str(e, "id") is not { } id) return null;
        string? name = null;
        if (e.TryGetProperty("identity", out var identity) && identity.TryGetProperty("user", out var user))
            name = Str(user, "displayName") is { } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null;
        var count = e.TryGetProperty("photoCount", out var c) && c.TryGetInt32(out var value) ? value : 0;
        var hidden = e.TryGetProperty("isHidden", out var h) && h.ValueKind == JsonValueKind.True;
        return new OneDrivePerson(id, name, count, hidden, Str(e, "representativeItemId"));
    }

    internal static OneDrivePhoto? ParsePhoto(JsonElement e)
    {
        if (Str(e, "id") is not { } id || Str(e, "name") is not { } name) return null;
        string? folder = null;
        if (e.TryGetProperty("parentReference", out var parent) && Str(parent, "path") is { } path)
        {
            // "/drives/19B4…/root:/Pictures/Camera Roll" → "Pictures/Camera Roll"
            var i = path.IndexOf("root:", StringComparison.Ordinal);
            if (i >= 0) folder = Uri.UnescapeDataString(path[(i + 5)..].TrimStart('/'));
        }

        var faces = new List<OneDriveFace>();
        if (e.TryGetProperty("image", out var image) && Num(image, "width") is double width and > 0 && Num(image, "height") is double height and > 0 &&
            e.TryGetProperty("detectedEntities", out var detected) && detected.ValueKind == JsonValueKind.Array)
        {
            foreach (var face in detected.EnumerateArray())
            {
                if (Str(face, "id") is not { } faceId ||
                    !face.TryGetProperty("recognizedEntity", out var entity) || Str(entity, "id") is not { } personId) continue;
                var left = Num(face, "boundingBoxLeft");
                var top = Num(face, "boundingBoxTop");
                var w = Num(face, "boundingBoxWidth");
                var h = Num(face, "boundingBoxHeight");
                if (left is null || top is null || w is not > 0 || h is not > 0) continue;
                // Pixels of an upright frame whose long side is image's long side (see FaceBox).
                var longSide = Math.Max(width, height);
                var box = new FaceBox(
                    Math.Clamp(left.Value / longSide, 0, 1), Math.Clamp(top.Value / longSide, 0, 1),
                    Math.Clamp(w.Value / longSide, 0, 1), Math.Clamp(h.Value / longSide, 0, 1));
                faces.Add(new OneDriveFace(faceId, personId, box));
            }
        }
        // A Live Photo has an (empty) "livePhoto" object in its photo facet.
        var live = e.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object && photo.TryGetProperty("livePhoto", out _);
        return new OneDrivePhoto(id, name, folder, Str(e, "@odata.etag"), faces, live);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>The continuation link, only if it stays on the API host (the token is never sent anywhere else).</summary>
    private static string? NextLink(JsonElement root)
    {
        if (Str(root, "@odata.nextLink") is not { } link) return null;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            ? link
            : throw new InvalidDataException($"OneDrive returned a continuation link to an unexpected host ({uri?.Host}).");
    }

    private Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);

    /// <summary>
    /// A request to the API host with the web session's Authorization header; <paramref name="body"/> goes as JSON.
    /// Retried after a refreshed sign-in, and while OneDrive is busy (then it wasn't applied, so a change can be
    /// sent again).
    /// </summary>
    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OneDrive's people API is only called on its own host.");
        for (var attempt = 1; ; attempt++)
        {
            var auth = await token.GetAsync(forceRefresh: attempt == 2, ct)
                       ?? throw new OneDriveWebUnavailableException("Connect OneDrive in Settings to read people from OneDrive.");
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", auth);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized when attempt == 1:
                    continue; // token expired: refresh once and retry
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new OneDriveWebUnavailableException("OneDrive refused the web session; sign in again in Settings.");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                    when attempt < 5:
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
                    Log.Info($"OneDrive people API busy ({(int)response.StatusCode}); waiting {wait.TotalSeconds:0}s");
                    await Task.Delay(wait, ct);
                    continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OneDrive people API returned {(int)response.StatusCode}{await ErrorMessageAsync(response, ct)}.", null, response.StatusCode);
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
    }

    /// <summary>OneDrive's own explanation of an error (<c>{"error":{"message":…}}</c>), if it gave one.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return json.RootElement.TryGetProperty("error", out var error) && Str(error, "message") is { Length: > 0 } message
                ? $": {(message.Length > 200 ? message[..200] + "…" : message)}"
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
