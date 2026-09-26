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
    public void Online_only_placeholders_are_indexed_from_the_name_without_reading()
    {
        // The path doesn't exist: any attempt to read content would fall back differently or throw.
        var item = LibraryIndexer.BuildItem(Path.Combine("Z:", "nowhere", "20260926_042529000_iOS.MOV"), 625_000_000, 0, MediaKind.Video, onlineOnly: true);

        Assert.True(item.OnlineOnly);
        Assert.Equal(DateSource.FileName, item.DateSource);
        Assert.Equal(0, item.DurationMs);
    }

    [Fact]
    public void Short_8dot3_roots_are_expanded_to_long_paths()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PhotoGallery long name test")).FullName;
        try
        {
            var shortPath = ShortPath(dir);
            Assert.SkipWhen(shortPath is null || !shortPath.Contains('~'), "8.3 names are disabled on this volume");
            Assert.Equal(LibraryIndexer.LongPath(dir), LibraryIndexer.LongPath(shortPath!), ignoreCase: true);
            Assert.DoesNotContain("~", Path.GetFileName(LibraryIndexer.LongPath(shortPath!)));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint bufferLength);

    private static string? ShortPath(string path)
    {
        var buffer = new System.Text.StringBuilder(1024);
        return GetShortPathName(path, buffer, 1024) > 0 ? buffer.ToString() : null;
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
