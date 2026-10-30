namespace PhotoGallery.Core.Places;

/// <summary>A place on a track: photos taken one after another within <see cref="TrackBuilder.StopRadius"/> of where it started.</summary>
public sealed class TrackStop
{
    /// <summary>1, 2, 3… in time order.</summary>
    public int Number { get; init; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>The first and last photo's time (local wall clock, Unix seconds).</summary>
    public long Start { get; set; }
    public long End { get; set; }
    /// <summary>Which day of the track it's on: 0 for the first day with photos, and so on.</summary>
    public int Day { get; set; }
    /// <summary>Its photos, oldest first: the located ones, and those whose location was estimated from them.</summary>
    public IReadOnlyList<long> Ids { get; internal set; } = [];

    public DateTime StartLocal => TrackBuilder.Local(Start);
    public DateTime EndLocal => TrackBuilder.Local(End);

    /// <summary>The first and last photo's real time (UTC Unix seconds), for the order and the time between stops.</summary>
    internal long StartUtc { get; set; }
    internal long EndUtc { get; set; }
}

/// <summary>
/// From one stop to the next (indexes into <see cref="Track.Stops"/>). Known: soon after, at ground speed, so the line is
/// roughly the way; otherwise only the order is known (overnight, a flight).
/// </summary>
public sealed record TrackLeg(int From, int To, bool Known);

/// <summary>
/// Photos that could all be one party's, when photos were taken in more than one place at once (you on a trip, your
/// family at home): how many, where most of those only it has were taken, and the cameras that took them, most first.
/// </summary>
public sealed record TrackGroup(int Photos, double Latitude, double Longitude, IReadOnlyList<string> Cameras);

/// <summary>Where the photos were taken, in order: the places stopped at and the way between them.</summary>
public sealed record Track(IReadOnlyList<TrackStop> Stops, IReadOnlyList<TrackLeg> Legs, double Meters, int Days)
{
    public static Track Empty { get; } = new([], [], 0, 0);

    /// <summary>When people were apart, the groups (the one led by the camera with the most photos first) and which one this is the track of; otherwise none.</summary>
    public IReadOnlyList<TrackGroup> Groups { get; init; } = [];
    public int Group { get; init; }
}

/// <summary>Recreates a track from photos' times and locations (<see cref="Build"/>).</summary>
public static class TrackBuilder
{
    /// <summary>Photos within this of a stop's middle, one after another, are the same stop.</summary>
    public const double StopRadius = 200;
    /// <summary>A longer pause without photos starts a new stop even in the same place (home in the morning and again at night).</summary>
    public static readonly TimeSpan StopGap = TimeSpan.FromHours(2);
    /// <summary>Stops further apart than this in time are separate visits: no line between them.</summary>
    public static readonly TimeSpan VisitGap = TimeSpan.FromHours(36);
    /// <summary>Faster than this between stops (250 km/h) is a flight, or a gap: the line is only the order.</summary>
    private const double GroundSpeed = 70;
    /// <summary>Photos placed by time rather than location (estimated from a photo outside the track, or a GPS spike) join a stop this close in time.</summary>
    private static readonly TimeSpan Attach = TimeSpan.FromHours(1);
    /// <summary>Fewer photos than this somewhere else (and under a tenth of them) are a glitch, not a group.</summary>
    private const int MinGroup = 3;
    private const int MaxGroups = 6;

