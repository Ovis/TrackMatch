using Dapper;
using Microsoft.Data.Sqlite;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// TrackMatchのSQLite接続とスキーマ初期化を管理する。
/// </summary>
public sealed class SqliteDatabase
{
    private const int BusyTimeoutMilliseconds = 5000;
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // GUIとScannerが同じDBを扱うため、読み取りと書き込みを並行しやすいWALを最初から使用する。
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA synchronous = NORMAL;", cancellationToken: cancellationToken));

        const string schema = """
            CREATE TABLE IF NOT EXISTS Tracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                FileSize INTEGER NOT NULL,
                LastWriteTimeUtcTicks INTEGER NOT NULL,
                DurationTicks INTEGER NOT NULL,
                ArtistsJson TEXT NOT NULL,
                Title TEXT NULL,
                Album TEXT NULL,
                TrackNumber INTEGER NULL,
                DiscNumber INTEGER NULL,
                GenresJson TEXT NOT NULL,
                IsMissing INTEGER NOT NULL DEFAULT 0 CHECK (IsMissing IN (0, 1)),
                UpdatedAtUtcTicks INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Tracks_LastWriteTimeUtcTicks ON Tracks (LastWriteTimeUtcTicks);

            CREATE TABLE IF NOT EXISTS Fingerprints (
                TrackId INTEGER PRIMARY KEY,
                Algorithm INTEGER NOT NULL,
                ValuesBlob BLOB NOT NULL,
                ExtractedAtUtcTicks INTEGER NOT NULL,
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS CandidateSegmentSketches (
                TrackId INTEGER NOT NULL,
                Algorithm INTEGER NOT NULL,
                SegmentLengthItems INTEGER NOT NULL,
                SegmentStrideItems INTEGER NOT NULL,
                SegmentIndex INTEGER NOT NULL,
                Hash INTEGER NOT NULL,
                FingerprintExtractedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackId, Algorithm, SegmentLengthItems, SegmentStrideItems, SegmentIndex),
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateSegmentSketches_Config
                ON CandidateSegmentSketches (Algorithm, SegmentLengthItems, SegmentStrideItems, TrackId);

            CREATE TABLE IF NOT EXISTS CandidatePairs (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                MinimumSegmentHashDistance INTEGER NOT NULL,
                GeneratedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                CHECK (TrackIdA < TrackIdB),
                FOREIGN KEY (TrackIdA) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackIdB) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidatePairs_TrackIdB ON CandidatePairs (TrackIdB);

            CREATE TABLE IF NOT EXISTS CandidateComparisons (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                Similarity REAL NOT NULL,
                BestOffsetItems INTEGER NOT NULL,
                BestOffsetTicks INTEGER NOT NULL,
                MatchedItems INTEGER NOT NULL,
                MatchedDurationTicks INTEGER NOT NULL,
                CoverageA REAL NOT NULL,
                CoverageB REAL NOT NULL,
                DurationRatio REAL NOT NULL,
                ComparedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                FOREIGN KEY (TrackIdA, TrackIdB)
                    REFERENCES CandidatePairs (TrackIdA, TrackIdB) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateComparisons_Similarity ON CandidateComparisons (Similarity DESC);

            CREATE TABLE IF NOT EXISTS CandidateClassifications (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                Reason TEXT NOT NULL,
                ThresholdProfileJson TEXT NOT NULL,
                ClassifiedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                FOREIGN KEY (TrackIdA, TrackIdB)
                    REFERENCES CandidateComparisons (TrackIdA, TrackIdB) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateClassifications_Kind ON CandidateClassifications (Kind);

            CREATE TABLE IF NOT EXISTS CandidateReviews (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                Decision TEXT NOT NULL,
                Note TEXT NULL,
                ReviewedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                CHECK (TrackIdA < TrackIdB),
                FOREIGN KEY (TrackIdA) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackIdB) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateReviews_Decision ON CandidateReviews (Decision);

            CREATE TABLE IF NOT EXISTS CandidateReviewSelections (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                KeepTrackId INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                CHECK (KeepTrackId = TrackIdA OR KeepTrackId = TrackIdB),
                FOREIGN KEY (TrackIdA, TrackIdB)
                    REFERENCES CandidateReviews (TrackIdA, TrackIdB) ON DELETE CASCADE,
                FOREIGN KEY (KeepTrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ScanSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RootPath TEXT NOT NULL,
                StartedAtUtcTicks INTEGER NOT NULL,
                CompletedAtUtcTicks INTEGER NULL,
                Status TEXT NOT NULL,
                TotalFiles INTEGER NOT NULL DEFAULT 0,
                ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                AddedFiles INTEGER NOT NULL DEFAULT 0,
                UpdatedFiles INTEGER NOT NULL DEFAULT 0,
                RemovedFiles INTEGER NOT NULL DEFAULT 0,
                ErrorCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_ScanSessions_StartedAtUtcTicks ON ScanSessions (StartedAtUtcTicks DESC);
            """;

        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = ON;", cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition($"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", cancellationToken: cancellationToken));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
