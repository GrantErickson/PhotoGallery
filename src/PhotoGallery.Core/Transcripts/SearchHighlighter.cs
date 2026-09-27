using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Transcripts;

/// <summary>A run of text to highlight.</summary>
public readonly record struct TextMatch(int Start, int Length);

/// <summary>
/// Finds the words a search matched, the way the search index does (SQLite FTS5, unicode61 tokenizer with diacritics
/// removed, each search word a prefix term): words are runs of letters and digits, compared without case or accents,
/// and a search word matches any word it starts ("birth" → "birthday"). A search word with punctuation inside
/// ("don't") matches the same run of words ("don", "t…"). Whole words are highlighted.
/// </summary>
public static partial class SearchHighlighter
{
    public static List<TextMatch> Find(string text, string? query)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(text)) return matches;
        var words = Words().Matches(text).Select(m => (m.Index, m.Length, Key: Fold(m.Value))).ToList();
        foreach (var term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = Words().Matches(term).Select(m => Fold(m.Value)).ToList();
            if (parts.Count == 0) continue;
            for (var i = 0; i + parts.Count <= words.Count; i++)
            {
                var hit = true;
                for (var k = 0; k < parts.Count && hit; k++)
                    hit = k == parts.Count - 1 ? words[i + k].Key.StartsWith(parts[k], StringComparison.Ordinal) : words[i + k].Key == parts[k];
                if (!hit) continue;
                var last = words[i + parts.Count - 1];
                matches.Add(new TextMatch(words[i].Index, last.Index + last.Length - words[i].Index));
            }
        }
        // In reading order, overlaps merged (two search words can hit the same word).
        var merged = new List<TextMatch>();
        foreach (var m in matches.OrderBy(m => m.Start))
        {
            if (merged.Count > 0 && m.Start < merged[^1].Start + merged[^1].Length)
            {
                var end = Math.Max(merged[^1].Start + merged[^1].Length, m.Start + m.Length);
                merged[^1] = merged[^1] with { Length = end - merged[^1].Start };
            }
            else merged.Add(m);
        }
        return merged;
    }

    /// <summary>Lower case without accents ("Café" → "cafe").</summary>
    private static string Fold(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    [GeneratedRegex(@"[\p{L}\p{N}\p{M}]+")]
    private static partial Regex Words();
}
