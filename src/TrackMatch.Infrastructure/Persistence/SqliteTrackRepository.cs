using System.Buffers.Binary;
using System.Text.Json;
using Dapper;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// SQLiteへ音源メタデータとChromaprint fingerprintを保存する。
/// </summary>
public sealed class SqliteTrackRepository(SqliteDatabase database) : ITrackRepository
{
    public async Task<long> UpsertMetadataAsync(
        AudioTrackMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        const string upsertSql = """
            INSERT INTO Tracks (
                Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson,
                IsMissing, UpdatedAtUtcTicks)
            VALUES (
                @Path, @FileSize, @LastWriteTimeUtcTicks, @DurationTicks,
                @ArtistsJson, @Title, @Album, @TrackNumber, @DiscNumber, @GenresJson,
                0, @UpdatedAtUtcTicks)
            ON CONFLICT(Path) DO UPDATE SET
                FileSize = excluded.FileSize,
                LastWriteTimeUtcTicks = excluded.LastWriteTimeUtcTicks,
                DurationTicks = excluded.DurationTicks,
                ArtistsJson = excluded.ArtistsJson,
                Title = excluded.Title,
                Album = excluded.Album,
                TrackNumber = excluded.TrackNumber,
                DiscNumber = excluded.DiscNumber,
                GenresJson = excluded.GenresJson,
                IsMissing = 0,
                UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks;
            """;

        var parameters = new
        {
            Path = Path.GetFullPath(metadata.Path),
            metadata.FileSize,
            LastWriteTimeUtcTicks = metadata.LastWriteTimeUtc.Ticks,
            DurationTicks = metadata.Duration.Ticks,
            ArtistsJson = JsonSerializer.Serialize(metadata.Artists),
            metadata.Title,
            metadata.Album,
            TrackNumber = metadata.TrackNumber is null ? (long?)null : metadata.TrackNumber.Value,
            DiscNumber = metadata.DiscNumber is null ? (long?)null : metadata.DiscNumber.Value,
            GenresJson = JsonSerializer.Serialize(metadata.Genres),
            UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
        };

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT Id FROM Tracks WHERE Path = @Path COLLATE NOCASE;",
            new { parameters.Path },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<StoredTrack?> GetByPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        const string sql = """
            SELECT Id, Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                   ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, IsMissing
            FROM Tracks
            WHERE Path = @Path COLLATE NOCASE;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TrackRow>(new CommandDefinition(
            sql,
            new { Path = Path.GetFullPath(path) },
            cancellationToken: cancellationToken));

        return row is null ? null : ToStoredTrack(row);
    }

    public async Task<IReadOnlyList<StoredTrack>> GetByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        // ルート自身との一致とディレクトリ区切り文字を付けた前方一致に限定し、D:\Music2等の隣接パスを混ぜない。
        var fullRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var prefix = EscapeLike(fullRootPath + Path.DirectorySeparatorChar) + "%";
        const string sql = """
            SELECT Id, Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                   ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, IsMissing
            FROM Tracks
            WHERE Path = @RootPath COLLATE NOCASE
               OR Path LIKE @Prefix ESCAPE '\' COLLATE NOCASE;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TrackRow>(new CommandDefinition(
            sql,
            new { RootPath = fullRootPath, Prefix = prefix },
            cancellationToken: cancellationToken));
        return rows.Select(ToStoredTrack).ToArray();
    }

    public async Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        const string sql = """
            UPDATE Tracks
            SET IsMissing = 1,
                UpdatedAtUtcTicks = @UpdatedAtUtcTicks
            WHERE Id = @TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { TrackId = trackId, UpdatedAtUtcTicks = DateTime.UtcNow.Ticks },
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            throw new InvalidOperationException("欠落状態へ更新するTrackが見つからなかった。");
        }
    }

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
            DeserializeList(row.GenresJson));

        return new StoredTrack(row.Id, metadata, row.IsMissing != 0);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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
            throw new InvalidDataException("Fingerprint BLOBの長さが4byte境界ではない。");
        }

        var values = new uint[bytes.Length / sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)));
        }

        return values;
    }

    private sealed record TrackRow(
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
        long IsMissing);

    private sealed record FingerprintRow(string Path, long DurationTicks, byte[] ValuesBlob);
}
