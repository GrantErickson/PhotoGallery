namespace PhotoGallery.Core.Media;

/// <summary>Maps file extensions to how the library treats them. Built from the library census in docs/plan.md.</summary>
public static class MediaFormats
{
    private static readonly Dictionary<string, MediaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Photo,
        [".jpeg"] = MediaKind.Photo,
        [".heic"] = MediaKind.Photo,
        [".heif"] = MediaKind.Photo,
        [".png"] = MediaKind.Photo,
        [".bmp"] = MediaKind.Photo,
        [".gif"] = MediaKind.Photo,
        [".webp"] = MediaKind.Photo,
        [".tif"] = MediaKind.Photo,
        [".tiff"] = MediaKind.Photo,
        [".psd"] = MediaKind.Photo,
        [".cr2"] = MediaKind.Raw,
        [".cr3"] = MediaKind.Raw,
        [".nef"] = MediaKind.Raw,
        [".arw"] = MediaKind.Raw,
        [".dng"] = MediaKind.Raw,
        [".mov"] = MediaKind.Video,
        [".mp4"] = MediaKind.Video,
        [".m4v"] = MediaKind.Video,
        [".avi"] = MediaKind.Video,
        [".mts"] = MediaKind.Video,
        [".m2ts"] = MediaKind.Video,
        [".mpg"] = MediaKind.Video,
        [".mpeg"] = MediaKind.Video,
        [".wmv"] = MediaKind.Video,
        [".mod"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
    };

    public static bool TryGetKind(string pathOrExtension, out MediaKind kind)
    {
        var ext = pathOrExtension.StartsWith('.') ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return Kinds.TryGetValue(ext, out kind);
    }

    /// <summary>ISO-BMFF / QuickTime containers we can parse ourselves without reading the whole file.</summary>
    public static bool IsQuickTimeFamily(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mov" or ".mp4" or ".m4v" or ".3gp";
}
