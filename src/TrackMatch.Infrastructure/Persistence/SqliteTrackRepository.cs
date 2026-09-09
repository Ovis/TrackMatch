using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Tracks (
                    Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                    ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson,
                    IsMissing, UpdatedAtUtcTicks)
                VALUES (
                    $path, $fileSize, $lastWriteTimeUtcTicks, $durationTicks,
                    $artistsJson, $title, $album, $trackNumber, $discNumber, $genresJson,
                    0, $updatedAtUtcTicks)
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
            AddMetadataParameters(command, metadata);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long id;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT Id FROM Tracks WHERE Path = $path COLLATE NOCASE;";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(metadata.Path));
            id = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("保存したTrackのIDを取得できなかった。"));
        }

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<StoredTrack?> GetByPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Path, FileSize, LastWriteTimeUtcTicks, DurationTicks,
                   ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, IsMissing
            FROM Tracks
            WHERE Path = $path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var metadata = new AudioTrackMetadata(
            reader.GetString(1),
            reader.GetInt64(2),
            new DateTime(reader.GetInt64(3), DateTimeKind.Utc),
            TimeSpan.FromTicks(reader.GetInt64(4)),
            DeserializeList(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : checked((uint)reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : checked((uint)reader.GetInt64(9)),
            DeserializeList(reader.GetString(10)));

        return new StoredTrack(reader.GetInt64(0), metadata, reader.GetInt64(11) != 0);
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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Fingerprints (TrackId, Algorithm, ValuesBlob, ExtractedAtUtcTicks)
            VALUES ($trackId, $algorithm, $valuesBlob, $extractedAtUtcTicks)
            ON CONFLICT(TrackId) DO UPDATE SET
                Algorithm = excluded.Algorithm,
                ValuesBlob = excluded.ValuesBlob,
                ExtractedAtUtcTicks = excluded.ExtractedAtUtcTicks;
            """;
        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$algorithm", algorithm);
        command.Parameters.AddWithValue("$valuesBlob", EncodeFingerprint(fingerprint.Values));
        command.Parameters.AddWithValue("$extractedAtUtcTicks", DateTime.UtcNow.Ticks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AudioFingerprint?> GetFingerprintAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Path, t.DurationTicks, f.ValuesBlob
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE f.TrackId = $trackId;
            """;
        command.Parameters.AddWithValue("$trackId", trackId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AudioFingerprint(
            reader.GetString(0),
            TimeSpan.FromTicks(reader.GetInt64(1)),
            DecodeFingerprint((byte[])reader[2]));
    }

    private static void AddMetadataParameters(SqliteCommand command, AudioTrackMetadata metadata)
    {
        command.Parameters.AddWithValue("$path", Path.GetFullPath(metadata.Path));
        command.Parameters.AddWithValue("$fileSize", metadata.FileSize);
        command.Parameters.AddWithValue("$lastWriteTimeUtcTicks", metadata.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("$durationTicks", metadata.Duration.Ticks);
        command.Parameters.AddWithValue("$artistsJson", JsonSerializer.Serialize(metadata.Artists));
        command.Parameters.AddWithValue("$title", (object?)metadata.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$album", (object?)metadata.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("$trackNumber", metadata.TrackNumber is null ? DBNull.Value : metadata.TrackNumber.Value);
        command.Parameters.AddWithValue("$discNumber", metadata.DiscNumber is null ? DBNull.Value : metadata.DiscNumber.Value);
        command.Parameters.AddWithValue("$genresJson", JsonSerializer.Serialize(metadata.Genres));
        command.Parameters.AddWithValue("$updatedAtUtcTicks", DateTime.UtcNow.Ticks);
    }

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
}
