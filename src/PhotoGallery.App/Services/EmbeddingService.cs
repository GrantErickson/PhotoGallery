using System.Globalization;
using PhotoGallery.Core;
using PhotoGallery.Core.Similarity;

namespace PhotoGallery.App.Services;

/// <summary>
/// Similar photos and searching by description, with OpenAI's CLIP ViT-L/14: every photo and video gets an embedding
/// from its thumbnail, in the background on the graphics card (<see cref="EmbedderClient"/>), newest first. Searches
/// compare embeddings in memory. The model (about 860 MB) is downloaded from Hugging Face on first use. Once every
/// item has one, photos are scored as utility shots or not (<see cref="UtilityShots"/>). Events are raised on a
/// background thread.
/// </summary>
public sealed class EmbeddingService(AppServices services) : IDisposable
{
    private const string Repo = "https://huggingface.co/Xenova/clip-vit-large-patch14/resolve/main/";
    private const int Batch = 16;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly (string Name, string Url, long Bytes)[] ModelFiles =
    [
        ("vision_model_fp16.onnx", Repo + "onnx/vision_model_fp16.onnx", 608_700_000),
        ("text_model_fp16.onnx", Repo + "onnx/text_model_fp16.onnx", 247_800_000),
        ("vocab.json", Repo + "vocab.json", 0),
        ("merges.txt", Repo + "merges.txt", 0),
    ];

    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _indexGate = new(1);
    private EmbedderClient? _embedder;
    private ClipTokenizer? _tokenizer;
    private SimilarityIndex? _index;
    private int _started;
    private DateTime _lastUse;
    private (sbyte[][] Records, sbyte[][] Memories)? _utilityDescriptions;

    public event Action? StateChanged;

    private string Folder => Path.Combine(services.Paths.Models, "clip-vit-large-patch14");
    private string ModelPath(string name) => Path.Combine(Folder, name);

    public bool IsInstalled => EmbedderClient.IsInstalled;
    public bool ModelsReady => ModelFiles.All(f => File.Exists(ModelPath(f.Name)));
    public bool IsWorking { get; private set; }
    /// <summary>0–1 while the model downloads.</summary>
    public double? DownloadProgress { get; private set; }
    public string? LastError { get; private set; }
    public string? Device => _embedder?.Device;

    /// <summary>Items that can be compared right now.</summary>
    public int Searchable => _index?.Count ?? 0;

