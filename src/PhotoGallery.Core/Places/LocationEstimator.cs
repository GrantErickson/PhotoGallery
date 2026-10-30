namespace PhotoGallery.Core.Places;

/// <summary>
/// A located photo or video: when (its clock's local time, Unix seconds like Media.DateTaken, and how far that clock was
/// ahead of UTC, in minutes), where, and which camera took it (a number per make and model; 0 when unknown).
/// </summary>
public readonly record struct Fix(long Id, long Time, double Latitude, double Longitude, int Offset = 0, int Camera = 0)
{
    /// <summary>When it was really taken: UTC Unix seconds.</summary>
    public long Utc => Time - Offset * 60L;
}

/// <summary>Where a photo without a location probably was: where <see cref="FromId"/> was taken, give or take <see cref="AccuracyMeters"/>.</summary>
public sealed record LocationEstimate(long Id, double Latitude, double Longitude, double AccuracyMeters, long FromId);

/// <summary>
/// Places photos that have no location (someone else's, with the location stripped when they were shared, or a camera
/// without GPS) where the located photo nearest in time was taken. Only clocks that are set for you can be lined up
/// (<see cref="IsPhoneMaker"/>), and they're lined up by real time when the photo says its clock's time zone (most
/// phones' do): your family's photo at 1 pm in Spokane isn't next to yours at 1 pm in Nairobi.
/// <para>
/// Tuned on the library by hiding the location of 60,000 located photos and estimating it from the rest: the nearest
/// photo beat drawing a line between the ones before and after (a photo two minutes after one at a café and four hours
/// before one at home is at the café). Close photos on both sides that agree mean you stayed put, which holds for up to
/// an hour; with a photo on one side only, or sides far apart, the guess fades fast after 15 minutes. The radius grows
/// with the gap and with how far the two sides are apart, so that about four in five estimates fall inside it.
/// </para>
/// <para>
/// People apart aren't mixed up: when the located photos within the hour show more than one place (no one could have
/// taken them all, <see cref="Travel"/>), only the photo's own camera's can say which it was taken in; and a photo
/// whose clock was in a time zone that isn't in effect near where it would be put wasn't taken there.
/// </para>
/// </summary>
public sealed class LocationEstimator
{
    /// <summary>A photo within this long, before or after, is enough on its own.</summary>
    public static readonly TimeSpan OneSided = TimeSpan.FromMinutes(15);
    /// <summary>With located photos on both sides (each within <see cref="Bracket"/>), the nearer may be this far away.</summary>
    public static readonly TimeSpan Bracketed = TimeSpan.FromHours(1);
    public static readonly TimeSpan Bracket = TimeSpan.FromHours(2);
    /// <summary>Estimates less sure than this aren't made.</summary>
    public const double MaxAccuracyMeters = 1000;
    public const double MinAccuracyMeters = 25;

    /// <summary>How far you drift (m/s) from the nearer photo: less when photos on both sides keep you in view.</summary>
    private const double DriftBracketed = 0.25, DriftOneSided = 0.8;

    /// <summary>The located photos by real time and by clock time, and each camera's own.</summary>
    private readonly Fix[] _byUtc, _byTime;
    private readonly Dictionary<int, (Fix[] ByUtc, Fix[] ByTime)> _byCamera;

    /// <param name="fixes">Located photos with trustworthy times, in any order; lone GPS spikes are left out (<see cref="WithoutSpikes"/>).</param>
    public LocationEstimator(IEnumerable<Fix> fixes)
    {
        _byUtc = WithoutSpikes(fixes.ToList()).ToArray();
        _byTime = ByTime(_byUtc);
        _byCamera = _byUtc.GroupBy(f => f.Camera).ToDictionary(g => g.Key, g => (g.ToArray(), ByTime(g)));
    }

    /// <summary>
    /// The estimate for a photo taken at <paramref name="time"/> (its clock's), or null if no located photo is near
    /// enough, or it can't be told which of the places people were in it was taken in.
    /// </summary>
    /// <param name="offset">How far its clock was ahead of UTC (minutes), if the file says; if not, its clock is taken to read the same as theirs.</param>
    /// <param name="camera">Which camera took it, as in <see cref="Fix.Camera"/>.</param>
    public LocationEstimate? Estimate(long id, long time, int? offset = null, int camera = 0)
    {
        var real = offset is not null;
        var at = real ? time - offset!.Value * 60L : time;
        var fixes = real ? _byUtc : _byTime;
        if (!Together(fixes, at, real))
        {
            // People were in more than one place: only this camera's own photos can say which it was in.
            if (!_byCamera.TryGetValue(camera, out var own)) return null;
            fixes = real ? own.ByUtc : own.ByTime;
            if (!Together(fixes, at, real)) return null;
        }
        if (Nearest(fixes, id, at, real) is not var (estimate, from)) return null;
        // A clock in a zone not in effect near there was somewhere else, unless the located photo's clock was in it too.
        if (offset is { } zone && zone != from.Offset && !TimeZones.InEffectNear(from.Latitude, from.Longitude, from.Utc, zone))
            return null;
        return estimate;
    }

