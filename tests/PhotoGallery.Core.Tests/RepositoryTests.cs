using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Tests;

public sealed class RepositoryTests : IDisposable
{
    private const string Root = @"D:\Lib";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pg-test-{Guid.NewGuid():N}.db");
    private readonly GalleryDatabase _db;
    private readonly MediaRepository _media;
    private readonly CollectionRepository _collections;

    public RepositoryTests()
    {
        _db = new GalleryDatabase(_dbPath);
        _db.Migrate();
        _media = new MediaRepository(_db);
        _collections = new CollectionRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) File.Delete(f);
    }

    private MediaItem Add(string relative, MediaKind kind = MediaKind.Photo, DateTime? taken = null, string? contentId = null,
        long durationMs = 0, bool screenshot = false, long motionLength = 0, string? model = null)
    {
        var item = new MediaItem
        {
            Path = Path.Combine(Root, relative),
            FileName = Path.GetFileName(relative),
            FileSize = 100,
            FileModified = 1,
            Kind = kind,
            DateTaken = MediaRepository.ToUnix(taken ?? new DateTime(2020, 1, 1)),
            ContentId = contentId,
            DurationMs = durationMs,
            IsScreenshot = screenshot,
            MotionLength = motionLength,
            MotionOffset = motionLength > 0 ? 10 : 0,
            Motion = motionLength > 0 ? MotionSource.Embedded : MotionSource.None,
            CameraModel = model,
        };
        using var db = _db.Open();
        using var tx = db.BeginTransaction();
        var folders = _media.GetFolderIds();
        var folder = Path.GetDirectoryName(item.Path)!;
        item.FolderId = _media.EnsureFolder(db, tx, folders, folder, Root);
        _media.Upsert(db, tx, item, folder);
        tx.Commit();
        return item;
    }

    [Fact]
    public void Folders_are_created_as_a_tree_under_the_root()
    {
        Add(@"2024\04\a.jpg");

        var folders = _media.GetFolders();

        Assert.Equal([Root, @"D:\Lib\2024", @"D:\Lib\2024\04"], folders.Select(f => f.Path));
        Assert.Null(folders[0].ParentId);
        Assert.Equal(folders[0].Id, folders[1].ParentId);
        Assert.Equal(folders[1].Id, folders[2].ParentId);
    }

    [Fact]
    public void Reindex_updates_in_place_and_keeps_rating()
    {
        var item = Add("a.jpg");
        _media.SetRating([item.Id], 4);

        var again = Add("a.jpg", taken: new DateTime(2021, 5, 5));

        Assert.Equal(item.Id, again.Id);
        var stored = _media.Get(item.Id)!;
        Assert.Equal(4, stored.Rating);
        Assert.Equal(new DateTime(2021, 5, 5), stored.TakenLocal);
    }

    [Fact]
    public void Live_photo_pairs_by_content_id_and_hides_the_mov()
    {
        var still = Add("IMG_0840.JPG", contentId: "ABC");
        var mov = Add("IMG_0840.MOV", MediaKind.Video, contentId: "ABC", durationMs: 2800);
        var cloudOnly = Add("20260924_031147076_iOS.heic", contentId: "XYZ");
        var ordinaryVideo = Add("Ding A Ding.MOV", MediaKind.Video, durationMs: 12000);
        var pixel = Add("PXL_1.MP.jpg", motionLength: 5000);

        _media.RecomputeMotion();

        var s = _media.Get(still.Id)!;
        Assert.Equal(MotionSource.LocalPair, s.Motion);
        Assert.Equal(mov.Id, s.PairedId);
        Assert.True(_media.Get(mov.Id)!.IsHidden);
        Assert.Equal(MotionSource.Cloud, _media.Get(cloudOnly.Id)!.Motion);
        Assert.Equal(MotionSource.Embedded, _media.Get(pixel.Id)!.Motion);
        Assert.False(_media.Get(ordinaryVideo.Id)!.IsHidden);

        var timeline = _media.Query(MediaFilter.Timeline).Select(m => m.Id).ToList();
        Assert.DoesNotContain(mov.Id, timeline);
        Assert.Contains(ordinaryVideo.Id, timeline);
    }

    [Fact]
    public void Cloud_missing_survives_recompute()
    {
        var still = Add("x_iOS.heic", contentId: "XYZ");
        _media.RecomputeMotion();
        _media.SetMotion(still.Id, MotionSource.CloudMissing);

        _media.RecomputeMotion();

        Assert.Equal(MotionSource.CloudMissing, _media.Get(still.Id)!.Motion);
    }

    [Fact]
    public void Reindex_keeps_pairing_state_until_recompute()
    {
        var still = Add("IMG_1.JPG", contentId: "P");
        Add("IMG_1.MOV", MediaKind.Video, contentId: "P", durationMs: 2000);
        var cloud = Add("x_iOS.heic", contentId: "Q");
        _media.RecomputeMotion();
        _media.SetMotion(cloud.Id, MotionSource.CloudMissing);

        Add("IMG_1.JPG", contentId: "P");
        Add("x_iOS.heic", contentId: "Q");

        Assert.Equal(MotionSource.LocalPair, _media.Get(still.Id)!.Motion);
        Assert.Equal(MotionSource.CloudMissing, _media.Get(cloud.Id)!.Motion);
    }

    [Fact]
    public void Timeline_is_newest_first_and_hides_screenshots_unless_asked()
    {
        var old = Add("old.jpg", taken: new DateTime(2010, 1, 1));
        var shot = Add("IMG_1.PNG", taken: new DateTime(2015, 1, 1), screenshot: true);
        var recent = Add("new.jpg", taken: new DateTime(2020, 1, 1));

        Assert.Equal([recent.Id, old.Id], _media.Query(MediaFilter.Timeline).Select(m => m.Id));
        Assert.Equal([recent.Id, shot.Id, old.Id], _media.Query(new MediaFilter { IncludeScreenshots = true }).Select(m => m.Id));
    }

    [Fact]
    public void Filters_by_kind_folder_date_and_on_this_day()
    {
        var a = Add(@"2019\a.jpg", taken: new DateTime(2019, 9, 25, 10, 0, 0));
        var b = Add(@"2021\b.mov", MediaKind.Video, taken: new DateTime(2021, 9, 25, 8, 0, 0), durationMs: 20000);
        var c = Add(@"2021\sub\c.jpg", taken: new DateTime(2021, 3, 1));
        var folder2021 = _media.GetFolders().Single(f => f.Name == "2021").Id;

        Assert.Equal([b.Id], _media.Query(new MediaFilter { Kinds = KindFilter.Videos }).Select(m => m.Id));
        Assert.Equal([b.Id, c.Id], _media.Query(new MediaFilter { FolderId = folder2021 }).Select(m => m.Id));
        Assert.Equal([b.Id], _media.Query(new MediaFilter { FolderId = folder2021, IncludeSubfolders = false }).Select(m => m.Id));
        Assert.Equal([b.Id, a.Id], _media.Query(new MediaFilter { MonthDay = (9, 25) }).Select(m => m.Id));
        Assert.Equal([c.Id], _media.Query(new MediaFilter { From = new DateTime(2021, 1, 1), To = new DateTime(2021, 6, 1) }).Select(m => m.Id));
    }

    [Fact]
    public void Text_search_matches_name_folder_camera_and_tags()
    {
        var wedding = Add(@"Lane Wedding 2023\IMG_9095.HEIC", model: "iPhone 14 Pro");
        var other = Add(@"2024\PXL_20240417.jpg", model: "Pixel 8");
        _collections.AddTag([other.Id], "Grandma");

        Assert.Equal([wedding.Id], _media.Query(new MediaFilter { Text = "wedd" }).Select(m => m.Id));
        Assert.Equal([wedding.Id], _media.Query(new MediaFilter { Text = "iphone" }).Select(m => m.Id));
        Assert.Equal([other.Id], _media.Query(new MediaFilter { Text = "grand" }).Select(m => m.Id));
        Assert.Equal([other.Id], _media.Query(new MediaFilter { Text = "pxl 2024" }).Select(m => m.Id));
        Assert.Empty(_media.Query(new MediaFilter { Text = "\"unbalanced" }));
    }

    [Fact]
    public void Albums_keep_insertion_order_and_support_reordering()
    {
        var a = Add("a.jpg", taken: new DateTime(2020, 1, 1));
        var b = Add("b.jpg", taken: new DateTime(2021, 1, 1));
        var c = Add("c.jpg", taken: new DateTime(2022, 1, 1));
        var album = _collections.CreateAlbum("Trip");

        _collections.AddToAlbum(album, [a.Id, c.Id]);
        _collections.AddToAlbum(album, [b.Id, a.Id]); // a is already in it

        Assert.Equal([a.Id, c.Id, b.Id], _media.Query(new MediaFilter { AlbumId = album }).Select(m => m.Id));

        _collections.MoveInAlbum(album, b.Id, 0);
        Assert.Equal([b.Id, a.Id, c.Id], _media.Query(new MediaFilter { AlbumId = album }).Select(m => m.Id));

        var row = Assert.Single(_collections.GetAlbums());
        Assert.Equal(3, row.Count);
        Assert.Equal(b.Id, row.CoverMediaId);
    }

    [Fact]
    public void Removing_last_use_of_a_tag_deletes_it()
    {
        var a = Add("a.jpg");
        _collections.AddTag([a.Id], "Beach");
        var tag = Assert.Single(_collections.GetTagsFor(a.Id));

        _collections.RemoveTag([a.Id], tag.Id);

        Assert.Empty(_collections.GetTags());
        Assert.Empty(_media.Query(new MediaFilter { Text = "beach" }));
    }

    [Fact]
    public void Delete_removes_media_and_search_rows()
    {
        var a = Add("gone.jpg");
        using (var db = _db.Open())
        using (var tx = db.BeginTransaction())
        {
            _media.Delete(db, tx, [a.Id]);
            tx.Commit();
        }

        Assert.Null(_media.Get(a.Id));
        Assert.Empty(_media.Query(new MediaFilter { Text = "gone" }));
    }
}
