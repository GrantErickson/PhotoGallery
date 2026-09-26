using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Tests.Mp4Builder;

namespace PhotoGallery.Core.Tests;

public sealed class VideoAndCropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-vc-{Guid.NewGuid():N}");

    public VideoAndCropTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>An MP4 shaped like the Windows encoder's output: ftyp, mdat, then moov last.</summary>
    private string WriteEncoderStyleMp4()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path,
        [
            .. Box("ftyp", System.Text.Encoding.ASCII.GetBytes("mp42"), U32(0)),
            .. Box("mdat", Bytes(256)),
            .. Box("moov", Mvhd(0, 600, 1200), Box("trak", Tkhd(1440, 1920)), Box("trak", Tkhd(0, 0))),
        ]);
        return path;
    }

    [Fact]
    public void Patch_sets_capture_time_rotation_and_location()
    {
        var path = WriteEncoderStyleMp4();
        var mdatBefore = File.ReadAllBytes(path).AsSpan(24, 256).ToArray();
        var taken = new DateTime(2026, 9, 26, 14, 10, 9, DateTimeKind.Utc);

        Mp4Metadata.Patch(path, taken, 90, (47.6344, -117.2687));

        using var stream = File.OpenRead(path);
        var info = QuickTimeReader.Read(stream)!;
        Assert.Equal(taken, info.CreatedUtc);
        Assert.Equal((1920, 1440), (info.Width, info.Height)); // rotated 90° → display size swaps
        Assert.Equal(47.6344, info.Latitude!.Value, 4);
        Assert.Equal(-117.2687, info.Longitude!.Value, 4);
        Assert.Equal(mdatBefore, File.ReadAllBytes(path).AsSpan(24, 256).ToArray()); // media untouched
    }

    [Fact]
    public void Patch_without_rotation_or_location_only_touches_times()
    {
        var path = WriteEncoderStyleMp4();
        var before = File.ReadAllBytes(path).Length;

        Mp4Metadata.Patch(path, new DateTime(2015, 1, 10, 21, 9, 21, DateTimeKind.Utc), 0, null);

        using var stream = File.OpenRead(path);
        var info = QuickTimeReader.Read(stream)!;
        Assert.Equal(new DateTime(2015, 1, 10, 21, 9, 21, DateTimeKind.Utc), info.CreatedUtc);
        Assert.Equal((1440, 1920), (info.Width, info.Height));
        Assert.Equal(before, new FileInfo(path).Length);
    }

    [Fact]
    public void Video_edits_rotate_and_measure()
    {
        var edits = VideoEdits.None.RotateCounterClockwise();
        Assert.Equal(270, edits.Rotation);
        Assert.Equal(0, edits.RotateClockwise().Rotation);
        Assert.Equal(TimeSpan.FromSeconds(1.5), (edits with { TrimStart = TimeSpan.FromSeconds(0.5), TrimEnd = TimeSpan.FromSeconds(2) }).KeptDuration(TimeSpan.FromSeconds(3)));
    }

    /// <summary>A 100×60 map with interest concentrated around (cx, cy).</summary>
    private static float[] Blob(int width, int height, double cx, double cy, double radius)
    {
        var map = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                map[y * width + x] = (float)Math.Max(0, 1 - d / radius) + 0.01f;
            }
        return map;
    }

    [Fact]
    public void Square_crop_follows_the_interesting_part()
    {
        var crop = SmartCrop.Find(Blob(100, 60, 85, 30, 12), 100, 60, [], aspect: 1.0);

        Assert.Equal(0.6, crop.Width, 3); // 60 of 100 wide
        Assert.Equal(1.0, crop.Height, 3);
        Assert.True(crop.X > 0.35, $"crop should move right, X = {crop.X}");
    }

    [Fact]
    public void Faces_win_over_other_interest()
    {
        // Busy area on the right, but a face on the left: the crop keeps the face whole.
        var face = new Region(8, 20, 10, 12);
        var crop = SmartCrop.Find(Blob(100, 60, 88, 30, 10), 100, 60, [face], aspect: 1.0);

        var left = crop.X * 100;
        Assert.True(left <= face.X - 3.5, $"face (with head room) must be inside; crop starts at {left}");
    }

    [Fact]
    public void Free_crop_tightens_around_the_subject()
    {
        var crop = SmartCrop.Find(Blob(100, 60, 30, 20, 8), 100, 60, [], aspect: null);

        Assert.True(crop.Width < 0.7, $"should tighten, width {crop.Width}");
        Assert.True(crop.X * 100 < 30 && (crop.X + crop.Width) * 100 > 30, "subject inside horizontally");
        var shape = crop.Width * 100 / (crop.Height * 60);
        Assert.InRange(shape, 0.5, 2.0);
    }

    [Fact]
    public void Free_crop_of_a_group_follows_the_faces_not_the_busy_background()
    {
        // Portrait photo: busy detail across the top, a group of faces filling the lower three quarters.
        const int w = 60, h = 80;
        var map = new float[w * h];
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < w; x++) map[y * w + x] = (x + y) % 3 == 0 ? 1f : 0f;
        Region[] faces = [new(8, 30, 9, 10), new(26, 26, 9, 10), new(44, 32, 9, 10), new(14, 52, 9, 10), new(36, 56, 9, 10)];

        var crop = SmartCrop.Find(map, w, h, faces, aspect: null);

        Assert.False(crop.IsFull);
        Assert.True(crop.Y * h > 5, $"the busy top is cut, crop starts at {crop.Y * h}");
        Assert.True(crop.Y * h <= 26 - 5.5, "the top face keeps its head room");
        Assert.True((crop.Y + crop.Height) * h >= 66, "the bottom faces are kept");
    }

    [Fact]
    public void Free_crop_of_an_evenly_busy_image_keeps_everything()
    {
        var even = Enumerable.Repeat(1f, 100 * 60).ToArray();
        Assert.True(SmartCrop.Find(even, 100, 60, [], aspect: null).IsFull);
    }

    [Fact]
    public void Saliency_marks_a_red_square_on_grey()
    {
        const int w = 40, h = 30;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var red = x is >= 25 and < 35 && y is >= 10 and < 20;
                (px[i], px[i + 1], px[i + 2]) = red ? ((byte)30, (byte)30, (byte)220) : ((byte)128, (byte)128, (byte)128);
            }

        var map = Saliency.Compute(px, w, h);

        Assert.True(map[15 * w + 30] > 3 * map[15 * w + 5], "the red square should stand out");
    }
}
