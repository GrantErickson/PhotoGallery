using System.Buffers.Binary;
using System.Text;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class MotionPhotoLocatorTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, .. new byte[500], 0xFF, 0xD9];

    private static byte[] GoogleXmp(long length) => Encoding.UTF8.GetBytes(
        $"""
        <x:xmpmeta><rdf:Description GCamera:MotionPhoto="1"><Container:Directory><rdf:Seq>
        <rdf:li><Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/></rdf:li>
        <rdf:li><Container:Item
            Item:Mime="video/mp4"
            Item:Semantic="MotionPhoto"
            Item:Length="{length}"
            Item:Padding="0"/></rdf:li>
        </rdf:Seq></Container:Directory></rdf:Description></x:xmpmeta>
        """);

    [Fact]
    public void Google_container_video_is_found_at_end_of_file()
    {
        var mp4 = Mp4Builder.MinimalMp4();
        byte[] file = [.. GoogleXmp(mp4.Length), .. Jpeg, .. mp4];

        var result = MotionPhotoLocator.Locate(new MemoryStream(file));

        Assert.Equal((file.Length - mp4.Length, (long)mp4.Length), result);
    }

    [Fact]
    public void Truncated_copy_with_stale_length_is_rejected()
    {
        var mp4 = Mp4Builder.MinimalMp4();
        byte[] file = [.. GoogleXmp(mp4.Length * 50), .. Jpeg, .. mp4]; // XMP claims more than the file holds

        Assert.Null(MotionPhotoLocator.Locate(new MemoryStream(file)));
    }

    [Fact]
    public void Flag_without_video_is_rejected()
    {
        byte[] file = [.. GoogleXmp(64), .. Jpeg, .. new byte[64]]; // range exists but is not an MP4

        Assert.Null(MotionPhotoLocator.Locate(new MemoryStream(file)));
    }

    [Fact]
    public void Samsung_seft_trailer_video_is_found()
    {
        var mp4 = Mp4Builder.MinimalMp4();
        // Record: [u16 pad][u16 type][u32 name length]["MotionPhoto_Data"][mp4]
        byte[] record = [.. new byte[8], .. "MotionPhoto_Data"u8, .. mp4];
        byte[] body = [.. Jpeg, .. record];
        var recordStart = Jpeg.Length;
        var sefhPos = body.Length;

        var dir = new byte[24];
        "SEFH"u8.CopyTo(dir);
        BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(4), 106);
        BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(14), 0x0A30);
        BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(16), (uint)(sefhPos - recordStart));
        BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(20), (uint)record.Length);
        var tail = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, (uint)dir.Length);
        "SEFT"u8.CopyTo(tail.AsSpan(4));
        byte[] file = [.. body, .. dir, .. tail];

        var result = MotionPhotoLocator.Locate(new MemoryStream(file));

        Assert.Equal((recordStart + 24L, (long)mp4.Length), result);
    }

    [Fact]
    public void Plain_jpeg_has_no_motion() => Assert.Null(MotionPhotoLocator.Locate(new MemoryStream(Jpeg)));
}
