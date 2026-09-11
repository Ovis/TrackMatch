using System.Buffers.Binary;
using System.Text.Json;
using Dapper;
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

        var fullPath = Path.GetFullPath(metadata.Path);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var ownership = await ResolveOwnershipAsync(connection, fullPath, cancellationToken);

        const string upsertSql = """
            INSERT INTO Tracks (
                LibraryId, RootId, RelativePath, Path,
                FileSize, LastWriteTimeUtcTicks, DurationTicks,
                ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson,
                IsMissing, UpdatedAtUtcTicks)
            VALUES (
                @LibraryId, @RootId, @RelativePath, @Path,
                @FileSize, @LastWriteTimeUtcTicks, @DurationTicks,
                @ArtistsJson, @Title, @Album, @TrackNumber, @DiscNumber, @GenresJson,
                0, @UpdatedAtUtcTicks)
            ON CONFLICT(RootId, RelativePath) DO UPDATE SET
                LibraryId = excluded.LibraryId,
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
                IsMissing = 0,
                UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks;
            """;

        var parameters = new
        {
            ownership.LibraryId,
            RootId = ownership.Id,
            ownership.RelativePath,
            Path = fullPath,
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

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT Id FROM Tracks WHERE RootId = @RootId AND RelativePath = @RelativePath COLLATE NOCASE;",
            new { parameters.RootId, parameters.RelativePath },
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
            SELECT Id, LibraryId, RootId, RelativePath, Path,
                   FileSize, LastWriteTimeUtcTicks, DurationTicks,
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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var root = await ResolveRegisteredRootAsync(connection, rootPath, cancellationToken);
        const string sql = """
            SELECT Id, LibraryId, RootId, RelativePath, Path,
                   FileSize, LastWriteTimeUtcTicks, DurationTicks,
                   ArtistsJson, Title, Album, TrackNumber, DiscNumber, GenresJson, IsMissing
            FROM Tracks
            WHERE RootId = @RootId;
            """;

        var rows = await connection.QueryAsync<TrackRow>(new CommandDefinition(
            sql,
            new { RootId = root.Id },
            cancellationToken: cancellationToken));
        return rows.Select(ToStoredTrack).ToArray();
    }

    public async Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var root = await ResolveRegisteredRootAsync(connection, rootPath, cancellationToken);
        const string sql = """
            SELECT t.Id
            FROM Tracks t
            LEFT JOIN Fingerprints f ON f.TrackId = t.Id
            WHERE f.TrackId IS NULL
              AND t.RootId = @RootId;
            """;

        var ids = await connection.QueryAsync<long>(new CommandDefinition(
            sql,
            new { RootId = root.Id },
            cancellationToken: cancellationToken));
        return ids.ToHashSet();
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

    private static async Task<TrackOwnership> ResolveOwnershipAsync(
        SqliteConnection connection,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var roots = (await connection.QueryAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots;",
            cancellationToken: cancellationToken))).ToArray();

        foreach (var root in roots.OrderByDescending(item => item.Path.Length))
        {
            var relativePath = Path.GetRelativePath(root.Path, fullPath);
            if (relativePath == "."
                || relativePath == ".."
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath))
            {
                continue;
            }

            return new TrackOwnership(root.Id, root.LibraryId, relativePath);
        }

        throw new InvalidOperationException($"Trackが登録済みLibrary Rootの配下にありません: {fullPath}");
    }

    private static async Task<RootRow> ResolveRegisteredRootAsync(
        SqliteConnection connection,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var fullRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var roots = await connection.QueryAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots;",
            cancellationToken: cancellationToken));
        var root = roots.SingleOrDefault(item =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path)),
                fullRootPath,
                StringComparison.OrdinalIgnoreCase));

        return root ?? throw new InvalidOperationException($"Library Rootが登録されていません: {fullRootPath}");
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

        return new StoredTrack(
            row.Id,
            metadata,
            row.IsMissing != 0,
            row.LibraryId,
            row.RootId,
            row.RelativePath);
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

    private sealed record TrackRow(
        long Id,
        long LibraryId,
        long RootId,
        string RelativePath,
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

    private sealed record RootRow(long Id, long LibraryId, string Path);
    private sealed record TrackOwnership(long Id, long LibraryId, string RelativePath);
    private sealed record FingerprintRow(string Path, long DurationTicks, byte[] ValuesBlob);
}
