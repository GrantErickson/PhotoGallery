using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Tests;

public sealed class LocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-locations-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly PlaceRepository _places;
    private readonly LocationRepository _locations;

    public LocationTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _places = new PlaceRepository(_database);
        _locations = new LocationRepository(_database);
    }

    public void Dispose()
    {
        _database.CloseConnections(); // only this test's database: other test classes run at the same time
        Directory.Delete(_dir, recursive: true);
    }

    // A park near Spokane, a café ~2.2 km away, and home; ~0.001° of latitude ≈ 111 m.
    private const double ParkLat = 47.6340, ParkLon = -117.4110;
    private const double CafeLat = 47.6540, CafeLon = -117.4110;
    private const double HomeLat = 47.6900, HomeLon = -117.4110;
    private static readonly DateTime Noon = new(2025, 7, 12, 12, 0, 0);
    // Further away: Seattle (370 km), Coeur d'Alene (50 km), Libby, Montana (35 km from Idaho's Pacific time), Nairobi.
    private const double SeattleLat = 47.6062, SeattleLon = -122.3321;
    private const double LakeLat = 47.6777, LakeLon = -116.7805;
    private const double LibbyLat = 48.3883, LibbyLon = -115.5561;
    private const double NairobiLat = -1.2864, NairobiLon = 36.8172;
    // July clocks: Pacific and Mountain summer time, Central, East Africa.
    private const int Pacific = -7 * 60, Mountain = -6 * 60, Central = -5 * 60, EastAfrica = 3 * 60;

    private static long T(DateTime local) => MediaRepository.ToUnix(local);

    private long Add(string name, DateTime taken, double? lat = null, double? lon = null, DateSource source = DateSource.Exif,
                     bool screenshot = false, string make = "Apple", string model = "iPhone 15", int? offset = null)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem
        {
            Path = $@"D:\Lib\{name}", FileName = name, FileSize = 1, FileModified = 1, Kind = MediaKind.Photo,
            DateTaken = T(taken), UtcOffset = offset, DateSource = source, Latitude = lat, Longitude = lon, IsScreenshot = screenshot,
            CameraMake = make, CameraModel = model,
        };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    // ---------- Estimating ----------

    private static Fix F(long id, DateTime taken, double lat, double lon, int offset = 0, int camera = 0) => new(id, T(taken), lat, lon, offset, camera);

    private static LocationEstimate? Estimate(Fix[] fixes, DateTime taken, int? offset = null, int camera = 0) =>
        new LocationEstimator(fixes).Estimate(9, T(taken), offset, camera);

    [Fact]
    public void A_photo_soon_after_a_located_one_is_placed_there()
    {
        var fixes = new[] { F(1, Noon, ParkLat, ParkLon) };
        var estimate = Estimate(fixes, Noon.AddMinutes(3))!;
        Assert.Equal((ParkLat, ParkLon, 1L), (estimate.Latitude, estimate.Longitude, estimate.FromId));
        Assert.Equal(0.8 * 180, estimate.AccuracyMeters); // drifting from the one photo for three minutes

        Assert.Equal(25, Estimate(fixes, Noon)!.AccuracyMeters); // the same moment
        Assert.Null(Estimate(fixes, Noon.AddMinutes(20))); // too long with only one side
        Assert.Null(Estimate([], Noon));
    }

    [Fact]
    public void Photos_on_both_sides_that_agree_hold_for_up_to_an_hour()
    {
        var fixes = new[] { F(1, Noon, ParkLat, ParkLon), F(2, Noon.AddMinutes(100), ParkLat + 0.0005, ParkLon) };
        var estimate = Estimate(fixes, Noon.AddMinutes(45))!;
        Assert.Equal(1, estimate.FromId); // the nearer
        Assert.InRange(estimate.AccuracyMeters, 700, 800); // 45 min of drift, plus most of the 56 m between them

        // Over an hour from the nearer, or more than two hours to the other side: no guess.
        Assert.Null(Estimate([F(1, Noon, ParkLat, ParkLon), F(2, Noon.AddMinutes(130), ParkLat, ParkLon)], Noon.AddMinutes(65)));
        Assert.Null(Estimate([F(1, Noon, ParkLat, ParkLon), F(2, Noon.AddHours(3), ParkLat, ParkLon)], Noon.AddMinutes(30)));
    }

    [Fact]
    public void Between_far_apart_photos_the_guess_is_only_made_close_to_one()
    {
        // Moving from the park to the café (2.2 km) in ten minutes.
        var fixes = new[] { F(1, Noon, ParkLat, ParkLon), F(2, Noon.AddMinutes(10), CafeLat, CafeLon) };
        Assert.Null(Estimate(fixes, Noon.AddMinutes(5))); // somewhere on the way
        var nearCafe = Estimate(fixes, Noon.AddMinutes(9.5))!;
        Assert.Equal(2, nearCafe.FromId);
        Assert.InRange(nearCafe.AccuracyMeters, 200, 300);
    }

    [Fact]
    public void A_lone_wild_fix_is_left_out()
    {
        var fixes = new[]
        {
            F(1, Noon, ParkLat, ParkLon),
            F(2, Noon.AddSeconds(20), ParkLat + 0.05, ParkLon), // 5.5 km away and back within a minute
            F(3, Noon.AddSeconds(60), ParkLat + 0.0002, ParkLon),
        };
        Assert.Equal([1, 3], LocationEstimator.WithoutSpikes(fixes).Select(f => f.Id));
        // A real trip (the same jump over an hour) stays.
        var trip = new[] { fixes[0], fixes[1] with { Time = T(Noon.AddMinutes(30)) }, fixes[2] with { Time = T(Noon.AddMinutes(60)) } };
        Assert.Equal(3, LocationEstimator.WithoutSpikes(trip).Count);
    }

    [Fact]
    public void Photos_are_lined_up_by_real_time_and_not_across_time_zones()
    {
        // Your photo in Nairobi at 1:15 pm, and your family's in Spokane at 1:01 pm their time: ten hours apart.
        var fixes = new[] { F(1, Noon.AddMinutes(15), NairobiLat, NairobiLon, EastAfrica) };
        Assert.Null(Estimate(fixes, Noon.AddMinutes(1), Pacific));
        Assert.Null(Estimate(fixes, Noon.AddMinutes(15).AddHours(-10), Pacific)); // the same moment, but on Spokane's time
        Assert.Equal(1, Estimate(fixes, Noon.AddMinutes(10), EastAfrica)!.FromId); // a friend's phone on the trip
        Assert.Equal(1, Estimate(fixes, Noon.AddMinutes(10))!.FromId); // a file that doesn't say: its clock is taken as theirs
        // A phone that kept home time on the trip lines up with one that did the same.
        Assert.Equal(1, Estimate([F(1, Noon.AddHours(-10), NairobiLat, NairobiLon, Pacific)], Noon.AddHours(-10).AddMinutes(5), Pacific)!.FromId);
    }

    [Fact]
    public void A_clock_on_the_time_across_a_nearby_border_still_lines_up()
    {
        // In Libby, Montana, a phone may be on Idaho's Pacific time; nowhere near is on Central time.
        var fixes = new[] { F(1, Noon, LibbyLat, LibbyLon, Mountain) };
        Assert.Equal(1, Estimate(fixes, Noon.AddMinutes(-57), Pacific)!.FromId); // three minutes later
        Assert.Null(Estimate(fixes, Noon.AddMinutes(63), Central));
        Assert.Equal(Mountain, TimeZones.OffsetAt(LibbyLat, LibbyLon, fixes[0].Utc));
        Assert.Equal(Pacific, TimeZones.OffsetAtLocal(ParkLat, ParkLon, T(Noon)));
        Assert.Equal(EastAfrica, TimeZones.OffsetAtLocal(NairobiLat, NairobiLon, T(Noon)));
    }

    [Fact]
    public void When_people_were_in_two_places_only_the_photos_own_camera_says_which()
    {
        // Your phone (1) at the park, your family's (2) in Seattle at the same time.
        var fixes = new[] { F(1, Noon, ParkLat, ParkLon, camera: 1), F(2, Noon.AddMinutes(5), SeattleLat, SeattleLon, camera: 2) };
        Assert.Equal(1, Estimate(fixes, Noon.AddMinutes(4), camera: 1)!.FromId); // nearer in time to Seattle's, but yours
        Assert.Equal(2, Estimate(fixes, Noon.AddMinutes(1), camera: 2)!.FromId);
        Assert.Null(Estimate(fixes, Noon.AddMinutes(2), camera: 3)); // someone else's: which?
        // Two phones of one model, one in each place: no telling either.
        Assert.Null(Estimate([fixes[0], fixes[1] with { Camera = 1 }], Noon.AddMinutes(2), camera: 1));
        // Coeur d'Alene is near enough to be together.
        Assert.Equal(1, Estimate([fixes[0], F(2, Noon.AddMinutes(5), LakeLat, LakeLon, camera: 2)], Noon, camera: 3)!.FromId);
    }

    [Fact]
    public void A_clocks_zone_comes_from_the_file_or_its_name()
    {
        Assert.Equal(-420, Metadata.MediaMetadataReader.ParseOffset("-07:00"));
        Assert.Equal(330, Metadata.MediaMetadataReader.ParseOffset("+05:30"));
        Assert.Null(Metadata.MediaMetadataReader.ParseOffset("+5:30"));
        Assert.Null(Metadata.MediaMetadataReader.ParseOffset("   "));
        var utc = Metadata.FileNameDates.ParseUtc("20261012_200136756_iOS.heic");
        Assert.Equal(new DateTime(2026, 10, 12, 20, 1, 36, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
        Assert.NotNull(Metadata.FileNameDates.ParseUtc("PXL_20261026_210933087.jpg"));
        Assert.Null(Metadata.FileNameDates.ParseUtc("IMG_1234.HEIC"));
        Assert.Equal(-420, Metadata.MediaMetadataReader.OffsetBetween(new DateTime(2026, 10, 12, 13, 1, 36), utc.Value));
        Assert.Null(Metadata.MediaMetadataReader.OffsetBetween(new DateTime(2026, 10, 12, 13, 9, 36), utc.Value)); // saved later
    }

    // ---------- Tracks ----------

    [Fact]
    public void A_day_becomes_stops_in_order_with_the_way_between()
    {
        var fixes = new[]
        {
            F(1, Noon.AddHours(-3), HomeLat, HomeLon),
            F(2, Noon, ParkLat, ParkLon),
            F(3, Noon.AddMinutes(20), ParkLat + 0.0005, ParkLon), // 56 m: the same stop
            F(4, Noon.AddMinutes(90), CafeLat, CafeLon),
            F(5, Noon.AddDays(1), HomeLat, HomeLon),               // the next morning
            F(6, Noon.AddDays(5), ParkLat, ParkLon),               // another visit days later
        };
        var track = TrackBuilder.Build(fixes, [(10, T(Noon.AddMinutes(5)), 2), (11, T(Noon.AddMinutes(95)), 999)]);

        Assert.Equal([1, 2, 3, 4, 5], track.Stops.Select(s => s.Number));
        Assert.Equal([[1], [2, 10, 3], [4, 11], [5], [6]], track.Stops.Select(s => s.Ids.ToArray()));
        Assert.Equal([0, 0, 0, 1, 2], track.Stops.Select(s => s.Day));
        Assert.Equal(3, track.Days);
        Assert.Equal(T(Noon), track.Stops[1].Start);
        Assert.Equal(T(Noon.AddMinutes(20)), track.Stops[1].End);
        // Home → park took three hours (the way isn't known), park → café is known, the café to home was overnight,
        // and nothing joins the visit days later.
        Assert.Equal([(0, 1, false), (1, 2, true), (2, 3, false)], track.Legs.Select(l => (l.From, l.To, l.Known)));
        Assert.InRange(track.Meters, 12_000, 13_000); // home → park 6.2 km, → café 2.2 km, → home 4 km
    }

    [Fact]
    public void A_flight_is_not_drawn_as_the_way()
    {
        var fixes = new[] { F(1, Noon, HomeLat, HomeLon), F(2, Noon.AddMinutes(150), 37.6213, -122.3790) }; // San Francisco, gate to gate
        var leg = Assert.Single(TrackBuilder.Build(fixes, []).Legs);
        Assert.False(leg.Known);
    }

    [Fact]
    public void People_apart_get_a_track_each()
    {
        var day = Noon.Date;
        var fixes = new[]
        {
            // Saturday at home and the park together: you (camera 1) and your family (2).
            F(1, day.AddHours(8), HomeLat, HomeLon, Pacific, 1), F(2, day.AddHours(9), HomeLat, HomeLon, Pacific, 2),
            F(3, day.AddHours(10), ParkLat, ParkLon, Pacific, 1), F(4, day.AddHours(10.1), ParkLat, ParkLon, Pacific, 2),
            // Then you fly to Nairobi while they stay home...
            F(5, day.AddHours(42), NairobiLat, NairobiLon, EastAfrica, 1), F(6, day.AddHours(44), NairobiLat, NairobiLon, EastAfrica, 1),
            F(7, day.AddHours(57), NairobiLat, NairobiLon, EastAfrica, 1), F(8, day.AddHours(60), NairobiLat, NairobiLon, EastAfrica, 1),
            F(9, day.AddHours(62), NairobiLat, NairobiLon, EastAfrica, 1),
            F(10, day.AddHours(33), HomeLat, HomeLon, Pacific, 2), F(11, day.AddHours(39), ParkLat, ParkLon, Pacific, 2),
            F(12, day.AddHours(57), HomeLat, HomeLon, Pacific, 2), F(13, day.AddHours(58), HomeLat, HomeLon, Pacific, 2),
            // ...and someone with the same phone as yours is at the lake.
            F(14, day.AddHours(36), LakeLat, LakeLon, Pacific, 1), F(15, day.AddHours(37), LakeLat, LakeLon, Pacific, 1),
        };
        var estimated = new[] { (50L, fixes[9].Utc + 300, (long?)10) }; // estimated from your family's photo at home
        string Name(int camera) => camera == 1 ? "iPhone 17 Pro Max" : "iPhone 15 Pro Max";

        var yours = TrackBuilder.Build(fixes, estimated, 0, Name);
        Assert.Equal(2, yours.Groups.Count);
        Assert.Equal(0, yours.Group);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], yours.Stops.SelectMany(s => s.Ids).Order());
        Assert.Equal(["iPhone 17 Pro Max"], yours.Groups[0].Cameras);
        Assert.Equal(9, yours.Groups[0].Photos);
        Assert.InRange(yours.Groups[0].Latitude, -1.5, -1);
        Assert.Equal(3, yours.Days);

        var theirs = TrackBuilder.Build(fixes, estimated, 1, Name);
        Assert.Equal(1, theirs.Group);
        Assert.Equal([1, 2, 3, 4, 10, 11, 12, 13, 14, 15, 50], theirs.Stops.SelectMany(s => s.Ids).Order());
        Assert.Equal(["iPhone 15 Pro Max", "iPhone 17 Pro Max"], theirs.Groups[1].Cameras);
        Assert.Equal(11, theirs.Groups[1].Photos);
        Assert.InRange(theirs.Groups[1].Latitude, 47, 48);

        // Together, there's one track and no groups; a group that isn't there is the last one.
        Assert.Empty(TrackBuilder.Build(fixes[..4], []).Groups);
        Assert.Equal(1, TrackBuilder.Build(fixes, estimated, 7).Group);
    }

    // ---------- The library ----------

    [Fact]
    public void Photos_without_a_location_get_the_one_taken_nearest_in_time()
    {
        var park = Add("park.heic", Noon, ParkLat, ParkLon);
        var friend = Add("from-a-friend.jpg", Noon.AddMinutes(4));
        var later = Add("much-later.jpg", Noon.AddHours(5));
        var named = Add("named-from-file.jpg", Noon.AddMinutes(2), source: DateSource.FileName); // saved later, maybe
        var screenshot = Add("screenshot.png", Noon.AddMinutes(1), screenshot: true);
        var spot = _places.Save(new Place(0, "Manito Park", ParkLat, ParkLon, 300));

        Assert.Equal(1, _locations.EstimateAll());
        var item = _media.Get(friend)!;
        Assert.Equal((LocationSource.Estimated, ParkLat, ParkLon, park), (item.LocationSource, item.Latitude, item.Longitude, item.LocationFromId));
        Assert.Equal(192, item.LocationAccuracy);
        Assert.Null(item.FileLocation); // never written into saved copies
        Assert.Equal((ParkLat, ParkLon), _media.Get(park)!.FileLocation);
        foreach (var unplaced in new[] { later, named, screenshot }) Assert.Null(_media.Get(unplaced)!.Latitude);

        // It's at your place, found by its name, and on the map as approximate.
        Assert.Equal([park, friend], _media.Query(new MediaFilter { PlaceId = spot }).Select(m => m.Id).Order());
        Assert.Contains(friend, _media.Query(new MediaFilter { Text = "manito" }).Select(m => m.Id));
        Assert.Equal([(park, false), (friend, true)], _media.GetGeoPoints().Select(p => (p.Id, p.Approximate)).Order());
        Assert.Equal((1, 0), _locations.GetCounts());
        Assert.Equal(0, _locations.EstimateAll()); // nothing new
    }

    [Fact]
    public void Only_clocks_that_are_set_for_you_are_lined_up()
    {
        var park = Add("park.heic", Noon, ParkLat, ParkLon);
        var friendsPixel = Add("pixel.jpg", Noon.AddMinutes(2), make: "Google", model: "Pixel 9");      // a phone: network time
        var camera = Add("IMG_0001.JPG", Noon.AddMinutes(3), make: "Canon", model: "Canon EOS R6");     // its own clock
        var gpsCamera = Add("DSC_0001.JPG", Noon.AddMinutes(4), make: "NIKON", model: "COOLPIX P1000"); // has GPS, below
        for (var i = 0; i < 3; i++) Add($"DSC_10{i}.JPG", Noon.AddDays(-10 - i), HomeLat, HomeLon, make: "NIKON", model: "COOLPIX P1000");
        var canonWithGps = Add("IMG_0002.JPG", Noon.AddMinutes(30), CafeLat, CafeLon, make: "Canon", model: "Canon EOS R6");
        var nearCanon = Add("from-a-friend.jpg", Noon.AddMinutes(31));

        _locations.EstimateAll();
        Assert.Equal(park, _media.Get(friendsPixel)!.LocationFromId);
        Assert.Equal(park, _media.Get(gpsCamera)!.LocationFromId);
        Assert.Null(_media.Get(camera)!.Latitude);
        // A camera's own location is fine, but its time can't place others: the park is 31 minutes away on one side.
        Assert.Equal(CafeLat, _media.Get(canonWithGps)!.Latitude);
        Assert.Null(_media.Get(nearCanon)!.Latitude);
    }

    [Fact]
    public void An_estimate_survives_reindexing_but_the_files_own_location_wins()
    {
        Add("park.heic", Noon, ParkLat, ParkLon);
        var friend = Add("from-a-friend.jpg", Noon.AddMinutes(4));
        _locations.EstimateAll();

        Add("from-a-friend.jpg", Noon.AddMinutes(4)); // the file changed, still without GPS
        Assert.Equal(LocationSource.Estimated, _media.Get(friend)!.LocationSource);
        Assert.Equal(ParkLat, _media.Get(friend)!.Latitude);

        Add("from-a-friend.jpg", Noon.AddMinutes(4), CafeLat, CafeLon); // now it has its own
        var item = _media.Get(friend)!;
        Assert.Equal((LocationSource.File, CafeLat, (double?)null, (long?)null), (item.LocationSource, item.Latitude, item.LocationAccuracy, item.LocationFromId));
        _locations.EstimateAll();
        Assert.Equal(CafeLat, _media.Get(friend)!.Latitude);
    }

    [Fact]
    public void A_removed_estimate_stays_removed_until_allowed_again()
    {
        Add("park.heic", Noon, ParkLat, ParkLon);
        var friend = Add("from-a-friend.jpg", Noon.AddMinutes(4));
        var spot = _places.Save(new Place(0, "Manito Park", ParkLat, ParkLon, 300));
        _locations.EstimateAll();

        Assert.Equal(1, _locations.RemoveEstimates([friend]));
        _locations.EstimateAll();
        Add("from-a-friend.jpg", Noon.AddMinutes(4)); // and re-indexing doesn't bring it back either
        var item = _media.Get(friend)!;
        Assert.Equal((LocationSource.Removed, (double?)null), (item.LocationSource, item.Latitude));
        Assert.DoesNotContain(friend, _media.Query(new MediaFilter { PlaceId = spot }).Select(m => m.Id));
        Assert.DoesNotContain(friend, _media.Query(new MediaFilter { Text = "manito" }).Select(m => m.Id));
        Assert.Equal((0, 1), _locations.GetCounts());

        Assert.Equal(1, _locations.AllowEstimates([friend]));
        _locations.EstimateAll();
        Assert.Equal(LocationSource.Estimated, _media.Get(friend)!.LocationSource);
    }

    [Fact]
    public void Estimates_follow_the_photos_they_came_from_and_can_all_be_cleared()
    {
        var park = Add("park.heic", Noon, ParkLat, ParkLon);
        var cafe = Add("cafe.heic", Noon.AddMinutes(30), CafeLat, CafeLon);
        var friend = Add("from-a-friend.jpg", Noon.AddMinutes(3));
        _locations.EstimateAll();
        Assert.Equal(park, _media.Get(friend)!.LocationFromId);

        _media.Remove([park]);
        Assert.Null(_media.Get(friend)!.LocationFromId); // the photo it came from is gone…
        _locations.EstimateAll();
        Assert.Null(_media.Get(friend)!.Latitude); // …and the café, 27 minutes away on one side only, is too far

        Add("park-again.heic", Noon.AddMinutes(1), ParkLat, ParkLon);
        _locations.EstimateAll();
        Assert.Equal(ParkLat, _media.Get(friend)!.Latitude);
        Assert.Equal(1, _locations.ClearEstimates());
        Assert.Equal((LocationSource.File, (double?)null), (_media.Get(friend)!.LocationSource, _media.Get(friend)!.Latitude));
        Assert.Equal(CafeLat, _media.Get(cafe)!.Latitude);
    }

    [Fact]
    public void A_track_comes_from_the_photos_the_filter_matches()
    {
        var park = Add("park.heic", Noon, ParkLat, ParkLon);
        var cafe = Add("cafe.heic", Noon.AddMinutes(60), CafeLat, CafeLon);
        var friend = Add("from-a-friend.jpg", Noon.AddMinutes(62));
        Add("next-day.heic", Noon.AddDays(1), HomeLat, HomeLon);
        _locations.EstimateAll();

        var day = new MediaFilter { From = Noon.Date, To = Noon.Date.AddDays(1) };
        var track = _locations.GetTrack(day);
        Assert.Equal([[park], [cafe, friend]], track.Stops.Select(s => s.Ids.ToArray()));
        Assert.Equal(Noon.AddMinutes(62), _locations.GetNewest(day));
        Assert.Equal([park], _locations.GetTrack(new MediaFilter { Ids = [park, 12345] }).Stops.SelectMany(s => s.Ids));
        Assert.Empty(_locations.GetTrack(new MediaFilter { From = Noon.AddYears(1) }).Stops);
    }

    [Fact]
    public void A_photo_whose_clock_was_in_another_time_zone_isnt_placed_by_the_hour_on_it()
    {
        var nairobi = Add("nairobi.heic", Noon, NairobiLat, NairobiLon, model: "iPhone 17 Pro Max", offset: EastAfrica);
        var fromHome = Add("from-home.heic", Noon.AddMinutes(-14), model: "iPhone 17", offset: Pacific); // ten hours later
        var onTheTrip = Add("pixel.jpg", Noon.AddMinutes(5), make: "Google", model: "Pixel 11 Pro", offset: EastAfrica);
        var noZone = Add("IMG_0001.JPG", Noon.AddMinutes(-3), model: "iPhone 13");
        // At home that evening, a visitor's phone still on Mountain time, and your family's five minutes later.
        var visitor = Add("visitor.heic", Noon.AddHours(5), HomeLat, HomeLon, model: "iPhone 16", offset: Mountain);
        var family = Add("family.heic", Noon.AddHours(4).AddMinutes(5), model: "iPhone 17", offset: Pacific);

        _locations.EstimateAll();
        Assert.Null(_media.Get(fromHome)!.Latitude);
        Assert.Equal(nairobi, _media.Get(onTheTrip)!.LocationFromId);
        Assert.Equal(nairobi, _media.Get(noZone)!.LocationFromId);
        Assert.Equal(visitor, _media.Get(family)!.LocationFromId);
        Assert.Equal(Pacific, _media.Get(fromHome)!.UtcOffset);
    }

    [Fact]
    public void A_track_of_people_apart_is_one_groups_with_the_others_offered()
    {
        for (var i = 0; i < 4; i++)
            Add($"nairobi{i}.heic", Noon.AddHours(i), NairobiLat, NairobiLon, model: "iPhone 17 Pro Max", offset: EastAfrica);
        var home = Add("home.heic", Noon.AddHours(-9), HomeLat, HomeLon, model: "iPhone 15 Pro Max"); // the file doesn't say
        Add("park.heic", Noon.AddHours(-8), ParkLat, ParkLon, model: "iPhone 15 Pro Max", offset: Pacific);
        Add("home2.heic", Noon.AddHours(-7), HomeLat, HomeLon, model: "iPhone 15 Pro Max", offset: Pacific);

        var day = new MediaFilter { From = Noon.Date, To = Noon.Date.AddDays(1) };
        var track = _locations.GetTrack(day);
        Assert.Equal([["iPhone 17 Pro Max"], ["iPhone 15 Pro Max"]], track.Groups.Select(g => g.Cameras));
        Assert.Equal(4, track.Stops.Single().Ids.Count);
        Assert.Contains(home, _locations.GetTrack(day, 1).Stops.SelectMany(s => s.Ids));
    }
}
