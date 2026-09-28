using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Imaging;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Remote;

// Reading the library: lists, details, thumbnails, photos, videos, the map, people, albums, tags, folders, duplicates.
public sealed partial class RemoteServer
{
    private void MapReading(WebApplication app)
    {
        app.MapGet("/api/items", async ctx =>
        {
            if (await FilterFromAsync(ctx) is { } found) await WriteItemsAsync(ctx, found.Filter, found.Pictures);
        });
        // A given set, too long for an address: a map cluster, a duplicate group.
        app.MapPost("/api/items/list", async ctx =>
        {
            var body = await ReadAsync<IdsRequest>(ctx);
            if (body?.Ids is not { Count: > 0 and <= 100_000 } ids)
            {
                await WriteJsonAsync(ctx, new { error = "Which items?" }, StatusCodes.Status400BadRequest);
                return;
            }
            var order = body.Sort switch { "oldest" => MediaOrder.Oldest, "listed" => MediaOrder.Listed, _ => MediaOrder.Newest };
            await WriteItemsAsync(ctx, new MediaFilter { Ids = ids, Order = order, IncludeScreenshots = true }, null);
        });

        app.MapGet("/api/suggest", ctx => WriteJsonAsync(ctx, _library.Suggest(ctx.Request.Query["q"].ToString().Trim())
            .Select(s => new { text = s.Text, kind = s.Kind })));
        app.MapGet("/api/people", ctx => WriteJsonAsync(ctx, _library.GetPeople(ctx.Request.Query["hidden"] == "1")
            .Select(p => new { id = p.Id, name = p.Name, count = p.Count, hidden = p.Hidden, notTagged = p.NotTagged })));
        app.MapGet("/api/people/review", ctx => WriteJsonAsync(ctx, _library.GetPeopleToReview().Select(p => new { id = p.Id, count = p.Count })));
        app.MapGet("/api/people/{id:long}/samples", ctx =>
        {
            var (samples, span) = _library.GetFaceSamples(IdOf(ctx), 8);
            return WriteJsonAsync(ctx, new
            {
                first = span?.First, last = span?.Last,
                samples = samples.Select(s => new { media = s.MediaId, taken = s.DateTaken }),
            });
        });
        app.MapGet("/api/albums", ctx => WriteJsonAsync(ctx, _library.GetAlbums()
            .Select(a => new { id = a.Id, name = a.Name, count = a.Count, cover = a.CoverMediaId })));
        app.MapGet("/api/tags", ctx => WriteJsonAsync(ctx, _library.GetTags()
            .Where(t => t.Count > 0)
            .Select(t => new { id = t.Id, name = t.Name, count = t.Count, yours = t.IsUserTag, type = t.TagType.ToString().ToLowerInvariant() })));
        app.MapGet("/api/folders", ctx => WriteJsonAsync(ctx, _library.GetFolders()
            .Select(f => new { id = f.Id, parent = f.ParentId, name = f.Name })));
        app.MapGet("/api/map", async ctx =>
        {
            var points = await Task.Run(_library.GetGeoPoints, ctx.RequestAborted);
            await WriteJsonAsync(ctx, new
            {
                ids = points.Select(p => p.Id),
                lat = points.Select(p => Math.Round(p.Latitude, 5)),
                lon = points.Select(p => Math.Round(p.Longitude, 5)),
            });
        });
        app.MapGet("/api/places", async ctx =>
        {
            var query = ctx.Request.Query["q"].ToString().Trim();
            if (query.Length is < 2 or > 200)
            {
                await WriteJsonAsync(ctx, new { error = "What place?" }, StatusCodes.Status400BadRequest);
                return;
            }
            try
            {
                var hits = await _library.SearchPlacesAsync(query, ctx.Request.Query["online"] == "1", ctx.RequestAborted);
                await WriteJsonAsync(ctx, hits.Select(h => new
                {
                    name = h.Name, detail = h.Detail, kind = h.Kind, caption = h.Caption, lat = h.Latitude, lon = h.Longitude,
                    s = h.South, w = h.West, n = h.North, e = h.East,
                }));
            }
            catch (IOException ex)
            {
                await WriteJsonAsync(ctx, new { error = ex.Message }, StatusCodes.Status502BadGateway);
            }
        });
        app.MapGet("/api/duplicates", ctx => WriteJsonAsync(ctx, DuplicatesJson()));
        app.MapPost("/api/duplicates/scan", async ctx =>
        {
            _library.Duplicates.Start();
            await WriteJsonAsync(ctx, DuplicatesJson());
        });

        app.MapGet("/api/media/{id:long}/face", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            if (!long.TryParse(ctx.Request.Query["person"], out var personId))
            {
                await BadAsync(ctx, "Whose face?");
                return;
            }
            await SendFileAsync(ctx, await _library.GetFaceCropAsync(item, personId, ctx.RequestAborted), "image/jpeg", "private, max-age=86400");
        });
        app.MapGet("/api/people/{id:long}/face", async ctx =>
        {
            var (path, final) = await _library.GetFaceAsync(IdOf(ctx), ctx.RequestAborted);
            await SendFileAsync(ctx, path, "image/jpeg", final ? "private, max-age=3600" : "no-store");
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
                faces = details.Faces?.Select(f => new { id = f.PersonId, name = f.Name, x = f.X, y = f.Y, w = f.Width, h = f.Height }),
                text = details.Text,
                textLines = details.TextLines?.Select(l => l.Words.Select(w => new { t = w.Text, x = w.X, y = w.Y, w = w.Width, h = w.Height })),
                transcript = details.Transcript?.Select(p => new { start = p.Start, text = p.Text, speaker = p.Speaker }),
                tags = details.Tags?.Select(t => new { id = t.Id, name = t.Name, yours = t.Yours }),
                albums = details.Albums,
                edited = details.Edited,
                screenshot = item.IsScreenshot,
                utility = item.IsClutter,
                utilityChosen = item.UtilityOverride is not null,
            });
        });
        app.MapGet("/api/media/{id:long}/similar", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            var similar = await _library.FindSimilarAsync(item.Id, ctx.RequestAborted);
            await WriteItemsAsync(ctx, new MediaFilter { Ids = [item.Id, .. similar], Order = MediaOrder.Listed, IncludeScreenshots = true }, null);
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
    }

    private sealed record IdsRequest(List<long>? Ids, string? Sort);

    private static long IdOf(HttpContext ctx) => long.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id) ? id : 0;

    private async Task<MediaItem?> ItemOrNotFoundAsync(HttpContext ctx)
    {
        var item = await Task.Run(() => _library.Get(IdOf(ctx)));
        if (item is null) ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return item;
    }

    private static bool HasMotion(MotionSource motion) => motion is MotionSource.LocalPair or MotionSource.Embedded or MotionSource.Cloud;

    /// <summary>
    /// The filter for /api/items: a section (with its id or day), then the filters every list has (kind, people,
    /// rating, Live Photos only, screenshots). Writes the error itself and returns null if the request is wrong.
    /// </summary>
    private async Task<(MediaFilter Filter, bool? Pictures)?> FilterFromAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var filter = new MediaFilter
        {
            Kinds = q["kind"].ToString() switch { "photos" => KindFilter.Photos, "videos" => KindFilter.Videos, _ => KindFilter.All },
            People = ParseIds(q["people"]) is { Count: > 0 } people ? people : null,
            MinRating = int.TryParse(q["rating"], out var rating) ? Math.Clamp(rating, 0, 5) : 0,
            MotionOnly = q["live"] == "1",
        };
        bool? pictures = null;
        bool? screenshots = null; // the section's own choice, unless the screenshots filter says otherwise
        switch (q["section"].ToString())
        {
            case "favorites":
                filter = filter with { MinRating = Math.Max(4, filter.MinRating) };
                screenshots = true;
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
                    return await BadAsync(ctx, "Which day? (month-day)");
                filter = filter with { MonthDay = (month, date), To = DateTime.Today.AddDays(2) };
                break;
            case "person" when long.TryParse(q["id"], out var personId):
                filter = filter with { PersonId = personId };
                screenshots = true;
                break;
            case "album" when long.TryParse(q["id"], out var albumId):
                filter = filter with { AlbumId = albumId };
                break;
            case "tag" when long.TryParse(q["id"], out var tagId):
                filter = filter with { TagId = tagId };
                screenshots = true;
                break;
            case "folder" when long.TryParse(q["id"], out var folderId):
                filter = filter with { FolderId = folderId, IncludeSubfolders = q["sub"] != "0" };
                break;
            case "blurry":
                filter = filter with { SharpnessBelow = Sharpness.BlurryBelow, Order = MediaOrder.Blurriest };
                break;
            case "area":
                if (!double.TryParse(q["s"], System.Globalization.CultureInfo.InvariantCulture, out var s) ||
                    !double.TryParse(q["w"], System.Globalization.CultureInfo.InvariantCulture, out var w) ||
                    !double.TryParse(q["n"], System.Globalization.CultureInfo.InvariantCulture, out var n) ||
                    !double.TryParse(q["e"], System.Globalization.CultureInfo.InvariantCulture, out var e))
                    return await BadAsync(ctx, "Which area? (s, w, n, e)");
                filter = filter with { Bounds = (s, w, n, e) };
                screenshots = true;
                break;
            case "search":
                var text = q["q"].ToString().Trim();
                if (text.Length is 0 or > 500) return await BadAsync(ctx, "What to search for?");
                var (ids, matchedPictures) = await _library.SearchAsync(text, q["exact"] == "1", ctx.RequestAborted);
                pictures = matchedPictures;
                filter = filter with
                {
                    Ids = ids,
                    Order = q["sort"].ToString() switch { "newest" => MediaOrder.Newest, "oldest" => MediaOrder.Oldest, _ => MediaOrder.Listed },
                };
                screenshots = true;
                break;
            case "timeline" or "":
                break;
            default:
                return await BadAsync(ctx, "No such section.");
        }
        filter = q["screenshots"].ToString() switch
        {
            "only" => filter with { ScreenshotsOnly = true, IncludeScreenshots = true },
            "with" => filter with { IncludeScreenshots = true },
            "hide" => filter with { IncludeScreenshots = false },
            _ => filter with { IncludeScreenshots = screenshots ?? MediaFilter.Timeline.IncludeScreenshots },
        };
        return (filter, pictures);
    }

    private static async Task<(MediaFilter, bool?)?> BadAsync(HttpContext ctx, string error)
    {
        await WriteJsonAsync(ctx, new { error }, StatusCodes.Status400BadRequest);
        return null;
    }

    /// <summary>
    /// A list as three parallel arrays (ids, dates and flags), small enough for the whole timeline: flags are 1 = video,
    /// 2 = Live Photo, 4·rating, and 32·seconds for a video's length.
    /// </summary>
    private async Task WriteItemsAsync(HttpContext ctx, MediaFilter filter, bool? pictures)
    {
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

    private object DuplicatesJson()
    {
        var scan = _library.Duplicates;
        return new
        {
            running = scan.IsRunning,
            progress = scan.Progress is { } p ? new { stage = p.Stage, done = p.Done, total = p.Total } : null,
            finished = scan.Finished?.ToString("s"),
            error = scan.Error,
            groups = scan.Groups?.Select(g => new
            {
                kind = g.Kind.ToString().ToLowerInvariant(),
                reclaimable = g.ReclaimableBytes,
                members = g.Members.Select(m => new
                {
                    id = m.Id,
                    name = m.FileName,
                    size = m.FileSize,
                    width = m.Width,
                    height = m.Height,
                    taken = m.TakenLocal.ToString("s"),
                    video = m.Kind == MediaKind.Video,
                    keep = m.IsSuggestedKeep,
                }),
            }),
        };
    }

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
}
