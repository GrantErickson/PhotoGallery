using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Metadata;

public sealed record QuickTimeInfo(
    DateTime? CreatedUtc,
    DateTimeOffset? AppleCreationDate,
    long DurationMs,
    int Width,
    int Height,
    string? ContentIdentifier,
    string? Make,
    string? Model,
    double? Latitude,
    double? Longitude);

/// <summary>
/// Minimal QuickTime / ISO-BMFF reader. Walks top-level atoms by seeking and parses only <c>moov</c>,
/// so it never reads the media data (the library has ~900 GB of .MOV files).
/// </summary>
public static partial class QuickTimeReader
{
    private const long MaxMoovBytes = 64L * 1024 * 1024;
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static QuickTimeInfo? Read(Stream stream)
    {
        if (FindTopLevel(stream, "moov") is not var (offset, length) || length > MaxMoovBytes) return null;
        var buffer = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return ParseMoov(buffer);
    }

    /// <summary>Returns the payload range (after the header) of the first top-level atom of the given type.</summary>
    internal static (long Offset, long Length)? FindTopLevel(Stream stream, string type)
    {
        Span<byte> header = stackalloc byte[16];
        long pos = 0, end = stream.Length;
        while (pos + 8 <= end)
        {
            stream.Seek(pos, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(header, 8, throwOnEndOfStream: false);
            if (read < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8;
            if (size == 1)
            {
                if (read < 16) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerSize) return null;
            if (Encoding.ASCII.GetString(header.Slice(4, 4)) == type)
                return (pos + headerSize, Math.Min(size, end - pos) - headerSize);
            pos += size;
        }
        return null;
    }

    internal static QuickTimeInfo ParseMoov(ReadOnlyMemory<byte> moov)
    {
        DateTime? created = null;
        long durationMs = 0;
        int width = 0, height = 0;
        var keys = new List<string>();
        var values = new Dictionary<uint, string>();
        string? udtaXyz = null;

        foreach (var (type, body) in Children(moov))
        {
            switch (type)
            {
                case "mvhd":
                    (created, durationMs) = ParseMvhd(body.Span);
                    break;
                case "trak":
                    var (w, h) = ParseTrakDimensions(body);
                    if (w * h > width * height) (width, height) = (w, h);
                    break;
                case "meta":
                    ParseQuickTimeMeta(body, keys, values);
                    break;
                case "udta":
                    foreach (var (ut, ub) in Children(body))
                        if (ut == "©xyz" && ub.Length > 4)
                            udtaXyz = Encoding.UTF8.GetString(ub.Span[4..]);
                    break;
            }
        }

        string? Key(string name)
        {
            var index = keys.IndexOf(name);
            return index >= 0 && values.TryGetValue((uint)index + 1, out var v) ? v : null;
        }

        DateTimeOffset? appleDate = null;
        if (Key("com.apple.quicktime.creationdate") is { } cd &&
            DateTimeOffset.TryParse(FixOffset(cd), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            appleDate = parsed;

        var (lat, lon) = ParseIso6709(Key("com.apple.quicktime.location.ISO6709") ?? udtaXyz);

        return new QuickTimeInfo(created, appleDate, durationMs, width, height,
            Key("com.apple.quicktime.content.identifier"),
            Key("com.apple.quicktime.make"), Key("com.apple.quicktime.model"), lat, lon);
    }

    private static (DateTime?, long) ParseMvhd(ReadOnlySpan<byte> b)
    {
        if (b.Length < 32) return (null, 0);
        ulong created, duration;
        uint timescale;
        if (b[0] == 1)
        {
            created = BinaryPrimitives.ReadUInt64BigEndian(b[4..]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(b[24..]);
        }
        else
        {
            created = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        }
        // Cameras that never set the clock write 0 (1904) or tiny values; treat anything before 1990 as missing.
        DateTime? when = created > 0 && created < 10_000_000_000 ? Epoch1904.AddSeconds(created) : null;
        if (when?.Year < 1990) when = null;
        var ms = timescale > 0 ? (long)(duration * 1000 / timescale) : 0;
        return (when, ms);
    }

    /// <summary>Display size from tkhd; tracks without a size (audio, metadata) return 0x0.</summary>
    private static (int, int) ParseTrakDimensions(ReadOnlyMemory<byte> trak)
    {
        foreach (var (type, body) in Children(trak))
        {
            var b = body.Span;
            if (type != "tkhd" || b.Length < 84) continue;
            var v1 = b[0] == 1;
            if (v1 && b.Length < 96) return (0, 0);
            var matrix = v1 ? 52 : 40;
            var sizeAt = v1 ? 88 : 76;
            var w = (int)(BinaryPrimitives.ReadUInt32BigEndian(b[sizeAt..]) >> 16);
            var h = (int)(BinaryPrimitives.ReadUInt32BigEndian(b[(sizeAt + 4)..]) >> 16);
            // Matrix [a b; c d] with a == 0 means a 90/270 degree rotation, so the display size is swapped.
            var a = BinaryPrimitives.ReadInt32BigEndian(b[matrix..]);
            return a == 0 ? (h, w) : (w, h);
        }
        return (0, 0);
    }

    /// <summary>Apple 'meta' holding 'keys' + 'ilst'. QuickTime meta is a plain atom; ISO meta is a full box.</summary>
    private static void ParseQuickTimeMeta(ReadOnlyMemory<byte> meta, List<string> keys, Dictionary<uint, string> values)
    {
        if (meta.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(meta.Span) == 0)
            meta = meta[4..];

        foreach (var (type, body) in Children(meta))
        {
            var b = body.Span;
            if (type == "keys" && b.Length >= 8)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
                var p = 8;
                for (var i = 0; i < count && p + 8 <= b.Length; i++)
                {
                    var size = (int)BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
                    if (size < 8 || p + size > b.Length) break;
                    keys.Add(Encoding.UTF8.GetString(b.Slice(p + 8, size - 8)));
                    p += size;
                }
            }
            else if (type == "ilst")
            {
                foreach (var (rawType, itemBody) in Children(body, raw: true))
                    foreach (var (dataType, data) in Children(itemBody))
                    {
                        var d = data.Span;
                        if (dataType != "data" || d.Length < 8) continue;
                        if ((BinaryPrimitives.ReadUInt32BigEndian(d) & 0xFFFFFF) == 1) // well-known type 1 = UTF-8
                            values[uint.Parse(rawType, CultureInfo.InvariantCulture)] = Encoding.UTF8.GetString(d[8..]);
                    }
            }
        }
    }

    /// <summary>
    /// When each frame of the (first) video track starts showing, in order, from its sample tables: durations (stts),
    /// reordering offsets (ctts) and the edit list's shift. Frame rates vary (a Live Photo's video starts slower), so
    /// this is how to land on an exact frame. Null if the file has no readable video track.
    /// </summary>
    public static List<TimeSpan>? ReadFrameTimes(Stream stream)
    {
        if (FindTopLevel(stream, "moov") is not var (offset, length) || length > MaxMoovBytes) return null;
        var buffer = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return ParseFrameTimes(buffer);
    }

    internal static List<TimeSpan>? ParseFrameTimes(ReadOnlyMemory<byte> moov)
    {
        uint movieScale = 600;
        foreach (var (type, body) in Children(moov))
        {
            if (type == "mvhd" && body.Length >= 20)
                movieScale = BinaryPrimitives.ReadUInt32BigEndian(body.Span[(body.Span[0] == 1 ? 20 : 12)..]);
            if (type != "trak") continue;
            var children = Children(body);
            if (children.FirstOrDefault(c => c.Type == "mdia").Body is not { Length: > 0 } mdia) continue;
            var mdiaChildren = Children(mdia);
            var hdlr = mdiaChildren.FirstOrDefault(c => c.Type == "hdlr").Body;
            if (hdlr.Length < 12 || Encoding.ASCII.GetString(hdlr.Span.Slice(8, 4)) != "vide") continue;
            var mdhd = mdiaChildren.FirstOrDefault(c => c.Type == "mdhd").Body.Span;
            if (mdhd.Length < 20) return null;
            var scale = BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
            var stbl = Children(Children(mdiaChildren.FirstOrDefault(c => c.Type == "minf").Body).FirstOrDefault(c => c.Type == "stbl").Body);
            var stts = stbl.FirstOrDefault(c => c.Type == "stts").Body.Span;
            if (scale == 0 || stts.Length < 8) return null;

            // Decode times from the durations, then presentation times with the reordering offsets.
            var times = new List<long>();
            long t = 0;
            var entries = BinaryPrimitives.ReadUInt32BigEndian(stts[4..]);
            for (var i = 0; i < entries && 8 + i * 8 + 8 <= stts.Length && times.Count < 100_000; i++)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(stts[(8 + i * 8)..]);
                var delta = BinaryPrimitives.ReadUInt32BigEndian(stts[(12 + i * 8)..]);
                for (var k = 0; k < count && times.Count < 100_000; k++, t += delta) times.Add(t);
            }
            var ctts = stbl.FirstOrDefault(c => c.Type == "ctts").Body.Span;
            if (ctts.Length >= 8)
            {
                var n = 0;
                var cttsEntries = BinaryPrimitives.ReadUInt32BigEndian(ctts[4..]);
                for (var i = 0; i < cttsEntries && 8 + i * 8 + 8 <= ctts.Length; i++)
                {
                    var count = BinaryPrimitives.ReadUInt32BigEndian(ctts[(8 + i * 8)..]);
                    var shift = BinaryPrimitives.ReadInt32BigEndian(ctts[(12 + i * 8)..]); // signed in version 1; small either way
                    for (var k = 0; k < count && n < times.Count; k++) times[n++] += shift;
                }
            }

            // The edit list: an empty edit delays the start (movie timescale); the media edit says where in the media
            // it begins, and frames before that aren't shown (except one already showing at that point).
            double empty = 0;
            long mediaStart = 0;
            var edts = children.FirstOrDefault(c => c.Type == "edts").Body;
            var elst = edts.Length > 0 ? Children(edts).FirstOrDefault(c => c.Type == "elst").Body.Span : default;
            if (elst.Length >= 8)
            {
                var v1 = elst[0] == 1;
                var entrySize = v1 ? 20 : 12;
                var elstEntries = BinaryPrimitives.ReadUInt32BigEndian(elst[4..]);
                for (var i = 0; i < elstEntries && 8 + (i + 1) * entrySize <= elst.Length; i++)
                {
                    var e = elst[(8 + i * entrySize)..];
                    var duration = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(e) : BinaryPrimitives.ReadUInt32BigEndian(e);
                    var mediaTime = v1 ? BinaryPrimitives.ReadInt64BigEndian(e[8..]) : BinaryPrimitives.ReadInt32BigEndian(e[4..]);
                    if (mediaTime == -1)
                    {
                        empty += duration / (double)movieScale;
                        continue;
                    }
                    mediaStart = mediaTime;
                    break;
                }
            }
            times.Sort();
            var first = Math.Max(0, times.FindLastIndex(x => x <= mediaStart));
            return times.Skip(first).Select(x => TimeSpan.FromSeconds(Math.Max(0, (x - mediaStart) / (double)scale) + empty)).ToList();
        }
        return null;
    }

    /// <summary>Child atoms of a container. With <paramref name="raw"/>, the type is the big-endian u32 as a number (ilst indexes).</summary>
    private static List<(string Type, ReadOnlyMemory<byte> Body)> Children(ReadOnlyMemory<byte> data, bool raw = false)
    {
        var list = new List<(string, ReadOnlyMemory<byte>)>();
        var span = data.Span;
        var pos = 0;
        while (pos + 8 <= span.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
            var headerSize = 8;
            if (size == 1 && pos + 16 <= span.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span[(pos + 8)..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = span.Length - pos;
            }
            if (size < headerSize || pos + size > span.Length) break;
            var type = raw
                ? BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]).ToString(CultureInfo.InvariantCulture)
                : Encoding.Latin1.GetString(span.Slice(pos + 4, 4));
            list.Add((type, data.Slice(pos + headerSize, (int)size - headerSize)));
            pos += (int)size;
        }
        return list;
    }

    /// <summary>"2019-07-27T20:43:29-0600" → "2019-07-27T20:43:29-06:00" so DateTimeOffset can parse it.</summary>
    private static string FixOffset(string s) =>
        s.Length > 5 && s[^5] is '+' or '-' && char.IsDigit(s[^1]) ? s.Insert(s.Length - 2, ":") : s;

    internal static (double?, double?) ParseIso6709(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        var m = Iso6709Regex().Match(s);
        if (!m.Success) return (null, null);
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && (lat != 0 || lon != 0) ? (lat, lon) : (null, null);
    }

    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)")]
    private static partial Regex Iso6709Regex();
}
