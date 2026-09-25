using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Finds the MP4 embedded in an Android motion photo (Google/Pixel XMP container or Samsung SEFT trailer).
/// Returns a range only when it lies inside the file and starts with an <c>ftyp</c> box — the XMP flag
/// alone is not trusted (edited copies keep the flag, truncated duplicates keep a stale length).
/// </summary>
public static partial class MotionPhotoLocator
{
    private const int HeadBytes = 256 * 1024;

    public static (long Offset, long Length)? Locate(Stream stream)
    {
        var size = stream.Length;
        if (size < 64) return null;

        var head = ReadAt(stream, 0, (int)Math.Min(HeadBytes, size));
        var xmp = Encoding.Latin1.GetString(head);

        if (FromGoogleContainer(xmp, size) is { } google && IsMp4At(stream, google.Offset, google.Length))
            return google;
        if (FromMicroVideoOffset(xmp, size) is { } micro && IsMp4At(stream, micro.Offset, micro.Length))
            return micro;
        if (FromSamsungTrailer(stream, size) is { } samsung && IsMp4At(stream, samsung.Offset, samsung.Length))
            return samsung;
        return null;
    }

    private static (long Offset, long Length)? FromGoogleContainer(string xmp, long size)
    {
        foreach (Match item in ContainerItemRegex().Matches(xmp))
        {
            var tag = item.Value;
            if (!tag.Contains("Item:Semantic=\"MotionPhoto\"", StringComparison.Ordinal)) continue;
            var len = ItemLengthRegex().Match(tag);
            if (!len.Success || !long.TryParse(len.Groups[1].Value, out var length) || length <= 0) return null;
            return (size - length, length);
        }
        return null;
    }

    private static (long Offset, long Length)? FromMicroVideoOffset(string xmp, long size)
    {
        var m = MicroVideoOffsetRegex().Match(xmp);
        if (!m.Success || !long.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out var length) || length <= 0)
            return null;
        return (size - length, length);
    }

    /// <summary>
    /// Samsung trailer: file ends with [u32 dirLength]["SEFT"]; the directory starts with "SEFH", then
    /// version, count and 12-byte entries [u16 pad][u16 type][u32 offset back from SEFH][u32 length].
    /// </summary>
    private static (long Offset, long Length)? FromSamsungTrailer(Stream stream, long size)
    {
        var tail = ReadAt(stream, size - 8, 8);
        if (!tail.AsSpan(4).SequenceEqual("SEFT"u8)) return null;
        var dirLength = BinaryPrimitives.ReadUInt32LittleEndian(tail);
        var sefhPos = size - 8 - dirLength;
        if (dirLength < 12 || sefhPos < 0) return null;

        var dir = ReadAt(stream, sefhPos, (int)Math.Min(dirLength, 64 * 1024));
        if (!dir.AsSpan(0, 4).SequenceEqual("SEFH"u8)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(8));
        for (var i = 0; i < count && 12 + (i + 1) * 12 <= dir.Length; i++)
        {
            var entry = dir.AsSpan(12 + i * 12, 12);
            long back = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var start = sefhPos - back;
            if (start < 0 || length < 16 || start + length > size) continue;

            // The record begins with a small header and name (e.g. "MotionPhoto_Data"); the MP4 follows.
            var recordHead = ReadAt(stream, start, (int)Math.Min(length, 128));
            var ftyp = recordHead.AsSpan().IndexOf("ftyp"u8);
            if (ftyp >= 4) return (start + ftyp - 4, length - (ftyp - 4));
        }
        return null;
    }

    private static bool IsMp4At(Stream stream, long offset, long length)
    {
        if (offset < 0 || length < 16 || offset + length > stream.Length) return false;
        return ReadAt(stream, offset + 4, 4).AsSpan().SequenceEqual("ftyp"u8);
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }

    [GeneratedRegex(@"<Container:Item\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerItemRegex();

    [GeneratedRegex(@"Item:Length=""(\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ItemLengthRegex();

    [GeneratedRegex(@"GCamera:MicroVideoOffset=""(\d+)""|<GCamera:MicroVideoOffset>(\d+)<", RegexOptions.CultureInvariant)]
    private static partial Regex MicroVideoOffsetRegex();
}
