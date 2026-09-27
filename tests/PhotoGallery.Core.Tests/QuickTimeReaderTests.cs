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

public class FrameTimeTests
{
    private static byte[] Full(string type, params byte[][] body) => Box(type, [U32(0), .. body]); // version 0, no flags

    private static byte[] Track(string handler, uint scale, (uint Count, uint Delta)[] durations, byte[]? edits = null)
    {
        byte[] stts = [.. U32((uint)durations.Length), .. durations.SelectMany(d => U32(d.Count).Concat(U32(d.Delta)))];
        var mdia = Box("mdia",
            Full("mdhd", U32(0), U32(0), U32(scale), U32(0), U32(0)),
            Full("hdlr", U32(0), Encoding.ASCII.GetBytes(handler), Bytes(12)),
            Box("minf", Box("stbl", Full("stts", stts))));
        return edits is null ? Box("trak", Tkhd(1080, 1440), mdia) : Box("trak", Tkhd(1080, 1440), Box("edts", edits), mdia);
    }

    [Fact]
    public void A_live_photo_video_that_starts_slower_has_its_real_frame_times()
    {
        // As an iPhone 6s writes it: 4 frames at 7.5 fps, then 16 at 15 fps (timescale 600); the sound track comes first.
        byte[] moov = Box("moov",
            Mvhd(0, 600, 960),
            Track("soun", 44100, [(70560, 1)]),
            Track("vide", 600, [(4, 80), (16, 40)], Full("elst", U32(1), U32(960), U32(0), U32(0x10000))));

        var times = QuickTimeReader.ParseFrameTimes(moov.AsMemory(8))!;

        Assert.Equal(20, times.Count);
        Assert.Equal([0, 0.133, 0.267, 0.4, 0.533, 0.6], times.Take(6).Select(t => Math.Round(t.TotalSeconds, 3)));
        Assert.Equal(1.533, Math.Round(times[^1].TotalSeconds, 3));
    }

    [Fact]
    public void An_edit_list_shifts_the_frames()
    {
        // Media starts at 0.5 s (300 of 600) and a 0.25 s empty edit comes first (movie timescale 1000).
        var edits = Full("elst", U32(2), U32(250), U32(unchecked((uint)-1)), U32(0x10000), U32(1000), U32(300), U32(0x10000));
        byte[] moov = Box("moov", Mvhd(0, 1000, 1250), Track("vide", 600, [(6, 100)], edits));

        var times = QuickTimeReader.ParseFrameTimes(moov.AsMemory(8))!;

        // Frames at 0, 1/6 … 5/6 s of media; the first three are before the edit starts, and the rest move by -0.5 + 0.25.
        Assert.Equal([0.25, 0.417, 0.583], times.Select(t => Math.Round(t.TotalSeconds, 3)));
        Assert.Null(QuickTimeReader.ParseFrameTimes(Box("moov", Mvhd(0, 600, 600), Track("soun", 44100, [(10, 1)])).AsMemory(8)));
    }
}
