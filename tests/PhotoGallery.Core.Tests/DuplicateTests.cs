using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-dup-{Guid.NewGuid():N}");

    public DuplicateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Quick_hash_matches_identical_content_and_detects_tail_changes()
    {
        var content = Random(500_000, 1);
        var a = WriteFile("a.jpg", content);
        var b = WriteFile("b.jpg", content);
        var changed = (byte[])content.Clone();
        changed[^10] ^= 0xFF;
        var c = WriteFile("c.jpg", changed);
        var small = WriteFile("small.jpg", Random(1000, 2));

        Assert.Equal(ContentHashes.QuickHash(a), ContentHashes.QuickHash(b));
        Assert.NotEqual(ContentHashes.QuickHash(a), ContentHashes.QuickHash(c));
        Assert.Equal(32, ContentHashes.QuickHash(small).Length);
    }

    [Fact]
    public void Difference_hash_follows_gradients()
    {
        // Brightness increasing left→right sets every bit; decreasing clears them.
        byte[] Gradient(bool increasing)
        {
            var px = new byte[9 * 8 * 4];
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 9; x++)
                {
                    var v = (byte)(increasing ? x * 25 : 200 - x * 25);
                    px[(y * 9 + x) * 4] = px[(y * 9 + x) * 4 + 1] = px[(y * 9 + x) * 4 + 2] = v;
                }
            return px;
        }

        Assert.Equal(-1L, ContentHashes.DifferenceHash(Gradient(true), 9, 8));
        Assert.Equal(0L, ContentHashes.DifferenceHash(Gradient(false), 9, 8));
        Assert.Equal(64, ContentHashes.Distance(-1L, 0L));
    }

    [Fact]
    public void Cluster_links_near_hashes_transitively()
    {
        var items = new List<(long, long)>
        {
            (1, 0b0000), (2, 0b0111), (3, 0b1111_1111_1111), // 1–2 within 3 bits; 3 is far from 1 but within 9 of 2
            (4, long.MaxValue),
        };

        var clusters = DuplicateFinder.Cluster(items, threshold: 3);

        Assert.Equal([1L, 2L], Assert.Single(clusters).Order());
    }

    [Theory]
    [InlineData("IMG_0840 (1).JPEG", true)]
    [InlineData("IMG_0840(0).jpg", true)]
    [InlineData("Photo - Copy.jpg", true)]
    [InlineData("IMG_0840.JPEG", false)]
    [InlineData("20260110_120928.jpg", false)]
    public void Copy_names(string name, bool isCopy) => Assert.Equal(isCopy, DuplicateFinder.IsCopyName(name));

    [Fact]
    public void Suggest_keep_prefers_resolution_then_original_name()
    {
        var copy = new DuplicateMember { Id = 1, FileName = "a (1).jpg", Path = @"D:\a (1).jpg", Width = 4000, Height = 3000 };
        var original = new DuplicateMember { Id = 2, FileName = "a.jpg", Path = @"D:\x\a.jpg", Width = 4000, Height = 3000 };
        var small = new DuplicateMember { Id = 3, FileName = "a.jpg", Path = @"D:\a.jpg", Width = 1024, Height = 768 };

        DuplicateFinder.SuggestKeep([copy, original, small]);

        Assert.True(original.IsSuggestedKeep);
        Assert.False(copy.IsSuggestedKeep);
        Assert.False(small.IsSuggestedKeep);
    }

    [Fact]
    public async Task Finds_exact_duplicates_and_caches_their_hashes()
    {
        var dbPath = Path.Combine(_dir, "g.db");
        var database = new GalleryDatabase(dbPath);
        database.Migrate();
        var media = new MediaRepository(database);
        var content = Random(200_000, 3);
        var paths = new[] { WriteFile("x.jpg", content), WriteFile("x (1).jpg", content), WriteFile("y.jpg", Random(200_000, 4)) };
        using (var db = database.Open())
        using (var tx = db.BeginTransaction())
        {
            var folders = media.GetFolderIds();
            foreach (var p in paths)
            {
                var item = new MediaItem { Path = p, FileName = Path.GetFileName(p), FileSize = 200_000, FileModified = 1, Kind = MediaKind.Photo, DateTaken = 1000 + paths.ToList().IndexOf(p) };
                item.FolderId = media.EnsureFolder(db, tx, folders, _dir, _dir);
                media.Upsert(db, tx, item, _dir);
            }
            tx.Commit();
        }
        var finder = new DuplicateFinder(database, (_, _, _) => Task.FromResult<string?>(null));

        var groups = await finder.FindAsync(ct: TestContext.Current.CancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(DuplicateKind.Exact, group.Kind);
        Assert.Equal(["x (1).jpg", "x.jpg"], group.Members.Select(m => m.FileName).Order());
        Assert.Equal("x.jpg", group.Members.Single(m => m.IsSuggestedKeep).FileName);
        Assert.Equal(200_000, group.ReclaimableBytes);
        using (var db = database.Open())
            Assert.Equal(3L, Dapper.SqlMapper.ExecuteScalar<long>(db, "SELECT count(*) FROM Media WHERE QuickHash IS NOT NULL"));
    }
}
