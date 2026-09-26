using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Writes the few things an exported MP4 needs that the Windows encoder doesn't: capture time (mvhd), rotation
/// (the video track's tkhd matrix — lossless, honoured by players the same way phone videos are) and location
/// (udta/©xyz). Times and the matrix are patched in place; the location is appended, which is only done when
/// <c>moov</c> is the last box so no media offsets move.
/// </summary>
public static class Mp4Metadata
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void Patch(string path, DateTime? createdUtc, int rotationDegrees, (double Latitude, double Longitude)? location)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (QuickTimeReader.FindTopLevel(file, "moov") is not var (bodyOffset, bodyLength)) throw new InvalidDataException("No moov box.");
        var headerOffset = bodyOffset - 8;
        var moov = new byte[bodyLength];
        file.Seek(bodyOffset, SeekOrigin.Begin);
        file.ReadExactly(moov);

        foreach (var (type, start, end) in Children(moov, 0, moov.Length))
        {
            if (type == "mvhd" && createdUtc is { } created) SetTimes(moov, start, created);
            if (type == "trak" && rotationDegrees % 360 != 0) SetVideoRotation(moov, start, end, rotationDegrees);
        }
        file.Seek(bodyOffset, SeekOrigin.Begin);
        file.Write(moov);

        var moovIsLast = bodyOffset + bodyLength == file.Length;
        if (location is { } gps && moovIsLast && BinaryPrimitives.ReadUInt32BigEndian(ReadAt(file, headerOffset, 4)) == bodyLength + 8)
        {
            var xyz = XyzBox(gps.Latitude, gps.Longitude);
            var udta = Box("udta", xyz);
            file.Seek(0, SeekOrigin.End);
            file.Write(udta);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(bodyLength + 8 + udta.Length));
            file.Seek(headerOffset, SeekOrigin.Begin);
            file.Write(size);
        }
    }

    private static void SetTimes(byte[] moov, int body, DateTime createdUtc)
    {
        var seconds = (ulong)Math.Max(0, (createdUtc.ToUniversalTime() - Epoch1904).TotalSeconds);
        if (moov[body] == 1)
        {
            BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(body + 4), seconds);  // creation
            BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(body + 12), seconds); // modification
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(body + 4), (uint)seconds);
            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(body + 8), (uint)seconds);
        }
    }

    /// <summary>Sets the display matrix of the trak's tkhd if it's a video track (non-zero size).</summary>
    private static void SetVideoRotation(byte[] moov, int trakStart, int trakEnd, int degrees)
    {
        foreach (var (type, start, end) in Children(moov, trakStart, trakEnd))
        {
            if (type != "tkhd") continue;
            var v1 = moov[start] == 1;
            var matrix = start + (v1 ? 52 : 40);
            var sizeAt = start + (v1 ? 88 : 76);
            if (sizeAt + 8 > end) return;
            var width = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(sizeAt)) >> 16;
            var height = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(sizeAt + 4)) >> 16;
            if (width == 0 || height == 0) return; // audio or metadata track
            const int One = 0x10000;
            int[] values = (((degrees % 360) + 360) % 360) switch
            {
                90 => [0, One, 0, -One, 0, 0, (int)(height << 16), 0, 0x40000000],
                180 => [-One, 0, 0, 0, -One, 0, (int)(width << 16), (int)(height << 16), 0x40000000],
                270 => [0, -One, 0, One, 0, 0, 0, (int)(width << 16), 0x40000000],
                _ => [One, 0, 0, 0, One, 0, 0, 0, 0x40000000],
            };
            for (var i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(moov.AsSpan(matrix + i * 4), values[i]);
            return;
        }
    }

    /// <summary>©xyz as QuickTime and phones write it: [u16 length][u16 language]["+47.6344-117.2687/"].</summary>
    private static byte[] XyzBox(double latitude, double longitude)
    {
        var text = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{latitude:+00.0000;-00.0000}{longitude:+000.0000;-000.0000}/"));
        var body = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)text.Length);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), 0x15C7); // "und" packed language
        text.CopyTo(body, 4);
        return Box("©xyz", body);
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static IEnumerable<(string Type, int BodyStart, int End)> Children(byte[] data, int start, int end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (size < 8 || pos + size > end) yield break;
            yield return (Encoding.Latin1.GetString(data, pos + 4, 4), pos + 8, pos + size);
            pos += size;
        }
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }
}
