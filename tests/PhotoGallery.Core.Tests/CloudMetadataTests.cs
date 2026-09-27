using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Tests;

public sealed class CloudMetadataTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pg-cloud-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        new GalleryDatabase(_dbPath).CloseConnections(); // only this test's database: other test classes run at the same time
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) File.Delete(f);
    }

    [Fact]
    public void Parser_separates_tags_from_place_parts()
    {
        var tags = CloudTagParser.Parse(
            "Dog;Animal;LikelyPleasantMemory;Medical Lake;Spokane Co.;WA;United States;99022",
            "United States    WA    Medical Lake",
            ["__Nature_32", "Text_4"],
            null);

        Assert.Equal(
            [("Animal", TagType.Keyword), ("Dog", TagType.Keyword), ("Medical Lake, WA", TagType.Place), ("Nature", TagType.Category), ("Text", TagType.Category)],
            tags.Select(t => (t.Name, t.Type)).OrderBy(t => t.Name));
    }

    [Fact]
    public void Parser_handles_missing_fields_and_user_tags()
    {
        Assert.Empty(CloudTagParser.Parse(null, null, null, null));
        Assert.Equal(["Grandma", "Lake trip"], CloudTagParser.Parse(";;", "", [], "Grandma;lake trip").Select(t => t.Name).Order());
    }

    [Theory]
    [InlineData("Dog", true)]
    [InlineData("Coastal and oceanic landforms", true)]
    [InlineData("Person getting married", true)]
    [InlineData("POD GO\nWIRELESS", false)]
    [InlineData("'Bic", false)]
    [InlineData("Text—align: center", false)]
    [InlineData("! , Ill ,", false)]
    [InlineData("04693694", false)]
    [InlineData("한:才)", false)]
    [InlineData("SPRING FORWARD YOU MUST", false)]
    public void Tag_shape_filters_out_recognised_text(string token, bool isTag) => Assert.Equal(isTag, CloudTagParser.LooksLikeTag(token));

    [Theory]
    [InlineData("/personal/19b4e9941270dff7/Documents/Pictures/Camera Roll/2026/09/a.heic", @"D:\OneDrive\Pictures\Camera Roll\2026\09\a.heic")]
    [InlineData("/personal/19b4e9941270dff7/Other/a.heic", null)]
    public void File_refs_map_to_local_paths(string fileRef, string? expected) =>
        Assert.Equal(expected, OneDriveMetadataSync.ToLocalPath(fileRef, @"D:\OneDrive"));

    [Fact]
    public void People_can_be_named_searched_filtered_and_merged()
    {
        var database = new GalleryDatabase(_dbPath);
        database.Migrate();
        var media = new MediaRepository(database);
        var people = new PeopleRepository(database);
        long AddPhoto(string name)
        {
            using var db = database.Open();
            using var tx = db.BeginTransaction();
            var item = new MediaItem { Path = $@"D:\Lib\{name}", FileName = name, FileSize = 1, FileModified = 1, Kind = MediaKind.Photo };
            item.FolderId = media.EnsureFolder(db, tx, media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
            media.Upsert(db, tx, item, @"D:\Lib");
            tx.Commit();
            return item.Id;
        }
        var solo = AddPhoto("solo.jpg");
        var group = AddPhoto("group.jpg");
        long personA, personB;
        using (var db = database.Open())
        {
            personA = db.ExecuteScalar<long>("INSERT INTO People (OneDrivePersonId) VALUES ('a') RETURNING Id");
            personB = db.ExecuteScalar<long>("INSERT INTO People (OneDrivePersonId) VALUES ('b') RETURNING Id");
            db.Execute("INSERT INTO MediaFaces (MediaId, PersonId) VALUES (@solo, @personA), (@group, @personA), (@group, @personB)", new { solo, group, personA, personB });
        }

        var list = people.GetPeople();
        Assert.Equal(2, list.Single(p => p.Id == personA).Count);
        Assert.Equal(solo, list.Single(p => p.Id == personA).CoverMediaId); // fewest other people
        Assert.Equal([solo, group], media.Query(new MediaFilter { PersonId = personA }).Select(m => m.Id).Order());

        people.Rename(personA, "Emily");
        Assert.Equal("Emily", people.Get(personA)!.DisplayName);
        Assert.Equal([solo, group], media.Query(new MediaFilter { Text = "emily" }).Select(m => m.Id).Order());

        people.Merge(personB, personA);
        Assert.Null(people.Get(personB));
        Assert.Equal([solo, group], media.Query(new MediaFilter { PersonId = personA }).Select(m => m.Id).Order());
        using (var db = database.Open())
            Assert.Equal(personA, db.ExecuteScalar<long>("SELECT PersonId FROM PersonAliases WHERE OneDrivePersonId = 'b'"));
    }
}
