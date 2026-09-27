using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Tests;

public sealed class PoiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-poi-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly PoiRepository _pois;

    public PoiTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _pois = new PoiRepository(_database);
    }

    public void Dispose()
    {
        _database.CloseConnections(); // only this test's database: other test classes run at the same time
        Directory.Delete(_dir, recursive: true);
    }

    // A park (about 400 × 300 m) with a café inside it, and a restaurant across the street from the park.
    private static readonly Poi Park = new(1, "w1", "Manito Park", "park", 47.6390, -117.4100, 47.6375, -117.4127, 47.6405, -117.4073);
    private static readonly Poi Cafe = new(2, "n2", "Park Bench Café", "café", 47.6390, -117.4100);
    private static readonly Poi Diner = new(3, "n3", "Corner Diner", "restaurant", 47.6410, -117.4050);

    [Fact]
    public void A_spot_right_there_beats_the_park_around_it_and_the_park_beats_a_spot_further_off()
    {
        Poi[] all = [Park, Cafe, Diner];
        Assert.Equal("Park Bench Café", PoiMatcher.Best(47.63905, -117.41005, all)?.Name);     // ~7 m from the café
        Assert.Equal("Manito Park", PoiMatcher.Best(47.6380, -117.4120, all)?.Name);          // in the park, far from the café
        Assert.Equal("Corner Diner", PoiMatcher.Best(47.64115, -117.40495, all)?.Name);       // ~15 m from the diner, outside the park
        Assert.Null(PoiMatcher.Best(47.6500, -117.4000, all));                               // nowhere near
        Assert.True(Park.AreaM2 is > 50_000 and < 150_000);
    }

    [Fact]
    public void Overpass_answers_are_read_with_kinds_and_areas()
    {
        const string json = """
            {"elements":[
              {"type":"node","id":7,"lat":47.66,"lon":-117.41,"tags":{"amenity":"cafe","name":"Atticus"}},
              {"type":"way","id":8,"bounds":{"minlat":47.62,"minlon":-117.42,"maxlat":47.63,"maxlon":-117.40},"tags":{"leisure":"park","name":"Comstock Park"}},
              {"type":"node","id":9,"lat":47.6,"lon":-117.4,"tags":{"amenity":"cafe"}},
              {"type":"node","id":10,"lat":47.6,"lon":-117.4,"tags":{"amenity":"parking","name":"Lot B"}}
            ]}
            """;
        var pois = Overpass.Parse(json);
        Assert.Equal(["n7 Atticus café", "w8 Comstock Park park"], pois.Select(p => $"{p.OsmKey} {p.Name} {p.Kind}"));
        Assert.True(pois[1].IsArea);
        Assert.Equal(47.625, pois[1].Latitude, 6);

        var query = Overpass.Query(Overpass.TileOf(47.66, -117.41));
        Assert.Contains("(47.65,-117.45,47.7,-117.4)", query);
        Assert.Contains("\"leisure\"~\"^(park|", query);
    }

    [Fact]
    public void Areas_use_their_outline_not_their_box()
    {
        // A thin diagonal strip (a riverside reserve) from SW to NE: its box covers the corners, the strip doesn't.
        IReadOnlyList<(double, double)> strip = [(47.760, -117.545), (47.762, -117.545), (47.798, -117.455), (47.796, -117.455), (47.760, -117.545)];
        var reserve = new Poi(1, "r1", "Little River Natural Area", "nature reserve", 47.779, -117.5, 47.760, -117.545, 47.798, -117.455,
            PoiShape.Encode([strip]));
        Assert.False(reserve.Contains(47.795, -117.540)); // top-left corner of the box: a neighbourhood, not the reserve
        Assert.True(reserve.Contains(47.779, -117.4975));  // on the strip
        Assert.True(reserve.Contains(47.7785, -117.4995, margin: 150)); // just beside it, within the margin
        Assert.Null(PoiMatcher.Best(47.795, -117.540, [reserve]));

        // The same reserve without an outline: its box is far too big to trust.
        Assert.False((reserve with { Shape = null }).Contains(47.795, -117.540));
        Assert.Contains("out geom;", Overpass.Query(Overpass.TileOf(47.78, -117.5)));
    }

    [Fact]
    public void Outlines_are_read_from_ways_and_relations()
    {
        const string json = """
            {"elements":[
              {"type":"way","id":1,"bounds":{"minlat":0,"minlon":0,"maxlat":1,"maxlon":1},"tags":{"leisure":"park","name":"Square Park"},
               "geometry":[{"lat":0,"lon":0},{"lat":0,"lon":1},{"lat":1,"lon":1},{"lat":1,"lon":0},{"lat":0,"lon":0}]},
              {"type":"relation","id":2,"bounds":{"minlat":0,"minlon":0,"maxlat":2,"maxlon":2},"tags":{"leisure":"nature_reserve","name":"Split Reserve"},
               "members":[{"type":"way","ref":5,"role":"outer","geometry":[{"lat":0,"lon":0},{"lat":0,"lon":2},{"lat":2,"lon":2}]},
                          {"type":"way","ref":6,"role":"outer","geometry":[{"lat":2,"lon":2},{"lat":2,"lon":0},{"lat":0,"lon":0}]},
                          {"type":"way","ref":7,"role":"inner","geometry":[{"lat":0.5,"lon":0.5},{"lat":0.5,"lon":1.5},{"lat":1.5,"lon":1.5},{"lat":1.5,"lon":0.5},{"lat":0.5,"lon":0.5}]}]},
              {"type":"way","id":3,"bounds":{"minlat":0,"minlon":0,"maxlat":0.1,"maxlon":0.1},"tags":{"leisure":"track","name":"Open Track"},
               "geometry":[{"lat":0,"lon":0},{"lat":0.1,"lon":0.1}]}
            ]}
            """;
        var pois = Overpass.Parse(json);
        var park = pois.Single(p => p.Name == "Square Park");
        var reserve = pois.Single(p => p.Name == "Split Reserve");
        Assert.True(park.Contains(0.5, 0.5, margin: 0));
        Assert.True(reserve.Contains(0.25, 0.25, margin: 0));  // inside the outer ring, drawn as two ways
        Assert.False(reserve.Contains(1.0, 1.0, margin: 0));   // in the hole
        Assert.False(pois.Single(p => p.Name == "Open Track").IsArea); // an open line counts as a spot
    }

    private long Add(string name, double lat, double lon)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem { Path = $@"D:\Lib\{name}", FileName = name, FileSize = 1, FileModified = 1, Kind = MediaKind.Photo, Latitude = lat, Longitude = lon };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    [Fact]
    public void Photos_in_looked_up_tiles_are_named_after_the_place_and_searchable_by_name_and_kind()
    {
        var inPark = Add("walk.jpg", 47.6380, -117.4120);
        var atDiner = Add("lunch.jpg", 47.64115, -117.40495);
        var elsewhere = Add("trip.jpg", 21.0, -157.0);
        var tile = Overpass.TileOf(47.639, -117.41);
        Assert.Equal(2, _pois.GetPendingTiles().Count);

        var named = _pois.SaveTile(tile, [Park with { Id = 0 }, Diner with { Id = 0 }]);

        Assert.Equal(2, named);
        Assert.Equal("Manito Park", _pois.GetFor(inPark)?.Name);
        Assert.Equal("Corner Diner", _pois.GetFor(atDiner)?.Name);
        Assert.Null(_pois.GetFor(elsewhere));
        Assert.Equal([inPark], _media.Query(new MediaFilter { Text = "manito" }).Select(m => m.Id));
        Assert.Equal([atDiner], _media.Query(new MediaFilter { Text = "restaurant" }).Select(m => m.Id));
        Assert.Equal((1, 2, 2, 2), _pois.GetProgress());

        // Indexed again unchanged: keeps its place; a new photo in a looked-up tile is matched without asking again.
        Add("walk.jpg", 47.6380, -117.4120);
        Assert.Equal("Manito Park", _pois.GetFor(inPark)?.Name);
        var later = Add("picnic.jpg", 47.6395, -117.4110);
        Assert.Equal(1, _pois.AssignPending());
        Assert.Equal("Manito Park", _pois.GetFor(later)?.Name);
        Assert.Equal([Overpass.TileOf(21.0, -157.0)], _pois.GetPendingTiles());
    }
}
