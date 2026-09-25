using System.Text.Json;

namespace PhotoGallery.Core;

/// <summary>Where the app keeps its database, caches and settings (%LocalAppData%\PhotoGallery).</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoGallery"));

    public string Root { get; } = root;
    public string Database => Path.Combine(Root, "gallery.db");
    public string Thumbnails => Path.Combine(Root, "thumbs");
    public string MotionCache => Path.Combine(Root, "motion");
    public string TokenCache => Path.Combine(Root, "auth");
    public string Settings => Path.Combine(Root, "settings.json");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Thumbnails, MotionCache, TokenCache }) Directory.CreateDirectory(dir);
    }
}

public sealed class AppSettings
{
    /// <summary>Azure app registration (personal Microsoft accounts, public client, Files.Read).</summary>
    public const string DefaultClientId = "5f0b2132-a2b1-45ef-9f3c-2ffc26fe24b5";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> LibraryRoots { get; set; } = [];
    /// <summary>Local OneDrive sync root; library paths under it map to OneDrive paths for Graph.</summary>
    public string? OneDriveRoot { get; set; }
    public string ClientId { get; set; } = DefaultClientId;
    public bool IncludeScreenshots { get; set; }
    /// <summary>Grid tile edge in DIPs.</summary>
    public double TileSize { get; set; } = 180;

    public static AppSettings Load(AppPaths paths)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(paths.Settings))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.Settings));
        }
        catch (JsonException)
        {
        }
        settings ??= new AppSettings();
        var oneDrive = Environment.GetEnvironmentVariable("OneDriveConsumer") ?? Environment.GetEnvironmentVariable("OneDrive");
        settings.OneDriveRoot ??= oneDrive;
        if (settings.LibraryRoots.Count == 0 && oneDrive is not null)
        {
            var pictures = Path.Combine(oneDrive, "Pictures");
            if (Directory.Exists(pictures)) settings.LibraryRoots.Add(pictures);
        }
        return settings;
    }

    public void Save(AppPaths paths) => File.WriteAllText(paths.Settings, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>"D:\OneDrive\Pictures\a.jpg" → "Pictures/a.jpg", or null if the file is not under the OneDrive root.</summary>
    public string? ToOneDrivePath(string localPath)
    {
        if (string.IsNullOrEmpty(OneDriveRoot)) return null;
        var root = Path.TrimEndingDirectorySeparator(OneDriveRoot) + Path.DirectorySeparatorChar;
        return localPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? localPath[root.Length..].Replace(Path.DirectorySeparatorChar, '/')
            : null;
    }
}
