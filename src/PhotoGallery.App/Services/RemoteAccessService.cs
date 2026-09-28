using System.Runtime.InteropServices;
using PhotoGallery.Core;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Transcripts;
using PhotoGallery.Remote;

namespace PhotoGallery.App.Services;

/// <summary>
/// Remote access, on the computer with the library: once it's turned on in Settings and a passphrase is set, other
/// computers on the network can browse, search and view the library, in a browser or in Photo Gallery's "Another
/// computer" page. The work (searching, decoding photos, fetching Live Photo videos from OneDrive) happens here.
/// </summary>
public sealed class RemoteAccessService(AppServices services) : IRemoteLibrary
{
    private readonly SemaphoreSlim _gate = new(1);
    private RemoteServer? _server;

    /// <summary>Raised (on any thread) after it starts, stops or fails to start.</summary>
    public event Action? StateChanged;

    public bool IsRunning => _server is not null;
    public string? Fingerprint => _server?.Fingerprint;
    public int Port => _server?.Port ?? services.Settings.RemotePort;
    public string? LastError { get; private set; }

    public bool HasPassphrase => Secret is not null;

    private RemoteSecret? Secret => services.Settings is { RemotePassphraseSalt: { } salt, RemotePassphraseKey: { } key, RemotePassphraseIterations: > 0 and var iterations }
        ? new RemoteSecret(salt, iterations, key)
        : null;

    /// <summary>Keeps the passphrase (as a key) and restarts, which signs every other computer out.</summary>
    public async Task SetPassphraseAsync(string passphrase)
    {
        var secret = await Task.Run(() => RemoteSecret.Create(passphrase));
        var settings = services.Settings;
        (settings.RemotePassphraseSalt, settings.RemotePassphraseIterations, settings.RemotePassphraseKey) = (secret.Salt, secret.Iterations, secret.Key);
        services.SaveSettings();
        await ApplyAsync();
    }

    /// <summary>Starts or stops to match the settings; restarting signs everyone out.</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopServerAsync();
            LastError = null;
            var settings = services.Settings;
            if (settings.RemoteEnabled && Secret is { } secret)
            {
                try
                {
                    var certificate = await Task.Run(HostCertificate.GetOrCreate);
                    _server = await RemoteServer.StartAsync(this, new RemoteServerOptions { Port = settings.RemotePort, Secret = secret, Certificate = certificate });
                    Log.Info($"Remote access: listening on port {_server.Port}");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException
                                               or System.Net.Sockets.SocketException or UnauthorizedAccessException)
                {
                    LastError = ex.Message;
                    Log.Error("Remote access didn't start", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopServerAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is not { } server) return;
        _server = null;
        await server.DisposeAsync();
    }

    // ---------- The library, as the server sees it ----------

    public string Name => Environment.MachineName;

    public List<MediaSummary> Query(MediaFilter filter) => services.Media.Query(filter);

    public async Task<(List<long> Ids, bool Pictures)> SearchAsync(string text, bool exact, CancellationToken ct)
    {
        if (!exact)
        {
            try
            {
                return await services.Similar.SearchAsync(text, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Log.Error($"Remote search for \"{text}\" couldn't match pictures", ex);
            }
        }
        return (await Task.Run(() => services.Media.SearchWords(text), ct), false);
    }

    public MediaItem? Get(long id) => services.Media.Get(id);

    public async Task<MediaDetails> GetDetailsAsync(MediaItem item, CancellationToken ct)
    {
        string? place = null;
        if (item is { Latitude: { } latitude, Longitude: { } longitude })
        {
            var (mine, spot, area) = await services.PlaceNames.DescribeAsync(item.Id, latitude, longitude);
            var names = new[] { mine?.Name, spot?.Name, area }.OfType<string>().Distinct().ToList();
            if (names.Count > 0) place = string.Join(" · ", names);
        }
        return await Task.Run(() =>
        {
            var people = services.People.GetFacesIn(item.Id)
                .Where(f => !f.Hidden)
                .GroupBy(f => f.PersonId)
                .Select(g => (g.Key, g.First().DisplayName))
                .ToList();
            var text = services.PhotoTexts.Get(item.Id) is { HasText: true } found ? found.Text : null;
            var transcript = services.Transcripts.Get(item.Id) is { HasSpeech: true } said ? TranscriptFormatter.Paragraphs(said.Segments) : null;
            return new MediaDetails(place, people, text, transcript, services.Edits.Get(item.Id) is not null);
        }, ct);
    }

    public List<PersonRow> GetPeople() => services.People.GetPeople();

    public List<AlbumRow> GetAlbums() => services.Collections.GetAlbums();

    public void SetRating(long id, int rating) => services.Media.SetRating([id], rating);

    public Task<string?> GetThumbnailAsync(MediaItem item, CancellationToken ct) => services.Thumbnails.GetOrCreateAsync(item.Id, item.Path, ct);

    /// <summary>Their face as OneDrive found it (or the clearest among a few photos), else their cover photo's thumbnail.</summary>
    public async Task<string?> GetFaceAsync(long personId, CancellationToken ct)
    {
        var candidates = services.People.GetCoverCandidates(personId, 8)
            .Select(c => (Path: services.Media.GetPath(c.MediaId), c.Box))
            .Where(c => c.Path is not null)
            .Select(c => (c.Path!, c.Box))
            .ToList();
        if (await services.Faces.GetOrCreateAsync(personId, candidates) is { } face) return face;
        return services.People.Get(personId)?.CoverMediaId is { } cover && services.Media.GetPath(cover) is { } path
            ? await services.Thumbnails.GetOrCreateAsync(cover, path, ct)
            : null;
    }

    public async Task<byte[]?> RenderAsync(MediaItem item, int maxSize, CancellationToken ct)
    {
        try
        {
            // Edits kept in the gallery are shown, as they are here.
            if (item.Kind != MediaKind.Video && services.Edits.Get(item.Id) is { } ops)
            {
                using var edited = await Editing.EditRenderer.RenderPreviewAsync(item.Path, ops, maxSize);
                return await DisplayRenderer.EncodeAsync(edited);
            }
            return await DisplayRenderer.RenderAsync(item, maxSize, ct);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Remote access: couldn't render {item.Path}", ex);
            return null;
        }
    }

    public Task<(MotionResult Result, string? Path)> GetMotionAsync(MediaItem item, CancellationToken ct) => services.Motion.GetVideoAsync(item, ct);
}
