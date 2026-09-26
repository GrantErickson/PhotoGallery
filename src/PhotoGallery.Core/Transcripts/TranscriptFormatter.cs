using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Transcripts;

/// <summary>Turns recognised segments into readable text: drops noise, then groups sentences into paragraphs.</summary>
public static partial class TranscriptFormatter
{
    /// <summary>A pause this long after a finished sentence starts a new paragraph.</summary>
    private const double ParagraphPause = 1.5;
    /// <summary>A pause this long starts a new paragraph even mid-sentence.</summary>
    private const double LongPause = 4.0;
    /// <summary>Paragraphs longer than this break at the next sentence end.</summary>
    private const double MaxParagraph = 30.0;

    /// <summary>
    /// Removes what isn't speech worth reading: empty text, lone fillers ("Um."), sound labels ("[Music]",
    /// "(laughs)"), Whisper's stock phrases for silence, and the same line repeated back to back (a decoding loop).
    /// </summary>
    public static List<TranscriptSegment> Clean(IEnumerable<TranscriptSegment> segments)
    {
        var result = new List<TranscriptSegment>();
        foreach (var segment in segments)
        {
            var text = Whitespace().Replace(segment.Text, " ").Trim();
            text = SoundLabel().Replace(text, "").Trim();
            if (text.Length == 0 || Filler().IsMatch(text) || StockPhrase().IsMatch(text)) continue;
            if (result.Count > 0 && string.Equals(Normalize(result[^1].Text), Normalize(text), StringComparison.OrdinalIgnoreCase))
            {
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, segment.End) };
                continue;
            }
            result.Add(segment with { Text = text });
        }
        return result;
    }

    /// <summary>Groups segments into paragraphs at pauses, speaker changes and (for long stretches) sentence ends.</summary>
    public static List<TranscriptParagraph> Paragraphs(IReadOnlyList<TranscriptSegment> segments)
    {
        var paragraphs = new List<TranscriptParagraph>();
        var current = new List<TranscriptSegment>();
        foreach (var segment in segments)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var pause = segment.Start - last.End;
                var sentenceEnded = EndsSentence(last.Text);
                var breakHere = segment.Speaker != last.Speaker ||
                                pause >= LongPause ||
                                (sentenceEnded && pause >= ParagraphPause) ||
                                (sentenceEnded && segment.Start - current[0].Start >= MaxParagraph);
                if (breakHere)
                {
                    paragraphs.Add(Join(current));
                    current.Clear();
                }
            }
            current.Add(segment);
        }
        if (current.Count > 0) paragraphs.Add(Join(current));
        return paragraphs;
    }

    /// <summary>The whole transcript as plain text, paragraphs separated by blank lines (for copying and search).</summary>
    public static string PlainText(IEnumerable<TranscriptParagraph> paragraphs, Func<int, string>? speakerName = null) =>
        string.Join(Environment.NewLine + Environment.NewLine, paragraphs.Select(p =>
            p.Speaker is { } s && speakerName is not null ? $"{speakerName(s)}: {p.Text}" : p.Text));

    private static TranscriptParagraph Join(List<TranscriptSegment> segments) =>
        new(segments[0].Start, segments[^1].End, string.Join(" ", segments.Select(s => s.Text)), segments[0].Speaker);

    private static bool EndsSentence(string text) => text.TrimEnd('"', '\'', ')', '”', '’') is { Length: > 0 } t && t[^1] is '.' or '?' or '!' or '…';

    private static string Normalize(string text) => Punctuation().Replace(text, "").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:(?:um+|uh+|hmm+|mm+|mhm|ah+|er+|oh)[\s,.!?…-]*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    [GeneratedRegex(@"\[[^\]]*\]|\((?:music|laugh\w*|applause|inaudible|silence|noise|cough\w*|sigh\w*)\)|♪+", RegexOptions.IgnoreCase)]
    private static partial Regex SoundLabel();

    [GeneratedRegex(@"^(?:thanks? (?:you )?for watching[.!]*|please subscribe[.!]*|subtitles? by .*|transcribed by .*|you[.!]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex StockPhrase();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex Punctuation();
}
