using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Ocr;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-ocr-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly PhotoTextRepository _texts;

    public PhotoTextTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _texts = new PhotoTextRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static OcrLine L(params string[] words) => new(words.Select((w, i) => new OcrWord(w, i * 0.1, 0, 0.09, 0.02)).ToList());

    [Fact]
    public void Cleaning_keeps_text_and_drops_texture_noise()
    {
        var receipt = OcrCleaner.Clean([L("Spokane", "Intl", "Airport"), L("O"), L("Receipt", "5", "1966"), L("C", "e"), L("&&", "~")]);
        Assert.Equal(["Spokane Intl Airport", "Receipt 5 1966"], receipt.Select(l => l.Text));

        Assert.Empty(OcrCleaner.Clean([L("O"), L("ll", "e"), L("Ie")]));   // a tree
        Assert.Empty(OcrCleaner.Clean([L("abc")]));                         // one short stray word
        Assert.Equal(["EXIT"], OcrCleaner.Clean([L("EXIT")]).Select(l => l.Text));
        Assert.Equal(["2022 NISS", "CVC5389"], OcrCleaner.Clean([L("2022", "NISS"), L("CVC5389")]).Select(l => l.Text));
    }

    private long Add(string name, bool screenshot = false, MediaKind kind = MediaKind.Photo, long taken = 0, long size = 100)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem
        {
            Path = $@"D:\Lib\{name}", FileName = name, FileSize = size, FileModified = 1, Kind = kind, IsScreenshot = screenshot, DateTaken = taken,
        };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    [Fact]
    public void Text_is_read_screenshots_and_text_photos_first_searchable_and_forgotten_when_the_file_changes()
    {
        var beach = Add("beach.jpg", taken: 300);
        var receipt = Add("receipt.jpg", taken: 100);
        var screen = Add("screen.png", screenshot: true, taken: 50);
        var movie = Add("movie.mov", kind: MediaKind.Video, taken: 400);
        using (var db = _database.Open())
        {
            var tag = db.ExecuteScalar<long>("INSERT INTO Tags (Name, Source, TagType) VALUES ('Receipt', 1, 2) RETURNING Id");
            db.Execute("INSERT INTO MediaTags (MediaId, TagId) VALUES (@receipt, @tag)", new { receipt, tag });
        }

        var backlog = _texts.GetBacklog(10).Select(j => j.MediaId).ToList();
        Assert.Equal([receipt, screen, beach], backlog); // text-ish first (newest first among them), no videos
        Assert.DoesNotContain(movie, backlog);

        _texts.Save(new PhotoText(receipt, [L("Spokane", "Intl", "Airport")], "test"));
        _texts.Save(new PhotoText(beach, [], "test"));
        Assert.Equal("Spokane Intl Airport", _texts.Get(receipt)!.Text);
        Assert.False(_texts.Get(beach)!.HasText);
        Assert.Equal([screen], _texts.GetBacklog(10).Select(j => j.MediaId));
        Assert.Equal((2, 3, 1), _texts.GetProgress());
        Assert.Equal([receipt], _media.Query(new MediaFilter { Text = "airport" }).Select(m => m.Id));

        Add("receipt.jpg", taken: 100); // re-indexed unchanged: still searchable
        Assert.Equal([receipt], _media.Query(new MediaFilter { Text = "airport" }).Select(m => m.Id));
        Add("receipt.jpg", taken: 100, size: 200); // the file changed
        Assert.Null(_texts.Get(receipt));
        Assert.Empty(_media.Query(new MediaFilter { Text = "airport" }));
    }

    [Fact]
    public void Word_boxes_scale_by_the_long_side()
    {
        var word = new OcrWord("EXIT", 0.25, 0.1, 0.05, 0.02);
        Assert.Equal((1000.0, 400.0, 200.0, 80.0), word.In(4000, 3000));
    }
}