    /// <param name="fixes">Photos with their own location.</param>
    /// <param name="estimated">Photos with an estimated location: when (real time, UTC Unix seconds), and the located photo it came from.</param>
    /// <param name="group">When people were apart, whose track: an index into <see cref="Track.Groups"/>.</param>
    /// <param name="cameraName">The name of a <see cref="Fix.Camera"/>, for <see cref="TrackGroup.Cameras"/>.</param>
    public static Track Build(IReadOnlyList<Fix> fixes, IReadOnlyList<(long Id, long Utc, long? FromId)> estimated,
                              int group = 0, Func<int, string?>? cameraName = null)
    {
        var clean = LocationEstimator.WithoutSpikes(fixes);
        if (clean.Count == 0) return Track.Empty;
        var groups = Split(clean);
        group = Math.Clamp(group, 0, groups.Count - 1);
        var mine = groups[group];

        var stops = new List<TrackStop>();
        var members = new List<List<(long Utc, long Id)>>();
        var stopOf = new Dictionary<long, int>();
        var count = 0;
        foreach (var fix in mine)
        {
            var current = stops.Count > 0 ? stops[^1] : null;
            if (current is null || fix.Utc - current.EndUtc > StopGap.TotalSeconds
                || Geo.Meters(current.Latitude, current.Longitude, fix.Latitude, fix.Longitude) > StopRadius)
            {
                current = new TrackStop
                {
                    Number = stops.Count + 1, Latitude = fix.Latitude, Longitude = fix.Longitude, Start = fix.Time, StartUtc = fix.Utc,
                };
                stops.Add(current);
                members.Add([]);
                count = 0;
            }
            // The middle of the stop's photos so far.
            count++;
            current.Latitude += (fix.Latitude - current.Latitude) / count;
            current.Longitude += (fix.Longitude - current.Longitude) / count;
            (current.End, current.EndUtc) = (fix.Time, fix.Utc);
            members[^1].Add((fix.Utc, fix.Id));
            stopOf[fix.Id] = stops.Count - 1;
        }

        // Spikes left out of the line join the stop nearest in time (if this group's cameras took them), and estimated
        // photos their photo's stop; estimated from a photo outside the track, the stop nearest in time. Those estimated
        // from another group's photos are that group's.
        var kept = clean.Select(f => f.Id).ToHashSet();
        var cameras = mine.Select(f => f.Camera).ToHashSet();
        foreach (var spike in fixes.Where(f => !kept.Contains(f.Id) && cameras.Contains(f.Camera)))
            if (NearestInTime(stops, spike.Utc) is { } at)
            {
                members[at].Add((spike.Utc, spike.Id));
                stopOf[spike.Id] = at;
            }
        var located = fixes.Select(f => f.Id).ToHashSet();
        foreach (var (id, utc, fromId) in estimated)
        {
            var index = fromId is { } from && stopOf.TryGetValue(from, out var its) ? its
                : fromId is { } other && located.Contains(other) ? null
                : NearestInTime(stops, utc);
            if (index is { } at) members[at].Add((utc, id));
        }
        for (var i = 0; i < stops.Count; i++)
            stops[i].Ids = members[i].OrderBy(m => m.Utc).ThenBy(m => m.Id).Select(m => m.Id).ToList();

        var days = stops.Select(s => s.StartLocal.Date).Distinct().Order().Select((day, i) => (day, i)).ToDictionary(d => d.day, d => d.i);
        foreach (var stop in stops) stop.Day = days[stop.StartLocal.Date];

        var legs = new List<TrackLeg>();
        double meters = 0;
        for (var i = 1; i < stops.Count; i++)
        {
            var (from, to) = (stops[i - 1], stops[i]);
            var gap = to.StartUtc - from.EndUtc;
            if (gap > VisitGap.TotalSeconds) continue;
            var distance = Geo.Haversine(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            meters += distance;
            legs.Add(new TrackLeg(i - 1, i, gap <= StopGap.TotalSeconds && distance / Math.Max(gap, 60) <= GroundSpeed));
        }
        return new Track(stops, legs, meters, days.Count)
        {
            Groups = groups.Count > 1 ? groups.Select(g => Describe(g, groups, estimated, cameraName)).ToList() : [],
            Group = group,
        };
    }

    /// <summary>
    /// Splits photos taken in more than one place at once into groups that could each be one party's. Each camera's
    /// photos are followed as far as anyone could travel (<see cref="Travel"/>), so two phones of one model apart are
    /// two; the one with the most photos leads a group, and what none of the leads so far could have been with leads
    /// the next. Then each photo joins every group whose lead was with it (a photo of theirs within
    /// <see cref="StopGap"/> it could be reached from: when everyone was together, all of them); or else, of those
    /// whose lead it could have been with, the one its phone was mostly in (your second phone at the airport on the
    /// way home, after days without photos on your first), then the one with a photo nearest in time. The group led
    /// by the camera with the most photos first (most likely yours), each in real-time order.
    /// </summary>
    internal static List<List<Fix>> Split(List<Fix> fixes)
    {
        var streams = new List<List<Fix>>();
        var streamOf = new int[fixes.Count];
        var ofCamera = new Dictionary<int, List<int>>();
        for (var i = 0; i < fixes.Count; i++)
        {
            var fix = fixes[i];
            if (!ofCamera.TryGetValue(fix.Camera, out var own)) ofCamera[fix.Camera] = own = [];
            // The camera's stream that could have got here, last seen most recently.
            var best = -1;
            foreach (var s in own)
                if (Travel.Possible(streams[s][^1], fix) && (best < 0 || streams[s][^1].Utc > streams[best][^1].Utc)) best = s;
            if (best < 0)
            {
                best = streams.Count;
                streams.Add([]);
                own.Add(best);
            }
            streams[best].Add(fix);
            streamOf[i] = best;
        }

        var leads = new List<List<Fix>>();
        var left = Enumerable.Repeat(true, fixes.Count).ToArray();
        var leftCount = fixes.Count;
        while (leftCount > 0 && leads.Count < MaxGroups)
        {
            if (leads.Count > 0 && leftCount < MinGroup && leftCount * 10 < fixes.Count) break;
            var counts = new int[streams.Count];
            for (var i = 0; i < fixes.Count; i++)
                if (left[i]) counts[streamOf[i]]++;
            var lead = streams[Array.IndexOf(counts, counts.Max())];
            leads.Add(lead);
            for (var i = 0; i < fixes.Count; i++)
                if (left[i] && Seen(lead, fixes[i]).Gap is not null) (left[i], leftCount) = (false, leftCount - 1);
        }
        if (leads.Count == 1) return [fixes];

        var seen = fixes.Select(fix => leads.Select(lead => Seen(lead, fix)).ToArray()).ToArray();
        // How many of each stream's photos each group's lead was with: where that phone mostly was.
        var withStream = new int[streams.Count, leads.Count];
        for (var i = 0; i < fixes.Count; i++)
            for (var g = 0; g < leads.Count; g++)
                if (seen[i][g].With) withStream[streamOf[i], g]++;

        var groups = leads.Select(_ => new List<Fix>()).ToList();
        for (var i = 0; i < fixes.Count; i++)
        {
            var with = Enumerable.Range(0, leads.Count).Where(g => seen[i][g].With).ToList();
            // Not seen with anyone: the group its phone was mostly in, or the one with a photo nearest in time; with no
            // one's photos for a day and a half either side (before their phones, years ago), it's in all of them.
            var possible = Enumerable.Range(0, leads.Count).Where(g => seen[i][g].Gap is not null).ToList();
            if (with.Count == 0 && possible.All(g => seen[i][g].Gap > VisitGap.TotalSeconds))
                with.AddRange(possible);
            else if (with.Count == 0 && possible.Count > 0)
                with.Add(possible.OrderByDescending(g => withStream[streamOf[i], g]).ThenBy(g => seen[i][g].Gap!.Value).First());
            foreach (var g in with) groups[g].Add(fixes[i]);
        }
        return groups.Where(g => g.Count > 0).ToList();
    }

    /// <summary>
    /// Whether whoever took <paramref name="lead"/> (in real-time order) could have taken a photo: Gap is null if not
    /// (their photo just before or after is out of reach), else how near in time their nearest photo is; With if that's
    /// within <see cref="StopGap"/>, so they were there.
    /// </summary>
    private static (long? Gap, bool With) Seen(List<Fix> lead, Fix fix)
    {
        int lo = 0, hi = lead.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (lead[mid].Utc <= fix.Utc) lo = mid + 1;
            else hi = mid;
        }
        var before = lo > 0 ? lead[lo - 1] : (Fix?)null;
        var after = lo < lead.Count ? lead[lo] : (Fix?)null;
        if (before is { } b && !Travel.Possible(b, fix) || after is { } a && !Travel.Possible(fix, a)) return (null, false);
        var gap = Math.Min(before is { } b2 ? fix.Utc - b2.Utc : long.MaxValue, after is { } a2 ? a2.Utc - fix.Utc : long.MaxValue);
        return (gap, gap <= StopGap.TotalSeconds);
    }

