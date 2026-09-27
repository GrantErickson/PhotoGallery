using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Similarity;

namespace PhotoGallery.Core.Tests;

public sealed class SimilarityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-sim-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly EmbeddingRepository _embeddings;

    public SimilarityTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _embeddings = new EmbeddingRepository(_database);
    }

    public void Dispose()
    {
        _database.CloseConnections(); // only this test's database: other test classes run at the same time
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Words_are_merged_by_rank_and_padded_to_77_tokens()
    {
        var vocab = new Dictionary<string, int>
        {
            ["<|startoftext|>"] = 100, ["<|endoftext|>"] = 101,
            ["a</w>"] = 1, ["c"] = 2, ["a"] = 3, ["t</w>"] = 4, ["ca"] = 5, ["cat</w>"] = 6, ["s</w>"] = 7, ["!</w>"] = 8, ["cats</w>"] = 9, ["t"] = 10,
        };
        var tokenizer = new ClipTokenizer(vocab, ["#version: 0.2", "c a", "ca t</w>", "ca t", "cat s</w>"]);

        var ids = tokenizer.Encode("  A   Cat!  cats ");

        Assert.Equal(ClipTokenizer.Length, ids.Length);
        Assert.Equal([100L, 1, 6, 8, 9, 101], ids[..6]);
        Assert.All(ids[6..], id => Assert.Equal(101, id));

        var long_ = tokenizer.Encode(string.Join(' ', Enumerable.Repeat("cat", 200)));
        Assert.Equal(ClipTokenizer.Length, long_.Length);
        Assert.Equal(101, long_[^1]); // truncated, still ends with the end marker
    }

    private static float[] Direction(params (int Index, float Value)[] parts)
    {
        var v = new float[Embedding.Dimensions];
        foreach (var (i, value) in parts) v[i] = value;
        return v;
    }

    [Fact]
    public void Quantized_vectors_keep_cosine_similarity_and_search_finds_the_nearest()
    {
        var a = Embedding.Quantize(Direction((0, 3), (1, 4)));        // (0.6, 0.8)
        var b = Embedding.Quantize(Direction((0, 4), (1, 3)));        // (0.8, 0.6): cos 0.96 with a
        var c = Embedding.Quantize(Direction((2, 1)));                // orthogonal
        Assert.Equal(1, Embedding.Similarity(a, a), 0.01);
        Assert.Equal(0.96, Embedding.Similarity(a, b), 0.01);
        Assert.Equal(0, Embedding.Similarity(a, c), 0.01);

        var index = new SimilarityIndex([10, 20, 30], [.. a, .. b, .. c]);
        var found = index.Search(a, count: 5, minSimilarity: 0.5, except: 10);
        Assert.Equal([20L], found.Select(f => f.Id));
        Assert.Equal([10L, 20, 30], index.Search(a, count: 3).Select(f => f.Id));
        Assert.Equal(b, index.VectorOf(20).ToArray());
    }

    [Fact]
    public void Embeddings_added_later_are_searchable_and_can_be_replaced()
    {
        var index = SimilarityIndex.Empty;
        var x = Embedding.Quantize(Direction((0, 1)));
        var y = Embedding.Quantize(Direction((1, 1)));
        for (var id = 1; id <= 1500; id++) index.Add(id, id == 700 ? x : y); // grows past its first capacity
        Assert.Equal(1500, index.Count);
        Assert.Equal(700, index.Search(x, 1)[0].Id);

        index.Add(700, y);
        Assert.Equal(1500, index.Count);
        Assert.True(index.Search(x, 1)[0].Similarity < 0.1);
    }

    [Fact]
    public void Removed_embeddings_are_no_longer_found()
    {
        var x = Embedding.Quantize(Direction((0, 1)));
        var y = Embedding.Quantize(Direction((1, 1)));
        var index = new SimilarityIndex([1, 2, 3], [.. x, .. y, .. y]);

        index.Remove(1);
        index.Remove(42); // not there

        Assert.Equal(2, index.Count);
        Assert.False(index.Contains(1));
        Assert.Equal([2L, 3], index.Search(y, 5).Select(r => r.Id).Order());
        Assert.Equal(y, index.VectorOf(3).ToArray()); // moved into the freed row
    }

    [Fact]
    public void Removing_items_announces_their_ids()
    {
        var a = Add("a.jpg");
        var b = Add("b.jpg");
        var announced = new List<long>();
        _media.Removed += ids => announced.AddRange(ids);

        _media.Remove([a]);
        _media.RemoveRoot(@"D:\Lib");

        Assert.Equal([a, b], announced);
    }

    [Fact]
    public void People_filter_needs_everyone_chosen_and_dates_sort_both_ways()
    {
        var both = Add("both.jpg");
        var onlyAnne = Add("anne.jpg");
        var nobody = Add("nobody.jpg");
        using (var db = _database.Open())
        {
            var anne = db.ExecuteScalar<long>("INSERT INTO People (Name) VALUES ('Anne') RETURNING Id");
            var emily = db.ExecuteScalar<long>("INSERT INTO People (Name) VALUES ('Emily') RETURNING Id");
            db.Execute("INSERT INTO MediaFaces (MediaId, PersonId) VALUES (@both, @anne), (@both, @emily), (@onlyAnne, @anne)", new { both, onlyAnne, anne, emily });
            db.Execute("UPDATE Media SET DateTaken = Id * 100");
            Assert.Equal([both], _media.Query(new MediaFilter { People = [anne, emily] }).Select(m => m.Id));
            Assert.Equal([onlyAnne, both], _media.Query(new MediaFilter { People = [anne] }).Select(m => m.Id));
        }
        Assert.Equal([both, onlyAnne, nobody], _media.Query(new MediaFilter { Order = MediaOrder.Oldest }).Select(m => m.Id));
        Assert.Equal([nobody, onlyAnne, both], _media.Query(new MediaFilter { Ids = [both, nobody, onlyAnne], Order = MediaOrder.Newest }).Select(m => m.Id));
    }

    [Fact]
    public void Word_search_finds_every_word_best_match_first()
    {
        var cake = Add("birthday cake.jpg");
        Add("cake.jpg");
        var party = Add("birthday party cake at the lake.jpg");
        Assert.Equal([cake, party], _media.SearchWords("birthday cake").Order());
        Assert.Empty(_media.SearchWords("   "));
    }

    [Fact]
    public void Listed_order_keeps_the_order_of_the_ids()
    {
        var a = Add("a.jpg");
        var b = Add("b.jpg");
        var c = Add("c.jpg");
        Assert.Equal([b, c, a], _media.Query(new MediaFilter { Ids = [b, c, a], Order = MediaOrder.Listed }).Select(m => m.Id));
    }

    private long Add(string name, long size = 1, bool hidden = false)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem { Path = $@"D:\Lib\{name}", FileName = name, FileSize = size, FileModified = 1, Kind = MediaKind.Photo };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    [Fact]
    public void Embeddings_are_stored_loaded_and_redone_when_the_file_changes()
    {
        var beach = Add("beach.jpg");
        var dog = Add("dog.jpg");
        var broken = Add("broken.jpg");
        Assert.Equal([beach, dog, broken], _embeddings.GetBacklog(10).Select(b => b.Id).Order());

        var v1 = Embedding.Quantize(Direction((0, 1)));
        var v2 = Embedding.Quantize(Direction((1, 1)));
        _embeddings.Save([(beach, v1), (dog, v2), (broken, null)]);
        Assert.Empty(_embeddings.GetBacklog(10));
        Assert.Equal((3, 3), _embeddings.GetProgress());

        var index = _embeddings.LoadIndex();
        Assert.Equal(2, index.Count); // the unreadable one isn't searchable
        Assert.Equal(v2, index.VectorOf(dog).ToArray());

        Add("dog.jpg", size: 2); // changed: redone, and not searchable meanwhile
        Assert.Equal([dog], _embeddings.GetBacklog(10).Select(b => b.Id));
        Assert.False(_embeddings.LoadIndex().Contains(dog));
    }
}
