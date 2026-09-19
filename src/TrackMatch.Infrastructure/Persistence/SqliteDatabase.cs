using Dapper;
using Microsoft.Data.Sqlite;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// TrackMatchのSQLite接続とGlobal Track Schema初期化を管理する。
/// </summary>
public sealed class SqliteDatabase
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int CurrentSchemaVersion = 7;
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

    /// <summary>
    /// 現行Global Track Schemaを初期化する。
    /// </summary>
    /// <remarks>
    /// Global Track再設計以前の開発DBからのMigrationは意図的に提供しない。
    /// 今回のSchemaを今後のVersion管理Baselineとして扱う。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // WPFアプリ内の読み取りと書き込みを並行しやすくするため、WALを最初から使用する。
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA synchronous = NORMAL;", cancellationToken: cancellationToken));

        // Schema作成前にVersion管理状態を確認する。
        // 既存の旧DBへSchemaInfoだけを後付けすると、旧構造をVersion 2と誤認して以後の障害原因になるため、
        // Version管理前のTrackMatchテーブルが存在するDBはMigrationせず明示的に拒否する。
        var hasSchemaInfo = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaInfo';",
            cancellationToken: cancellationToken)) != 0;
        if (hasSchemaInfo)
        {
            var existingVersion = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT Version FROM SchemaInfo WHERE Id = 1;",
                cancellationToken: cancellationToken));
            if (existingVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"対応していないTrackMatch DB Schema Versionです。期待値: {CurrentSchemaVersion}, 実際: {existingVersion}。DBを削除して再作成してください。");
            }
        }
        else
        {
            var existingUserTableCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
                cancellationToken: cancellationToken));
            if (existingUserTableCount != 0)
            {
                throw new InvalidOperationException(
                    "Version管理前のTrackMatch DB Schemaが検出されました。旧DBのMigrationは提供していないため、DBを削除して再作成してください。");
            }
        }

        const string schema = """
            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL
            );

            INSERT INTO SchemaInfo (Id, Version)
            VALUES (1, 6)
            ON CONFLICT(Id) DO NOTHING;

            CREATE TABLE IF NOT EXISTS Libraries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                NormalizedName TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS LibraryRoots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryId INTEGER NOT NULL,
                Path TEXT NOT NULL,
                PathKey TEXT NOT NULL,
                UNIQUE (LibraryId, PathKey),
                UNIQUE (Id, LibraryId),
                FOREIGN KEY (LibraryId) REFERENCES Libraries (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_LibraryRoots_LibraryId ON LibraryRoots (LibraryId);
            CREATE INDEX IF NOT EXISTS IX_LibraryRoots_PathKey ON LibraryRoots (PathKey);

            CREATE TABLE IF NOT EXISTS Tracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL COLLATE NOCASE,
                PathKey TEXT NOT NULL UNIQUE,
                FileSize INTEGER NOT NULL,
                LastWriteTimeUtcTicks INTEGER NOT NULL,
                DurationTicks INTEGER NOT NULL,
                ArtistsJson TEXT NOT NULL,
                Title TEXT NULL,
                Album TEXT NULL,
                TrackNumber INTEGER NULL,
                DiscNumber INTEGER NULL,
                GenresJson TEXT NOT NULL,
                Year INTEGER NULL,
                Format TEXT NULL,
                Codec TEXT NULL,
                BitrateKbps INTEGER NULL,
                SampleRateHz INTEGER NULL,
                BitDepth INTEGER NULL,
                Channels INTEGER NULL,
                IsMissing INTEGER NOT NULL DEFAULT 0 CHECK (IsMissing IN (0, 1)),
                ContentVerificationStatus TEXT NOT NULL DEFAULT 'Verified'
                    CHECK (ContentVerificationStatus IN ('Verified', 'VerificationPending', 'VerificationFailed', 'ReevaluationPending', 'ReevaluationFailed')),
                ContentVerificationError TEXT NULL,
                UpdatedAtUtcTicks INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Tracks_Path ON Tracks (Path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Tracks_LastWriteTimeUtcTicks ON Tracks (LastWriteTimeUtcTicks);
            CREATE INDEX IF NOT EXISTS IX_Tracks_IsMissing ON Tracks (IsMissing);
            CREATE INDEX IF NOT EXISTS IX_Tracks_ContentVerificationStatus ON Tracks (ContentVerificationStatus);

            -- Content Verification/再評価中に、直前のDuplicate Group範囲でファイル整理を停止する。
            CREATE TABLE IF NOT EXISTS TrackFileOrganizationBlocks (
                SourceTrackId INTEGER NOT NULL,
                AffectedTrackId INTEGER NOT NULL,
                PRIMARY KEY (SourceTrackId, AffectedTrackId),
                FOREIGN KEY (SourceTrackId) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (AffectedTrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_TrackFileOrganizationBlocks_Affected
                ON TrackFileOrganizationBlocks (AffectedTrackId);

            CREATE TABLE IF NOT EXISTS LibraryTracks (
                LibraryId INTEGER NOT NULL,
                TrackId INTEGER NOT NULL,
                RootId INTEGER NOT NULL,
                RelativePath TEXT NOT NULL COLLATE NOCASE,
                CandidateGenerationPending INTEGER NOT NULL DEFAULT 1 CHECK (CandidateGenerationPending IN (0, 1)),
                CandidateGenerationVersion INTEGER NULL,
                PRIMARY KEY (LibraryId, TrackId),
                UNIQUE (LibraryId, RootId, RelativePath),
                FOREIGN KEY (LibraryId) REFERENCES Libraries (Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (RootId, LibraryId) REFERENCES LibraryRoots (Id, LibraryId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_LibraryTracks_TrackId ON LibraryTracks (TrackId);
            CREATE INDEX IF NOT EXISTS IX_LibraryTracks_RootId ON LibraryTracks (RootId);
            CREATE INDEX IF NOT EXISTS IX_LibraryTracks_Pending
                ON LibraryTracks (LibraryId, CandidateGenerationPending);

            CREATE TABLE IF NOT EXISTS Fingerprints (
                TrackId INTEGER PRIMARY KEY,
                Algorithm INTEGER NOT NULL,
                DurationTicks INTEGER NOT NULL,
                ValuesBlob BLOB NOT NULL,
                ExtractedAtUtcTicks INTEGER NOT NULL,
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS CandidateSegmentSketches (
                TrackId INTEGER NOT NULL,
                Algorithm INTEGER NOT NULL,
                SegmentLengthItems INTEGER NOT NULL,
                SegmentStrideItems INTEGER NOT NULL,
                MaximumSegmentHashDistance INTEGER NOT NULL,
                SegmentIndex INTEGER NOT NULL,
                Hash INTEGER NOT NULL,
                FingerprintExtractedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (
                    TrackId, Algorithm, SegmentLengthItems, SegmentStrideItems,
                    MaximumSegmentHashDistance, SegmentIndex),
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateSegmentSketches_Config
                ON CandidateSegmentSketches (
                    Algorithm, SegmentLengthItems, SegmentStrideItems,
                    MaximumSegmentHashDistance, TrackId);

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
                ComparisonVersion INTEGER NOT NULL,
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

            -- Current Human VerdictはGlobal Pair単位で1件だけ保持する。
            -- 操作元Libraryが削除されても判定元表示とHistory退避時の出所を失わないよう名前SnapshotもCurrentに保持する。
            CREATE TABLE IF NOT EXISTS CandidateReviews (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                Decision TEXT NOT NULL,
                PreferredTrackId INTEGER NULL,
                Note TEXT NULL,
                SourceLibraryId INTEGER NULL,
                SourceLibraryNameSnapshot TEXT NULL,
                ReviewedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                CHECK (TrackIdA < TrackIdB),
                CHECK (
                    (Decision = 'ConfirmedDuplicate' AND PreferredTrackId IN (TrackIdA, TrackIdB))
                    OR (Decision = 'NotDuplicate' AND PreferredTrackId IS NULL)),
                FOREIGN KEY (TrackIdA) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackIdB) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (PreferredTrackId) REFERENCES Tracks (Id) ON DELETE CASCADE,
                FOREIGN KEY (SourceLibraryId) REFERENCES Libraries (Id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateReviews_Decision ON CandidateReviews (Decision);

            CREATE TABLE IF NOT EXISTS CandidateReviewHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                Decision TEXT NOT NULL,
                PreferredTrackId INTEGER NULL,
                Note TEXT NULL,
                SourceLibraryId INTEGER NULL,
                SourceLibraryNameSnapshot TEXT NULL,
                ChangedAtUtcTicks INTEGER NOT NULL,
                ChangeKind TEXT NOT NULL,
                InvalidationReason TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateReviewHistory_Pair
                ON CandidateReviewHistory (TrackIdA, TrackIdB, ChangedAtUtcTicks DESC);

            -- Duplicate GroupのIdentityはGlobal ConfirmedDuplicate Graphから再構成する。
            CREATE TABLE IF NOT EXISTS DuplicateGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GraphKey TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS DuplicateGroupTracks (
                DuplicateGroupId INTEGER NOT NULL,
                TrackId INTEGER NOT NULL UNIQUE,
                PRIMARY KEY (DuplicateGroupId, TrackId),
                FOREIGN KEY (DuplicateGroupId) REFERENCES DuplicateGroups (Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_DuplicateGroupTracks_TrackId ON DuplicateGroupTracks (TrackId);

            CREATE TABLE IF NOT EXISTS LibraryDuplicateGroupKeepStates (
                LibraryId INTEGER NOT NULL,
                DuplicateGroupId INTEGER NOT NULL,
                KeepTrackId INTEGER NULL,
                Status TEXT NOT NULL,
                UpdatedAtUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (LibraryId, DuplicateGroupId),
                FOREIGN KEY (LibraryId) REFERENCES Libraries (Id) ON DELETE CASCADE,
                FOREIGN KEY (DuplicateGroupId) REFERENCES DuplicateGroups (Id) ON DELETE CASCADE,
                FOREIGN KEY (KeepTrackId) REFERENCES Tracks (Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS LibraryDuplicateGroupKeepHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryId INTEGER NULL,
                LibraryNameSnapshot TEXT NULL,
                DuplicateGroupId INTEGER NULL,
                GraphKeySnapshot TEXT NOT NULL,
                KeepTrackId INTEGER NULL,
                Status TEXT NOT NULL,
                ChangeKind TEXT NOT NULL,
                ChangedAtUtcTicks INTEGER NOT NULL,
                Note TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_LibraryDuplicateGroupKeepHistory_Group
                ON LibraryDuplicateGroupKeepHistory (GraphKeySnapshot, ChangedAtUtcTicks DESC);

            CREATE TABLE IF NOT EXISTS TrackQualityAnalyses (
                TrackId INTEGER PRIMARY KEY,
                AnalysisVersion INTEGER NOT NULL,
                Status TEXT NOT NULL,
                IntegratedLoudnessLufs REAL NULL,
                TruePeakDbtp REAL NULL,
                LoudnessRangeLu REAL NULL,
                PeakToLoudnessRatioDb REAL NULL,
                PeakNearSampleCount INTEGER NOT NULL DEFAULT 0,
                ClippingRunCount INTEGER NOT NULL DEFAULT 0,
                ClippingTotalDurationTicks INTEGER NOT NULL DEFAULT 0,
                ClippingLongestDurationTicks INTEGER NOT NULL DEFAULT 0,
                LeftRightLevelDifferenceDb REAL NULL,
                EffectiveUpperFrequencyHz REAL NULL,
                HasHighFrequencyCutoff INTEGER NULL CHECK (HasHighFrequencyCutoff IS NULL OR HasHighFrequencyCutoff IN (0, 1)),
                HighFrequencyCutoffHz REAL NULL,
                HighFrequencyEnergyRatio REAL NULL,
                HighFrequencyConsistency REAL NULL,
                AnalyzedAtUtcTicks INTEGER NULL,
                FailureReason TEXT NULL,
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_TrackQualityAnalyses_Status ON TrackQualityAnalyses (Status);
            CREATE INDEX IF NOT EXISTS IX_TrackQualityAnalyses_AnalysisVersion ON TrackQualityAnalyses (AnalysisVersion);

            CREATE TABLE IF NOT EXISTS CandidateQualityComparisons (
                TrackIdA INTEGER NOT NULL,
                TrackIdB INTEGER NOT NULL,
                ComparisonVersion INTEGER NOT NULL,
                Status TEXT NOT NULL,
                MatchedLoudnessDifferenceLu REAL NULL,
                GainDifferenceMeanDb REAL NULL,
                GainDifferenceStandardDeviationDb REAL NULL,
                PeakToLoudnessRatioDifferenceDb REAL NULL,
                LoudnessRangeDifferenceLu REAL NULL,
                IsPrimarilyGainDifference INTEGER NULL CHECK (IsPrimarilyGainDifference IS NULL OR IsPrimarilyGainDifference IN (0, 1)),
                RelativeHighFrequencyDifference REAL NULL,
                ComparedAtUtcTicks INTEGER NULL,
                FailureReason TEXT NULL,
                PRIMARY KEY (TrackIdA, TrackIdB),
                CHECK (TrackIdA < TrackIdB),
                FOREIGN KEY (TrackIdA, TrackIdB)
                    REFERENCES CandidateComparisons (TrackIdA, TrackIdB) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_CandidateQualityComparisons_Status ON CandidateQualityComparisons (Status);
            CREATE INDEX IF NOT EXISTS IX_CandidateQualityComparisons_ComparisonVersion ON CandidateQualityComparisons (ComparisonVersion);


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

        var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT Version FROM SchemaInfo WHERE Id = 1;",
            cancellationToken: cancellationToken));
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"対応していないTrackMatch DB Schema Versionです。期待値: {CurrentSchemaVersion}, 実際: {version}");
        }
    }

    /// <summary>
    /// Foreign KeyとBusy Timeoutを有効化したSQLite接続を開く。
    /// </summary>
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
