using Dapper;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.Core.Tests;

public sealed class TranscriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-transcripts-{Guid.NewGuid():N}");
    private readonly GalleryDatabase _database;
    private readonly MediaRepository _media;
    private readonly TranscriptRepository _transcripts;

    public TranscriptTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new GalleryDatabase(Path.Combine(_dir, "gallery.db"));
        _database.Migrate();
        _media = new MediaRepository(_database);
        _transcripts = new TranscriptRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static TranscriptSegment S(double start, double end, string text, int? speaker = null) => new(start, end, text, speaker);

    [Fact]
    public void Cleaning_drops_fillers_sound_labels_stock_phrases_and_loops()
    {
        var cleaned = TranscriptFormatter.Clean([
            S(0, 1, " So we have these  little honey things out."),
            S(1, 2, "Um."),
            S(2, 3, "Uh, um..."),
            S(3, 4, "[Music]"),
            S(4, 5, "Thanks for watching!"),
            S(5, 6, "(laughs) That's *laughs* funny."),
            S(6, 7, "I hoped that..."),
            S(7, 8, "I hoped that..."),
            S(8, 9, "You."),
        ]);

        Assert.Equal(["So we have these little honey things out.", "That's funny.", "I hoped that..."], cleaned.Select(s => s.Text));
        Assert.Equal(8, cleaned[^1].End); // the repeat is folded into the first
    }

    [Fact]
    public void Paragraphs_break_at_pauses_after_sentences_speakers_and_long_stretches()
    {
        var paragraphs = TranscriptFormatter.Paragraphs([
            S(0, 2, "So I just recorded a whole thing."),
            S(2.3, 4, "But it got turned off."),
            S(6, 8, "So anyway, let's try"),          // 2 s pause after a finished sentence → new paragraph
            S(10, 11, "this one more time."),         // 2 s pause mid-sentence → same paragraph
            S(16, 18, "Much later."),                 // 5 s pause → new paragraph
            S(18.2, 19, "Hi!", speaker: 1),           // new speaker
        ]);

        Assert.Equal([
            "So I just recorded a whole thing. But it got turned off.",
            "So anyway, let's try this one more time.",
            "Much later.",
            "Hi!",
        ], paragraphs.Select(p => p.Text));
        Assert.Equal([0, 6, 16, 18.2], paragraphs.Select(p => p.Start));

        var monologue = Enumerable.Range(0, 30).Select(i => S(i * 3, i * 3 + 2.9, $"Sentence {i}.")).ToList();
        Assert.All(TranscriptFormatter.Paragraphs(monologue), p => Assert.True(p.End - p.Start <= 35, $"{p.Start}-{p.End}"));
    }

    [Fact]
    public void Stitching_keeps_speech_leaves_out_silence_and_maps_times_back()
    {
        const int rate = 100;
        var audio = new float[20 * rate];
        for (var i = 2 * rate; i < 4 * rate; i++) audio[i] = 0.5f;   // speech 2–4 s
        for (var i = 10 * rate; i < 13 * rate; i++) audio[i] = 0.7f; // speech 10–13 s

        var stitched = SpeechStitcher.Join(audio, rate, [(2, 4), (10, 13)]);

        Assert.Equal((2 + 3 + 2 * SpeechStitcher.Gap) * rate, stitched.Samples.Length);
        Assert.Equal(5, stitched.SpeechSeconds, 3);
        Assert.Equal(0.5f, stitched.Samples[0]);
        Assert.Equal(0f, stitched.Samples[(int)(2.5 * rate)]);                 // the gap
        Assert.Equal(0.7f, stitched.Samples[(int)((2 + SpeechStitcher.Gap) * rate)]);
        Assert.Equal(2.5, stitched.ToOriginal(0.5), 3);
        Assert.Equal(11, stitched.ToOriginal(2 + SpeechStitcher.Gap + 1), 3);
    }

    [Fact]
    public void Speakers_are_labelled_only_when_two_voices_clearly_take_part()
    {
        TranscriptSegment[] talk = [S(0, 5, "Where are we going?"), S(5, 10, "To the lake."), S(10, 15, "Can we swim?"), S(15, 20, "If it's warm.")];

        // Two voices taking turns (diarization numbers them 7 and 3): Speaker 1 and 2 by first appearance.
        var two = SpeakerAssigner.Assign(talk, [new(0, 5, 7), new(5, 10, 3), new(10, 15, 7), new(15, 20, 3)]);
        Assert.Equal([1, 2, 1, 2], two.Select(s => s.Speaker));

        // One person split by a change of tone (a small share) isn't two speakers.
        var monologue = Enumerable.Range(0, 10).Select(i => S(i * 5, i * 5 + 5, $"Line {i}.")).ToList();
        var split = SpeakerAssigner.Assign(monologue, [new(0, 45, 0), new(45, 50, 1)]);
        Assert.All(split, s => Assert.Null(s.Speaker));

        // A stray third voice is folded into its neighbour; gaps with no turn take the previous label.
        var three = SpeakerAssigner.Assign([.. talk, S(20, 21, "Hm."), S(21, 25, "Race you!")],
            [new(0, 5, 0), new(5, 10, 1), new(10, 15, 0), new(15, 20, 1), new(20, 21, 2)]);
        Assert.Equal([1, 2, 1, 2, 2, 2], three.Select(s => s.Speaker));
    }

    [Theory]
    [InlineData("Happy birthday, Emily!", "birth", new[] { "birthday" })]
    [InlineData("Happy birthday, Emily!", "EMILY happy", new[] { "Happy", "Emily" })]
    [InlineData("We ate at the café. Cafeteria food is fine.", "cafe", new[] { "café", "Cafeteria" })]
    // As in the index, "don't" is the phrase "don" + "t…", which "Don took" matches too.
    [InlineData("I don't know. Don took it.", "don't", new[] { "don't", "Don took" })]
    [InlineData("Nothing here.", "birthday", new string[0])]
    [InlineData("Some text", "", new string[0])]
    public void Search_words_are_found_as_the_index_matches_them(string text, string query, string[] expected) =>
        Assert.Equal(expected, SearchHighlighter.Find(text, query).Select(m => text.Substring(m.Start, m.Length)));

    [Fact]
    public void Overlapping_search_words_highlight_once()
    {
        var text = "Grandpa and grandma";
        Assert.Equal([new TextMatch(0, 7), new TextMatch(12, 7)], SearchHighlighter.Find(text, "grand grandpa"));
    }

    private long AddVideo(string name, long durationMs = 60_000, long size = 100)
    {
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        var item = new MediaItem
        {
            Path = $@"D:\Lib\{name}", FileName = name, FileSize = size, FileModified = 1, Kind = MediaKind.Video, DurationMs = durationMs,
        };
        item.FolderId = _media.EnsureFolder(db, tx, _media.GetFolderIds(), @"D:\Lib", @"D:\Lib");
        _media.Upsert(db, tx, item, @"D:\Lib");
        tx.Commit();
        return item.Id;
    }

    [Fact]
    public void Transcripts_are_stored_searchable_and_forgotten_when_the_file_changes()
    {
        var talk = AddVideo("talk.mov");
        var quiet = AddVideo("quiet.mov");
        var blip = AddVideo("blip.mov", durationMs: 1500);
        Assert.Equal([talk, quiet], _transcripts.GetBacklog(10).Select(j => j.MediaId).Order());
        Assert.Equal((0, 2, 0), _transcripts.GetProgress());

        _transcripts.Save(new Transcript(talk, TranscriptStatus.Done, [S(0, 2, "Happy birthday, Emily!")], "en", "test"));
        _transcripts.Save(new Transcript(quiet, TranscriptStatus.NoSpeech, [], Model: "test"));

        Assert.Equal("Happy birthday, Emily!", _transcripts.Get(talk)!.Segments.Single().Text);
        Assert.Empty(_transcripts.GetBacklog(10));
        Assert.Equal((2, 2, 1), _transcripts.GetProgress());
        Assert.Equal([talk], _media.Query(new MediaFilter { Text = "birthday" }).Select(m => m.Id));
        Assert.Equal([talk], _media.Query(new MediaFilter { Text = "talk" }).Select(m => m.Id)); // names still indexed

        // Re-indexing an unchanged file keeps its words; a changed file drops the transcript.
        AddVideo("talk.mov");
        Assert.Equal([talk], _media.Query(new MediaFilter { Text = "birthday" }).Select(m => m.Id));
        AddVideo("talk.mov", size: 200);
        Assert.Null(_transcripts.Get(talk));
        Assert.Empty(_media.Query(new MediaFilter { Text = "birthday" }));
        Assert.Equal([talk], _transcripts.GetBacklog(10).Select(j => j.MediaId));
        Assert.DoesNotContain(blip, _transcripts.GetBacklog(10).Select(j => j.MediaId));
    }

    [Fact]
    public void Transcripts_from_an_older_pipeline_are_redone_after_new_videos()
    {
        var old = AddVideo("old.mov");
        var silent = AddVideo("silent.mov");
        var fresh = AddVideo("fresh.mov");
        _transcripts.Save(new Transcript(old, TranscriptStatus.Done, [S(0, 1, "Hello.")], Model: "v1"));
        _transcripts.Save(new Transcript(silent, TranscriptStatus.NoSpeech, [], Model: "v1"));

        Assert.Equal([fresh], _transcripts.GetBacklog(10, "v1").Select(j => j.MediaId));
        Assert.Equal([fresh, old], _transcripts.GetBacklog(10, "v2").Select(j => j.MediaId)); // nothing to redo without speech
    }

    [Fact]
    public void Upgrading_keeps_the_existing_search_index()
    {
        // A v8 database with an indexed file, then the v9 migration.
        var path = Path.Combine(_dir, "old.db");
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            db.Execute("CREATE VIRTUAL TABLE MediaFts USING fts5(Name, Folder, Tags, Camera, tokenize = 'unicode61 remove_diacritics 2');");
            db.Execute("INSERT INTO MediaFts (rowid, Name, Folder, Tags, Camera) VALUES (7, 'beach.jpg', 'D:\\Lib', 'Ocean', '')");
        }
        var migration = typeof(GalleryDatabase).GetField("Migrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as string[];
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            db.Execute("CREATE TABLE Media (Id INTEGER PRIMARY KEY);");
            db.Execute(migration![8]);
            db.Execute(migration[9]);
            Assert.Equal(7, db.ExecuteScalar<long>("SELECT rowid FROM MediaFts WHERE MediaFts MATCH 'ocean'"));
            Assert.Equal(6, db.Query("PRAGMA table_info(MediaFts)").Count());
        }
    }
}
