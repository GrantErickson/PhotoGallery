namespace PhotoGallery.Core.Transcripts;

/// <summary>A stretch attributed to one voice by speaker diarization (seconds from the start of the video).</summary>
public readonly record struct SpeakerTurn(double Start, double End, int Speaker);

/// <summary>
/// Puts speaker numbers on transcript segments from diarization turns, cautiously: diarization readily splits one
/// person into several "speakers" (walking outside, wind, a change of tone), so voices with only a small share of the
/// speech are folded into their neighbours, and a transcript is only labelled when at least two voices remain.
/// Speakers are numbered 1, 2, … in the order they first speak.
/// </summary>
public static class SpeakerAssigner
{
    /// <summary>A voice needs at least this share of the speech to count as a separate speaker…</summary>
    public const double MinShare = 0.15;
    /// <summary>…and at least this many seconds of it.</summary>
    public const double MinSeconds = 4;

    public static List<TranscriptSegment> Assign(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<SpeakerTurn> turns)
    {
        // Each segment goes to the voice that overlaps it most.
        var labels = segments.Select(s =>
        {
            var best = turns.GroupBy(t => t.Speaker)
                .Select(g => (Speaker: g.Key, Overlap: g.Sum(t => Math.Max(0, Math.Min(s.End, t.End) - Math.Max(s.Start, t.Start)))))
                .Where(x => x.Overlap > 0)
                .OrderByDescending(x => x.Overlap)
                .FirstOrDefault();
            return best.Overlap > 0 ? best.Speaker : (int?)null;
        }).ToList();

        // Voices with too little speech aren't separate people: they take the label of the segment before (or after).
        var total = segments.Sum(s => s.End - s.Start);
        var major = labels.Select((l, i) => (l, Seconds: segments[i].End - segments[i].Start))
            .Where(x => x.l is not null)
            .GroupBy(x => x.l!.Value)
            .Where(g => g.Sum(x => x.Seconds) >= Math.Max(MinSeconds, MinShare * total))
            .Select(g => g.Key)
            .ToHashSet();
        if (major.Count < 2) return segments.Select(s => s with { Speaker = null }).ToList();

        int? previous = null;
        for (var i = 0; i < labels.Count; i++)
        {
            if (labels[i] is { } l && major.Contains(l)) previous = l;
            else labels[i] = previous;
        }
        int? next = null;
        for (var i = labels.Count - 1; i >= 0; i--)
        {
            if (labels[i] is { } l) next = l;
            else labels[i] = next;
        }

        // Number by first appearance.
        var numbers = new Dictionary<int, int>();
        return segments.Select((s, i) =>
        {
            if (labels[i] is not { } l) return s with { Speaker = null };
            if (!numbers.TryGetValue(l, out var n)) numbers[l] = n = numbers.Count + 1;
            return s with { Speaker = n };
        }).ToList();
    }
}
