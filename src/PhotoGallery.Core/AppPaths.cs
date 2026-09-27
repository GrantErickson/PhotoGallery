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
    /// <summary>Speech models (downloaded on first use; ~1.6 GB).</summary>
    public string Models => Path.Combine(Root, "models");
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
    /// <summary>The OneDrive web session (for Live Photo videos) has been connected. The token itself is never stored.</summary>
    public bool OneDriveWebConnected { get; set; }
    /// <summary>Transcribe every video in the background (the one being watched is always done on request).</summary>
    public bool TranscribeInBackground { get; set; } = true;
    /// <summary>Read the text in every photo (OCR) in the background (the one being viewed is always read).</summary>
    public bool ReadPhotoTextInBackground { get; set; } = true;
    /// <summary>Make CLIP embeddings of the whole library in the background (Similar photos, searching by description).</summary>
    public bool FindSimilarInBackground { get; set; } = true;
    /// <summary>Grid tile edge in DIPs.</summary>
    public double TileSize { get; set; } = 220;
    /// <summary>Bumped when a default changes, so saved settings can move to it once.</summary>
    public int Version { get; set; }

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
        settings ??= new AppSettings { Version = 2 };
        if (settings.Version < 2)
        {
            if (settings.TileSize <= 180) settings.TileSize = 220; // tiles got bigger by default
            settings.Version = 2;
        }
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