    private static TrackGroup Describe(List<Fix> group, List<List<Fix>> groups, IReadOnlyList<(long Id, long Utc, long? FromId)> estimated,
                                       Func<int, string?>? cameraName)
    {
        // What sets it apart: its photos that no other group has (when everyone was together, all of them are shared).
        var others = groups.Where(g => g != group).SelectMany(g => g).Select(f => f.Id).ToHashSet();
        var own = group.Where(f => !others.Contains(f.Id)).ToList();
        if (own.Count == 0) own = group;
        // Where most of them were taken: the busiest tenth of a degree (about 10 km).
        var busiest = own.GroupBy(f => (Math.Round(f.Latitude * 10), Math.Round(f.Longitude * 10))).MaxBy(g => g.Count())!;
        var ids = group.Select(f => f.Id).ToHashSet();
        var photos = group.Count + estimated.Count(e => e.FromId is { } from && ids.Contains(from));
        var cameras = own.GroupBy(f => f.Camera).OrderByDescending(g => g.Count())
            .Select(g => cameraName?.Invoke(g.Key)).OfType<string>().ToList();
        return new TrackGroup(photos, busiest.Average(f => f.Latitude), busiest.Average(f => f.Longitude), cameras);
    }

    /// <summary>A wall-clock time stored as Unix seconds (Kind = Unspecified, like <see cref="Media.MediaItem.TakenLocal"/>).</summary>
    public static DateTime Local(long time) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(time), DateTimeKind.Unspecified);

    /// <summary>The stop whose real time is nearest (within <see cref="Attach"/>), or null. Stops follow one another in time.</summary>
    private static int? NearestInTime(List<TrackStop> stops, long utc)
    {
        int lo = 0, hi = stops.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (stops[mid].StartUtc <= utc) lo = mid + 1;
            else hi = mid;
        }
        // lo - 1 started at or before the time (and may still run past it); lo starts after it.
        double before = lo > 0 ? Math.Max(0, utc - stops[lo - 1].EndUtc) : double.MaxValue;
        double after = lo < stops.Count ? stops[lo].StartUtc - utc : double.MaxValue;
        if (Math.Min(before, after) > Attach.TotalSeconds) return null;
        return before <= after ? lo - 1 : lo;
    }
}
