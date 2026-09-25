using System.Text;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Tests.Mp4Builder;

namespace PhotoGallery.Core.Tests;

public class QuickTimeReaderTests
{
    private static byte[] LivePhotoMov(bool rotated = false) =>
    [
        .. Box("ftyp", Encoding.ASCII.GetBytes("qt  "), U32(0)),
        .. Box("wide"),
        .. Box("mdat", Bytes(1000)),
        .. Box("moov",
            Mvhd(created: 3_600_000_000, timescale: 600, duration: 1422),
            Box("trak", Tkhd(1920, 1440, rotated)),
            Box("trak", Tkhd(0, 0)),
            AppleMeta(
                ("com.apple.quicktime.content.identifier", "986A9874-2DEA-49B1-8FBE-82AB893C8E14"),
                ("com.apple.quicktime.creationdate", "2026-09-23T20:11:47-0600"),
                ("com.apple.quicktime.location.ISO6709", "+40.5249-111.8638+1400.000/"),
                ("com.apple.quicktime.model", "iPhone 17 Pro Max"))),
    ];

    [Fact]
    public void Reads_apple_live_photo_metadata_from_moov_only()
    {
        var info = QuickTimeReader.Read(new MemoryStream(LivePhotoMov()));

        Assert.NotNull(info);
        Assert.Equal("986A9874-2DEA-49B1-8FBE-82AB893C8E14", info.ContentIdentifier);
        Assert.Equal(2370, info.DurationMs);
        Assert.Equal((1920, 1440), (info.Width, info.Height));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 20, 11, 47, TimeSpan.FromHours(-6)), info.AppleCreationDate);
        Assert.Equal("iPhone 17 Pro Max", info.Model);
        Assert.Equal(40.5249, info.Latitude!.Value, 4);
        Assert.Equal(-111.8638, info.Longitude!.Value, 4);
    }

    [Fact]
    public void Rotated_track_reports_display_size()
    {
        var info = QuickTimeReader.Read(new MemoryStream(LivePhotoMov(rotated: true)));

        Assert.Equal((1440, 1920), (info!.Width, info.Height));
    }

    [Fact]
    public void File_without_moov_returns_null() =>
        Assert.Null(QuickTimeReader.Read(new MemoryStream([.. Box("ftyp", Bytes(8)), .. Box("mdat", Bytes(64))])));

    [Theory]
    [InlineData("+40.5249-111.8638/", 40.5249, -111.8638)]
    [InlineData("-33.8688+151.2093+012.000/", -33.8688, 151.2093)]
    public void Parses_iso6709(string value, double lat, double lon)
    {
        var (la, lo) = QuickTimeReader.ParseIso6709(value);
        Assert.Equal(lat, la!.Value, 4);
        Assert.Equal(lon, lo!.Value, 4);
    }
}
