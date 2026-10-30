using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Metadata;

/// <summary>Dates encoded in camera/phone file names, used when a file has no embedded date.</summary>
public static partial class FileNameDates
{
    /// <summary>Returns the local wall-clock time encoded in the name, if any.</summary>
    public static DateTime? Parse(string fileName, TimeZoneInfo? localZone = null)
    {
        localZone ??= TimeZoneInfo.Local;

        if (ParseUtc(fileName) is { } utc)
            return TimeZoneInfo.ConvertTimeFromUtc(utc, localZone);

        // Samsung / generic Android and screenshots are local: 20260110_120709, IMG_20200805_181521, Screenshot 2021-03-04 101500.
        var m = LocalNameRegex().Match(fileName);
        if (m.Success && TryBuild(m.Groups["d"].Value, m.Groups["t"].Value, out var local))
            return local;

        m = DashedNameRegex().Match(fileName);
        if (m.Success && TryBuild(m.Groups["d"].Value.Replace("-", ""), m.Groups["t"].Value.Replace(".", "").Replace("-", "").Replace("_", ""), out local))
            return local;

        return null;
    }

    /// <summary>
    /// The UTC time in a OneDrive camera upload's (iOS) or a Pixel's name: 20260924_031147076_iOS, PXL_20240418_181647617.
    /// Against the photo's own clock, it says which time zone that clock was in.
    /// </summary>
    public static DateTime? ParseUtc(string fileName)
    {
        var m = UtcNameRegex().Match(fileName);
        return m.Success && TryBuild(m.Groups["d"].Value, m.Groups["t"].Value, out var utc) ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : null;
    }

    private static bool TryBuild(string date, string time, out DateTime value) =>
        DateTime.TryParseExact(date + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        && value.Year is >= 1990 and <= 2100;

    [GeneratedRegex(@"(?:^PXL_|^)(?<d>\d{8})_(?<t>\d{6})\d{3}(?:_iOS|\.|_)", RegexOptions.IgnoreCase)]
    private static partial Regex UtcNameRegex();

    [GeneratedRegex(@"(?:^|[^\d])(?<d>(?:19|20)\d{6})[_-](?<t>\d{6})(?!\d)")]
    private static partial Regex LocalNameRegex();

    [GeneratedRegex(@"(?<d>(?:19|20)\d{2}-\d{2}-\d{2})[ _T-]+(?<t>\d{2}[.\-_]?\d{2}[.\-_]?\d{2})")]
    private static partial Regex DashedNameRegex();
}
