using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class FileNameDatesTests
{
    private static readonly TimeZoneInfo Mountain = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

    [Theory]
    [InlineData("20260924_031147076_iOS.heic", "2026-09-23T21:11:47")] // UTC name → MDT (UTC-6)
    [InlineData("PXL_20240418_181647617.MP.jpg", "2024-04-18T12:16:47")]
    [InlineData("20260110_120709.jpg", "2026-01-10T12:07:09")] // Samsung: local time
    [InlineData("IMG_20200805_181521.jpg", "2020-08-05T18:15:21")]
    [InlineData("MVIMG_20200805_181521.jpg", "2020-08-05T18:15:21")]
    [InlineData("Screenshot 2021-03-04 101500.png", "2021-03-04T10:15:00")]
    [InlineData("Screenshot_2021-03-04-10-15-00.png", "2021-03-04T10:15:00")]
    public void Parses_known_patterns(string name, string expected) =>
        Assert.Equal(DateTime.Parse(expected), FileNameDates.Parse(name, Mountain));

    [Theory]
    [InlineData("IMG_0840.JPG")]
    [InlineData("100_0542.MOV")]
    [InlineData("Ding A Ding.MOV")]
    public void Returns_null_without_a_date(string name) => Assert.Null(FileNameDates.Parse(name, Mountain));
}
