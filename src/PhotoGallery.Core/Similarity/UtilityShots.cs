namespace PhotoGallery.Core.Similarity;

/// <summary>
/// Utility shots: photos taken to keep a record of a thing (a receipt, a document, a screen, a box, a ticket, a
/// whiteboard, instructions, a label…) rather than of people, places and moments. Each photo's CLIP embedding is
/// compared with descriptions of both kinds: its score is how much closer it is to the nearest "record" description
/// than to the nearest "memory" one, nudged by the text read in it (a lot of text: more likely a record) and the faces
/// found in it (people: more likely a memory). Like screenshots, utility shots are left out of the timeline.
/// </summary>
public static class UtilityShots
{
    /// <summary>Bumped when the descriptions or the scoring change: every photo is scored again.</summary>
    public const int Version = 1;

    /// <summary>A score at or above this is a utility shot.</summary>
    public const double Threshold = 0.03;

    /// <summary>Records of things.</summary>
    public static readonly string[] Records =
    [
        "a photo of a receipt",
        "a photo of a printed document",
        "a close-up photo of a page of text",
        "a photo of a letter or a bill",
        "a photo of a paper form with boxes to fill in",
        "a photo of handwritten notes on paper",
        "a close-up photo of a whiteboard with writing",
        "a close-up photo of a computer screen",
        "a photo of a laptop screen showing a website",
        "a close-up photo of a phone screen",
        "a close-up photo of a television screen",
        "a screenshot",
        "a photo of a cardboard box",
        "a photo of a package",
        "a photo of a shipping label",
        "a photo of a ticket",
        "a photo of a boarding pass",
        "a photo of an instruction manual",
        "a close-up photo of a product label",
        "a photo of a barcode",
        "a close-up photo of a serial number sticker",
        "a photo of a price tag",
        "a photo of a business card",
        "a photo of a restaurant menu",
        "a photo of a calendar",
        "a photo of a medicine label",
        "a photo of an id card",
        "a close-up photo of a projected presentation slide",
        "a scanned document",
    ];

    /// <summary>
    /// Memories: people, animals, places and moments. Also what the record descriptions would otherwise pull in (talks
    /// on a stage, classrooms, libraries) and accidental shots, which aren't records of anything either.
    /// </summary>
    public static readonly string[] Memories =
    [
        "a photo of a person",
        "a photo of a group of people",
        "a photo of a family",
        "a selfie",
        "a photo of a child",
        "a photo of a baby",
        "a photo of friends at a party",
        "a photo of a wedding",
        "a photo of a birthday party",
        "a photo of people eating at a restaurant",
        "a photo of a dog",
        "a photo of a cat",
        "a photo of an animal",
        "a photo of a landscape",
        "a photo of the beach",
        "a photo of mountains",
        "a photo of a sunset",
        "a photo of a city street",
        "a photo of a famous landmark",
        "a photo of a garden with flowers",
        "a photo of a sports game",
        "a photo of a concert",
        "a photo of a meal on a table",
        "a photo of a holiday celebration",
        "a photo of a house",
        "a photo of a car",
        "a photo of a lake",
        "a photo of snow",
        "a photo of a christmas tree",
        "a photo of a person giving a talk on a stage",
        "a photo of a performance on a stage",
        "a photo of people in a meeting room",
        "a photo of a classroom",
        "a photo of a library with bookshelves",
        "a photo of a church service",
        "a photo of someone working at a desk",
        "a photo of a room in a house",
        "a photo of a building",
        "a blurry photo taken by accident",
        "a dark photo taken by accident",
    ];

    /// <summary>A photo with this many words read in it or more counts as text-heavy.</summary>
    public const int ManyWords = 25;
    public const double TextBonus = 0.015;
    public const double FacePenalty = 0.06;

    /// <summary>
    /// The score for one photo: the best "record" similarity minus the best "memory" similarity, plus a little for a
    /// lot of text and minus some for faces.
    /// </summary>
    public static double Score(ReadOnlySpan<sbyte> photo, IReadOnlyList<sbyte[]> records, IReadOnlyList<sbyte[]> memories, int words, int faces)
    {
        var record = Best(photo, records);
        var memory = Best(photo, memories);
        var score = record - memory;
        if (words >= ManyWords) score += TextBonus;
        if (faces > 0) score -= FacePenalty;
        return score;
    }

    /// <summary>Which record description a photo is closest to (to say why it's a utility shot), and how close.</summary>
    public static (int Index, double Similarity) Closest(ReadOnlySpan<sbyte> photo, IReadOnlyList<sbyte[]> descriptions)
    {
        var best = (Index: -1, Similarity: double.MinValue);
        for (var i = 0; i < descriptions.Count; i++)
        {
            var s = Embedding.Similarity(photo, descriptions[i]);
            if (s > best.Similarity) best = (i, s);
        }
        return best;
    }

    private static double Best(ReadOnlySpan<sbyte> photo, IReadOnlyList<sbyte[]> descriptions)
    {
        var best = double.MinValue;
        foreach (var d in descriptions) best = Math.Max(best, Embedding.Similarity(photo, d));
        return best;
    }
}
