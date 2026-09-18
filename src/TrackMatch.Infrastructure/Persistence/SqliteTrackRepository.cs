using System.Buffers.Binary;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// SQLiteへGlobal Track、Library Membership、Chromaprint fingerprintを保存する。
/// </summary>
public sealed class SqliteTrackRepository(SqliteDatabase database) : ITrackRepository
{
    /// <inheritdoc />
    public async Task<long> UpsertMetadataAsync(
        AudioTrackMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var normalizedPath = LibraryValueNormalizer.NormalizeTrackPath(metadata.Path);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingTrackRow>(new CommandDefinition(
            "SELECT Id, FileSize, LastWriteTimeUtcTicks, IsMissing FROM Tracks WHERE PathKey = @PathKey;",
            new { PathKey = normalizedPath.Key },
            transaction,
            cancellationToken: cancellationToken));

        // FileSize/LastWriteTimeの変化だけでは音声内容変更と断定しない。
        // Human Verdictの無効化は追加解析後にConfirmContentChangedAsyncから明示的に行う。

        var parameters = new
        {
            Path = normalizedPath.DisplayPath,
            PathKey = normalizedPath.Key,
            metadata.FileSize,
            LastWriteTimeUtcTicks = metadata.LastWriteTimeUtc.Ticks,
            DurationTicks = metadata.Duration.Ticks,
            ArtistsJson = JsonSerializer.Serialize(metadata.Artists),
            metadata.Title,
            metadata.Album,
            TrackNumber = metadata.TrackNumber is null ? (long?)null : metadata.TrackNumber.Value,
            DiscNumber = metadata.DiscNumber is null ? (long?)null : metadata.DiscNumber.Value,
            GenresJson = JsonSerializer.Serialize(metadata.Genres),
            Year = metadata.Year is null ? (long?)null : metadata.Year.Value,
            metadata.Format,
            metadata.Codec,
            metadata.BitrateKbps,
            metadata.SampleRateHz,
            metadata.BitDepth,
            metadata.Channels,
            UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
        };

        const string upsertSql = """
            INSERT INTO Tracks (
                Path, PathKey, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, Year,
                Format, Codec, BitrateKbps, SampleRateHz, BitDepth, Channels,
                IsMissing, UpdatedAtUtcTicks)
            VALUES (
                @Path, @PathKey, @FileSize, @LastWriteTimeUtcTicks, @DurationTicks,
                @ArtistsJson, @Title, @Album, @TrackNumber, @DiscNumber, @GenresJson, @Year,
                @Format, @Codec, @BitrateKbps, @SampleRateHz, @BitDepth, @Channels,
                0, @UpdatedAtUtcTicks)
            ON CONFLICT(PathKey) DO UPDATE SET
                Path = excluded.Path,
                FileSize = excluded.FileSize,
                LastWriteTimeUtcTicks = excluded.LastWriteTimeUtcTicks,
                DurationTicks = excluded.DurationTicks,
                ArtistsJson = excluded.ArtistsJson,
                Title = excluded.Title,
                Album = excluded.Album,
                TrackNumber = excluded.TrackNumber,
                DiscNumber = excluded.DiscNumber,
                GenresJson = excluded.GenresJson,
                Year = excluded.Year,
                Format = excluded.Format,
                Codec = excluded.Codec,
                BitrateKbps = excluded.BitrateKbps,
                SampleRateHz = excluded.SampleRateHz,
                BitDepth = excluded.BitDepth,
                Channels = excluded.Channels,
                IsMissing = 0,
                UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks;
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT Id FROM Tracks WHERE PathKey = @PathKey;",
            new { PathKey = normalizedPath.Key },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <inheritdoc />
    public async Task<StoredTrack?> GetByPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = LibraryValueNormalizer.NormalizeTrackPath(path);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TrackRow>(new CommandDefinition(
            TrackSelectSql + " WHERE t.PathKey = @PathKey;",
            new { PathKey = normalizedPath.Key },
            cancellationToken: cancellationToken));
        return row is null ? null : ToStoredTrack(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>> GetByRootAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT lt.LibraryId, lt.TrackId, lt.RootId, lt.RelativePath,
                   lt.CandidateGenerationPending, lt.CandidateGenerationVersion,
                   t.Id, t.Path, t.FileSize, t.LastWriteTimeUtcTicks, t.DurationTicks,
                   t.ArtistsJson, t.Title, t.Album, t.TrackNumber, t.DiscNumber, t.GenresJson, t.Year,
                   t.Format, t.Codec, t.BitrateKbps, t.SampleRateHz, t.BitDepth, t.Channels, t.IsMissing
            FROM LibraryTracks lt
            INNER JOIN Tracks t ON t.Id = lt.TrackId
            WHERE lt.LibraryId = @LibraryId AND lt.RootId = @RootId
            ORDER BY lt.RelativePath COLLATE NOCASE;
            """;
        var rows = await connection.QueryAsync<RootTrackRow>(new CommandDefinition(
            sql,
            new { LibraryId = libraryId, RootId = rootId },
            cancellationToken: cancellationToken));
        return rows.Select(row => (
            new StoredLibraryTrack(
                row.LibraryId,
                row.TrackId,
                row.RootId,
                row.RelativePath,
                row.CandidateGenerationPending != 0,
                row.CandidateGenerationVersion is null ? null : checked((int)row.CandidateGenerationVersion.Value)),
            ToStoredTrack(row))).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootAsync(
        long libraryId,
        long rootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT lt.TrackId
            FROM LibraryTracks lt
            LEFT JOIN Fingerprints f ON f.TrackId = lt.TrackId
            WHERE lt.LibraryId = @LibraryId
              AND lt.RootId = @RootId
              AND f.TrackId IS NULL;
            """;
        var ids = await connection.QueryAsync<long>(new CommandDefinition(
            sql,
            new { LibraryId = libraryId, RootId = rootId },
            cancellationToken: cancellationToken));
        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public async Task EnsureMembershipAsync(
        long libraryId,
        long rootId,
        long trackId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootId));
        }

        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO LibraryTracks (
                LibraryId, TrackId, RootId, RelativePath,
                CandidateGenerationPending, CandidateGenerationVersion)
            VALUES (@LibraryId, @TrackId, @RootId, @RelativePath, 1, NULL)
            ON CONFLICT(LibraryId, TrackId) DO UPDATE SET
                RootId = excluded.RootId,
                RelativePath = excluded.RelativePath;
            """;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { LibraryId = libraryId, TrackId = trackId, RootId = rootId, RelativePath = relativePath },
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("LibraryTrack Membershipを確立できませんでした。Library/Root/Trackの整合性を確認してください。", exception);
        }
    }

