using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PhotoGallery.Core;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Remote;

// Editing from another computer: photo edits previewed and saved on the host (with the app's own renderer, so the
// result is the same as editing there), frames saved from videos and Live Photos, and videos trimmed and saved.
public sealed partial class RemoteServer
{
    private readonly ConcurrentDictionary<string, VideoJob> _jobs = new();

    /// <summary>Edits as the app writes them (Rotation, Crop…), or as a browser would (rotation, crop…).</summary>
    private static readonly JsonSerializerOptions OpsJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record EditRequest(JsonElement? Ops, int? Size, bool? Crop, string? Mode);
    private sealed record FrameRequest(double? Seconds);
    private sealed record VideoEditRequest(int? Rotation, double? Start, double? End, bool? Mute);

    private void MapEditing(WebApplication app)
    {
        app.MapGet("/api/media/{id:long}/edits", async ctx =>
        {
            if (await PhotoOrNotFoundAsync(ctx) is not { } item) return;
            using var ops = JsonDocument.Parse(_library.GetEdits(item).ToJson());
            await WriteJsonAsync(ctx, new { ops = ops.RootElement.Clone(), canOverwrite = _library.CanOverwrite(item) });
        });
        app.MapPost("/api/media/{id:long}/preview", async ctx =>
        {
            var body = await ReadAsync<EditRequest>(ctx);
            if (await PhotoOrNotFoundAsync(ctx) is not { } item) return;
            var ops = OpsOf(body) ?? EditOperations.None;
            if (body?.Crop == false) ops = ops with { Crop = null }; // choosing the crop: the whole picture
            var size = Math.Clamp(body?.Size ?? 1600, 256, 2560);
            byte[]? jpeg;
            await _renderGate.WaitAsync(ctx.RequestAborted);
            try
            {
                jpeg = await _library.RenderEditAsync(item, ops, size, ctx.RequestAborted);
            }
            finally
            {
                _renderGate.Release();
            }
            if (jpeg is null)
            {
                await WriteJsonAsync(ctx, new { error = "This photo can't be edited here." }, StatusCodes.Status422UnprocessableEntity);
                return;
            }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentLength = jpeg.Length;
            await ctx.Response.Body.WriteAsync(jpeg, ctx.RequestAborted);
        });
        app.MapPost("/api/media/{id:long}/auto", async ctx =>
        {
            var body = await ReadAsync<EditRequest>(ctx);
            if (await PhotoOrNotFoundAsync(ctx) is not { } item) return;
            var ops = await _library.AutoAdjustAsync(item, OpsOf(body) ?? EditOperations.None, ctx.RequestAborted);
            using var json = JsonDocument.Parse(ops.ToJson());
            await WriteJsonAsync(ctx, new { ops = json.RootElement.Clone() });
        });
        MapChange(app, "/api/media/{id:long}/edit", async ctx =>
        {
            var body = await ReadAsync<EditRequest>(ctx);
            var mode = body?.Mode switch { "keep" => EditSave.Keep, "copy" => EditSave.Copy, "overwrite" => EditSave.Overwrite, _ => (EditSave?)null };
            if (OpsOf(body) is not { } ops || mode is null) return Bad("Which edits, and keep, copy or overwrite?");
            if (await Task.Run(() => _library.Get(IdOf(ctx))) is not { Kind: not MediaKind.Video } item) return Bad("No such photo.");
            try
            {
                var message = await _library.SaveEditAsync(item, ops, mode.Value);
                Log.Info($"Remote access: {ctx.Connection.RemoteIpAddress} saved edits to {item.Id} ({mode})");
                return new { message };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or COMException)
            {
                Log.Error($"Remote access: saving edits to {item.Path} failed", ex);
                return Bad($"Couldn't save: {ex.Message}");
            }
        });

        app.MapPost("/api/media/{id:long}/sharpest", async ctx =>
        {
            if (await ItemOrNotFoundAsync(ctx) is not { } item) return;
            var at = await _library.FindSharpestAsync(item, ctx.RequestAborted);
            if (at is null) await WriteJsonAsync(ctx, new { error = "It has no video to look through." }, StatusCodes.Status404NotFound);
            else await WriteJsonAsync(ctx, new { seconds = at.Value.TotalSeconds });
        });
        MapChange(app, "/api/media/{id:long}/frame", async ctx =>
        {
            var body = await ReadAsync<FrameRequest>(ctx);
            if (body?.Seconds is not double seconds || seconds is < 0 or >= 86_400) return Bad("Which moment of the video?");
            if (await Task.Run(() => _library.Get(IdOf(ctx))) is not { } item) return Bad("No such item.");
            try
            {
                return new { message = await _library.SaveFrameAsync(item, TimeSpan.FromSeconds(seconds)) };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                Log.Error($"Remote access: saving a frame of {item.Path} failed", ex);
                return Bad($"Couldn't save the frame: {ex.Message}");
            }
        });
        MapChange(app, "/api/media/{id:long}/export", async ctx =>
        {
            var body = await ReadAsync<VideoEditRequest>(ctx);
            if (body is null) return Bad("Which edits?");
            if (await Task.Run(() => _library.Get(IdOf(ctx))) is not { } item) return Bad("No such item.");
            var start = TimeSpan.FromSeconds(Math.Max(0, body.Start ?? 0));
            TimeSpan? end = body.End is { } e && e > 0 ? TimeSpan.FromSeconds(e) : null;
            if (end <= start) return Bad("The end has to come after the start.");
            var edits = new VideoEdits { Rotation = (((body.Rotation ?? 0) % 360) + 360) % 360 / 90 * 90, TrimStart = start, TrimEnd = end, Mute = body.Mute == true };
            foreach (var old in _jobs.Where(j => j.Value.Done).Select(j => j.Key).ToList()) _jobs.TryRemove(old, out _);
            var job = _library.StartVideoExport(item, edits);
            _jobs[job.Id] = job;
            Log.Info($"Remote access: {ctx.Connection.RemoteIpAddress} started saving a video of {item.Id}");
            return new { job = job.Id };
        });
        app.MapGet("/api/jobs/{job}", async ctx =>
        {
            if (!_jobs.TryGetValue(ctx.Request.RouteValues["job"]?.ToString() ?? "", out var job))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await WriteJsonAsync(ctx, new { progress = job.Progress, done = job.Done, message = job.Message, error = job.Error });
        });
        app.MapPost("/api/jobs/{job}/cancel", async ctx =>
        {
            if (_jobs.TryGetValue(ctx.Request.RouteValues["job"]?.ToString() ?? "", out var job)) job.Cancellation.Cancel();
            await WriteJsonAsync(ctx, new { });
        });
    }