    public void Start()
    {
        if (!IsInstalled || Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Wakes the background work (after it was switched on, or new photos arrived).</summary>
    public void Nudge()
    {
        if (_started == 1) _wake.Release();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The items most like this one (most similar first), not including it. One the background work hasn't reached
    /// yet is compared now; empty if it can't be (no thumbnail).
    /// </summary>
    public async Task<List<(long Id, double Similarity)>> FindSimilarAsync(long mediaId, int count = 200)
    {
        var index = await IndexAsync();
        if (!index.Contains(mediaId) && services.Media.GetPath(mediaId) is { } path)
        {
            _lastUse = DateTime.UtcNow;
            if (await services.Thumbnails.GetOrCreateAsync(mediaId, path) is { } thumbnail &&
                (await Embedder.EmbedImagesAsync([thumbnail]))[0] is { } raw)
            {
                var vector = Embedding.Quantize(raw);
                services.Embeddings.Save([(mediaId, vector)]);
                index.Add(mediaId, vector);
            }
        }
        var query = index.VectorOf(mediaId).ToArray();
        return query.Length == 0 ? [] : index.Search(query, count, minSimilarity: 0.55, except: mediaId);
    }

    /// <summary>
    /// A search's best matches, best first: photos and videos that look like the words (CLIP) together with those
    /// whose names, folders, tags, people, places, text or speech contain them, which rank higher the better they look
    /// the part too. Only word matches (in their own order) while the model isn't ready; Pictures says which it was.
    /// </summary>
    public async Task<(List<long> Ids, bool Pictures)> SearchAsync(string text, CancellationToken ct = default)
    {
        const double WordBonus = 0.1, MinSimilarity = 0.19;
        var words = await Task.Run(() => services.Media.SearchWords(text), ct);
        if (!ModelsReady || !IsInstalled) return (words, false);
        _tokenizer ??= ClipTokenizer.Load(ModelPath("vocab.json"), ModelPath("merges.txt"));
        var query = Embedding.Quantize(await Embedder.EmbedTextAsync(_tokenizer.Encode(text), ct));
        _lastUse = DateTime.UtcNow;
        var index = await IndexAsync();
        var scores = index.Search(query, 400, MinSimilarity).ToDictionary(r => r.Id, r => r.Similarity);
        for (var rank = 0; rank < words.Count; rank++)
        {
            // Not compared yet: placed just above the picture-only matches, in word order.
            var similarity = index.SimilarityOf(query, words[rank]) ?? MinSimilarity - rank * 1e-7;
            scores[words[rank]] = similarity + WordBonus;
        }
        return (scores.OrderByDescending(s => s.Value).Select(s => s.Key).ToList(), true);
    }

    private EmbedderClient Embedder => _embedder ??= new EmbedderClient(ModelPath("vision_model_fp16.onnx"), ModelPath("text_model_fp16.onnx"));

    private async Task<SimilarityIndex> IndexAsync()
    {
        if (_index is { } ready) return ready;
        await _indexGate.WaitAsync();
        try
        {
            return _index ??= await Task.Run(services.Embeddings.LoadIndex);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            if (!services.Settings.FindSimilarInBackground)
            {
                await StopEmbedderAsync();
                await _wake.WaitAsync();
                continue;
            }
            if (!await EnsureModelsAsync())
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(10)); // try the download again later
                continue;
            }
            var index = await IndexAsync();
            var backlog = services.Embeddings.GetBacklog(Batch * 8);
            if (backlog.Count == 0)
            {
                await ScoreUtilityAsync(index);
                IsWorking = false;
                StateChanged?.Invoke();
                // Keep the model loaded a while for searches, then give the graphics card its memory back.
                while (!await _wake.WaitAsync(TimeSpan.FromMinutes(1)))
                    if (DateTime.UtcNow - _lastUse > TimeSpan.FromMinutes(5)) await StopEmbedderAsync();
                continue;
            }

            IsWorking = true;
            StateChanged?.Invoke();
            try
            {
                foreach (var chunk in backlog.Chunk(Batch))
                {
                    // Thumbnails: made at background priority if they're missing.
                    var thumbnails = await Task.WhenAll(chunk.Select(item => services.Thumbnails.GetOrCreateAsync(item.Id, item.Path, background: true)));
                    var readable = Enumerable.Range(0, chunk.Length).Where(i => thumbnails[i] is not null).ToList();
                    var vectors = readable.Count == 0 ? [] : await Embedder.EmbedImagesAsync(readable.Select(i => thumbnails[i]!).ToList());
                    var results = chunk.Select(item => (item.Id, (sbyte[]?)null)).ToArray();
                    for (var k = 0; k < readable.Count; k++)
                        if (vectors[k] is { } v) results[readable[k]].Item2 = Embedding.Quantize(v);
                    services.Embeddings.Save(results);
                    foreach (var (id, vector) in results)
                        if (vector is not null) index.Add(id, vector);
                    _lastUse = DateTime.UtcNow;
                }
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Log.Error("Making embeddings failed", ex);
                LastError = ex.Message;
                await StopEmbedderAsync();
                StateChanged?.Invoke();
                await _wake.WaitAsync(TimeSpan.FromMinutes(5));
            }
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Scores the photos that have an embedding but no utility score (all of them when the way of scoring changed),
    /// and has views re-query if any turned out to be utility shots.
    /// </summary>
    private async Task ScoreUtilityAsync(SimilarityIndex index)
    {
        const string VersionKey = "UtilityShotsVersion";
        try
        {
            var version = UtilityShots.Version.ToString(CultureInfo.InvariantCulture);
            if (services.Media.GetSyncValue(VersionKey) != version)
            {
                await Task.Run(services.Media.ResetUtility);
                services.Media.SetSyncValue(VersionKey, version);
            }
            var backlog = await Task.Run(() => services.Media.GetUtilityBacklog());
            if (backlog.Count == 0) return;
            if (_utilityDescriptions is not { } descriptions)
            {
                var tokenizer = _tokenizer ??= ClipTokenizer.Load(ModelPath("vocab.json"), ModelPath("merges.txt"));
                async Task<sbyte[][]> EmbedAsync(string[] texts)
                {
                    var vectors = new sbyte[texts.Length][];
                    for (var i = 0; i < texts.Length; i++) vectors[i] = Embedding.Quantize(await Embedder.EmbedTextAsync(tokenizer.Encode(texts[i])));
                    return vectors;
                }
                _utilityDescriptions = descriptions = (await EmbedAsync(UtilityShots.Records), await EmbedAsync(UtilityShots.Memories));
                _lastUse = DateTime.UtcNow;
            }
            var scores = await Task.Run(() => backlog
                .AsParallel()
                .Select(item =>
                {
                    var vector = index.VectorOf(item.Id);
                    return (item.Id, Score: vector.IsEmpty ? double.NaN : UtilityShots.Score(vector, descriptions.Records, descriptions.Memories, item.Words, item.Faces));
                })
                .Where(s => !double.IsNaN(s.Score))
                .ToList());
            await Task.Run(() => services.Media.SetUtility(scores));
            var found = scores.Count(s => s.Score >= UtilityShots.Threshold);
            Log.Info($"Scored {scores.Count:N0} photos as utility shots or not: {found:N0} are");
            if (found > 0) services.Indexing.RaiseLibraryChanged();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Scoring utility shots failed", ex);
        }
    }

    private async Task StopEmbedderAsync()
    {
        if (_embedder is { } embedder) await embedder.StopWhenIdleAsync();
    }

    private async Task<bool> EnsureModelsAsync()
    {
        if (ModelsReady) return true;
        try
        {
            Directory.CreateDirectory(Folder);
            var total = ModelFiles.Sum(f => f.Bytes);
            long before = 0;
            foreach (var (name, url, bytes) in ModelFiles)
            {
                var path = ModelPath(name);
                if (!File.Exists(path))
                {
                    var done = before;
                    await DownloadAsync(url, path, read =>
                    {
                        DownloadProgress = Math.Min(1, (done + read) / (double)total);
                        StateChanged?.Invoke();
                    });
                }
                before += bytes;
            }
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Error("Downloading the CLIP model failed", ex);
            LastError = $"couldn't download the model ({ex.Message})";
            return false;
        }
        finally
        {
            DownloadProgress = null;
            StateChanged?.Invoke();
        }
    }

    private static async Task DownloadAsync(string url, string path, Action<long> progress)
    {
        var partial = path + ".partial";
        await using (var source = await Http.GetStreamAsync(url))
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long total = 0, reported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                total += read;
                if (total - reported > 8 << 20)
                {
                    progress(total);
                    reported = total;
                }
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>Items that left the library (their ids may be given to new files).</summary>
    public void Forget(IEnumerable<long> ids)
    {
        if (_index is not { } index) return;
        foreach (var id in ids) index.Remove(id);
    }

    public void Dispose() => _embedder?.Dispose();
}
