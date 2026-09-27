namespace PhotoGallery.Core.Places;

/// <summary>A place you named: everything within <see cref="RadiusMeters"/> of the centre is "at" it.</summary>
public sealed record Place(long Id, string Name, double Latitude, double Longitude, double RadiusMeters)
{
    /// <summary>Metres from the centre to a point (equirectangular: plenty for a few kilometres).</summary>
    public double DistanceTo(double latitude, double longitude) => Geo.Meters(Latitude, Longitude, latitude, longitude);

    public bool Contains(double latitude, double longitude) => DistanceTo(latitude, longitude) <= RadiusMeters;
}

public static class Geo
{
    public const double MetersPerDegree = 111_320;

    /// <summary>Distance in metres (equirectangular approximation; accurate to well under 1% at these scales).</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        var x = (lon2 - lon1) * Math.Cos((lat1 + lat2) / 2 * Math.PI / 180) * MetersPerDegree;
        var y = (lat2 - lat1) * MetersPerDegree;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Great-circle distance in metres, for longer ranges (nearest town).</summary>
    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double ToRad(double d) => d * Math.PI / 180;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