    private async Task<MediaItem?> PhotoOrNotFoundAsync(HttpContext ctx)
    {
        if (await ItemOrNotFoundAsync(ctx) is not { } item) return null;
        if (item.Kind != MediaKind.Video) return item;
        await WriteJsonAsync(ctx, new { error = "Videos are trimmed, not edited like photos." }, StatusCodes.Status400BadRequest);
        return null;
    }

    /// <summary>The edits in a request (the app's own format), within their ranges; null if missing or unreadable.</summary>
    private static EditOperations? OpsOf(EditRequest? body)
    {
        if (body?.Ops is not { ValueKind: JsonValueKind.Object } json) return null;
        EditOperations ops;
        try
        {
            ops = json.Deserialize<EditOperations>(OpsJson) ?? EditOperations.None;
        }
        catch (JsonException)
        {
            return null;
        }
        return ops with
        {
            Rotation = ((ops.Rotation % 360) + 360) % 360 / 90 * 90,
            Crop = ops.Crop is { } crop && !crop.IsFull ? crop.Clamp() : null,
            Exposure = Math.Clamp(ops.Exposure, -2, 2),
            Brightness = Math.Clamp(ops.Brightness, -1, 1),
            Contrast = Math.Clamp(ops.Contrast, -1, 1),
            Saturation = Math.Clamp(ops.Saturation, -1, 1),
            Temperature = Math.Clamp(ops.Temperature, -1, 1),
            Tint = Math.Clamp(ops.Tint, -1, 1),
        };
    }
}
