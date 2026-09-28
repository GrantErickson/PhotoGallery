using PhotoGallery.Core.Places;

namespace PhotoGallery.Core.Tests;

public class PlaceSearchTests
{
    private static CityIndex Towns()
    {
        var index = new CityIndex();
        index.Add(new City("Spokane Valley", "US", "WA", 47.67, -117.24, 102_000));
        index.Add(new City("Spokane", "US", "WA", 47.66, -117.43, 229_000));
        index.Add(new City("Spokane", "US", "MO", 36.98, -93.31, 200)); // a small one, in Missouri
        index.Add(new City("Paris", "FR", "11", 48.85, 2.35, 2_138_000));
        index.Add(new City("Paris", "US", "TX", 33.66, -95.56, 24_000));
        index.Add(new City("Montréal", "CA", "10", 45.51, -73.59, 1_762_000), "Montreal");
        index.Add(new City("Zürich", "CH", "ZH", 47.37, 8.54, 341_000), "Zurich");
        index.Add(new City("East Paris", "US", "MI", 42.9, -85.6, 3_000));
        return index;
    }

    [Theory]
    [InlineData("Zürich", "zurich")]
    [InlineData("  MONTRÉAL ", "montreal")]
    [InlineData("Café", "cafe")]
    public void Names_are_compared_without_case_or_accents(string name, string folded) => Assert.Equal(folded, PlaceSearch.Fold(name));

    [Theory]
    [InlineData("spokane", "spokane", 3)]
    [InlineData("spokane valley", "spokane", 2)]
    [InlineData("east paris", "paris", 1)]
    [InlineData("comparison", "paris", 0)] // inside a word doesn't count
    [InlineData("paris", "", 0)]
    public void Scores_a_name_against_what_was_typed(string name, string query, int score) => Assert.Equal(score, PlaceSearch.Score(name, query));

    [Fact]
    public void Towns_come_best_match_first_then_biggest()
    {
        var towns = Towns();
        Assert.Equal(["Spokane, WA", "Spokane, MO", "Spokane Valley, WA"], towns.Search("spokane").Select(c => c.DisplayName));
        Assert.Equal(["Paris", "Paris", "East Paris"], towns.Search("Paris").Select(c => c.Name));
        Assert.Equal("FR", towns.Search("Paris")[0].CountryCode); // the biggest Paris first
        Assert.Equal("Montréal", towns.Search("montreal").Single().Name);
        Assert.Equal("Zürich", towns.Search("zurich").Single().Name);
        Assert.Empty(towns.Search("s"));
    }

    [Fact]
    public void A_state_or_country_after_a_comma_narrows_it_down()
    {
        var towns = Towns();
        Assert.Equal(["Spokane, MO"], towns.Search("Spokane, MO").Select(c => c.DisplayName));
        Assert.Equal("TX", towns.Search("paris, tx").Single().Admin1);
        Assert.Equal("FR", towns.Search("Paris, France").Single().CountryCode);
        Assert.Equal("US", towns.Search("Paris, US").First().CountryCode);
    }

    [Fact]
    public void Your_places_come_first_then_places_near_your_photos_then_towns()
    {
        Place[] yours = [new(1, "Grandma's", 47.6, -117.4, 200), new(2, "Paris trip hotel", 48.86, 2.34, 100)];
        Poi[] spots =
        [
            new(1, "w1", "Paris Café", "café", 47.62, -117.41),
            new(2, "w2", "Paris Café", "café", 47.62, -117.41), // the same place twice (a node and its building)
            new(3, "w3", "Riverfront Park", "park", 47.66, -117.42, 47.65, -117.43, 47.67, -117.41),
        ];
        var hits = PlaceSearch.Local("paris", yours, spots, Towns());
        Assert.Equal(["your place Paris trip hotel", "place near your photos Paris Café", "town Paris", "town Paris", "town East Paris"],
            hits.Select(h => $"{h.Kind} {h.Name}"));
        Assert.True(hits[0].HasBounds);
        Assert.Equal("café · place near your photos", hits[1].Caption);

        var park = PlaceSearch.Local("riverfront", yours, spots, null).Single();
        Assert.Equal((47.65, -117.43, 47.67, -117.41), (park.South, park.West, park.North, park.East));
        Assert.Empty(PlaceSearch.Local("x", yours, spots, Towns()));
    }

    [Fact]
    public void Reads_nominatims_answers()
    {
        const string json = """
            [
              {"place_id":1,"lat":"47.6334","lon":"-117.4118","name":"Manito Park","display_name":"Manito Park, Spokane, Spokane County, Washington, United States",
               "boundingbox":["47.6300","47.6372","-117.4150","-117.4080"],"type":"park"},
              {"place_id":2,"lat":"47.6","lon":"-117.4","name":"","display_name":"1234 Main Street, Spokane, Washington"},
              {"place_id":3,"lat":"not a number","lon":"0","name":"Broken"}
            ]
            """;
        var hits = Nominatim.Parse(json);
        Assert.Equal(2, hits.Count);
        Assert.Equal(("Manito Park", "Spokane, Spokane County, Washington, United States"), (hits[0].Name, hits[0].Detail));
        Assert.Equal((47.63, -117.415, 47.6372, -117.408), (hits[0].South, hits[0].West, hits[0].North, hits[0].East));
        Assert.Equal(("1234 Main Street", "Spokane, Washington"), (hits[1].Name, hits[1].Detail));
        Assert.False(hits[1].HasBounds);
        Assert.Empty(Nominatim.Parse("{}"));
        Assert.Contains("q=Manito%20Park", Nominatim.Url(" Manito Park ", "en"));
    }
}
