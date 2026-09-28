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
    /// <summary>Look up park, school, restaurant… names from OpenStreetMap (sends the rough areas where photos were taken).</summary>
    public bool NamePlacesFromOsm { get; set; }
    /// <summary>"Dark" (the default), "Light", or "System" (follow Windows).</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>The last searches, most recent first (offered in the search box).</summary>
    public List<string> RecentSearches { get; set; } = [];
    /// <summary>Ask before deleting photos (they go to the Recycle Bin either way).</summary>
    public bool ConfirmDelete { get; set; } = true;
    /// <summary>Make CLIP embeddings of the whole library in the background (Similar photos, searching by description).</summary>
    public bool FindSimilarInBackground { get; set; } = true;
    /// <summary>Remote access (host): other computers on the network may open this library, with the passphrase.</summary>
    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 47813;
    /// <summary>Other computers may delete (to this PC's Recycle Bin), tag, and manage albums and people, not only look and rate.</summary>
    public bool RemoteAllowChanges { get; set; }
    /// <summary>The passphrase as a salted PBKDF2 key (see PhotoGallery.Remote.RemoteSecret); never the passphrase itself.</summary>
    public string? RemotePassphraseSalt { get; set; }
    public int RemotePassphraseIterations { get; set; }
    public string? RemotePassphraseKey { get; set; }
    /// <summary>Remote access (client): the other computer last connected to, as typed ("GRANT-PC" or "192.168.1.20:47813").</summary>
    public string? RemoteComputer { get; set; }
    /// <summary>Open the other computer's photos when the app starts (for a computer that only looks at another's library).</summary>
    public bool RemoteOpenAtStart { get; set; }
    /// <summary>The certificate fingerprint accepted for each other computer ("host:port"), checked on every connection.</summary>
    public Dictionary<string, string> RemotePins { get; set; } = [];
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
