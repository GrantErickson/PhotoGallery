using System.Collections.Concurrent;
using GeoTimeZone;

namespace PhotoGallery.Core.Places;

/// <summary>
/// Which time zone's clocks are in effect at a place (from a bundled map of the world's zones). Phones set their clocks
/// to where they are, so a photo whose clock was in another zone than any near a place wasn't taken there, and a
/// located photo whose file doesn't say its zone was taken on the zone's time where it was (true of 99.7 % of the
/// library's photos that do say).
/// </summary>
public static class TimeZones
{
    /// <summary>
    /// A phone near a border can take the other side's zone from the network: in the library, photos up to 30 km into
    /// Montana on Idaho's time, and in Nevada's West Wendover, which keeps Utah's.
    /// </summary>
    public const double BorderMeters = 50_000;

    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new();

    /// <summary>How far clocks at a place are ahead of UTC (minutes) at a real time (UTC Unix seconds).</summary>
    public static int OffsetAt(double latitude, double longitude, long utc)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(utc);
        return Zone(latitude, longitude) is { } zone ? (int)zone.GetUtcOffset(instant).TotalMinutes : Nominal(longitude);
    }

    /// <summary>
    /// How far clocks at a place are ahead of UTC (minutes) when they read <paramref name="local"/> (Unix seconds, like
    /// Media.DateTaken). Within an hour of a change to or from summer time it may be the other one.
    /// </summary>
    public static int OffsetAtLocal(double latitude, double longitude, long local)
    {
        if (Zone(latitude, longitude) is not { } zone) return Nominal(longitude);
        return (int)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(local - (long)zone.BaseUtcOffset.TotalSeconds)).TotalMinutes;
    }

    /// <summary>Whether clocks <paramref name="offset"/> minutes ahead of UTC are in effect within <paramref name="meters"/> of a place, at a real time.</summary>
    public static bool InEffectNear(double latitude, double longitude, long utc, int offset, double meters = BorderMeters)
    {
        if (OffsetAt(latitude, longitude, utc) == offset) return true;
        // Rings around the place: zones are hundreds of kilometres across, so a border within reach crosses one.
        foreach (var fraction in (ReadOnlySpan<double>)[0.1, 0.3, 0.6, 1])
            for (var bearing = 0; bearing < 360; bearing += 15)
            {
                var (lat, lon) = Toward(latitude, longitude, meters * fraction, bearing);
                if (OffsetAt(lat, lon, utc) == offset) return true;
            }
        return false;
    }

    private static TimeZoneInfo? Zone(double latitude, double longitude)
    {
        var id = TimeZoneLookup.GetTimeZone(Math.Clamp(latitude, -90, 90), Wrap(longitude)).Result;
        return Zones.GetOrAdd(id, static id =>
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
        });
    }

    /// <summary>Without the zone's rules: the hour the longitude is in.</summary>
    private static int Nominal(double longitude) => (int)Math.Round(Wrap(longitude) / 15) * 60;

    private static double Wrap(double longitude) => longitude is >= -180 and <= 180 ? longitude : (longitude % 360 + 540) % 360 - 180;

    /// <summary>The point <paramref name="meters"/> away on a bearing (degrees from north).</summary>
    private static (double Latitude, double Longitude) Toward(double latitude, double longitude, double meters, double bearing)
    {
        const double R = 6_371_000;
        double ToRad(double d) => d * Math.PI / 180;
        var (d, b, p1, l1) = (meters / R, ToRad(bearing), ToRad(latitude), ToRad(longitude));
        var p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
        var l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        return (p2 * 180 / Math.PI, Wrap(l2 * 180 / Math.PI));
    }
}
