using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Imaging;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Tests;

public sealed class SharpnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-sharp-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;

    public SharpnessTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A 360 × 240 BGRA picture of 8-pixel squares, box-blurred <paramref name="blur"/> times, scaled to a brightness range.</summary>
    private static byte[] Checkerboard(int blur, double from = 0, double to = 255)
    {
        const int w = 360, h = 240;
        var v = new double[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                v[y * w + x] = (x / 8 + y / 8) % 2 == 0 ? 0 : 1;
        for (var pass = 0; pass < blur; pass++)
        {
            var next = new double[v.Length];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    double sum = 0;
                    var n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            sum += v[yy * w + xx];
                            n++;
                        }
                    next[y * w + x] = sum / n;
                }
            v = next;
        }
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < v.Length; i++)
        {
            var b = (byte)Math.Round(from + (to - from) * v[i]);
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = b;
            bgra[i * 4 + 3] = 255;
        }
        return bgra;
    }

    [Fact]
    public void Blur_lowers_the_score_and_darkness_does_not()
    {
        var sharp = Sharpness.Score(Checkerboard(0), 360, 240);
        var soft = Sharpness.Score(Checkerboard(2), 360, 240);
        var blurry = Sharpness.Score(Checkerboard(6), 360, 240);
        Assert.True(sharp > soft && soft > blurry, $"{sharp:F1} > {soft:F1} > {blurry:F1}");
        Assert.True(blurry < Sharpness.BlurryBelow);
        Assert.True(sharp > 3 * Sharpness.BlurryBelow);

        // The same sharp picture taken in the dark (0–40 instead of 0–255) is just as sharp.
        Assert.Equal(sharp, Sharpness.Score(Checkerboard(0, 0, 40), 360, 240), sharp * 0.1);
        Assert.Equal(0, Sharpness.Score(new byte[16], 2, 2));
    }

    private long Add(string name, long size = 1, MediaKind kind = MediaKind.Photo)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem { Path = $@"D:\Lib\{name}", FileName = name, FileSize = size, FileModified = 1, Kind = kind };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    [Fact]
    public void Blurry_photos_list_blurriest_first_and_are_measured_again_when_the_file_changes()
    {
        var sharp = Add("sharp.jpg");
        var soft = Add("soft.jpg");
        var worst = Add("worst.jpg");
        var unreadable = Add("broken.jpg");
        var video = Add("clip.mov", kind: MediaKind.Video);
        var todo = _media.GetSharpnessBacklog(10).Select(b => b.Id).ToList();
        Assert.Equal([sharp, soft, worst, unreadable], todo.Order());
        Assert.DoesNotContain(video, todo);

        _media.SetSharpness([(sharp, 60), (soft, 20), (worst, 9.5), (unreadable, -1)]);
        Assert.Empty(_media.GetSharpnessBacklog(10));
        var blurry = new MediaFilter { SharpnessBelow = Sharpness.BlurryBelow, Order = MediaOrder.Blurriest };
        Assert.Equal([worst, soft], _media.Query(blurry).Select(m => m.Id));
        Assert.Equal((4, 4, 2), _media.GetSharpnessProgress());

        Add("soft.jpg"); // indexed again, unchanged: keeps its score
        Assert.Empty(_media.GetSharpnessBacklog(10));
        Add("soft.jpg", size: 2); // the file changed: measured again
        Assert.Equal([soft], _media.GetSharpnessBacklog(10).Select(b => b.Id));
        Assert.Equal([worst], _media.Query(blurry).Select(m => m.Id));
    }
}
