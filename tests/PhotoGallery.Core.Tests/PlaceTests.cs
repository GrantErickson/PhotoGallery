using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Tests;

public sealed class PlaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-places-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly PlaceRepository _places;

    public PlaceTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _places = new PlaceRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private long Add(string name, double? lat, double? lon)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem { Path = $@"D:\Lib\{name}", FileName = name, FileSize = 1, FileModified = 1, Kind = MediaKind.Photo, Latitude = lat, Longitude = lon };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    // Home near Spokane; ~0.001° of latitude ≈ 111 m.
    private const double HomeLat = 47.6588, HomeLon = -117.4260;

    [Fact]
    public void Photos_within_the_radius_are_at_the_place_searchable_and_filtered()
    {
        var porch = Add("porch.jpg", HomeLat + 0.0003, HomeLon + 0.0003);   // ~40 m
        var street = Add("street.jpg", HomeLat + 0.003, HomeLon);           // ~330 m
        var nowhere = Add("scan.jpg", null, null);

        var home = _places.Save(new Place(0, "Home", HomeLat, HomeLon, 150));

        Assert.Equal([porch], _media.Query(new MediaFilter { PlaceId = home }).Select(m => m.Id));
        Assert.Equal([porch], _media.Query(new MediaFilter { Text = "home" }).Select(m => m.Id));
        Assert.Equal(1, _places.CountMedia(home));

        // Bigger radius: the street photo joins; a new photo there is filed when indexed.
        _places.Save(new Place(home, "Home", HomeLat, HomeLon, 500));
        var garden = Add("garden.jpg", HomeLat - 0.001, HomeLon);
        Assert.Equal([porch, street, garden], _media.Query(new MediaFilter { PlaceId = home }).Select(m => m.Id).Order());
        Assert.DoesNotContain(nowhere, _media.Query(new MediaFilter { Text = "home" }).Select(m => m.Id));

        // Renamed: searchable by the new name only. Deleted: gone from search.
        _places.Save(new Place(home, "The house", HomeLat, HomeLon, 500));
        Assert.Empty(_media.Query(new MediaFilter { Text = "home" }));
        Assert.Equal(3, _media.Query(new MediaFilter { Text = "house" }).Count);
        _places.Delete(home);
        Assert.Empty(_media.Query(new MediaFilter { Text = "house" }));
    }

    [Fact]
    public void The_smallest_containing_place_wins()
    {
        _places.Save(new Place(0, "Spokane", HomeLat, HomeLon, 10_000));
        _places.Save(new Place(0, "Home", HomeLat, HomeLon, 100));
        Assert.Equal("Home", _places.FindFor(HomeLat + 0.0002, HomeLon)!.Name);
        Assert.Equal("Spokane", _places.FindFor(HomeLat + 0.02, HomeLon)!.Name);
        Assert.Null(_places.FindFor(0, 0));
    }

    [Fact]
    public void The_nearest_town_is_found_within_range()
    {
        var index = CityIndex.Load([
            "5811696\tSpokane\tSpokane\t\t47.65966\t-117.42908\tP\tPPLA2\tUS\t\tWA\t063\t\t\t228989\t\t\tAmerica/Los_Angeles\t2019-09-05",
            "5801905\tMedical Lake\tMedical Lake\t\t47.57294\t-117.68216\tP\tPPL\tUS\t\tWA\t063\t\t\t5060\t\t\tAmerica/Los_Angeles\t2017-03-09",
            "6173331\tVancouver\tVancouver\t\t49.24966\t-123.11934\tP\tPPLA2\tCA\t\t02\t\t\t\t600000\t\t\tAmerica/Vancouver\t2019-08-08",
            "not a city line",
        ]);
        Assert.Equal(3, index.Count);
        Assert.Equal("Medical Lake, WA", index.Nearest(47.58, -117.66)!.DisplayName);
        Assert.Equal("Spokane, WA", index.Nearest(47.66, -117.40)!.DisplayName);
        Assert.Equal("Vancouver, Canada", index.Nearest(49.28, -123.12)!.DisplayName);
        Assert.Null(index.Nearest(45.0, -110.0)); // far from all
    }
}
