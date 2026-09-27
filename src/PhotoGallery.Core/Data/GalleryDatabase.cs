using Dapper;
using Microsoft.Data.Sqlite;

namespace PhotoGallery.Core.Data;

/// <summary>Owns the SQLite file: connection setup (WAL) and schema migrations keyed on PRAGMA user_version.</summary>
public sealed class GalleryDatabase
{
    private readonly string _connectionString;

    static GalleryDatabase()
    {
        SqlMapper.AddTypeHandler(new BoolHandler());
    }

    public GalleryDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Closes this database's pooled connections (e.g. so its file can be deleted); other databases are left alone.</summary>
    public void CloseConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;");
        return connection;
    }

    public void Migrate()
    {
        using var db = Open();
        db.Execute("PRAGMA journal_mode=WAL;");
        var version = db.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = db.BeginTransaction();
            db.Execute(Migrations[i], transaction: tx);
            db.Execute($"PRAGMA user_version = {i + 1};", transaction: tx);
            tx.Commit();
        }
    }

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE Folders (
            Id       INTEGER PRIMARY KEY,
            ParentId INTEGER REFERENCES Folders(Id) ON DELETE CASCADE,
            Path     TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Name     TEXT NOT NULL
        );
        CREATE INDEX IX_Folders_Parent ON Folders(ParentId);

        CREATE TABLE Media (
            Id           INTEGER PRIMARY KEY,
            FolderId     INTEGER NOT NULL REFERENCES Folders(Id) ON DELETE CASCADE,
            Path         TEXT NOT NULL UNIQUE COLLATE NOCASE,
            FileName     TEXT NOT NULL,
            FileSize     INTEGER NOT NULL,
            FileModified INTEGER NOT NULL,
            Kind         INTEGER NOT NULL,
            DateTaken    INTEGER NOT NULL,
            DateSource   INTEGER NOT NULL,
            Width        INTEGER NOT NULL DEFAULT 0,
            Height       INTEGER NOT NULL DEFAULT 0,
            Orientation  INTEGER NOT NULL DEFAULT 0,
            DurationMs   INTEGER NOT NULL DEFAULT 0,
            CameraMake   TEXT,
            CameraModel  TEXT,
            Latitude     REAL,
            Longitude    REAL,
            IsScreenshot INTEGER NOT NULL DEFAULT 0,
            ContentId    TEXT,
            MotionOffset INTEGER NOT NULL DEFAULT 0,
            MotionLength INTEGER NOT NULL DEFAULT 0,
            Motion       INTEGER NOT NULL DEFAULT 0,
            PairedId     INTEGER,
            IsHidden     INTEGER NOT NULL DEFAULT 0,
            Rating       INTEGER NOT NULL DEFAULT 0,
            OneDriveItemId TEXT
        );
        CREATE INDEX IX_Media_Timeline ON Media(IsHidden, DateTaken DESC, Id DESC);
        CREATE INDEX IX_Media_Folder ON Media(FolderId);
        CREATE INDEX IX_Media_ContentId ON Media(ContentId) WHERE ContentId IS NOT NULL;
        CREATE INDEX IX_Media_Size ON Media(FileSize);

        CREATE VIRTUAL TABLE MediaFts USING fts5(Name, Folder, Tags, Camera, tokenize = 'unicode61 remove_diacritics 2');

        -- Tags: Source = 0 Local/User, 1 OneDrive AI; TagType = 0 Keyword, 1 Person, 2 Object, 3 Location.
        CREATE TABLE Tags (
            Id      INTEGER PRIMARY KEY,
            Name    TEXT NOT NULL COLLATE NOCASE,
            Source  INTEGER NOT NULL DEFAULT 0,
            TagType INTEGER NOT NULL DEFAULT 0,
            UNIQUE (Name, Source)
        );
        CREATE TABLE MediaTags (
            MediaId INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
            TagId   INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            PRIMARY KEY (MediaId, TagId)
        ) WITHOUT ROWID;
        CREATE INDEX IX_MediaTags_Tag ON MediaTags(TagId);

        -- Placeholders for OneDrive people/faces (deferred).
        CREATE TABLE People (
            Id               INTEGER PRIMARY KEY,
            Name             TEXT,
            OneDrivePersonId TEXT UNIQUE
        );
        CREATE TABLE MediaFaces (
            MediaId  INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
            PersonId INTEGER NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
            BoxX REAL, BoxY REAL, BoxW REAL, BoxH REAL,
            PRIMARY KEY (MediaId, PersonId)
        ) WITHOUT ROWID;

        CREATE TABLE Albums (
            Id           INTEGER PRIMARY KEY,
            Name         TEXT NOT NULL,
            Created      INTEGER NOT NULL,
            CoverMediaId INTEGER REFERENCES Media(Id) ON DELETE SET NULL
        );
        CREATE TABLE AlbumMedia (
            AlbumId   INTEGER NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE,
            MediaId   INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
            SortOrder INTEGER NOT NULL,
            PRIMARY KEY (AlbumId, MediaId)
        ) WITHOUT ROWID;

        -- Non-destructive edits (Phase 6).
        CREATE TABLE Edits (
            MediaId        INTEGER PRIMARY KEY REFERENCES Media(Id) ON DELETE CASCADE,
            OperationsJson TEXT NOT NULL,
            Modified       INTEGER NOT NULL
        );

        CREATE TABLE SyncState (
            Key   TEXT PRIMARY KEY,
            Value TEXT
        );
        """,
        // v2/v3: OneDrive's 406 refusals were misread as "no motion"; v3 repeats the reset for rows marked
        // while diagnosing. Refusals are no longer recorded as CloudMissing.
        """
        UPDATE Media SET Motion = 3 WHERE Motion = 4;
        """,
        """
        UPDATE Media SET Motion = 3 WHERE Motion = 4;
        """,
        // v4: cached hashes for duplicate detection (cleared by Upsert when the file changes).
        """
        ALTER TABLE Media ADD COLUMN QuickHash TEXT;
        ALTER TABLE Media ADD COLUMN PerceptualHash INTEGER;
        """,
        // v5: OneDrive people (named here), merges of split people, lookups by person and tag type.
        """
        ALTER TABLE People ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE PersonAliases (
            OneDrivePersonId TEXT PRIMARY KEY,
            PersonId INTEGER NOT NULL REFERENCES People(Id) ON DELETE CASCADE
        );
        CREATE INDEX IX_MediaFaces_Person ON MediaFaces(PersonId);
        CREATE INDEX IX_Tags_Type ON Tags(TagType, Source);
        """,
        // v6: copies saved from the editor remember their original.
        """
        ALTER TABLE Media ADD COLUMN DerivedFromId INTEGER REFERENCES Media(Id) ON DELETE SET NULL;
        """,
        // v7: cloud-only placeholders are indexed from the name only, and re-read once downloaded.
        """
        ALTER TABLE Media ADD COLUMN OnlineOnly INTEGER NOT NULL DEFAULT 0;
        """,
        // v8: people and face boxes from OneDrive's web API: names given in OneDrive (until renamed here), the photo
        // OneDrive uses for each person, each face's OneDrive id, and the item's eTag to spot changes.
        """
        ALTER TABLE People ADD COLUMN NameFromOneDrive INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE People ADD COLUMN OneDriveCoverItemId TEXT;
        ALTER TABLE MediaFaces ADD COLUMN OneDriveFaceId TEXT;
        ALTER TABLE Media ADD COLUMN OneDriveETag TEXT;
        CREATE INDEX IX_Media_OneDriveItem ON Media(OneDriveItemId) WHERE OneDriveItemId IS NOT NULL;
        """,
        // v9: video transcripts (valid for the file size/date they were made from), and what was said in the search
        // index. FTS5 tables can't gain columns, so MediaFts is rebuilt with a Speech column.
        """
        CREATE TABLE Transcripts (
            MediaId      INTEGER PRIMARY KEY REFERENCES Media(Id) ON DELETE CASCADE,
            Status       INTEGER NOT NULL,
            Language     TEXT,
            Model        TEXT NOT NULL,
            FileSize     INTEGER NOT NULL,
            FileModified INTEGER NOT NULL,
            CreatedUtc   TEXT NOT NULL,
            Error        TEXT,
            Segments     TEXT NOT NULL,
            Text         TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE MediaFtsNew USING fts5(Name, Folder, Tags, Camera, Speech, tokenize = 'unicode61 remove_diacritics 2');
        INSERT INTO MediaFtsNew (rowid, Name, Folder, Tags, Camera) SELECT rowid, Name, Folder, Tags, Camera FROM MediaFts;
        DROP TABLE MediaFts;
        ALTER TABLE MediaFtsNew RENAME TO MediaFts;
        """,
        // v10: text read from photos (OCR) with word boxes, and its words in the search index (MediaFts rebuilt again).
        """
        CREATE TABLE PhotoText (
            MediaId      INTEGER PRIMARY KEY REFERENCES Media(Id) ON DELETE CASCADE,
            FileSize     INTEGER NOT NULL,
            FileModified INTEGER NOT NULL,
            Engine       TEXT NOT NULL,
            CreatedUtc   TEXT NOT NULL,
            Lines        TEXT NOT NULL,
            Text         TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE MediaFtsNew USING fts5(Name, Folder, Tags, Camera, Speech, PhotoText, tokenize = 'unicode61 remove_diacritics 2');
        INSERT INTO MediaFtsNew (rowid, Name, Folder, Tags, Camera, Speech) SELECT rowid, Name, Folder, Tags, Camera, Speech FROM MediaFts;
        DROP TABLE MediaFts;
        ALTER TABLE MediaFtsNew RENAME TO MediaFts;
        """,
        // v11: places you named (centre + radius) and the photos at each.
        """
        CREATE TABLE Places (
            Id           INTEGER PRIMARY KEY,
            Name         TEXT NOT NULL,
            Latitude     REAL NOT NULL,
            Longitude    REAL NOT NULL,
            RadiusMeters REAL NOT NULL,
            LonScale     REAL NOT NULL
        );
        CREATE TABLE MediaPlaces (
            MediaId INTEGER NOT NULL REFERENCES Media(Id) ON DELETE CASCADE,
            PlaceId INTEGER NOT NULL REFERENCES Places(Id) ON DELETE CASCADE,
            PRIMARY KEY (MediaId, PlaceId)
        ) WITHOUT ROWID;
        CREATE INDEX IX_MediaPlaces_Place ON MediaPlaces(PlaceId);
        CREATE INDEX IX_Media_Location ON Media(Latitude, Longitude) WHERE Latitude IS NOT NULL;
        """,
        // v12: whether OneDrive has a Live Photo video for the photo (its photo.livePhoto facet, read by the face
        // sync); NULL until OneDrive has said. The face sync is made due, as a full scan, so every photo gets it.
        """
        ALTER TABLE Media ADD COLUMN CloudLive INTEGER;
        UPDATE SyncState SET Value = '2000-01-01T00:00:00.0000000Z' WHERE Key IN ('OneDriveFacesSynced', 'OneDriveFacesFullScan');
        """,
        // v13: how sharp each photo looks (Imaging.Sharpness, from its thumbnail; -1 = couldn't tell), for Blurry photos.
        """
        ALTER TABLE Media ADD COLUMN Sharpness REAL;
        CREATE INDEX IX_Media_Sharpness ON Media(Sharpness) WHERE Sharpness >= 0;
        """,
        // v14: CLIP embeddings (768 × int8, empty = couldn't be read) for Similar photos and searching by description.
        """
        CREATE TABLE Embeddings (
            MediaId      INTEGER PRIMARY KEY REFERENCES Media(Id) ON DELETE CASCADE,
            FileSize     INTEGER NOT NULL,
            FileModified INTEGER NOT NULL,
            Vector       BLOB NOT NULL
        ) WITHOUT ROWID;
        """,
        // v15: named places from OpenStreetMap (parks, schools, restaurants…), the tiles already looked up, and the
        // place each photo was taken at (NULL = not looked at, 0 = none).
        """
        CREATE TABLE Pois (
            Id        INTEGER PRIMARY KEY,
            OsmKey    TEXT NOT NULL UNIQUE,
            Name      TEXT NOT NULL,
            Kind      TEXT NOT NULL,
            Latitude  REAL NOT NULL,
            Longitude REAL NOT NULL,
            South     REAL,
            West      REAL,
            North     REAL,
            East      REAL
        );
        CREATE INDEX IX_Pois_Location ON Pois(Latitude, Longitude);
        CREATE TABLE PoiTiles (
            Tile       TEXT PRIMARY KEY,
            FetchedUtc TEXT NOT NULL,
            Count      INTEGER NOT NULL
        ) WITHOUT ROWID;
        ALTER TABLE Media ADD COLUMN PoiId INTEGER;
        """,
    ];

    private sealed class BoolHandler : SqlMapper.TypeHandler<bool>
    {
        public override bool Parse(object value) => Convert.ToInt64(value) != 0;
        public override void SetValue(System.Data.IDbDataParameter parameter, bool value) => parameter.Value = value ? 1 : 0;
    }
}
