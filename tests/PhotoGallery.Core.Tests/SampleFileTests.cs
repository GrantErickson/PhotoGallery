using PhotoGallery.Core.Indexing;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>Checks against the real files studied in the Phase 0 spike. Skipped on machines without the library.</summary>
public class SampleFileTests
{
    private static readonly string CameraRoll = Path.Combine(Environment.GetEnvironmentVariable("OneDrive") ?? "", "Pictures", "Camera Roll");

    private static string Sample(string relative)
    {
        var path = Path.Combine(CameraRoll, relative);
        Assert.SkipUnless(File.Exists(path), $"Sample not available: {path}");
        return path;
    }

    [Fact]
    public void Cloud_live_photo_still_has_content_id_and_no_embedded_video()
    {
        var md = MediaMetadataReader.Read(Sample(@"2026\09\20260924_031147076_iOS.heic"), MediaKind.Photo);

        Assert.Equal("986A9874-2DEA-49B1-8FBE-82AB893C8E14", md.ContentId);
        Assert.Equal(0, md.MotionLength);
        Assert.Equal(DateSource.Exif, md.DateSource);
        Assert.Equal("iPhone 17 Pro Max", md.Model);
        Assert.True(md.Width > 0 && md.Height > 0);
    }

    [Fact]
    public void Pixel_motion_photo_video_range()
    {
        var md = MediaMetadataReader.Read(Sample(@"2024\04\PXL_20240418_181647617.MP.jpg"), MediaKind.Photo);

        Assert.Equal((3248218L, 2661604L), (md.MotionOffset, md.MotionLength));
    }

    [Fact]
    public void Samsung_motion_photo_video_range()
    {
        var md = MediaMetadataReader.Read(Sample(@"2026\2026 Bridal Show\20260110_120709.jpg"), MediaKind.Photo);

        Assert.Equal((1175320L, 4181566L), (md.MotionOffset, md.MotionLength));
    }

    [Fact]
    public void Truncated_duplicate_has_no_motion()
    {
        var md = MediaMetadataReader.Read(Sample(@"2026\2026 Bridal Show\20260110_120928(0).jpg"), MediaKind.Photo);

        Assert.Equal(0, md.MotionLength);
    }

    [Fact]
    public void Local_live_photo_pair_shares_content_id()
    {
        var still = MediaMetadataReader.Read(Sample("IMG_0840.JPEG"), MediaKind.Photo);
        var mov = MediaMetadataReader.Read(Sample("IMG_0840.MOV"), MediaKind.Video);

        Assert.NotNull(still.ContentId);
        Assert.Equal(still.ContentId, mov.ContentId);
        Assert.InRange(mov.DurationMs, 1000, 4000);
    }

    [Fact]
    public void Build_item_marks_embedded_motion()
    {
        var path = Sample(@"2024\04\PXL_20240418_181647617.MP.jpg");
        var item = LibraryIndexer.BuildItem(path, new FileInfo(path).Length, 0, MediaKind.Photo);

        Assert.Equal(MotionSource.Embedded, item.Motion);
        Assert.False(item.IsScreenshot);
    }
}