    private static (LocationEstimate, Fix)? Nearest(Fix[] fixes, long id, long time, bool real)
    {
        var after = FirstAtOrAfter(fixes, time, real);
        // The last at or before: the one before "after", or "after" itself when it's at the same moment.
        var before = after < fixes.Length && At(fixes[after], real) == time ? after : after - 1;
        Fix? b = before >= 0 ? fixes[before] : null;
        Fix? a = after < fixes.Length ? fixes[after] : null;
        if (b is null && a is null) return null;

        var gapBefore = b is { } fb ? time - At(fb, real) : long.MaxValue;
        var gapAfter = a is { } fa ? At(fa, real) - time : long.MaxValue;
        var nearest = gapBefore <= gapAfter ? b!.Value : a!.Value;
        double gap = Math.Min(gapBefore, gapAfter);

        double accuracy;
        if (Math.Max(gapBefore, gapAfter) <= Bracket.TotalSeconds)
        {
            if (gap > Bracketed.TotalSeconds) return null;
            // Photos on both sides: you were somewhere along the way between them, nearer the nearer one.
            var spread = Geo.Meters(b!.Value.Latitude, b.Value.Longitude, a!.Value.Latitude, a.Value.Longitude);
            var span = gapBefore + gapAfter;
            accuracy = DriftBracketed * gap + (span > 0 ? spread * Math.Min(1, 2 * gap / span) : 0);
        }
        else
        {
            if (gap > OneSided.TotalSeconds) return null;
            accuracy = DriftOneSided * gap;
        }
        accuracy = Math.Max(MinAccuracyMeters, accuracy);
        return accuracy > MaxAccuracyMeters ? null
            : (new LocationEstimate(id, nearest.Latitude, nearest.Longitude, Math.Round(accuracy), nearest.Id), nearest);
    }

    /// <summary>
    /// Whether the located photos within an hour of <paramref name="time"/> could all be one party's: none further from
    /// the one nearest in time than anyone could have gone (<see cref="Travel"/>).
    /// </summary>
    private static bool Together(Fix[] fixes, long time, bool real)
    {
        var window = (long)Bracketed.TotalSeconds;
        var (first, end) = (FirstAtOrAfter(fixes, time - window, real), FirstAtOrAfter(fixes, time + window + 1, real));
        if (end - first < 2) return true;
        var after = FirstAtOrAfter(fixes, time, real);
        var nearest = after >= end || after > first && time - At(fixes[after - 1], real) <= At(fixes[after], real) - time ? after - 1 : after;
        for (var i = first; i < end; i++)
            if (!Travel.Possible(fixes[nearest], fixes[i])) return false;
        return true;
    }

    /// <summary>
    /// Phones and tablets set their clocks from the network, so their photos line up with yours wherever they were
    /// taken. A camera keeps whatever time it was given: in the library, a Canon ran an hour (and some years several
    /// hours) out for months at a time, which would put its photos where your phone was an hour before.
    /// </summary>
    public static bool IsPhoneMaker(string? make) => make is not null && PhoneMakers.Contains(make.Trim());

    private static readonly HashSet<string> PhoneMakers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Apple", "Google", "samsung", "motorola", "LGE", "HTC", "OnePlus", "Xiaomi", "HUAWEI", "HONOR", "OPPO", "vivo",
        "realme", "HMD Global", "Nokia", "Microsoft", "Nothing", "Fairphone",
    };

    /// <summary>
    /// A camera whose photos carry GPS somewhere in the library (at least this share of them) has a phone's or a GPS
    /// receiver's clock too. Phones whose photos were shared without the location still pass (one model: 2 %).
    /// </summary>
    public const double GpsShare = 0.01;

    /// <summary>
    /// Leaves out lone wild fixes: a photo kilometres away from the same camera's just before and after, which agree
    /// with each other, reached at more than 100 km/h each way (a phone's GPS jumping indoors). About one photo in a
    /// thousand. Another camera somewhere else in between is someone else, not a spike. Returns them by real time.
    /// </summary>
    public static List<Fix> WithoutSpikes(IReadOnlyList<Fix> fixes)
    {
        const double Away = 1000, Together = 300, Speed = 30; // metres, metres, m/s
        var kept = new List<Fix>(fixes.Count);
        foreach (var camera in fixes.GroupBy(f => f.Camera))
        {
            var own = camera.OrderBy(f => f.Utc).ThenBy(f => f.Id).ToList();
            for (var i = 0; i < own.Count; i++)
            {
                if (i > 0 && i < own.Count - 1)
                {
                    var (p, f, n) = (own[i - 1], own[i], own[i + 1]);
                    if (n.Utc - p.Utc <= 3600
                        && Geo.Meters(p.Latitude, p.Longitude, n.Latitude, n.Longitude) < Together
                        && Geo.Meters(p.Latitude, p.Longitude, f.Latitude, f.Longitude) is var outward && outward > Away
                        && Geo.Meters(f.Latitude, f.Longitude, n.Latitude, n.Longitude) is var back && back > Away
                        && outward / Math.Max(1, f.Utc - p.Utc) > Speed && back / Math.Max(1, n.Utc - f.Utc) > Speed)
                        continue;
                }
                kept.Add(own[i]);
            }
        }
        kept.Sort((x, y) => x.Utc != y.Utc ? x.Utc.CompareTo(y.Utc) : x.Id.CompareTo(y.Id));
        return kept;
    }

    private static Fix[] ByTime(IEnumerable<Fix> fixes) => fixes.OrderBy(f => f.Time).ThenBy(f => f.Id).ToArray();

    private static long At(Fix fix, bool real) => real ? fix.Utc : fix.Time;

    private static int FirstAtOrAfter(Fix[] fixes, long time, bool real)
    {
        int lo = 0, hi = fixes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (At(fixes[mid], real) < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
