using System.Buffers.Binary;
using System.Text;

namespace PhotoGallery.Core.Tests;

/// <summary>Builds tiny ISO-BMFF / QuickTime byte structures for parser tests.</summary>
internal static class Mp4Builder
{
    public static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    public static byte[] RawBox(uint type, params byte[][] children)
    {
        var box = Box("xxxx", children);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), type);
        return box;
    }

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    public static byte[] Bytes(int count) => new byte[count];

    /// <summary>A minimal valid-looking MP4: ftyp + mdat + moov(mvhd).</summary>
    public static byte[] MinimalMp4(uint timescale = 600, uint duration = 1200) =>
        [.. Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0)), .. Box("mdat", Bytes(32)), .. Box("moov", Mvhd(0, timescale, duration))];

    public static byte[] Mvhd(uint created, uint timescale, uint duration) =>
        Box("mvhd", U32(0), U32(created), U32(created), U32(timescale), U32(duration), Bytes(80));

    /// <summary>tkhd v0 with an identity or 90-degree matrix.</summary>
    public static byte[] Tkhd(int width, int height, bool rotated = false)
    {
        var b = new byte[84];
        var matrix = rotated
            ? new[] { 0, 0x10000, 0, -0x10000, 0, 0, 0, 0, 0x40000000 }
            : new[] { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000 };
        for (var i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(40 + i * 4), matrix[i]);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(80), (uint)height << 16);
        return Box("tkhd", b);
    }

    /// <summary>QuickTime 'meta' with mdta keys and UTF-8 ilst values.</summary>
    public static byte[] AppleMeta(params (string Key, string Value)[] entries)
    {
        var keyBoxes = entries.Select(e => Box("mdta", Encoding.UTF8.GetBytes(e.Key))).SelectMany(k => k);
        var keys = Box("keys", [.. U32(0), .. U32((uint)entries.Length), .. keyBoxes]);
        var items = entries.Select((e, i) => RawBox((uint)i + 1, Box("data", U32(1), U32(0), Encoding.UTF8.GetBytes(e.Value)))).ToArray();
        return Box("meta", Box("hdlr", Bytes(24)), keys, Box("ilst", items));
    }
}