    /// <inheritdoc />
    public async Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Tracks SET IsMissing = 1, UpdatedAtUtcTicks = @UpdatedAtUtcTicks WHERE Id = @TrackId;",
            new { TrackId = trackId, UpdatedAtUtcTicks = DateTime.UtcNow.Ticks },
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            throw new InvalidOperationException("欠落状態へ更新するTrackが見つかりませんでした。");
        }
    }

    /// <inheritdoc />
    public async Task MarkMissingBatchAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        if (ids.Any(trackId => trackId <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(trackIds));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var updatedAtUtcTicks = DateTime.UtcNow.Ticks;
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Tracks SET IsMissing = 1, UpdatedAtUtcTicks = @UpdatedAtUtcTicks WHERE Id IN @TrackIds;",
            new { TrackIds = ids, UpdatedAtUtcTicks = updatedAtUtcTicks },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != ids.Length)
        {
            throw new InvalidOperationException("Missing確定対象の一部Trackが見つからないため、Root ScanのMissing更新を中止しました。");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveFingerprintAsync(
        long trackId,
        AudioFingerprint fingerprint,
        int algorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        const string sql = """
            INSERT INTO Fingerprints (TrackId, Algorithm, ValuesBlob, ExtractedAtUtcTicks)
            VALUES (@TrackId, @Algorithm, @ValuesBlob, @ExtractedAtUtcTicks)
            ON CONFLICT(TrackId) DO UPDATE SET
                Algorithm = excluded.Algorithm,
                ValuesBlob = excluded.ValuesBlob,
                ExtractedAtUtcTicks = excluded.ExtractedAtUtcTicks;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                TrackId = trackId,
                Algorithm = algorithm,
                ValuesBlob = EncodeFingerprint(fingerprint.Values),
                ExtractedAtUtcTicks = DateTime.UtcNow.Ticks,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Fingerprints WHERE TrackId = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<AudioFingerprint?> GetFingerprintAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        const string sql = """
            SELECT t.Path, t.DurationTicks, f.ValuesBlob
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE f.TrackId = @TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<FingerprintRow>(new CommandDefinition(
            sql,
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new AudioFingerprint(row.Path, TimeSpan.FromTicks(row.DurationTicks), DecodeFingerprint(row.ValuesBlob));
    }

    /// <inheritdoc />
    public async Task<bool> IsContentVerificationPendingAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var status = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT ContentVerificationStatus FROM Tracks WHERE Id = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return status is "VerificationFailed" or "ReevaluationPending" or "ReevaluationFailed";
    }

    /// <inheritdoc />
    public async Task MarkContentVerificationFailedAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CaptureFileOrganizationBlockAsync(connection, transaction, trackId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Tracks SET ContentVerificationStatus = 'VerificationFailed' WHERE Id = @TrackId;",
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkContentVerifiedAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await SetContentVerificationStatusAsync(trackId, "Verified", cancellationToken);
        await ClearFileOrganizationBlockAsync(trackId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ConfirmContentChangedAndGetInvalidatedReviewCountAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM CandidateReviews WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        await ConfirmContentChangedAsync(trackId, cancellationToken);
        return checked((int)count);
    }

    /// <inheritdoc />
    public async Task ConfirmContentChangedAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Groupが一時分断された後に安全範囲を復元できないため、無効化前のGroup構成を整理禁止範囲として保存する。
        await CaptureFileOrganizationBlockAsync(connection, transaction, trackId, cancellationToken);

        // Content Changed確定後だけ、対象Trackに直接関係するVerdictとContent依存解析を同一Transactionで無効化する。
        await InvalidateTrackContentAsync(connection, transaction, trackId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Tracks SET ContentVerificationStatus = 'ReevaluationPending' WHERE Id = @TrackId;",
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CaptureFileOrganizationBlockAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long trackId,
        CancellationToken cancellationToken)
    {
        // Materialized Groupは失敗直前の有効Verdictから構成されているため、ここで影響範囲をSnapshotする。
        // Group外TrackまでLibrary全体を止めず、該当Groupが無い場合は対象Track自身だけを停止する。
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO TrackFileOrganizationBlocks (SourceTrackId, AffectedTrackId)
            SELECT @TrackId, gt.TrackId
            FROM DuplicateGroupTracks gt
            WHERE gt.DuplicateGroupId = (
                SELECT DuplicateGroupId FROM DuplicateGroupTracks WHERE TrackId = @TrackId)
            UNION
            SELECT @TrackId, @TrackId;
            """,
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task ClearFileOrganizationBlockAsync(long trackId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM TrackFileOrganizationBlocks WHERE SourceTrackId = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// TrackのContent Verification状態だけを更新する。
    /// Human Verdict自体へ機械状態を混在させず、利用可否はTrack状態から導出する。
    /// </summary>
    private async Task SetContentVerificationStatusAsync(
        long trackId,
        string status,
        CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Tracks SET ContentVerificationStatus = @Status WHERE Id = @TrackId;",
            new { TrackId = trackId, Status = status },
            cancellationToken: cancellationToken));
        if (updated == 0)
        {
            throw new InvalidOperationException("Content Verification対象のTrackが見つかりません。");
        }
    }

    /// <summary>
    /// LibraryのCandidate再評価が最後まで成功したTrackを通常利用可能へ戻す。
    /// </summary>
    public async Task MarkReevaluationCompletedAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        await SetReevaluationStatusForLibraryAsync(libraryId, "Verified", cancellationToken);
    }

    /// <summary>
    /// LibraryのCandidate再評価が失敗したTrackを永続的な失敗状態へ遷移させる。
    /// </summary>
    public async Task MarkReevaluationFailedAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        await SetReevaluationStatusForLibraryAsync(libraryId, "ReevaluationFailed", cancellationToken);
    }

    private async Task SetReevaluationStatusForLibraryAsync(
        long libraryId,
        string status,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (string.Equals(status, "Verified", StringComparison.Ordinal))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM TrackFileOrganizationBlocks
                WHERE SourceTrackId IN (
                    SELECT t.Id
                    FROM Tracks t
                    WHERE t.ContentVerificationStatus = 'ReevaluationPending'
                      AND t.Id IN (SELECT TrackId FROM LibraryTracks WHERE LibraryId = @LibraryId));
                """,
                new { LibraryId = libraryId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Tracks
            SET ContentVerificationStatus = @Status
            WHERE ContentVerificationStatus = 'ReevaluationPending'
              AND Id IN (SELECT TrackId FROM LibraryTracks WHERE LibraryId = @LibraryId);
            """,
            new { LibraryId = libraryId, Status = status },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InvalidateTrackContentAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long trackId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviews WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;",
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidatePairs WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;",
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM TrackQualityAnalyses WHERE TrackId = @TrackId;
            DELETE FROM CandidateQualityComparisons WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;
            """,
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateSegmentSketches WHERE TrackId = @TrackId; DELETE FROM Fingerprints WHERE TrackId = @TrackId;",
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE LibraryTracks
            SET CandidateGenerationPending = 1,
                CandidateGenerationVersion = NULL
            WHERE TrackId = @TrackId;
            """,
            new { TrackId = trackId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private const string TrackSelectSql = """
        SELECT t.Id, t.Path, t.FileSize, t.LastWriteTimeUtcTicks, t.DurationTicks,
               t.ArtistsJson, t.Title, t.Album, t.TrackNumber, t.DiscNumber, t.GenresJson, t.Year,
               t.Format, t.Codec, t.BitrateKbps, t.SampleRateHz, t.BitDepth, t.Channels, t.IsMissing
        FROM Tracks t
        """;

    private static StoredTrack ToStoredTrack(TrackRow row)
    {
        var metadata = new AudioTrackMetadata(
            row.Path,
            row.FileSize,
            new DateTime(row.LastWriteTimeUtcTicks, DateTimeKind.Utc),
            TimeSpan.FromTicks(row.DurationTicks),
            DeserializeList(row.ArtistsJson),
            row.Title,
            row.Album,
            row.TrackNumber is null ? null : checked((uint)row.TrackNumber.Value),
            row.DiscNumber is null ? null : checked((uint)row.DiscNumber.Value),
            DeserializeList(row.GenresJson),
            row.Format,
            row.Codec,
            ToNullableInt(row.BitrateKbps),
            ToNullableInt(row.SampleRateHz),
            ToNullableInt(row.BitDepth),
            ToNullableInt(row.Channels),
            row.Year is null ? null : checked((uint)row.Year.Value));
        return new StoredTrack(row.Id, metadata, row.IsMissing != 0);
    }

    private static int? ToNullableInt(long? value)
        => value is null ? null : checked((int)value.Value);

    private static IReadOnlyList<string> DeserializeList(string json)
        => JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidDataException("SQLite内の文字列配列JSONを復元できなかった。");

    private static byte[] EncodeFingerprint(IReadOnlyList<uint> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(uint))];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)), values[i]);
        }

        return bytes;
    }

    private static IReadOnlyList<uint> DecodeFingerprint(byte[] bytes)
    {
        if (bytes.Length % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Fingerprint BLOBの長さが4byte境界ではありません。");
        }

        var values = new uint[bytes.Length / sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)));
        }

        return values;
    }

    private sealed record ExistingTrackRow(long Id, long FileSize, long LastWriteTimeUtcTicks, long IsMissing);

    private record TrackRow(
        long Id,
        string Path,
        long FileSize,
        long LastWriteTimeUtcTicks,
        long DurationTicks,
        string ArtistsJson,
        string? Title,
        string? Album,
        long? TrackNumber,
        long? DiscNumber,
        string GenresJson,
        long? Year,
        string? Format,
        string? Codec,
        long? BitrateKbps,
        long? SampleRateHz,
        long? BitDepth,
        long? Channels,
        long IsMissing);

    private sealed record RootTrackRow(
        long LibraryId,
        long TrackId,
        long RootId,
        string RelativePath,
        long CandidateGenerationPending,
        long? CandidateGenerationVersion,
        long Id,
        string Path,
        long FileSize,
        long LastWriteTimeUtcTicks,
        long DurationTicks,
        string ArtistsJson,
        string? Title,
        string? Album,
        long? TrackNumber,
        long? DiscNumber,
        string GenresJson,
        long? Year,
        string? Format,
        string? Codec,
        long? BitrateKbps,
        long? SampleRateHz,
        long? BitDepth,
        long? Channels,
        long IsMissing) : TrackRow(
            Id, Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
            ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, Year,
            Format, Codec, BitrateKbps, SampleRateHz, BitDepth, Channels, IsMissing);

    private sealed record FingerprintRow(string Path, long DurationTicks, byte[] ValuesBlob);
}
