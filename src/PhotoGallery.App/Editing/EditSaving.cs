using PhotoGallery.App.Services;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;

namespace PhotoGallery.App.Editing;

/// <summary>
/// Writing a photo's edits: keep them in the gallery only, save a copy next to the original, or overwrite the original
/// (the current version goes to the Recycle Bin first). Used by the editor, and by other computers through remote
/// access, so both do exactly the same.
/// </summary>
public static class EditSaving
{
    /// <summary>Non-destructive: the edits are kept in the gallery and no file changes.</summary>
    public static void Keep(AppServices services, MediaItem item, EditOperations ops)
    {
        services.Edits.Save(item.Id, ops);
        services.Thumbnails.Invalidate([item.Id]);
    }

    /// <summary>Writes "&lt;name&gt;_N.jpg" next to the original, indexes it and links it to the original; returns its path.</summary>
    public static async Task<string> SaveCopyAsync(AppServices services, MediaItem item, EditOperations ops)
    {
        var target = NextCopyPath(item.Path);
        await EditRenderer.WriteFileAsync(item.Path, ops, target, item.TakenLocal, Location(item));
        var copy = await Task.Run(() => services.Indexing.IndexFileNow(target));
        if (copy is not null) services.Media.SetDerivedFrom(copy.Id, item.Id);
        // The copy carries the edits; the original goes back to showing itself.
        services.Edits.Save(item.Id, EditOperations.None);
        services.Thumbnails.Invalidate([item.Id]);
        return target;
    }

    /// <summary>Replaces the original with the edited photo; the current version goes to the Recycle Bin first.</summary>
    public static async Task OverwriteAsync(AppServices services, MediaItem item, EditOperations ops)
    {
        if (!EditRenderer.CanWriteFormat(item.Path))
            throw new InvalidOperationException($"{Path.GetExtension(item.Path).ToUpperInvariant()} files can't be overwritten; save a copy instead.");
        var temp = Path.Combine(Path.GetDirectoryName(item.Path)!, $".{Guid.NewGuid():N}{Path.GetExtension(item.Path)}");
        await EditRenderer.WriteFileAsync(item.Path, ops, temp, item.TakenLocal, Location(item));
        if (!RecycleBin.Recycle(item.Path))
        {
            File.Delete(temp);
            throw new IOException("The original couldn't be moved to the Recycle Bin, so it was left unchanged.");
        }
        File.Move(temp, item.Path);
        services.Edits.Save(item.Id, EditOperations.None);
        services.Thumbnails.Invalidate([item.Id]);
        services.Indexing.RequestIndex();
    }

    /// <summary>"D:\x\IMG_0840.HEIC" → "D:\x\IMG_0840_1.jpg" (the next free number).</summary>
    public static string NextCopyPath(string original)
    {
        var folder = Path.GetDirectoryName(original)!;
        var name = Path.GetFileNameWithoutExtension(original);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name}_{n}.jpg");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static (double, double)? Location(MediaItem item) =>
        item is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null;
}
