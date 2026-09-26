namespace PhotoGallery.Core.Transcripts;

/// <summary>
/// Joins the speech found by voice detection into one buffer for the recogniser, leaving out long silences (where
/// Whisper tends to invent text) while keeping short pauses so sentences stay whole, and maps times in the joined
/// audio back to the original. Whisper costs the same per 30-second window however little speech it holds, so one
/// joined buffer is much cheaper than recognising each stretch on its own.
/// </summary>
public sealed class SpeechStitcher
{
    /// <summary>Silence put between joined stretches, so the recogniser hears a pause there.</summary>
    public const double Gap = 1.0;

    private readonly List<(double Joined, double Original)> _starts = [];

    public float[] Samples { get; }
    public int SampleRate { get; }
    public double SpeechSeconds { get; }

    private SpeechStitcher(float[] samples, int sampleRate, double speech)
    {
        Samples = samples;
        SampleRate = sampleRate;
        SpeechSeconds = speech;
    }

    /// <param name="regions">Speech (start, end) in seconds, in order, as voice detection found it.</param>
    public static SpeechStitcher Join(ReadOnlySpan<float> audio, int sampleRate, IEnumerable<(double Start, double End)> regions)
    {
        var gap = (int)(Gap * sampleRate);
        var pieces = new List<(int From, int To, double Start)>();
        foreach (var (start, end) in regions)
        {
            var from = Math.Clamp((int)(start * sampleRate), 0, audio.Length);
            var to = Math.Clamp((int)Math.Ceiling(end * sampleRate), from, audio.Length);
            if (to > from) pieces.Add((from, to, from / (double)sampleRate));
        }

        var total = pieces.Sum(p => p.To - p.From + gap);
        var samples = new float[total];
        var stitcher = new SpeechStitcher(samples, sampleRate, pieces.Sum(p => (p.To - p.From) / (double)sampleRate));
        var at = 0;
        foreach (var (from, to, start) in pieces)
        {
            stitcher._starts.Add((at / (double)sampleRate, start));
            audio[from..to].CopyTo(samples.AsSpan(at));
            at += to - from + gap; // the gap stays zero
        }
        return stitcher;
    }

    /// <summary>A time in the joined audio as a time in the original.</summary>
    public double ToOriginal(double joined)
    {
        if (_starts.Count == 0) return joined;
        var i = _starts.FindLastIndex(s => s.Joined <= joined + 1e-6);
        var (pieceJoined, pieceOriginal) = _starts[Math.Max(0, i)];
        return pieceOriginal + Math.Max(0, joined - pieceJoined);
    }
}
