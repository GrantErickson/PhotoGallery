using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Ocr;

/// <summary>A recognised word and where it is, as fractions of the upright picture's longer side (like face boxes).</summary>
public sealed record OcrWord(
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double Width,
    [property: JsonPropertyName("h")] double Height)
{
    /// <summary>The box in pixels (or any units) of the upright picture shown at this size.</summary>
    public (double X, double Y, double Width, double Height) In(double width, double height)
    {
        var scale = Math.Max(width, height);
        return (X * scale, Y * scale, Width * scale, Height * scale);
    }
}

/// <summary>A line of text as the recogniser grouped it.</summary>
public sealed record OcrLine([property: JsonPropertyName("w")] IReadOnlyList<OcrWord> Words)
{
    [JsonIgnore]
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>The text found in a photo (none for most photos).</summary>
public sealed record PhotoText(long MediaId, IReadOnlyList<OcrLine> Lines, string Engine = "")
{
    public bool HasText => Lines.Count > 0;
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>
/// Keeps what reads as text and drops what the recogniser finds in textures, foliage and patterns: lone letters and
/// symbols, and lines without a single real word. A photo needs at least two real words (or one of four letters or
/// more, like "EXIT") to count as having text.
/// </summary>
public static partial class OcrCleaner
{
    public static List<OcrLine> Clean(IEnumerable<OcrLine> lines)
    {
        var kept = new List<OcrLine>();
        var strong = 0;
        var longest = 0;
        foreach (var line in lines)
        {
            var words = line.Words.Where(w => w.Text.Length > 0).ToList();
            var strongWords = words.Where(w => IsStrong(w.Text)).ToList();
            if (strongWords.Count == 0) continue;
            // In a line with real words, short tokens ("5", "&", "No.") are part of it; alone they're noise.
            kept.Add(new OcrLine(words.Where(w => Alphanumerics(w.Text) > 0).ToList()));
            strong += strongWords.Count;
            longest = Math.Max(longest, strongWords.Max(w => Letters().Count(w.Text)));
        }
        return strong >= 2 || longest >= 4 ? kept : [];
    }

    /// <summary>Three or more letters, three or more digits, or a mix of three or more letters and digits.</summary>
    private static bool IsStrong(string word) => Letters().Count(word) >= 3 || Digits().Count(word) >= 3 || Alphanumerics(word) >= 3 && Letters().Count(word) >= 1;

    private static int Alphanumerics(string word) => Letters().Count(word) + Digits().Count(word);

    [GeneratedRegex(@"\p{L}")]
    private static partial Regex Letters();

    [GeneratedRegex(@"\p{Nd}")]
    private static partial Regex Digits();
}
