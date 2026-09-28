using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PhotoGallery.Core;

namespace PhotoGallery.Remote;

// Changing the library from another computer. Ratings are always allowed; everything else only while the host's
// Settings allow changes (read on every request), and each change is logged on the host.
public sealed partial class RemoteServer
{
    private sealed record RatingRequest(List<long>? Ids, int? Rating);
    private sealed record TagRequest(List<long>? Ids, string? Name, long? TagId);
    private sealed record NameRequest(string? Name);
    private sealed record HiddenRequest(bool Hidden);
    private sealed record MergeRequest(long Into);

    private void MapChanging(WebApplication app)
    {
        app.MapPost("/api/media/{id:long}/rating", async ctx =>
        {
            var body = await ReadAsync<RatingRequest>(ctx);
            if (body?.Rating is not int rating || rating is < 0 or > 5)
            {
                await WriteJsonAsync(ctx, new { error = "A rating is 0 to 5." }, StatusCodes.Status400BadRequest);
                return;
            }
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            _library.SetRating([item.Id], rating);
            await WriteJsonAsync(ctx, new { rating });
        });
        app.MapPost("/api/media/rating", async ctx =>
        {
            var body = await ReadAsync<RatingRequest>(ctx);
            if (body?.Rating is not int rating || rating is < 0 or > 5 || !IdsOk(body.Ids))
            {
                await WriteJsonAsync(ctx, new { error = "Which items, and a rating of 0 to 5." }, StatusCodes.Status400BadRequest);
                return;
            }
            _library.SetRating(body.Ids!, rating);
            await WriteJsonAsync(ctx, new { rating, count = body.Ids!.Count });
        });

        MapChange(app, "/api/media/delete", async ctx =>
        {
            var body = await ReadAsync<IdsRequest>(ctx);
            if (!IdsOk(body?.Ids)) return Bad("Which items?");
            var (deleted, failed) = await _library.DeleteAsync(body!.Ids!);
            _library.Duplicates.Forget(deleted);
            Log.Info($"Remote access: {ctx.Connection.RemoteIpAddress} deleted {deleted.Count} item(s)" + (failed.Count > 0 ? $", {failed.Count} failed" : ""));
            return new { deleted, failed };
        });
        MapChange(app, "/api/tags/add", async ctx =>
        {
            var body = await ReadAsync<TagRequest>(ctx);
            var name = body?.Name?.Trim();
            if (!IdsOk(body?.Ids) || string.IsNullOrEmpty(name) || name.Length > 100) return Bad("Which items, and a tag of up to 100 characters.");
            _library.AddTag(body!.Ids!, name);
            return new { };
        });
        MapChange(app, "/api/tags/remove", async ctx =>
        {
            var body = await ReadAsync<TagRequest>(ctx);
            if (!IdsOk(body?.Ids) || body!.TagId is not { } tagId) return Bad("Which items, and which tag?");
            _library.RemoveTag(body.Ids!, tagId);
            return new { };
        });
        MapChange(app, "/api/albums", async ctx =>
        {
            var name = (await ReadAsync<NameRequest>(ctx))?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) return Bad("A name of up to 200 characters.");
            return new { id = _library.CreateAlbum(name) };
        });
        MapChange(app, "/api/albums/{id:long}/rename", async ctx =>
        {
            var name = (await ReadAsync<NameRequest>(ctx))?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) return Bad("A name of up to 200 characters.");
            _library.RenameAlbum(IdOf(ctx), name);
            return new { };
        });
        MapChange(app, "/api/albums/{id:long}/delete", ctx =>
        {
            _library.DeleteAlbum(IdOf(ctx)); // the album only; its photos stay in the library
            return Task.FromResult<object>(new { });
        });
        MapChange(app, "/api/albums/{id:long}/add", async ctx =>
        {
            var body = await ReadAsync<IdsRequest>(ctx);
            if (!IdsOk(body?.Ids)) return Bad("Which items?");
            _library.AddToAlbum(IdOf(ctx), body!.Ids!);
            return new { };
        });
        MapChange(app, "/api/albums/{id:long}/remove", async ctx =>
        {
            var body = await ReadAsync<IdsRequest>(ctx);
            if (!IdsOk(body?.Ids)) return Bad("Which items?");
            _library.RemoveFromAlbum(IdOf(ctx), body!.Ids!);
            return new { };
        });
        MapChange(app, "/api/people/{id:long}/rename", async ctx =>
        {
            var name = (await ReadAsync<NameRequest>(ctx))?.Name?.Trim();
            if (name is { Length: > 200 }) return Bad("A name of up to 200 characters.");
            _library.RenamePerson(IdOf(ctx), string.IsNullOrEmpty(name) ? null : name);
            return new { };
        });
        MapChange(app, "/api/people/{id:long}/hide", async ctx =>
        {
            var body = await ReadAsync<HiddenRequest>(ctx);
            if (body is null) return Bad("Hidden or not?");
            _library.HidePerson(IdOf(ctx), body.Hidden);
            return new { };
        });
        MapChange(app, "/api/people/{id:long}/merge", async ctx =>
        {
            var body = await ReadAsync<MergeRequest>(ctx);
            var source = IdOf(ctx);
            if (body is null || body.Into <= 0 || body.Into == source) return Bad("Merge into whom?");
            _library.MergePeople(source, body.Into);
            Log.Info($"Remote access: {ctx.Connection.RemoteIpAddress} merged person {source} into {body.Into}");
            return new { };
        });
    }

    private static bool IdsOk(List<long>? ids) => ids is { Count: > 0 and <= 100_000 };

    /// <summary>A bad request, returned from a change's handler.</summary>
    private sealed record BadRequest(string Error);

    private static BadRequest Bad(string error) => new(error);

    /// <summary>A change: refused while the host doesn't allow changes; a BadRequest result becomes a 400.</summary>
    private void MapChange(WebApplication app, string pattern, Func<HttpContext, Task<object>> handler) =>
        app.MapPost(pattern, async ctx =>
        {
            if (!_library.AllowChanges)
            {
                await WriteJsonAsync(ctx, new { error = $"Changes from other computers are turned off on {_library.Name} (Settings › Remote access)." },
                    StatusCodes.Status403Forbidden);
                return;
            }
            var result = await handler(ctx);
            if (result is BadRequest bad) await WriteJsonAsync(ctx, new { error = bad.Error }, StatusCodes.Status400BadRequest);
            else await WriteJsonAsync(ctx, result);
        });
}
