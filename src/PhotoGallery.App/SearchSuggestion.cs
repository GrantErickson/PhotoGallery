using PhotoGallery.App.Services;
using PhotoGallery.Core.Cloud;

namespace PhotoGallery.App;

/// <summary>A line in the search box's list: a recent search, or a person, place or tag whose name matches.</summary>
public sealed record SearchSuggestion(string Query, string Kind, string Glyph)
{
    public override string ToString() => Query;

    private const int Max = 10;

    /// <summary>
    /// Recent searches first (all of them while nothing is typed), then people, places and tags whose names contain
    /// the text, the ones starting with it first.
    /// </summary>
    public static List<SearchSuggestion> For(string text, AppServices services, bool includeRecent = true)
    {
        text = text.Trim();
        var recent = (includeRecent ? services.Settings.RecentSearches : [])
            .Where(r => text.Length == 0 || r.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(r => new SearchSuggestion(r, "recent", "\uE81C"));
        if (text.Length == 0) return recent.Take(Max).ToList();

        var names = services.People.GetPeople().Where(p => p.Name is not null).Select(p => new SearchSuggestion(p.Name!, "person", "\uE77B"))
            .Concat(services.Places.GetAll().Select(p => new SearchSuggestion(p.Name, "your place", "\uE707")))
            .Concat(PlacesInUse(services).Select(name => new SearchSuggestion(name, "place", "\uE707")))
            .Concat(services.Collections.GetTags().Where(t => t.Count > 0 && t.TagType != TagType.Person)
                .Select(t => new SearchSuggestion(t.Name, t.TagType == TagType.Place ? "place" : "tag", t.TagType == TagType.Place ? "\uE707" : "\uE8EC")))
            .Where(s => s.Query.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(s => s.Query.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        return recent.Concat(names).DistinctBy(s => s.Query.ToLowerInvariant()).Take(Max).ToList();
    }

    private static (DateTime At, List<string> Names)? _placesInUse;

    /// <summary>OpenStreetMap places photos were taken at (re-read at most once a minute; the list can be long).</summary>
    private static List<string> PlacesInUse(AppServices services)
    {
        if (_placesInUse is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Names;
        var names = services.Pois.GetNamesInUse();
        _placesInUse = (DateTime.UtcNow, names);
        return names;
    }

    /// <summary>Remembers a search (most recent first, no repeats).</summary>
    public static void Remember(string text, AppServices services)
    {
        var list = services.Settings.RecentSearches;
        list.RemoveAll(r => string.Equals(r, text, StringComparison.CurrentCultureIgnoreCase));
        list.Insert(0, text);
        if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
        services.SaveSettings();
    }
}
