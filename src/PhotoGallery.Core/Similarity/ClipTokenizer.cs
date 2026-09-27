using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Similarity;

/// <summary>
/// OpenAI CLIP's text tokenizer (byte-level BPE, lower-cased, 77 tokens with start/end markers, padded with the end
/// marker), from the model's vocab.json and merges.txt.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int Length = 77;
    private const string StartOfText = "<|startoftext|>", EndOfText = "<|endoftext|>";

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks = [];
    private readonly Dictionary<string, string[]> _cache = [];
    private readonly char[] _byteToChar = BytesToUnicode();

    public ClipTokenizer(Dictionary<string, int> vocab, IEnumerable<string> merges)
    {
        _vocab = vocab;
        var rank = 0;
        foreach (var line in merges)
        {
            if (line.StartsWith("#version", StringComparison.Ordinal) || line.Length == 0) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2) _ranks.TryAdd((parts[0], parts[1]), rank++);
        }
        StartId = vocab[StartOfText];
        EndId = vocab[EndOfText];
    }

    public int StartId { get; }
    public int EndId { get; }

    public static ClipTokenizer Load(string vocabJson, string mergesTxt) =>
        new(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJson))!, File.ReadLines(mergesTxt));

    /// <summary>Token ids for the text, exactly <see cref="Length"/> of them.</summary>
    public long[] Encode(string text)
    {
        var ids = new List<long>(Length) { StartId };
        var clean = Whitespace().Replace(text, " ").Trim().ToLowerInvariant();
        foreach (Match word in Pattern().Matches(clean))
        {
            var chars = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(word.Value)) chars.Append(_byteToChar[b]);
            foreach (var piece in Bpe(chars.ToString()))
                if (_vocab.TryGetValue(piece, out var id)) ids.Add(id);
        }
        if (ids.Count > Length - 1) ids.RemoveRange(Length - 1, ids.Count - (Length - 1));
        ids.Add(EndId);
        while (ids.Count < Length) ids.Add(EndId);
        return [.. ids];
    }

    /// <summary>Merges a word's characters by rank; the last piece carries the end-of-word marker.</summary>
    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;
        var word = token.Select(c => c.ToString()).ToList();
        word[^1] += "</w>";
        while (word.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank)
                    (best, bestRank) = (i, r);
            if (best < 0) break;
            var (first, second) = (word[best], word[best + 1]);
            // Merge every occurrence of the pair, left to right.
            var merged = new List<string>(word.Count);
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second)
                {
                    merged.Add(first + second);
                    i++;
                }
                else merged.Add(word[i]);
            }
            word = merged;
        }
        var result = word.ToArray();
        _cache[token] = result;
        return result;
    }

    /// <summary>GPT-2's reversible byte → printable character map.</summary>
    private static char[] BytesToUnicode()
    {
        var map = new char[256];
        var used = new bool[256];
        foreach (var (from, to) in new[] { ('!', '~'), ('¡', '¬'), ('®', 'ÿ') })
            for (var c = from; c <= to; c++)
            {
                map[c] = c;
                used[c] = true;
            }
        var next = 256;
        for (var b = 0; b < 256; b++)
            if (!used[b]) map[b] = (char)next++;
        return map;
    }

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
