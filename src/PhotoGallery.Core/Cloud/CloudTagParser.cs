using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Cloud;

/// <summary>Tag kinds, stored in Tags.TagType.</summary>
public enum TagType
{
    Keyword = 0,
    Person = 1,
    /// <summary>OneDrive's photo categories (Nature, Receipt, Screenshot…).</summary>
    Category = 2,
    Place = 3,
}

public readonly record struct CloudTag(string Name, TagType Type);

/// <summary>
/// Turns OneDrive's SharePoint media columns into tags. MediaServiceOCR mixes AI tags with place parts
/// ("Dog;Animal;LikelyPleasantMemory;Medical Lake;Spokane Co.;WA;United States;99022"); place parts are
/// removed using MediaServiceLocation ("United States    WA    Medical Lake") and a few patterns.
/// </summary>
public static partial class CloudTagParser
{
    public static List<CloudTag> Parse(string? ocr, string? location, IEnumerable<string>? tagList, string? userTags)
    {
        var tags = new Dictionary<string, CloudTag>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, TagType type)
        {
            name = name.Trim();
            if (name.Length is 0 or > 60) return;
            name = char.ToUpperInvariant(name[0]) + name[1..];
            tags.TryAdd(name, new CloudTag(name, type));
        }

        var places = SplitLocation(location);
        if (PlaceTag(places) is { } place) Add(place, TagType.Place);

        foreach (var raw in (ocr ?? "").Split(';'))
        {
            var token = raw.Trim();
            if (token.Length == 0 || places.Contains(token, StringComparer.OrdinalIgnoreCase) || IsPlaceLike(token) ||
                token.EndsWith("PleasantMemory", StringComparison.Ordinal) || !LooksLikeTag(token))
                continue;
            Add(token, TagType.Keyword);
        }

        foreach (var raw in tagList ?? [])
        {
            // "__Nature_32" → "Nature", "Receipt_2" → "Receipt"
            var name = TagListSuffixRegex().Replace(raw.TrimStart('_'), "");
            if (name.Length > 0) Add(name, TagType.Category);
        }

        foreach (var raw in (userTags ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
            Add(raw, TagType.Keyword);

        return [.. tags.Values];
    }

    /// <summary>"United States    WA    Spokane" → ["United States", "WA", "Spokane"].</summary>
    public static string[] SplitLocation(string? location) =>
        string.IsNullOrWhiteSpace(location) ? [] : LocationSeparatorRegex().Split(location.Trim()).Where(p => p.Length > 0).ToArray();

    /// <summary>"Spokane, WA" for [country, region, city]; the most specific parts available otherwise.</summary>
    public static string? PlaceTag(string[] parts) => parts.Length switch
    {
        >= 3 => $"{parts[^1]}, {parts[^2]}",
        2 => $"{parts[1]}, {parts[0]}",
        1 => parts[0],
        _ => null,
    };

    /// <summary>
    /// The same column also carries text read from the photo (receipts, signs, screens): multi-line, symbols,
    /// other scripts. AI tags are short capitalised phrases ("Dog", "Coastal and oceanic landforms").
    /// </summary>
    internal static bool LooksLikeTag(string token) =>
        TagShapeRegex().IsMatch(token) && token.Split(' ').Length <= 5;

    private static bool IsPlaceLike(string token) =>
        PostalCodeRegex().IsMatch(token) ||
        token.EndsWith(" Co.", StringComparison.Ordinal) || token.EndsWith(" County", StringComparison.Ordinal) ||
        token.EndsWith(" Parish", StringComparison.Ordinal);

    [GeneratedRegex(@"^\p{Lu}\p{Ll}*(?:[ '\-&]\p{L}+)*$")]
    private static partial Regex TagShapeRegex();

    [GeneratedRegex(@"\s{2,}|\t")]
    private static partial Regex LocationSeparatorRegex();

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex TagListSuffixRegex();

    [GeneratedRegex(@"^[A-Z]?\d[\dA-Z -]{2,9}$")]
    private static partial Regex PostalCodeRegex();
}
