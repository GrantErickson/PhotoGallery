namespace PhotoGallery.Core.Places;

/// <summary>
/// Whether one person could have taken two located photos: how far anyone gets in the real time between them. Photos
/// that no one could have taken both of mean people were apart (you on a trip, your family at home).
/// </summary>
public static class Travel
{
    /// <summary>Nearby is together: GPS error, and a short drive, take no time.</summary>
    public const double NearbyMeters = 50_000;
    /// <summary>By road (100 km/h door to door), and by air (900 km/h) once an hour at airports is spent (a photo at the gate, one on landing).</summary>
    private const double Road = 28, Air = 250;
    private const long Airports = 3600;

    public static bool Possible(Fix a, Fix b)
    {
        var seconds = Math.Abs(b.Utc - a.Utc);
        var meters = Geo.Haversine(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        return meters <= NearbyMeters + Road * seconds
            || seconds > Airports && meters <= NearbyMeters + Air * (seconds - Airports);
    }
}
